using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;

namespace ProjectHub.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AssetInput(Guid RequestId, string Name, string Purpose, string FileName, byte[] Content,
    string[]? Tags = null, Guid? AssetId = null, int BaseVersion = 0);

internal static partial class DeviceApi
{
    private static void MapAssetApi(RouteGroupBuilder project)
    {
        project.MapGet("/assets", (HttpContext c, CoreStore store, string? query, Guid? after, int? limit) =>
            store.ListAssetsAsync(Access(c), query, after, limit ?? 50));
        project.MapGet("/assets/{assetId:guid}", (HttpContext c, CoreStore store, Guid assetId, int? version) =>
            store.GetAssetAsync(Access(c), assetId, version));
        project.MapGet("/assets/{assetId:guid}/versions/{version:int}/decisions",
            (HttpContext c, CoreStore store, Guid assetId, int version) =>
                store.GetAssetDecisionsAsync(Access(c), assetId, version));
        project.MapGet("/assets/{assetId:guid}/content", async (HttpContext c, CoreStore store, Guid assetId, int version) =>
        {
            var file = await store.DownloadAssetAsync(Access(c), assetId, version);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
        project.MapPost("/assets", PublishAssetAsync);
    }

    private static async Task<IResult> PublishAssetAsync(HttpContext c, CoreStore store)
    {
        var scope = Access(c);
        // Reject ungranted senders before buffering their files. The committing transaction rechecks the grant.
        await store.RequireAssetUploadAsync(scope);
        if (!c.Request.HasJsonContentType()) return Problem(c, 415, "unsupported_media_type", "素材发布请求需要 JSON。");
        const long maxRequest = 4 * ((CoreStore.MaxAssetBytes + 2) / 3) + 32768;
        if (c.Request.ContentLength > maxRequest) return Problem(c, 413, "asset_too_large", "素材请求过大。");
        if (c.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = Math.Min(limit.MaxRequestBodySize ?? maxRequest, maxRequest);
        try
        {
            var input = await c.Request.ReadFromJsonAsync<AssetInput>(c.RequestAborted);
            return input is null ? Problem(c, 400, "invalid_argument", "缺少素材字段。")
                : await Save(store.PublishAssetAsync(scope, input));
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        {
            return Problem(c, 413, "asset_too_large", "素材请求过大。");
        }
        catch (JsonException)
        {
            return Problem(c, 400, "invalid_argument", "素材字段或文件编码无效。");
        }
    }
}
