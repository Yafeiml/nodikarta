using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProjectHub.Edge;

internal static class EdgeStatePaths
{
    private static readonly AsyncLocal<(string Local, string Machine)?> TestFolders = new();
    private static string Local => TestFolders.Value?.Local ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Machine => TestFolders.Value?.Machine ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    internal static string LocalDefault => Path.Combine(Local, "Nodicarta", "Edge");
    internal static string MachineDefault => Path.Combine(Machine, "Nodicarta", "Edge");
    internal static string LegacyLocalDefault => Path.Combine(Local, "ProjectHub", "Edge");
    internal static string LegacyMachineDefault => Path.Combine(Machine, "ProjectHub", "Edge");
    private static readonly StringComparison WindowsPath = StringComparison.OrdinalIgnoreCase;

    private sealed class RestoreFolders((string, string)? previous) : IDisposable
    {
        public void Dispose() => TestFolders.Value = previous;
    }

    internal static IDisposable UseKnownFoldersForTest(string localAppData, string programData)
    {
        var reset = new RestoreFolders(TestFolders.Value);
        TestFolders.Value = (Path.GetFullPath(localAppData), Path.GetFullPath(programData));
        return reset;
    }

    private sealed record Location(string Old, string New, string? Profile)
    {
        internal string Canonical => Profile == null ? New : Path.Combine(New, Profile);
        internal string StagedLink => Path.Combine(Path.GetDirectoryName(Old)!, ".Edge-Nodicarta-migration");
    }

