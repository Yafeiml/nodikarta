using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace ProjectHub.Core;

internal static partial class DeviceApi
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record RedeemRequest(string? OneTimeCode, string? DeviceLabel, string? InstanceNonce);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ClaimUpdateRequest(string? OneTimeCode, string? DeviceLabel, string? InstanceNonce, string? AttemptSecret);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ConfirmUpdateRequest(string? AttemptSecret);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record RefreshRequest(string? RefreshToken);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record WorkspaceRequest(Guid RequestId, Guid WorkspaceId, Guid[]? RepositoryIds);

    public static void MapDeviceApi(this WebApplication app)
    {
        var api = app.MapGroup("/v1");
        api.AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                return await next(invocation);
            }
            catch (DeviceApiError error)
            {
                return Problem(invocation.HttpContext, error.Status, error.Code, error.Message);
            }
        });

        MapCollaborationApi(api);
        api.MapPost("/device-pairings/{pairingId:guid}/redeem", async (Guid pairingId, RedeemRequest body,
            CoreStore store, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(body.OneTimeCode) || string.IsNullOrWhiteSpace(body.InstanceNonce)
                || body.InstanceNonce.Length > 128 || body.InstanceNonce.Any(char.IsControl)
                || body.DeviceLabel is { Length: > 120 } || body.DeviceLabel?.Any(char.IsControl) == true)
                return Problem(context, 400, "invalid_argument", "配对字段无效。");
            var tokens = await store.RedeemPairingAsync(pairingId, body.OneTimeCode, body.DeviceLabel ?? "", body.InstanceNonce);
            return tokens is null ? Problem(context, 401, "unauthenticated", "配对码无效、过期或已被使用。")
                : Results.Json(tokens, statusCode: 201);
        });
        api.MapPost("/device-pairings/{pairingId:guid}/claim", async (Guid pairingId, ClaimUpdateRequest body,
            CoreStore store, HttpContext context) =>
        {
            if (!IsHexSecret(body.OneTimeCode, 32) || !IsHexSecret(body.AttemptSecret, 64) ||
                string.IsNullOrWhiteSpace(body.InstanceNonce) || body.InstanceNonce.Length > 128 ||
                body.InstanceNonce.Any(char.IsControl) || body.DeviceLabel is { Length: > 120 } ||
                body.DeviceLabel?.Any(char.IsControl) == true)
                return Problem(context, 400, "invalid_argument", "接替请求字段无效。");
            var tokens = await store.ClaimDeviceUpdateAsync(pairingId, body.OneTimeCode!, (body.DeviceLabel ?? "").Trim(),
                body.InstanceNonce, body.AttemptSecret!);
            return tokens is null ? UnavailableUpdate(context) : Results.Ok(tokens);
        });
        api.MapPost("/device-pairings/{pairingId:guid}/confirm", async (Guid pairingId, ConfirmUpdateRequest body,
            CoreStore store, HttpContext context) =>
        {
            if (!IsHexSecret(body.AttemptSecret, 64))
                return Problem(context, 400, "invalid_argument", "接替确认字段无效。");
            var tokens = await store.ConfirmDeviceUpdateAsync(pairingId, body.AttemptSecret!);
            return tokens is null ? UnavailableUpdate(context) : Results.Ok(tokens);
        });
        api.MapPost("/tokens/refresh", async (RefreshRequest body, CoreStore store, HttpContext context) =>
        {
            var tokens = await store.RefreshDeviceAsync(body.RefreshToken ?? "");
            return tokens is null ? Problem(context, 401, "unauthenticated", "设备续期凭证无效。") : Results.Ok(tokens);
        });
        api.MapGet("/projects", async (HttpContext context, CoreStore store) =>
        {
            var projects = await store.ReadDeviceProjectsAsync(Bearer(context), null);
            return Results.Ok(new { items = projects, nextCursor = (string?)null });
        });
        api.MapGet("/projects/{projectId:guid}", async (Guid projectId, HttpContext context, CoreStore store) =>
        {
            var projects = await store.ReadDeviceProjectsAsync(Bearer(context), projectId);
            return Results.Ok(projects[0]);
        });
        const string workspaces = "/projects/{projectId:guid}/bindings/{bindingId:guid}/workspaces";
        api.MapGet(workspaces, async (Guid projectId, Guid bindingId, HttpContext context, CoreStore store) =>
        {
            var ids = await store.ReadWorkspaceReferencesAsync(Bearer(context), projectId, bindingId);
            return Results.Ok(new { items = ids.Select(id => new { workspaceId = id, version = 1 }), nextCursor = (string?)null });
        });
        api.MapPost(workspaces, async (Guid projectId, Guid bindingId, WorkspaceRequest body, HttpContext context, CoreStore store) =>
        {
            var token = Bearer(context);
            if (body.WorkspaceId == Guid.Empty || body.RequestId == Guid.Empty || body.RepositoryIds is { Length: > 0 })
                return Problem(context, 400, "invalid_argument", "需要有效的工作区及请求标识。此版本仅支持空仓库列表。");
            var created = await store.RegisterWorkspaceAsync(token, projectId, bindingId, body.WorkspaceId, body.RequestId);
            return Results.Json(new { bindingId, body.WorkspaceId, version = 1 }, statusCode: created ? 201 : 200);
        });
    }

    private static bool IsHexSecret(string? value, int length) => value?.Length == length && value.All(char.IsAsciiHexDigit);

    private static IResult UnavailableUpdate(HttpContext context) =>
        Problem(context, 401, "pairing_unavailable", "设备更新码已失效或此次接替不可用。");

    private static string Bearer(HttpContext context)
    {
        if (AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var auth)
            && auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            && auth.Parameter is { Length: > 0 and <= 256 })
            return auth.Parameter;
        throw new DeviceApiError(401, "unauthenticated", "需要有效的设备凭证。");
    }

    private static IResult Problem(HttpContext context, int status, string code, string message)
    {
        if (context.Features.Get<CoreActivityRequest>() is { } request) request.Failure = code;
        if (status == 401)
            context.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Json(new { error = new { code, message, traceId = context.TraceIdentifier } }, statusCode: status);
    }
}
