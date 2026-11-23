using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace ProjectHub.Edge;

internal sealed record EdgeAccessEndpoint(Guid Id, string Name);

internal sealed record EdgeToolInfo(string Name, string Description, string Category, bool RequiresWrite, bool RequiresEndpoint)
{
    internal bool IsAvailable(bool writable, bool hasEndpoint) =>
        (!RequiresWrite || writable) && (!RequiresEndpoint || hasEndpoint);
}

internal static class EdgeToolCatalog
{
    internal static Guid? MatchEndpoint(IReadOnlyList<EdgeAccessEndpoint> endpoints, Guid? preferred)
    {
        if (preferred.HasValue) return endpoints.Any(e => e.Id == preferred) ? preferred : null;
        return endpoints.Count == 1 ? endpoints[0].Id : null;
    }

    private const string Knowledge = "项目与知识";
    private const string Feedback = "反馈";
    private const string Events = "事件";
    private const string Tasks = "任务";
    private const string Agent = "Agent";
    private const string Assets = "共享素材";

    internal static IReadOnlyList<string> Categories { get; } = [Knowledge, Assets, Feedback, Events, Tasks, Agent];

    internal static IReadOnlyList<EdgeToolInfo> Tools { get; } = typeof(ProjectTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.IsDefined(typeof(McpServerToolAttribute)))
        .Select(Describe)
        .OrderBy(tool => tool.Name, StringComparer.Ordinal)
        .ToArray();

    private static EdgeToolInfo Describe(MethodInfo method)
    {
        var (category, write, endpoint) = method.Name switch
        {
            nameof(ProjectTools.Projects) or nameof(ProjectTools.Snapshot) or nameof(ProjectTools.Search)
                or nameof(ProjectTools.History) => (Knowledge, false, false),
            nameof(ProjectTools.Commit) => (Knowledge, true, false),
            nameof(ProjectTools.PublishAsset) => (Assets, true, false),
            nameof(ProjectTools.ListAssets) or nameof(ProjectTools.GetAsset) or nameof(ProjectTools.GetAssetDecisions)
                or nameof(ProjectTools.DownloadAsset) => (Assets, false, false),
            nameof(ProjectTools.FeedbackList) or nameof(ProjectTools.GetFeedback) => (Feedback, false, false),
            nameof(ProjectTools.Feedback) => (Feedback, true, false),
            nameof(ProjectTools.Tasks) => (Tasks, false, true),
            nameof(ProjectTools.Claim) or nameof(ProjectTools.Result) => (Tasks, true, true),
            nameof(ProjectTools.Endpoints) => (Agent, false, false),
            _ => throw new InvalidOperationException($"请为 MCP 工具 {method.Name} 标注分类和调用条件。")
        };
        var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name
            ?? throw new InvalidOperationException($"MCP 工具 {method.Name} 缺少显式注册名称。");
        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? throw new InvalidOperationException($"MCP 工具 {name} 缺少说明。");
        return new EdgeToolInfo(name, description, category, write, endpoint);
    }
}
