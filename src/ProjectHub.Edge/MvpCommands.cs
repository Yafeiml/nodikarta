using System.Text.Json;

namespace ProjectHub.Edge;

internal static class MvpCommands
{
    internal static async Task<int?> RunAsync(string[] args, EdgeSession session, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is not ("mcp-stdio" or "mcp-stdio-global" or "mcp-http" or "sync" or "codex-link" or "register-endpoint" or "dispatch")) return null;
        var options = args.Skip(1).ToList();
        string Take(string key)
        {
            var index = options.IndexOf(key);
            if (index < 0 || index + 1 >= options.Count || options[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("缺少参数 " + key);
            var value = options[index + 1];
            options.RemoveRange(index, 2);
            return value;
        }
        void Check()
        {
            if (options.Count != 0) throw new ArgumentException("有未识别的参数。");
        }
        if (args[0] == "mcp-stdio-global")
        {
            var workspaceVariable = options.Contains("--workspace-env") ? Take("--workspace-env") : null;
            var useRoots = options.Remove("--workspace-roots");
            Guid? boundWorkspace = options.Contains("--bound-workspace") ? Guid.Parse(Take("--bound-workspace")) : null;
            if (workspaceVariable is not (null or "PROJECTHUB_WORKSPACE" or "CLAUDE_PROJECT_DIR"))
                throw new ArgumentException("未知的工作目录环境变量。");
            if (useRoots && workspaceVariable != null) throw new ArgumentException("请只指定一个工作目录来源。");
            if (boundWorkspace.HasValue && (useRoots || workspaceVariable != null))
                throw new ArgumentException("绑定校验需要客户端的实际工作目录。");
            Check();
            await GlobalMcpSession.RunAsync(session, workspaceVariable, useRoots, ct, boundWorkspace);
            return 0;
        }
        if (args[0] == "dispatch")
        {
            var watch = options.Remove("--watch");
            Check();
            var dispatcher = new MvpDispatch(session);
            do
            {
                await dispatcher.TickAsync(ct);
                if (watch) await Task.Delay(TimeSpan.FromSeconds(5), ct);
            } while (watch);
            return 0;
        }
        var projectId = Guid.Parse(Take("--project"));
        if (args[0] == "codex-link")
        {
            var path = Take("--path");
            var executable = Take("--codex");
            var thread = Guid.Parse(Take("--thread"));
            var name = Take("--name");
            Check();
            var link = await new MvpDispatch(session).LinkAsync(projectId, path, executable, thread, name, ct);
            Console.WriteLine(JsonSerializer.Serialize(link));
            return 0;
        }
        var bindingId = Guid.Parse(Take("--binding"));
        if (args[0] == "register-endpoint")
        {
            var name = Take("--name");
            Check();
            var registered = await new ProjectClient(session, projectId, bindingId, true)
                .WriteAsync("endpoints", new { requestId = Guid.NewGuid(), name }, ct);
            Console.WriteLine(registered.GetRawText());
            return 0;
        }
        var mode = options.Contains("--mode") ? Take("--mode") : "readOnly";
        if (mode is not ("readOnly" or "readWrite" or "auto") || args[0] == "sync" && mode == "auto")
            throw new ArgumentException("MCP mode 需要为 readOnly、readWrite 或 auto。");
        Guid? endpoint = options.Contains("--endpoint") ? Guid.Parse(Take("--endpoint")) : null;
        Guid? workspace = args[0] != "sync" && options.Contains("--workspace") ? Guid.Parse(Take("--workspace")) : null;
        int? port = args[0] == "mcp-http" ? int.Parse(Take("--port")) : null;
        var selection = args[0] != "sync" && options.Contains("--tools") ? Take("--tools") : null;
        Check();
        if (workspace == null && args[0] != "sync" && mode != "auto")
        {
            // Older launch configurations omitted the workspace ID. Pin an identifiable directory for this process.
            var local = session.EnumerateLocalWorkspaces().Where(row => row.ProjectId == projectId).ToArray();
            if (local.Length == 1) workspace = local[0].WorkspaceId;
            else if (local.Length > 1 && session.MatchWorkspace(Environment.CurrentDirectory) is { } current && current.ProjectId == projectId)
                workspace = current.WorkspaceId;
        }
        var client = new ProjectClient(session, projectId, bindingId, mode != "readOnly", endpoint, workspace);
        if (mode == "auto")
        {
            if (workspace is null || selection != null) throw new ArgumentException("自动匹配需要 --workspace，并由 Edge 选择工具。");
            var directory = await client.CurrentAsync(ct);
            var endpoints = await client.WorkspaceEndpointsAsync(directory, ct);
            endpoint = EdgeToolCatalog.MatchEndpoint(endpoints, endpoint);
            var writable = directory.AccessMode == "readWrite";
            selection = string.Join(',', EdgeToolCatalog.Tools.Where(t => t.IsAvailable(writable, endpoint.HasValue)).Select(t => t.Name));
            client = new ProjectClient(session, projectId, bindingId, writable, endpoint, workspace);
        }
        if (args[0] == "sync") await client.SyncAsync(ct);
        else
        {
            var listener = new McpListenerStatus(port, projectId, bindingId, workspace, session.GetIdentityOrDefault()?.DeviceId);
            await McpHost.RunAsync(client, listener, session.StateDirectory, selection, ct);
        }
        return 0;
    }
}
