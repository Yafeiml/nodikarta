using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkiaSharp;

namespace ProjectHub.Edge;

internal sealed partial class ProjectTools
{
    [McpServerTool(Name = "publish_asset"), Description("发布绑定目录内的一份素材。首发只需相对路径、名称、用途和稳定的 requestId。发布新版需提供 assetId 和已读取的 baseVersion。")]
    public static Task<JsonElement> PublishAsset(ProjectClient scope, Guid requestId, string relativePath, string name,
        string purpose, CancellationToken ct, string[]? tags = null, Guid? assetId = null, int baseVersion = 0) =>
        AssetCall(() => scope.PublishAssetAsync(requestId, relativePath, name, purpose, tags, assetId, baseVersion, ct));

    [McpServerTool(Name = "list_assets", ReadOnly = true), Description("按关键词搜索本项目素材名称、用途或标签。列表仅含最新版摘要，继续查询时将 nextCursor 传给 after。")]
    public static Task<JsonElement> ListAssets(ProjectClient scope, CancellationToken ct, string? query = null, Guid? after = null, int limit = 50) =>
        AssetCall(() => scope.ReadAsync($"assets?query={Uri.EscapeDataString(query ?? "")}&limit={limit}" + (after is null ? "" : $"&after={after:D}"), ct));

    [McpServerTool(Name = "get_asset", ReadOnly = true), Description("查看素材的指定版本或最新版本。includePreview 可附带小幅位图预览；SVG 和 PDF 可用 download_asset 获取原件。")]
    public static Task<CallToolResult> GetAsset(ProjectClient scope, Guid assetId, CancellationToken ct,
        int? version = null, bool includePreview = false) => AssetCall(async () =>
    {
        var meta = await scope.ReadAsync($"assets/{assetId:D}" + (version is null ? "" : $"?version={version}"), ct);
        List<ContentBlock> blocks = [new TextContentBlock { Text = meta.GetRawText() }];
        if (includePreview && meta.GetProperty("contentType").GetString() is "image/jpeg" or "image/png" or "image/webp")
        {
            var bytes = await scope.AssetContentAsync(meta, ct);
            using var codec = SKCodec.Create(new SKMemoryStream(bytes));
            if (codec != null && (long)codec.Info.Width * codec.Info.Height <= 16_000_000)
            {
                var scale = Math.Min(1d, 320d / Math.Max(codec.Info.Width, codec.Info.Height));
                var target = new SKImageInfo(Math.Max(1, (int)(codec.Info.Width * scale)), Math.Max(1, (int)(codec.Info.Height * scale)));
                using var bitmap = SKBitmap.Decode(codec);
                using var thumb = bitmap?.Resize(target, SKFilterQuality.Medium);
                using var encoded = thumb?.Encode(SKEncodedImageFormat.Png, 100);
                if (encoded != null) blocks.Add(ImageContentBlock.FromBytes(encoded.ToArray(), "image/png"));
            }
            if (blocks.Count == 1) blocks.Add(new TextContentBlock { Text = "此位图无法生成缩略图。可用 download_asset 获取原件。" });
        }
        return new CallToolResult { Content = blocks, StructuredContent = meta };
    });

    [McpServerTool(Name = "get_asset_decisions", ReadOnly = true), Description("查交付版本当时的用料和依据。versionUseChecks 列出已有新版的用料；接续制作时按 searchQuery 查采纳依据，向用户交代上次用版、库中最新版和本次选版。isCurrent 表示此知识是否仍为最新修订。")]
    public static Task<JsonElement> GetAssetDecisions(ProjectClient scope, Guid assetId, int version, CancellationToken ct) =>
        AssetCall(() => scope.ReadAsync($"assets/{assetId:D}/versions/{version}/decisions", ct));

    [McpServerTool(Name = "download_asset"), Description("将指定版本保存到当前工作区的相对路径。使用原文件格式。保留已有同名文件并返回冲突，成功时返回相对路径与 SHA-256。")]
    public static Task<object> DownloadAsset(ProjectClient scope, Guid assetId, int version, string destinationPath, CancellationToken ct) =>
        AssetCall(() => scope.DownloadAssetAsync(assetId, version, destinationPath, ct));

