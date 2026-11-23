using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace ProjectHub.Core;

public sealed partial class CoreStore
{
    private const string LiveEndpoints = """
        FROM projecthub.endpoints e
        JOIN projecthub.devices d ON d.id=e.device_id AND d.revoked_at IS NULL
        JOIN projecthub.device_grants g ON g.binding_id=e.binding_id AND g.device_id=e.device_id
            AND g.project_id=e.project_id AND g.access_mode='readWrite'
        """;

    private static DeviceApiError InvalidInput() => new(400, "invalid_argument", "请核对正文、标识和版本。");
    private static DeviceApiError VersionConflict() => new(409, "version_conflict", "当前版本或任务状态已变化。");

    private static bool ValidText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength &&
        !value.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r'));

    private static NpgsqlCommand Query(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params object?[] args)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var argument in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = argument ?? DBNull.Value });
        return cmd;
    }

    private static async Task<JsonElement> RecordAsync(NpgsqlCommand command)
    {
        var json = await command.ExecuteScalarAsync() as string ?? throw UnknownResource();
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    internal async Task<object> ListCollaborationAsync(ProjectAccess scope, string resource, string? query = null, Guid? itemId = null,
        string? view = null)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var device = await RequireWorkspaceAccessAsync(conn, tx, scope.Token, scope.ProjectId, scope.BindingId, false);
        if (query is { Length: > 512 } || query?.Any(char.IsControl) is true || itemId == Guid.Empty || view is not (null or "brief"))
            throw InvalidInput();
        if (resource == "knowledge" && (view == "brief" || !string.IsNullOrWhiteSpace(query)))
            return await FindKnowledgeAsync(conn, tx, scope.ProjectId, query?.Trim() ?? "", view == "brief");
        if (resource == "history") CaptureObject(itemId);
        await using var rows = resource switch
        {
            "knowledge" => Query(conn, tx, """
                SELECT record FROM (
                    SELECT DISTINCT ON (item_id) record,created_at,item_id
                    FROM projecthub.knowledge_records WHERE project_id=$1 ORDER BY item_id,version DESC
                ) latest
                ORDER BY created_at DESC,item_id
                """, scope.ProjectId),
            "history" => Query(conn, tx, """
                SELECT record FROM projecthub.knowledge_records WHERE project_id=$1 AND item_id=$2 ORDER BY version DESC
                """, scope.ProjectId, itemId),
            "feedback" => Query(conn, tx, """
                SELECT record FROM projecthub.feedback_records WHERE project_id=$1 ORDER BY created_at DESC,id
                """, scope.ProjectId),
            "endpoints" => Query(conn, tx, """
                SELECT jsonb_build_object('endpointId',e.id,'name',e.name,'deviceId',e.device_id)
                """ + "\n" + LiveEndpoints + "\nWHERE e.project_id=$1 ORDER BY e.name,e.id", scope.ProjectId),
            "tasks" => Query(conn, tx, """
                SELECT record FROM projecthub.task_records WHERE project_id=$1 AND device_id=$2 AND binding_id=$3 ORDER BY id
                """, scope.ProjectId, device, scope.BindingId),
            _ => throw new ArgumentOutOfRangeException(nameof(resource))
        };
        return await ReadListAsync(rows);
    }

    private async Task<object> ReadListAsync(NpgsqlCommand rows)
    {
        var items = new List<JsonElement>();
        await using var reader = await rows.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            items.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        CaptureMatches(items.Count);
        return new { items, nextCursor = (string?)null };
    }

    // Reserve the key in the same transaction as its mutation, so crashes and concurrent retries have one outcome.
    private async Task<StoredReply> WriteCollaborationAsync(ProjectAccess scope, string operation, Guid requestId, object input,
        Func<NpgsqlConnection, NpgsqlTransaction, Guid, Task<JsonElement>> mutate, bool created = true)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var device = await RequireWorkspaceAccessAsync(conn, tx, scope.Token, scope.ProjectId, scope.BindingId, true);
        if (requestId == Guid.Empty)
            throw InvalidInput();
        if (Activity is { } request) request.RequestId = requestId;
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { scope.BindingId, input }));
        await using var reserve = Query(conn, tx, """
            INSERT INTO projecthub.collaboration_requests(device_id,project_id,operation,request_id,fingerprint)
            VALUES ($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING
            """, device, scope.ProjectId, operation, requestId, fingerprint);
        if (await reserve.ExecuteNonQueryAsync() == 0)
        {
            await using var prior = Query(conn, tx, """
                SELECT reply FROM projecthub.collaboration_requests WHERE device_id=$1 AND project_id=$2
                    AND operation=$3 AND request_id=$4 AND fingerprint=$5
                """, device, scope.ProjectId, operation, requestId, fingerprint);
            var json = await prior.ExecuteScalarAsync() as string;
            if (json is null)
                throw new DeviceApiError(409, "idempotency_conflict", "请求标识已用于其他内容或绑定。");
            return new(JsonSerializer.Deserialize<JsonElement>(json), false);
        }
        var result = await mutate(conn, tx, device);
        await using var save = Query(conn, tx, """
            UPDATE projecthub.collaboration_requests SET reply=$5::jsonb
            WHERE device_id=$1 AND project_id=$2 AND operation=$3 AND request_id=$4
            """, device, scope.ProjectId, operation, requestId, result.GetRawText());
        await save.ExecuteNonQueryAsync();
        await AuditAsync(conn, tx, device, operation, scope.ProjectId, device,
            objectId: WrittenObject(result, operation),
            version: result.TryGetProperty("version", out var version) ? version.GetInt64() : null, requestId: requestId);
        await tx.CommitAsync();
        return new(result, created);
    }

    internal Task<StoredReply> SubmitKnowledgeAsync(ProjectAccess scope, KnowledgeInput input) =>
        WriteCollaborationAsync(scope, "knowledge.commit", input.RequestId, input, async (conn, tx, device) =>
        {
            if (!IsValidName(input.Title, 160) || !ValidText(input.Body, 32000) || !ValidText(input.Evidence, 4000) ||
                input.ItemId == Guid.Empty || input.RelatedTaskId == Guid.Empty || input.BaseVersion is < 0 or long.MaxValue ||
                (input.BaseVersion != 0 && input.ItemId is null))
                throw InvalidInput();
            if (input.RelatedTaskId is Guid taskId)
            {
                await using var related = Query(conn, tx,
                    "SELECT id FROM projecthub.task_records WHERE project_id=$1 AND id=$2", scope.ProjectId, taskId);
                if (await related.ExecuteScalarAsync() is null)
                    throw UnknownResource();
            }
            var item = input.ItemId ?? Guid.NewGuid();
            var (validity, predecessor, predecessorVersion) = await PrepareKnowledgeValidityAsync(conn, tx, scope.ProjectId, item, input);
            await using var insert = Query(conn, tx, """
                INSERT INTO projecthub.knowledge_versions(item_id,version,project_id,device_id,title,body,evidence,related_task_id,
                    validity,supersedes_item_id,supersedes_version)
                SELECT $1,$2+1::bigint,$3,$4,$5,$6,$7,$8::uuid,jsonb_strip_nulls($9::jsonb),$10::uuid,$11::bigint
                WHERE $2=0 OR EXISTS (
                    SELECT 1 FROM projecthub.knowledge_versions WHERE project_id=$3 AND item_id=$1 AND version=$2)
                ON CONFLICT (project_id,item_id,version) DO NOTHING RETURNING commit_id
                """, item, input.BaseVersion, scope.ProjectId, device, input.Title.Trim(), input.Body, input.Evidence, input.RelatedTaskId,
                validity is null ? null : JsonSerializer.Serialize(validity, JsonSerializerOptions.Web), predecessor, predecessorVersion);
            if (await insert.ExecuteScalarAsync() is not Guid commit)
                throw VersionConflict();
            await SaveKnowledgeAssetRefsAsync(conn, tx, scope.ProjectId, item, input.BaseVersion, input.AssetRefs);
            await using var saved = Query(conn, tx, "SELECT record FROM projecthub.knowledge_records WHERE commit_id=$1", commit);
            return await RecordAsync(saved);
        });

    internal Task<StoredReply> RegisterEndpointAsync(ProjectAccess scope, EndpointInput input) =>
        WriteCollaborationAsync(scope, "endpoint.register", input.RequestId, input, async (conn, tx, device) =>
        {
            if (!IsValidName(input.Name, 120))
                throw InvalidInput();
            await using var save = Query(conn, tx, """
                INSERT INTO projecthub.endpoints(project_id,device_id,binding_id,name) VALUES ($1,$2,$3,$4)
                ON CONFLICT(binding_id,name) DO UPDATE SET name=excluded.name
                RETURNING jsonb_build_object('endpointId',id,'name',name,'deviceId',device_id)
                """, scope.ProjectId, device, scope.BindingId, input.Name.Trim());
            return await RecordAsync(save);
        });

    internal Task<StoredReply> SubmitFeedbackAsync(ProjectAccess scope, FeedbackInput input) =>
        WriteCollaborationAsync(scope, "feedback.submit", input.RequestId,
            // Keep the original receipt fingerprint for clients retrying a v3 request.
            input.Observation is null ? new { input.RequestId, input.Content, input.TargetEndpointId } : input,
            async (conn, tx, device) =>
        {
            if (!ValidText(input.Content, 32000) || input.TargetEndpointId == Guid.Empty)
                throw InvalidInput();
            if (input.Observation is { } observation &&
                (observation.Kind is not ("readOnlyWriteCount" or "duplicateCommitCount" or "retrievalResultCount") ||
                 observation.Expected is not (>= 0 and <= 1_000_000) ||
                 observation.Observed is not (>= 0 and <= 1_000_000)))
                throw InvalidInput();
            await using var insert = Query(conn, tx, """
                INSERT INTO projecthub.feedback(project_id,device_id,content,observation)
                VALUES ($1,$2,$3,$4::jsonb) RETURNING id
                """, scope.ProjectId, device, input.Content, input.Observation is null ? null :
                    JsonSerializer.Serialize(input.Observation, JsonSerializerOptions.Web));
            var feedbackId = (Guid)(await insert.ExecuteScalarAsync())!;
            if (input.TargetEndpointId is Guid endpoint)
            {
                await using var assign = Query(conn, tx, """
                    INSERT INTO projecthub.analysis_tasks(feedback_id,endpoint_id)
                    SELECT $1,e.id
                    """ + "\n" + LiveEndpoints + "\nWHERE e.id=$2 AND e.project_id=$3 FOR SHARE OF d,g",
                    feedbackId, endpoint, scope.ProjectId);
                if (await assign.ExecuteNonQueryAsync() != 1)
                    throw UnknownResource();
            }
            await using var saved = Query(conn, tx, "SELECT record FROM projecthub.feedback_records WHERE id=$1", feedbackId);
            return await RecordAsync(saved);
        });

    internal Task<StoredReply> AdvanceTaskAsync(ProjectAccess scope, Guid taskId, Guid requestId, string? result, bool complete) =>
        WriteCollaborationAsync(scope, complete ? "task.result" : "task.claim", requestId, new { taskId, result },
        async (conn, tx, device) =>
        {
            if (complete && !ValidText(result, 32000))
                throw InvalidInput();
            await using var task = Query(conn, tx, """
                SELECT t.status FROM projecthub.analysis_tasks t JOIN projecthub.endpoints e ON e.id=t.endpoint_id
                WHERE t.id=$1 AND e.project_id=$2 AND e.device_id=$3 AND e.binding_id=$4 FOR UPDATE OF t
                """, taskId, scope.ProjectId, device, scope.BindingId);
            var status = await task.ExecuteScalarAsync() as string ?? throw UnknownResource();
            if (status != (complete ? "claimed" : "pending"))
                throw VersionConflict();
            await using var advance = Query(conn, tx, "UPDATE projecthub.analysis_tasks SET status=$2,result=$3::text WHERE id=$1",
                taskId, complete ? "completed" : "claimed", result);
            await advance.ExecuteNonQueryAsync();
            await using var saved = Query(conn, tx, "SELECT record FROM projecthub.task_records WHERE id=$1", taskId);
            return await RecordAsync(saved);
        }, created: false);
}
