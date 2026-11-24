using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProjectHub.Edge;

internal sealed partial class AssetWorkspace
{
    // open(2) flags on Linux x86_64. O_PATH inspects an inode without opening a device or blocking on a FIFO.
    private const int LinuxPath = 0x200000, NoFollow = 0x20000, DirectoryOnly = 0x10000, CloseOnExec = 0x80000;

    private string PinLinuxParent(string root, string[] segments, bool createParents)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("绑定目录无效。");
        var directory = Pin("/");
        foreach (var name in root.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (name is "." or "..") throw new ArgumentException("绑定目录需要使用规范路径。");
            directory = Pin(Path.Combine(directory, name));
        }
        foreach (var name in segments[..^1])
        {
            var child = Path.Combine(directory, name);
            if (createParents && mkdir(child, 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17) // 0700; EEXIST
                throw new IOException("无法创建素材目录。", new Win32Exception(Marshal.GetLastPInvokeError()));
            directory = Pin(child);
        }
        return Path.Combine(directory, segments[^1]);

        string Pin(string path)
        {
            var handle = LinuxOpen(path, DirectoryOnly);
            directories.Add(handle);
            // Resolve children from the opened directory, even if its original name is replaced.
            return "/proc/self/fd/" + handle.DangerousGetHandle();
        }
    }

    private FileStream OpenLinuxSource()
    {
        using var handle = LinuxOpen(FilePath, 0);
        const int emptyPath = 0x1000, typeAndLinks = 5, typeMask = 0xf000, regular = 0x8000;
        if (statx(handle, "", emptyPath, typeAndLinks, out var stat) != 0 ||
            (stat.Mask & typeAndLinks) != typeAndLinks || (stat.Mode & typeMask) != regular || stat.LinkCount != 1)
            throw new IOException("请选择绑定目录内的普通文件。文件链接不能作为素材来源。");
        return File.OpenRead("/proc/self/fd/" + handle.DangerousGetHandle());
    }

    private static SafeFileHandle LinuxOpen(string path, int flags)
    {
        var fd = open(path, LinuxPath | NoFollow | CloseOnExec | flags);
        if (fd < 0) throw new IOException("无法打开绑定目录中的素材路径。", new Win32Exception(Marshal.GetLastPInvokeError()));
        return new SafeFileHandle(fd, ownsHandle: true);
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(28)] public ushort Mode;
    }
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int mkdir(string path, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int statx(SafeFileHandle fd, string path, int flags, uint mask, out LinuxStatx stat);
}
