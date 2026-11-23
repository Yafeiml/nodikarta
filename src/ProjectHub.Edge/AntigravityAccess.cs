using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ProjectHub.Edge;

public sealed partial class EdgeSession
{
    internal bool AntigravityReady(string command)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT command FROM antigravity_access WHERE NOT EXISTS (SELECT 1 FROM antigravity_pending)";
        return string.Equals(cmd.ExecuteScalar() as string, command, StringComparison.OrdinalIgnoreCase);
    }

    internal void EnableAntigravity(string command)
    {
        if (!Path.IsPathFullyQualified(command) || !File.Exists(command) ||
            !Path.GetFileName(command).Equals("ProjectHub.Edge.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("找不到 Antigravity 所需的 Edge 程序。");
        using var db = Connect();
        using var tx = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM antigravity_disabled;
            INSERT OR REPLACE INTO antigravity_access(singleton,command) VALUES(1,$command);
            INSERT OR IGNORE INTO antigravity_pending(workspace_id,project_id,local_path)
                SELECT workspace_id,project_id,local_path FROM workspaces;
            """;
        cmd.Parameters.AddWithValue("$command", command);
        cmd.ExecuteNonQuery();
        tx.Commit();
        FlushAntigravity();
    }

    internal (string? Enabled, string? Disabled, bool Pending, bool RegistrationPending) AntigravityMaintenance()
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT command FROM antigravity_access), (SELECT command FROM antigravity_disabled),
                EXISTS(SELECT 1 FROM antigravity_pending), EXISTS(SELECT 1 FROM antigravity_disabled WHERE registration_pending=1);
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetBoolean(2), reader.GetBoolean(3));
    }

    internal void PrepareAntigravityDisconnect(string command)
    {
        using var db = Connect();
        using var tx = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT command FROM antigravity_access UNION ALL
            SELECT command FROM antigravity_disabled WHERE registration_pending=1 OR EXISTS(SELECT 1 FROM antigravity_pending);
            """;
        if (cmd.ExecuteScalar() is string active && !active.Equals(command, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("接入由另一份 Edge 管理。");
        cmd.CommandText = """
            INSERT OR IGNORE INTO antigravity_pending(workspace_id,project_id,local_path)
                SELECT w.workspace_id,w.project_id,w.local_path FROM workspaces w CROSS JOIN antigravity_access;
            DELETE FROM antigravity_access;
            INSERT OR REPLACE INTO antigravity_disabled(singleton,command,registration_pending) VALUES(1,$command,1);
            """;
        cmd.Parameters.AddWithValue("$command", command);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    internal void FinishAntigravityDisconnect(string command)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE antigravity_disabled SET registration_pending=0 WHERE command=$command COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$command", command);
        cmd.ExecuteNonQuery();
    }

    internal void RetryAntigravityCleanup() => FlushAntigravity();

    // Binding and retirement intent commit together, including when the folder is temporarily unavailable.
    private static void QueueAntigravity(SqliteConnection db, SqliteTransaction tx, LocalBinding binding)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO antigravity_pending(workspace_id,project_id,local_path)
                SELECT $id,$project,$path FROM antigravity_access;
            """;
        cmd.Parameters.AddWithValue("$id", binding.WorkspaceId.ToString("D"));
        cmd.Parameters.AddWithValue("$project", binding.ProjectId.ToString("D"));
        cmd.Parameters.AddWithValue("$path", binding.LocalPath);
        cmd.ExecuteNonQuery();
    }

    private void FinishAntigravityBinding(LocalBinding binding)
    {
        try { FlushAntigravity(binding.LocalPath); }
        catch (IOException ex) { throw new IOException("目录已绑定，Antigravity 接入待重试：" + ex.Message, ex); }
    }

    private void FlushAntigravity(string? localPath = null)
    {
        // Project files are shared by every identity that explicitly binds this Windows directory.
        using var fileLock = AcquireProfileLockAsync("antigravity-files.lock", CancellationToken.None).GetAwaiter().GetResult();
        using var db = Connect();
        using var cmd = db.CreateCommand();
        // At the same path retire an old registration before writing its replacement.
        cmd.CommandText = """
            SELECT p.workspace_id FROM antigravity_pending p
            LEFT JOIN workspaces w ON w.workspace_id=p.workspace_id
            WHERE $path IS NULL OR p.local_path=$path COLLATE NOCASE
            ORDER BY w.workspace_id IS NOT NULL, p.local_path;
            """;
        cmd.Parameters.AddWithValue("$path", (object?)localPath ?? DBNull.Value);
        var jobs = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) jobs.Add(reader.GetString(0));
        var problems = new List<string>();
        foreach (var id in jobs)
        {
            using var tx = db.BeginTransaction(deferred: false);
            using var job = db.CreateCommand();
            job.Transaction = tx;
            job.CommandText = """
                SELECT p.project_id,p.local_path,a.command,
                    EXISTS(SELECT 1 FROM workspaces w WHERE w.device_id=$device AND w.workspace_id=p.workspace_id)
                FROM antigravity_pending p LEFT JOIN antigravity_access a ON a.singleton=1 WHERE p.workspace_id=$id;
                """;
            job.Parameters.AddWithValue("$id", id);
            job.Parameters.AddWithValue("$device", (object?)GetIdentityOrDefault()?.DeviceId.ToString("D") ?? DBNull.Value);
            LocalBinding binding;
            string? executable;
            bool bound;
            using (var reader = job.ExecuteReader())
            {
                if (!reader.Read()) continue;
                binding = new(reader.GetGuid(0), Guid.Empty, Guid.Parse(id), reader.GetString(1), "", "");
                executable = reader.IsDBNull(2) ? null : reader.GetString(2);
                bound = reader.GetBoolean(3);
            }
            try
            {
                if (bound && executable != null)
                {
                    RetirePreviousAntigravityFiles(binding);
                    AntigravityWorkspace.Apply(binding, executable, StateDirectory);
                }
                else AntigravityWorkspace.Remove(binding, StateDirectory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                problems.Add(binding.LocalPath + "：" + e.Message);
                continue;
            }
            job.CommandText = "DELETE FROM antigravity_pending WHERE workspace_id=$id";
            job.ExecuteNonQuery();
            tx.Commit();
        }
        if (problems.Count != 0) throw new IOException(string.Join(Environment.NewLine, problems));
    }
}
