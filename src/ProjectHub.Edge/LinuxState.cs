using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ProjectHub.Edge;

public sealed partial class EdgeSession
{
    private static byte[] ProtectSecret(string directory, byte[] plain) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine)
        : OperatingSystem.IsLinux() ? LinuxState.Encrypt(directory, plain) : throw new PlatformNotSupportedException();

    private static byte[] UnprotectSecret(string directory, byte[] secret) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(secret, null, DataProtectionScope.LocalMachine)
        : OperatingSystem.IsLinux() ? LinuxState.Decrypt(directory, secret) : throw new PlatformNotSupportedException();
}

[SupportedOSPlatform("linux")]
internal static class LinuxState
{
    internal const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectoryMode = PrivateFileMode | UnixFileMode.UserExecute;
    internal const string KeyName = ".credentials.key";
    private const int KeyLength = 32, NonceLength = 12, TagLength = 16;

    // statx has the same kernel ABI on x86_64 and arm64. AT_SYMLINK_NOFOLLOW validates the entry itself.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileStatus
    {
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, string path, int flags, uint mask, out FileStatus status);
    [DllImport("libc")]
    private static extern uint geteuid();
    [DllImport("libc", SetLastError = true)]
    private static extern SafeFileHandle open(string path, int flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(SafeFileHandle handle);
    [DllImport("libc", SetLastError = true)]
    private static extern int mkdir(string path, uint mode);

    private static FileStatus? TryStatus(string path)
    {
        if (statx(-100, path, 0x100, 0x7ff, out var status) == 0) return status;
        var error = Marshal.GetLastPInvokeError();
        if (error == 2) return null; // ENOENT
        throw new IOException($"无法检查 Edge 数据路径：{path}", new Win32Exception(error));
    }

    private static IEnumerable<string> Components(string path)
    {
        var parts = new Stack<string>();
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent) parts.Push(dir.FullName);
        return parts;
    }

    private static void CheckDirectory(string part, FileStatus entry, uint owner, bool leaf)
    {
        if ((entry.Mode & 0xf000) != 0x4000 || (entry.Uid != owner && entry.Uid != 0))
            throw new IOException($"Edge 数据路径包含链接、非目录或其他用户的目录：{part}");
        if (leaf && (entry.Uid != owner || entry.Mode != (0x4000 | (int)PrivateDirectoryMode)))
            throw new IOException("Edge 数据目录必须是当前用户所有的实体目录，权限为 0700。");
    }

