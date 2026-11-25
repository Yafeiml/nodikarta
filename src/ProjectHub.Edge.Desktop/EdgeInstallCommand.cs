namespace ProjectHub.Edge.Desktop;

internal enum EdgeCommandKind { Exact, OtherCopy, Missing, Unknown }

internal static class EdgeInstallCommand
{
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal static string DesktopEntry(string executable)
    {
        var runtime = Path.GetDirectoryName(executable)!;
        var sibling = Path.Combine(runtime, ReleasePackage.Cli);
        return File.Exists(Path.Combine(runtime, ReleasePackage.NativeCli)) && File.Exists(sibling) ? sibling : executable;
    }

    private static bool BrokenLauncher(string command)
    {
        var root = Path.GetDirectoryName(command)!;
        var manifest = Path.Combine(root, ReleasePackage.Manifest);
        var lease = Path.Combine(root, ReleasePackage.Lease);
        if (!File.Exists(manifest) && !File.Exists(lease)) return false;
        CodexAccessFile.CheckPath(lease);
        return !File.Exists(lease) || ReleasePackage.Active(root) is not { } active ||
            !File.Exists(active.Executable) || !File.Exists(Path.Combine(root, ReleasePackage.NativeCli)) ||
            !ReleasePackage.IsInstalledLauncher(root, active);
    }

    internal static EdgeCommandKind Classify(string previous, string currentEffective)
    {
        try
        {
            if (!Path.IsPathFullyQualified(previous) || !Same(Path.GetFileName(previous), ReleasePackage.Cli) ||
                !Path.IsPathFullyQualified(currentEffective) || !File.Exists(currentEffective)) return EdgeCommandKind.Unknown;
            previous = Path.GetFullPath(previous);
            currentEffective = Path.GetFullPath(currentEffective);
            CodexAccessFile.CheckPath(previous);
            CodexAccessFile.CheckPath(currentEffective);
            if (BrokenLauncher(previous) || (!Same(previous, currentEffective) && BrokenLauncher(currentEffective)))
                return EdgeCommandKind.Unknown;
            if (Same(previous, currentEffective)) return EdgeCommandKind.Exact;
            try
            {
                using var file = File.Open(previous, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                for (string? parent = Path.GetDirectoryName(previous); parent != null; parent = Path.GetDirectoryName(parent))
                {
                    try { _ = File.GetAttributes(parent); return EdgeCommandKind.Missing; }
                    catch (IOException missing) when (missing is FileNotFoundException or DirectoryNotFoundException) { }
                }
                return EdgeCommandKind.Unknown;
            }
            return EdgeCommandKind.OtherCopy;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return EdgeCommandKind.Unknown;
    }
}
