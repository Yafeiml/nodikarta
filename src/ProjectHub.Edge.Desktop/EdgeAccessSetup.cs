using System.Text.Json.Nodes;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ProjectHub.Edge.Desktop;

internal enum EdgeAccessProfile { Auto, FollowGrant, Knowledge, Feedback, Tasks, Assets, All }
internal sealed record EdgeAccessContext(DeviceProject Project, LocalBinding Binding, IReadOnlyList<EdgeAccessEndpoint> Endpoints,
    string CodexStatus, string[]? AutomaticTools = null, Guid? ConfiguredEndpoint = null)
{
    public bool AutoUsesEndpoints => AutomaticTools is null || EdgeToolCatalog.Tools.Any(t => t.RequiresEndpoint && AutomaticTools.Contains(t.Name));
    public Guid? SuggestedEndpoint => AutoUsesEndpoints ? EdgeToolCatalog.MatchEndpoint(Endpoints, ConfiguredEndpoint) : null;
    public string AutoExplanation => AutomaticTools switch
    {
        { Length: 0 } => "现有配置需手动核对，可另选工具范围。",
        { } tools => UiLanguage.Format("沿用此目录已选的 {0} 项工具，并核对当前项目授权。", tools.Length),
        _ => "根据当前项目授权和本机目录的任务端点匹配工具。"
    };
}
internal sealed record EdgeAccessConfig(string Command, string[] Arguments, string[] Tools, string StdioJson, string CodexToml);

internal sealed class EdgeAccessSetup(EdgeDesktopClient client)
{
    internal const string ServerName = "nodicarta";
    internal const string LegacyServerName = "projecthub";
    private readonly EdgeSession session = client.Session;
    private static string StdioCommand => OperatingSystem.IsLinux()
        ? Path.Combine(AppContext.BaseDirectory, "ProjectHub.Edge")
        : GlobalCodexAccess.CurrentCommand;

    internal static string[] GlobalArguments(string state) => ["mcp-stdio-global", "--state", state];
    internal static string StdioJson(string command, IEnumerable<string> args)
    {
        var server = new JsonObject
        {
            ["command"] = command,
            ["args"] = JsonSerializer.SerializeToNode(args.ToArray(), DesktopJson.Default.StringArray)
        };
        var servers = new JsonObject { [ServerName] = server };
        return new JsonObject { ["mcpServers"] = servers }.ToJsonString(DesktopJson.Display.Options);
    }
    internal string GlobalStdioJson() => StdioJson(StdioCommand, GlobalArguments(client.StateDirectory));

    internal string GlobalSetupInstructions() => UiLanguage.Text(
        $"""
        请在当前客户端的全局 MCP 设置中添加下面的 {ServerName}。将它配置为通过标准输入和输出通信的本地进程。使用客户端支持的添加命令或配置格式，原样保留 command 和 args。先检查现有接入。如果已有条目使用同一命令和参数，就沿用该条目。若同名条目使用不同配置，请先告诉我。保留其他配置。

        如果无法修改本机配置，请告诉我应该打开哪个设置页面，以及如何填入下面的值。配置后请尝试重新加载 MCP 并检查工具列表。如果需要我重启客户端，或者需要在 Edge 中绑定当前目录，请告诉我。

        在已绑定的目录中，用可用的 nodicarta-context 或项目工具回查旧稿、依据和待办。用户确认选稿、批准下一步、采用决定或取得新凭据时，即使只要两句晨会摘要，也在简短回复前用可用的 nodicarta-sync 或项目工具核对已有记录，只保存获准共享的新变化。仅润色来稿、假设示例、仅本地、只读或不要记录的工作不写入共享项目。对外只使用适用于该受众的已批准口径。验证接入时只检查工具列表。
        """,
        $"""
        Register {ServerName} below in your client's global MCP settings as a local process using standard input and output. Use the client's supported MCP command or configuration format and preserve command and args exactly. Check existing entries first. Reuse any entry with the same command and arguments. If the same name already has different settings, ask me first. Keep other settings.

        If you cannot change the local configuration, tell me which settings page to open and how to enter the values below. After configuring it, try reloading MCP and listing the tools. Tell me if I need to restart the client or link the current folder in Edge.

        In a linked folder, use nodicarta-context when available or the exposed project tools to look up prior drafts, evidence, and next steps. When the user confirms a draft, next step, decision, or new evidence, check existing knowledge with nodicarta-sync or the exposed project tools before the brief reply, even if they only ask for a stand-up summary. Save only authorized changes. Skip shared writes for wording-only edits, hypothetical examples, or read-only, local-only, and do-not-record work. Use only information approved for the external audience. When testing setup, only list the tools.
        """) + "\n\n```json\n" + GlobalStdioJson() + "\n```";

