using System.Text.Json;
using Npgsql;

namespace ProjectHub.Core;

public sealed record AdminProject(Guid Id, string Name, long KnowledgeCount, long AssetCount, long DeviceCount,
    long RevisionCount, long AssetVersions, long AssetBytes, DateTimeOffset? Changed, string? ChangeTitle,
    string? ChangeTab, Guid? ChangeId, long? ChangeVersion);
public sealed record AdminResource(Guid Id, string Name, long Version, DateTimeOffset Created, Guid DeviceId,
    string Device, bool Replaced, string Caption, string? Validity, long Size);
public sealed record AdminReference(Guid Id, long Version, string Name, string Role, string? Locator);
public sealed record AdminPairing(Guid Id, string Label, string Status, DateTimeOffset Expires, Guid? Successor);

public sealed partial class CoreStore
{
    // All object lookups carry the owner and project together, including direct links to older versions.
    private NpgsqlCommand AdminQuery(string sql, params object?[] values)
    {
        var cmd = db.CreateCommand(sql);
        foreach (var value in values)
            cmd.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return cmd;
    }

    public async Task<List<AdminProject>> AdminProjectsAsync(Guid owner, string search, int offset, int take, Guid? id = null)
    {
        await using var cmd = AdminQuery("""
            WITH page AS MATERIALIZED (
                SELECT id,name,created_at FROM projecthub.projects
                WHERE owner_id=$1 AND ($2::uuid IS NULL OR id=$2)
                    AND ($3='' OR strpos(lower(name),lower($3))>0)
                ORDER BY created_at DESC,id LIMIT $4 OFFSET $5
            )
            SELECT p.id,p.name,k.items,a.items,d.devices,k.versions,a.versions,a.bytes,
                   recent.created_at,recent.title,recent.tab,recent.id,recent.ver
            FROM page p
            CROSS JOIN LATERAL (SELECT count(DISTINCT item_id) items,count(*) versions
                FROM projecthub.knowledge_versions WHERE project_id=p.id) k
            CROSS JOIN LATERAL (SELECT count(DISTINCT asset_id) items,count(*) versions,coalesce(sum(size),0) bytes
                FROM projecthub.asset_versions WHERE project_id=p.id) a
            CROSS JOIN LATERAL (SELECT count(*) devices FROM projecthub.device_grants g
                JOIN projecthub.devices d ON d.id=g.device_id WHERE g.project_id=p.id AND d.revoked_at IS NULL) d
            LEFT JOIN LATERAL (SELECT * FROM (
                (SELECT created_at,title,'knowledge' tab,item_id id,version ver FROM projecthub.knowledge_versions
                 WHERE project_id=p.id ORDER BY created_at DESC,item_id,version DESC LIMIT 1)
                UNION ALL
                (SELECT created_at,name,'assets',asset_id,version FROM projecthub.asset_versions
                 WHERE project_id=p.id ORDER BY created_at DESC,asset_id,version DESC LIMIT 1)
            ) u ORDER BY created_at DESC,id,ver DESC LIMIT 1) recent ON true
            ORDER BY p.created_at DESC,p.id
            """, owner, id, search, take, offset);
        var rows = new List<AdminProject>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(new(r.GetGuid(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4),
                r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.IsDBNull(8) ? null : r.GetDateTime(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetGuid(11), r.IsDBNull(12) ? null : r.GetInt64(12)));
        return rows;
    }

    public async Task<List<AdminResource>> AdminResourcesAsync(Guid owner, Guid project, bool assets, Guid? subject,
        string search, int offset, int take)
    {
        var source = assets ? "asset_versions" : "knowledge_versions";
        var id = assets ? "asset_id" : "item_id";
        var title = assets ? "name" : "title";
        var description = assets ? "v.file_name,NULL::text,v.size::bigint" :
            "''::text,k.record->'validity'->>'status',0::bigint";
        var validity = assets ? "" : "JOIN projecthub.knowledge_records k ON (k.project_id,k.item_id,k.version)=(v.project_id,v.item_id,v.version)";
        var distinct = subject is null ? $"DISTINCT ON (v.{id})" : "";
        await using var cmd = AdminQuery($"""
            WITH heads AS (
                SELECT {distinct} v.project_id,v.{id},v.version,v.created_at
                FROM projecthub.{source} v JOIN projecthub.projects p ON p.id=v.project_id
                WHERE p.owner_id=$1 AND p.id=$2 AND ($3::uuid IS NULL OR v.{id}=$3)
                ORDER BY v.{id},v.version DESC
            ), page AS MATERIALIZED (
                SELECT h.* FROM heads h JOIN projecthub.{source} s
                ON (s.project_id,s.{id},s.version)=(h.project_id,h.{id},h.version)
                WHERE ($4='' OR strpos(lower(s.{title}),lower($4))>0)
                ORDER BY h.created_at DESC,h.{id},h.version DESC LIMIT $5 OFFSET $6
            )
            SELECT v.{id},v.{title},v.version::bigint,v.created_at,v.device_id,d.label,
                EXISTS(SELECT 1 FROM projecthub.devices n WHERE n.replaces_device_id=d.id),{description}
            FROM page h JOIN projecthub.{source} v
                ON (v.project_id,v.{id},v.version)=(h.project_id,h.{id},h.version)
            JOIN projecthub.devices d ON d.id=v.device_id {validity}
            ORDER BY v.created_at DESC,v.{id},v.version DESC
            """, owner, project, subject, search, take, offset);
        var rows = new List<AdminResource>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(new(r.GetGuid(0),r.GetString(1),r.GetInt64(2),r.GetDateTime(3),r.GetGuid(4),r.GetString(5),
                r.GetBoolean(6),r.GetString(7),r.IsDBNull(8) ? null : r.GetString(8),r.GetInt64(9)));
        return rows;
    }

    public async Task<JsonElement?> AdminResourceVersionAsync(Guid owner, Guid project, bool assets, Guid id, long? version)
    {
        var source = assets ? "asset_records" : "knowledge_records";
        var versions = assets ? "asset_versions" : "knowledge_versions";
        var key = assets ? "asset_id" : "item_id";
        await using var cmd = AdminQuery($"""
            SELECT v.record || jsonb_build_object('sourceDeviceId',d.id,'sourceLabel',d.label,'sourceReplaced',
                EXISTS(SELECT 1 FROM projecthub.devices n WHERE n.replaces_device_id=d.id))
            FROM projecthub.{source} v JOIN projecthub.projects p ON p.id=v.project_id
            JOIN projecthub.{versions} s ON (s.project_id,s.{key},s.version)=(v.project_id,v.{key},v.version)
            JOIN projecthub.devices d ON d.id=s.device_id AND d.owner_id=p.owner_id
            WHERE p.owner_id=$1 AND p.id=$2 AND v.{key}=$3 AND ($4::bigint IS NULL OR v.version=$4)
            ORDER BY v.version DESC LIMIT 1
            """, owner, project, id, version);
        return await cmd.ExecuteScalarAsync() is string data ? JsonSerializer.Deserialize<JsonElement>(data) : null;
    }

    public async Task<List<AdminReference>> AdminReferencesAsync(Guid owner, Guid project, bool assets, Guid id, long version)
    {
        var lookup = assets
            ? "SELECT v.item_id,v.version,v.title,r.role,r.locator FROM projecthub.knowledge_asset_refs r JOIN projecthub.knowledge_versions v ON (v.project_id,v.item_id,v.version)=(r.project_id,r.item_id,r.knowledge_version)"
            : "SELECT v.asset_id,v.version::bigint,v.name,r.role,r.locator FROM projecthub.knowledge_asset_refs r JOIN projecthub.asset_versions v ON (v.project_id,v.asset_id,v.version)=(r.project_id,r.asset_id,r.asset_version)";
        var predicate = assets ? "r.asset_id=$3 AND r.asset_version=$4 ORDER BY v.created_at DESC,v.item_id,r.position" :
            "r.item_id=$3 AND r.knowledge_version=$4 ORDER BY r.position";
        await using var cmd = AdminQuery(lookup + " JOIN projecthub.projects p ON p.id=r.project_id WHERE p.owner_id=$1 AND p.id=$2 AND " + predicate,
            owner, project, id, version);
        var rows = new List<AdminReference>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(new(r.GetGuid(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.IsDBNull(4) ? null : r.GetString(4)));
        return rows;
    }

    public async Task<(byte[] Content,string Name)?> AdminFileAsync(Guid owner, Guid project, Guid asset, int version)
    {
        await using var cmd = AdminQuery("""
            SELECT a.content,a.file_name FROM projecthub.asset_versions a JOIN projecthub.projects p ON p.id=a.project_id
            WHERE p.owner_id=$1 AND p.id=$2 AND a.asset_id=$3 AND a.version=$4
            """, owner,project,asset,version);
        await using var r = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess);
        if (!await r.ReadAsync()) return null;
        return (await r.GetFieldValueAsync<byte[]>(0),r.GetString(1));
    }

    public async Task<AdminPairing?> AdminPairingAsync(Guid owner, Guid id)
    {
        await using var cmd = AdminQuery("""
            SELECT id,label,CASE WHEN consumed_at IS NOT NULL THEN 'confirmed' WHEN revoked_at IS NOT NULL THEN 'cancelled'
                WHEN expires_at<=clock_timestamp() THEN 'expired' WHEN claim_digest IS NOT NULL THEN 'claimed' ELSE 'pending' END,
                expires_at,redeemed_device_id FROM projecthub.device_pairings WHERE owner_id=$1 AND id=$2
            """,owner,id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetDateTime(3),r.IsDBNull(4) ? null : r.GetGuid(4)) : null;
    }

    public async Task<Dictionary<Guid,DateTimeOffset>> AdminContactsAsync(Guid owner)
    {
        await using var cmd = AdminQuery("SELECT id,last_contact_at FROM projecthub.devices WHERE owner_id=$1 AND last_contact_at IS NOT NULL",owner);
        await using var r = await cmd.ExecuteReaderAsync();
        var contacts = new Dictionary<Guid,DateTimeOffset>();
        while (await r.ReadAsync()) contacts.Add(r.GetGuid(0),r.GetDateTime(1));
        return contacts;
    }
}
