using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ProjectHub.Edge.Desktop;

// 1.3.6 and earlier only understood an activation event scoped to one data directory.
internal static class LegacyDesktop
{
    internal static (bool Continue, string? PreviousExe) Retire(string directory, bool background)
    {
        using var self = Process.GetCurrentProcess();
        var windows = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            var name = new StringBuilder(96);
            if (GetClassNameW(hwnd, name, name.Capacity) > 0 && name.ToString().StartsWith("AvaloniaMessageWindow ", StringComparison.Ordinal))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                windows.Add((int)pid);
            }
            return true;
        }, 0);

        var candidates = Process.GetProcessesByName("ProjectHub.Edge.Desktop");
        try
        {
            var owners = new List<Process>();
            foreach (var p in candidates)
            {
                if (p.Id == self.Id || p.HasExited) continue;
                var otherSession = p.SessionId != self.SessionId;
                if (!otherSession && !windows.Contains(p.Id)) continue;
                try
                {
                    // A current desktop may still own a closing HWND just after releasing the gate.
                    var v = p.MainModule?.FileVersionInfo;
                    if (v != null && (new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart) >= new Version(1, 3, 7) ||
                        File.Exists(Path.Combine(Path.GetDirectoryName(p.MainModule!.FileName)!, "nodicarta-release.json")))) continue;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { if (p.HasExited) continue; }
                if (otherSession)
                {
                    if (background) return (false, null);
                    throw DesktopInstance.AnotherSession();
                }
                owners.Add(p);
            }
            using (DesktopInstance.OpenLegacyWake(directory, out var available))
            {
                if (owners.Count == 0 && available) return (true, null);
                if (background) return (false, null);
                if (owners.Count != 1 || available) throw NeedLegacyQuit();
            }
            var old = owners[0];
            var previous = old.MainModule?.FileName;
            if (previous == null || old.MainModule?.FileVersionInfo.ProductName is not ("Nodicarta Edge" or "ProjectHub Edge"))
                throw NeedLegacyQuit();
            if (!QuitThroughTray(old)) throw NeedLegacyQuit();
            return (true, previous);
        }
        finally { foreach (var p in candidates) p.Dispose(); }
    }

    private static InvalidOperationException NeedLegacyQuit() => new(UiLanguage.Text(
        "检测到早期版本的脉络 Edge 正在运行。请从原托盘菜单选择「退出」，然后重新打开新版。",
        "An older Nodicarta Edge desktop is running. Choose Quit in its tray menu, then reopen this version."));

    private static bool QuitThroughTray(Process old)
    {
        // Use the existing graceful upgrade path, and only for a verified same-account GUI.
        try
        {
            var executable = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
                Environment.GetEnvironmentVariable("DEVTOOLS_HOME") is { Length: > 0 } tools
                    ? Path.Combine(tools, "powershell", "pwsh.exe") : null
            }.FirstOrDefault(File.Exists);
            if (executable == null) return false;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Nodicarta.LegacyTrayExit.ps1")!;
            using var reader = new StreamReader(stream);
            var script = reader.ReadToEnd() + $$"""

                $ErrorActionPreference = 'Stop'
                $item = Get-CimInstance Win32_Process -Filter 'ProcessId={{old.Id}}'
                if (!$item) { exit 0 }
                if ((Invoke-CimMethod -InputObject $item -MethodName GetOwnerSid).Sid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { exit 1 }
                $process = Get-Process -Id {{old.Id}}
                if ($process.StartTime.ToUniversalTime().Ticks -ne {{old.StartTime.ToUniversalTime().Ticks}}) { exit 1 }
                Stop-EdgeTestDesktop $process
                """;
            var start = new ProcessStartInfo(executable) { CreateNoWindow = true, UseShellExecute = false };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                start.ArgumentList.Add(arg);
            using var helper = Process.Start(start);
            return helper != null && helper.WaitForExit(25000) && helper.ExitCode == 0 && old.WaitForExit(1000);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        { return old.HasExited; }
    }

    private delegate bool EnumVisitor(nint hwnd, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumVisitor callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
}
