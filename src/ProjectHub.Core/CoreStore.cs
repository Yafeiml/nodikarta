using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace ProjectHub.Core;

public sealed record AdminIdentity(Guid Id, string Username, long SessionVersion);
public sealed record ProjectEntry(Guid ProjectId, string Name, long Version);
public sealed class ProjectRequestConflictException() : InvalidOperationException("同一请求标识已经用于其他项目名称。");

public sealed partial class CoreStore(NpgsqlDataSource db, IHttpContextAccessor? requests = null)
{
    private static readonly PasswordHasher<string> Hasher = new();
    private static readonly string DummyHash = Hasher.HashPassword("", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    public static bool IsValidPassword(string? password) => password is { Length: >= 12 and <= 128 };
    public static bool IsValidUsername(string? name) => IsValidName(name, 64);
    public static bool IsValidProjectName(string? name) => IsValidName(name, 120);

    private static bool IsValidName(string? value, int maxLength) =>
        value is not null && value.Trim().Length is > 0 && value.Trim().Length <= maxLength && !value.Any(char.IsControl);

    public async Task InitializeAsync()
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // Serialize schema creation even when two fresh processes start against an empty database.
        await using var prepare = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(1346917704, 1);
            CREATE SCHEMA IF NOT EXISTS projecthub;
            CREATE TABLE IF NOT EXISTS projecthub.schema_version(version integer PRIMARY KEY);
            """, conn, tx);
        await prepare.ExecuteNonQueryAsync();
        await using var read = new NpgsqlCommand("SELECT coalesce(max(version), 0) FROM projecthub.schema_version", conn, tx);
        var version = (int)(await read.ExecuteScalarAsync())!;
        if (version > 10)
            throw new InvalidOperationException("数据库版本高于当前 Core 支持的版本。");
        if (version == 0)
        {
            await using var migrate = new NpgsqlCommand("""
                CREATE TABLE projecthub.bootstrap (
                    singleton boolean PRIMARY KEY CHECK (singleton),
                    code_digest bytea,
                    expires_at timestamptz
                );
                INSERT INTO projecthub.bootstrap(singleton) VALUES (true);
                CREATE TABLE projecthub.owner_account (
                    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                    id uuid NOT NULL UNIQUE,
                    username varchar(64) NOT NULL,
                    password_hash text NOT NULL,
                    session_version bigint NOT NULL DEFAULT 1
                );
                CREATE TABLE projecthub.projects (
                    id uuid PRIMARY KEY,
                    owner_id uuid NOT NULL REFERENCES projecthub.owner_account(id),
                    name varchar(120) NOT NULL,
                    version bigint NOT NULL DEFAULT 1,
                    request_id uuid NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    UNIQUE (owner_id, request_id)
                );
                CREATE TABLE projecthub.audit_log (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    actor_id uuid,
                    action text NOT NULL,
                    project_id uuid
                );
                INSERT INTO projecthub.schema_version(version) VALUES (1);
                """, conn, tx);
            await migrate.ExecuteNonQueryAsync();
        }
        if (version < 2)
        {
            await using var migrate = new NpgsqlCommand(DeviceSchemaSql + WorkspaceSchemaSql +
                "INSERT INTO projecthub.schema_version(version) VALUES (2);", conn, tx);
            await migrate.ExecuteNonQueryAsync();
        }
        if (version < 3)
        {
            await using var migrate = new NpgsqlCommand(CollaborationSchemaSql +
                "INSERT INTO projecthub.schema_version(version) VALUES (3);", conn, tx);
            await migrate.ExecuteNonQueryAsync();
        }
        if (version < 4)
        {
            await using var migrate = new NpgsqlCommand(ObservationSchemaSql +
                "INSERT INTO projecthub.schema_version(version) VALUES (4);", conn, tx);
            await migrate.ExecuteNonQueryAsync();
        }
        if (version < 5)
        {
            await using var migrate = new NpgsqlCommand(AssetSchemaSql +
                "INSERT INTO projecthub.schema_version(version) VALUES (5);", conn, tx);
            await migrate.ExecuteNonQueryAsync();
        }
        if (version < 6)
        {
            await using var migrate = new NpgsqlCommand(KnowledgeAssetSchemaSql +
                "INSERT INTO projecthub.schema_version(version) VALUES (6);", conn, tx);
            await migrate.ExecuteNonQueryAsync();
        }
        if (version < 7)
        {
            await using var migrate = new NpgsqlCommand(KnowledgeValiditySchemaSql +
                "INSERT INTO projecthub.schema_version(version) VALUES (7);", conn, tx);
            await migrate.ExecuteNonQueryAsync();
        }
        if (version < 8)
        {
            await using var migrate = new NpgsqlCommand(DeviceUpdateSchemaSql +
                "INSERT INTO projecthub.schema_version(version) VALUES (8);", conn, tx);
            await migrate.ExecuteNonQueryAsync();
        }
        if (version < 9)
        {
            await using var migrate = new NpgsqlCommand(ActivitySchemaSql +
                "INSERT INTO projecthub.schema_version(version) VALUES (9);", conn, tx);
            await migrate.ExecuteNonQueryAsync();
        }
        if (version < 10)
        {
            await using var migrate = new NpgsqlCommand("""
                CREATE TABLE projecthub.login_challenges (
                    id uuid PRIMARY KEY, browser_id uuid NOT NULL, answer_digest bytea NOT NULL,
                    expires_at timestamptz NOT NULL DEFAULT clock_timestamp() + interval '5 minutes'
                );
                CREATE INDEX login_challenge_expiry ON projecthub.login_challenges(expires_at);
                INSERT INTO projecthub.schema_version(version) VALUES (10);
                """, conn, tx);
            await migrate.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<(string Code, DateTimeOffset ExpiresAt)?> IssueSetupCodeAsync()
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // Check ownership in a fresh snapshot after any concurrent installation has committed.
        await using var gate = new NpgsqlCommand("SELECT singleton FROM projecthub.bootstrap FOR UPDATE", conn, tx);
        await gate.ExecuteScalarAsync();
        await using var issue = new NpgsqlCommand("""
            UPDATE projecthub.bootstrap SET code_digest = $1, expires_at = clock_timestamp() + interval '30 minutes'
            WHERE NOT EXISTS (SELECT 1 FROM projecthub.owner_account) RETURNING expires_at
            """, conn, tx);
        issue.Parameters.AddWithValue(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        var result = await issue.ExecuteScalarAsync();
        await tx.CommitAsync();
        return result is DateTime expiry ? (code, new DateTimeOffset(expiry)) : null;
    }

    public async Task<bool> IsInstalledAsync()
    {
        await using var cmd = db.CreateCommand("SELECT EXISTS(SELECT 1 FROM projecthub.owner_account)");
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<bool> SetupAsync(string code, string username, string password)
    {
        if (!IsValidUsername(username) || !IsValidPassword(password) || code?.Trim().Length != 32)
            return false;
        username = username.Trim();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var claim = new NpgsqlCommand("""
            UPDATE projecthub.bootstrap SET code_digest = NULL, expires_at = NULL
            WHERE code_digest = $1 AND expires_at > clock_timestamp()
            """, conn, tx);
        claim.Parameters.AddWithValue(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant())));
        if (await claim.ExecuteNonQueryAsync() != 1)
            return false;
        var ownerId = Guid.NewGuid();
        await using var create = new NpgsqlCommand("""
            INSERT INTO projecthub.owner_account(id, username, password_hash) VALUES ($1, $2, $3)
            """, conn, tx);
        create.Parameters.AddWithValue(ownerId);
        create.Parameters.AddWithValue(username);
        create.Parameters.AddWithValue(Hasher.HashPassword(username, password));
        await create.ExecuteNonQueryAsync();
        await AuditAsync(conn, tx, ownerId, "owner.created");
        await tx.CommitAsync();
        return true;
    }

    public async Task<AdminIdentity?> LoginAsync(string username, string password)
    {
        if (!IsValidUsername(username) || !IsValidPassword(password))
            return null;
        username = username.Trim();
        AdminIdentity? owner = null;
        var hash = DummyHash;
        await using (var find = db.CreateCommand("""
            SELECT id, username, session_version, password_hash FROM projecthub.owner_account WHERE username = $1
            """))
        {
            find.Parameters.AddWithValue(username);
            await using var reader = await find.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                owner = new(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2));
                hash = reader.GetString(3);
            }
        }
        if (Hasher.VerifyHashedPassword(username, hash, password) == PasswordVerificationResult.Failed || owner is null)
            return null;
        await using var login = db.CreateCommand("""
            INSERT INTO projecthub.audit_log(actor_id, action)
            SELECT id, 'owner.login' FROM projecthub.owner_account WHERE id = $1 AND session_version = $2
            """);
        login.Parameters.AddWithValue(owner.Id);
        login.Parameters.AddWithValue(owner.SessionVersion);
        return await login.ExecuteNonQueryAsync() == 1 ? owner : null;
    }

    public async Task<bool> SessionValidAsync(Guid ownerId, long sessionVersion)
    {
        await using var cmd = db.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM projecthub.owner_account WHERE id = $1 AND session_version = $2)
            """);
        cmd.Parameters.AddWithValue(ownerId);
        cmd.Parameters.AddWithValue(sessionVersion);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<bool> ResetPasswordAsync(string newPassword)
    {
        if (!IsValidPassword(newPassword))
            return false;
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var reset = new NpgsqlCommand("""
            UPDATE projecthub.owner_account SET password_hash = $1, session_version = session_version + 1 RETURNING id
            """, conn, tx);
        reset.Parameters.AddWithValue(Hasher.HashPassword("", newPassword));
        if (await reset.ExecuteScalarAsync() is not Guid ownerId)
            return false;
        await AuditAsync(conn, tx, ownerId, "owner.password_reset");
        await tx.CommitAsync();
        return true;
    }

