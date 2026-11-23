using System.Security.Cryptography;
using System.Text;

namespace ProjectHub.Edge.Desktop;

internal sealed class ReleasePackage
{
    internal const string Desktop = "ProjectHub.Edge.Desktop.exe";
    internal const string Cli = "ProjectHub.Edge.exe";
    internal const string Launcher = "Nodicarta.Edge.Launcher.exe";
    internal string DirectoryPath { get; }
    internal string Id { get; }
    internal string Executable => Path.Combine(DirectoryPath, "bin", Desktop);
    private readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
    internal static bool IsId(string value) => value.Length == 12 && value.All(char.IsAsciiHexDigitLower);

    internal ReleasePackage(string directory, bool validateId)
    {
        DirectoryPath = directory;
        var manifest = Path.Combine(directory, "SHA256SUMS");
        CodexAccessFile.CheckPath(manifest);
        byte[] bytes = File.ReadAllBytes(manifest);
        Id = Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
        if (validateId && Id != Path.GetFileName(directory)) throw new InvalidDataException("Invalid release directory.");
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 67 || line.Substring(64, 2) != "  " || !line[..64].All(char.IsAsciiHexDigit))
                throw new InvalidDataException("Invalid package manifest.");
            var file = line[66..];
            if (file.Split('/').Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
                !files.TryAdd(file, line[..64])) throw new InvalidDataException("Invalid package file name.");
        }
        if (!files.ContainsKey(Desktop) || !files.ContainsKey(Cli) || !files.ContainsKey(Launcher))
            throw new InvalidDataException("The Edge package is incomplete.");
    }

    internal static string Hash(string path)
    {
        CodexAccessFile.CheckPath(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal bool MatchesLauncher(string hash) => string.Equals(files[Launcher], hash, StringComparison.OrdinalIgnoreCase);

    internal void Verify()
    {
        var bin = Path.Combine(DirectoryPath, "bin");
        var seen = 0;
        foreach (var file in RegularFiles(bin))
        {
            string relative = Path.GetRelativePath(bin, file).Replace('\\', '/');
            if (!files.TryGetValue(relative, out var expected) || !string.Equals(Hash(file), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Edge package verification failed: " + relative);
            seen++;
        }
        if (seen != files.Count) throw new InvalidDataException("Missing files in Edge package.");
    }

    private static IEnumerable<string> RegularFiles(string directory)
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

    internal ReleasePackage InstallInto(string root)
    {
        var destination = Path.Combine(root, Id);
        if (!Directory.Exists(destination))
        {
            var stage = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N") + ".staging")).FullName;
            try
            {
                File.Copy(Path.Combine(DirectoryPath, "SHA256SUMS"), Path.Combine(stage, "SHA256SUMS"));
                foreach (var name in files.Keys)
                {
                    var path = Path.Combine(stage, "bin", name);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.Copy(Path.Combine(DirectoryPath, "bin", name), path);
                }
                new ReleasePackage(stage, false).Verify();
                Directory.Move(stage, destination);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
        }
        var result = new ReleasePackage(destination, true);
        result.Verify();
        return result;
    }

    internal static void WriteAtomically(string path, byte[] bytes)
    {
        CodexAccessFile.CheckPath(path);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, true);
        }
        finally { File.Delete(tmp); }
    }

    internal static ReleasePackage? Active(string root)
    {
        var pointer = Path.Combine(root, "active-release");
        CodexAccessFile.CheckPath(pointer);
        if (!File.Exists(pointer)) return null;
        var id = File.ReadAllText(pointer);
        if (!IsId(id)) throw new InvalidDataException("Invalid current Edge release.");
        return new ReleasePackage(Path.Combine(root, id), true);
    }
}
