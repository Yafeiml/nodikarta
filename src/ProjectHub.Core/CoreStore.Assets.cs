using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using Npgsql;

namespace ProjectHub.Core;

public sealed partial class CoreStore
{
    internal const int MaxAssetBytes = 8 * 1024 * 1024;

    // Keep the original and its immutable version in the same transaction and database backup.
    private const string AssetSchemaSql = """
        CREATE TABLE projecthub.asset_versions (
            project_id uuid NOT NULL REFERENCES projecthub.projects(id),
            asset_id uuid NOT NULL,
            version integer NOT NULL CHECK(version > 0),
            device_id uuid NOT NULL REFERENCES projecthub.devices(id),
            name text NOT NULL,
            purpose text NOT NULL,
            tags text[] NOT NULL,
            file_name text NOT NULL,
            content_type text NOT NULL,
            content bytea NOT NULL CHECK(octet_length(content) BETWEEN 1 AND 8388608),
            size integer GENERATED ALWAYS AS (octet_length(content)) STORED,
            sha256 text NOT NULL,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            PRIMARY KEY(project_id,asset_id,version)
        );
        CREATE VIEW projecthub.asset_records AS
            SELECT v.project_id,v.asset_id,v.version,v.name,v.purpose,v.tags,v.file_name,
                jsonb_build_object('assetId',v.asset_id,'version',v.version,'name',v.name,'purpose',v.purpose,
                    'tags',v.tags,'fileName',v.file_name,'contentType',v.content_type,'size',v.size,
                    'sha256',v.sha256,'createdAt',v.created_at) AS record
            FROM projecthub.asset_versions v;
        """;