    public async Task<EdgeAccessContext> LoadAsync(DeviceProject project, LocalBinding binding, CancellationToken ct = default)
    {
        var snapshot = await client.RefreshAsync(ct);
        var current = snapshot.Projects.SingleOrDefault(p => p.ProjectId == project.ProjectId && p.BindingId == project.BindingId)
            ?? throw new InvalidOperationException("此项目的设备授权已更改，请刷新主界面。");
        var pathCase = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var directory = snapshot.Bindings.SingleOrDefault(b => b.ProjectId == current.ProjectId && b.BindingId == current.BindingId
            && b.WorkspaceId == binding.WorkspaceId && string.Equals(b.LocalPath, binding.LocalPath, pathCase))
            ?? throw new InvalidOperationException("当前目录的项目绑定已更改，请刷新主界面。");
        if (!Directory.Exists(directory.LocalPath)) throw new DirectoryNotFoundException("绑定的本机目录已移动或无法访问。");

        var api = new ProjectClient(session, current.ProjectId, current.BindingId, false, workspace: directory.WorkspaceId);
        var endpoints = await api.WorkspaceEndpointsAsync(directory, ct);
        if (client.GetIdentity() != snapshot.Identity) throw new InvalidOperationException("设备身份已更改，请刷新主界面。");
        var policy = session.GetWorkspacePolicy(directory);
        if (policy != null)
        {
            var description = policy.Tools switch
            {
                null => "跟随当前项目授权",
                { Length: 0 } => "目录工具已暂停，请核对自定义配置",
                _ => $"此目录已限制为 {policy.Tools.Length} 项工具"
            };
            return new(current, directory, endpoints, description, policy.Tools, policy.EndpointId);
        }
        if (!OperatingSystem.IsWindows()) return new(current, directory, endpoints, "");
        var existing = CodexAccessFile.Read(directory, session.StateDirectory);
        return new(current, directory, endpoints, existing.Label, existing.Block == "" ? [] : existing.ToolLimit, existing.Endpoint);
    }

    public EdgeAccessConfig Build(EdgeAccessContext context, EdgeAccessProfile profile, Guid? endpointId = null)
    {
        var command = StdioCommand;
        if (!File.Exists(command))
            throw new FileNotFoundException("找不到桌面版随附的 Edge 命令行程序，请核对安装目录。", command);
        session.EnsureActiveProfile();
        return CreateConfig(context, profile, endpointId, command, session.StateDirectory);
    }

    internal static EdgeAccessConfig CreateConfig(EdgeAccessContext context, EdgeAccessProfile profile, Guid? endpointId,
        string command, string stateDirectory)
    {
        var automatic = profile == EdgeAccessProfile.FollowGrant || profile == EdgeAccessProfile.Auto && context.AutomaticTools is null;
        var suggested = profile == EdgeAccessProfile.FollowGrant
            ? EdgeToolCatalog.MatchEndpoint(context.Endpoints, context.ConfiguredEndpoint) : context.SuggestedEndpoint;
        var pinnedEndpoint = endpointId == null || endpointId == suggested ? context.ConfiguredEndpoint : endpointId;
        if (profile is EdgeAccessProfile.Auto or EdgeAccessProfile.FollowGrant)
        {
            if (!automatic && context.AutomaticTools is { Length: 0 }) throw new InvalidOperationException(context.AutoExplanation);
            endpointId ??= suggested;
        }
        if (profile is EdgeAccessProfile.Tasks && endpointId is null)
            throw new InvalidOperationException("请先为当前目录绑定一个接收任务的端点，再选择任务处理。");
        if (endpointId.HasValue && !context.Endpoints.Any(e => e.Id == endpointId))
            throw new InvalidOperationException("此端点已失效或属于其他目录，请重新选择。");
        var writable = context.Project.AccessMode == "readWrite" && context.Binding.AccessMode == "readWrite";
        var tools = EdgeToolCatalog.Tools.Where(t => t.IsAvailable(writable, endpointId.HasValue) && (profile switch
        {
            EdgeAccessProfile.Auto => context.AutomaticTools is null || context.AutomaticTools.Contains(t.Name),
            EdgeAccessProfile.Knowledge => t.Category == "项目与知识",
            EdgeAccessProfile.Assets => t.Category == "共享素材" || t.Name == "list_projects",
            EdgeAccessProfile.Feedback => t.Category == "反馈" || t.Name is "list_projects" or "list_agent_endpoints",
            EdgeAccessProfile.Tasks => t.RequiresEndpoint || t.Name is "list_projects" or "list_agent_endpoints",
            EdgeAccessProfile.FollowGrant or EdgeAccessProfile.All => true,
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        })).ToArray();
        string[] names = tools.Select(t => t.Name).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("当前授权范围没有可用的工具。");
        List<string> args = ["mcp-stdio", "--state", stateDirectory, "--project", context.Project.ProjectId.ToString(),
            "--binding", context.Binding.BindingId.ToString(), "--mode", automatic ? "auto" : tools.Any(t => t.RequiresWrite) ? "readWrite" : "readOnly"];
        var configuredEndpoint = automatic ? pinnedEndpoint : tools.Any(t => t.RequiresEndpoint) ? endpointId : null;
        if (configuredEndpoint.HasValue) args.AddRange(["--endpoint", configuredEndpoint.Value.ToString()]);
        args.AddRange(["--workspace", context.Binding.WorkspaceId.ToString()]);
        if (!automatic) args.AddRange(["--tools", string.Join(',', names)]);
        var json = StdioJson(command, args);
        var toml = CodexAccessFile.Format(command, args, names, automatic);
        return new(command, [..args], names, json, toml);
    }

