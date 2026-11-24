using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ProjectHub.Edge.Desktop;

// Runs from the installer's private extraction directory, outside all release directories.
internal static class ReleaseInstallation
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nodicarta.Edge";
    private const string Journal = ".nodicarta-install-transaction.json";
    private const string Receipt = "public-install.json";
    private const string ReadyPrefix = "Nodicarta.Edge.Install.Ready.";
    private const string ReadyVariable = "NODICARTA_INSTALL_READY_EVENT";
    private static readonly NamedWaitHandleOptions Session = new() { CurrentUserOnly = true, CurrentSessionOnly = true };
    internal sealed record Registration(string? Root, string? Version, string? Icon);
    internal sealed record Transaction(string Target, string? PreviousExe, string State, bool WasRunning, byte[]?[] Before, Registration? Registered);
    private static bool FixtureBuild => WindowsAotReleaseMetadata.IsFixture;

    internal static void SignalReady()
    {
        var name = Environment.GetEnvironmentVariable(ReadyVariable);
        if (name == null || !name.StartsWith(ReadyPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[ReadyPrefix.Length..], "N", out _)) return;
        Environment.SetEnvironmentVariable(ReadyVariable, null);
        if (FixtureBuild && Environment.GetEnvironmentVariable("NODICARTA_EDGE_SETUP_SCOPE") == "fixture" &&
            Environment.GetEnvironmentVariable("NODICARTA_EDGE_SETUP_TEST_HOME") is { } scope &&
            File.Exists(Path.Combine(scope, "withhold-ready-" + Program.ReleaseVersion))) return;
        if (EventWaitHandle.TryOpenExisting(name, Session, out var handle)) using (handle) handle.Set();
    }

    private static string? Scope(string mode)
    {
        if (mode == "standard") return null;
        var home = Environment.GetEnvironmentVariable("NODICARTA_EDGE_SETUP_TEST_HOME");
        if (mode != "fixture" || !FixtureBuild || string.IsNullOrEmpty(home) || !Path.IsPathFullyQualified(home))
            throw new ArgumentException("Invalid isolated installer scope.");
        Environment.SetEnvironmentVariable("NODICARTA_EDGE_SETUP_SCOPE", "fixture");
        CodexAccessFile.CheckPath(home);
        return Directory.CreateDirectory(home).FullName;
    }

    internal static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        bool setup = args.FirstOrDefault() == "--install-release";
        try
        {
            if (!setup)
            {
                if (args.Any(s => s != "--background" && s.StartsWith("--", StringComparison.Ordinal) && s != "--state")) return false;
                var bin = new DirectoryInfo(AppContext.BaseDirectory);
                var release = bin.Parent;
                var parent = release?.Parent;
                if (bin.Name != "bin" || parent == null || !ReleasePackage.IsId(release!.Name) || !File.Exists(Path.Combine(parent.FullName, Journal)))
                    return false;
                using var mutex = InstallMutex();
                if (!TryLock(mutex)) return false; // The active installer is currently testing this new desktop.
                try
                {
                    var scope = Scope(FixtureBuild && Environment.GetEnvironmentVariable("NODICARTA_EDGE_SETUP_SCOPE") == "fixture" ? "fixture" : "standard");
                    var old = Recover(parent.FullName, new ReleaseLinks(scope, pathsOnly: true), scope, restart: false);
                    if (old == null || ReleaseLinks.Same(old.PreviousExe, Environment.ProcessPath)) return false;
                    if (old.PreviousExe != null) using (Start(old.PreviousExe, old.State, args.Contains("--background"), null)) { }
                    return true;
                }
                finally { mutex.ReleaseMutex(); }
            }

            if (args is not [_, var input, var action, var scopeName] || !Path.IsPathFullyQualified(input))
                throw new ArgumentException("Invalid installation arguments.");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
            if (ReleaseLinks.Same(root, Path.GetPathRoot(root))) throw new IOException("Choose a dedicated Edge installation directory.");
            CodexAccessFile.CheckPath(root);
            var scopeDirectory = Scope(scopeName);
            using var installLock = InstallMutex();
            if (!TryLock(installLock)) throw new IOException("Another Edge installation is in progress.");
            try
            {
                Recover(root, new ReleaseLinks(scopeDirectory, pathsOnly: true), scopeDirectory, restart: true);
                var links = new ReleaseLinks(scopeDirectory);
                if (action == "uninstall") Uninstall(root, links, scopeDirectory);
                else Install(root, links, scopeDirectory, action);
            }
            finally { installLock.ReleaseMutex(); }
        }
        catch (Exception error)
        {
            Trace.TraceError("Nodicarta Edge setup: {0}", error.Message);
            var report = Path.Combine(Path.GetTempPath(), "Nodicarta-Edge-Setup.log");
            try { CodexAccessFile.CheckPath(report); File.AppendAllText(report, $"{DateTimeOffset.Now:o} {error.Message}{Environment.NewLine}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (setup && args.ElementAtOrDefault(2) == "interactive") MessageBoxW(0, error.Message, "Nodicarta Edge", 0x10);
            exitCode = 1;
        }
        return true;
    }

    private static Mutex InstallMutex() => new(false, @"Local\Nodicarta.Edge.Desktop.Install." + WindowsIdentity.GetCurrent().User!.Value);
    private static bool TryLock(Mutex mutex)
    {
        try { return mutex.WaitOne(0); } catch (AbandonedMutexException) { return true; }
    }

    private static IEnumerable<string> TrackedFiles(string root, ReleaseLinks links) => links.Paths.Concat(
        new[] { "active-release", Receipt, ReleasePackage.Cli, "Uninstall.exe" }.Select(name => Path.Combine(root, name)));

    private static byte[]? Snapshot(string path)
    {
        CodexAccessFile.CheckPath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Unexpected managed file size: " + path);
        return File.ReadAllBytes(path);
    }

    private static void Restore(string path, byte[]? bytes)
    {
        CodexAccessFile.CheckPath(path);
        if (bytes == null) { if (File.Exists(path)) File.Delete(path); return; }
        if (File.Exists(path) && bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path))) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ReleasePackage.WriteAtomically(path, bytes);
    }

    private static Transaction? Recover(string root, ReleaseLinks links, string? scope, bool restart)
    {
        var path = Path.Combine(root, Journal);
        CodexAccessFile.CheckPath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid pending Edge installation.");
        var tx = JsonSerializer.Deserialize(File.ReadAllBytes(path), DesktopJson.Default.Transaction) ?? throw new InvalidDataException("Invalid pending Edge installation.");
        var tracked = TrackedFiles(root, links).ToArray();
        if (!ReleasePackage.IsId(tx.Target) || tx.Before.Length != tracked.Length || !Path.IsPathFullyQualified(tx.State) ||
            tx.PreviousExe != null && !ReleaseLinks.IsEdgeDesktop(tx.PreviousExe)) throw new InvalidDataException("Invalid pending Edge paths.");
        var candidate = Path.Combine(root, tx.Target, "bin", ReleasePackage.Desktop);
        if (!ReleaseLinks.Same(candidate, tx.PreviousExe)) StopExisting(candidate);
        for (int i = 0; i < tracked.Length; i++) Restore(tracked[i], tx.Before[i]);
        RestoreRegistration(root, scope, tx.Registered);
        File.Delete(path);
        if (restart && tx.WasRunning && tx.PreviousExe != null && !IsResident(tx.PreviousExe))
            using (Start(tx.PreviousExe, tx.State, true, null)) { }
        return tx;
    }

    private static void Install(string root, ReleaseLinks links, string? scope, string action)
    {
        if (action is not ("interactive" or "quiet" or "update" or "migrate")) throw new ArgumentException("Unknown setup action.");
        var key = DesktopUpdates.PublisherKeyId ?? throw new InvalidOperationException("Missing distribution trust key.");
        var source = new ReleasePackage(Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName, false);
        var old = ReleasePackage.Active(root);
        var oldExe = old?.Executable ?? links.ExistingDesktop;
        if (oldExe != null && !ReleaseLinks.IsEdgeDesktop(oldExe)) throw new InvalidDataException("Cannot verify the previous desktop.");
        var oldMarker = Snapshot(Path.Combine(root, Receipt));
        if (oldMarker != null) VerifyPublisher(oldMarker, key);
        var previousMeta = oldExe == null ? null : Path.Combine(Path.GetDirectoryName(oldExe)!, "nodicarta-release.json");
        bool previousPublic = previousMeta != null && File.Exists(previousMeta);
        if (previousPublic)
        {
            CodexAccessFile.CheckPath(previousMeta!);
            using var previous = JsonDocument.Parse(File.ReadAllBytes(previousMeta!));
            if (previous.RootElement.GetProperty("releaseLine").GetString() != "public-v1" ||
                !ReleaseLinks.Same(previous.RootElement.GetProperty("publisherKeyId").GetString(), key) ||
                previous.RootElement.GetProperty("channel").GetString() != DesktopUpdates.Channel)
                throw new IOException("The old release belongs to a different publisher or channel.");
        }
        var version = Version.Parse(Program.ReleaseVersion);
        var previousVersion = oldExe == null ? null : Version.Parse(FileVersionInfo.GetVersionInfo(oldExe).ProductVersion!.Split('+')[0]);
        if (action == "update" && (old == null || oldMarker == null || links.ExistingDesktop != null && !ReleaseLinks.Same(oldExe, links.ExistingDesktop) || previousVersion >= version))
            throw new InvalidOperationException("Automatic update requires a newer release of this installed product.");
        if (oldMarker != null && old == null || (oldMarker != null || previousPublic) && previousVersion > version)
            throw new IOException("Downgrading or replacing an incomplete public installation is not supported.");
        if (!previousPublic && oldMarker == null && oldExe != null && previousVersion >= version && action != "migrate")
        {
            if (action != "interactive" || MessageBoxW(0, $"将开发版 {previousVersion} 迁移到正式发行版 {version}，沿用当前设备数据？\n\nMigrate development build {previousVersion} to release {version} and keep this device's data?",
                    "Nodicarta Edge", 0x124) != 6) throw new IOException("Development migration requires explicit consent (/MIGRATEDEV).");
        }
        var last = DesktopRunRecord.Read();
        var state = oldExe != null && ReleaseLinks.Same(last?.Executable, oldExe) ? last!.StateDirectory :
            links.ExistingState ?? Program.DefaultStateDirectory;
        if (links.ExistingState is { } explicitState && !EdgeStatePaths.Same(state, explicitState) &&
            ReleaseLinks.Same(EdgeStatePaths.Normalize(state), EdgeStatePaths.Normalize(explicitState)))
            throw new IOException("The last Edge desktop and its shortcuts select independent data directories.");
        var launcherPath = Path.Combine(root, ReleasePackage.Cli);
        if (File.Exists(launcherPath) && (old == null || !KnownLauncher(root, ReleasePackage.Hash(launcherPath))))
            throw new IOException("The MCP launcher belongs to another application.");
        var uninstallPath = Path.Combine(root, "Uninstall.exe");
        if (oldMarker == null && File.Exists(uninstallPath)) throw new IOException("An unrelated uninstaller occupies this directory.");
        Directory.CreateDirectory(root);
        var installed = source.InstallInto(root);
        bool alreadyRunning = oldExe != null && IsResident(oldExe);
        var tx = new Transaction(installed.Id, oldExe, state, alreadyRunning, TrackedFiles(root, links).Select(Snapshot).ToArray(), ReadRegistration(scope));
        ReleasePackage.WriteAtomically(Path.Combine(root, Journal), JsonSerializer.SerializeToUtf8Bytes(tx, DesktopJson.Default.Transaction));
        try
        {
            if (alreadyRunning) StopExisting(oldExe!);
            var launcher = Path.Combine(installed.DirectoryPath, "bin", ReleasePackage.Launcher);
            if (!File.Exists(launcherPath) || !installed.MatchesLauncher(ReleasePackage.Hash(launcherPath)))
            {
                try { ReleasePackage.WriteAtomically(launcherPath, File.ReadAllBytes(launcher)); }
                catch (IOException) when (old != null && File.Exists(launcherPath) && KnownLauncher(root, ReleasePackage.Hash(launcherPath))) { }
            }
            ReleasePackage.WriteAtomically(Path.Combine(root, "active-release"), Encoding.ASCII.GetBytes(installed.Id));
            ReleasePackage.WriteAtomically(Path.Combine(root, Receipt), JsonSerializer.SerializeToUtf8Bytes(new System.Text.Json.Nodes.JsonObject
                { ["schema"] = 1, ["product"] = "Nodicarta Edge", ["channel"] = DesktopUpdates.Channel, ["publisher"] = key }, DesktopJson.Default.JsonObject));
            var embeddedUninstaller = Path.Combine(source.DirectoryPath, "Uninstall.exe");
            ReleasePackage.WriteAtomically(uninstallPath, File.ReadAllBytes(embeddedUninstaller));
            links.SetTarget(installed.Executable, state);
            StartAndCheck(installed.Executable, state, action != "interactive");
            Register(root, scope, version.ToString(), installed.Executable);
            File.Delete(Path.Combine(root, Journal));
        }
        catch
        {
            Recover(root, links, scope, restart: true);
            throw;
        }
    }

    private static void VerifyPublisher(byte[] receipt, string expectedKey)
    {
        using var doc = JsonDocument.Parse(receipt);
        var node = doc.RootElement;
        if (node.GetProperty("schema").GetInt32() != 1 || node.GetProperty("product").GetString() != "Nodicarta Edge" ||
            node.GetProperty("channel").GetString() != DesktopUpdates.Channel ||
            !ReleaseLinks.Same(node.GetProperty("publisher").GetString(), expectedKey)) throw new IOException("Different update publisher or channel.");
    }

    private static bool KnownLauncher(string root, string hash) => ReleasePackage.Installed(root).Any(package => package.MatchesLauncher(hash));

    private static Process? Resident(string path)
    {
        using var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ReleasePackage.Desktop)))
        {
            try
            {
                if (p.Id != self.Id && p.SessionId == self.SessionId && ReleaseLinks.Same(p.MainModule?.FileName, path) && !p.HasExited) return p;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
        return null;
    }

    private static bool IsResident(string path) { using var process = Resident(path); return process != null; }
    private static void StopExisting(string path)
    {
        using var owner = Resident(path);
        if (owner == null) return;
        if (EventWaitHandle.TryOpenExisting(DesktopInstance.Name + ".Retire", Session, out var retire))
            using (retire) retire.Set();
        if (!owner.WaitForExit(15000)) throw new IOException("Quit the current Edge desktop from the tray and try again.");
    }

    private static Process Start(string exe, string state, bool background, string? ready)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        if (background) start.ArgumentList.Add("--background");
        start.ArgumentList.Add("--state");
        start.ArgumentList.Add(state);
        if (ready != null) start.Environment[ReadyVariable] = ready;
        return Process.Start(start) ?? throw new IOException("Could not start Nodicarta Edge.");
    }

    private static void StartAndCheck(string exe, string state, bool background)
    {
        var nonce = ReadyPrefix + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, nonce, Session);
        using var child = Start(exe, state, background, nonce);
        var watch = Stopwatch.StartNew();
        while (!child.HasExited && watch.Elapsed < TimeSpan.FromSeconds(30))
            if (ready.WaitOne(100) && !child.WaitForExit(750)) return;
        if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); }
        throw new IOException("The new Edge desktop did not report a healthy start. Restoring the previous release.");
    }

    private static Registration? ReadRegistration(string? scope)
    {
        if (scope != null)
        {
            var path = Path.Combine(scope, "install-location");
            CodexAccessFile.CheckPath(path);
            return File.Exists(path) ? new Registration(File.ReadAllText(path), null, null) : null;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (key == null) return null;
        if (!Equals(key.GetValue("DisplayName"), "Nodicarta Edge") || key.GetValue("InstallLocation") is not string directory ||
            !Path.IsPathFullyQualified(directory)) throw new IOException("The uninstall registration does not belong to Nodicarta Edge.");
        return new Registration(directory, key.GetValue("DisplayVersion") as string, key.GetValue("DisplayIcon") as string);
    }

    private static void RestoreRegistration(string root, string? scope, Registration? previous)
    {
        if (scope != null)
        {
            Restore(Path.Combine(scope, "install-location"), previous?.Root is { } s ? Encoding.UTF8.GetBytes(s) : null);
            return;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (key == null && previous == null) return;
        if (key != null && (key.GetValue("DisplayName") is string name && name != "Nodicarta Edge" ||
            key.GetValue("InstallLocation") is string dir && !ReleaseLinks.Same(dir, root) && !ReleaseLinks.Same(dir, previous?.Root)))
            throw new IOException("The uninstall registration changed during installation.");
        if (previous?.Root is { } oldRoot)
            Register(oldRoot, null, previous.Version ?? "", previous.Icon ?? "");
        else Registry.CurrentUser.DeleteSubKey(RegistryPath, throwOnMissingSubKey: false);
    }

    private static void Register(string root, string? scope, string version, string exe)
    {
        if (scope != null)
        {
            ReleasePackage.WriteAtomically(Path.Combine(scope, "install-location"), Encoding.UTF8.GetBytes(root));
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue("DisplayName", "Nodicarta Edge"); key.SetValue("Publisher", "Nodicarta");
        key.SetValue("InstallLocation", root); key.SetValue("DisplayIcon", exe); key.SetValue("DisplayVersion", version);
        key.SetValue("UninstallString", "\"" + Path.Combine(root, "Uninstall.exe") + "\"");
        key.SetValue("QuietUninstallString", "\"" + Path.Combine(root, "Uninstall.exe") + "\" /S");
        key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
    }

    private static void Uninstall(string root, ReleaseLinks links, string? scope)
    {
        var actual = ReleasePackage.Active(root) ?? throw new IOException("Could not identify this installation.");
        VerifyPublisher(File.ReadAllBytes(Path.Combine(root, Receipt)), DesktopUpdates.PublisherKeyId ?? "");
        var packages = ReleasePackage.Installed(root).ToArray();
        if (packages.All(package => package.Id != actual.Id)) throw new IOException("Could not verify the currently installed release.");
        string launcher = Path.Combine(root, ReleasePackage.Cli);
        if (!KnownLauncher(root, ReleasePackage.Hash(launcher))) throw new IOException("Unrecognized MCP launcher.");
        foreach (var cli in Process.GetProcessesByName("ProjectHub.Edge"))
            using (cli)
            {
                try
                {
                    var path = cli.MainModule?.FileName;
                    if (path != null && (ReleaseLinks.Same(path, launcher) || packages.Any(p => ReleaseLinks.Same(path, Path.Combine(p.DirectoryPath, "bin", ReleasePackage.Cli)))))
                        throw new IOException("Close the MCP sessions using this installation before uninstalling.");
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        StopExisting(actual.Executable);
        if (ReleaseLinks.Same(links.ExistingDesktop, actual.Executable))
            foreach (var link in links.Paths) if (File.Exists(link)) File.Delete(link);
        foreach (var package in packages) package.RemoveInstalledFiles();
        foreach (var name in new[] { ReleasePackage.Cli, "active-release", Receipt, "Uninstall.exe" }) File.Delete(Path.Combine(root, name));
        var cache = Path.Combine(root, "updates");
        CodexAccessFile.CheckPath(cache);
        if (Directory.Exists(cache))
        {
            foreach (var path in Directory.EnumerateFiles(cache))
            {
                var name = Path.GetFileName(path);
                if (name != "last-attempt")
                {
                    if (name.EndsWith(".attempt", StringComparison.Ordinal)) name = name[..^8];
                    if (!name.StartsWith("setup-", StringComparison.Ordinal) || !name.EndsWith(".exe", StringComparison.Ordinal) ||
                        name.Length != 42 || !Guid.TryParseExact(name[6..^4], "N", out _)) continue;
                }
                CodexAccessFile.CheckPath(path);
                File.Delete(path);
            }
            if (!Directory.EnumerateFileSystemEntries(cache).Any()) Directory.Delete(cache);
        }
        if (scope != null) File.Delete(Path.Combine(scope, "install-location"));
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (ReleaseLinks.Same(key?.GetValue("InstallLocation") as string, root)) Registry.CurrentUser.DeleteSubKey(RegistryPath, throwOnMissingSubKey: false);
        }
        if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
