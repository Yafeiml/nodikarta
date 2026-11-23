using System.Text.Json;
using Npgsql;

namespace ProjectHub.Core;

public sealed record AdminActivity(long Id, DateTimeOffset OccurredAt, string Action, string Result,
    Guid? ProjectId, Guid? DeviceId, Guid? ActorId, Guid? ObjectId, Guid? RequestId, string? DeviceLabel,
    string? ActorLabel, string? Client, string? ClientVersion, string? Entry, string? Ip, string? TraceId,
    string? Detail, Guid? ReplacedByDeviceId, long? ObjectVersion, int? MatchCount, DateTimeOffset? OriginAt, bool IsReplay);
public sealed record ActivityCoverage(DateTimeOffset StartedAt, long MissedCount, DateTimeOffset? FirstMissedAt, DateTimeOffset? LastMissedAt);

public sealed partial class CoreStore
{
    private const string ActivitySchemaSql = """
        ALTER TABLE projecthub.devices ADD COLUMN last_contact_at timestamptz;
        ALTER TABLE projecthub.audit_log
            ADD COLUMN client_name varchar(64), ADD COLUMN client_version varchar(48),
            ADD COLUMN entry text, ADD COLUMN source_ip inet, ADD COLUMN trace_id text,
            ADD COLUMN origin_at timestamptz, ADD COLUMN is_replay boolean NOT NULL DEFAULT false,
            ADD COLUMN result text NOT NULL DEFAULT 'success', ADD COLUMN is_diagnostic boolean NOT NULL DEFAULT false,
            ADD COLUMN object_id uuid, ADD COLUMN object_version bigint, ADD COLUMN request_id uuid,
            ADD COLUMN match_count int, ADD COLUMN detail text;
        CREATE INDEX activity_by_project ON projecthub.audit_log(project_id,occurred_at DESC,id DESC);
        CREATE INDEX activity_by_device ON projecthub.audit_log(device_id,occurred_at DESC,id DESC);
        CREATE INDEX activity_retention ON projecthub.audit_log(occurred_at) WHERE is_diagnostic;
        CREATE INDEX activity_ip_expiry ON projecthub.audit_log(occurred_at) WHERE source_ip IS NOT NULL;
        CREATE TABLE projecthub.activity_coverage(
            singleton boolean PRIMARY KEY CHECK(singleton), started_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            missed_count bigint NOT NULL DEFAULT 0, first_missed_at timestamptz, last_missed_at timestamptz);
        INSERT INTO projecthub.activity_coverage(singleton) VALUES(true);
        """;

    private CoreActivityRequest? Activity => requests?.HttpContext?.Features.Get<CoreActivityRequest>();
    private readonly SemaphoreSlim diagnosticSlots = new(12, 12);
    private readonly object coverageLock = new();
    private long missed;
    private DateTimeOffset? firstMissed, lastMissed;

