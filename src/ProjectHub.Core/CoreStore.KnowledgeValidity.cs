using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace ProjectHub.Core;

public sealed partial class CoreStore
{
    private sealed record KnowledgeHead(long Version, KnowledgeValidity? Validity, bool Replaced, Guid? Predecessor, long? PredecessorVersion);

    private static async Task<KnowledgeHead?> ReadKnowledgeHeadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid project, Guid item)
    {
        await using var query = Query(conn, tx, """
            SELECT k.version,k.validity,EXISTS (SELECT 1 FROM projecthub.knowledge_versions n
                    WHERE n.project_id=k.project_id AND n.supersedes_item_id=k.item_id),
                origin.supersedes_item_id,origin.supersedes_version
            FROM projecthub.knowledge_versions k
            LEFT JOIN projecthub.knowledge_versions origin ON origin.project_id=k.project_id
                AND origin.item_id=k.item_id AND origin.supersedes_item_id IS NOT NULL
            WHERE k.project_id=$1 AND k.item_id=$2 ORDER BY k.version DESC LIMIT 1
            """, project, item);
        await using var reader = await query.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new(reader.GetInt64(0), reader.IsDBNull(1) ? null :
                JsonSerializer.Deserialize<KnowledgeValidity>(reader.GetString(1), JsonSerializerOptions.Web),
            reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }

    private static DateTimeOffset ObservationTime(string? text)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
        if (!DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ||
            value > DateTimeOffset.UtcNow)
            throw InvalidInput();
        return value;
    }

    private static async Task<(KnowledgeValidity? Validity, Guid? Predecessor, long? PredecessorVersion)> PrepareKnowledgeValidityAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid project, Guid item, KnowledgeInput input)
    {
        var submitted = input.Validity;
        var predecessor = submitted?.SupersedesItemId;
        var predecessorVersion = submitted?.SupersedesVersion;
        if (predecessor.HasValue != predecessorVersion.HasValue || predecessor == Guid.Empty || predecessor == item || predecessorVersion is <= 0)
            throw InvalidInput();

        // Both heads must stay stable while a replacement is validated, including concurrent edits and competing successors.
        Guid[] items = predecessor is Guid source ? [item, source] : [item];
        await using var locks = Query(conn, tx, """
            SELECT pg_advisory_xact_lock(lock_key) FROM (
                SELECT DISTINCT hashtextextended($1::text || i::text, 0) AS lock_key FROM unnest($2::uuid[]) i ORDER BY lock_key
            ) heads
            """, project.ToString(), items);
        await locks.ExecuteNonQueryAsync();
        var head = await ReadKnowledgeHeadAsync(conn, tx, project, item);
        if ((head?.Version ?? 0) != input.BaseVersion) throw VersionConflict();
        if (submitted is null)
        {
            if (head?.Validity?.Kind == "observation")
            {
                // A client that cannot supply a measurement must not attach new claims or evidence to an old timestamp.
                await using var unchanged = Query(conn, tx, """
                    SELECT title=$4 AND body=$5 AND evidence=$6 AND related_task_id IS NOT DISTINCT FROM $7::uuid
                        AND ($8::jsonb IS NULL OR (record->'assetRefs')=jsonb_strip_nulls($8::jsonb))
                    FROM projecthub.knowledge_record_contents WHERE project_id=$1 AND item_id=$2 AND version=$3
                    """, project, item, input.BaseVersion, input.Title.Trim(), input.Body, input.Evidence, input.RelatedTaskId,
                    input.AssetRefs is null ? null : JsonSerializer.Serialize(input.AssetRefs, JsonSerializerOptions.Web));
                if (await unchanged.ExecuteScalarAsync() is not true)
                    throw new DeviceApiError(400, "observation_confirmation_required",
                        "修订设备观察的结论或依据需要显式确认。请核对现场证据，填写 validity.kind=observation、subject、带时区的 observedAt 和 evidence。");
            }
            return (head?.Validity, null, null);
        }

        var info = submitted with { Scope = submitted.Scope?.Trim(), Subject = submitted.Subject?.Trim(),
            SupersedesItemId = null, SupersedesVersion = null };
        if ((info.Scope is not null && !IsValidName(submitted.Scope, 160)) ||
            (info.Subject is not null && !IsValidName(submitted.Subject, 160)) ||
            info.Kind is not ("fact" or "decision" or "observation") ||
            (info.Kind == "decision" && info.Scope is null) || (info.Kind != "decision" && predecessor is not null) ||
            (info.Kind == "observation" && info.Subject is null) || (info.Kind != "observation" && (info.ObservedAt is not null || info.Subject is not null)))
            throw InvalidInput();
        if (head?.Validity is {} previous && (previous.Kind != info.Kind || previous.Scope != info.Scope || previous.Subject != info.Subject))
            throw InvalidInput();
        if (info.Kind == "observation")
        {
            var observed = ObservationTime(info.ObservedAt);
            if (head?.Validity is { ObservedAt: {} priorTime } && observed < ObservationTime(priorTime))
                throw VersionConflict();
        }
        if (predecessor is null) return (info, null, null);
        if (head?.Predecessor is not null)
        {
            if (head.Predecessor != predecessor || head.PredecessorVersion != predecessorVersion) throw VersionConflict();
            return (info, null, null);
        }
        var prior = await ReadKnowledgeHeadAsync(conn, tx, project, predecessor.Value) ?? throw UnknownResource();
        if (prior.Version != predecessorVersion || prior.Replaced || head?.Replaced == true) throw VersionConflict();
        if (prior.Validity?.Kind != "decision" || prior.Validity.Scope != info.Scope) throw InvalidInput();
        return (info, predecessor, predecessorVersion);
    }
}
