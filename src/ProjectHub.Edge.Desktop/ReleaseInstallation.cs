using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ProjectHub.Edge.Desktop;

// Runs from the installer's private extraction directory, outside all release directories.
internal static class ReleaseInstallation
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nodicarta.Edge";
    private const string Backup = ".nodicarta-install-backup";
    private const string StagedFile = ".nodicarta-install-write.tmp";
    private const string StagedJournal = ".nodicarta-install-journal.tmp";
    private const string ReadyPrefix = "Nodicarta.Edge.Install.Ready.";
    private const string ReadyVariable = "NODICARTA_INSTALL_READY_EVENT";
    internal const int Deferred = 75;
    private const int Cancelled = 1602;
    private static readonly NamedWaitHandleOptions Session = new() { CurrentUserOnly = true, CurrentSessionOnly = true };
    internal sealed record Registration(string? Root, string? Version, string? Icon);
    internal sealed record RuntimeChange(string Name, string? Before, string? After);
    internal sealed record Transaction(string State, bool WasRunning, bool Developer, RuntimeChange[] Files, byte[]?[] Links,
        Registration? Registered, bool Committed = false);
    private sealed class LeaseBusyException() : IOException("Edge MCP or a local command is still running. Close it before installing.");
    private sealed class InstallCancelledException : Exception;
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
        if (args.FirstOrDefault() != "--install-release") return false;
        try
        {
            if (args is not [_, var input, var action, var scopeName, .. var options] || !Path.IsPathFullyQualified(input) ||
                action is not ("interactive" or "quiet" or "update" or "migrate" or "uninstall" or "developer" or "developer-uninstall") ||
                options.Length > 1 || options.Length == 1 && (action != "developer" || !Path.IsPathFullyQualified(options[0])))
                throw new ArgumentException("Invalid installation arguments.");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
            if (ReleaseLinks.Same(root, Path.GetPathRoot(root))) throw new IOException("Choose a dedicated Edge installation directory.");
            CodexAccessFile.CheckPath(root);
            var scopeDirectory = Scope(scopeName);
            using var installLock = InstallMutex();
            if (!TryLock(installLock))
            {
                if (action == "update") { exitCode = Deferred; return true; }
                throw new IOException("Another Edge installation is in progress.");
            }
            try
            {
                var journal = Path.Combine(root, ReleasePackage.Journal);
                CodexAccessFile.CheckPath(journal);
                if (File.Exists(journal))
                {
                    using var lease = OpenLease(root, create: true);
                    Recover(root, new ReleaseLinks(scopeDirectory, pathsOnly: true), scopeDirectory);
                }
                var links = new ReleaseLinks(scopeDirectory);
                if (action is "uninstall" or "developer-uninstall") Uninstall(root, links, scopeDirectory, action == "developer-uninstall");
                else Install(root, links, scopeDirectory, action, options.FirstOrDefault());
            }
            finally { installLock.ReleaseMutex(); }
        }
        catch (InstallCancelledException) { exitCode = Cancelled; }
        catch (LeaseBusyException) when (args.ElementAtOrDefault(2) == "update") { exitCode = Deferred; }
        catch (Exception error)
        {
            Trace.TraceError("Nodicarta Edge setup: {0}", error.Message);
            var report = Path.Combine(Path.GetTempPath(), "Nodicarta-Edge-Setup.log");
            try { CodexAccessFile.CheckPath(report); File.AppendAllText(report, $"{DateTimeOffset.Now:o} {error.Message}{Environment.NewLine}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (args.ElementAtOrDefault(2) == "interactive") MessageBoxW(0, error.Message, "Nodicarta Edge", 0x10);
            exitCode = 1;
        }
        return true;
    }

    private static Mutex InstallMutex() => new(false, @"Local\Nodicarta.Edge.Desktop.Install." + WindowsIdentity.GetCurrent().User!.Value);
    private static bool TryLock(Mutex mutex)
    {
        try { return mutex.WaitOne(0); } catch (AbandonedMutexException) { return true; }
    }

    internal static bool IsLaunchIdle(string root)
    {
        try
        {
            using var lease = OpenLease(root, create: false);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static FileStream OpenLease(string root, bool create)
    {
        var path = Path.Combine(root, ReleasePackage.Lease);
        CodexAccessFile.CheckPath(path);
        try { return File.Open(path, create ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { throw new LeaseBusyException(); }
    }

    private static FileStream OpenInstallLease(string root, string launcher, ReleasePackage? old, string action)
    {
        try { return OpenLease(root, create: true); }
        catch (LeaseBusyException) when (action == "interactive" && old != null && ReleasePackage.IsInstalledLauncher(root, old))
        {
            if (!StopInstalledCli(launcher, mcpOnly: true, inspectOnly: true))
                return OpenLease(root, create: false);
            var title = UiLanguage.Text("断开 MCP 并更新", "Disconnect MCP and update");
            var message = UiLanguage.Text(
                "是否断开此安装的 Edge MCP 会话并继续更新？正在执行的 MCP 请求可能会中断。",
                "Disconnect this installation's Edge MCP sessions and continue updating? Running MCP requests may be interrupted.");
            if (MessageBoxW(0, message, title, 0x10134) != 6) throw new InstallCancelledException();

            if (ReleasePackage.Active(root)?.Id != old.Id || !ReleasePackage.IsInstalledLauncher(root, old))
                throw new IOException("The Edge installation changed during setup.");

            // The installed launcher already checks this barrier before loading the CLI.
            var journal = Path.Combine(root, ReleasePackage.Journal);
            CodexAccessFile.CheckPath(journal);
            using (File.Open(journal, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
            try
            {
                StopInstalledCli(launcher, mcpOnly: true);
                var wait = Stopwatch.StartNew();
                while (true)
                {
                    try { return OpenLease(root, create: false); }
                    catch (LeaseBusyException) when (wait.ElapsedMilliseconds < 5000) { Thread.Sleep(50); }
                }
            }
            finally { File.Delete(journal); }
        }
    }

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

    private static bool Digest(string? value) => value == null || value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static void Recover(string root, ReleaseLinks links, string? scope)
    {
        var journal = Path.Combine(root, ReleasePackage.Journal);
        CodexAccessFile.CheckPath(journal);
        var length = new FileInfo(journal).Length;
        // Uninstall uses the same launch barrier before it stops running clients.
        if (length == 0) { File.Delete(journal); return; }
        if (length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid pending Edge installation.");
        var tx = JsonSerializer.Deserialize(File.ReadAllBytes(journal), DesktopJson.Default.Transaction) ??
            throw new InvalidDataException("Invalid pending Edge installation.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Path.IsPathFullyQualified(tx.State) || tx.Links.Length != links.Paths.Length ||
            tx.Registered != null && !ReleaseLinks.Same(tx.Registered.Root, root) ||
            tx.Files.Any(file => !(ReleasePackage.IsRuntimePath(file.Name) || file.Name == ReleasePackage.Manifest ||
                !tx.Developer && file.Name is (ReleasePackage.Receipt or ReleasePackage.Uninstaller)) ||
                !names.Add(file.Name) || !Digest(file.Before) || !Digest(file.After) || file.Before == file.After))
            throw new InvalidDataException("Invalid pending Edge paths.");

        var backup = Path.Combine(root, Backup);
        var stage = Path.Combine(backup, StagedFile);
        var desktop = Path.Combine(root, ReleasePackage.Desktop);
        if (!tx.Committed)
        {
            StopExisting(desktop);
            var restore = new List<RuntimeChange>();
            foreach (var change in tx.Files)
            {
                var path = Path.Combine(root, change.Name);
                var current = CurrentHash(path);
                if (string.Equals(current, change.Before, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(current, change.After, StringComparison.OrdinalIgnoreCase) ||
                    change.Before != null && !ReleasePackage.Matches(Path.Combine(backup, change.Name), change.Before))
                    throw new IOException("Cannot verify the Edge rollback file: " + path);
                restore.Add(change);
            }
            foreach (var change in restore)
            {
                var path = Path.Combine(root, change.Name);
                if (change.Before == null) File.Delete(path);
                else ReleasePackage.CopyAtomically(Path.Combine(backup, change.Name), path, stage);
            }
            ReleasePackage.PruneDirectories(root, restore.Select(change => change.Name));
            for (int i = 0; i < links.Paths.Length; i++) Restore(links.Paths[i], tx.Links[i]);
            if (!tx.Developer) RestoreRegistration(root, scope, tx.Registered);
        }
        FinishTransaction(journal, backup, tx.Files);
        if (!tx.Committed && tx.WasRunning && !IsResident(desktop)) using (Start(desktop, tx.State, true, null)) { }
    }

    private static string? CurrentHash(string path)
    {
        CodexAccessFile.CheckPath(path);
        if (Directory.Exists(path)) throw new IOException("A directory occupies this Edge file: " + path);
        return File.Exists(path) ? ReleasePackage.Hash(path) : null;
    }

    private static void FinishTransaction(string journal, string backup, RuntimeChange[] files)
    {
        CodexAccessFile.CheckPath(backup);
        if (Directory.Exists(backup))
        {
            var stage = Path.Combine(backup, StagedFile);
            CodexAccessFile.CheckPath(stage);
            File.Delete(stage);
            foreach (var file in files)
            {
                var saved = Path.Combine(backup, file.Name);
                CodexAccessFile.CheckPath(saved);
                if (File.Exists(saved)) File.Delete(saved);
            }
            ReleasePackage.PruneDirectories(backup, files.Select(file => file.Name));
            Directory.Delete(backup);
        }
        File.Delete(journal);
    }

    private static void Install(string root, ReleaseLinks links, string? scope, string action, string? explicitState)
    {
        bool developer = action == "developer";
        var key = DesktopUpdates.PublisherKeyId;
        if (!developer && key == null) throw new InvalidOperationException("Missing distribution trust key.");
        var extraction = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName;
        var source = new ReleasePackage(extraction, bundle: true);
        source.VerifyBundle();
        var legacyPointer = Path.Combine(root, "active-release");
        CodexAccessFile.CheckPath(legacyPointer);
        if (File.Exists(legacyPointer))
            throw new IOException("Uninstall the previous Edge installation first. Device data is kept.");
        var old = ReleasePackage.Active(root);
        var registration = ReadRegistration(scope);
        var launcherPath = Path.Combine(root, ReleasePackage.Cli);
        var desktop = Path.Combine(root, ReleasePackage.Desktop);
        var last = DesktopRunRecord.Read();
        var anchor = links.ExistingDesktop;
        if (registration?.Root != null && !ReleaseLinks.Same(registration.Root, root))
            throw new IOException("The registered Edge installation is in another directory.");
        if (developer && registration != null) throw new IOException("A registered Edge install must be managed by its installer.");
        if (old != null && anchor != null && !ReleaseLinks.Same(anchor, launcherPath) && !ReleaseLinks.Same(anchor, desktop))
            throw new IOException("The Edge shortcut points to another installation.");
        var oldExe = old?.Executable;
        if (old == null && anchor != null && !File.Exists(anchor))
        {
            CodexAccessFile.CheckPath(anchor);
            var existing = Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root)
                .Any(path => !ReleaseLinks.Same(path, Path.Combine(root, ReleasePackage.Lease)));
            if (!ReleaseLinks.UnderInstallRoot(root, anchor) || !developer && !ReleaseLinks.Same(registration?.Root, root) ||
                existing || last != null && !ReleaseLinks.UnderInstallRoot(root, last.Executable) && IsResident(last.Executable))
                throw new IOException("Cannot establish the installation for the missing Edge shortcut.");
        }
        else if (old == null && anchor != null)
            throw new IOException("The previous Edge shortcut points to a different or unregistered installation.");

        var oldMarker = Snapshot(Path.Combine(root, ReleasePackage.Receipt));
        if (developer && oldMarker != null) throw new IOException("A public Edge install must be managed by its installer.");
        if (oldMarker != null) VerifyPublisher(oldMarker, key!);
        bool previousPublic = oldMarker != null;
        var repair = old?.Id == source.Id && (developer || previousPublic && ReleaseLinks.Same(registration?.Root, root)) && action != "update";
        if (oldExe != null && !(repair && !File.Exists(oldExe)) && !ReleaseLinks.IsEdgeDesktop(oldExe))
            throw new InvalidDataException("Cannot verify the previous desktop.");
        old?.Verify(allowMissing: repair);
        if (previousPublic)
        {
            if (old == null) throw new IOException("The Edge installation is incomplete. Reinstall the same version.");
            var previousMeta = Path.Combine(root, "nodicarta-release.json");
            if (repair && !File.Exists(previousMeta)) previousMeta = Path.Combine(source.DirectoryPath, "nodicarta-release.json");
            CodexAccessFile.CheckPath(previousMeta);
            using var previous = JsonDocument.Parse(File.ReadAllBytes(previousMeta));
            if (previous.RootElement.GetProperty("releaseLine").GetString() != "public-v1" ||
                !ReleaseLinks.Same(previous.RootElement.GetProperty("publisherKeyId").GetString(), key) ||
                previous.RootElement.GetProperty("channel").GetString() != DesktopUpdates.Channel)
                throw new IOException("The old release belongs to a different publisher or channel.");
        }
        var version = Version.Parse(Program.ReleaseVersion);
        var previousVersion = repair ? version : oldExe == null ? null :
            Version.Parse(FileVersionInfo.GetVersionInfo(oldExe).ProductVersion!.Split('+')[0]);
        if (action == "update" && (old == null || !previousPublic || previousVersion >= version))
            throw new InvalidOperationException("Automatic update requires a newer release of this installed product.");
        if (previousPublic && previousVersion > version)
            throw new IOException("Downgrading a public installation is not supported.");
        if (!developer && !previousPublic && oldExe != null && previousVersion >= version && action != "migrate")
        {
            if (action != "interactive" || MessageBoxW(0, $"将开发版 {previousVersion} 迁移到正式发行版 {version}，沿用当前设备数据？\n\nMigrate development build {previousVersion} to release {version} and keep this device's data?",
                    "Nodicarta Edge", 0x124) != 6) throw new IOException("Development migration requires explicit consent (/MIGRATEDEV).");
        }
        var recordedState = last != null && (ReleaseLinks.Same(last.Executable, oldExe) ||
            oldExe == null && anchor != null && ReleaseLinks.UnderInstallRoot(root, last.Executable)) ? last.StateDirectory : null;
        var state = explicitState ?? (oldExe == null ? links.ExistingState ?? recordedState : recordedState ?? links.ExistingState)
            ?? Program.DefaultStateDirectory;
        if (links.ExistingState is { } linkedState && recordedState != null && !EdgeStatePaths.Same(recordedState, linkedState) &&
            ReleaseLinks.Same(EdgeStatePaths.Normalize(recordedState), EdgeStatePaths.Normalize(linkedState)))
            throw new IOException("The last Edge desktop and its shortcuts select independent data directories.");

        var incoming = source.Files.ToDictionary(file => file.Key, file => Path.Combine(source.DirectoryPath, file.Key), StringComparer.OrdinalIgnoreCase);
        incoming.Add(ReleasePackage.Manifest, Path.Combine(extraction, ReleasePackage.Manifest));
        byte[]? receipt = null;
        if (!developer)
        {
            incoming.Add(ReleasePackage.Uninstaller, Path.Combine(extraction, ReleasePackage.Uninstaller));
            receipt = JsonSerializer.SerializeToUtf8Bytes(new System.Text.Json.Nodes.JsonObject
                { ["schema"] = 1, ["product"] = "Nodicarta Edge", ["channel"] = DesktopUpdates.Channel, ["publisher"] = key }, DesktopJson.Default.JsonObject);
        }
        var owned = new HashSet<string>(old?.Files.Keys ?? [], StringComparer.OrdinalIgnoreCase);
        if (old != null) owned.Add(ReleasePackage.Manifest);
        if (previousPublic) { owned.Add(ReleasePackage.Receipt); owned.Add(ReleasePackage.Uninstaller); }
        var after = source.Files.ToDictionary(file => file.Key, file => file.Value, StringComparer.OrdinalIgnoreCase);
        after.Add(ReleasePackage.Manifest, source.Id);
        if (receipt != null)
        {
            after.Add(ReleasePackage.Uninstaller, ReleasePackage.Hash(incoming[ReleasePackage.Uninstaller]));
            after.Add(ReleasePackage.Receipt, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(receipt)));
        }
        var journal = Path.Combine(root, ReleasePackage.Journal);
        var backup = Path.Combine(root, Backup);
        var stage = Path.Combine(backup, StagedFile);
        CodexAccessFile.CheckPath(backup);
        if (Directory.Exists(backup) || File.Exists(backup)) throw new IOException("An unexpected Edge installation backup remains.");
        Directory.CreateDirectory(root);
        using var lease = OpenInstallLease(root, launcherPath, old, action);
        if (ReleasePackage.Active(root)?.Id != old?.Id) throw new IOException("The Edge installation changed during setup.");
        var files = new List<RuntimeChange>();
        foreach (var name in owned.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(root, name);
            var current = CurrentHash(path);
            if (current != null && !owned.Contains(name)) throw new IOException("An unrelated file occupies this Edge path: " + path);
            after.TryGetValue(name, out var digest);
            if (!string.Equals(current, digest, StringComparison.OrdinalIgnoreCase)) files.Add(new(name, current, digest));
        }
        bool alreadyRunning = oldExe != null && IsResident(oldExe);
        if (developer && files.Count == 0 && alreadyRunning && ReleaseLinks.Same(anchor, launcherPath) &&
            links.ExistingState is { } linked && EdgeStatePaths.Same(state, linked)) return;

        var tx = new Transaction(state, alreadyRunning, developer, files.ToArray(), links.Paths.Select(Snapshot).ToArray(), registration);
        ReleasePackage.WriteAtomically(journal, JsonSerializer.SerializeToUtf8Bytes(tx, DesktopJson.Default.Transaction),
            Path.Combine(root, StagedJournal));
        try
        {
            foreach (var file in tx.Files)
                if (file.Before != null) ReleasePackage.CopyAtomically(Path.Combine(root, file.Name), Path.Combine(backup, file.Name), stage);
            if (alreadyRunning) StopExisting(oldExe!);
            foreach (var file in tx.Files)
            {
                var path = Path.Combine(root, file.Name);
                if (file.After == null) File.Delete(path);
                else if (file.Name == ReleasePackage.Receipt) ReleasePackage.WriteAtomically(path, receipt!, stage);
                else ReleasePackage.CopyAtomically(incoming[file.Name], path, stage);
            }
            new ReleasePackage(root).Verify();
            links.SetTarget(launcherPath, state);
            StartAndCheck(desktop, state, action != "interactive");
            if (!developer) Register(root, scope, version.ToString(), launcherPath);
            ReleasePackage.WriteAtomically(journal, JsonSerializer.SerializeToUtf8Bytes(tx with { Committed = true }, DesktopJson.Default.Transaction), stage);
        }
        catch
        {
            Recover(root, links, scope);
            throw;
        }
        FinishTransaction(journal, backup, tx.Files);
        ReleasePackage.PruneDirectories(root, tx.Files.Where(file => file.After == null).Select(file => file.Name));
    }

    private static void VerifyPublisher(byte[] receipt, string expectedKey)
    {
        using var doc = JsonDocument.Parse(receipt);
        var node = doc.RootElement;
        if (node.GetProperty("schema").GetInt32() != 1 || node.GetProperty("product").GetString() != "Nodicarta Edge" ||
            node.GetProperty("channel").GetString() != DesktopUpdates.Channel ||
            !ReleaseLinks.Same(node.GetProperty("publisher").GetString(), expectedKey)) throw new IOException("Different update publisher or channel.");
    }

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
        var deadline = Stopwatch.StartNew();
        do
        {
            if (EventWaitHandle.TryOpenExisting(DesktopInstance.Name + ".Retire", Session, out var retire))
                using (retire) retire.Set();
            if (owner.WaitForExit(100)) return;
        } while (deadline.ElapsedMilliseconds < 15000);
        throw new IOException("Quit the current Edge desktop from the tray and try again.");
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

    private static void Uninstall(string root, ReleaseLinks links, string? scope, bool developer)
    {
        var actual = ReleasePackage.Active(root) ?? throw new IOException("Could not identify this installation.");
        var receipt = Path.Combine(root, ReleasePackage.Receipt);
        var uninstaller = Path.Combine(root, ReleasePackage.Uninstaller);
        var registration = ReadRegistration(scope);
        if (developer && (File.Exists(receipt) || registration != null))
            throw new IOException("A public Edge install must be managed by its installer.");
        if (!developer)
        {
            if (!ReleaseLinks.Same(registration?.Root, root)) throw new IOException("The registered Edge installation is in another directory.");
            VerifyPublisher(Snapshot(receipt) ?? throw new IOException("Could not identify this installation."), DesktopUpdates.PublisherKeyId ?? "");
        }
        string launcher = Path.Combine(root, ReleasePackage.Cli);
        if (!ReleasePackage.IsInstalledLauncher(root, actual)) throw new IOException("Unrecognized MCP launcher.");
        var leasePath = Path.Combine(root, ReleasePackage.Lease);
        var journal = Path.Combine(root, ReleasePackage.Journal);
        CodexAccessFile.CheckPath(leasePath);
        CodexAccessFile.CheckPath(uninstaller);
        if (!File.Exists(leasePath)) throw new IOException("The installed Edge launch lock is missing.");
        ReleasePackage.WriteAtomically(journal, [], Path.Combine(root, StagedJournal));
        try
        {
            StopInstalledCli(launcher);
            using (OpenLease(root, create: false))
            {
                StopExisting(actual.Executable);
                if (ReleaseLinks.Same(links.ExistingDesktop, launcher) || ReleaseLinks.Same(links.ExistingDesktop, actual.Executable))
                    foreach (var link in links.Paths) if (File.Exists(link)) File.Delete(link);
                actual.RemoveInstalledFiles();
                if (!developer)
                {
                    File.Delete(receipt);
                    File.Delete(uninstaller);
                    RestoreRegistration(root, scope, null);
                    RemoveDownloads(root);
                }
            }
            File.Delete(leasePath);
        }
        finally { File.Delete(journal); }
        if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }

    private static void RemoveDownloads(string root)
    {
        var cache = DesktopUpdates.CacheFor(root);
        CodexAccessFile.CheckPath(cache);
        if (!Directory.Exists(cache)) return;
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

    private static bool StopInstalledCli(string launcher, bool mcpOnly = false, bool inspectOnly = false)
    {
        const uint query = 0x1000, terminate = 0x0001, synchronize = 0x00100000;
        using var user = WindowsIdentity.GetCurrent();
        using var self = Process.GetCurrentProcess();
        var image = new StringBuilder(32768);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var clients = new List<(int Id, SafeProcessHandle Handle)>();
            try
            {
                foreach (var cli in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ReleasePackage.Cli)))
                {
                    using (cli)
                    {
                        var process = OpenProcess(query | terminate | synchronize, false, cli.Id);
                        bool keep = false;
                        try
                        {
                            if (process.IsInvalid)
                            {
                                using var inspect = OpenProcess(query, false, cli.Id);
                                if (IsInstalledCli(inspect)) throw CannotStop(cli.Id);
                                continue;
                            }
                            if (!IsInstalledCli(process) || WaitForSingleObject(process, 0) == 0) continue;
                            if (mcpOnly && (cli.SessionId != self.SessionId || !ReleaseLinks.IsMcpCommand(process)))
                            {
                                if (WaitForSingleObject(process, 0) == 0) continue;
                                throw new IOException(UiLanguage.Text(
                                    "仍有 Edge 本地命令或无法确认的会话正在运行。请等它结束后重试更新。",
                                    "An Edge local command or an unverified session is still running. Let it finish and retry the update."));
                            }
                            clients.Add((cli.Id, process));
                            keep = true;
                        }
                        finally { if (!keep) process.Dispose(); }
                    }
                }
                if (inspectOnly || clients.Count == 0) return clients.Count != 0;
                foreach (var (id, process) in clients)
                {
                    // The verified process handle also performs termination, even if its PID is recycled.
                    if (!TerminateProcess(process, 1) && WaitForSingleObject(process, 0) != 0) throw CannotStop(id);
                    if (WaitForSingleObject(process, (uint)Math.Max(0, 5000 - watch.ElapsedMilliseconds)) != 0)
                        throw CannotStop(id);
                }
            }
            finally { foreach (var (_, process) in clients) process.Dispose(); }
            if (watch.ElapsedMilliseconds >= 5000)
                throw new IOException("Edge MCP sessions kept restarting. Pause the client's MCP reconnect and try again.");
        }

        bool IsInstalledCli(SafeProcessHandle process)
        {
            int length = image.Capacity;
            if (process.IsInvalid || !QueryFullProcessImageNameW(process, 0, image, ref length) || !ReleaseLinks.Same(launcher, image.ToString())) return false;
            if (!OpenProcessToken(process, (uint)TokenAccessLevels.Query, out var token))
            {
                int error = Marshal.GetLastPInvokeError();
                token.Dispose();
                if (error == 5) throw new IOException("Could not verify the owner of an Edge process.");
                return false;
            }
            using (token)
            using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
                return Equals(owner.User, user.User);
        }

        static IOException CannotStop(int pid) => new($"Could not stop Edge process {pid}. Close that session and try again.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
