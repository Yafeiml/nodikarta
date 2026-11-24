using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ProjectHub.Edge;

internal sealed class ObservationConfirmationRequiredException() : HttpRequestException(
    "修改实测记录时，请在 validity 中填写 kind=observation、原测量对象 subject 和实际测量时间 observedAt，并在 evidence 中给出依据。仅在重新实测后更新观测时间；用新的 requestId 提交修正后的内容。",
    null, HttpStatusCode.BadRequest);

// Project and binding come from the local owner's launch configuration, never from MCP input.
internal sealed partial class ProjectClient(EdgeSession session, Guid project, Guid binding, bool writable, Guid? clientEndpoint = null,
    Guid? workspace = null)
{
    private string Prefix => $"/v1/projects/{project}/bindings/{binding}/";
    public Guid ProjectId => project;
    internal string? WorkspaceRelativeDirectory { get; init; }

    internal HashSet<string>? HttpWorkspaceToolLimit()
    {
        if (workspace is null) return null;
        var directory = session.EnumerateLocalWorkspaces()
            .SingleOrDefault(row => row.WorkspaceId == workspace && row.ProjectId == project);
        if (directory == null) return [];
        var policy = session.GetWorkspacePolicy(directory);
        if (policy == null) return null;
        var endpointAllowed = false;
        if (clientEndpoint.HasValue)
        {
            var local = new MvpDispatch(session).LocalEndpoints(directory with { BindingId = binding });
            endpointAllowed = local.Contains(clientEndpoint.Value) &&
                (policy.EndpointId.HasValue ? policy.EndpointId == clientEndpoint : local.Count == 1);
        }
        return EdgeToolCatalog.Tools.Where(tool => tool.IsAvailable(writable, endpointAllowed) &&
            (policy.Tools == null || policy.Tools.Contains(tool.Name, StringComparer.Ordinal)))
            .Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
    }
    internal bool WorkspaceStillBound() => workspace == null ||
        session.EnumerateLocalWorkspaces().Any(row => row.WorkspaceId == workspace && row.ProjectId == project);

    internal async Task<IReadOnlyList<EdgeAccessEndpoint>> WorkspaceEndpointsAsync(LocalBinding directory, CancellationToken ct)
    {
        var local = new MvpDispatch(session).LocalEndpoints(directory);
        if (local.Count == 0) return [];
        var device = session.GetIdentity().DeviceId;
        var registered = await ReadAsync("endpoints", ct);
        return registered.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("deviceId").GetGuid() == device)
            .Select(item => new EdgeAccessEndpoint(item.GetProperty("endpointId").GetGuid(), item.GetProperty("name").GetString()!))
            .Where(e => local.Contains(e.Id)).ToArray();
    }

    public async Task<JsonElement> AssignedAsync(CancellationToken ct)
    {
        var tasks = await ReadAsync("tasks", ct);
        var mine = tasks.GetProperty("items").EnumerateArray().Where(t => t.GetProperty("targetEndpointId").GetGuid() == clientEndpoint);
        return JsonSerializer.SerializeToElement(new JsonObject { ["items"] = JsonSerializer.SerializeToNode(mine.ToArray(), EdgeJson.Default.JsonElementArray) }, EdgeJson.Default.JsonObject);
    }

    public async Task<JsonElement> WriteTaskAsync(Guid taskId, string action, JsonObject message, CancellationToken ct)
    {
        if (clientEndpoint is null || !(await AssignedAsync(ct)).GetProperty("items").EnumerateArray()
                .Any(t => t.GetProperty("taskId").GetGuid() == taskId))
            throw new InvalidOperationException("找不到当前客户端端点的任务。");
        return await WriteAsync($"tasks/{taskId}/{action}", message, ct);
    }

    public async Task<LocalBinding> CurrentAsync(CancellationToken ct)
    {
        var rows = await session.BindingsAsync(ct);
        var row = rows.FirstOrDefault(r => r.ProjectId == project && r.BindingId == binding && (workspace == null || r.WorkspaceId == workspace))
            ?? throw new InvalidOperationException("项目绑定已失效。请在 Edge 核对授权并重新绑定。");
        return writable ? row : row with { AccessMode = "readOnly" };
    }

    public async Task<JsonElement> ReadAsync(string resource, CancellationToken ct)
    {
        await CurrentAsync(ct);
        return await RequestAsync(HttpMethod.Get, resource, null, ct);
    }

    public async Task<JsonElement> WriteAsync(string resource, JsonObject value, CancellationToken ct)
    {
        if (!writable || (await CurrentAsync(ct)).AccessMode != "readWrite")
            throw new InvalidOperationException("当前客户端或设备仅有只读权限。");
        return await RequestAsync(HttpMethod.Post, resource, value.ToJsonString(), ct);
    }

    private async Task<JsonElement> RequestAsync(HttpMethod method, string resource, string? json, CancellationToken ct)
    {
        using var body = json is null ? null : new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await session.SendAsync(method, Prefix + resource, body, ct);
        if (method == HttpMethod.Post && resource == "knowledge" && response.StatusCode == HttpStatusCode.BadRequest)
        {
            try
            {
                var document = await response.Content.ReadFromJsonAsync(EdgeJson.Web.JsonElement, ct);
                if (document.ValueKind == JsonValueKind.Object && document.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.String && code.ValueEquals("observation_confirmation_required"))
                    throw new ObservationConfirmationRequiredException();
            }
            catch (JsonException) { /* Proxies and older Core versions can return a plain HTTP error. */ }
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Core 返回 {(int)response.StatusCode}。", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync(EdgeJson.Web.JsonElement, ct);
    }

    private SqliteConnection OpenDrafts()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = Path.Combine(session.StateDirectory, "mvp.sqlite3") }.ToString());
        try
        {
            db.Open();
            using var tx = db.BeginTransaction(deferred: false);
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS draft(
                    binding TEXT NOT NULL, request TEXT NOT NULL, resource TEXT NOT NULL, body TEXT NOT NULL,
                    paused INTEGER NOT NULL DEFAULT 0, source TEXT, PRIMARY KEY(binding,request));
                SELECT COUNT(*) FROM pragma_table_info('draft') WHERE name='source'
                """;
            if ((long)cmd.ExecuteScalar()! == 0)
            {
                cmd.CommandText = "ALTER TABLE draft ADD COLUMN source TEXT";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private sealed record Draft(string Resource, Guid Request, string Body, string? Source)
    {
        public EdgeRequestSource SourceFor(bool replay)
        {
            if (Source != null)
            {
                try
                {
                    if (JsonSerializer.Deserialize(Source, EdgeJson.Default.EdgeRequestSource) is { } original)
                        return original with { Replay = replay };
                }
                catch (JsonException) { /* A damaged optional source must not block the business draft. */ }
            }
            return new EdgeRequestSource(null, Replay: replay);
        }
    }

    public async Task<JsonElement> SubmitAsync(string resource, Guid request, JsonObject value, CancellationToken ct)
    {
        if (!writable || request == Guid.Empty || resource is not ("knowledge" or "feedback"))
            throw new InvalidOperationException("需要有效的写权限和请求标识。");
        if (!WorkspaceStillBound()) throw new InvalidOperationException("目录绑定已解除，请重新连接。");
        var json = value.ToJsonString();
        // Knowledge can include 32 pinned locators plus the body and evidence, up to six JSON characters per UTF-16 unit.
        var maxLength = resource == "knowledge" ? 320 * 1024 : 32 * 1024;
        if (json.Length > maxLength) throw new ArgumentException("提交内容过长。");
        var origin = session.RequestSource;
        var draft = new Draft(resource, request, json, JsonSerializer.Serialize(origin with
        {
            OriginAt = (origin.OriginAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            Replay = false
        }, EdgeJson.Default.EdgeRequestSource));
        using var db = OpenDrafts();
        bool replay;
        using (var tx = db.BeginTransaction(deferred: false))
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO draft(binding,request,resource,body,source) VALUES ($b,$r,$path,$body,$source)
                ON CONFLICT(binding,request) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("$b", binding.ToString());
            cmd.Parameters.AddWithValue("$r", request.ToString());
            cmd.Parameters.AddWithValue("$path", resource);
            cmd.Parameters.AddWithValue("$body", json);
            cmd.Parameters.AddWithValue("$source", draft.Source);
            replay = cmd.ExecuteNonQuery() == 0;
            if (replay)
            {
                cmd.CommandText = "SELECT resource,body,paused,source FROM draft WHERE binding=$b AND request=$r";
                using var row = cmd.ExecuteReader();
                if (!row.Read() || row.GetString(0) != resource || row.GetString(1) != json || row.GetInt32(2) != 0)
                    throw new InvalidOperationException("此前的草稿已暂停或请求标识已用于其他内容。");
                draft = draft with { Source = row.IsDBNull(3) ? null : row.GetString(3) };
            }
            tx.Commit();
        }
        return await SendDraftAsync(db, draft, replay, ct);
    }

    private async Task<JsonElement> SendDraftAsync(SqliteConnection db, Draft draft, bool replay, CancellationToken ct)
    {
        JsonElement result;
        try
        {
            var local = await CurrentAsync(ct);
            if (local.AccessMode != "readWrite") throw new InvalidOperationException("该项目现为只读。");
            // Even a legacy row with no origin must override the worker's ambient identity.
            using var sending = draft.SourceFor(replay).Use();
            result = await RequestAsync(HttpMethod.Post, draft.Resource, draft.Body, ct);
        }
        catch (HttpRequestException e) when (e.StatusCode is null or HttpStatusCode.TooManyRequests || (int?)e.StatusCode >= 500)
        {
            return JsonSerializer.SerializeToElement(new JsonObject { ["outcome"] = "queuedLocally", ["requestId"] = draft.Request }, EdgeJson.Default.JsonObject);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return JsonSerializer.SerializeToElement(new JsonObject { ["outcome"] = "queuedLocally", ["requestId"] = draft.Request }, EdgeJson.Default.JsonObject);
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException)
        {
            UpdateDraft(db, draft, pause: true);
            throw;
        }
        UpdateDraft(db, draft, pause: false);
        return result;
    }

    private void UpdateDraft(SqliteConnection db, Draft draft, bool pause)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = (pause ? "UPDATE draft SET paused=1" : "DELETE FROM draft")
            + " WHERE binding=$b AND request=$r AND resource=$path AND body=$body AND source IS $source";
        cmd.Parameters.AddWithValue("$b", binding.ToString());
        cmd.Parameters.AddWithValue("$r", draft.Request.ToString());
        cmd.Parameters.AddWithValue("$path", draft.Resource);
        cmd.Parameters.AddWithValue("$body", draft.Body);
        cmd.Parameters.AddWithValue("$source", (object?)draft.Source ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public async Task SyncAsync(CancellationToken ct)
    {
        if (!writable) return;
        using var db = OpenDrafts();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT resource,request,body,source FROM draft WHERE binding=$b AND paused=0 ORDER BY rowid LIMIT 50";
        cmd.Parameters.AddWithValue("$b", binding.ToString());
        var drafts = new List<Draft>();
        using (var rows = cmd.ExecuteReader())
            while (rows.Read()) drafts.Add(new(rows.GetString(0), Guid.Parse(rows.GetString(1)), rows.GetString(2),
                rows.IsDBNull(3) ? null : rows.GetString(3)));
        foreach (var row in drafts)
        {
            var result = await SendDraftAsync(db, row, replay: true, ct);
            if (result.TryGetProperty("outcome", out var outcome) && outcome.GetString() == "queuedLocally") break;
        }
    }

    internal static async Task SyncPendingAsync(EdgeSession session, CancellationToken ct)
    {
        var dbPath = Path.Combine(session.StateDirectory, "mvp.sqlite3");
        if (!File.Exists(dbPath)) return;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='draft'";
        if ((long)cmd.ExecuteScalar()! == 0) return;
        cmd.CommandText = "SELECT DISTINCT binding FROM draft WHERE paused=0";
        var pending = new List<Guid>();
        using (var rows = cmd.ExecuteReader())
            while (rows.Read()) pending.Add(Guid.Parse(rows.GetString(0)));
        if (pending.Count == 0) return;
        var bindings = await session.BindingsAsync(ct);
        foreach (var id in pending)
        {
            var local = bindings.FirstOrDefault(b => b.BindingId == id && b.AccessMode == "readWrite");
            if (local is null)
            {
                using var pause = db.CreateCommand();
                pause.CommandText = "UPDATE draft SET paused=1 WHERE binding=$b";
                pause.Parameters.AddWithValue("$b", id.ToString());
                pause.ExecuteNonQuery();
                continue;
            }
            try { await new ProjectClient(session, local.ProjectId, id, true).SyncAsync(ct); }
            catch (HttpRequestException e) when (e.StatusCode.HasValue && (int)e.StatusCode.Value < 500 && e.StatusCode != HttpStatusCode.TooManyRequests)
            { /* This draft was paused. Other authorized projects can still synchronize. */ }
        }
    }
}
