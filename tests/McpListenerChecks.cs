#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge/ProjectHub.Edge.csproj
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ProjectHub.Edge;

// dotnet run --file tests/McpListenerChecks.cs -- <Edge.exe>
var exe = Path.GetFullPath(args.Single());
var root = Directory.CreateTempSubdirectory("projecthub-listeners-");
var state = Path.Combine(root.FullName, "state");
var other = Path.Combine(root.FullName, "other");
EdgeSession.Open(state);
EdgeSession.Open(other);
var project = Guid.NewGuid();
var binding = Guid.NewGuid();
var workspace = Guid.NewGuid();
List<Process> children = [];
Process Start(string dir, params string[] transport)
{
    var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var arg in transport.Concat(["--state", dir, "--project", project.ToString(),
        "--binding", binding.ToString(), "--workspace", workspace.ToString()])) start.ArgumentList.Add(arg);
    start.Environment["PROJECTHUB_MCP_TOKEN"] = Guid.NewGuid().ToString("N");
    var process = Process.Start(start)!;
    children.Add(process);
    return process;
}
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
async Task Wait(string dir, params int?[] ports)
{
    for (int i = 0; i < 100; i++)
    {
        var rows = McpListenerStatus.Read(dir);
        if (rows.Select(row => row.Port).SequenceEqual(ports) && rows.All(row => row.ProjectId == project
            && row.BindingId == binding && row.WorkspaceId == workspace && row.DeviceId == null)) return;
        await Task.Delay(60);
    }
    throw new Exception("Unexpected live transport list.");
}
try
{
    using var occupied = new TcpListener(IPAddress.Loopback, 0);
    occupied.Start();
    var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
    var collision = Start(state, "mcp-http", "--port", port.ToString());
    await collision.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(collision.ExitCode != 0, "An occupied port should not start.");
    await Wait(state);
    occupied.Stop();
    var http = Start(state, "mcp-http", "--port", port.ToString());
    await Wait(state, port);
    using (var probe = new HttpClient(new SocketsHttpHandler { UseProxy = false }))
    using (var response = await probe.GetAsync($"http://127.0.0.1:{port}/mcp"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "HTTP registration must follow a listening MCP host.");
    var a = Start(state, "mcp-stdio");
    var b = Start(state, "mcp-stdio");
    await Wait(state, null, null, port);
    await Wait(other);
    a.StandardInput.Close();
    await a.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    await Wait(state, null, port);
    b.Kill();
    http.Kill();
    await Task.WhenAll(b.WaitForExitAsync(), http.WaitForExitAsync());
    await Wait(state);
    using var replacement = new TcpListener(IPAddress.Loopback, port);
    replacement.Start();
    await Wait(state);
    File.WriteAllText(Path.Combine(state, $"mcp-live.{Guid.NewGuid():N}.{port}.{project:N}.{binding:N}.{workspace:N}.{Guid.Empty:N}.lease"), "");
    await Wait(state);
    Console.WriteLine("PASS: actual HTTP bind, unrelated port owner, two STDIO hosts, state isolation, graceful EOF, abrupt exit and stale lease.");
}
finally
{
    foreach (var process in children)
    {
        if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        process.Dispose();
    }
    // Windows may signal child process exit before every file object finishes its delete-on-close.
    for (int retry = 0; ; retry++)
    {
        try { root.Delete(recursive: true); break; }
        catch (IOException) when (retry < 10) { await Task.Delay(100); }
    }
}
