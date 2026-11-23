using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ProjectHub.Core;

public sealed partial class CoreStore
{
    private const string DeviceUpdateSchemaSql = """
        ALTER TABLE projecthub.devices
            ADD COLUMN replaces_device_id uuid UNIQUE REFERENCES projecthub.devices(id);
        ALTER TABLE projecthub.device_pairings
            ADD COLUMN target_device_id uuid REFERENCES projecthub.devices(id),
            ADD COLUMN claim_digest bytea,
            ADD COLUMN claim_nonce varchar(128),
            ADD COLUMN claim_label varchar(120),
            ADD COLUMN candidate_device_id uuid,
            ADD COLUMN candidate_edge_id uuid,
            ADD COLUMN confirmed_access_expiry timestamptz;
        CREATE UNIQUE INDEX pending_device_update ON projecthub.device_pairings(target_device_id)
            WHERE target_device_id IS NOT NULL AND consumed_at IS NULL AND revoked_at IS NULL;
        ALTER TABLE projecthub.pairing_grants ADD COLUMN source_binding_id uuid;
        """;

    public async Task<PairingOffer> IssueDeviceUpdateAsync(Guid ownerId, long sessionVersion, Guid deviceId, Guid requestId,
        bool checkPending = false, Guid? expectedPairingId = null)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("更新设备需要有效的请求标识。");
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await RequireOwnerSessionAsync(conn, tx, ownerId, sessionVersion);
        await using var device = new NpgsqlCommand("""
            SELECT label FROM projecthub.devices WHERE id=$1 AND owner_id=$2 AND revoked_at IS NULL FOR UPDATE
            """, conn, tx);
        device.Parameters.AddWithValue(deviceId);
        device.Parameters.AddWithValue(ownerId);
        if (await device.ExecuteScalarAsync() is not string label)
            throw new KeyNotFoundException("设备已撤销或被接替。");
        if (checkPending)
        {
            await using var pending = new NpgsqlCommand("""
                SELECT id FROM projecthub.device_pairings WHERE target_device_id=$1 AND revoked_at IS NULL
                    AND consumed_at IS NULL AND expires_at>clock_timestamp()
                """, conn, tx);
            pending.Parameters.AddWithValue(deviceId);
            if ((await pending.ExecuteScalarAsync() as Guid?) != expectedPairingId)
                throw new PairingAlreadyIssuedException();
        }
        await CancelDeviceUpdatesAsync(conn, tx, deviceId);
        var pairingId = Guid.NewGuid();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await using var issue = new NpgsqlCommand("""
            INSERT INTO projecthub.device_pairings(id,request_id,owner_id,label,code_digest,expires_at,target_device_id)
            VALUES ($1,$2,$3,$4,$5,clock_timestamp() + interval '10 minutes',$6)
            ON CONFLICT (owner_id,request_id) DO NOTHING RETURNING expires_at
            """, conn, tx);
        issue.Parameters.AddWithValue(pairingId);
        issue.Parameters.AddWithValue(requestId);
        issue.Parameters.AddWithValue(ownerId);
        issue.Parameters.AddWithValue(label);
        issue.Parameters.AddWithValue(TokenDigest(secret));
        issue.Parameters.AddWithValue(deviceId);
        if (await issue.ExecuteScalarAsync() is not DateTime expiry)
            throw new PairingAlreadyIssuedException(); // Rolls back cancellation of the original invitation.
        await using var snapshot = new NpgsqlCommand("""
            INSERT INTO projecthub.pairing_grants(pairing_id,project_id,access_mode,source_binding_id)
            SELECT $1,project_id,access_mode,binding_id FROM projecthub.device_grants WHERE device_id=$2
            """, conn, tx);
        snapshot.Parameters.AddWithValue(pairingId);
        snapshot.Parameters.AddWithValue(deviceId);
        await snapshot.ExecuteNonQueryAsync();
        await AuditAsync(conn, tx, ownerId, "device.update_issued", deviceId: deviceId, pairingId: pairingId);
        await tx.CommitAsync();
        return new(pairingId, secret, expiry);
    }

    private static async Task CancelDeviceUpdatesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid deviceId)
    {
        await using var cancel = new NpgsqlCommand("""
            UPDATE projecthub.device_pairings SET revoked_at=clock_timestamp(),code_digest=NULL
            WHERE target_device_id=$1 AND revoked_at IS NULL AND consumed_at IS NULL
            """, conn, tx);
        cancel.Parameters.AddWithValue(deviceId);
        await cancel.ExecuteNonQueryAsync();
    }

    // Edge keeps a random per-attempt secret in its protected pending state. Credentials can be
    // reissued identically after a lost response without storing bearer or refresh tokens in Core.
    private static DeviceTokens CandidateTokens(Guid pairingId, Guid deviceId, Guid edgeId, string attemptSecret, DateTime expiry)
    {
        var key = Convert.FromHexString(attemptSecret);
        string Derive(string purpose) => Convert.ToHexString(HMACSHA256.HashData(key,
            Encoding.ASCII.GetBytes($"nodicarta-device-update-v1/{pairingId:D}/{purpose}")));
        return new(deviceId, edgeId, Derive("access"), Derive("refresh"), expiry);
    }

    internal async Task<DeviceTokens?> ClaimDeviceUpdateAsync(Guid pairingId, string oneTimeCode, string deviceLabel,
        string instanceNonce, string attemptSecret)
    {
        var digest = TokenDigest(attemptSecret.ToUpperInvariant());
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var read = new NpgsqlCommand("""
            SELECT p.target_device_id,p.expires_at,p.claim_digest,p.claim_nonce,p.candidate_device_id,p.candidate_edge_id
            FROM projecthub.device_pairings p JOIN projecthub.devices d ON d.id=p.target_device_id
            WHERE p.id=$1 AND p.code_digest=$2 AND p.revoked_at IS NULL AND p.consumed_at IS NULL
                AND p.expires_at>clock_timestamp() AND d.revoked_at IS NULL FOR UPDATE OF p
            """, conn, tx);
        read.Parameters.AddWithValue(pairingId);
        read.Parameters.AddWithValue(TokenDigest(oneTimeCode.ToUpperInvariant()));
        Guid target, candidateId, edgeId;
        DateTime expiry;
        await using (var reader = await read.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
                return null;
            target = reader.GetGuid(0);
            expiry = reader.GetDateTime(1);
            if (!reader.IsDBNull(2))
            {
                if (!CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte[]>(2), digest)
                    || reader.GetString(3) != instanceNonce)
                    return null;
                return CandidateTokens(pairingId, reader.GetGuid(4), reader.GetGuid(5), attemptSecret, expiry);
            }
            candidateId = Guid.NewGuid();
            edgeId = Guid.NewGuid();
        }
        await using var claim = new NpgsqlCommand("""
            UPDATE projecthub.device_pairings SET claim_digest=$2,claim_nonce=$3,claim_label=$4,
                candidate_device_id=$5,candidate_edge_id=$6 WHERE id=$1
            """, conn, tx);
        claim.Parameters.AddWithValue(pairingId);
        claim.Parameters.AddWithValue(digest);
        claim.Parameters.AddWithValue(instanceNonce);
        claim.Parameters.AddWithValue(deviceLabel);
        claim.Parameters.AddWithValue(candidateId);
        claim.Parameters.AddWithValue(edgeId);
        await claim.ExecuteNonQueryAsync();
        await AuditAsync(conn, tx, candidateId, "device.update_claimed", deviceId: target, pairingId: pairingId);
        await tx.CommitAsync();
        return CandidateTokens(pairingId, candidateId, edgeId, attemptSecret, expiry);
    }

    internal async Task<DeviceTokens?> ConfirmDeviceUpdateAsync(Guid pairingId, string attemptSecret)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // The same lock order as the administrator's reissue, revoke and grant operations.
        await using var target = new NpgsqlCommand("""
            SELECT d.id FROM projecthub.devices d JOIN projecthub.device_pairings p ON p.target_device_id=d.id
            WHERE p.id=$1 AND p.claim_digest=$2 AND p.revoked_at IS NULL FOR UPDATE OF d
            """, conn, tx);
        target.Parameters.AddWithValue(pairingId);
        target.Parameters.AddWithValue(TokenDigest(attemptSecret.ToUpperInvariant()));
        if (await target.ExecuteScalarAsync() is not Guid oldId)
            return null;
        await using var read = new NpgsqlCommand("""
            SELECT p.candidate_device_id,p.candidate_edge_id,p.expires_at,p.confirmed_access_expiry,
                p.expires_at>clock_timestamp() AND d.revoked_at IS NULL
            FROM projecthub.device_pairings p JOIN projecthub.devices d ON d.id=p.target_device_id
            WHERE p.id=$1 AND p.revoked_at IS NULL FOR UPDATE OF p
            """, conn, tx);
        read.Parameters.AddWithValue(pairingId);
        DeviceTokens candidate;
        bool confirmed;
        await using (var reader = await read.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
                return null;
            confirmed = !reader.IsDBNull(3);
            if (!confirmed && !reader.GetBoolean(4))
                return null;
            candidate = CandidateTokens(pairingId, reader.GetGuid(0), reader.GetGuid(1), attemptSecret,
                reader.GetDateTime(confirmed ? 3 : 2));
        }
        if (confirmed)
        {
            // A lost response can be recovered after the invitation or access token expires. Once
            // refresh rotation has begun, handing the old pair out again could trigger replay revocation.
            await using var current = new NpgsqlCommand("""
                SELECT 1 FROM projecthub.devices d JOIN projecthub.refresh_tokens r ON r.device_id=d.id
                WHERE d.id=$1 AND d.revoked_at IS NULL AND r.digest=$2 AND r.used_at IS NULL AND r.expires_at>clock_timestamp()
                """, conn, tx);
            current.Parameters.AddWithValue(candidate.DeviceId);
            current.Parameters.AddWithValue(TokenDigest(candidate.RefreshToken));
            return await current.ExecuteScalarAsync() is null ? null : candidate;
        }

        await using var create = new NpgsqlCommand("""
            INSERT INTO projecthub.devices(id,edge_id,owner_id,label,reported_label,instance_nonce,replaces_device_id)
            SELECT candidate_device_id,candidate_edge_id,owner_id,label,claim_label,claim_nonce,target_device_id
            FROM projecthub.device_pairings WHERE id=$1
            """, conn, tx);
        create.Parameters.AddWithValue(pairingId);
        await create.ExecuteNonQueryAsync();
        await using var grants = new NpgsqlCommand("""
            INSERT INTO projecthub.device_grants(device_id,project_id,access_mode)
            SELECT $1,g.project_id,CASE WHEN g.access_mode='readOnly' OR s.access_mode='readOnly' THEN 'readOnly' ELSE 'readWrite' END
            FROM projecthub.pairing_grants s JOIN projecthub.device_grants g
                ON g.binding_id=s.source_binding_id AND g.project_id=s.project_id AND g.device_id=$2
            WHERE s.pairing_id=$3
            """, conn, tx);
        grants.Parameters.AddWithValue(candidate.DeviceId);
        grants.Parameters.AddWithValue(oldId);
        grants.Parameters.AddWithValue(pairingId);
        await grants.ExecuteNonQueryAsync();
        await AuditAsync(conn, tx, candidate.DeviceId, "grant.transferred", deviceId: candidate.DeviceId, pairingId: pairingId,
            objectId: oldId, forDeviceGrants: true);
        var active = await IssueTokensAsync(conn, tx, candidate.DeviceId, candidate.EdgeId, candidate.AccessToken, candidate.RefreshToken);
        await using var retire = new NpgsqlCommand("""
            WITH completed AS (
                UPDATE projecthub.device_pairings SET consumed_at=clock_timestamp(),code_digest=NULL,
                    redeemed_device_id=$2,confirmed_access_expiry=$3 WHERE id=$1 RETURNING target_device_id)
            UPDATE projecthub.devices SET revoked_at=clock_timestamp(),access_digest=NULL WHERE id IN (SELECT target_device_id FROM completed)
            """, conn, tx);
        retire.Parameters.AddWithValue(pairingId);
        retire.Parameters.AddWithValue(candidate.DeviceId);
        retire.Parameters.AddWithValue(active.ExpiresAt);
        await retire.ExecuteNonQueryAsync();
        await AuditAsync(conn, tx, candidate.DeviceId, "device.update_confirmed", deviceId: candidate.DeviceId, pairingId: pairingId);
        await AuditAsync(conn, tx, candidate.DeviceId, "device.superseded", deviceId: oldId, pairingId: pairingId);
        await tx.CommitAsync();
        return active;
    }
}
