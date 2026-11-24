using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;

namespace ProjectHub.Edge.Desktop;

internal static class Program
{
    internal static string ReleaseVersion { get; } = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];

    internal static string DefaultStateDirectory => ProcessStateOverride("NODICARTA_EDGE_STATE")
        ?? ProcessStateOverride("PROJECTHUB_EDGE_STATE")
        ?? DesktopRunRecord.Read()?.StateDirectory
        ?? Environment.GetEnvironmentVariable("NODICARTA_EDGE_STATE")
        ?? Environment.GetEnvironmentVariable("PROJECTHUB_EDGE_STATE")
        ?? Environment.GetEnvironmentVariable("NODICARTA_EDGE_STATE", EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable("PROJECTHUB_EDGE_STATE", EnvironmentVariableTarget.User)
        ?? EdgeStatePaths.LocalDefault;

    private static string? ProcessStateOverride(string variable)
    {
        var current = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(current)) return null;
        // A persistent legacy default is normally inherited by every process, including
        // a newly moved desktop. Preserve the last explicitly selected data directory.
        var inherited = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.User) ??
            Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Machine);
        if (inherited == null) return current;
        return EdgeStatePaths.Same(Path.TrimEndingDirectorySeparator(current), Path.TrimEndingDirectorySeparator(inherited))
            ? null : current;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--version"]) { Console.WriteLine(ReleaseVersion); return 0; }
#if LINUX_DESKTOP
        return LinuxDesktop.Run(args);
#else
        if (ReleaseInstallation.TryRun(args, out var installCode)) return installCode;
        var codexAction = args.FirstOrDefault() is "--connect-codex" or "--disconnect-codex";
        var refreshRegistration = args.FirstOrDefault() is "--refresh-codex-registration" or "--refresh-harness-registration";
        if (codexAction || refreshRegistration) Console.OutputEncoding = new UTF8Encoding(false);
        var background = args.Contains("--background");
        try
        {
            if (refreshRegistration)
            {
                if (args is not [_, _] and not [_, _, "--state", _]) throw new ArgumentException("请指定 Edge 安装目录。");
                var state = args.Length == 4 ? args[3] : DefaultStateDirectory;
                DesktopInstance.RunMaintenance(() =>
                {
                    if (args[0] == "--refresh-codex-registration")
                    {
                        GlobalCodexAccess.RefreshManagedCommand(args[1], state);
                        return;
                    }
                    var launcher = Path.Combine(Path.GetFullPath(args[1]), "ProjectHub.Edge.exe");
                    if (!string.Equals(launcher, GlobalCodexAccess.CurrentCommand, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("无法确认此 Edge 安装的启动路径。");
                    var existing = EdgeStatePaths.Resolve(state);
                    if (Directory.Exists(existing))
                    {
                        var harnesses = new HarnessAccess(new EdgeDesktopClient(existing));
                        harnesses.MigrateRegisteredState();
                        harnesses.MigrateInstalledCommands();
                    }
                });
                return 0;
            }
            var stateArgs = codexAction ? args[1..] : args.Where(value => value != "--background").ToArray();
            var requested = stateArgs switch
            {
                [] => null,
                ["--state", var path] when !string.IsNullOrWhiteSpace(path) => path,
                _ => throw new ArgumentException("可用参数为 --background 和 --state <数据目录>。")
            };
            if (codexAction)
            {
                var access = new GlobalCodexAccess(new EdgeDesktopClient(ResolveDesktopDirectory(requested ?? DefaultStateDirectory)));
                var action = args[0] == "--connect-codex" ? access.InstallAsync() : access.CleanForReconnectAsync();
                Console.WriteLine(action.GetAwaiter().GetResult());
                return 0;
            }
            var executable = Environment.ProcessPath!;
            using var instance = DesktopInstance.Enter(executable, background);
            if (instance == null) return 0;
            var previous = DesktopRunRecord.Read();
            var selectedState = requested ?? DefaultStateDirectory;
            var legacy = LegacyDesktop.Retire(EdgeStatePaths.Normalize(selectedState), background);
            if (!legacy.Continue) return 0;
            var directory = ResolveDesktopDirectory(selectedState);
            using var legacyWake = DesktopInstance.OpenLegacyWake(directory, out var first);
            if (!first) throw DesktopInstance.AnotherSession();
            var client = new EdgeDesktopClient(directory);
            var record = new DesktopRunRecord(executable, directory);
            return AppBuilder.Configure(() => new App { Client = client, Wake = instance.Wake, LegacyWake = legacyWake,
                    Retire = instance.Retire, Background = background, Started = () =>
                    {
                        DesktopLocation.UpdateShortcuts(legacy.PreviousExe ?? previous?.Executable, executable);
                        if (record != previous)
                        {
                            try { record.Save(); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            { System.Diagnostics.Trace.TraceWarning("Could not save the last Edge desktop location: {0}", ex.Message); }
                        }
                    } })
                .UseWin32().UseSkia().StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception error)
        {
            if (codexAction || refreshRegistration) Console.Error.WriteLine(UiLanguage.Message(error.Message));
            else if (!background)
                MessageBoxW(0, UiLanguage.Text("无法启动脉络 Edge。", "Could not start Nodicarta Edge.") + "\n" + UiLanguage.Message(error.Message), UiLanguage.Product, 0x10);
            return 1;
        }
#endif
    }

    internal static string ResolveDesktopDirectory(string requested)
    {
        string directory;
        try
        {
            directory = EdgeStatePaths.Resolve(requested);
            // When a shortcut targets a physical Core profile, validate its pin before sharing the root desktop instance.
            if (Path.GetFileName(directory).StartsWith(".edge-core-", StringComparison.OrdinalIgnoreCase))
                directory = EdgeSession.Open(directory).RootDirectory;
        }
        catch
        {
            // A failed move must still explain the problem in the user's previous UI language.
            var normalized = EdgeStatePaths.Normalize(requested);
            foreach (var candidate in new[] { requested, normalized, EdgeStatePaths.LegacyLocalDefault, EdgeStatePaths.LegacyMachineDefault })
            {
                if (!EdgeStatePaths.Normalize(candidate).Equals(normalized, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var preference = Path.Combine(candidate, "ui-language.txt");
                    CodexAccessFile.CheckPath(preference);
                    if (!File.Exists(preference)) continue;
                    UiLanguage.Load(candidate);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
        UiLanguage.Load(directory);
        return directory;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
