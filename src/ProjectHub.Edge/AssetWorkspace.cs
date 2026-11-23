using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProjectHub.Edge;

// Pin each Windows directory while opening the file so a concurrent rename or junction cannot redirect the transfer.
internal sealed class AssetWorkspace : IDisposable
{
    private readonly List<SafeFileHandle> directories = [];
    internal string FilePath { get; }
    internal string RelativePath { get; }

    internal AssetWorkspace(string root, string relativePath, bool createParents)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("请使用绑定目录内的相对文件路径。");
        var segments = relativePath.Split(['/', '\\']);
        foreach (var component in segments)
        {
            var device = component.Split('.')[0].TrimEnd().ToUpperInvariant();
            if (component.Length == 0 || component.EndsWith('.') || component.EndsWith(' ') ||
                component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                device is "CON" or "NUL" or "AUX" or "PRN" or "CONIN$" or "CONOUT$" ||
                (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal))
                    && "0123456789¹²³".Contains(device[3])))
                throw new ArgumentException("素材路径包含不支持的目录名、文件名或路径跳转。");
        }
        RelativePath = string.Join('/', segments);
        FilePath = Path.Join(root, Path.Combine(segments));
        var current = Path.GetPathRoot(root) ?? throw new ArgumentException("绑定目录无效。");
        try
        {
            PinDirectory(current);
            foreach (var segment in root[current.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                PinDirectory(current);
            }
            foreach (var segment in segments[..^1])
            {
                current = Path.Combine(current, segment);
                if (createParents && !Directory.Exists(current)) Directory.CreateDirectory(current);
                PinDirectory(current);
            }
        }
        catch { Dispose(); throw; }
    }

    private void PinDirectory(string path)
    {
        var handle = OpenExisting(path, directory: true);
        directories.Add(handle);
        var info = Inspect(handle);
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new IOException("素材路径不能经过目录链接或重解析点。");
    }

    internal FileStream OpenSource()
    {
        var handle = OpenExisting(FilePath, directory: false);
        try
        {
            var info = Inspect(handle);
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 || info.NumberOfLinks != 1)
                throw new IOException("请选择绑定目录内的普通文件。文件链接不能作为素材来源。");
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    internal async Task SaveAsync(byte[] bytes, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(FilePath)!, ".projecthub-" + Guid.NewGuid().ToString("N") + ".tmp");
        var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            await using (output) { await output.WriteAsync(bytes, ct); }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, FilePath, overwrite: false);
        }
        catch { File.Delete(temp); throw; }
    }

    public void Dispose()
    {
        foreach (var handle in directories) handle.Dispose();
    }

    private static SafeFileHandle OpenExisting(string path, bool directory)
    {
        const uint openReparsePoint = 0x00200000, backupSemantics = 0x02000000, readAttributes = 0x80, genericRead = 0x80000000;
        var handle = CreateFileW(path, directory ? readAttributes : genericRead, FileShare.Read,
            IntPtr.Zero, 3, openReparsePoint | (directory ? backupSemantics : 0u), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            handle.Dispose();
            throw new IOException("无法打开绑定目录中的素材路径：" + error.Message, error);
        }
        return handle;
    }

    private static HandleInfo Inspect(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var info) ? info : throw new IOException("无法验证素材路径。");

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInfo info);
}