    public async Task<bool> ChangePasswordAsync(Guid ownerId, long sessionVersion, string current, string replacement)
    {
        if (!IsValidPassword(current) || !IsValidPassword(replacement) || current == replacement)
            return false;
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var find = new NpgsqlCommand("""
            SELECT password_hash FROM projecthub.owner_account WHERE id = $1 AND session_version = $2 FOR UPDATE
            """, conn, tx);
        find.Parameters.AddWithValue(ownerId);
        find.Parameters.AddWithValue(sessionVersion);
        if (await find.ExecuteScalarAsync() is not string hash ||
            Hasher.VerifyHashedPassword("", hash, current) == PasswordVerificationResult.Failed)
            return false;
        await using var update = new NpgsqlCommand("""
            UPDATE projecthub.owner_account SET password_hash = $1, session_version = session_version + 1 WHERE id = $2
            """, conn, tx);
        update.Parameters.AddWithValue(Hasher.HashPassword("", replacement));
        update.Parameters.AddWithValue(ownerId);
        await update.ExecuteNonQueryAsync();
        await AuditAsync(conn, tx, ownerId, "owner.password_changed");
        await tx.CommitAsync();
        return true;
    }

    public async Task<IReadOnlyList<ProjectEntry>> ListProjectsAsync(Guid ownerId)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, name, version FROM projecthub.projects WHERE owner_id = $1 ORDER BY created_at DESC, id
            """);
        cmd.Parameters.AddWithValue(ownerId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var projects = new List<ProjectEntry>();
        while (await reader.ReadAsync())
            projects.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2)));
        return projects;
    }

    public async Task<ProjectEntry> CreateProjectAsync(Guid ownerId, long sessionVersion, string name, Guid requestId)
    {
        if (!IsValidProjectName(name) || requestId == Guid.Empty)
            throw new ArgumentException("项目名称或请求标识无效。");
        name = name.Trim();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await RequireOwnerSessionAsync(conn, tx, ownerId, sessionVersion);
        var projectId = Guid.NewGuid();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO projecthub.projects(id, owner_id, name, request_id) VALUES ($1, $2, $3, $4)
            ON CONFLICT (owner_id, request_id) DO NOTHING
            """, conn, tx);
        insert.Parameters.AddWithValue(projectId);
        insert.Parameters.AddWithValue(ownerId);
        insert.Parameters.AddWithValue(name);
        insert.Parameters.AddWithValue(requestId);
        if (await insert.ExecuteNonQueryAsync() == 1)
        {
            await AuditAsync(conn, tx, ownerId, "project.created", projectId);
            await tx.CommitAsync();
            return new(projectId, name, 1);
        }
        await using var replay = new NpgsqlCommand("""
            SELECT id, name, version FROM projecthub.projects WHERE owner_id = $1 AND request_id = $2
            """, conn, tx);
        replay.Parameters.AddWithValue(ownerId);
        replay.Parameters.AddWithValue(requestId);
        await using var reader = await replay.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("无法找到创建项目的原始请求记录。");
        if (reader.GetString(1) != name)
            throw new ProjectRequestConflictException();
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2));
    }

}
