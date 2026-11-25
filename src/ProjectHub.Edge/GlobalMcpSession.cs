using System.Text.Json.Nodes;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ProjectHub.Edge;

// Each STDIO connection stays pinned to one client-supplied workspace.
internal sealed class GlobalMcpSession
{
    internal const string DiagnosticTool = "get_edge_status";
    private readonly EdgeSession session;
    private readonly string cwd;
    private readonly DeviceIdentity? device;
    private readonly LocalBinding? workspace;
    private readonly string? workspaceRelativeDirectory;
    private readonly string? initialError;
    private DeviceProject? firstGrant;

    private GlobalMcpSession(EdgeSession session, DeviceIdentity? device, string cwd, Guid? expectedWorkspace = null)
    {
        this.session = session;
        this.cwd = cwd;
        this.device = device;
        if (device == null) return;
        if (!Path.IsPathFullyQualified(cwd))
        {
            initialError = "客户端尚未提供完整的工作目录，请打开已绑定的目录并重新连接。";
            return;
        }
        try
        {
            workspace = session.MatchWorkspace(cwd);
            if (workspace != null && expectedWorkspace is { } id && workspace.WorkspaceId != id)
            {
                initialError = "此接入配置与当前目录的绑定不一致。请从 Edge 已绑定的项目新建聊天。";
                return;
            }
            if (workspace != null)
                workspaceRelativeDirectory = Path.GetRelativePath(WorkspacePath.Verified(workspace.LocalPath),
                    WorkspacePath.Verified(cwd)).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        { initialError = "当前目录无法唯一匹配，请在 Edge 核对目录绑定。"; }
    }

    private async Task<GlobalToolAccess> ResolveAsync(CancellationToken ct)
    {
        if (!session.IsActiveProfile) return GlobalToolAccess.Denied("changed", "当前 Core 已更换，请在新设备的绑定目录重新连接。");
        if (initialError != null) return GlobalToolAccess.Denied("ambiguous", initialError);
        if (device == null) return GlobalToolAccess.Denied("unpaired", "请先在 Edge 配对设备，再重新连接。");
        if (workspace == null) return GlobalToolAccess.Denied("unbound", "当前聊天的工作目录没有绑定，请在 Edge 绑定后重新连接。");
        try
        {
            if (session.GetIdentityOrDefault() != device || session.MatchWorkspace(cwd)?.WorkspaceId != workspace.WorkspaceId)
                return GlobalToolAccess.Denied("changed", "当前设备或目录绑定已变化，请重新连接。");
            var grant = (await session.ProjectsAsync(ct)).SingleOrDefault(p => p.ProjectId == workspace.ProjectId);
            if (grant == null) return GlobalToolAccess.Denied("unauthorized", "此目录对应的项目尚未授权或授权已撤销。");
            var original = Interlocked.CompareExchange(ref firstGrant, grant, null);
            if (original != null && grant.BindingId != original.BindingId)
                return GlobalToolAccess.Denied("changed", "项目授权身份已变化，请重新连接。");
            var current = workspace with { BindingId = grant.BindingId, Name = grant.Name, AccessMode = grant.AccessMode };
            var policy = session.GetWorkspacePolicy(current);
            var writable = grant.AccessMode == "readWrite";
            var client = new ProjectClient(session, grant.ProjectId, grant.BindingId, writable, workspace: workspace.WorkspaceId);
            var endpoints = await client.WorkspaceEndpointsAsync(current, ct);
            var endpoint = EdgeToolCatalog.MatchEndpoint(endpoints, policy?.EndpointId);
            var tools = EdgeToolCatalog.Tools.Where(t => t.IsAvailable(writable, endpoint.HasValue) &&
                (policy?.Tools == null || policy.Tools.Contains(t.Name, StringComparer.Ordinal))).Select(t => t.Name).ToArray();
            if (tools.Length == 0) return GlobalToolAccess.Denied("restricted", "当前目录策略与项目授权下没有可用的业务工具。");
            return new("ready", "当前目录已授权。", new(session, grant.ProjectId, grant.BindingId, writable, endpoint, workspace.WorkspaceId)
                { WorkspaceRelativeDirectory = workspaceRelativeDirectory }, tools);
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { return GlobalToolAccess.Denied("unauthorized", "Core 未授权当前设备或项目。"); }
        catch (Exception e) when (e is IOException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException
            || e is OperationCanceledException && !ct.IsCancellationRequested)
        { return GlobalToolAccess.Denied("unavailable", "暂时无法确认目录、项目或 Core 状态，请在 Edge 核对接入。"); }
    }

    private sealed record ClientRoot(string? Directory, string? Diagnostic = null, bool Conflicting = false)
    {
        internal bool ConflictsWith(string directory) => Conflicting ||
            Directory != null && !string.Equals(Directory, directory, StringComparison.Ordinal);
    }

    private static string? LocalRootDirectory(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc ||
            uri.Host.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var path = uri.LocalPath;
        if (OperatingSystem.IsLinux())
            return value.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && path.StartsWith('/') &&
                !path.StartsWith("//", StringComparison.Ordinal) && !path.Contains('\0')
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
        // Antigravity Desktop escapes the drive colon in file:///d%3A/…, which .NET reads as /d:/….
        if (path.Length >= 4 && path[0] == '/' &&
            value.StartsWith($"file:///{path[1]}%3A/", StringComparison.OrdinalIgnoreCase))
            path = path[1..];
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('/' or '\\'))
            return null;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(char.ToUpperInvariant(path[0]) + path[1..]));
    }

