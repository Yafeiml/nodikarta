using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ProjectHub.Edge;

public sealed partial class EdgeSession
{
    private const string ProfilePrefix = ".edge-core-";
    private const string SelectionFile = "active-core-profile";
    private const string PreviousFile = "previous-core-profile";
    internal string RootDirectory { get; }
    private Guid Profile { get; }
    internal bool IsActiveProfile => ReadActive(RootDirectory) == Profile;

    private static string ProfilePath(string root, Guid id) =>
        id == Guid.Empty ? root : Path.Combine(root, ProfilePrefix + id.ToString("N"));

    public static EdgeSession Open(string stateDirectory) => Open(stateDirectory, pinLegacyProfile: false);

    internal static EdgeSession Open(string stateDirectory, bool pinLegacyProfile, EdgeRequestSource? source = null)
    {
        var directory = EdgeStatePaths.Resolve(stateDirectory);
        var name = Path.GetFileName(directory);
        var pinned = Guid.Empty;
        var child = name.StartsWith(ProfilePrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && Guid.TryParseExact(name.AsSpan(ProfilePrefix.Length), "N", out pinned) && pinned != Guid.Empty;
        var root = child ? Path.GetDirectoryName(directory)! : directory;
        EnsurePrivateDirectory(root);
        var active = ReadActive(root);
        if ((child || pinLegacyProfile) && pinned != active) throw ProfileChanged();
        if (active != Guid.Empty && !File.Exists(Path.Combine(ProfilePath(root, active), "edge.sqlite3")))
            throw new InvalidDataException("当前 Core 的本机数据目录不完整，请核对数据目录。");
        return new EdgeSession(root, active, source ?? EdgeRequestSource.Cli);
    }

    internal EdgeSession CurrentProfile() => IsActiveProfile ? this : Open(RootDirectory, false, defaultSource);

    private static InvalidOperationException ProfileChanged() =>
        new("当前 Core 已更换，请重新打开客户端并从新设备的绑定目录接入。");

    internal void EnsureActiveProfile()
    {
        if (!IsActiveProfile) throw ProfileChanged();
    }

    private static Guid ReadActive(string root)
    {
        string text;
        try
        {
            using var file = new FileStream(Path.Combine(root, SelectionFile), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(file);
            text = reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return Guid.Empty; } // Original installations keep their files in the root.
        return Guid.TryParseExact(text, "N", out var id) && id != Guid.Empty ? id :
            throw new InvalidDataException("无法识别当前 Core 的本机身份记录。");
    }

    // All pair/re-pair and token renewal operations share the root identity lock. Requests already
    // in flight retain their old session, transport destination and physical SQLite databases.
    private async Task<DeviceIdentity> ReplaceProfileAsync(Uri core, Guid pairingId, string secret,
        string label, CancellationToken ct)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(core.AbsoluteUri + "\n" + pairingId.ToString("N") + "." + secret));
        var id = new Guid(digest.AsSpan(0, 16));
        if (id == Profile) return GetIdentity();
        var path = ProfilePath(RootDirectory, id);
        var parent = Path.Combine(path, PreviousFile);
        EdgeSession target;
        Credentials? saved = null;
        var created = !Directory.Exists(path);
        if (!created)
        {
            target = new EdgeSession(RootDirectory, id, defaultSource);
            saved = target.ReadStoredCredentials();
            if (!File.Exists(parent) && saved == null) File.WriteAllText(parent, Profile.ToString("N"));
            if (File.ReadAllText(parent) != Profile.ToString("N")) throw new InvalidOperationException("该配对码已用于此前的设备身份，请生成新码。");
        }
        else
        {
            // Prepare storage before consuming a one-time code.
            target = new EdgeSession(RootDirectory, id, defaultSource);
            File.WriteAllText(parent, Profile.ToString("N"));
        }
        if (saved == null)
        {
            Tokens tokens;
            try { tokens = await target.RedeemAsync(core, pairingId, secret, label, ct); }
            catch (PairingRejected) when (created)
            {
                RemoveRejectedProfile(target, parent);
                throw;
            }
            saved = new(core, tokens, "active");
            target.Save(saved, label);
        }
        if (saved.Core != core) throw new InvalidDataException("配对结果与目标 Core 不一致。");
        WriteProfileMarker(Path.Combine(RootDirectory, SelectionFile), id);
        return new(core, saved.Tokens.DeviceId, saved.Tokens.EdgeId) { Profile = id };
    }

    private static void WriteProfileMarker(string path, Guid id) =>
        WriteDurably(path, Encoding.ASCII.GetBytes(id.ToString("N")));

    private static void WriteDurably(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = LinuxState.PrivateFileMode;
            using (var output = new FileStream(temp, options))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            if (OperatingSystem.IsLinux()) LinuxState.SyncDirectory(Path.GetDirectoryName(path)!);
        }
        finally { File.Delete(temp); }
    }

    private static void RemoveRejectedProfile(EdgeSession target, string parent)
    {
        try
        {
            var folder = new DirectoryInfo(target.StateDirectory);
            var files = folder.GetFileSystemInfos();
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || files.Any(f => f is not FileInfo || f.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || (f.Name is not (PreviousFile or "edge.sqlite3" or "edge.sqlite3-wal" or "edge.sqlite3-shm")
                        && !(OperatingSystem.IsLinux() && f.Name == LinuxState.KeyName)))
                || !File.Exists(parent) || target.ReadStoredCredentials() != null) return;
            foreach (var item in files) File.Delete(item.FullName);
            folder.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { }
    }

    // Only an explicit rebind of the same physical directory can reclaim its old managed files.
    // A local path in an archived SQLite database is not authorization for the new Core.
    private void RetirePreviousAntigravityFiles(LocalBinding workspace)
    {
        if (Profile == Guid.Empty || !AntigravityWorkspace.HasPriorFiles(workspace, StateDirectory)) return;
        var seen = new HashSet<Guid> { Profile };
        var id = Profile;
        while (id != Guid.Empty)
        {
            var parent = File.ReadAllText(Path.Combine(ProfilePath(RootDirectory, id), PreviousFile));
            if (!Guid.TryParseExact(parent, "N", out id) || !seen.Add(id))
                throw new InvalidDataException("旧 Core 的本机身份记录无效。");
            var directory = ProfilePath(RootDirectory, id);
            var database = Path.Combine(directory, "edge.sqlite3");
            if (!File.Exists(database)) return;
            EnsurePrivateDirectory(directory);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT workspace_id FROM workspaces WHERE local_path=$path";
            cmd.Parameters.AddWithValue("$path", workspace.LocalPath);
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
                AntigravityWorkspace.Remove(workspace with { WorkspaceId = rows.GetGuid(0) }, directory);
            if (!AntigravityWorkspace.HasPriorFiles(workspace, StateDirectory)) return;
        }
    }
}
