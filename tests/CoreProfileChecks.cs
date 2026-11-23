#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ProjectHub.Edge;
using ProjectHub.Edge.Desktop;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(95));
var ct = timeout.Token;
var temp = Directory.CreateTempSubdirectory("projecthub-core-profiles-");
var root = Path.Combine(temp.FullName, "state");
var directory = Directory.CreateDirectory(Path.Combine(temp.FullName, "project")).FullName;
var executable = Path.Combine(temp.FullName, "ProjectHub.Edge.exe");
File.WriteAllBytes(executable, []);
var deviceId = Guid.NewGuid();
var edgeId = Guid.NewGuid();
var projectId = Guid.NewGuid();
var bindingId = Guid.NewGuid();
var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var seenWrongToken = 0;
var savedToB = 0;
var pairs = new System.Collections.Concurrent.ConcurrentDictionary<Guid, byte>();
Process? worker = null;

async Task<WebApplication> StartCore(char tag)
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    var app = builder.Build();
    app.Run(async ctx =>
    {
        var path = ctx.Request.Path.Value!;
        var auth = ctx.Request.Headers.Authorization.ToString();
        var token = new string(tag, 64);
        if (path.EndsWith("/redeem"))
        {
            if (auth.Length != 0) Interlocked.Increment(ref seenWrongToken);
            using var body = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
            var id = Guid.Parse(path.Split('/')[3]);
            if (body.RootElement.GetProperty("oneTimeCode").GetString() != new string('1', 32) || !pairs.TryAdd(id, 0))
                ctx.Response.StatusCode = 401;
            else await ctx.Response.WriteAsJsonAsync(new { deviceId, edgeId, accessToken = token,
                refreshToken = new string(char.ToLowerInvariant(tag), 64), expiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            return;
        }
        if (auth != "Bearer " + token) { Interlocked.Increment(ref seenWrongToken); ctx.Response.StatusCode = 401; return; }
        if (path == "/v1/hold") { blocked.TrySetResult(); await unblock.Task.WaitAsync(ct); }
        else if (path == "/v1/projects") await ctx.Response.WriteAsJsonAsync(new {
            items = new[] { new { projectId, bindingId, name = "Project " + tag, accessMode = "readWrite" } } });
        else if (ctx.Request.Method == "POST")
        {
            if (tag == 'A') ctx.Response.StatusCode = 503;
            else { Interlocked.Increment(ref savedToB); await ctx.Response.WriteAsJsonAsync(new { accepted = true }); }
        }
    });
    await app.StartAsync(ct);
    return app;
}

static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
static async Task Reject(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { return; }
    throw new Exception(message);
}
static string Code() => Guid.NewGuid() + "." + new string('1', 32);
await using var a = await StartCore('A');
await using var b = await StartCore('B');
try
{
    var desktop = new EdgeDesktopClient(root);
    var urlA = a.Urls.Single();
    var urlB = b.Urls.Single();
    var initial = await desktop.PairAsync(urlA, Code(), "A", ct);
    var old = desktop.Session;
    var oldWorkspace = await old.BindAsync(projectId, directory, ct);
    old.SetWorkspacePolicy(oldWorkspace, new(["list_projects"], Guid.NewGuid()));
    old.EnableAntigravity(executable);
    var plugin = Path.Combine(directory, ".agents", "plugins", "nodicarta", "mcp_config.json");
    Check(File.ReadAllText(plugin).Contains(oldWorkspace.WorkspaceId.ToString()), "first profile plugin");
    var oldClient = new ProjectClient(old, projectId, bindingId, true, workspace: oldWorkspace.WorkspaceId);
    var cli = Path.Combine(AppContext.BaseDirectory, "ProjectHub.Edge.exe");
    var workerStart = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in new[] { "run", "--state", root }) workerStart.ArgumentList.Add(arg);
    worker = Process.Start(workerStart)!;
    while (!File.Exists(Path.Combine(root, "connection.json"))) { ct.ThrowIfCancellationRequested(); await Task.Delay(100, ct); }
    var transport = new StdioClientTransport(new StdioClientTransportOptions {
        Name = "A-session", Command = cli, Arguments = ["mcp-stdio-global", "--state", root], WorkingDirectory = directory });
    await using var oldMcp = await McpClient.CreateAsync(transport, cancellationToken: ct);
    Check((await oldMcp.ListToolsAsync(cancellationToken: ct)).Select(t => t.Name).SequenceEqual(["list_projects"]), "live MCP starts on A");
    var draft = await oldClient.SubmitAsync("knowledge", Guid.NewGuid(), new { text = "private A" }, ct);
    Check(draft.GetProperty("outcome").GetString() == "queuedLocally", "first profile queued a real submission");
    Check(old.ReadPairingInventory(initial.DeviceId) == (1, 1), "switch dialog counts the old binding and unsent draft");
    // Seed an earlier installation's chat route and terminal delivery receipt without launching a real chat.
    var endpoint = Guid.NewGuid();
    await new MvpDispatch(old).TickAsync(ct);
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "mvp.sqlite3"), Pooling = false }.ToString()))
    {
        db.Open();
        using var sql = db.CreateCommand();
        sql.CommandText = """
            INSERT INTO route(endpoint,project,binding,directory,executable,thread,owner_sid)
              VALUES($endpoint,$project,$binding,$directory,$exe,$id,$sid);
            INSERT INTO attempt(task,endpoint,claim,result,stage) VALUES($id,$endpoint,$id,$id,'completed');
            """;
        using var user = System.Security.Principal.WindowsIdentity.GetCurrent();
        sql.Parameters.AddWithValue("$endpoint", endpoint.ToString());
        sql.Parameters.AddWithValue("$project", projectId.ToString());
        sql.Parameters.AddWithValue("$binding", bindingId.ToString());
        sql.Parameters.AddWithValue("$directory", directory);
        sql.Parameters.AddWithValue("$exe", executable);
        sql.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        sql.Parameters.AddWithValue("$sid", user.User!.Value);
        sql.ExecuteNonQuery();
    }
    Check(new MvpDispatch(old).LocalEndpoints(oldWorkspace).Contains(endpoint), "old profile contains a matching legacy chat route");
    var bad = Guid.NewGuid() + "." + new string('0', 32);
    await Reject(() => desktop.ReplacePairingAsync(initial, urlB, bad, "B", ct), "invalid code changed Core");
    Check(!Directory.EnumerateDirectories(root, ".edge-core-*").Any(), "a confirmed rejection leaves no empty profile");
    Check(desktop.GetIdentity() == initial && desktop.Session.GetWorkspacePolicy(oldWorkspace) != null, "failed exchange preserves identity and settings");

    var inFlight = old.SendAsync(HttpMethod.Get, "/v1/hold", ct: ct);
    await blocked.Task.WaitAsync(ct);
    var secondCode = Code();
    var replacement = await desktop.ReplacePairingAsync(initial, urlB, secondCode, "B", ct);
    unblock.TrySetResult();
    using (var completion = await inFlight) Check(completion.IsSuccessStatusCode, "in-flight A request completes on A");
    await Reject(() => oldClient.ReadAsync("knowledge", ct), "an existing project session should require reconnection");
    Check((await oldMcp.ListToolsAsync(cancellationToken: ct)).Select(t => t.Name).SequenceEqual(["get_edge_status"]), "live MCP loses all old project tools");
    var diagnostic = await oldMcp.CallToolAsync("get_edge_status", cancellationToken: ct);
    var diagnosticText = diagnostic.Content.OfType<TextContentBlock>().Single().Text!;
    using (var info = System.Text.Json.JsonDocument.Parse(diagnosticText))
        Check(info.RootElement.GetProperty("status").GetString() == "changed", "live MCP directs the old chat to reconnect");
    await Reject(() => old.PairAsync(new Uri(urlB), Code(), "B", true, ct), "an already-open CLI cannot replay a replacement");
    await Reject(() => { _ = EdgeSession.Open(root, pinLegacyProfile: true); return Task.CompletedTask; }, "legacy pinned registration follows new Core");
    var current = desktop.Session;
    Check(current.StateDirectory != old.StateDirectory && root == desktop.StateDirectory && desktop.GetIdentity() == replacement, "pinned storage and stable registration root");
    Check(new EdgeDesktopClient(root).GetIdentity() == replacement && EdgeSession.Open(current.StateDirectory, true).GetIdentity() == replacement,
        "restarted desktop/global CLI and newly pinned registrations select B");
    Check(current.EnumerateLocalWorkspaces().Count == 0 && !File.Exists(Path.Combine(current.StateDirectory, "mvp.sqlite3")),
        "matching remote project/device/binding IDs do not inherit local bindings, drafts or dispatch database");
    current.EnableAntigravity(executable);
    var rebound = await desktop.BindAsync(projectId, directory, ct);
    Check(rebound.WorkspaceId != oldWorkspace.WorkspaceId && current.GetWorkspacePolicy(rebound) == null, "explicit rebind has a fresh policy");
    Check(!new MvpDispatch(current).LocalEndpoints(rebound).Any(), "a new grant on the same folder cannot inherit A's chat route");
    Check(File.ReadAllText(plugin).Contains(rebound.WorkspaceId.ToString()) && !File.ReadAllText(plugin).Contains(oldWorkspace.WorkspaceId.ToString()),
        "freshly authorized rebind replaces only the old managed per-directory plugin");
    await ProjectClient.SyncPendingAsync(current, ct);
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "mvp.sqlite3"), Pooling = false }.ToString()))
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM draft WHERE paused=0 AND body LIKE '%private A%'";
        Check((long)cmd.ExecuteScalar()! == 1 && savedToB == 0, "A draft is preserved and never sent to B");
        cmd.CommandText = "SELECT COUNT(*) FROM attempt WHERE stage='completed'";
        Check((long)cmd.ExecuteScalar()! == 1, "archived chat delivery receipts are preserved");
    }
    // An interrupted activation may already have redeemed the single-use code and persisted its new credentials.
    var thirdCode = Code();
    using (File.Open(Path.Combine(root, "active-core-profile"), FileMode.Open, FileAccess.Read, FileShare.Read))
        await Reject(() => desktop.ReplacePairingAsync(replacement, urlB, thirdCode, "B2", ct), "pinned activation marker should block the commit");
    Check(desktop.GetIdentity() == replacement, "failed activation preserves current profile");
    var third = await desktop.ReplacePairingAsync(replacement, urlB, thirdCode, "B2", ct);
    Check(third != replacement && pairs.Count == 3, "activation retry uses the persisted receipt, without consuming the pairing code twice");
    await Reject(() => desktop.ReplacePairingAsync(replacement, urlB, Code(), "stale", ct), "same remote IDs must not authorize a stale UI confirmation");
    await Reject(() => desktop.ReplacePairingAsync(third, urlB, secondCode, "archived", ct), "a spent code must never reselect its archived profile");
    while (!File.Exists(Path.Combine(desktop.Session.StateDirectory, "connection.json"))) await Task.Delay(200, ct);
    Check(!worker.HasExited && savedToB == 0, "the already-running CLI worker follows the active Core without replaying A's old draft");
    Check(seenWrongToken == 0 && desktop.GetIdentity() == third, "old credentials never reached the new address");
    Console.WriteLine("PASS: failed/retried activation, concurrent requests, identity collisions, archived drafts, real CLI heartbeat and live MCP reconnect, new directory policy and Antigravity rebind.");
}
finally
{
    unblock.TrySetResult();
    if (worker != null)
    {
        if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(); }
        worker.Dispose();
    }
    SqliteConnection.ClearAllPools();
    temp.Delete(true);
}
