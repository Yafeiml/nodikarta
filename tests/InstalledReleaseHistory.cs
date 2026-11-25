#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Reflection;
using System.Text.Json;
using ProjectHub.Edge.Desktop;

var work = Directory.CreateTempSubdirectory("nodicarta-flat-release-");
try
{
    var root = work.CreateSubdirectory("installed").FullName;
    var scope = work.CreateSubdirectory("isolated").FullName;
    ReleasePackage Bundle(string name)
    {
        var folder = work.CreateSubdirectory(name);
        var bin = folder.CreateSubdirectory("bin");
        bin.CreateSubdirectory("Assets");
        var entries = new List<string>();
        foreach (var file in new[] { ReleasePackage.Desktop, ReleasePackage.Cli, ReleasePackage.NativeCli, "Assets/product.txt" })
        {
            var path = Path.Combine(bin.FullName, file);
            File.WriteAllText(path, name + " " + file);
            entries.Add(ReleasePackage.Hash(path) + "  " + file);
        }
        File.WriteAllLines(Path.Combine(folder.FullName, ReleasePackage.Manifest), entries);
        var package = new ReleasePackage(folder.FullName, bundle: true);
        package.VerifyBundle();
        return package;
    }

    var previous = Bundle("previous");
    var next = Bundle("next");
    foreach (var name in previous.Files.Keys)
        ReleasePackage.CopyAtomically(Path.Combine(previous.DirectoryPath, name), Path.Combine(root, name));
    ReleasePackage.CopyAtomically(Path.Combine(work.FullName, "previous", ReleasePackage.Manifest), Path.Combine(root, ReleasePackage.Manifest));
    var installed = ReleasePackage.Active(root)!;
    Check(installed.Id == previous.Id && ReleasePackage.IsInstalledLauncher(root, installed) &&
        installed.Executable == Path.Combine(root, ReleasePackage.Desktop), "the installation manifest owns the single root runtime");
    installed.Verify();
    var userFile = Path.Combine(root, "Assets", "user.txt");
    File.WriteAllText(userFile, "keep");

    var lease = Path.Combine(root, ReleasePackage.Lease);
    File.WriteAllBytes(lease, []);
    using (File.Open(lease, FileMode.Open, FileAccess.Read, FileShare.Read))
        Check(!ReleaseInstallation.IsLaunchIdle(root), "an open MCP read lease defers the update");
    Check(ReleaseInstallation.IsLaunchIdle(root), "the update can resume after the MCP exits");

    var recovery = typeof(ReleaseInstallation).GetMethod("Recover", BindingFlags.NonPublic | BindingFlags.Static)!;
    var links = new ReleaseLinks(scope, pathsOnly: true);
    var change = new ReleaseInstallation.RuntimeChange(
        ReleasePackage.NativeCli, previous.Files[ReleasePackage.NativeCli], next.Files[ReleasePackage.NativeCli]);
    var untouched = new ReleaseInstallation.RuntimeChange(
        ReleasePackage.Desktop, previous.Files[ReleasePackage.Desktop], next.Files[ReleasePackage.Desktop]);
    var missing = new ReleaseInstallation.RuntimeChange("new/folder/file.txt", null, next.Files[ReleasePackage.Desktop]);
    var tx = new ReleaseInstallation.Transaction(scope, false, true, [change, untouched, missing], new byte[]?[links.Paths.Length], null);
    var journal = Path.Combine(root, ReleasePackage.Journal);
    var backup = Directory.CreateDirectory(Path.Combine(root, ".nodicarta-install-backup")).FullName;
    var native = Path.Combine(root, ReleasePackage.NativeCli);
    ReleasePackage.CopyAtomically(native, Path.Combine(backup, ReleasePackage.NativeCli));
    ReleasePackage.CopyAtomically(Path.Combine(next.DirectoryPath, ReleasePackage.NativeCli), native);
    File.WriteAllText(Path.Combine(backup, ".nodicarta-install-write.tmp"), "interrupted copy");
    File.WriteAllBytes(journal, JsonSerializer.SerializeToUtf8Bytes(tx, DesktopJson.Default.Transaction));
    recovery.Invoke(null, [root, links, scope]);
    installed.Verify();
    Check(!File.Exists(journal) && !Directory.Exists(backup) && File.ReadAllText(userFile) == "keep",
        "replaying a partially written transaction restores the original runtime without leaving a backup or discarding user files");

    File.WriteAllText(installed.Executable, "locally changed");
    try { installed.Verify(); throw new Exception("Verification accepted a changed payload."); }
    catch (InvalidDataException) { }
    installed.RemoveInstalledFiles();
    Check(ReleasePackage.Active(root) == null && !File.Exists(native) && File.ReadAllText(userFile) == "keep" &&
          File.ReadAllText(installed.Executable) == "locally changed", "uninstall only removes owned, verified payload files");
    Console.WriteLine("PASS: flat manifest, MCP update deferral, interrupted recovery and owned-file uninstall.");
}
finally { work.Delete(true); }

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
