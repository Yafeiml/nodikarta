using Microsoft.Data.Sqlite;

namespace ProjectHub.Edge;

public sealed partial class EdgeSession
{
    // Return local counts even when Core is offline or the old device has been revoked.
    internal (long Directories, long Unsent) ReadPairingInventory(Guid deviceId)
    {
        using var local = Connect();
        using var count = local.CreateCommand();
        count.CommandText = "SELECT count(*) FROM workspaces WHERE device_id=$device";
        count.Parameters.AddWithValue("$device", deviceId.ToString());
        var directories = (long)count.ExecuteScalar()!;
        var path = Path.Combine(StateDirectory, "mvp.sqlite3");
        if (!File.Exists(path)) return (directories, 0);
        using var drafts = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2
        }.ToString());
        drafts.Open();
        using var pending = drafts.CreateCommand();
        pending.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='draft'";
        if ((long)pending.ExecuteScalar()! == 0) return (directories, 0);
        pending.CommandText = "SELECT count(*) FROM draft";
        return (directories, (long)pending.ExecuteScalar()!);
    }
}