    private Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid actorId, string action,
        Guid? projectId = null, Guid? deviceId = null, Guid? pairingId = null, Guid? objectId = null,
        long? version = null, Guid? requestId = null, string? detail = null, bool forDeviceGrants = false)
    {
        var request = Activity ?? (requests?.HttpContext is { } ctx ? CoreActivityRequest.FromHttp(ctx, api: false) : null);
        return InsertActivityAsync(conn, tx, actorId, action, projectId, deviceId, pairingId, objectId,
            version, requestId, detail, request, "success", diagnostic: false, CancellationToken.None, forDeviceGrants);
    }

    private static async Task InsertActivityAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? actorId, string action,
        Guid? projectId, Guid? deviceId, Guid? pairingId, Guid? objectId, long? version, Guid? requestId, string? detail,
        CoreActivityRequest? source, string result, bool diagnostic, CancellationToken cancellation, bool forDeviceGrants = false)
    {
        // Device activation/retirement takes one snapshot of the affected grants in its business transaction.
        var scopes = forDeviceGrants ? "SELECT project_id,access_mode FROM projecthub.device_grants WHERE device_id=$4::uuid"
            : "SELECT NULL::uuid AS project_id,NULL::text AS access_mode";
        await using var cmd = new NpgsqlCommand($$"""
            INSERT INTO projecthub.audit_log(actor_id,action,project_id,device_id,pairing_id,object_id,object_version,
                request_id,detail,client_name,client_version,entry,source_ip,trace_id,origin_at,is_replay,result,is_diagnostic,match_count)
            SELECT $1::uuid,$2,coalesce($3::uuid,scope.project_id),$4::uuid,$5::uuid,$6::uuid,$7::bigint,$8::uuid,
                coalesce($9::text,scope.access_mode),$10::text,$11::text,$12::text,$13::inet,$14::text,$15::timestamptz,$16,$17,$18,$19::int
            FROM ({{scopes}}) scope
            """, conn, tx);
        object?[] values = [actorId, action, projectId, deviceId, pairingId, objectId, version, requestId, detail,
            source?.Client, source?.ClientVersion, source?.Entry, source?.Ip, source?.Trace, source?.OriginAt, source?.Replay ?? false,
            result, diagnostic, source?.MatchCount];
        foreach (var value in values) cmd.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(cancellation);
    }

    private void CaptureObject(Guid? id, long? version = null)
    {
        if (Activity is not { } activity) return;
        activity.ObjectId = id;
        activity.ObjectVersion = version;
    }

    private void CaptureMatches(int count)
    {
        if (Activity is { } activity) activity.MatchCount = count;
    }

    private static Guid? WrittenObject(JsonElement record, string operation)
    {
        var property = operation switch
        {
            "knowledge.commit" => "itemId", "asset.publish" => "assetId", "feedback.submit" => "feedbackId",
            "endpoint.register" => "endpointId", "task.result" or "task.claim" => "taskId", _ => null
        };
        return property is not null && record.TryGetProperty(property, out var field) && field.TryGetGuid(out var id) ? id : null;
    }

    internal void ObserveAdminResource(Guid owner, Guid project, bool asset, Guid? subject, long? version, int? count, bool download = false)
    {
        if (Activity is not { Entry: "admin" } request) return;
        request.AdminId = owner;
        request.ProjectId = project;
        request.Action = download ? "asset.download" : (asset, subject.HasValue) switch
        {
            (true, true) => "asset.get", (true, false) => "asset.search", (false, true) => "knowledge.history", _ => "knowledge.search"
        };
        request.ObjectId = subject;
        request.ObjectVersion = version;
        request.MatchCount = subject.HasValue ? null : count;
    }

    internal async Task ObserveRequestAsync(HttpContext context, RequestDelegate next)
    {
        var observation = CoreActivityRequest.FromHttp(context, context.Request.Path.StartsWithSegments("/v1"));
        context.Features.Set(observation);
        try { await next(context); }
        catch
        {
            observation.Failure ??= "service_error";
            throw;
        }
        finally
        {
            var status = context.Response.StatusCode;
            var result = observation.Failure ?? (status >= 400 ? $"http_{status}" : "success");
            var diagnostic = observation.Action is not null && (result != "success" || observation.IsRead &&
                observation.Action is not ("workspace.list" or "grant.lookup"));
            if (diagnostic || observation.DeviceId.HasValue && result == "success")
                await CompleteObservationAsync(observation, result, diagnostic);
        }
    }

    private async Task CompleteObservationAsync(CoreActivityRequest source, string result, bool diagnostic)
    {
        if (!diagnosticSlots.Wait(0))
        {
            MarkMissed(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var conn = await db.OpenConnectionAsync(timeout.Token);
            if (diagnostic)
                await InsertActivityAsync(conn, null, source.AdminId ?? source.DeviceId, source.Action!, source.ProjectId, source.DeviceId,
                    null, source.ProjectId.HasValue ? source.ObjectId : null, source.ProjectId.HasValue ? source.ObjectVersion : null,
                    source.ProjectId.HasValue ? source.RequestId : null, result == "forbidden" ? source.AccessMode : null,
                    source, result, true, timeout.Token);
            if (source.DeviceId is Guid device && result == "success")
            {
                await using var seen = new NpgsqlCommand("""
                    UPDATE projecthub.devices SET last_contact_at=clock_timestamp() WHERE id=$1
                    """, conn);
                seen.Parameters.AddWithValue(device);
                await seen.ExecuteNonQueryAsync(timeout.Token);
            }
        }
        catch (Exception error) when (error is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            var now = DateTimeOffset.UtcNow;
            MarkMissed(now, now, 1);
        }
        finally { diagnosticSlots.Release(); }
    }

    private void MarkMissed(DateTimeOffset first, DateTimeOffset last, long count)
    {
        lock (coverageLock)
        {
            missed += count;
            if (!firstMissed.HasValue || first < firstMissed) firstMissed = first;
            if (!lastMissed.HasValue || last > lastMissed) lastMissed = last;
        }
    }

    public async Task<ActivityCoverage> GetActivityCoverageAsync()
    {
        await using var cmd = db.CreateCommand("SELECT started_at,missed_count,first_missed_at,last_missed_at FROM projecthub.activity_coverage");
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        lock (coverageLock)
        {
            DateTimeOffset? persistedFirst = r.IsDBNull(2) ? null : r.GetDateTime(2);
            DateTimeOffset? persistedLast = r.IsDBNull(3) ? null : r.GetDateTime(3);
            return new(r.GetDateTime(0), r.GetInt64(1) + missed, persistedFirst ?? firstMissed, lastMissed ?? persistedLast);
        }
    }

    internal async Task MaintainActivityAsync(bool expire, CancellationToken cancel)
    {
        long count;
        DateTimeOffset? first, last;
        lock (coverageLock)
        {
            (count, first, last) = (missed, firstMissed, lastMissed);
            (missed, firstMissed, lastMissed) = (0, null, null);
        }
        if (count > 0)
        {
            try
            {
                await using var persist = db.CreateCommand("""
                    UPDATE projecthub.activity_coverage SET missed_count=missed_count+$1,
                        first_missed_at=least(first_missed_at,$2),last_missed_at=greatest(last_missed_at,$3)
                    """);
                persist.Parameters.AddWithValue(count);
                persist.Parameters.AddWithValue(first!.Value);
                persist.Parameters.AddWithValue(last!.Value);
                await persist.ExecuteNonQueryAsync(cancel);
            }
            catch { MarkMissed(first!.Value, last!.Value, count); throw; }
        }
        if (!expire) return;
        await using var prune = db.CreateCommand("""
            UPDATE projecthub.audit_log SET source_ip=NULL WHERE source_ip IS NOT NULL AND occurred_at<now()-interval '7 days';
            DELETE FROM projecthub.audit_log WHERE is_diagnostic AND occurred_at<now()-interval '30 days';
            """);
        await prune.ExecuteNonQueryAsync(cancel);
    }
}

internal sealed class ActivityMaintenance(CoreStore store, ILogger<ActivityMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var nextExpiry = DateTimeOffset.MinValue;
        do
        {
            try
            {
                var expire = DateTimeOffset.UtcNow >= nextExpiry;
                await store.MaintainActivityAsync(expire, stoppingToken);
                if (expire) nextExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
            }
            catch (Exception e) when (e is NpgsqlException or TimeoutException)
            {
                logger.LogWarning("活动记录维护暂时不可用：{Type}", e.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
