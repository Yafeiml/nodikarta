using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ProjectHub.Edge.Desktop;

internal sealed class DesktopStartup(string executable, string stateDirectory, string folder, Func<string, bool> approved)
{
    private const string CurrentLink = "Nodicarta Edge.lnk";
    private const string LegacyLink = "ProjectHub Edge.lnk";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    internal static DesktopStartup ForCurrentUser(string state)
    {
        var executable = EdgeInstallCommand.DesktopEntry(Environment.ProcessPath!);
        var testChannel = WindowsAotReleaseMetadata.IsFixture;
        var testHome = Environment.GetEnvironmentVariable("NODICARTA_EDGE_SETUP_TEST_HOME");
        if (testChannel && Environment.GetEnvironmentVariable("NODICARTA_EDGE_SETUP_SCOPE") == "fixture" &&
            !string.IsNullOrWhiteSpace(testHome) && Path.IsPathFullyQualified(testHome))
            return new DesktopStartup(executable, state, Path.Combine(testHome, "Startup"), _ => true);

        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup, Environment.SpecialFolderOption.DoNotVerify);
        return new DesktopStartup(executable, state, folder, IsApproved);
    }

    internal static bool IsApproved(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        var value = key?.GetValue(name);
        return value == null || value is byte[] { Length: >= 4 } bytes && BitConverter.ToUInt32(bytes) is 2 or 6;
    }

    private List<string> RegisteredLinks()
    {
        if (!Path.IsPathFullyQualified(folder)) throw new IOException(UiLanguage.Text(
            "无法读取当前用户的启动文件夹。", "The Windows Startup folder is unavailable."));
        var paths = new List<string>(2);
        foreach (var name in new[] { CurrentLink, LegacyLink })
        {
            var path = Path.Combine(folder, name);
            CodexAccessFile.CheckPath(path);
            using (var stream = CodexAccessFile.OpenConfig(path))
                if (stream == null) continue;
            var owned = DesktopLocation.WithShortcut<bool>(path, link =>
                string.Equals((string)link.TargetPath, executable, StringComparison.OrdinalIgnoreCase) &&
                (string)link.Description is "Nodicarta Edge" or "ProjectHub Edge" &&
                Regex.IsMatch(((string)link.Arguments).Trim(), """^(?:--background(?:\s+--state\s+(?:"[^"]+"|\S+))?|--state\s+(?:"[^"]+"|\S+)\s+--background)$""",
                    RegexOptions.CultureInvariant));
            if (!owned) throw new InvalidOperationException(UiLanguage.Text(
                $"请先核对 Windows 启动文件夹中的同名快捷方式：{name}", $"Review the existing shortcut named {name} in the Windows Startup folder."));
            paths.Add(path);
        }
        return paths;
    }

    internal (bool Registered, bool Enabled) Read()
    {
        var links = RegisteredLinks();
        return (links.Count > 0, links.Any(path => approved(Path.GetFileName(path))));
    }

    internal void Set(bool enabled)
    {
        using var user = WindowsIdentity.GetCurrent();
        using var installLock = new Mutex(false, @"Local\Nodicarta.Edge.Desktop.Install." + user.User!.Value);
        bool entered;
        try { entered = installLock.WaitOne(0); }
        catch (AbandonedMutexException) { entered = true; }
        if (!entered) throw new IOException(UiLanguage.Text("Edge 正在安装更新，请稍后重试。", "Edge is being installed. Try again shortly."));
        try
        {
            var paths = RegisteredLinks();
            if (!enabled)
            {
                foreach (var path in paths) File.Delete(path);
                return;
            }
            if (paths.Count > 0) return;

            var pathToCreate = Path.Combine(folder, CurrentLink);
            ReleaseLinks.Write(pathToCreate, executable, "--background " + ReleaseLinks.StateArguments(stateDirectory), overwrite: false);
        }
        finally { installLock.ReleaseMutex(); }
    }
}
