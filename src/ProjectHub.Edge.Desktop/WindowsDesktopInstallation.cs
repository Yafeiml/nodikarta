using Microsoft.Win32;
using System.Security;

namespace ProjectHub.Edge.Desktop;

internal static class WindowsDesktopInstallation
{
    // Cursor's WinGet manifests record both spellings (3.0.16 vs 3.31.15).
    internal static readonly string[] CursorProducts = new[] {
        "{DADADADA-ADAD-ADAD-ADAD-ADADADADADAD}", "{D7D7D7D7-7D7D-7D7D-7D7D-7D7D7D7D7D7D}",
        "{DBDBDBDB-BDBD-BDBD-BDBD-BDBDBDBDBDBD}", "{D8D8D8D8-8D8D-8D8D-8D8D-8D8D8D8D8D8D}"
    }.SelectMany(id => new[] { id + "_is1", id + "}_is1" }).ToArray();
    private static readonly string[] OpenCodeProducts = ["d074f30d-5f88-5885-b075-be1348cc7676", "OpenCode"];

    internal static bool CursorDesktop(string local, string programs) =>
        File.Exists(Path.Combine(local, "Programs", "Cursor", "Cursor.exe")) ||
        File.Exists(Path.Combine(programs, "Cursor", "Cursor.exe")) ||
        Registered("Cursor.exe", CursorProducts);

    internal static bool OpenCodeDesktop(string local, string programs) =>
        File.Exists(Path.Combine(local, "OpenCode", "OpenCode.exe")) ||
        File.Exists(Path.Combine(programs, "OpenCode", "OpenCode.exe")) ||
        new[] { "ai.opencode.desktop", "@opencodedesktop", "opencode-desktop", "OpenCode" }
            .Any(folder => File.Exists(Path.Combine(local, "Programs", folder, "OpenCode.exe"))) ||
        Registered("OpenCode.exe", OpenCodeProducts);

    internal static bool Registered(string executable, params string[] products)
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var apps = root.OpenSubKey(uninstall);
                if (apps != null && HasInstalledProduct(apps, products, executable)) return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        return false;
    }

    internal static bool HasInstalledProduct(RegistryKey apps, string[] products, string executable)
    {
        foreach (var product in products)
        {
            using var app = apps.OpenSubKey(product);
            if (app == null) continue;
            if (ContainsExecutable(app.GetValue("InstallLocation") as string, executable)) return true;
            // electron-builder can publish only DisplayIcon in Add/Remove Programs.
            if (app.GetValue("DisplayIcon") is string icon)
            {
                int comma = icon.LastIndexOf(',');
                if (comma >= 0 && int.TryParse(icon.AsSpan(comma + 1), out _)) icon = icon[..comma];
                icon = icon.Trim().Trim('"');
                if (Path.IsPathFullyQualified(icon) && ContainsExecutable(Path.GetDirectoryName(icon), executable)) return true;
            }
        }
        return false;
    }

    private static bool ContainsExecutable(string? directory, string executable) =>
        !string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory) &&
        File.Exists(Path.Combine(directory, executable));
}
