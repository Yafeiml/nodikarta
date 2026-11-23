#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AssetChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge/ProjectHub.Edge.csproj
using ProjectHub.Edge;
using System.Runtime.InteropServices;

var temp = Directory.CreateTempSubdirectory("projecthub-asset-paths-");
var workspace = temp.CreateSubdirectory("workspace").FullName;
var sibling = temp.CreateSubdirectory("sibling").FullName;
var outside = Path.Combine(sibling, "private.svg");
File.WriteAllBytes(outside, [3]);
try
{
    foreach (var invalid in new[] { "../sibling/private.svg", outside, "x.svg:secret", @"\\.\NUL", "NUL.svg", "a/./b.svg", "a./logo.svg" })
        Reject(() => { using var path = new AssetWorkspace(workspace, invalid, true); });
    using (var path = new AssetWorkspace(workspace, "设计/new/logo.svg", true))
    {
        await path.SaveAsync([1, 2], default);
        Reject(() => path.SaveAsync([3], default).GetAwaiter().GetResult());
        using var stream = path.OpenSource();
        if (stream.ReadByte() != 1 || stream.ReadByte() != 2 || stream.ReadByte() != -1) throw new Exception("Original changed.");
    }
    var link = Path.Combine(workspace, "link.svg");
    if (!CreateHardLinkW(link, outside, IntPtr.Zero)) throw new Exception("Test setup failed to create NTFS hard link.");
    Reject(() => { using var path = new AssetWorkspace(workspace, "link.svg", false); using var file = path.OpenSource(); });
    var junction = Path.Combine(workspace, "outside");
    var cmd = new System.Diagnostics.ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
    foreach (var arg in new[] { "/d", "/c", "mklink", "/j", junction, sibling }) cmd.ArgumentList.Add(arg);
    using (var proc = System.Diagnostics.Process.Start(cmd)!)
    {
        proc.WaitForExit();
        if (proc.ExitCode != 0) throw new Exception("Test setup failed to create NTFS junction.");
    }
    try
    {
        Reject(() => { using var path = new AssetWorkspace(workspace, "outside/private.svg", false); using var file = path.OpenSource(); });
        Reject(() => { using var path = new AssetWorkspace(workspace, "outside/child/new.svg", true); });
        if (Directory.Exists(Path.Combine(sibling, "child"))) throw new Exception("Created a directory outside the workspace.");
    }
    finally { Directory.Delete(junction); }
    Console.WriteLine("PASS: confined source/destination, existing files, nested folders, Windows device names, ADS, traversal, hard links and junctions.");
}
finally { temp.Delete(true); }

static void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is ArgumentException or IOException) { return; }
    throw new Exception("Expected path operation to be refused.");
}

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
