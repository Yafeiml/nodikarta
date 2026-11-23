using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace ProjectHub.Edge;

internal static class Program
{
    private const string Usage = """
        Nodicarta Edge for Windows
          pair --core URL [--label NAME] [--replace]   从终端或标准输入读取一次性配对码
          projects                                    读取当前可见项目
          bind --project UUID --path DIRECTORY        将本地目录关联到获准项目
          unbind --project UUID --workspace UUID --path DIRECTORY  解除指定本机目录的绑定
          bindings [--local]                          读取当前有效绑定，--local 离线列出本机目录记录
          status                                      查询配对和在线状态
          migrate-default-state                       迁移旧默认状态目录并输出当前实体路径
          run                                         在前台保持连接
          codex-link --project ID --path DIR --codex EXE --thread ID --name NAME
          register-endpoint --project ID --binding ID --name NAME    登记人工领取端点
          dispatch [--watch]                          执行派工或持续检查
          mcp-stdio-global [--workspace-roots | --workspace-env CLAUDE_PROJECT_DIR|PROJECTHUB_WORKSPACE | --bound-workspace UUID]    通过客户端工作目录匹配本机绑定
          mcp-stdio --project ID --binding ID [--workspace ID] [--mode readOnly|readWrite|auto] [--endpoint ID] [--tools NAME,NAME]
          mcp-http --project ID --binding ID --port PORT [--workspace ID] [--mode readOnly|readWrite|auto] [--endpoint ID] [--tools NAME,NAME]
          auto 需要 --workspace，每次启动时按实时授权和端点选择工具。
          sync --project ID --binding ID --mode readWrite    重发本地待同步内容
        可用 --state DIRECTORY 或 NODICARTA_EDGE_STATE 指定专用数据目录。兼容 PROJECTHUB_EDGE_STATE。
        """;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help") { Console.WriteLine(Usage); return 0; }
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            var values = args.ToList();
            var directory = Option(values, "--state") ?? Environment.GetEnvironmentVariable("NODICARTA_EDGE_STATE")
                ?? Environment.GetEnvironmentVariable("PROJECTHUB_EDGE_STATE") ?? EdgeStatePaths.MachineDefault;
            var command = values.FirstOrDefault();
            if (command == "migrate-default-state")
            {
                NoOptions(values.Skip(1).ToList());
                Console.WriteLine(EdgeStatePaths.Resolve(directory));
                return 0;
            }
            using var signal = command == "dispatch" && values.Contains("--watch") &&
                Environment.GetEnvironmentVariable("PROJECTHUB_DISPATCH_STOP_FILE") is { Length: > 0 } stopFile
                ? new Timer(_ => { if (File.Exists(stopFile)) stop.Cancel(); }, null, 1000, 1000) : null;
            if (command == "service")
            {
                using var service = new EdgeWindowsService(directory);
                ServiceBase.Run(service);
                return 0;
            }
            // Project- and plugin-specific registrations written before profiles existed belong to the original identity.
            var scoped = command is "mcp-stdio" or "mcp-http" || command == "mcp-stdio-global" && values.Contains("--bound-workspace");
            var entry = command switch
            {
                "mcp-stdio" or "mcp-http" or "mcp-stdio-global" => EdgeRequestSource.ForMcp(null, null),
                "dispatch" when values.Contains("--watch") => EdgeRequestSource.Background,
                _ => EdgeRequestSource.Cli
            };
            var session = EdgeSession.Open(directory, pinLegacyProfile: scoped, source: entry);
            var options = values.Skip(1).ToList();
            switch (command)
            {
                case "pair":
                    var core = RequiredOption(options, "--core");
                    var label = Option(options, "--label") ?? Environment.MachineName;
                    var replace = options.Remove("--replace");
                    NoOptions(options);
                    var device = await session.PairAsync(new Uri(core), ReadCode(), label, replace, stop.Token);
                    Print(device);
                    return 0;
                case "projects":
                    NoOptions(options); Print(await session.ProjectsAsync(stop.Token)); return 0;
                case "bind":
                    var project = Guid.Parse(RequiredOption(options, "--project"));
                    var path = RequiredOption(options, "--path");
                    NoOptions(options);
                    Print(await session.BindAsync(project, path, stop.Token));
                    return 0;
                case "unbind":
                    var projectId = Guid.Parse(RequiredOption(options, "--project"));
                    var workspaceId = Guid.Parse(RequiredOption(options, "--workspace"));
                    var directoryPath = RequiredOption(options, "--path");
                    NoOptions(options);
                    session.Unbind(projectId, workspaceId, directoryPath);
                    Print(new { projectId, workspaceId, removed = true });
                    return 0;
                case "bindings":
                    var local = options.Remove("--local");
                    NoOptions(options);
                    if (local) Print(session.EnumerateLocalWorkspaces().Select(row => new { row.ProjectId, row.WorkspaceId, row.LocalPath }));
                    else Print(await session.BindingsAsync(stop.Token));
                    return 0;
                case "status":
                    NoOptions(options);
                    var identity = session.GetIdentity();
                    using (var res = await session.SendAsync(HttpMethod.Get, "/v1/projects", ct: stop.Token))
                    {
                        Print(new { identity.DeviceId, identity.EdgeId, identity.CoreUrl, online = res.IsSuccessStatusCode, http = (int)res.StatusCode });
                        return res.IsSuccessStatusCode ? 0 : 1;
                    }
                case "run":
                    NoOptions(options);
                    await KeepConnectedAsync(session, stop.Token);
                    return 0;
                default:
                    var extra = await MvpCommands.RunAsync(values.ToArray(), session, stop.Token);
                    if (extra.HasValue) return extra.Value;
                    throw new ArgumentException("未知命令。运行 --help 查看用法。");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));

    private static string? Option(List<string> options, string name)
    {
        var i = options.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 == options.Count || options[i + 1].StartsWith("--")) throw new ArgumentException($"{name} 缺少参数值。");
        var result = options[i + 1];
        options.RemoveRange(i, 2);
        return result;
    }

    private static string RequiredOption(List<string> options, string name) => Option(options, name)
        ?? throw new ArgumentException($"需要提供 {name}。");

    private static void NoOptions(List<string> rest)
    {
        if (rest.Count != 0) throw new ArgumentException("命令包含未知或重复参数。");
    }

    private static string ReadCode()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        Console.Error.Write("一次性配对码（输入隐藏）：");
        var code = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return code.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (code.Length > 0) code.Length--; }
            else if (!char.IsControl(key.KeyChar) && code.Length < 256) code.Append(key.KeyChar);
        }
    }

    internal static async Task KeepConnectedAsync(EdgeSession session, CancellationToken ct)
    {
        using var background = EdgeRequestSource.Background.Use();
        // A desktop window, CLI and service sharing this state need only one heartbeat and outbox worker.
        using var worker = await AcquireWorkerAsync(session.RootDirectory, ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            session = session.CurrentProfile();
            try { await session.ResumeReplacementAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidOperationException
                or IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                or System.Security.Cryptography.CryptographicException or Microsoft.Data.Sqlite.SqliteException)
            {
                // The active identity still serves its own Core while a handover awaits a retry.
                System.Diagnostics.Trace.TraceWarning("Device update: {0}", e.GetType().Name);
            }
            session = session.CurrentProfile();
            var state = "online";
            try
            {
                using var response = await session.SendAsync(HttpMethod.Get, "/v1/projects", ct: ct);
                if (!response.IsSuccessStatusCode) state = response.StatusCode == HttpStatusCode.Unauthorized ? "unauthorized" : "offline";
                else await ProjectClient.SyncPendingAsync(session, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (InvalidOperationException) { state = "pairing_required"; }
            catch (HttpRequestException) { state = "offline"; }
            catch (OperationCanceledException) { state = "offline"; }
            var output = Path.Combine(session.StateDirectory, "connection.json");
            var staging = output + "." + Environment.ProcessId + ".tmp";
            await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(new { checkedAt = DateTimeOffset.UtcNow, state }), ct);
            File.Move(staging, output, true);
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private static async Task<FileStream> AcquireWorkerAsync(string stateDirectory, CancellationToken ct)
    {
        var path = Path.Combine(stateDirectory, "connection.worker.lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        }
    }
}

internal sealed class EdgeWindowsService : ServiceBase
{
    private readonly string directory;
    private readonly CancellationTokenSource stop = new();
    private Task? worker;

    public EdgeWindowsService(string directory)
    {
        this.directory = directory;
        ServiceName = "ProjectHubEdge";
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        var session = EdgeSession.Open(directory);
        worker = Task.Run(async () =>
        {
            try { await Program.KeepConnectedAsync(session, stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch { ExitCode = 1; Stop(); }
        });
    }

    protected override void OnStop()
    {
        stop.Cancel();
        if (ExitCode == 0) worker?.Wait(TimeSpan.FromSeconds(10));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) stop.Dispose();
        base.Dispose(disposing);
    }
}
