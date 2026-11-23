#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using ProjectHub.Edge.Desktop;

if (args is [var phase, var ns, var scratch, var executable, var customState])
{
    Environment.SetEnvironmentVariable("LOCALAPPDATA", scratch);
    using var instance = DesktopInstance.Enter(executable, phase == "background", ns);
    if (instance == null) return 0;
    var previous = DesktopRunRecord.Read();
    var state = customState == "default" ? previous?.StateDirectory ?? Path.Combine(scratch, "state") : customState;
    new DesktopRunRecord(executable, state).Save();
    File.WriteAllText(Path.Combine(scratch, phase + ".ready"), previous?.Executable ?? "first");
    var first = WaitHandle.WaitAny([instance.Wake, instance.Retire], TimeSpan.FromSeconds(20));
    if (first == 0)
    {
        File.WriteAllText(Path.Combine(scratch, phase + ".woke"), "");
        if (!instance.Retire.WaitOne(TimeSpan.FromSeconds(20))) return 2;
    }
    else if (first != 1) return 2;
    // Simulate finishing a config write before the owner releases its mutex.
    Thread.Sleep(160);
    File.WriteAllText(Path.Combine(scratch, phase + ".finished"), "");
    return 0;
}

var directory = Directory.CreateTempSubdirectory("nodicarta-one-desktop-");
var name = "Nodicarta.Edge.Test." + Guid.NewGuid().ToString("N");
var a = Path.Combine(directory.FullName, "A", "ProjectHub.Edge.Desktop.exe");
var b = Path.Combine(directory.FullName, "B", "ProjectHub.Edge.Desktop.exe");
var originalState = Path.Combine(directory.FullName, "数据目录");
var processes = new List<Process>();
Process Start(string phase, string exe, string state)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, phase, name, directory.FullName, exe, state })
        start.ArgumentList.Add(arg);
    var process = Process.Start(start)!;
    processes.Add(process);
    return process;
}
void WaitFile(string relative)
{
    if (!SpinWait.SpinUntil(() => File.Exists(Path.Combine(directory.FullName, relative)), TimeSpan.FromSeconds(8)))
        throw new Exception(relative);
}
void CheckExit(Process p)
{
    if (!p.WaitForExit(8000) || p.ExitCode != 0) throw new Exception("An isolated process did not complete");
}
try
{
    var owner = Start("owner", a, originalState);
    WaitFile("owner.ready");
    CheckExit(Start("repeat", a, Path.Combine(directory.FullName, "ignored-state")));
    WaitFile("owner.woke");
    CheckExit(Start("background", b, "default"));
    if (File.Exists(Path.Combine(directory.FullName, "background.ready")) || owner.HasExited)
        throw new Exception("A background start took ownership");
    var moved = Start("moved", b, "default");
    WaitFile("moved.ready");
    CheckExit(owner);
    WaitFile("owner.finished");
    if (File.ReadAllText(Path.Combine(directory.FullName, "moved.ready")) != a) throw new Exception("Lost former desktop path");
    Environment.SetEnvironmentVariable("LOCALAPPDATA", directory.FullName);
    if (DesktopRunRecord.Read() != new DesktopRunRecord(b, originalState)) throw new Exception("Lost custom user state");
    string defaultState() => ProjectHub.Edge.Desktop.Program.DefaultStateDirectory;
    Environment.SetEnvironmentVariable("NODICARTA_EDGE_STATE", null);
    Environment.SetEnvironmentVariable("PROJECTHUB_EDGE_STATE", null);
    if (defaultState() != originalState) throw new Exception("Persistent defaults displaced the remembered state");
    var inheritedState = Environment.GetEnvironmentVariable("PROJECTHUB_EDGE_STATE", EnvironmentVariableTarget.User);
    if (!string.IsNullOrEmpty(inheritedState))
    {
        var inheritedAlias = string.Equals(ProjectHub.Edge.EdgeStatePaths.Normalize(inheritedState),
            ProjectHub.Edge.EdgeStatePaths.LocalDefault, StringComparison.OrdinalIgnoreCase)
            ? ProjectHub.Edge.EdgeStatePaths.LegacyLocalDefault : inheritedState;
        Environment.SetEnvironmentVariable("PROJECTHUB_EDGE_STATE", inheritedAlias + Path.DirectorySeparatorChar);
        if (defaultState() != originalState) throw new Exception("An inherited state alias displaced the remembered state");
    }
    var processState = Path.Combine(directory.FullName, "explicit process override");
    Environment.SetEnvironmentVariable("NODICARTA_EDGE_STATE", processState);
    if (defaultState() != processState) throw new Exception("Explicit process state was ignored");
    using (var retire = EventWaitHandle.OpenExisting(name + ".Retire",
        new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true })) retire.Set();
    CheckExit(moved);
    Directory.CreateDirectory(Path.GetDirectoryName(a)!);
    Directory.CreateDirectory(Path.GetDirectoryName(b)!);
    File.WriteAllBytes(a, []);
    File.WriteAllBytes(b, []);
    var shortcutPath = Path.Combine(directory.FullName, "测试.lnk");
    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
    try
    {
        var customArgs = "--background --state \"" + originalState + "\"";
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = a;
        shortcut.Arguments = customArgs;
        shortcut.WorkingDirectory = Path.GetDirectoryName(a);
        shortcut.IconLocation = a + ",0";
        shortcut.Save();
        Marshal.FinalReleaseComObject((object)shortcut);
        DesktopLocation.Retarget(shortcutPath, a, b);
        shortcut = shell.CreateShortcut(shortcutPath);
        if ((string)shortcut.TargetPath != b || (string)shortcut.WorkingDirectory != Path.GetDirectoryName(b) ||
            (string)shortcut.Arguments != customArgs || (string)shortcut.IconLocation != b + ",0") throw new Exception("Broken moved shortcut");
        shortcut.WorkingDirectory = originalState;
        shortcut.IconLocation = Path.Combine(directory.FullName, "custom.ico") + ",0";
        shortcut.Save();
        Marshal.FinalReleaseComObject((object)shortcut);
        var bytes = File.ReadAllBytes(shortcutPath);
        DesktopLocation.Retarget(shortcutPath, a, b);
        if (!bytes.SequenceEqual(File.ReadAllBytes(shortcutPath))) throw new Exception("Unrelated shortcut was overwritten");
        DesktopLocation.Retarget(shortcutPath, b, a);
        shortcut = shell.CreateShortcut(shortcutPath);
        if ((string)shortcut.WorkingDirectory != originalState || !((string)shortcut.IconLocation).EndsWith("custom.ico,0"))
            throw new Exception("User shortcut settings were lost");
        Marshal.FinalReleaseComObject((object)shortcut);
    }
    finally { Marshal.FinalReleaseComObject((object)shell); }
    var installLinks = new ReleaseLinks(directory.FullName);
    ReleaseLinks.Write(installLinks.Paths[1], a, "--background " + ReleaseLinks.StateArguments(originalState));
    var startupOnly = new ReleaseLinks(directory.FullName);
    if (startupOnly.ExistingState != originalState || !startupOnly.StartupEnabled || startupOnly.ExistingDesktop != a)
        throw new Exception("The only existing login shortcut lost its data directory.");
    ReleaseLinks.Write(installLinks.Paths[0], a, "");
    if (new ReleaseLinks(directory.FullName).ExistingState != originalState)
        throw new Exception("A menu link without --state overrode the login link's custom data directory.");
    ReleaseLinks.Write(installLinks.Paths[0], a, ReleaseLinks.StateArguments(Path.Combine(directory.FullName, "other state")));
    try { _ = new ReleaseLinks(directory.FullName); throw new Exception("Conflicting shortcut data was silently selected."); }
    catch (IOException) { }
    Console.WriteLine("PASS: machine-wide lease, repeated launch, stale login start, graceful move, custom state, persisted location, managed shortcuts.");
    return 0;
}
finally
{
    foreach (var p in processes)
    {
        if (!p.HasExited) { p.Kill(); p.WaitForExit(); }
        p.Dispose();
    }
    directory.Delete(true);
}
