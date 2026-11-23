using Npgsql;
using NpgsqlTypes;

namespace ProjectHub.Core;

public sealed record ProjectGrant(Guid ProjectId, string Name, Guid BindingId, string AccessMode, long Version);
public sealed record ManagedDevice(Guid DeviceId, Guid EdgeId, string Label, bool Revoked, long GrantRevision, IReadOnlyList<ProjectGrant> Grants,
    Guid? ReplacesDeviceId = null, Guid? ReplacedByDeviceId = null);
public sealed record DeviceProject(Guid ProjectId, string Name, long Version, Guid BindingId, string AccessMode, long GrantVersion);
public sealed class GrantConflictException() : InvalidOperationException("项目授权已变化，请刷新后再修改。");
internal sealed class DeviceApiError(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed partial class CoreStore
{
    // References belong to the device and project so that removing a grant preserves registered data.
    private const string WorkspaceSchemaSql = """
        CREATE TABLE projecthub.workspace_registrations (
            device_id uuid NOT NULL REFERENCES projecthub.devices(id),
            project_id uuid NOT NULL REFERENCES projecthub.projects(id),
            workspace_id uuid NOT NULL,
            request_id uuid NOT NULL,
            original_binding_id uuid NOT NULL,
            PRIMARY KEY (device_id, project_id, workspace_id),
            UNIQUE (device_id, project_id, request_id)
        );
        """;

    public async Task<IReadOnlyList<ManagedDevice>> ListDevicesAsync(Guid ownerId)
    {
        await using var cmd = db.CreateCommand("""
            SELECT d.id, d.edge_id, d.label, d.revoked_at IS NOT NULL,
                   p.id, p.name, g.binding_id, g.access_mode, g.version, d.grant_revision, d.replaces_device_id, successor.id
            FROM projecthub.devices d
            LEFT JOIN projecthub.devices successor ON successor.replaces_device_id=d.id
            LEFT JOIN projecthub.device_grants g ON g.device_id = d.id
            LEFT JOIN projecthub.projects p ON p.id = g.project_id AND p.owner_id = d.owner_id
            WHERE d.owner_id = $1 ORDER BY d.label, d.id, p.name, p.id
            """);
        cmd.Parameters.AddWithValue(ownerId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var devices = new List<ManagedDevice>();
        List<ProjectGrant> grants = [];
        while (await reader.ReadAsync())
        {
            var deviceId = reader.GetGuid(0);
            if (devices.Count == 0 || devices[^1].DeviceId != deviceId)
            {
                grants = [];
                devices.Add(new(deviceId, reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt64(9), grants,
                    reader.IsDBNull(10) ? null : reader.GetGuid(10), reader.IsDBNull(11) ? null : reader.GetGuid(11)));
            }
            if (!reader.IsDBNull(4))
                grants.Add(new(reader.GetGuid(4), reader.GetString(5), reader.GetGuid(6), reader.GetString(7), reader.GetInt64(8)));
        }
        return devices;
    }

    public async Task ChangeGrantAsync(Guid ownerId, long sessionVersion, Guid deviceId, Guid projectId,
        string? accessMode, Guid? expectedBindingId, long expectedVersion)
    {
        if (accessMode is not (null or "readOnly" or "readWrite"))
            throw new ArgumentException("项目权限无效。");
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await RequireOwnerSessionAsync(conn, tx, ownerId, sessionVersion);
        await using var lockDevice = new NpgsqlCommand("""
            SELECT d.grant_revision FROM projecthub.devices d
            JOIN projecthub.projects p ON p.id = $3 AND p.owner_id = d.owner_id
            WHERE d.id = $1 AND d.owner_id = $2 AND d.revoked_at IS NULL FOR UPDATE OF d
            """, conn, tx);
        lockDevice.Parameters.AddWithValue(deviceId);
        lockDevice.Parameters.AddWithValue(ownerId);
        lockDevice.Parameters.AddWithValue(projectId);
        if (await lockDevice.ExecuteScalarAsync() is not long deviceRevision)
            throw new KeyNotFoundException("设备或项目不可用。");

        await using var find = new NpgsqlCommand("""
            SELECT binding_id, version, access_mode FROM projecthub.device_grants
            WHERE device_id = $1 AND project_id = $2
            """, conn, tx);
        find.Parameters.AddWithValue(deviceId);
        find.Parameters.AddWithValue(projectId);
        Guid? currentId = null;
        // A previously absent binding may already have been added and removed by another form.
        long currentVersion = deviceRevision;
        string? currentMode = null;
        await using (var reader = await find.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                currentId = reader.GetGuid(0);
                currentVersion = reader.GetInt64(1);
                currentMode = reader.GetString(2);
            }
        }
        if (currentId != expectedBindingId || currentVersion != expectedVersion)
            throw new GrantConflictException();
        if (currentMode == accessMode)
            return;
        await using var change = conn.CreateCommand();
        change.Transaction = tx;
        if (accessMode is null)
        {
            change.CommandText = "DELETE FROM projecthub.device_grants WHERE binding_id = $1";
            change.Parameters.AddWithValue(currentId!.Value);
        }
        else if (currentId is null)
        {
            change.CommandText = """
                INSERT INTO projecthub.device_grants(binding_id, device_id, project_id, access_mode)
                VALUES ($1, $2, $3, $4)
                """;
            change.Parameters.AddWithValue(Guid.NewGuid());
            change.Parameters.AddWithValue(deviceId);
            change.Parameters.AddWithValue(projectId);
            change.Parameters.AddWithValue(accessMode);
        }
        else
        {
            change.CommandText = """
                UPDATE projecthub.device_grants SET access_mode = $2, version = version + 1
                WHERE binding_id = $1
                """;
            change.Parameters.AddWithValue(currentId.Value);
            change.Parameters.AddWithValue(accessMode);
        }
        await change.ExecuteNonQueryAsync();
        await using var revision = new NpgsqlCommand("""
            UPDATE projecthub.devices SET grant_revision = grant_revision + 1 WHERE id = $1
            """, conn, tx);
        revision.Parameters.AddWithValue(deviceId);
        await revision.ExecuteNonQueryAsync();
        await AuditAsync(conn, tx, ownerId, accessMode is null ? "grant.removed" : "grant." + accessMode, projectId, deviceId,
            detail: (currentMode ?? "none") + " → " + (accessMode ?? "none"));
        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<DeviceProject>> ReadDeviceProjectsAsync(string accessToken, Guid? projectId)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var deviceId = await RequireDeviceAsync(conn, tx, accessToken);
        await using var cmd = new NpgsqlCommand("""
            SELECT p.id, p.name, p.version, g.binding_id, g.access_mode, g.version
            FROM projecthub.device_grants g JOIN projecthub.projects p ON g.project_id = p.id
            WHERE g.device_id = $1 AND ($2::uuid IS NULL OR p.id = $2)
            ORDER BY p.id
            """, conn, tx);
        cmd.Parameters.AddWithValue(deviceId);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)projectId ?? DBNull.Value });
        await using var reader = await cmd.ExecuteReaderAsync();
        var entries = new List<DeviceProject>();
        while (await reader.ReadAsync())
            entries.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetGuid(3), reader.GetString(4), reader.GetInt64(5)));
        if (projectId.HasValue && entries.Count == 0)
            throw UnknownResource();
        if (projectId.HasValue && Activity is { } request) request.ProjectId = projectId;
        return entries;
    }

    internal async Task<IReadOnlyList<Guid>> ReadWorkspaceReferencesAsync(string token, Guid projectId, Guid bindingId)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var deviceId = await RequireWorkspaceAccessAsync(conn, tx, token, projectId, bindingId, write: false);
        await using var cmd = new NpgsqlCommand("""
            SELECT workspace_id FROM projecthub.workspace_registrations
            WHERE device_id = $1 AND project_id = $2 ORDER BY workspace_id
            """, conn, tx);
        cmd.Parameters.AddWithValue(deviceId);
        cmd.Parameters.AddWithValue(projectId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return ids;
    }

    internal async Task<bool> RegisterWorkspaceAsync(string token, Guid projectId, Guid bindingId,
        Guid workspaceId, Guid requestId)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var deviceId = await RequireWorkspaceAccessAsync(conn, tx, token, projectId, bindingId, write: true);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO projecthub.workspace_registrations(device_id, project_id, workspace_id, request_id, original_binding_id)
            VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING
            """, conn, tx);
        insert.Parameters.AddWithValue(deviceId);
        insert.Parameters.AddWithValue(projectId);
        insert.Parameters.AddWithValue(workspaceId);
        insert.Parameters.AddWithValue(requestId);
        insert.Parameters.AddWithValue(bindingId);
        if (await insert.ExecuteNonQueryAsync() == 1)
        {
            await AuditAsync(conn, tx, deviceId, "workspace.registered", projectId, deviceId,
                objectId: workspaceId, requestId: requestId);
            await tx.CommitAsync();
            return true;
        }
        await using var prior = new NpgsqlCommand("""
            SELECT workspace_id, original_binding_id FROM projecthub.workspace_registrations
            WHERE device_id = $1 AND project_id = $2 AND request_id = $3
            """, conn, tx);
        prior.Parameters.AddWithValue(deviceId);
        prior.Parameters.AddWithValue(projectId);
        prior.Parameters.AddWithValue(requestId);
        await using var reader = await prior.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new DeviceApiError(409, "already_exists", "工作区已登记。");
        if (reader.GetGuid(0) != workspaceId || reader.GetGuid(1) != bindingId)
            throw new DeviceApiError(409, "idempotency_conflict", "请求标识已用于其他登记。");
        return false;
    }

    private async Task<Guid> RequireWorkspaceAccessAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        string token, Guid projectId, Guid bindingId, bool write)
    {
        var deviceId = await RequireDeviceAsync(conn, tx, token);
        await using var cmd = new NpgsqlCommand("""
            SELECT access_mode FROM projecthub.device_grants
            WHERE device_id = $1 AND project_id = $2 AND binding_id = $3 FOR SHARE
            """, conn, tx);
        cmd.Parameters.AddWithValue(deviceId);
        cmd.Parameters.AddWithValue(projectId);
        cmd.Parameters.AddWithValue(bindingId);
        if (await cmd.ExecuteScalarAsync() is not string mode)
            throw UnknownResource();
        if (Activity is { } request)
        {
            request.ProjectId = projectId;
            request.AccessMode = mode;
        }
        if (write && mode != "readWrite")
            throw new DeviceApiError(403, "forbidden", "此设备只有该项目的只读权限。");
        return deviceId;
    }

    private async Task<Guid> RequireDeviceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string token)
    {
        var id = await LockDeviceAsync(conn, tx, token) ?? throw new DeviceApiError(401, "unauthenticated", "设备凭证无效或已过期。");
        if (Activity is { } request) request.DeviceId = id;
        return id;
    }

    private static DeviceApiError UnknownResource() => new(404, "not_found", "找不到指定资源。");
}