    private static async Task<T> AssetCall<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (HttpRequestException e)
        {
            throw new McpException(e.StatusCode switch
            {
                HttpStatusCode.Conflict => "素材版本或 requestId 冲突。请重新读取最新版本，检查是否在重用不同内容的请求标识。",
                HttpStatusCode.NotFound => "本项目中没有找到该素材或版本。",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "当前设备身份或项目授权不允许此操作。",
                HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType =>
                    "Core 拒绝了素材参数、文件格式或大小，请核对后重试。",
                _ => "素材请求未完成，请核对 Core 连接状态，原请求重试时保留相同 requestId。"
            });
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { throw new McpException(e.Message); }
        catch (IOException e) when ((e.HResult & 0xffff) is 80 or 183) { throw new McpException("目标文件已存在，请指定新的相对路径。"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new McpException("素材传输未完成，请核对文件完整性、绑定目录中的普通文件路径及读写权限。"); }
    }
}

internal sealed partial class ProjectClient
{
    private const int MaxAssetBytes = 8 * 1024 * 1024;

    private async Task<LocalBinding> AssetWorkspaceAsync(CancellationToken ct)
    {
        var matching = (await session.BindingsAsync(ct)).Where(row => row.ProjectId == project && row.BindingId == binding
            && (workspace == null || row.WorkspaceId == workspace)).ToList();
        return matching.Count switch
        {
            1 => matching[0],
            0 => throw new InvalidOperationException("项目授权或工作目录绑定已失效。"),
            _ => throw new InvalidOperationException("本项目绑定了多个本机目录，请在接入设置中选择目录，或在启动配置中指定 --workspace。")
        };
    }

    private static string AssetType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpeg" or ".jpg" => "image/jpeg", ".webp" => "image/webp",
        ".svg" => "image/svg+xml", ".pdf" => "application/pdf",
        _ => throw new ArgumentException("素材格式支持 PNG、JPEG、WebP、SVG 或 PDF。")
    };

    internal async Task<JsonElement> PublishAssetAsync(Guid requestId, string relativePath, string name, string purpose,
        string[]? tags, Guid? assetId, int baseVersion, CancellationToken ct)
    {
        var row = await AssetWorkspaceAsync(ct);
        if (!writable || row.AccessMode != "readWrite") throw new InvalidOperationException("当前设备或客户端仅有只读权限。");
        _ = AssetType(relativePath);
        byte[] content;
        using (var source = new AssetWorkspace(row.LocalPath, relativePath, createParents: false))
        using (var input = source.OpenSource())
        {
            if (input.Length is < 1 or > MaxAssetBytes) throw new ArgumentException("素材必须非空且不超过 8 MiB。");
            content = new byte[(int)input.Length];
            await input.ReadExactlyAsync(content, ct);
        }
        return await WriteAsync("assets", new { requestId, name, purpose, fileName = Path.GetFileName(relativePath),
            tags, assetId, baseVersion, content }, ct);
    }

    internal async Task<byte[]> AssetContentAsync(JsonElement meta, CancellationToken ct)
    {
        var id = meta.GetProperty("assetId").GetGuid();
        var version = meta.GetProperty("version").GetInt32();
        var size = meta.GetProperty("size").GetInt32();
        if (size is < 1 or > MaxAssetBytes) throw new InvalidDataException("Core 返回的素材大小无效。");
        using var response = await session.SendAsync(HttpMethod.Get, Prefix + $"assets/{id:D}/content?version={version}",
            ct: ct, completion: HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(size, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length != size || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),
            meta.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("素材大小或 SHA-256 校验失败。");
        return bytes;
    }

    internal async Task<object> DownloadAssetAsync(Guid assetId, int version, string destinationPath, CancellationToken ct)
    {
        if (version < 1) throw new ArgumentException("请指定已发布的素材版本。");
        var row = await AssetWorkspaceAsync(ct);
        var meta = await ReadAsync($"assets/{assetId:D}?version={version}", ct);
        if (AssetType(destinationPath) != meta.GetProperty("contentType").GetString())
            throw new ArgumentException("目标文件扩展名必须与素材原格式匹配。");
        var bytes = await AssetContentAsync(meta, ct);
        using var destination = new AssetWorkspace(row.LocalPath, destinationPath, createParents: true);
        await destination.SaveAsync(bytes, ct);
        return new { assetId, version, relativePath = destination.RelativePath, size = bytes.Length, sha256 = meta.GetProperty("sha256").GetString() };
    }
}