    private static bool IsClaudeCoworkUploadRoot(string directory)
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local) || !Path.IsPathFullyQualified(local)) return false;
        var path = Path.GetRelativePath(local, directory).Split(Path.DirectorySeparatorChar);
        static bool Is(string actual, string expected) => actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        static bool IsSessionId(string part) => part.Length == 8 && part.All(char.IsAsciiHexDigit);
        var profile = path switch
        {
            [var name, _, _, _, _, _] when Is(name, "Claude-3p") || Is(name, "Claude") => 1,
            [var name, var family, _, _, _, _, _] when Is(name, "Claude-Data") && Is(family, "claude_pzs8sxrjxfjjc") => 2,
            _ => 0
        };
        return profile != 0 && Is(path[profile], "local-agent-mode-sessions") &&
            Is(path[^1], "uploads") &&
            IsSessionId(path[profile + 1]) && IsSessionId(path[profile + 2]) && IsSessionId(path[profile + 3]);
    }

    internal static async Task RunAsync(EdgeSession session, string? workspaceVariable, bool useRoots, CancellationToken ct, Guid? expectedWorkspace = null)
    {
        var device = session.GetIdentityOrDefault();
        var cwd = workspaceVariable == null ? Environment.CurrentDirectory : Environment.GetEnvironmentVariable(workspaceVariable) ?? "";
        var cursorPlaceholder = workspaceVariable == "PROJECTHUB_WORKSPACE" && cwd == "${workspaceFolder}";
        var cursorCli = cursorPlaceholder && IsCursorCliParent()
            ? new GlobalMcpSession(session, device, Environment.CurrentDirectory) : null;
        var route = useRoots || cursorPlaceholder ? null : new GlobalMcpSession(session, device, cwd, expectedWorkspace);
        var changed = 0;
        Task<ClientRoot>? rootUpdate = null;
#pragma warning disable MCP9005
        async Task<ClientRoot> ReadRoot(McpServer server, CancellationToken token)
        {
            if (server.ClientCapabilities?.Roots == null)
                return new(null, "客户端没有声明支持提供工作目录。请从已绑定的文件夹重新连接。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var roots = await server.RequestRootsAsync(new(), timeout.Token);
            var cowork = OperatingSystem.IsWindows() &&
                server.ClientInfo?.Name.StartsWith("local-agent-mode-", StringComparison.Ordinal) == true;
            string? selected = null;
            foreach (var root in roots.Roots)
            {
                var directory = LocalRootDirectory(root.Uri);
                if (directory == null)
                    return new(null, "客户端提供的工作目录地址不受支持。请打开本机已绑定的文件夹。");
                // Cowork adds an internal uploads root when a task starts. Keep any explicitly bound root.
                if (cowork && IsClaudeCoworkUploadRoot(directory) && session.MatchWorkspace(directory) == null) continue;
                if (selected != null && !string.Equals(selected, directory, StringComparison.Ordinal))
                    return new(null, "客户端提供了多个不同的工作目录。请只打开要使用的文件夹后重新连接。", Conflicting: true);
                selected = directory;
            }
            return selected == null ? new(null, "客户端尚未提供工作目录。请打开一个已绑定的文件夹后重新连接。") : new(selected);
        }
        async Task<GlobalToolAccess> Access(McpServer server, bool call, CancellationToken token)
        {
            if (!useRoots && !cursorPlaceholder) return await route!.ResolveAsync(token);
            if (cursorPlaceholder && cursorCli != null && server.ClientInfo?.Name == "Cursor" && server.ClientCapabilities?.Roots == null)
                return await cursorCli.ResolveAsync(token);
            var update = Volatile.Read(ref rootUpdate);
            if (update != null && !update.IsCompletedSuccessfully)
                return GlobalToolAccess.Denied("unavailable", "客户端正在更新工作目录。");
            ClientRoot root;
            try
            {
                // Some clients synchronously refresh tools on notification and cannot answer a nested roots/list.
                root = !call && update != null ? update.Result : await ReadRoot(server, token);
            }
            catch (Exception e) when (e is McpException or IOException or InvalidOperationException or ArgumentException ||
                e is OperationCanceledException && !token.IsCancellationRequested)
            { return GlobalToolAccess.Denied("unavailable", "无法读取客户端工作目录，请重新连接。"); }
            if (update != Volatile.Read(ref rootUpdate)) return GlobalToolAccess.Denied("unavailable", "客户端正在更新工作目录。");
            var pinned = Volatile.Read(ref route);
            if (pinned != null && root.ConflictsWith(pinned.cwd)) Interlocked.Exchange(ref changed, 1);
            if (Volatile.Read(ref changed) != 0) return GlobalToolAccess.Denied("changed", "聊天的工作目录已变化，请重新连接。");
            if (root.Directory == null) return GlobalToolAccess.Denied("unbound", root.Diagnostic!);
            if (pinned == null)
            {
                var first = new GlobalMcpSession(session, device, root.Directory);
                if (first.workspace == null) return await first.ResolveAsync(token);
                pinned = Interlocked.CompareExchange(ref route, first, null) ?? first;
                if (root.ConflictsWith(pinned.cwd)) Interlocked.Exchange(ref changed, 1);
            }
            if (Volatile.Read(ref changed) != 0) return GlobalToolAccess.Denied("changed", "聊天的工作目录已变化，请重新连接。");
            if (call && update is { Result: { Directory: null, Conflicting: false } })
            {
                var confirmed = Task.FromResult(root);
                if (Interlocked.CompareExchange(ref rootUpdate, confirmed, update) != update)
                    return GlobalToolAccess.Denied("unavailable", "客户端正在更新工作目录。");
                update = confirmed;
            }
            var access = await pinned.ResolveAsync(token);
            return update == Volatile.Read(ref rootUpdate) && Volatile.Read(ref changed) == 0 ? access :
                GlobalToolAccess.Denied("changed", "客户端工作目录正在变化，请重新确认。");
        }
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddLogging();
        builder.Services.AddScoped<GlobalToolCall>();
        builder.Services.AddScoped(sp => sp.GetRequiredService<GlobalToolCall>().Access!.Client!);
        builder.Services.AddMcpServer(options =>
            {
                McpHost.SetServerInfo(options);
                options.ScopeRequests = true;
                if (useRoots || (cursorPlaceholder && cursorCli == null))
                {
                    // The client must be able to answer roots/list outside a tool request when its workspace changes.
                    options.ProtocolVersion = "2025-11-25";
                    options.Capabilities = new() { Tools = new() { ListChanged = true } };
                }
            }).WithStdioServerTransport()
            .WithTools<ProjectTools>(McpHost.ToolOptions).WithTools<GlobalDiagnostics>(McpHost.ToolOptions).WithRequestFilters(filters =>
            {
                filters.AddListToolsFilter(next => async (request, token) =>
                {
                    using var source = McpHost.UseClientSource(request.Server);
                    var scope = await Access(request.Server, false, token);
                    var catalog = await next(request, token);
                    return new ListToolsResult { Tools = catalog.Tools.Where(t => scope.Tools.Contains(t.Name)).ToList() };
                });
                filters.AddCallToolFilter(next => async (request, token) =>
                {
                    using var source = McpHost.UseClientSource(request.Server);
                    var scope = await Access(request.Server, true, token);
                    if (!scope.Tools.Contains(request.Params?.Name))
                        return new CallToolResult { IsError = true, Content = [new TextContentBlock
                            { Text = scope.Status == "ready" ? "此工具不在当前工作目录的有效授权范围内。" : scope.Message }] };
                    request.Services!.GetRequiredService<GlobalToolCall>().Access = scope;
                    return await next(request, token);
                });
            });
        using var host = builder.Build();
        var server = host.Services.GetRequiredService<McpServer>();
        await using var notifications = useRoots || cursorPlaceholder ? server.RegisterNotificationHandler(NotificationMethods.RootsListChangedNotification,
            async (_, token) =>
            {
                if (server.ClientCapabilities?.Roots == null) return;
                var refresh = new TaskCompletionSource<ClientRoot>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref rootUpdate, refresh.Task);
                ClientRoot root;
                try { root = await ReadRoot(server, token); }
                catch (Exception e) when (e is McpException or IOException or ArgumentException or InvalidOperationException ||
                    e is OperationCanceledException && !token.IsCancellationRequested)
                { root = new(null, "无法读取客户端工作目录，请重新连接。"); }
                if (Volatile.Read(ref rootUpdate) != refresh.Task) return;
                if (Volatile.Read(ref route) is { } pinned && root.ConflictsWith(pinned.cwd))
                    Interlocked.Exchange(ref changed, 1);
                refresh.SetResult(root);
                await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, token);
            }) : null;
