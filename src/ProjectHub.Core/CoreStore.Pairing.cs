using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ProjectHub.Core;

public sealed record DeviceGrantInput(Guid ProjectId, string AccessMode);
public sealed record PairingOffer(Guid PairingId, string Code, DateTimeOffset ExpiresAt);
public sealed record PendingPairing(Guid PairingId, string Label, DateTimeOffset ExpiresAt, Guid? TargetDeviceId = null, bool Claimed = false);
public sealed record DeviceTokens(Guid DeviceId, Guid EdgeId, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
public sealed class PairingAlreadyIssuedException() : InvalidOperationException("这份表单已签发过配对码，请检查待配对列表。");

public sealed partial class CoreStore
{
    private const string DeviceSchemaSql = """
        CREATE TABLE projecthub.devices (
            id uuid PRIMARY KEY,
            edge_id uuid NOT NULL UNIQUE,
            owner_id uuid NOT NULL REFERENCES projecthub.owner_account(id),
            label varchar(120) NOT NULL,
            reported_label varchar(120) NOT NULL,
            instance_nonce varchar(128) NOT NULL,
            grant_revision bigint NOT NULL DEFAULT 0,
            access_digest bytea UNIQUE,
            access_expires_at timestamptz,
            revoked_at timestamptz
        );
        CREATE TABLE projecthub.refresh_tokens (
            digest bytea PRIMARY KEY,
            device_id uuid NOT NULL REFERENCES projecthub.devices(id),
            expires_at timestamptz NOT NULL,
            used_at timestamptz
        );
        CREATE INDEX refresh_tokens_device ON projecthub.refresh_tokens(device_id);
        CREATE TABLE projecthub.device_pairings (
            id uuid PRIMARY KEY,
            request_id uuid NOT NULL,
            owner_id uuid NOT NULL REFERENCES projecthub.owner_account(id),
            label varchar(120) NOT NULL,
            code_digest bytea,
            expires_at timestamptz NOT NULL,
            consumed_at timestamptz,
            revoked_at timestamptz,
            redeemed_device_id uuid REFERENCES projecthub.devices(id),
            UNIQUE (owner_id, request_id)
        );
        CREATE TABLE projecthub.pairing_grants (
            pairing_id uuid NOT NULL REFERENCES projecthub.device_pairings(id),
            project_id uuid NOT NULL,
            access_mode text NOT NULL CHECK (access_mode IN ('readOnly','readWrite')),
            PRIMARY KEY (pairing_id, project_id)
        );
        CREATE TABLE projecthub.device_grants (
            binding_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            device_id uuid NOT NULL REFERENCES projecthub.devices(id),
            project_id uuid NOT NULL REFERENCES projecthub.projects(id),
            access_mode text NOT NULL CHECK (access_mode IN ('readOnly','readWrite')),
            version bigint NOT NULL DEFAULT 1,
            UNIQUE (device_id, project_id)
        );
        ALTER TABLE projecthub.audit_log ADD COLUMN device_id uuid, ADD COLUMN pairing_id uuid;
        """;

    private static byte[] TokenDigest(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static async Task RequireOwnerSessionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid ownerId, long sessionVersion)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id FROM projecthub.owner_account WHERE id = $1 AND session_version = $2 FOR SHARE
            """, conn, tx);
        cmd.Parameters.AddWithValue(ownerId);
        cmd.Parameters.AddWithValue(sessionVersion);
        if (await cmd.ExecuteScalarAsync() is null)
            throw new UnauthorizedAccessException("管理会话已失效。");
    }

    public async Task<PairingOffer> IssuePairingAsync(Guid ownerId, long sessionVersion, string label,
        IReadOnlyList<DeviceGrantInput> grants, Guid requestId)
    {
        if (!IsValidName(label, 120) || requestId == Guid.Empty || grants is null)
            throw new ArgumentException("设备名称或请求标识无效。");
        var selectedProjects = new HashSet<Guid>();
        foreach (var grant in grants)
        {
            if (grant is null || grant.ProjectId == Guid.Empty || grant.AccessMode is not ("readOnly" or "readWrite")
                || !selectedProjects.Add(grant.ProjectId))
                throw new ArgumentException("项目或访问权限无效，或项目重复。");
        }

        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await RequireOwnerSessionAsync(conn, tx, ownerId, sessionVersion);
        var pairingId = Guid.NewGuid();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await using var issue = new NpgsqlCommand("""
            INSERT INTO projecthub.device_pairings(id,request_id,owner_id,label,code_digest,expires_at)
            VALUES ($1,$2,$3,$4,$5,clock_timestamp() + interval '10 minutes')
            ON CONFLICT (owner_id,request_id) DO NOTHING RETURNING expires_at
            """, conn, tx);
        issue.Parameters.AddWithValue(pairingId);
        issue.Parameters.AddWithValue(requestId);
        issue.Parameters.AddWithValue(ownerId);
        issue.Parameters.AddWithValue(label.Trim());
        issue.Parameters.AddWithValue(TokenDigest(secret));
        if (await issue.ExecuteScalarAsync() is not DateTime expiry)
            throw new PairingAlreadyIssuedException();
        await using var snapshot = new NpgsqlCommand("""
            INSERT INTO projecthub.pairing_grants(pairing_id,project_id,access_mode)
            SELECT $1,p.id,g.mode FROM unnest($2::uuid[],$3::text[]) AS g(id,mode)
            JOIN projecthub.projects p ON p.id=g.id AND p.owner_id=$4
            """, conn, tx);
        snapshot.Parameters.AddWithValue(pairingId);
        snapshot.Parameters.AddWithValue(grants.Select(g => g.ProjectId).ToArray());
        snapshot.Parameters.AddWithValue(grants.Select(g => g.AccessMode).ToArray());
        snapshot.Parameters.AddWithValue(ownerId);
        if (await snapshot.ExecuteNonQueryAsync() != grants.Count)
            throw new ArgumentException("所选项目不属于当前管理员。");
        await AuditAsync(conn, tx, ownerId, "device.pairing_issued", pairingId: pairingId);
        await tx.CommitAsync();
        return new(pairingId, secret, expiry);
    }

    public async Task<IReadOnlyList<PendingPairing>> ListPendingPairingsAsync(Guid ownerId)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id,label,expires_at,target_device_id,claim_digest IS NOT NULL FROM projecthub.device_pairings
            WHERE owner_id=$1 AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()
            ORDER BY expires_at DESC,id
            """);
        cmd.Parameters.AddWithValue(ownerId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<PendingPairing>();
        while (await reader.ReadAsync())
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetBoolean(4)));
        return result;
    }

    public async Task<DeviceTokens?> RedeemPairingAsync(Guid pairingId, string code, string deviceLabel, string instanceNonce)
    {
        if (deviceLabel is null || deviceLabel.Length > 120 || deviceLabel.Any(char.IsControl) || !IsValidName(instanceNonce, 128))
            throw new ArgumentException("设备自报名称或实例标识无效。");
        if (pairingId == Guid.Empty || code?.Length != 32)
            return null;

        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var consume = new NpgsqlCommand("""
            UPDATE projecthub.device_pairings s SET code_digest=NULL,consumed_at=clock_timestamp()
            WHERE s.id=$1 AND s.target_device_id IS NULL AND s.code_digest=$2 AND s.revoked_at IS NULL AND s.consumed_at IS NULL
              AND s.expires_at>clock_timestamp() AND NOT EXISTS (
                  SELECT 1 FROM projecthub.pairing_grants g LEFT JOIN projecthub.projects p
                  ON p.id=g.project_id AND p.owner_id=s.owner_id WHERE g.pairing_id=s.id AND p.id IS NULL)
            RETURNING s.owner_id,s.label
            """, conn, tx);
        consume.Parameters.AddWithValue(pairingId);
        consume.Parameters.AddWithValue(TokenDigest(code));
        Guid ownerId;
        string label;
        await using (var reader = await consume.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
                return null;
            ownerId = reader.GetGuid(0);
            label = reader.GetString(1);
        }
        var deviceId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        await using var create = new NpgsqlCommand("""
            INSERT INTO projecthub.devices(id,edge_id,owner_id,label,reported_label,instance_nonce)
            VALUES ($1,$2,$3,$4,$5,$6)
            """, conn, tx);
        create.Parameters.AddWithValue(deviceId);
        create.Parameters.AddWithValue(edgeId);
        create.Parameters.AddWithValue(ownerId);
        create.Parameters.AddWithValue(label);
        create.Parameters.AddWithValue(deviceLabel.Trim());
        create.Parameters.AddWithValue(instanceNonce.Trim());
        await create.ExecuteNonQueryAsync();
        await using var bind = new NpgsqlCommand("""
            WITH redeemed AS (
                UPDATE projecthub.device_pairings SET redeemed_device_id=$1 WHERE id=$2 RETURNING id)
            INSERT INTO projecthub.device_grants(device_id,project_id,access_mode)
            SELECT $1,g.project_id,g.access_mode FROM projecthub.pairing_grants g JOIN redeemed r ON r.id=g.pairing_id
            """, conn, tx);
        bind.Parameters.AddWithValue(deviceId);
        bind.Parameters.AddWithValue(pairingId);
        await bind.ExecuteNonQueryAsync();
        var tokens = await IssueTokensAsync(conn, tx, deviceId, edgeId);
        await AuditAsync(conn, tx, deviceId, "device.paired", deviceId: deviceId, pairingId: pairingId);
        await AuditAsync(conn, tx, deviceId, "grant.activated", deviceId: deviceId, pairingId: pairingId, forDeviceGrants: true);
        await tx.CommitAsync();
        return tokens;
    }

    private static async Task<DeviceTokens> IssueTokensAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid deviceId, Guid edgeId,
        string? access = null, string? refresh = null)
    {
        access ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        refresh ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var cmd = new NpgsqlCommand("""
            WITH refresh AS (
                INSERT INTO projecthub.refresh_tokens(digest,device_id,expires_at)
                VALUES ($1,$2,clock_timestamp() + interval '30 days'))
            UPDATE projecthub.devices SET access_digest=$3,access_expires_at=clock_timestamp() + interval '15 minutes',
                last_contact_at=clock_timestamp()
            WHERE id=$2 RETURNING access_expires_at
            """, conn, tx);
        cmd.Parameters.AddWithValue(TokenDigest(refresh));
        cmd.Parameters.AddWithValue(deviceId);
        cmd.Parameters.AddWithValue(TokenDigest(access));
        var expiry = (DateTime)(await cmd.ExecuteScalarAsync())!;
        return new(deviceId, edgeId, access, refresh, expiry);
    }

    private static async Task<Guid?> LockDeviceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string accessToken)
    {
        if (accessToken?.Length != 64)
            return null;
        await using var cmd = new NpgsqlCommand("""
            SELECT id FROM projecthub.devices WHERE access_digest=$1 AND revoked_at IS NULL
            AND access_expires_at>clock_timestamp() FOR SHARE
            """, conn, tx);
        cmd.Parameters.AddWithValue(TokenDigest(accessToken));
        return (Guid?)await cmd.ExecuteScalarAsync();
    }

    public async Task<DeviceTokens?> RefreshDeviceAsync(string refreshToken)
    {
        if (refreshToken?.Length != 64)
            return null;
        var digest = TokenDigest(refreshToken);
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // Serialize rotation, replay detection, and revocation against authorized device operations.
        await using var find = new NpgsqlCommand("""
            SELECT d.id,d.edge_id FROM projecthub.refresh_tokens r JOIN projecthub.devices d ON d.id=r.device_id
            WHERE r.digest=$1 AND r.expires_at>clock_timestamp() AND d.revoked_at IS NULL FOR UPDATE OF d
            """, conn, tx);
        find.Parameters.AddWithValue(digest);
        Guid deviceId;
        Guid edgeId;
        await using (var reader = await find.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
                return null;
            deviceId = reader.GetGuid(0);
            edgeId = reader.GetGuid(1);
        }
        await using var used = new NpgsqlCommand("""
            UPDATE projecthub.refresh_tokens SET used_at=clock_timestamp() WHERE digest=$1 AND used_at IS NULL
            """, conn, tx);
        used.Parameters.AddWithValue(digest);
        if (await used.ExecuteNonQueryAsync() == 0)
        {
            await using var quarantine = new NpgsqlCommand("""
                UPDATE projecthub.devices SET revoked_at=clock_timestamp(),access_digest=NULL WHERE id=$1
                """, conn, tx);
            quarantine.Parameters.AddWithValue(deviceId);
            await quarantine.ExecuteNonQueryAsync();
            await CancelDeviceUpdatesAsync(conn, tx, deviceId);
            await AuditAsync(conn, tx, deviceId, "device.refresh_reuse", deviceId: deviceId);
            await AuditAsync(conn, tx, deviceId, "grant.device_revoked", deviceId: deviceId, forDeviceGrants: true);
            await tx.CommitAsync();
            return null;
        }
        // Only still-valid spent hashes are needed to identify reuse.
        await using var prune = new NpgsqlCommand("""
            DELETE FROM projecthub.refresh_tokens WHERE device_id=$1 AND expires_at<=clock_timestamp()
            """, conn, tx);
        prune.Parameters.AddWithValue(deviceId);
        await prune.ExecuteNonQueryAsync();
        var tokens = await IssueTokensAsync(conn, tx, deviceId, edgeId);
        await AuditAsync(conn, tx, deviceId, "device.tokens_rotated", deviceId: deviceId);
        await tx.CommitAsync();
        return tokens;
    }

    public async Task<bool> RevokeDeviceAsync(Guid ownerId, long sessionVersion, Guid deviceId)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await RequireOwnerSessionAsync(conn, tx, ownerId, sessionVersion);
        await using var cmd = new NpgsqlCommand("""
            UPDATE projecthub.devices SET revoked_at=clock_timestamp(),access_digest=NULL
            WHERE id=$1 AND owner_id=$2 AND revoked_at IS NULL
            """, conn, tx);
        cmd.Parameters.AddWithValue(deviceId);
        cmd.Parameters.AddWithValue(ownerId);
        var changed = await cmd.ExecuteNonQueryAsync() == 1;
        if (changed)
        {
            await CancelDeviceUpdatesAsync(conn, tx, deviceId);
            await AuditAsync(conn, tx, ownerId, "device.revoked", deviceId: deviceId);
            await AuditAsync(conn, tx, ownerId, "grant.device_revoked", deviceId: deviceId, forDeviceGrants: true);
        }
        else
        {
            await using var known = new NpgsqlCommand("SELECT 1 FROM projecthub.devices WHERE id=$1 AND owner_id=$2", conn, tx);
            known.Parameters.AddWithValue(deviceId);
            known.Parameters.AddWithValue(ownerId);
            if (await known.ExecuteScalarAsync() is null)
                return false;
        }
        await tx.CommitAsync();
        return true;
    }

    public async Task<bool> RevokePairingAsync(Guid ownerId, long sessionVersion, Guid pairingId)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await RequireOwnerSessionAsync(conn, tx, ownerId, sessionVersion);
        await using var cmd = new NpgsqlCommand("""
            UPDATE projecthub.device_pairings SET revoked_at=clock_timestamp(),code_digest=NULL
            WHERE id=$1 AND owner_id=$2 AND consumed_at IS NULL AND revoked_at IS NULL
            """, conn, tx);
        cmd.Parameters.AddWithValue(pairingId);
        cmd.Parameters.AddWithValue(ownerId);
        if (await cmd.ExecuteNonQueryAsync() == 1)
            await AuditAsync(conn, tx, ownerId, "device.pairing_revoked", pairingId: pairingId);
        else
        {
            await using var known = new NpgsqlCommand("""
                SELECT 1 FROM projecthub.device_pairings WHERE id=$1 AND owner_id=$2 AND revoked_at IS NOT NULL
                """, conn, tx);
            known.Parameters.AddWithValue(pairingId);
            known.Parameters.AddWithValue(ownerId);
            if (await known.ExecuteScalarAsync() is null)
                return false;
        }
        await tx.CommitAsync();
        return true;
    }
}
