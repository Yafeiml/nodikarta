using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace ProjectHub.Edge;

public sealed record WorkspaceAccessPolicy(string[]? Tools, Guid? EndpointId);

internal static class WorkspacePath
{
    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool Within(string path, string root, StringComparison comparison) =>
        path.Equals(root, comparison) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison);

    internal static string Verified(string path)
    {
        var directory = new DirectoryInfo(path);
        for (var component = directory; component != null; component = component.Parent)
            if ((component.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                throw new InvalidOperationException("工作目录不可用或经过目录链接，无法确认绑定。");
        return TryFinal(directory.FullName) ?? throw new InvalidOperationException("无法确认工作目录的实际位置。");
    }

    // The normalized NT name handles SUBST drives, 8.3 aliases and volume mount aliases consistently.
    internal static string? TryFinal(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            var resolved = realpath(path, IntPtr.Zero);
            if (resolved == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(resolved); }
            finally { free(resolved); }
        }
        using var handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 2);
        if (length >= buffer.Capacity && length <= 32768)
        {
            buffer = new StringBuilder((int)length);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 2);
        }
        return length > 0 && length < buffer.Capacity ? buffer.ToString().TrimEnd('\\') : null;
    }

    internal static bool Overlaps(string other, string path, string canonical)
    {
        var compare = Comparison;
        if (Within(other, path, compare) || Within(path, other, compare)) return true;
        return TryFinal(other) is { } final && (Within(final, canonical, compare) || Within(canonical, final, compare));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder name, uint length, uint flags);

    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern nint realpath(string path, nint output);
    [DllImport("libc")]
    private static extern void free(nint memory);
}

public sealed partial class EdgeSession
{
    private static List<LocalBinding> LocalWorkspaces(SqliteConnection db, SqliteTransaction? tx, Guid device)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT project_id, workspace_id, local_path FROM workspaces WHERE device_id=$device ORDER BY local_path,project_id";
        cmd.Parameters.AddWithValue("$device", device.ToString("D"));
        using var reader = cmd.ExecuteReader();
        List<LocalBinding> rows = [];
        while (reader.Read())
            rows.Add(new(Guid.Parse(reader.GetString(0)), Guid.Empty, Guid.Parse(reader.GetString(1)), reader.GetString(2), "", ""));
        return rows;
    }

    // Local records include revoked grants, so stale child bindings cannot fall through to an ancestor.
    public IReadOnlyList<LocalBinding> EnumerateLocalWorkspaces()
    {
        using var db = Connect();
        return GetIdentityOrDefault() is { } id ? LocalWorkspaces(db, null, id.DeviceId) : [];
    }

    internal LocalBinding? MatchWorkspace(string cwd)
    {
        var canonical = WorkspacePath.Verified(cwd);
        var rows = EnumerateLocalWorkspaces();
        var matches = rows.Select(row => (row, final: WorkspacePath.TryFinal(row.LocalPath)))
            .Where(entry => entry.final != null && WorkspacePath.Within(canonical, entry.final, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.final!.Length).ToArray();
        if (matches.Length == 0) return null;
        var (winner, root) = matches[0];
        if ((matches.Length > 1 && root == matches[1].final) ||
            rows.Any(other => other.ProjectId != winner.ProjectId && WorkspacePath.Overlaps(other.LocalPath, winner.LocalPath, root!)))
            throw new InvalidOperationException("多个目录绑定发生冲突，请先在 Edge 核对当前目录。");
        WorkspacePath.Verified(winner.LocalPath);
        return winner;
    }

    public WorkspaceAccessPolicy? GetWorkspacePolicy(LocalBinding binding)
    {
        using var db = Connect();
        using var cmd = WorkspacePolicyCommand(db, binding);
        cmd.CommandText = "SELECT tools, endpoint FROM workspace_access WHERE workspace_id=(" + cmd.CommandText + ")";
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return null;
        return new(row.IsDBNull(0) ? null : JsonSerializer.Deserialize(row.GetString(0), EdgeJson.Default.StringArray)!,
            row.IsDBNull(1) ? null : Guid.Parse(row.GetString(1)));
    }

    public void SetWorkspacePolicy(LocalBinding binding, WorkspaceAccessPolicy policy) => WriteWorkspacePolicy(binding, policy, replace: true);
    public bool TryAddWorkspacePolicy(LocalBinding binding, WorkspaceAccessPolicy policy) => WriteWorkspacePolicy(binding, policy, replace: false);

    private bool WriteWorkspacePolicy(LocalBinding binding, WorkspaceAccessPolicy policy, bool replace)
    {
        if (policy.Tools?.Any(name => !EdgeToolCatalog.Tools.Any(t => t.Name == name)) == true)
            throw new ArgumentException("目录策略包含未知工具。");
        using var db = Connect();
        using var cmd = WorkspacePolicyCommand(db, binding);
        cmd.CommandText = "INSERT INTO workspace_access(workspace_id,tools,endpoint) SELECT workspace_id,$tools,$endpoint FROM (" +
            cmd.CommandText + ") WHERE true ON CONFLICT(workspace_id) " +
            (replace ? "DO UPDATE SET tools=excluded.tools, endpoint=excluded.endpoint" : "DO NOTHING");
        cmd.Parameters.AddWithValue("$tools", policy.Tools == null ? DBNull.Value : JsonSerializer.Serialize(policy.Tools.Distinct(StringComparer.Ordinal).ToArray(), EdgeJson.Default.StringArray));
        cmd.Parameters.AddWithValue("$endpoint", (object?)policy.EndpointId?.ToString("D") ?? DBNull.Value);
        var saved = cmd.ExecuteNonQuery() == 1;
        if (!saved && (replace || GetWorkspacePolicy(binding) == null))
            throw new InvalidOperationException("此目录的设备绑定已变化。");
        return saved;
    }

    private SqliteCommand WorkspacePolicyCommand(SqliteConnection db, LocalBinding binding)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT workspace_id FROM workspaces WHERE device_id=$d AND project_id=$p AND workspace_id=$w AND local_path=$l";
        cmd.Parameters.AddWithValue("$d", GetIdentity().DeviceId.ToString("D"));
        cmd.Parameters.AddWithValue("$p", binding.ProjectId.ToString("D"));
        cmd.Parameters.AddWithValue("$w", binding.WorkspaceId.ToString("D"));
        cmd.Parameters.AddWithValue("$l", binding.LocalPath);
        return cmd;
    }
}
