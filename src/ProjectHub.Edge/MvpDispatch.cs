using System.Text.Json.Nodes;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ProjectHub.Edge;

internal sealed class MvpDispatch(EdgeSession session)
{
    private sealed record Route(Guid Project, Guid Binding, Guid Endpoint, string Directory, string Executable, Guid Thread, Guid? Workspace);
    private sealed record Attempt(Guid Claim, Guid Result, string Stage);

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(session.StateDirectory, "mvp.sqlite3") }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS route(endpoint TEXT PRIMARY KEY, project TEXT NOT NULL, binding TEXT NOT NULL,
                directory TEXT NOT NULL, executable TEXT NOT NULL, thread TEXT NOT NULL, owner_sid TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS attempt(task TEXT PRIMARY KEY, endpoint TEXT NOT NULL, claim TEXT NOT NULL,
                result TEXT NOT NULL, stage TEXT NOT NULL, turn TEXT, detail TEXT);
            """;
        cmd.ExecuteNonQuery();
        return db;
    }

    private static string UserSid()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此派工功能仅在 Windows 上提供。");
        using var user = WindowsIdentity.GetCurrent();
        return user.User?.Value ?? throw new InvalidOperationException("未取得桌面用户身份。");
    }

    internal static void RemoveWorkspaceRoutes(string stateDirectory, Guid project, Guid workspace, string directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(stateDirectory, "mvp.sqlite3");
        if (!File.Exists(file)) return;
        // Retire legacy path-only routes durably before the caller commits the workspace removal.
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWrite }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous=FULL";
        cmd.ExecuteNonQuery();
        using var tx = db.BeginTransaction();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='route'";
        if (cmd.ExecuteScalar() == null) return;
        cmd.CommandText = "SELECT 1 FROM pragma_table_info('route') WHERE name='workspace'";
        var tracksWorkspace = cmd.ExecuteScalar() != null;
        db.CreateCollation("WINDOWS_PATH", StringComparer.OrdinalIgnoreCase.Compare);
        cmd.CommandText = "DELETE FROM route WHERE project=$project AND directory=$path COLLATE WINDOWS_PATH" +
            (tracksWorkspace ? " AND (workspace=$workspace OR workspace IS NULL)" : "");
        cmd.Parameters.AddWithValue("$project", project.ToString());
        cmd.Parameters.AddWithValue("$path", directory);
        cmd.Parameters.AddWithValue("$workspace", workspace.ToString());
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public async Task<JsonObject> LinkAsync(Guid project, string directory, string exe, Guid thread, string name, CancellationToken ct)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var local = (await session.BindingsAsync(ct)).FirstOrDefault(row => row.ProjectId == project &&
            string.Equals(row.LocalPath, path, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("请先绑定此项目和该聊天实际使用的工作目录。");
        var runtime = new CodexRuntime(exe, path, thread);
        var probe = await runtime.ProbeAsync(ct);
        if (!probe.Available) throw new InvalidOperationException(probe.Detail);
        var api = new ProjectClient(session, project, local.BindingId, true, workspace: local.WorkspaceId);
        var info = await api.WriteAsync("endpoints", new JsonObject { ["requestId"] = Guid.NewGuid(), ["name"] = name }, ct);
        var endpoint = info.GetProperty("endpointId").GetGuid();
        using var bindingDb = session.Connect();
        using var bindingLock = bindingDb.BeginTransaction(deferred: false);
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (session.MatchWorkspace(path)?.WorkspaceId != local.WorkspaceId)
            throw new InvalidOperationException("目录绑定已变化，无法连接此聊天。");
        using var save = db.CreateCommand();
        save.Transaction = tx;
        save.CommandText = "SELECT 1 FROM pragma_table_info('route') WHERE name='workspace'";
        if (save.ExecuteScalar() == null)
        {
            save.CommandText = "ALTER TABLE route ADD COLUMN workspace TEXT";
            save.ExecuteNonQuery();
        }
        save.CommandText = """
            INSERT INTO route(endpoint,project,binding,directory,executable,thread,owner_sid,workspace)
            VALUES($e,$p,$b,$d,$x,$t,$s,$w) ON CONFLICT(endpoint) DO NOTHING
            """;
        save.Parameters.AddWithValue("$e", endpoint.ToString());
        save.Parameters.AddWithValue("$p", project.ToString());
        save.Parameters.AddWithValue("$b", local.BindingId.ToString());
        save.Parameters.AddWithValue("$d", local.LocalPath);
        save.Parameters.AddWithValue("$x", exe);
        save.Parameters.AddWithValue("$t", thread.ToString());
        save.Parameters.AddWithValue("$s", UserSid());
        save.Parameters.AddWithValue("$w", local.WorkspaceId.ToString());
        if (save.ExecuteNonQuery() == 0 && !Routes(db, tx).Any(r => r.Endpoint == endpoint && r.Thread == thread &&
                (r.Workspace == null || r.Workspace == local.WorkspaceId) &&
                r.Binding == local.BindingId && string.Equals(r.Directory, path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("端点已绑定其他会话。请为新会话选择独立端点名称。");
        tx.Commit();
        return new JsonObject { ["projectId"] = project, ["bindingId"] = local.BindingId, ["endpointId"] = endpoint, ["threadId"] = thread };
    }

    internal HashSet<Guid> LocalEndpoints(LocalBinding binding)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var file = Path.Combine(session.StateDirectory, "mvp.sqlite3");
        if (!File.Exists(file)) return [];
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var check = db.CreateCommand();
        check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='route'";
        if (check.ExecuteScalar() is null) return [];
        return Routes(db).Where(route => route.Project == binding.ProjectId && route.Binding == binding.BindingId
            && (route.Workspace == null || route.Workspace == binding.WorkspaceId)
            && string.Equals(route.Directory, binding.LocalPath, StringComparison.OrdinalIgnoreCase))
            .Select(route => route.Endpoint).ToHashSet();
    }

    private static List<Route> Routes(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var sql = db.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = "SELECT 1 FROM pragma_table_info('route') WHERE name='workspace'";
        var workspace = sql.ExecuteScalar() == null ? "NULL" : "workspace";
        sql.CommandText = $"SELECT project,binding,endpoint,directory,executable,thread,{workspace} FROM route WHERE owner_sid=$s";
        sql.Parameters.AddWithValue("$s", UserSid());
        using var rows = sql.ExecuteReader();
        var routes = new List<Route>();
        while (rows.Read()) routes.Add(new(Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), Guid.Parse(rows.GetString(2)),
            rows.GetString(3), rows.GetString(4), Guid.Parse(rows.GetString(5)), rows.IsDBNull(6) ? null : Guid.Parse(rows.GetString(6))));
        return routes;
    }

    // Serialize the final handoff with route removal, including other Edge processes.
    private IDisposable HoldRoute(SqliteConnection db, Route route, Guid workspace)
    {
        var tx = db.BeginTransaction();
        try
        {
            if (!Routes(db, tx).Contains(route) || session.MatchWorkspace(route.Directory)?.WorkspaceId != workspace)
                throw new InvalidOperationException("目录或聊天绑定已解除。");
            return tx;
        }
        catch { tx.Dispose(); throw; }
    }

    private static Attempt? GetAttempt(SqliteConnection db, Guid endpoint, Guid task, bool create)
    {
        using var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue("$t", task.ToString());
        cmd.Parameters.AddWithValue("$e", endpoint.ToString());
        if (create)
        {
            cmd.CommandText = "INSERT OR IGNORE INTO attempt(task,endpoint,claim,result,stage) VALUES($t,$e,$c,$r,'ready')";
            cmd.Parameters.AddWithValue("$c", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$r", Guid.NewGuid().ToString());
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = "SELECT claim,result,stage FROM attempt WHERE task=$t AND endpoint=$e";
        using var row = cmd.ExecuteReader();
        return row.Read() ? new(Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)), row.GetString(2)) : null;
    }

    private static bool SetStage(SqliteConnection db, Guid task, string from, string to, CodexDelivery? receipt = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE attempt SET stage=$to,turn=$turn,detail=$detail WHERE task=$t AND stage=$from";
        cmd.Parameters.AddWithValue("$from", from); cmd.Parameters.AddWithValue("$to", to);
        cmd.Parameters.AddWithValue("$t", task.ToString());
        cmd.Parameters.AddWithValue("$turn", (object?)receipt?.TurnId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$detail", (object?)receipt?.Detail ?? DBNull.Value);
        return cmd.ExecuteNonQuery() == 1;
    }

    public async Task TickAsync(CancellationToken ct)
    {
        using var db = Open();
        foreach (var route in Routes(db))
        {
            try
            {
                var directory = session.MatchWorkspace(route.Directory);
                if (directory == null || directory.ProjectId != route.Project ||
                    !string.Equals(directory.LocalPath, route.Directory, StringComparison.OrdinalIgnoreCase) ||
                    route.Workspace != null && route.Workspace != directory.WorkspaceId) continue;
                var api = new ProjectClient(session, route.Project, route.Binding, true, workspace: directory.WorkspaceId);
                var tasks = await api.ReadAsync("tasks", ct);
                var runtime = new CodexRuntime(route.Executable, route.Directory, route.Thread);
                foreach (var task in tasks.GetProperty("items").EnumerateArray())
                {
                    if (task.GetProperty("targetEndpointId").GetGuid() != route.Endpoint ||
                        task.GetProperty("status").GetString() is not ("pending" or "claimed") ||
                        !task.TryGetProperty("observation", out var value) || value.ValueKind != JsonValueKind.Object) continue;
                    var observation = value.Deserialize(EdgeJson.Web.CodexObservation)!;
                    var id = task.GetProperty("taskId").GetGuid();
                    var attempt = GetAttempt(db, route.Endpoint, id, task.GetProperty("status").GetString() == "pending");
                    if (attempt is null || attempt.Stage == "completed") continue;
                    if (attempt.Stage == "ready")
                    {
                        await api.WriteAsync($"tasks/{id}/claim", new JsonObject { ["requestId"] = attempt.Claim }, ct);
                        if (session.MatchWorkspace(route.Directory)?.WorkspaceId != directory.WorkspaceId) break;
                        // A crash across this boundary has an unknown delivery outcome. Reconcile; never resubmit.
                        if (!SetStage(db, id, "ready", "dispatched")) continue;
                        var sent = await runtime.SendObservationAsync(id, attempt.Claim, observation, ct,
                            () => HoldRoute(db, route, directory.WorkspaceId));
                        Console.Error.WriteLine($"任务 {id}：{sent.Detail}");
                        if (sent.Status == "failed")
                        {
                            SetStage(db, id, "dispatched", "failed", sent);
                            continue;
                        }
                    }
                    var actual = await runtime.TryReadObservationAsync(id, attempt.Claim, observation, ct);
                    // A separate app-server can temporarily report an interrupted turn while Desktop is still flushing it.
                    // Keep checking this exact attempt; never enqueue it a second time.
                    if (actual.Status == "failed") SetStage(db, id, "dispatched", "failed", actual);
                    if (actual.Status != "completed" || string.IsNullOrWhiteSpace(actual.Text)) continue;
                    await api.WriteAsync($"tasks/{id}/result", new JsonObject { ["requestId"] = attempt.Result, ["result"] = actual.Text }, ct);
                    SetStage(db, id, attempt.Stage == "failed" ? "failed" : "dispatched", "completed", actual);
                    Console.Error.WriteLine($"任务 {id} 的最终结果已由 Core 确认。");
                }
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException)
            {
                Console.Error.WriteLine($"端点 {route.Endpoint}：{e.Message}");
            }
        }
    }
}
