using System.Globalization;
using System.Net;

namespace ProjectHub.Core;

// Exists only for one HTTP request. Project/device identity is filled exclusively by Core's auth checks.
internal sealed class CoreActivityRequest
{
    public string? Action { get; set; }
    public Guid? AdminId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? ObjectId { get; set; }
    public Guid? RequestId { get; set; }
    public long? ObjectVersion { get; set; }
    public int? MatchCount { get; set; }
    public string? AccessMode { get; set; }
    public string? Failure { get; set; }
    public bool IsRead { get; private init; }
    public string? Client { get; private init; }
    public string? ClientVersion { get; private init; }
    public string? Entry { get; private init; }
    public DateTimeOffset? OriginAt { get; private init; }
    public bool Replay { get; private init; }
    public string? Trace { get; private init; }
    public IPAddress? Ip { get; private init; }

    internal static CoreActivityRequest FromHttp(HttpContext context, bool api)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (!api) return new() { Entry = "admin", IsRead = HttpMethods.IsGet(context.Request.Method), Ip = ip, Trace = context.TraceIdentifier };
        string? Header(string name, int limit)
        {
            var values = context.Request.Headers["X-Nodicarta-" + name];
            if (values.Count != 1 || values[0] is not string value || value.Length > limit ||
                !value.All(c => c is >= ' ' and <= '~')) return null;
            value = value.Trim(' ');
            return value.Length == 0 ? null : value;
        }
        var entry = Header("Entry", 16);
        var stamp = Header("Origin-At", 40);
        var sourceAt = DateTimeOffset.TryParseExact(stamp, ["yyyy-MM-dd'T'HH:mm:ssK","yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && stamp is not null &&
            (stamp.EndsWith('Z') || stamp.LastIndexOfAny(['+', '-']) > 18) && parsed.Year is >= 1970 and <= 9998
            ? parsed.ToUniversalTime() : (DateTimeOffset?)null;
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var read = HttpMethods.IsGet(context.Request.Method);
        // Match server-declared route templates only. Never record path segments or query text supplied by callers.
        var suffix = route?.Split("/bindings/{bindingId:guid}", StringSplitOptions.None).Last();
        var action = suffix switch
        {
            "/knowledge" when !read => "knowledge.commit",
            "/knowledge" when context.Request.Query.ContainsKey("query") => "knowledge.search",
            "/knowledge" => "knowledge.snapshot",
            "/knowledge/history" => "knowledge.history",
            "/assets" => read ? "asset.search" : "asset.publish",
            "/assets/{assetId:guid}" => "asset.get",
            "/assets/{assetId:guid}/content" => "asset.download",
            "/assets/{assetId:guid}/versions/{version:int}/decisions" => "asset.decisions",
            "/feedback" => read ? "feedback.list" : "feedback.submit",
            "/endpoints" => read ? "endpoint.list" : "endpoint.register",
            "/tasks" => "task.list",
            "/tasks/{taskId:guid}/claim" => "task.claim",
            "/tasks/{taskId:guid}/result" => "task.result",
            "/workspaces" => read ? "workspace.list" : "workspace.registered",
            "/v1/projects" or "/v1/projects/{projectId:guid}" => "grant.lookup",
            "/v1/tokens/refresh" => "device.refresh",
            "/v1/device-pairings/{pairingId:guid}/redeem" => "device.redeem",
            "/v1/device-pairings/{pairingId:guid}/claim" => "device.claim",
            "/v1/device-pairings/{pairingId:guid}/confirm" => "device.confirm",
            _ => null
        };
        return new()
        {
            Action = action, IsRead = read, Client = Header("Client", 64), ClientVersion = Header("Client-Version", 48),
            Entry = entry is "mcp" or "cli" or "gui" or "background" ? entry : null,
            OriginAt = sourceAt, Replay = Header("Replay", 1) == "1", Trace = context.TraceIdentifier, Ip = ip
        };
    }
}
