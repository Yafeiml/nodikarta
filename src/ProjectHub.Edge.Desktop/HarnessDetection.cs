using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;

namespace ProjectHub.Edge.Desktop;

internal static class HarnessDetection
{
    internal static bool OnPath(params string[] names) => names.Any(name => FindOnPath(name) != null);

    internal static string? FindOnPath(string name)
    {
        var directories = EnvironmentValues("PATH").SelectMany(value => value.Split(Path.PathSeparator));
        foreach (var entry in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(dir)) continue;
            foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".ps1" })
            {
                var file = Path.Combine(dir, name + extension);
                if (File.Exists(file)) return file;
            }
        }
        return null;
    }

    internal static bool CodexInstalled() => PackageRegistered("OpenAI.Codex_2p2nqsd0c76g0") ||
        CodexFilesInstalled(GlobalCodexAccess.UserHome, EnvironmentValues);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, nint names, ref uint length, nint buffer);

    internal static bool PackageRegistered(string family)
    {
        uint count = 0, length = 0;
        const int insufficientBuffer = 122;
        return GetPackagesByPackageFamily(family, ref count, 0, ref length, 0) == insufficientBuffer && count > 0;
    }

    private static IEnumerable<string> EnvironmentValues(string name)
    {
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? value;
            try { value = Environment.GetEnvironmentVariable(name, scope); }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { continue; }
            if (!string.IsNullOrWhiteSpace(value)) yield return Environment.ExpandEnvironmentVariables(value);
        }
    }

    internal static bool CodexFilesInstalled(string home, Func<string, IEnumerable<string>> env)
    {
        var local = env("LOCALAPPDATA").FirstOrDefault() ?? Path.Combine(home, "AppData", "Local");
        var roaming = env("APPDATA").FirstOrDefault() ?? Path.Combine(home, "AppData", "Roaming");
        static HashSet<string> Directories(IEnumerable<string> values) => values.Select(value => value.Trim().Trim('"'))
            .Where(Path.IsPathFullyQualified).Select(Path.TrimEndingDirectorySeparator).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var onPath = Directories(env("PATH").SelectMany(value => value.Split(Path.PathSeparator)));
        var npmPrefixes = Directories(env("npm_config_prefix").Append(Path.Combine(roaming, "npm")));
        var paths = onPath.Concat(npmPrefixes).Concat(env("CODEX_INSTALL_DIR"))
            .Append(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin"))
            .Append(Path.Combine(home, ".local", "bin"));
        foreach (var directory in Directories(paths))
        {
            if (File.Exists(Path.Combine(directory, "codex.exe"))) return true;
            var launcher = File.Exists(Path.Combine(directory, "codex.cmd")) || File.Exists(Path.Combine(directory, "codex.ps1"));
            if (onPath.Contains(directory) && !npmPrefixes.Contains(directory) && (launcher || File.Exists(Path.Combine(directory, "codex.bat"))))
                return true;
            var npm = Path.Combine(directory, "node_modules", "@openai", "codex");
            if (launcher && File.Exists(Path.Combine(npm, "bin", "codex.js")) && File.Exists(Path.Combine(npm, "package.json"))) return true;
        }
        var codexHome = env("CODEX_HOME").FirstOrDefault() ?? Path.Combine(home, ".codex");
        if (Path.IsPathFullyQualified(codexHome))
        {
            var current = Path.Combine(codexHome, "packages", "standalone", "current");
            if (File.Exists(Path.Combine(current, "bin", "codex.exe")) || File.Exists(Path.Combine(current, "codex.exe"))) return true;
        }
        return new[] { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" }
            .Any(editor => CodexExtensionInstalled(Path.Combine(home, editor, "extensions")));
    }

    private static bool CodexExtensionInstalled(string directory)
    {
        var index = Path.Combine(directory, "extensions.json");
        if (!File.Exists(index)) return false;
        try
        {
            using var file = File.OpenRead(index);
            using var doc = JsonDocument.Parse(file);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var extension in doc.RootElement.EnumerateArray())
            {
                if (extension.ValueKind != JsonValueKind.Object || !extension.TryGetProperty("identifier", out var identity) ||
                    identity.ValueKind != JsonValueKind.Object || !identity.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String || !string.Equals(id.GetString(), "openai.chatgpt", StringComparison.OrdinalIgnoreCase) ||
                    !extension.TryGetProperty("relativeLocation", out var location) || location.ValueKind != JsonValueKind.String) continue;
                var folder = location.GetString()!;
                if (!folder.StartsWith("openai.chatgpt-", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(folder) != folder) continue;
                var bin = Path.Combine(directory, folder, "bin");
                if (File.Exists(Path.Combine(bin, "windows-x86_64", "codex.exe")) ||
                    (RuntimeInformation.OSArchitecture == Architecture.Arm64 && File.Exists(Path.Combine(bin, "windows-aarch64", "codex.exe")))) return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return false;
    }
}