    internal async Task RequireAssetUploadAsync(ProjectAccess scope)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await RequireWorkspaceAccessAsync(conn, tx, scope.Token, scope.ProjectId, scope.BindingId, true);
    }

    internal async Task<object> ListAssetsAsync(ProjectAccess scope, string? query, Guid? after, int limit)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await RequireWorkspaceAccessAsync(conn, tx, scope.Token, scope.ProjectId, scope.BindingId, false);
        if (query is { Length: > 512 } || query?.Any(char.IsControl) is true || limit is < 1 or > 100)
            throw InvalidInput();
        await using var cmd = Query(conn, tx, """
            SELECT record FROM (
                SELECT DISTINCT ON (asset_id) asset_id,name,purpose,tags,file_name,record
                FROM projecthub.asset_records WHERE project_id=$1 AND ($2::uuid IS NULL OR asset_id>$2)
                ORDER BY asset_id,version DESC
            ) latest WHERE strpos(lower(concat_ws(' ',name,purpose,file_name,array_to_string(tags,' '))),lower($3))>0
            ORDER BY asset_id LIMIT $4
            """, scope.ProjectId, after, query?.Trim() ?? "", limit + 1);
        var items = new List<JsonElement>(limit);
        Guid? nextCursor = null;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (items.Count == limit)
            {
                nextCursor = items[^1].GetProperty("assetId").GetGuid();
                break;
            }
            items.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        }
        CaptureMatches(items.Count);
        return new { items, nextCursor };
    }

    internal async Task<JsonElement> GetAssetAsync(ProjectAccess scope, Guid assetId, int? version)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await RequireWorkspaceAccessAsync(conn, tx, scope.Token, scope.ProjectId, scope.BindingId, false);
        if (version is <= 0) throw InvalidInput();
        await using var cmd = Query(conn, tx, """
            SELECT record FROM projecthub.asset_records WHERE project_id=$1 AND asset_id=$2
                AND ($3::int IS NULL OR version=$3) ORDER BY version DESC LIMIT 1
            """, scope.ProjectId, assetId, version);
        var record = await RecordAsync(cmd);
        CaptureObject(assetId, record.GetProperty("version").GetInt64());
        return record;
    }

    internal async Task<(byte[] Content, string ContentType, string FileName)> DownloadAssetAsync(
        ProjectAccess scope, Guid assetId, int version)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await RequireWorkspaceAccessAsync(conn, tx, scope.Token, scope.ProjectId, scope.BindingId, false);
        if (version <= 0) throw InvalidInput();
        await using var cmd = Query(conn, tx, """
            SELECT content,content_type,file_name FROM projecthub.asset_versions
            WHERE project_id=$1 AND asset_id=$2 AND version=$3
            """, scope.ProjectId, assetId, version);
        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess);
        if (!await reader.ReadAsync()) throw UnknownResource();
        CaptureObject(assetId, version);
        return (await reader.GetFieldValueAsync<byte[]>(0), reader.GetString(1), reader.GetString(2));
    }

    internal Task<StoredReply> PublishAssetAsync(ProjectAccess scope, AssetInput input)
    {
        if (input.Content is { Length: > MaxAssetBytes })
            throw new DeviceApiError(413, "asset_too_large", "单份素材最多 8 MiB。");
        if (input.Content is not { Length: > 0 } || !IsValidName(input.Name, 160) || !ValidText(input.Purpose, 1024)
            || !IsValidName(input.FileName, 200) || input.FileName != input.FileName.Trim()
            || input.FileName.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0
            || input.Tags is { Length: > 20 } || input.Tags?.Any(tag => !IsValidName(tag, 48)) == true
            || input.AssetId == Guid.Empty || input.BaseVersion is < 0 or int.MaxValue
            || input.AssetId.HasValue != (input.BaseVersion > 0))
            throw InvalidInput();
        var name = input.Name.Trim();
        var purpose = input.Purpose.Trim();
        var tags = input.Tags?.Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var type = AssetContentType(input.FileName, input.Content);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(input.Content));
        return WriteCollaborationAsync(scope, "asset.publish", input.RequestId,
            new { input.AssetId, input.BaseVersion, input.FileName, name, purpose, tags, sha256 },
            async (conn, tx, device) =>
            {
                var id = input.AssetId ?? Guid.NewGuid();
                await using var insert = Query(conn, tx, """
                    INSERT INTO projecthub.asset_versions(project_id,asset_id,version,device_id,name,purpose,
                            tags,file_name,content_type,content,sha256)
                        SELECT $1,$2,$3+1,$4,$5,$6,$7,$8,$9,$10,$11
                        WHERE $3=0 OR EXISTS (SELECT 1 FROM projecthub.asset_versions
                            WHERE project_id=$1 AND asset_id=$2 AND version=$3)
                        ON CONFLICT(project_id,asset_id,version) DO NOTHING
                    """, scope.ProjectId, id, input.BaseVersion, device, name, purpose, tags, input.FileName,
                    type, input.Content, sha256);
                if (await insert.ExecuteNonQueryAsync() != 1) throw VersionConflict();
                await using var saved = Query(conn, tx, """
                    SELECT record FROM projecthub.asset_records WHERE project_id=$1 AND asset_id=$2 AND version=$3
                    """, scope.ProjectId, id, input.BaseVersion + 1);
                return await RecordAsync(saved);
            });
    }

    private static string AssetContentType(string name, byte[] content)
    {
        var bytes = content.AsSpan();
        switch (Path.GetExtension(name).ToLowerInvariant())
        {
            case ".png" when bytes.StartsWith((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]): return "image/png";
            case ".jpg" or ".jpeg" when bytes.StartsWith((ReadOnlySpan<byte>)[255, 216, 255]): return "image/jpeg";
            case ".webp" when bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8):
                return "image/webp";
            case ".pdf" when bytes.StartsWith("%PDF-"u8): return "application/pdf";
            case ".svg":
                try
                {
                    using var source = new MemoryStream(content, writable: false);
                    using var xml = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null, MaxCharactersInDocument = MaxAssetBytes });
                    if (xml.MoveToContent() == XmlNodeType.Element && xml.LocalName == "svg" &&
                        xml.NamespaceURI == "http://www.w3.org/2000/svg")
                    {
                        while (xml.Read()) { }
                        return "image/svg+xml";
                    }
                }
                catch (XmlException) { }
                break;
        }
        throw new DeviceApiError(400, "invalid_asset_format", "请提供扩展名与内容相符的 PNG、JPEG、WebP、SVG 或 PDF 文件。");
    }
}
