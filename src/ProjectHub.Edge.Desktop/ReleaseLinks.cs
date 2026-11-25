using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProjectHub.Edge.Desktop;

internal sealed class ReleaseLinks
{
    private const string CurrentName = "Nodicarta Edge.lnk";
    private const string LegacyName = "ProjectHub Edge.lnk";
    internal string[] Paths { get; }
    internal string? ExistingDesktop { get; }
    internal string? ExistingState { get; }
    internal bool StartupEnabled { get; }

    internal ReleaseLinks(string? scope, bool pathsOnly = false)
    {
        string Folder(Environment.SpecialFolder folder)
        {
            var path = scope == null ? Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify) : Path.Combine(scope, folder.ToString());
            return Path.IsPathFullyQualified(path) ? path : throw new IOException("The current user's shortcut directory is unavailable.");
        }
        var programs = Folder(Environment.SpecialFolder.Programs);
        var startup = Folder(Environment.SpecialFolder.Startup);
        Paths = [Path.Combine(programs, CurrentName), Path.Combine(startup, CurrentName),
                 Path.Combine(programs, LegacyName), Path.Combine(startup, LegacyName)];
        if (pathsOnly) return;
        var current = Read(Paths[0]);
        var legacy = Read(Paths[2]);
        var menu = current ?? legacy;
        var auto = Read(Paths[1]);
        var legacyAuto = Read(Paths[3]);
        var anchor = menu ?? auto ?? legacyAuto;
        if (anchor != null && new[] { current, legacy, auto, legacyAuto }.Any(link => link != null && !Same(link.Value.Target, anchor.Value.Target)))
            throw new IOException("Edge shortcuts point to conflicting installations.");
        ExistingDesktop = anchor?.Target;
        var states = new[] { current, legacy, auto, legacyAuto }.Where(link => link != null)
            .Select(link => RequestedState(link!.Value.Arguments)).OfType<string>()
            .Select(state => Path.TrimEndingDirectorySeparator(Path.GetFullPath(state))).ToArray();
        ExistingState = states.FirstOrDefault();
        if (ExistingState != null && states.Any(state => !EdgeStatePaths.Same(state, ExistingState)))
            throw new IOException("Edge shortcuts select conflicting data directories.");
        StartupEnabled = anchor == null || auto != null || (legacyAuto != null && (scope != null || DesktopStartup.IsApproved(LegacyName)));
    }

    internal static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static (string Target, string Arguments)? Read(string path)
    {
        CodexAccessFile.CheckPath(path);
        if (!File.Exists(path)) return null;
        return DesktopLocation.WithShortcut<(string, string)>(path, link =>
        {
            string target = link.TargetPath;
            if (!Path.IsPathFullyQualified(target) ||
                !(Same(Path.GetFileName(target), ReleasePackage.Desktop) || Same(Path.GetFileName(target), ReleasePackage.Cli)) ||
                ((string)link.Description is not ("Nodicarta Edge" or "ProjectHub Edge") && !IsEdgeDesktop(target)))
                throw new IOException("The existing shortcut is not owned by Nodicarta Edge: " + path);
            return (target, (string)link.Arguments);
        });
    }

    internal static bool IsEdgeDesktop(string path) => Path.IsPathFullyQualified(path) && Same(Path.GetFileName(path), ReleasePackage.Desktop) &&
        File.Exists(path) && FileVersionInfo.GetVersionInfo(path).ProductName is "Nodicarta Edge" or "ProjectHub Edge";

    internal static bool UnderInstallRoot(string root, string target) => Same(target, Path.Combine(root, ReleasePackage.Cli)) ||
        Same(target, Path.Combine(root, ReleasePackage.Desktop));

    internal void SetTarget(string target, string state)
    {
        var args = StateArguments(state);
        Write(Paths[0], target, args);
        if (StartupEnabled) Write(Paths[1], target, "--background " + args);
        foreach (var legacy in Paths.Skip(2)) if (File.Exists(legacy)) File.Delete(legacy);
    }

    internal static string StateArguments(string state)
    {
        state = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state));
        return "--state \"" + state + (state.EndsWith('\\') ? "\\" : "") + "\"";
    }

    internal static void Write(string path, string target, string args, bool overwrite = true)
    {
        CodexAccessFile.CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            DesktopLocation.WithShortcut<bool>(temp, link =>
            {
                link.TargetPath = target;
                link.Arguments = args;
                link.Description = "Nodicarta Edge";
                link.WorkingDirectory = Path.GetDirectoryName(target)!;
                link.IconLocation = target + ",0";
                link.Save();
                return true;
            });
            File.Move(temp, path, overwrite);
        }
        finally { File.Delete(temp); }
    }

    internal static string? RequestedState(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return null;
        var values = (ReadArguments("Edge.exe " + args) ?? throw new IOException("Invalid desktop shortcut."))
            .Where(value => value != "--background").ToArray();
        return values switch
        {
            [] => null,
            ["--state", string state] when Path.IsPathFullyQualified(state) => state,
            _ => throw new IOException("Unrecognized Edge shortcut arguments.")
        };
    }

    internal static bool IsMcpCommand(SafeProcessHandle process)
    {
        const int processCommandLineInformation = 60;
        const int capacity = 65552;
        int header = 2 * IntPtr.Size;
        nint buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            // Query the same process handle used to verify identity and terminate.
            if (NtQueryInformationProcess(process, processCommandLineInformation, buffer, capacity, out int written) != 0 ||
                written < header || written > capacity) return false;
            int bytes = (ushort)Marshal.ReadInt16(buffer);
            int maximumBytes = (ushort)Marshal.ReadInt16(buffer, sizeof(short));
            nint commandPointer = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            nint offset = commandPointer - buffer;
            if (bytes == 0 || (bytes & 1) != 0 || maximumBytes < bytes ||
                offset < header || offset > written - maximumBytes) return false;
            string command = Marshal.PtrToStringUni(commandPointer, bytes / sizeof(char));
            if (command.Contains('\0') || ReadArguments(command) is not { } values) return false;

            // The Edge CLI removes the first --state and its value before selecting the command.
            int state = Array.IndexOf(values, "--state");
            if (state >= 0 && (state + 1 == values.Length || values[state + 1] is not { } statePath ||
                statePath.StartsWith("--", StringComparison.Ordinal))) return false;
            return values.ElementAtOrDefault(state == 0 ? 2 : 0) is "mcp-stdio" or "mcp-stdio-global" or "mcp-http";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string?[]? ReadArguments(string command)
    {
        var parsed = CommandLineToArgvW(command, out var count);
        if (parsed == 0) return null;
        try
        {
            return count > 0 ? Enumerable.Range(1, count - 1)
                .Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(parsed, i * IntPtr.Size))).ToArray() : null;
        }
        finally { LocalFree(parsed); }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        nint buffer, int capacity, out int written);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
