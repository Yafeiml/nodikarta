using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectHub.Core;

internal sealed record ProjectAccess(string Token, Guid ProjectId, Guid BindingId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record KnowledgeInput(Guid RequestId, string Title, string Body, string Evidence,
    Guid? ItemId = null, long BaseVersion = 0, Guid? RelatedTaskId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] KnowledgeAssetRef[]? AssetRefs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] KnowledgeValidity? Validity = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record KnowledgeValidity(string Kind, string? Scope = null, string? Subject = null, string? ObservedAt = null,
    Guid? SupersedesItemId = null, long? SupersedesVersion = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record KnowledgeAssetRef(string Role, Guid AssetId, int Version, string? Locator = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FeedbackInput(Guid RequestId, string Content, Guid? TargetEndpointId = null,
    FeedbackObservation? Observation = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FeedbackObservation(string Kind, int? Expected, int? Observed);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EndpointInput(Guid RequestId, string Name);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ClaimInput(Guid RequestId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AnalysisInput(Guid RequestId, string Result);
internal sealed record StoredReply(JsonElement Body, bool Created);

internal static partial class DeviceApi
{
    private static ProjectAccess Access(HttpContext context) => new(Bearer(context),
        Guid.Parse((string)context.Request.RouteValues["projectId"]!),
        Guid.Parse((string)context.Request.RouteValues["bindingId"]!));

    private static async Task<IResult> Save(Task<StoredReply> action)
    {
        var reply = await action;
        return Results.Json(reply.Body, statusCode: reply.Created ? 201 : 200);
    }

    private static void MapCollaborationApi(RouteGroupBuilder api)
    {
        var project = api.MapGroup("/projects/{projectId:guid}/bindings/{bindingId:guid}");
        MapAssetApi(project);
        project.MapGet("/knowledge", (HttpContext c, CoreStore store, string? query, string? view) =>
            store.ListCollaborationAsync(Access(c), "knowledge", query, view: view));
        project.MapGet("/knowledge/history", (HttpContext c, CoreStore store, Guid itemId) =>
            store.ListCollaborationAsync(Access(c), "history", itemId: itemId));
        project.MapGet("/feedback", (HttpContext c, CoreStore store) =>
            store.ListCollaborationAsync(Access(c), "feedback"));
        project.MapGet("/endpoints", (HttpContext c, CoreStore store) =>
            store.ListCollaborationAsync(Access(c), "endpoints"));
        project.MapGet("/tasks", (HttpContext c, CoreStore store) =>
            store.ListCollaborationAsync(Access(c), "tasks"));
        project.MapPost("/knowledge", (HttpContext c, CoreStore store, KnowledgeInput body) =>
            Save(store.SubmitKnowledgeAsync(Access(c), body)));
        project.MapPost("/endpoints", (HttpContext c, CoreStore store, EndpointInput body) =>
            Save(store.RegisterEndpointAsync(Access(c), body)));
        project.MapPost("/feedback", (HttpContext c, CoreStore store, FeedbackInput body) =>
            Save(store.SubmitFeedbackAsync(Access(c), body)));
        project.MapPost("/tasks/{taskId:guid}/claim", (HttpContext c, CoreStore store, Guid taskId, ClaimInput body) =>
            Save(store.AdvanceTaskAsync(Access(c), taskId, body.RequestId, null, complete: false)));
        project.MapPost("/tasks/{taskId:guid}/result", (HttpContext c, CoreStore store, Guid taskId, AnalysisInput body) =>
            Save(store.AdvanceTaskAsync(Access(c), taskId, body.RequestId, body.Result, complete: true)));
    }
}
