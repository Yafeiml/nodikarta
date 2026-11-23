using System.Text.Json;
using Npgsql;

namespace ProjectHub.Core;

public sealed partial class CoreStore
{
    // k is one selected knowledge revision. Restrict all asset lookups to its project and pinned references.
    private const string ReadLinkedKnowledgeAssets = """
        CROSS JOIN LATERAL (
            SELECT coalesce(jsonb_agg(jsonb_strip_nulls(jsonb_build_object(
                'role',ref.role,'assetId',ref.asset_id,'version',ref.asset_version,'name',a.name,
                'locator',CASE WHEN shape.brief THEN left(ref.locator,96) ELSE ref.locator END,
                'latestPublishedVersion',latest.version
            )) ORDER BY ref.position) FILTER (WHERE NOT shape.brief OR ref.position<=4),'[]'::jsonb) AS files,
            coalesce(to_jsonb((array_agg(jsonb_build_object(
                'assetId',ref.asset_id,'usedVersion',ref.asset_version,'latestPublishedVersion',latest.version,
                'currentAdoption','requiresEvidence','searchQuery',ref.asset_id::text || ' 现行规范',
                'guidance',format('复刻旧稿可沿用固定用料 v%s，素材库已发布 v%s。新交付先读 validity.currentDecision 指向的决定，没有指向时按 searchQuery 查找。核对适用范围；缺少采纳依据时标明用版待确认。',ref.asset_version,latest.version)
            ) ORDER BY ref.position) FILTER (WHERE ref.role='used' AND ref.asset_version<latest.version))
                [1:CASE WHEN shape.brief THEN 4 ELSE 32 END]),'[]'::jsonb) AS use_checks,
            count(*)>4 OR coalesce(bool_or(length(ref.locator)>96),false) AS shortened
            FROM projecthub.knowledge_asset_refs ref
            JOIN projecthub.asset_versions a ON (a.project_id,a.asset_id,a.version)=(ref.project_id,ref.asset_id,ref.asset_version)
            CROSS JOIN LATERAL (SELECT max(v.version) AS version FROM projecthub.asset_versions v
                WHERE v.project_id=ref.project_id AND v.asset_id=ref.asset_id) latest
            WHERE ref.project_id=k.project_id AND ref.item_id=k.item_id AND ref.knowledge_version=k.version
        ) refs
        """;

    private static async Task SaveKnowledgeAssetRefsAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid projectId, Guid itemId, long baseVersion, KnowledgeAssetRef[]? refs)
    {
        if (refs is null)
        {
            if (baseVersion == 0) return;
            // An older client can edit a decision without dropping its pinned files.
            await using var inherit = Query(conn, tx, """
                INSERT INTO projecthub.knowledge_asset_refs
                    (project_id,item_id,knowledge_version,position,role,asset_id,asset_version,locator)
                SELECT project_id,item_id,knowledge_version+1,position,role,asset_id,asset_version,locator
                FROM projecthub.knowledge_asset_refs WHERE project_id=$1 AND item_id=$2 AND knowledge_version=$3
                """, projectId, itemId, baseVersion);
            await inherit.ExecuteNonQueryAsync();
            return;
        }
        if (refs.Length == 0) return;
        if (refs.Length > 32 || refs.Any(r => r is null || r.AssetId == Guid.Empty || r.Version <= 0 ||
                r.Role is not ("deliverable" or "used" or "evidence") ||
                (r.Locator is string locator && (locator.Length > 512 || !IsValidName(locator, 512)))) ||
            refs.Count(r => r.Role == "deliverable") > 1 || refs.Distinct().Count() != refs.Length)
            throw InvalidInput();

        await using var add = Query(conn, tx, """
            INSERT INTO projecthub.knowledge_asset_refs
                (project_id,item_id,knowledge_version,position,role,asset_id,asset_version,locator)
            SELECT a.project_id,$2,$3,r.position,r.entry->>'role',a.asset_id,a.version,r.entry->>'locator'
            FROM jsonb_array_elements($4::jsonb) WITH ORDINALITY AS r(entry,position)
            JOIN projecthub.asset_versions a ON a.project_id=$1 AND a.asset_id=(r.entry->>'assetId')::uuid
                AND a.version=(r.entry->>'version')::integer
            """, projectId, itemId, baseVersion + 1, JsonSerializer.Serialize(refs, JsonSerializerOptions.Web));
        if (await add.ExecuteNonQueryAsync() != refs.Length)
            throw UnknownResource();
    }

    internal async Task<object> GetAssetDecisionsAsync(ProjectAccess scope, Guid assetId, int version)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await RequireWorkspaceAccessAsync(conn, tx, scope.Token, scope.ProjectId, scope.BindingId, false);
        if (version <= 0) throw InvalidInput();
        await using var exists = Query(conn, tx, """
            SELECT 1 FROM projecthub.asset_versions WHERE project_id=$1 AND asset_id=$2 AND version=$3
            """, scope.ProjectId, assetId, version);
        if (await exists.ExecuteScalarAsync() is null) throw UnknownResource();
        CaptureObject(assetId, version);
        await using var rows = Query(conn, tx, $$"""
            SELECT k.record || jsonb_build_object('linkedAssets',refs.files,'versionUseChecks',refs.use_checks,'isCurrent',NOT EXISTS (
                SELECT 1 FROM projecthub.knowledge_versions newer WHERE newer.project_id=k.project_id
                    AND newer.item_id=k.item_id AND newer.version>k.version))
            FROM projecthub.knowledge_asset_refs r JOIN projecthub.knowledge_records k
                ON k.project_id=r.project_id AND k.item_id=r.item_id AND k.version=r.knowledge_version
            CROSS JOIN (VALUES(false)) shape(brief)
            {{ReadLinkedKnowledgeAssets}}
            WHERE r.project_id=$1 AND r.asset_id=$2 AND r.asset_version=$3 AND r.role='deliverable'
            ORDER BY k.created_at DESC,k.item_id,k.version DESC
            """, scope.ProjectId, assetId, version);
        return await ReadListAsync(rows);
    }
}
