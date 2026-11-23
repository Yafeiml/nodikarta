using Npgsql;

namespace ProjectHub.Core;

public sealed partial class CoreStore
{
    // Client names reported by Edge are printable ASCII. These three reserved filter values
    // distinguish absent metadata without colliding with a real reported name.
    private const string ActivityClient = """
        coalesce(a.client_name, CASE WHEN a.entry='admin' THEN 'Core 后台'
            WHEN a.occurred_at<coverage.started_at THEN '未采集' ELSE '未上报' END)
        """;

    private const string ActivityScope = """
            FROM projecthub.audit_log a
            JOIN projecthub.owner_account owner ON owner.id=$1
            LEFT JOIN projecthub.projects p ON p.id=a.project_id
            LEFT JOIN projecthub.devices d ON d.id=a.device_id
            LEFT JOIN projecthub.devices successor ON successor.replaces_device_id=d.id
            LEFT JOIN projecthub.devices actor ON actor.id=a.actor_id
            CROSS JOIN projecthub.activity_coverage coverage
            LEFT JOIN projecthub.device_pairings pairing ON pairing.id=a.pairing_id
            WHERE (p.owner_id=$1 OR d.owner_id=$1 OR a.actor_id=$1 OR pairing.owner_id=$1
                OR a.is_diagnostic AND a.actor_id IS NULL AND a.project_id IS NULL AND a.device_id IS NULL)
                AND ($2::uuid IS NULL OR a.project_id=$2) AND ($3::uuid IS NULL OR a.device_id=$3)
                AND CASE $4 WHEN 'important' THEN (NOT a.is_diagnostic AND a.action<>'device.tokens_rotated' OR a.result<>'success')
                    WHEN 'reads' THEN a.is_diagnostic AND a.result='success'
                    WHEN 'errors' THEN a.result<>'success'
                    WHEN 'security' THEN a.result<>'success' AND a.project_id IS NULL ELSE true END
            """;

    public async Task<ActivityFilterOptions> ActivityFiltersAsync(Guid owner, Guid? project, Guid? device, string view)
    {
        await using var cmd = db.CreateCommand($$"""
            SELECT coalesce(array_agg(DISTINCT client), '{}'), coalesce(array_agg(DISTINCT action), '{}'),
                coalesce(array_agg(DISTINCT result), '{}'), coalesce(array_agg(DISTINCT device_id) FILTER (WHERE device_id IS NOT NULL), '{}')
            FROM (SELECT {{ActivityClient}} AS client, a.action, a.result, a.device_id
                {{ActivityScope}}) AS choices
            """);
        foreach (var value in new object?[] { owner, project, device, view })
            cmd.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var products = reader.GetFieldValue<string[]>(0).Select(ActivityClientNames.Product)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(products, reader.GetFieldValue<string[]>(1),
            reader.GetFieldValue<string[]>(2), reader.GetFieldValue<Guid[]>(3));
    }

    public async Task<IReadOnlyList<AdminActivity>> ListActivityAsync(Guid ownerId, Guid? projectId = null,
        Guid? deviceId = null, string view = "all", string[]? clients = null, string[]? actions = null,
        DateTimeOffset? since = null, DateTimeOffset? until = null, long? before = null, int limit = 50, string[]? results = null)
    {
        if (view is not ("all" or "important" or "reads" or "errors" or "security")) view = "all";
        await using var cmd = db.CreateCommand($$"""
            SELECT a.id,a.occurred_at,a.action,a.result,a.project_id,a.device_id,a.actor_id,a.object_id,a.request_id,
                d.label,coalesce(actor.label,CASE WHEN a.actor_id=owner.id THEN owner.username END),a.client_name,a.client_version,a.entry,host(a.source_ip),
                a.trace_id,a.detail,successor.id,a.object_version,a.match_count,a.origin_at,a.is_replay
            {{ActivityScope}}
                AND (cardinality($5::text[]) = 0 OR lower({{ActivityClient}}) = ANY($5)) AND (cardinality($6::text[]) = 0 OR a.action = ANY($6))
                AND ($7::timestamptz IS NULL OR a.occurred_at>=$7) AND ($8::timestamptz IS NULL OR a.occurred_at<$8)
                AND ($9::bigint IS NULL OR (a.occurred_at,a.id) < (SELECT occurred_at,id FROM projecthub.audit_log WHERE id=$9))
                AND (cardinality($11::text[]) = 0 OR a.result = ANY($11))
            ORDER BY a.occurred_at DESC,a.id DESC LIMIT $10
            """);
        var names = (clients ?? []).SelectMany(ActivityClientNames.Aliases)
            .Select(name => name.ToLowerInvariant()).Distinct().ToArray();
        object?[] values = [ownerId, projectId, deviceId, view, names,
            actions ?? [], since?.ToUniversalTime(), until?.ToUniversalTime(),
            before, Math.Clamp(limit, 1, 201), results ?? []];
        foreach (var value in values) cmd.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<AdminActivity>();
        Guid? GuidAt(int col) => r.IsDBNull(col) ? null : r.GetGuid(col);
        string? TextAt(int col) => r.IsDBNull(col) ? null : r.GetString(col);
        while (await r.ReadAsync())
            list.Add(new(r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.GetString(3), GuidAt(4), GuidAt(5),
                GuidAt(6), GuidAt(7), GuidAt(8), TextAt(9), TextAt(10), TextAt(11), TextAt(12), TextAt(13),
                TextAt(14), TextAt(15), TextAt(16), GuidAt(17), r.IsDBNull(18) ? null : r.GetInt64(18),
                r.IsDBNull(19) ? null : r.GetInt32(19), r.IsDBNull(20) ? null : r.GetDateTime(20), r.GetBoolean(21)));
        return list;
    }
}

public sealed record ActivityFilterOptions(string[] Clients, string[] Actions, string[] Results, Guid[] DeviceIds);

internal static class ActivityClientNames
{
    // The first name is the product label. Match complete identifiers so custom clients retain their own names.
    private static readonly string[][] Products =
    [
        ["Codex", "codex-mcp-client"],
        ["Claude Code", "claude-code"],
        ["Antigravity", "antigravity-client", "Google Antigravity"]
    ];

    public static string[] Aliases(string name) =>
        Products.FirstOrDefault(names => names.Contains(name, StringComparer.OrdinalIgnoreCase)) ?? [name];

    public static string Product(string name) => Aliases(name)[0];
}
