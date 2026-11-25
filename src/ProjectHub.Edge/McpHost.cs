using System.Text.Json.Nodes;
using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ProjectHub.Edge;

internal static class McpHost
{
    internal static readonly JsonSerializerOptions ToolOptions = EdgeJson.CreateToolOptions();

    internal const string ProjectMemoryInstructions = """
        For a bound Nodicarta (脉络) workspace, recall earlier work by its evidence. Use nodicarta-context when available to check the relevant revision, open next steps, and pinned asset versions. For customer-facing answers, use only information approved for that audience and channel.

        When the current work selects a reusable draft, approves a next step, adopts or withdraws a scoped decision, or obtains new verification or release evidence, check before replying. Use nodicarta-sync when available, or the exposed project tools, to compare with existing knowledge. Read the existing item before revising it and save only a meaningful change that the request and workspace authorize. Record what was chosen, why, what was approved, and what remains open. Keep internal plans, proposed drafts, adopted decisions, and public releases distinct.

        When the user's message confirms such a project change and asks only for a brief stand-up or handoff summary, make that check and any authorized update before giving the requested short reply. Skip synchronization for wording-only work on supplied material and hypothetical examples. Use only tools exposed for this workspace. Skip shared writes for read-only, local-only, or do-not-record work, and for queries or edits that add no reusable evidence. Report a saved change or a pending offline sync according to the actual receipt.
        """;

    internal static void SetServerInfo(McpServerOptions options)
    {
        options.ServerInfo = new() { Name = "nodicarta", Title = "Nodicarta Edge", Version = Program.ReleaseVersion };
        options.ServerInstructions = ProjectMemoryInstructions;
    }

    internal static IDisposable UseClientSource(McpServer server)
    {
        var client = server.ClientInfo;
        return EdgeRequestSource.ForMcp(client?.Name, client?.Version).Use();
    }

