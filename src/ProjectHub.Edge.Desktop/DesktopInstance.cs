using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjectHub.Edge.Desktop;

internal sealed class DesktopInstance : IDisposable
{
    internal const string Name = "Nodicarta.Edge.Desktop.Resident.v1";
    private static readonly NamedWaitHandleOptions Session = new() { CurrentUserOnly = true, CurrentSessionOnly = true };
    private readonly Mutex gate;
    internal EventWaitHandle Wake { get; }
    internal EventWaitHandle Retire { get; }

    private DesktopInstance(Mutex gate, string name, string wakeName)
    {
        this.gate = gate;
        Wake = new(false, EventResetMode.AutoReset, wakeName, Session);
        try { Retire = new(false, EventResetMode.AutoReset, name + ".Retire", Session); }
        catch { Wake.Dispose(); throw; }
    }

    private static string WakeName(string name, string executable) =>
        name + ".Wake." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())));

    internal static DesktopInstance? Enter(string executable, bool background, string name = Name)
    {
        var wakeName = WakeName(name, executable);
        Mutex gate;
        try { gate = new(false, @"Global\" + name); }
        catch (UnauthorizedAccessException)
        {
            if (background) return null;
            throw AnotherSession();
        }
        var claimed = false;
        try
        {
            var started = Stopwatch.StartNew();
            while (!(claimed = Acquire(gate, 0)))
            {
                if (background || Signal(wakeName)) return null;
                // Only the same user in this login session can ask the old desktop to exit.
                Signal(name + ".Retire");
                if (claimed = Acquire(gate, 200)) break;
                if (started.Elapsed >= TimeSpan.FromSeconds(12)) throw AnotherSession();
            }
            return new DesktopInstance(gate, name, wakeName);
        }
        catch
        {
            if (claimed) gate.ReleaseMutex();
            claimed = false;
            throw;
        }
        finally { if (!claimed) gate.Dispose(); }
    }

    private static bool Acquire(Mutex gate, int timeout)
    {
        try { return gate.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    private static bool Signal(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, Session, out var handle)) return false;
        using (handle) return handle.Set();
    }

    // Installation can run while the old GUI is still working on agent settings. The new GUI
    // retries registration maintenance after the old owner has exited.
    internal static void RunMaintenance(Action action)
    {
        Mutex gate;
        try { gate = new(false, @"Global\" + Name); }
        catch (UnauthorizedAccessException) { return; }
        using (gate)
        {
            if (!Acquire(gate, 0)) return;
            try { action(); }
            finally { gate.ReleaseMutex(); }
        }
    }

    internal static InvalidOperationException AnotherSession() => new(UiLanguage.Text(
        "这台电脑上已有脉络 Edge 正在运行。请先在原来的 Windows 会话中从托盘退出。",
        "Nodicarta Edge is already running on this PC. Quit it from the tray in the original Windows session."));

    internal static EventWaitHandle OpenLegacyWake(string directory, out bool first)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()));
        return new(false, EventResetMode.AutoReset, "ProjectHub.Edge.Desktop." + Convert.ToHexString(key), Session, out first);
    }

    public void Dispose()
    {
        Retire.Dispose();
        Wake.Dispose();
        gate.ReleaseMutex();
        gate.Dispose();
    }
}

internal sealed record DesktopRunRecord(string Executable, string StateDirectory)
{
    internal static string UserDirectory
    {
        get
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local))
                local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "Nodicarta");
        }
    }
    private static string RecordPath => Path.Combine(UserDirectory, "desktop-location.json");

    internal static DesktopRunRecord? Read()
    {
        try
        {
            var path = RecordPath;
            CodexAccessFile.CheckPath(path);
            if (new FileInfo(path) is not { Exists: true, Length: > 0 and < 524288 }) return null;
            var record = JsonSerializer.Deserialize<DesktopRunRecord>(File.ReadAllText(path));
            return record != null && Path.IsPathFullyQualified(record.Executable) && Path.IsPathFullyQualified(record.StateDirectory)
                ? record : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    internal void Save()
    {
        var path = RecordPath;
        CodexAccessFile.CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }
}
