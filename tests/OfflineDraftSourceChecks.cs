#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge/ProjectHub.Edge.csproj
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ProjectHub.Edge;

var tmp = Directory.CreateTempSubdirectory("nodicarta-draft-origin-");
var state = Path.Combine(tmp.FullName, "state");
var folder = Directory.CreateDirectory(Path.Combine(tmp.FullName, "workspace")).FullName;
var device = Guid.NewGuid();
var edge = Guid.NewGuid();
var project = Guid.NewGuid();
var binding = Guid.NewGuid();
var pairing = Guid.NewGuid();
var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var lost = Guid.NewGuid();
var concurrent = Guid.NewGuid();
var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var business = new ConcurrentDictionary<Guid, string>();
var posts = new ConcurrentQueue<(Guid Id, Headers Headers, string Body)>();
var reads = new ConcurrentQueue<Headers>();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var ct = timeout.Token;
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
app.Run(async ctx =>
{
    var path = ctx.Request.Path.Value;
    if (path == $"/v1/device-pairings/{pairing}/redeem")
    {
        await ctx.Response.WriteAsJsonAsync(new { deviceId = device, edgeId = edge, accessToken = token,
            refreshToken = token, expiresAt = DateTimeOffset.UtcNow.AddHours(1) }, ct);
        return;
    }
    if (ctx.Request.Headers.Authorization != "Bearer " + token) { ctx.Response.StatusCode = 401; return; }
    var headers = Headers.Read(ctx.Request.Headers);
    if (path == "/v1/projects")
    {
        reads.Enqueue(headers);
        await ctx.Response.WriteAsJsonAsync(new { items = new[] { new { projectId = project, bindingId = binding, name = "test", accessMode = "readWrite" } } }, ct);
        return;
    }
    if (ctx.Request.Method != "POST" || (path != $"/v1/projects/{project}/bindings/{binding}/knowledge" &&
        path != $"/v1/projects/{project}/bindings/{binding}/feedback")) { ctx.Response.StatusCode = 404; return; }
    using var reader = new StreamReader(ctx.Request.Body);
    var text = await reader.ReadToEndAsync(ct);
    using var document = JsonDocument.Parse(text);
    var id = document.RootElement.GetProperty("requestId").GetGuid();
    posts.Enqueue((id, headers, text));
    var first = business.TryAdd(id, text);
    if (business[id] != text) { ctx.Response.StatusCode = 409; return; }
    if (first && id == concurrent)
    {
        blocked.TrySetResult();
        await release.Task.WaitAsync(ct);
    }
    if (first && id == lost)
    {
        // The business transaction committed, but the success response never arrives intact.
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength = 200;
        await ctx.Response.WriteAsync("{", ct);
        await ctx.Response.Body.FlushAsync(ct);
        ctx.Abort();
        return;
    }
    await ctx.Response.WriteAsJsonAsync(new { accepted = true }, ct);
});

