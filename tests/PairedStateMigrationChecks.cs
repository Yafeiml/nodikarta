#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge/ProjectHub.Edge.csproj
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ProjectHub.Edge;

var tmp = Directory.CreateTempSubdirectory("nodicarta-paired-migration-");
var local = Path.Combine(tmp.FullName, "Local");
var old = Path.Combine(local, "ProjectHub", "Edge");
var current = Path.Combine(local, "Nodicarta", "Edge");
var workspace = Directory.CreateDirectory(Path.Combine(tmp.FullName, "旧版工作目录")).FullName;
var device = Guid.NewGuid();
var edge = Guid.NewGuid();
var project = Guid.NewGuid();
var grant = Guid.NewGuid();
var pairing = Guid.NewGuid();
var request = Guid.NewGuid();
var pairCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var submission = new { requestId = request, text = "离线迁移回归" };
int redeemed = 0, online = 0, accepted = 0, attempts = 0;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var ct = timeout.Token;
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var core = builder.Build();
core.Run(async ctx =>
{
    var path = ctx.Request.Path.Value;
    if (ctx.Request.Method == "POST" && path == $"/v1/device-pairings/{pairing:D}/redeem")
    {
        using var body = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
        if (body.RootElement.GetProperty("oneTimeCode").GetString() != pairCode || Interlocked.Increment(ref redeemed) != 1)
        { ctx.Response.StatusCode = 401; return; }
        await ctx.Response.WriteAsJsonAsync(new { deviceId = device, edgeId = edge, accessToken = token,
            refreshToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), expiresAt = DateTimeOffset.UtcNow.AddHours(1) }, ct);
        return;
    }
    if (ctx.Request.Headers.Authorization.ToString() != "Bearer " + token) { ctx.Response.StatusCode = 401; return; }
    if (ctx.Request.Method == "GET" && path == "/v1/projects")
        await ctx.Response.WriteAsJsonAsync(new { items = new[] { new { projectId = project, bindingId = grant, name = "migration", accessMode = "readWrite" } } }, ct);
    else if (ctx.Request.Method == "POST" && path == $"/v1/projects/{project:D}/bindings/{grant:D}/knowledge")
    {
        Interlocked.Increment(ref attempts);
        if (Volatile.Read(ref online) == 0) { ctx.Response.StatusCode = 503; return; }
        using var body = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
        if (body.RootElement.GetProperty("requestId").GetGuid() != request ||
            body.RootElement.GetProperty("text").GetString() != submission.text) { ctx.Response.StatusCode = 400; return; }
        Interlocked.Increment(ref accepted);
        await ctx.Response.WriteAsJsonAsync(new { accepted = true }, ct);
    }
    else ctx.Response.StatusCode = 404;
});
try
{
    await core.StartAsync(ct);
    var address = new Uri(core.Urls.Single());
    // Phase one uses the original on-disk default before the isolated known-folder mapping is activated.
    var previous = EdgeSession.Open(old);
    var identity = await previous.PairAsync(address, pairing + "." + pairCode, "原设备", replace: false, ct);
    var binding = await previous.BindAsync(project, workspace, ct);
    var first = new ProjectClient(previous, project, grant, true, workspace: binding.WorkspaceId);
    var queued = await first.SubmitAsync("knowledge", request, submission, ct);
    Check(identity.DeviceId == device && identity.EdgeId == edge && queued.GetProperty("outcome").GetString() == "queuedLocally" &&
        previous.ReadPairingInventory(device) == (1, 1) && attempts == 1 && accepted == 0, "old paired device holds one unsent draft");
    var encrypted = Ciphertext(previous);
    SqliteConnection.ClearAllPools(); // Simulate the old desktop and its draft store closing.

    // Phase two opens the actual new default with the same Windows DPAPI machine and performs the physical migration.
    using var folders = EdgeStatePaths.UseKnownFoldersForTest(local, Path.Combine(tmp.FullName, "ProgramData"));
    var migrated = EdgeSession.Open(EdgeStatePaths.LocalDefault);
    Check(migrated.RootDirectory == current && new DirectoryInfo(current).LinkTarget == null &&
        new DirectoryInfo(old).ResolveLinkTarget(false)?.FullName == current, "data moved physically to the new default");
    Check(Ciphertext(migrated).AsSpan().SequenceEqual(encrypted) && migrated.GetIdentity() == identity &&
        migrated.GetDeviceLabel(device) == "原设备", "same DPAPI ciphertext decrypts to the original identity");
    var linked = (await migrated.BindingsAsync(ct)).Single();
    Check(linked == binding && migrated.MatchWorkspace(workspace)?.WorkspaceId == binding.WorkspaceId && redeemed == 1,
        "original device credential authenticates, and the original workspace and grant resolve");
    Check(migrated.ReadPairingInventory(device) == (1, 1), "draft survived migration before any replay");
    Volatile.Write(ref online, 1);
    var second = new ProjectClient(migrated, project, grant, true, workspace: binding.WorkspaceId);
    await second.SyncAsync(ct);
    await second.SyncAsync(ct);
    Check(migrated.ReadPairingInventory(device) == (1, 0) && attempts == 2 && accepted == 1 && redeemed == 1,
        "the original draft ID and contents replay exactly once using the migrated device");
    Console.WriteLine("PASS: loopback pairing, identical DPAPI credential, original binding and queued knowledge draft survive physical relocation; original request replayed exactly once.");
}
finally
{
    await core.StopAsync();
    SqliteConnection.ClearAllPools();
    if (new DirectoryInfo(old).LinkTarget != null) Directory.Delete(old);
    tmp.Delete(true);
}

static byte[] Ciphertext(EdgeSession session)
{
    using var db = session.Connect();
    using var sql = db.CreateCommand();
    sql.CommandText = "SELECT credentials FROM identity WHERE singleton=1";
    return (byte[])sql.ExecuteScalar()!;
}

static void Check(bool condition, string check)
{
    if (!condition) throw new InvalidOperationException(check);
}