    public async Task<string> SavePolicyAsync(EdgeAccessContext context, EdgeAccessProfile profile, Guid? endpointId = null,
        CancellationToken ct = default)
    {
        var fresh = await LoadAsync(context.Project, context.Binding, ct);
        if (endpointId.HasValue && !fresh.Endpoints.Any(e => e.Id == endpointId.Value))
            throw new InvalidOperationException("接收端点已失效，请重新选择。");
        var config = Build(fresh with { ConfiguredEndpoint = null }, profile, endpointId);
        var limit = profile switch
        {
            EdgeAccessProfile.FollowGrant => null,
            EdgeAccessProfile.Auto => fresh.AutomaticTools,
            _ => config.Tools
        };
        ct.ThrowIfCancellationRequested();
        session.SetWorkspacePolicy(fresh.Binding, new WorkspaceAccessPolicy(limit, endpointId));
        return "已保存此目录的工具范围和接收端点。";
    }

    public async Task<ViewMessage> ProbeAsync(EdgeAccessContext context, EdgeAccessProfile profile, Guid? endpointId = null,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var fresh = await LoadAsync(context.Project, context.Binding, timeout.Token);
            var config = Build(fresh with { ConfiguredEndpoint = context.ConfiguredEndpoint }, profile, endpointId);
            var transport = new StdioClientTransport(new StdioClientTransportOptions { Name = "Nodicarta Edge",
                Command = config.Command, Arguments = config.Arguments, WorkingDirectory = fresh.Binding.LocalPath });
            await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            var tools = await mcp.ListToolsAsync(cancellationToken: timeout.Token);
            var count = tools.Count;
            if (!config.Tools.ToHashSet(StringComparer.Ordinal).SetEquals(tools.Select(t => t.Name)))
                throw new InvalidOperationException("Edge 实际返回的工具与所选配置不一致，请核对桌面版安装。");
            if (!config.Tools.Contains("list_projects"))
                return new(() => UiLanguage.Format("已连接，实际发现 {0} 个工具；当前范围未开放项目列表。", count));
            var result = await mcp.CallToolAsync("list_projects", cancellationToken: timeout.Token);
            if (result.IsError == true) throw new InvalidOperationException("MCP 已连接，但项目授权检查失败。");
            var content = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            if (string.IsNullOrEmpty(content)) throw new InvalidOperationException("MCP 已连接，但没有返回项目信息。");
            using var info = JsonDocument.Parse(content);
            var items = info.RootElement.GetProperty("items");
            if (items.GetArrayLength() != 1 || !items[0].EnumerateObject().Any(p =>
                    p.Name.Equals("projectId", StringComparison.OrdinalIgnoreCase) && p.Value.GetGuid() == fresh.Project.ProjectId))
                throw new InvalidOperationException("MCP 返回的项目与所选项目不一致。");
            var projectName = fresh.Project.Name;
            return new(() => UiLanguage.Format("已连接「{0}」，实际发现 {1} 个工具，list_projects 已确认当前项目。", projectName, count));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("MCP 连接检查超时，请核对 Core 和 Edge 状态后重试。"); }
    }
}