    // Only the two built-in roots and their exact generated profile children have aliases.
    private static Location? Find(string path)
    {
        const string prefix = ".edge-core-";
        string? profile = null;
        var leaf = Path.GetFileName(path);
        if (leaf.StartsWith(prefix, WindowsPath) && Guid.TryParseExact(leaf.AsSpan(prefix.Length), "N", out var id) && id != Guid.Empty)
        {
            profile = leaf;
            path = Path.GetDirectoryName(path)!;
        }
        foreach (var known in new[] { Local, Machine })
        {
            var old = Path.Combine(known, "ProjectHub", "Edge");
            var current = Path.Combine(known, "Nodicarta", "Edge");
            if (path.Equals(old, WindowsPath) || path.Equals(current, WindowsPath)) return new(old, current, profile);
        }
        return null;
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static string Normalize(string path)
    {
        var full = Full(path);
        return Find(full)?.Canonical ?? full;
    }

    internal static bool Same(string left, string right)
    {
        if (string.Equals(left, right, WindowsPath)) return true;
        if (!Path.IsPathFullyQualified(left) || !Path.IsPathFullyQualified(right)) return false;
        try
        {
            var a = Find(Full(left));
            var b = Find(Full(right));
            if (a == null || b == null || !a.Canonical.Equals(b.Canonical, WindowsPath)) return false;
            ValidateParents(a);
            var current = Ordinary(a.New);
            var attributes = Attributes(a.Old);
            return attributes == null ? Attributes(a.StagedLink) == null : current && JunctionPointsTo(a.Old, a.New);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { return false; }
    }

    internal static string Resolve(string path)
    {
        var full = Full(path);
        var location = Find(full);
        if (location == null) return full;
        ValidateParents(location);
        var currentExists = Ordinary(location.New);
        var old = Attributes(location.Old);
        if (old == null && Attributes(location.StagedLink) == null) return location.Canonical;
        if (currentExists && old?.HasFlag(FileAttributes.ReparsePoint) == true && JunctionPointsTo(location.Old, location.New)
            && Attributes(location.StagedLink) == null) return location.Canonical;
        var parent = Path.GetDirectoryName(location.New)!;
        Directory.CreateDirectory(parent);
        _ = Ordinary(parent);
        using var migration = LockMigration(parent);
        Migrate(location);
        return location.Canonical;
    }

    private static void Migrate(Location location)
    {
        ValidateParents(location);
        var current = Ordinary(location.New);
        var old = Attributes(location.Old);
        var pending = Attributes(location.StagedLink);
        if (pending != null && !JunctionPointsTo(location.StagedLink, location.New))
            throw new IOException("Edge 状态目录的迁移标记不属于本次迁移，请核对。");
        if (old?.HasFlag(FileAttributes.ReparsePoint) == true)
        {
            if (current && JunctionPointsTo(location.Old, location.New)) return;
            throw new IOException("旧 Edge 状态目录的链接目标与新默认位置不符，请核对。");
        }
        if (old == null)
        {
            if (pending == null) return;
            if (!current) throw new IOException("Edge 状态目录迁移尚未完成，找不到原数据。");
            EdgeSession.EnsurePrivateDirectory(location.New);
            Publish(location);
            return;
        }
        _ = Ordinary(location.Old);
        EdgeSession.EnsurePrivateDirectory(location.Old);
        if (current)
            throw new IOException($"新旧 Edge 状态目录同时存在。请先核对并保留两处数据：{location.Old}；{location.New}");
        // Same-volume rename preserves the complete SQLite WAL, profile history, receipts and protected ACL.
        // The staged junction doubles as a restart record between the two directory renames.
        try
        {
            CheckIdle(location.Old);
            if (pending == null) CreateJunction(location.StagedLink, location.New);
            Directory.Move(location.Old, location.New);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (JunctionPointsTo(location.StagedLink, location.New)) Directory.Delete(location.StagedLink);
            throw new IOException("无法迁移 Edge 状态目录。请退出使用旧数据的 Edge、智能体连接或服务后重试。", e);
        }
        Publish(location);
    }

    private static void Publish(Location location)
    {
        try { Directory.Move(location.StagedLink, location.Old); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leave the original location usable if publishing failed. A crash or another writer
            // can also leave the prepared junction for the next startup; never displace their data.
            if (Attributes(location.Old) == null && JunctionPointsTo(location.StagedLink, location.New))
            {
                try { Directory.Move(location.New, location.Old); Directory.Delete(location.StagedLink); }
                catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException) { }
            }
            throw new IOException("Edge 状态目录迁移未完成，请关闭占用目录的程序并重试。", e);
        }
    }

    private static void CheckIdle(string root)
    {
        var folders = new Stack<DirectoryInfo>();
        folders.Push(new DirectoryInfo(root));
        while (folders.TryPop(out var folder))
        {
            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("状态目录内含链接：" + entry.FullName);
                if (entry is DirectoryInfo directory) { folders.Push(directory); continue; }
                using var probe = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        }
    }

    private static FileStream LockMigration(string folder)
    {
        var path = Path.Combine(folder, ".edge-default-migration.lock");
        if (Attributes(path)?.HasFlag(FileAttributes.ReparsePoint) == true) throw new IOException("Edge 迁移锁不能是链接。");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 && timer.Elapsed < TimeSpan.FromSeconds(15)) { Thread.Sleep(50); }
        }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception e) when (e is DirectoryNotFoundException or FileNotFoundException) { return null; }
    }

    private static bool Ordinary(string path)
    {
        var attributes = Attributes(path);
        if (attributes == null) return false;
        if (!attributes.Value.HasFlag(FileAttributes.Directory) || attributes.Value.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Edge 需要实体状态目录，请核对：" + path);
        return true;
    }

    private static void ValidateParents(Location location)
    {
        _ = Ordinary(Path.GetDirectoryName(location.Old)!);
        _ = Ordinary(Path.GetDirectoryName(location.New)!);
    }

    // Directory junctions, unlike symbolic links, can be created by a standard Windows user.
    private const uint MountPoint = 0xA0000003;
    private static SafeFileHandle OpenJunction(string path, uint access) =>
        CreateFile(path, access, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);

    private static bool JunctionPointsTo(string path, string target)
    {
        if (Attributes(path)?.HasFlag(FileAttributes.ReparsePoint) != true) return false;
        using var handle = OpenJunction(path, 0);
        var data = new byte[16384];
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A8, null, 0, data, data.Length, out _, IntPtr.Zero) ||
            BitConverter.ToUInt32(data) != MountPoint) return false;
        var dest = new DirectoryInfo(path).LinkTarget;
        return dest != null && Path.IsPathFullyQualified(dest) && Full(dest).Equals(target, WindowsPath);
    }

    private static void CreateJunction(string path, string target)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var display = Encoding.Unicode.GetBytes(target);
        var data = new byte[20 + substitute.Length + display.Length];
        BitConverter.TryWriteBytes(data.AsSpan(), MountPoint);
        BitConverter.TryWriteBytes(data.AsSpan(4), checked((ushort)(data.Length - 8)));
        BitConverter.TryWriteBytes(data.AsSpan(10), checked((ushort)substitute.Length));
        BitConverter.TryWriteBytes(data.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BitConverter.TryWriteBytes(data.AsSpan(14), checked((ushort)display.Length));
        substitute.CopyTo(data, 16);
        display.CopyTo(data, 18 + substitute.Length);
        Directory.CreateDirectory(path);
        try
        {
            using var handle = OpenJunction(path, 0x40000000);
            if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, data, data.Length, null, 0, out _, IntPtr.Zero))
                throw new IOException("无法创建 Edge 状态目录的兼容链接。", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        catch { Directory.Delete(path); throw; }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputBytes,
        [Out] byte[]? output, int outputBytes, out int returned, IntPtr overlapped);
}
