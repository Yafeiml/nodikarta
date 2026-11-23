#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using ProjectHub.Edge.Desktop;

var root = Directory.CreateTempSubdirectory("nodicarta-startup-check-");
try
{
    var exe = Path.Combine(root.FullName, "ProjectHub.Edge.Desktop.exe");
    var state = Directory.CreateDirectory(Path.Combine(root.FullName, "custom state 目录")).FullName;
    var folder = Path.Combine(root.FullName, "Startup");
    var current = Path.Combine(folder, "Nodicarta Edge.lnk");
    var legacy = Path.Combine(folder, "ProjectHub Edge.lnk");
    File.WriteAllBytes(exe, []);
    var approved = true;
    var startup = new DesktopStartup(exe, state, folder, _ => approved);
    if (startup.Read() != (false, false)) throw new Exception("Missing startup shortcut must read as disabled.");
    startup.Set(true);
    var correctLink = DesktopLocation.WithShortcut<bool>(current, link => (string)link.TargetPath == exe &&
        (string)link.Arguments == $"--background --state \"{state}\"" && (string)link.Description == "Nodicarta Edge");
    if (!correctLink || startup.Read() != (true, true)) throw new Exception("The registered shortcut does not start the correct device silently.");
    var original = File.ReadAllBytes(current);
    approved = false;
    startup.Set(true);
    if (startup.Read() != (true, false) || !original.SequenceEqual(File.ReadAllBytes(current)))
        throw new Exception("Windows-disabled startup was reported as active or the existing link was overwritten.");
    File.Move(current, legacy);
    approved = true;
    if (startup.Read() != (true, true)) throw new Exception("Legacy startup shortcut was missed.");
    startup.Set(false);
    startup.Set(false);
    if (File.Exists(legacy) || startup.Read() != (false, false)) throw new Exception("Disabling must remove both old and current registration.");
    File.WriteAllBytes(current, original);
    DesktopLocation.WithShortcut<bool>(current, link =>
    {
        link.Arguments = "--connect-codex";
        link.Save();
        return true;
    });
    var foreign = File.ReadAllBytes(current);
    foreach (var enable in new[] { true, false })
    {
        try { startup.Set(enable); throw new Exception("Modified shortcut was accepted."); }
        catch (InvalidOperationException) { }
        if (!File.ReadAllBytes(current).SequenceEqual(foreign)) throw new Exception("Modified shortcut was overwritten.");
    }
    Console.WriteLine("PASS: current user startup shortcut, custom state, Windows disable, legacy name, owned links only.");
}
finally { root.Delete(true); }