try
{
    await app.StartAsync(ct);
    var session = EdgeSession.Open(state);
    await session.PairAsync(new(app.Urls.Single()), pairing + "." + code, "draft test", false, ct);
    var bound = await session.BindAsync(project, folder, ct);
    var client = new ProjectClient(session, project, binding, true, workspace: bound.WorkspaceId);
    var mcp = new EdgeRequestSource("mcp", "Example Client", "2.5");
    var background = new EdgeRequestSource("background");
    var cli = new EdgeRequestSource("cli");

    void WriteLegacy(bool create, bool damagedSource)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(state, "mvp.sqlite3") }.ToString());
        db.Open();
        using var sql = db.CreateCommand();
        if (create)
        {
            sql.CommandText = "CREATE TABLE draft(binding TEXT NOT NULL,request TEXT NOT NULL,resource TEXT NOT NULL,body TEXT NOT NULL,paused INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(binding,request))";
            sql.ExecuteNonQuery();
        }
        var id = Guid.NewGuid();
        // The old binary specifies exactly these columns. Preserve even a non-canonical body on replay.
        var raw = $$"""{"requestId":"{{id}}",  "text":"\u4E2D"}""";
        sql.CommandText = "INSERT INTO draft(binding,request,resource,body) VALUES($b,$r,'feedback',$body) ON CONFLICT(binding,request) DO NOTHING";
        sql.Parameters.AddWithValue("$b", binding.ToString());
        sql.Parameters.AddWithValue("$r", id.ToString());
        sql.Parameters.AddWithValue("$body", raw);
        sql.ExecuteNonQuery();
        if (damagedSource)
        {
            sql.CommandText = "UPDATE draft SET source='bad json' WHERE binding=$b AND request=$r";
            sql.ExecuteNonQuery();
        }
    }
    for (int i = 0; i < 3; i++)
    {
        WriteLegacy(create: i == 0, damagedSource: i == 2);
        reads.Clear();
        using (background.Use()) await ProjectClient.SyncPendingAsync(session, ct);
        var legacy = posts.Last();
        Check(legacy.Headers == new Headers(null, null, null, null, "1") && legacy.Body.Contains("  \"text\":\"\\u4E2D\""),
            "old schema and old binary inserts replay byte-for-byte with unknown origin");
        Check(reads.Count >= 2 && reads.All(h => h == new Headers("background", null, null, null, null)), "worker access checks retain worker source");
    }

    var payload = new { requestId = lost, text = "original knowledge 草稿" };
    var earliest = DateTimeOffset.UtcNow;
    using (mcp.Use())
    {
        var queued = await client.SubmitAsync("knowledge", lost, payload, ct);
        Check(queued.GetProperty("outcome").GetString() == "queuedLocally", "lost receipt retained locally");
    }
    var firstPost = posts.Last();
    Check(firstPost.Id == lost && firstPost.Headers is { Entry: "mcp", Client: "Example Client", Version: "2.5", Replay: null },
        "initial submission sends original source without replay flag");
    Check(DateTimeOffset.TryParseExact(firstPost.Headers.At, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var started) && earliest <= started && started <= DateTimeOffset.UtcNow, "original enqueue UTC time");
    reads.Clear();
    // Reopening the database discards the submitting client's ambient context.
    using (background.Use()) await ProjectClient.SyncPendingAsync(EdgeSession.Open(state), ct);
    var repeated = posts.Where(p => p.Id == lost).ToArray();
    Check(repeated.Length == 2 && repeated[1].Body == firstPost.Body && repeated[1].Headers == firstPost.Headers with { Replay = "1" }
        && reads.All(h => h == new Headers("background", null, null, null, null)), "replay after lost receipt preserves source, timestamp and fingerprint");

    var feedback = new { requestId = concurrent, content = "feedback" };
    Task<JsonElement> firstCall;
    using (mcp.Use()) firstCall = client.SubmitAsync("feedback", concurrent, feedback, ct);
    await blocked.Task.WaitAsync(ct);
    var inFlight = posts.Last();
    using (cli.Use())
    {
        try { await client.SubmitAsync("feedback", concurrent, new { requestId = concurrent, content = "different" }, ct); throw new Exception("accepted changed intent"); }
        catch (InvalidOperationException e) when (e.Message.Contains("请求标识")) { }
        reads.Clear();
        var duplicate = await client.SubmitAsync("feedback", concurrent, feedback, ct);
        Check(duplicate.GetProperty("accepted").GetBoolean() && reads.Single() == new Headers("cli", null, null, null, null), "conflict retry authorization uses caller");
    }
    var retries = posts.Where(p => p.Id == concurrent).ToArray();
    Check(retries.Length == 2 && inFlight.Headers.Replay == null && retries[1].Headers == inFlight.Headers with { Replay = "1" }
        && retries[1].Body == inFlight.Body, "concurrent same-id submission uses first caller's metadata and body");
    // An older process starts another attempt after the duplicate was acknowledged and removed.
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(state, "mvp.sqlite3") }.ToString()))
    {
        db.Open();
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO draft(binding,request,resource,body) VALUES($b,$r,'feedback',$body)";
        insert.Parameters.AddWithValue("$b", binding.ToString());
        insert.Parameters.AddWithValue("$r", concurrent.ToString());
        insert.Parameters.AddWithValue("$body", inFlight.Body);
        insert.ExecuteNonQuery();
    }
    release.TrySetResult();
    await firstCall;
    Check(session.ReadPairingInventory(device) == (1, 1), "an older in-flight acknowledgement cannot retire the new draft");
    using (background.Use()) await client.SyncAsync(ct);
    Check(posts.Last().Headers == new Headers(null, null, null, null, "1"), "new legacy attempt keeps its own unknown origin");
    Check(business.Count == 5 && session.ReadPairingInventory(device) == (1, 0), "each original business request accepted once and drafts retired");
    Console.WriteLine("PASS draft provenance: legacy migration/inserts, lost receipt, raw body and ID, UTC origin, concurrent duplicates, worker and CLI checks.");
}
finally
{
    release.TrySetResult();
    await app.StopAsync();
    SqliteConnection.ClearAllPools();
    tmp.Delete(true);
}

static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
sealed record Headers(string? Entry, string? Client, string? Version, string? At, string? Replay)
{
    internal static Headers Read(IHeaderDictionary h) => new(h["X-Nodicarta-Entry"].SingleOrDefault(),
        h["X-Nodicarta-Client"].SingleOrDefault(), h["X-Nodicarta-Client-Version"].SingleOrDefault(),
        h["X-Nodicarta-Origin-At"].SingleOrDefault(), h["X-Nodicarta-Replay"].SingleOrDefault());
}