#pragma warning restore MCP9005
        await host.StartAsync(ct);
        using var lease = new McpListenerStatus(null, route?.workspace?.ProjectId ?? Guid.Empty, Guid.Empty,
            route?.workspace?.WorkspaceId, device?.DeviceId).Register(session.StateDirectory);
        await host.WaitForShutdownAsync(ct);
    }

    // Cursor CLI's bundled Node launches the server directly. Its editor uses the same MCP client name.
    private static bool IsCursorCliParent()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var self = Process.GetCurrentProcess();
            var info = new nint[6];
            if (NtQueryInformationProcess(self.Handle, 0, info, info.Length * IntPtr.Size, out _) != 0) return false;
            using var parent = Process.GetProcessById(checked((int)info[5]));
            var exe = parent.MainModule?.FileName;
            if (parent.StartTime > self.StartTime || !string.Equals(Path.GetFileName(exe), "node.exe", StringComparison.OrdinalIgnoreCase))
                return false;
            using var manifest = File.OpenRead(Path.Combine(Path.GetDirectoryName(exe!)!, "package.json"));
            using var json = JsonDocument.Parse(manifest);
            return json.RootElement.TryGetProperty("name", out var name) && name.GetString() == "@anysphere/agent-cli-runtime";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or IOException or
            UnauthorizedAccessException or JsonException or OverflowException) { return false; }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int informationClass, [Out] nint[] information,
        int length, out int returnedLength);
}

internal sealed record GlobalToolAccess(string Status, string Message, ProjectClient? Client, string[] Tools)
{
    internal static GlobalToolAccess Denied(string status, string message) => new(status, message, null, [GlobalMcpSession.DiagnosticTool]);
}

internal sealed class GlobalToolCall
{
    public GlobalToolAccess? Access { get; set; }
}

[McpServerToolType]
internal sealed class GlobalDiagnostics
{
    [McpServerTool(Name = GlobalMcpSession.DiagnosticTool, ReadOnly = true), Description("查看当前聊天工作目录的脉络 Edge 接入状态。")]
    public static object Status(GlobalToolCall call) => new JsonObject { ["status"] = call.Access!.Status, ["message"] = call.Access.Message };
}
