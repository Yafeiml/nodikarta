#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Text;
using ProjectHub.Edge.Desktop;

var work = Directory.CreateTempSubdirectory("nodicarta-release-history-");
try
{
    string Add(string name, params string[] files)
    {
        var folder = Path.Combine(work.FullName, name);
        var bin = Directory.CreateDirectory(Path.Combine(folder, "bin"));
        var manifest = Path.Combine(folder, "SHA256SUMS");
        var entries = new List<string>();
        foreach (var file in files)
        {
            var path = Path.Combine(bin.FullName, file);
            File.WriteAllText(path, name + " " + file);
            entries.Add(ReleasePackage.Hash(path) + "  " + file);
        }
        File.WriteAllLines(manifest, entries, new UTF8Encoding(false));
        var id = ReleasePackage.Hash(manifest)[..12].ToLowerInvariant();
        Directory.Move(folder, Path.Combine(work.FullName, id));
        return id;
    }
    var legacy = Add("legacy", ReleasePackage.Desktop, ReleasePackage.Cli);
    var older = Add("older", ReleasePackage.Desktop, ReleasePackage.Cli, ReleasePackage.Launcher);
    var latest = Add("latest", ReleasePackage.Desktop, ReleasePackage.Cli, ReleasePackage.Launcher);
    var foreign = Directory.CreateDirectory(Path.Combine(work.FullName, "000000000000"));
    File.WriteAllText(Path.Combine(foreign.FullName, "personal.txt"), "keep");
    var history = ReleasePackage.Installed(work.FullName).ToArray();
    if (!history.Select(p => p.Id).ToHashSet().SetEquals([legacy, older, latest]))
        throw new Exception("Historical desktop releases were not distinguished from an unrelated directory.");
    var launcherHash = ReleasePackage.Hash(Path.Combine(work.FullName, older, "bin", ReleasePackage.Launcher));
    if (!history.Where(p => p.MatchesLauncher(launcherHash)).Select(p => p.Id).SequenceEqual([older]))
        throw new Exception("Could not identify the installed root launcher by its actual historical hash.");
    foreach (var item in history) item.Verify();
    try
    {
        history.Single(p => p.Id == legacy).InstallInto(Path.Combine(work.FullName, "new root"));
        throw new Exception("New distributions must still contain a launcher.");
    }
    catch (InvalidDataException) { }
    File.WriteAllText(Path.Combine(work.FullName, legacy, "bin", ReleasePackage.Desktop), "changed");
    try { history.Single(p => p.Id == legacy).Verify(); throw new Exception("Changed legacy payload would be removed."); }
    catch (InvalidDataException) { }
    var legacyDir = Path.Combine(work.FullName, legacy);
    File.WriteAllText(Path.Combine(legacyDir, "bin.rar"), "user archive");
    File.WriteAllText(Path.Combine(legacyDir, "bin", "user-settings.txt"), "user file");
    history.Single(p => p.Id == legacy).RemoveInstalledFiles();
    if (File.ReadAllText(Path.Combine(legacyDir, "bin.rar")) != "user archive" ||
        File.ReadAllText(Path.Combine(legacyDir, "bin", "user-settings.txt")) != "user file" ||
        File.ReadAllText(Path.Combine(legacyDir, "bin", ReleasePackage.Desktop)) != "changed" ||
        File.Exists(Path.Combine(legacyDir, "bin", ReleasePackage.Cli)))
        throw new Exception("Uninstall did not distinguish original and user files.");
    var latestDir = Path.Combine(work.FullName, latest);
    File.WriteAllText(Path.Combine(latestDir, "bin.rar"), "archive");
    var stash = Directory.CreateDirectory(Path.Combine(work.FullName, "backup")).FullName;
    var backup = history.Single(p => p.Id == latest).InstallInto(stash);
    history.Single(p => p.Id == latest).RemoveInstalledFiles();
    backup.InstallInto(work.FullName).Verify();
    if (File.ReadAllText(Path.Combine(latestDir, "bin.rar")) != "archive")
        throw new Exception("Reinstall lost the user's sibling archive.");
    Console.WriteLine("PASS: mixed release generations, exact launcher ownership, unrelated folder exclusion, legacy uninstall verification, strict new payload.");
    if (args.Length == 1)
    {
        var installed = ReleasePackage.Installed(args[0]).ToArray();
        var current = ReleasePackage.Hash(Path.Combine(args[0], ReleasePackage.Cli));
        if (!installed.Any(p => p.MatchesLauncher(current))) throw new Exception("Current root launcher could not be identified.");
        Console.WriteLine($"READ ONLY: {installed.Length} history manifests, {installed.Count(p => p.MatchesLauncher(current))} match the current launcher.");
    }
    return 0;
}
finally { work.Delete(true); }