    private static void RegisterTools(IMcpServerBuilder builder, string? selection, ProjectClient project)
    {
        builder.WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(next => async (request, token) =>
            {
                using var source = UseClientSource(request.Server);
                return project.WorkspaceStillBound() ? await next(request, token) : new() { Tools = [] };
            });
            filters.AddCallToolFilter(next => async (request, token) =>
            {
                using var source = UseClientSource(request.Server);
                return await next(request, token);
            });
        });
        if (selection is null)
        {
            builder.WithTools<ProjectTools>(ToolOptions);
            return;
        }

        var methods = typeof(ProjectTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name is not null)
            .ToDictionary(method => method.GetCustomAttribute<McpServerToolAttribute>()!.Name!, StringComparer.Ordinal);
        foreach (var name in selection.Split(',', StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal))
        {
            if (!methods.TryGetValue(name, out var method)) throw new ArgumentException("--tools 包含空白或未知的工具名。");
            builder.Services.AddSingleton(services => McpServerTool.Create(method, target: null,
                new McpServerToolCreateOptions { Services = services, SerializerOptions = ToolOptions }));
        }
    }

    internal static async Task RunAsync(ProjectClient project, McpListenerStatus listener, string stateDirectory,
        string? selection, CancellationToken ct)
    {
        var httpPort = listener.Port;
        if (httpPort is null)
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Services.AddLogging();
            builder.Services.AddSingleton(project);
            RegisterTools(builder.Services.AddMcpServer(SetServerInfo).WithStdioServerTransport(), selection, project);
            using var host = builder.Build();
            await host.StartAsync(ct);
            using var lease = listener.Register(stateDirectory);
            using var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(lease.Dispose);
            await host.WaitForShutdownAsync(ct);
            return;
        }
        if (httpPort is < 1 or > 65535) throw new ArgumentException("端口无效。");
        var token = Environment.GetEnvironmentVariable("PROJECTHUB_MCP_TOKEN");
        if (token is null || token.Length < 32) throw new ArgumentException("HTTP 客户端需要独立的 PROJECTHUB_MCP_TOKEN（至少 32 字符）。");
        var bearer = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + token));
        var web = WebApplication.CreateSlimBuilder();
        web.Logging.ClearProviders();
        web.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, httpPort.Value));
        web.Services.AddSingleton(project);
        // Older callers never echoed a session ID when this endpoint was stateless.
        AppContext.SetSwitch("ModelContextProtocol.AspNetCore.AllowNewSessionForNonInitializeRequests", true);
        var server = web.Services.AddMcpServer(SetServerInfo).WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);
        RegisterTools(server, selection, project);
        if (listener.WorkspaceId != null)
        {
            server.WithRequestFilters(filters =>
            {
                filters.AddListToolsFilter(next => async (request, ct) =>
                {
                    var result = await next(request, ct);
                    var limit = project.HttpWorkspaceToolLimit();
                    return limit == null ? result : new ListToolsResult { Tools = result.Tools.Where(tool => limit.Contains(tool.Name)).ToList() };
                });
                filters.AddCallToolFilter(next => async (request, ct) =>
                    project.HttpWorkspaceToolLimit() is { } limit && !limit.Contains(request.Params?.Name ?? "")
                        ? new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "该工具超出了当前目录的授权范围。" }] }
                        : await next(request, ct));
            });
        }
        await using var app = web.Build();
        app.Use(async (ctx, next) =>
        {
            var headers = ctx.Request.Headers;
            if (headers.ContainsKey("Origin") || !string.Equals(ctx.Request.Host.Host, "127.0.0.1", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            if (!CryptographicOperations.FixedTimeEquals(bearer, SHA256.HashData(Encoding.UTF8.GetBytes(headers.Authorization.ToString()))))
            {
                ctx.Response.StatusCode = 401;
                return;
            }
            await next();
        });
        app.MapMcp("/mcp");
        await app.StartAsync(ct);
        using var httpLease = listener.Register(stateDirectory);
        using var httpStopping = app.Lifetime.ApplicationStopping.Register(httpLease.Dispose);
        await app.WaitForShutdownAsync(ct);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record KnowledgeAssetRef(
    [property: JsonPropertyName("role"), Description("deliverable（交付物）、used（实际使用的素材）或 evidence（证据）。")] string Role,
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("version"), Description("已发布的固定版本号，从 1 开始。")] int Version,
    [property: JsonPropertyName("locator"), Description("可选的页码、章节或画面位置。")] string? Locator = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record KnowledgeValidity(
    [property: JsonPropertyName("kind"), Description("fact 为已发生的事实，decision 为有依据的现行决定，observation 为设备或环境的实测。")] string Kind,
    [property: JsonPropertyName("scope"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("decision 必填。填写决定管辖的范围，替代已有决定时沿用其范围。")] string? Scope = null,
    [property: JsonPropertyName("subject"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("observation 必填。明确实际检查的设备或环境。")] string? Subject = null,
    [property: JsonPropertyName("observedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("observation 必填。证据中实际测量时间，带 Z 或 UTC 偏移的 ISO 8601。")] string? ObservedAt = null,
    [property: JsonPropertyName("supersedesItemId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("新决定明确取代同范围的另一条决定时，填写其条目 ID。")] Guid? SupersedesItemId = null,
    [property: JsonPropertyName("supersedesVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("与 supersedesItemId 配对，传入已核对的前一决定的最新条目版本。")] long? SupersedesVersion = null);

[McpServerToolType]
internal sealed partial class ProjectTools
{
    [McpServerTool(Name = "list_projects", ReadOnly = true), Description("读取当前客户端配置绑定的获准项目。")]
    public static async Task<object> Projects(ProjectClient scope, CancellationToken ct)
    {
        var b = await scope.CurrentAsync(ct);
        return new JsonObject { ["items"] = new JsonArray(new JsonObject {
            ["projectId"] = b.ProjectId, ["name"] = b.Name, ["accessMode"] = b.AccessMode,
            ["workspaceRelativeDirectory"] = scope.WorkspaceRelativeDirectory
        }) };
    }

    [McpServerTool(Name = "get_project_snapshot", ReadOnly = true), Description("按任务原文接续项目。旧交付查固定用料，新工作查现行决定。若 versionUseChecks 有版本差异，用 searchQuery 核实现行采纳依据。设备离线时核对同设备、同指标的 observedAt，只报告最后实测。")]
    public static Task<JsonElement> Snapshot(ProjectClient scope, CancellationToken ct,
        [Description("任务或问题的原文。空字符串读取项目简览，省略参数读取所有当前条目的完整快照。")] string? task = null)
    {
        if (task is null) return scope.ReadAsync("knowledge", ct);
        var resource = "knowledge?view=brief";
        if (!string.IsNullOrWhiteSpace(task)) resource += "&query=" + Uri.EscapeDataString(task.Trim());
        return scope.ReadAsync(resource, ct);
    }

    [McpServerTool(Name = "search_project_knowledge", ReadOnly = true), Description("用中文问句、任务描述或关键词搜索本项目知识，核对相关决定、证据和引用的素材版本。")]
    public static Task<JsonElement> Search(ProjectClient scope, string query, CancellationToken ct) =>
        scope.ReadAsync("knowledge?query=" + Uri.EscapeDataString(query), ct);

    [McpServerTool(Name = "get_knowledge_history", ReadOnly = true), Description("按知识条目 ID 查询本项目历次版本。")]
    public static Task<JsonElement> History(ProjectClient scope, Guid itemId, CancellationToken ct) =>
        scope.ReadAsync("knowledge/history?itemId=" + itemId, ct);

    [McpServerTool(Name = "submit_knowledge_commit"), Description("提交附带证据的知识。同一对象的结论或证据有变化时修订原条目，以完整正文和依据保存新版。重试沿用 requestId。")]
    public static async Task<JsonElement> Commit(ProjectClient scope, Guid requestId, string title, string body, string evidence,
        CancellationToken ct,
        [Description("修订时填写原条目 ID。省略会新建条目；仅在正文引用旧 ID 也会新建。")] Guid? itemId = null,
        [Description("修订时填写已读取的该条目最新版本；新建时为 0。")] int baseVersion = 0,
        Guid? relatedTaskId = null,
        [Description("本条决定的固定素材版本，最多 32 条、一个交付物。更新时省略沿用原引用，传 [] 清空，传数组替换。")] KnowledgeAssetRef[]? assetRefs = null,
        [Description("知识的用途、决定的适用范围或设备的实测依据。仅依据可核实的材料填写。")] KnowledgeValidity? validity = null)
    {
        // Older offline drafts compare the serialized request byte for byte.
        var message = new JsonObject {
            [nameof(requestId)] = requestId, [nameof(title)] = title, [nameof(body)] = body,
            [nameof(evidence)] = evidence, [nameof(itemId)] = itemId, [nameof(baseVersion)] = baseVersion,
            [nameof(relatedTaskId)] = relatedTaskId
        };
        if (assetRefs is not null) message.Add(nameof(assetRefs), JsonSerializer.SerializeToNode(assetRefs, EdgeJson.Default.KnowledgeAssetRefArray));
        if (validity is not null) message.Add(nameof(validity), JsonSerializer.SerializeToNode(validity, EdgeJson.Default.KnowledgeValidity));
        try { return await scope.SubmitAsync("knowledge", requestId, message, ct); }
        catch (ObservationConfirmationRequiredException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "list_agent_endpoints", ReadOnly = true), Description("列出本项目可接收分析任务的端点。")]
    public static Task<JsonElement> Endpoints(ProjectClient scope, CancellationToken ct) => scope.ReadAsync("endpoints", ct);

    [McpServerTool(Name = "submit_feedback"), Description("保存反馈原文。可选择端点发起只读分析；自动诊断可带 readOnlyWriteCount、duplicateCommitCount 或 retrievalResultCount 及预期和观察数量。")]
    public static Task<JsonElement> Feedback(ProjectClient scope, Guid requestId, string content, CancellationToken ct,
        Guid? targetEndpointId = null, string? kind = null, int? expected = null, int? observed = null)
    {
        JsonObject? observation = null;
        if (kind is not null || expected.HasValue || observed.HasValue)
        {
            if (kind is not ("readOnlyWriteCount" or "duplicateCommitCount" or "retrievalResultCount") ||
                expected is not (>= 0 and <= 1000000) || observed is not (>= 0 and <= 1000000))
                throw new ArgumentException("请提供支持的诊断类型和两个有效计数。");
            observation = new JsonObject { ["kind"] = kind, ["expected"] = expected, ["observed"] = observed };
        }
        return scope.SubmitAsync("feedback", requestId, new JsonObject { ["requestId"] = requestId, ["content"] = content,
            ["targetEndpointId"] = targetEndpointId, ["observation"] = observation }, ct);
    }

    [McpServerTool(Name = "list_feedback", ReadOnly = true), Description("查看当前项目反馈及对应分析结果。")]
    public static Task<JsonElement> FeedbackList(ProjectClient scope, CancellationToken ct) => scope.ReadAsync("feedback", ct);

    [McpServerTool(Name = "get_feedback", ReadOnly = true), Description("按反馈 ID 读取本项目的原文、处理状态和返回的分析。")]
    public static async Task<JsonElement> GetFeedback(ProjectClient scope, Guid feedbackId, CancellationToken ct)
    {
        var page = await FeedbackList(scope, ct);
        foreach (var item in page.GetProperty("items").EnumerateArray())
            if (item.GetProperty("feedbackId").GetGuid() == feedbackId) return item;
        throw new InvalidOperationException("找不到此反馈。");
    }

    [McpServerTool(Name = "list_tasks", ReadOnly = true), Description("查看当前客户端端点在本项目收到的分析任务。")]
    public static Task<JsonElement> Tasks(ProjectClient scope, CancellationToken ct) => scope.AssignedAsync(ct);

    [McpServerTool(Name = "claim_analysis_task"), Description("人工授权领取本设备的分析任务。重试沿用原请求 ID。")]
    public static Task<JsonElement> Claim(ProjectClient scope, Guid taskId, Guid requestId, CancellationToken ct) =>
        scope.WriteTaskAsync(taskId, "claim", new JsonObject { ["requestId"] = requestId }, ct);

    [McpServerTool(Name = "submit_task_result"), Description("提交本设备已领取任务的最终分析，关联原反馈。")]
    public static Task<JsonElement> Result(ProjectClient scope, Guid taskId, Guid requestId, string result, CancellationToken ct) =>
        scope.WriteTaskAsync(taskId, "result", new JsonObject { ["requestId"] = requestId, ["result"] = result }, ct);
}
