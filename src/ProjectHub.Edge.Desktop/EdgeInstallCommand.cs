using System.Security.Cryptography;

namespace ProjectHub.Edge.Desktop;

internal enum EdgeCommandKind { Exact, SameInstall, OtherCopy, Missing, Unknown }

internal static class EdgeInstallCommand
{
    private const string ExeName = "ProjectHub.Edge.exe";
    private const string ActiveRelease = "active-release";
    private const string LauncherName = "Nodicarta.Edge.Launcher.exe";
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool ReleaseId(string id) => id.Length == 12 && id.All(char.IsAsciiHexDigitLower);

    // Only installed releases have a directory named after the hash of their verified package manifest.
    private static string? ReleaseRoot(string cli)
    {
        var bin = new FileInfo(cli).Directory;
        var release = bin?.Parent;
        if (release?.Parent == null || !Same(bin!.Name, "bin") || !ReleaseId(release.Name)) return null;
        return InManifest(release, ExeName, Hash(cli)) ? release.Parent.FullName : null;
    }

    private static string Hash(string path)
    {
        CodexAccessFile.CheckPath(path);
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static bool InManifest(DirectoryInfo release, string file, string hash)
    {
        var manifest = Path.Combine(release.FullName, "SHA256SUMS");
        if (!File.Exists(manifest)) return false;
        CodexAccessFile.CheckPath(manifest);
        using var stream = File.OpenRead(manifest);
        if (!Convert.ToHexString(SHA256.HashData(stream)).StartsWith(release.Name, StringComparison.OrdinalIgnoreCase)) return false;
        stream.Position = 0;
        var expected = hash + "  " + file;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            if (Same(line, expected)) return true;
        return false;
    }

    private static bool HasActiveRelease(string root)
    {
        var pointer = Path.Combine(root, ActiveRelease);
        CodexAccessFile.CheckPath(pointer);
        if (new FileInfo(pointer) is not { Exists: true, Length: 12 }) return false;
        using var stream = File.Open(pointer, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var id = reader.ReadToEnd();
        if (!ReleaseId(id)) return false;
        var target = Path.Combine(root, id, "bin", ExeName);
        if (!File.Exists(target) || !Same(ReleaseRoot(target), root)) return false;
        var hash = Hash(Path.Combine(root, ExeName));
        if (InManifest(new DirectoryInfo(Path.Combine(root, id)), LauncherName, hash)) return true;
        // Rollback or an open MCP session may keep a verified launcher from an earlier release.
        return KnownLauncher(root, hash, id);
    }

    private static bool KnownLauncher(string root, string hash, string? skip = null) =>
        new DirectoryInfo(root).EnumerateDirectories().Any(d => ReleaseId(d.Name) && !Same(d.Name, skip) &&
            !d.Attributes.HasFlag(FileAttributes.ReparsePoint) && InManifest(d, LauncherName, hash));

    private static bool BrokenLauncher(string command)
    {
        var root = Path.GetDirectoryName(command)!;
        if (File.Exists(Path.Combine(root, ActiveRelease))) return !File.Exists(command) || !HasActiveRelease(root);
        // An installed launcher without its pointer cannot run. A portable CLI carries its DLL beside it.
        return File.Exists(command) && !File.Exists(Path.Combine(root, "ProjectHub.Edge.dll")) && KnownLauncher(root, Hash(command));
    }

    internal static string Resolve(string releaseCli)
    {
        var cli = Path.GetFullPath(releaseCli);
        try
        {
            if (!Same(Path.GetFileName(cli), ExeName) || ReleaseRoot(cli) is not { } root) return cli;
            var launcher = Path.Combine(root, ExeName);
            CodexAccessFile.CheckPath(cli);
            CodexAccessFile.CheckPath(launcher);
            if (File.Exists(launcher) && HasActiveRelease(root)) return launcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return cli;
    }

    internal static EdgeCommandKind Classify(string previous, string currentEffective)
    {
        try
        {
            if (!Path.IsPathFullyQualified(previous) || !Same(Path.GetFileName(previous), ExeName) ||
                !Path.IsPathFullyQualified(currentEffective) || !File.Exists(currentEffective)) return EdgeCommandKind.Unknown;
            previous = Path.GetFullPath(previous);
            currentEffective = Path.GetFullPath(currentEffective);
            CodexAccessFile.CheckPath(previous);
            CodexAccessFile.CheckPath(currentEffective);
            var directory = Path.GetDirectoryName(previous)!;
            // A disconnected or incomplete installation with a launcher must not be taken over.
            if (BrokenLauncher(previous) || (!Same(previous, currentEffective) && BrokenLauncher(currentEffective)))
                return EdgeCommandKind.Unknown;
            if (Same(previous, currentEffective)) return EdgeCommandKind.Exact;
            var currentReleaseRoot = ReleaseRoot(currentEffective);
            if (Same(currentReleaseRoot, directory)) return EdgeCommandKind.Unknown;
            try
            {
                using var file = File.Open(previous, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                for (string? parent = directory; parent != null; parent = Path.GetDirectoryName(parent))
                {
                    try { _ = File.GetAttributes(parent); return EdgeCommandKind.Missing; }
                    catch (IOException missing) when (missing is FileNotFoundException or DirectoryNotFoundException) { }
                }
                return EdgeCommandKind.Unknown;
            }
            var root = ReleaseRoot(previous);
            if (root != null && Same(root, currentReleaseRoot)) return EdgeCommandKind.Unknown;
            return root != null && Same(Path.Combine(root, ExeName), currentEffective) && HasActiveRelease(root)
                ? EdgeCommandKind.SameInstall : EdgeCommandKind.OtherCopy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return EdgeCommandKind.Unknown;
    }
}
