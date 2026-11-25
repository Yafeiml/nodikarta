using System.Security.Cryptography;
using System.Text;

namespace ProjectHub.Edge.Desktop;

internal sealed class ReleasePackage
{
    internal const string Desktop = "ProjectHub.Edge.Desktop.exe";
    internal const string Cli = "ProjectHub.Edge.exe";
    internal const string NativeCli = "NodicartaEdgeCli.dll";
    internal const string Manifest = "SHA256SUMS";
    internal const string Lease = ".edge-lease";
    internal const string Journal = ".nodicarta-install-transaction.json";
    internal const string Receipt = "public-install.json";
    internal const string Uninstaller = "Uninstall.exe";
    internal string DirectoryPath { get; }
    internal string Id { get; }
    internal string Executable => Path.Combine(DirectoryPath, Desktop);
    private readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<string, string> Files => files;

    internal ReleasePackage(string directory, bool bundle = false)
    {
        DirectoryPath = bundle ? Path.Combine(directory, "bin") : directory;
        var manifest = Path.Combine(directory, Manifest);
        CodexAccessFile.CheckPath(manifest);
        if (new FileInfo(manifest).Length > 512 * 1024) throw new InvalidDataException("Invalid package manifest.");
        byte[] bytes = File.ReadAllBytes(manifest);
        Id = Convert.ToHexString(SHA256.HashData(bytes));
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 67 || line.Substring(64, 2) != "  " || !line[..64].All(char.IsAsciiHexDigit))
                throw new InvalidDataException("Invalid package manifest.");
            var file = line[66..];
            if (!IsRuntimePath(file) || !files.TryAdd(file, line[..64]))
                throw new InvalidDataException("Invalid package file name.");
        }
        if (!files.ContainsKey(Desktop) || !files.ContainsKey(Cli) || !files.ContainsKey(NativeCli))
            throw new InvalidDataException("The Edge package is incomplete.");
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        Manifest, Lease, Journal, Receipt, Uninstaller, "active-release", "Nodicarta.Edge.Launcher.exe", "hostfxr.dll"
    };

    internal static bool IsRuntimePath(string path)
    {
        var parts = path.Split('/');
        return !Reserved.Contains(parts[0]) && !parts[0].StartsWith(".nodicarta-install-", StringComparison.OrdinalIgnoreCase) &&
            parts.All(part => part is not ("" or "." or "..") && !part.EndsWith('.') && !part.EndsWith(' ') &&
                part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !part.Contains('\\') && !part.Contains(':'));
    }

    internal static string Hash(string path)
    {
        CodexAccessFile.CheckPath(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static bool Matches(string path, string digest) =>
        File.Exists(path) && string.Equals(Hash(path), digest, StringComparison.OrdinalIgnoreCase);

    internal static bool IsInstalledLauncher(string root, ReleasePackage active) => Matches(Path.Combine(root, Cli), active.files[Cli]);

    internal void Verify(bool allowMissing = false)
    {
        foreach (var (name, digest) in files)
        {
            var path = Path.Combine(DirectoryPath, name);
            CodexAccessFile.CheckPath(path);
            if ((!allowMissing || File.Exists(path)) && !Matches(path, digest))
                throw new InvalidDataException("Edge package verification failed: " + path);
        }
    }

    internal void VerifyBundle()
    {
        var seen = 0;
        foreach (var path in RegularFiles(DirectoryPath))
        {
            var name = Path.GetRelativePath(DirectoryPath, path).Replace('\\', '/');
            if (!files.TryGetValue(name, out var digest)) throw new InvalidDataException("Unexpected file in Edge package: " + name);
            if (!Matches(path, digest)) throw new InvalidDataException("Edge package verification failed: " + path);
            seen++;
        }
        if (seen != files.Count) throw new InvalidDataException("Missing files in Edge package.");
    }

    internal static IEnumerable<string> RegularFiles(string directory)
    {
        CodexAccessFile.CheckPath(directory);
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unexpected link in Edge package.");
            if (item is DirectoryInfo dir)
            {
                foreach (var child in RegularFiles(dir.FullName)) yield return child;
            }
            else yield return item.FullName;
        }
    }

    internal void RemoveInstalledFiles()
    {
        foreach (var (relative, digest) in files)
        {
            var path = Path.Combine(DirectoryPath, relative);
            try
            {
                if (Matches(path, digest)) File.Delete(path);
            }
            catch (CodexAccessFile.LinkedPathException) { }
        }
        PruneDirectories(DirectoryPath, files.Keys);
        File.Delete(Path.Combine(DirectoryPath, Manifest));
    }

    internal static void PruneDirectories(string root, IEnumerable<string> files)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in files)
            for (var parent = Path.GetDirectoryName(Path.Combine(root, relative)); parent != null && parent.Length > root.Length;
                parent = Path.GetDirectoryName(parent)) dirs.Add(parent);
        foreach (var path in dirs.OrderByDescending(path => path.Length))
        {
            try
            {
                CodexAccessFile.CheckPath(path);
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            }
            catch (CodexAccessFile.LinkedPathException) { }
        }
    }

    internal static void CopyAtomically(string source, string path, string? stage = null)
    {
        CodexAccessFile.CheckPath(source);
        CodexAccessFile.CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Replace(path, stage, stream =>
        {
            using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            input.CopyTo(stream);
        });
    }

    internal static void WriteAtomically(string path, byte[] bytes, string? stage = null) =>
        Replace(path, stage, stream => stream.Write(bytes));

    private static void Replace(string path, string? stage, Action<FileStream> write)
    {
        CodexAccessFile.CheckPath(path);
        var tmp = stage ?? path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        if (stage != null)
        {
            CodexAccessFile.CheckPath(stage);
            Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
            File.Delete(stage);
        }
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            var delay = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try { File.Move(tmp, path, true); break; }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && delay.ElapsedMilliseconds < 2000)
                { Thread.Sleep(40); }
            }
        }
        finally { File.Delete(tmp); }
    }

    internal static ReleasePackage? Active(string root)
    {
        var manifest = Path.Combine(root, Manifest);
        CodexAccessFile.CheckPath(manifest);
        return File.Exists(manifest) ? new ReleasePackage(root) : null;
    }
}