    // In a sticky directory (such as /tmp), another user cannot rename our existing child.
    private static bool SharedParent(FileStatus entry) =>
        (entry.Mode & (int)(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 && (entry.Mode & (int)UnixFileMode.StickyBit) == 0;

    private static (bool Shared, bool HasData) InspectDefaultDirectory(string path)
    {
        var owner = geteuid();
        bool shared = false;
        foreach (var part in Components(path))
        {
            if (TryStatus(part) is not { } entry) return (shared, false);
            CheckDirectory(part, entry, owner, part == path);
            if (part == path) return (shared, Directory.EnumerateFileSystemEntries(part).Any());
            shared |= SharedParent(entry);
        }
        throw new ArgumentException("需要绝对数据路径。", nameof(path));
    }

    internal static string DefaultDirectory(string home)
    {
        var canonical = Path.Combine(home, ".local", "state", "Nodicarta", "Edge");
        var fallback = Path.Combine(home, ".Nodicarta", "Edge");
        var original = InspectDefaultDirectory(canonical);
        var alternate = InspectDefaultDirectory(fallback);
        if (original.HasData && alternate.HasData)
            throw new IOException($"两处均有 Edge 数据，请用 --state 指定要使用的位置：{canonical}；{fallback}");
        if (original.HasData) return canonical;
        return alternate.HasData || original.Shared ? fallback : canonical;
    }

    internal static void EnsurePrivateDirectory(string path)
    {
        var owner = geteuid();
        // Directory.CreateDirectory(path, mode) applies mode only to the leaf. At umask 0002
        // its implicit parents would be 0775 and our own next validation would reject them.
        foreach (var part in Components(path))
        {
            var info = TryStatus(part);
            if (info == null)
            {
                if (mkdir(part, (uint)PrivateDirectoryMode) != 0 && Marshal.GetLastPInvokeError() != 17) // EEXIST if another instance won
                    throw new IOException($"无法创建 Edge 数据目录：{part}", new Win32Exception(Marshal.GetLastPInvokeError()));
                info = TryStatus(part) ?? throw new IOException($"Edge 数据目录已变化：{part}");
            }
            CheckDirectory(part, info.Value, owner, part == path);
            if (part != path && SharedParent(info.Value))
                throw new IOException($"Edge 数据目录的上级路径不安全：{part}");
        }
        if (new DriveInfo(path).DriveType == DriveType.Network)
            throw new IOException("Edge 的 SQLite 数据目录必须位于本机磁盘。");
    }

    private static void CheckPrivateFile(string path)
    {
        var entry = TryStatus(path) ?? throw new FileNotFoundException("找不到 Edge 数据文件。", path);
        if (entry.Uid != geteuid() || entry.Mode != (0x8000 | (int)PrivateFileMode))
            throw new IOException($"Edge 需要当前用户所有且权限为 0600 的常规文件：{path}");
    }

    private static FileStream CreatePrivateFile(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read, UnixCreateMode = PrivateFileMode
    });

    internal static void SyncDirectory(string path)
    {
        // Opening a directory read-only is sufficient for fsync on Linux.
        using var handle = open(path, 0x80000); // O_CLOEXEC
        if (handle.IsInvalid || fsync(handle) != 0)
            throw new IOException($"无法保存 Edge 数据目录的更改：{path}", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    internal static void EnsurePrivateFile(string path)
    {
        if (!File.Exists(path))
        {
            try { using var created = CreatePrivateFile(path); }
            catch (IOException) when (File.Exists(path)) { } // Concurrent first launch.
        }
        CheckPrivateFile(path);
    }

    private static byte[] ReadKey(string directory, bool create)
    {
        var path = Path.Combine(directory, KeyName);
        if (create && !File.Exists(path))
        {
            var temp = path + "." + Guid.NewGuid().ToString("N");
            var key = RandomNumberGenerator.GetBytes(KeyLength);
            try
            {
                using (var file = CreatePrivateFile(temp))
                {
                    file.Write(key);
                    file.Flush(flushToDisk: true);
                }
                try { File.Move(temp, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { } // Always read the winning key.
                SyncDirectory(directory);
            }
            finally { CryptographicOperations.ZeroMemory(key); File.Delete(temp); }
        }
        CheckPrivateFile(path);
        using var saved = File.OpenRead(path);
        if (saved.Length != KeyLength) throw new InvalidDataException("Edge 凭据密钥文件长度错误。");
        var result = new byte[KeyLength];
        saved.ReadExactly(result);
        return result;
    }

    internal static void PrepareCredentials(string directory) => CryptographicOperations.ZeroMemory(ReadKey(directory, true));

    internal static byte[] Encrypt(string directory, byte[] plain)
    {
        var key = ReadKey(directory, true);
        try
        {
            var bytes = new byte[NonceLength + TagLength + plain.Length];
            var nonce = bytes.AsSpan(0, NonceLength);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plain, bytes.AsSpan(NonceLength + TagLength), bytes.AsSpan(NonceLength, TagLength));
            return bytes;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static byte[] Decrypt(string directory, byte[] bytes)
    {
        if (bytes.Length < NonceLength + TagLength) throw new InvalidDataException("Edge 凭据记录不完整。");
        var key = ReadKey(directory, false);
        try
        {
            var plain = new byte[bytes.Length - NonceLength - TagLength];
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(bytes.AsSpan(0, NonceLength), bytes.AsSpan(NonceLength + TagLength),
                bytes.AsSpan(NonceLength, TagLength), plain);
            return plain;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
