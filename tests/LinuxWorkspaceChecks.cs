#:property TargetFramework=net10.0
#:property AssemblyName=ProjectHub.Edge.AssetChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge/ProjectHub.Edge.csproj
using System.Runtime.InteropServices;
using ProjectHub.Edge;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Run this check on Linux.");
var fixture = Directory.CreateTempSubdirectory();
try
{
    var root = fixture.CreateSubdirectory("照片").FullName;
    var upper = fixture.CreateSubdirectory("Case").FullName;
    var lower = fixture.CreateSubdirectory("case").FullName;
    Check(!WorkspacePath.Overlaps(upper, lower, WorkspacePath.Verified(lower)), "case-sensitive workspace paths");
    Directory.CreateSymbolicLink(Path.Combine(root, "link"), upper);
    Refused(() => WorkspacePath.Verified(Path.Combine(root, "link")));
    Refused(() => { using var file = new AssetWorkspace(root, "link/nested/image.png", true); });
    Check(!Directory.Exists(Path.Combine(upper, "nested")), "outside directory untouched");

    using (var file = new AssetWorkspace(root, "形状/preview.png", true))
    {
        await file.SaveAsync([1, 2, 3], default);
        Refused(() => file.SaveAsync([4], default).GetAwaiter().GetResult());
        using var source = file.OpenSource();
        Check(source.Length == 3 && source.ReadByte() == 1, "download is atomic and preserves its destination");
    }
    var outside = Path.Combine(upper, "outside.svg");
    File.WriteAllText(outside, "<svg/>");
    File.CreateSymbolicLink(Path.Combine(root, "symlink.svg"), outside);
    Check(link(outside, Path.Combine(root, "hardlink.svg")) == 0, "create test hardlink");
    Check(mkfifo(Path.Combine(root, "fifo.svg"), 384) == 0, "create test FIFO");
    foreach (var name in new[] { "symlink.svg", "hardlink.svg", "fifo.svg" })
        Refused(() => { using var file = new AssetWorkspace(root, name, false); using var input = file.OpenSource(); });
    using (var pinned = new AssetWorkspace(root, "待保存/preview.png", true))
    {
        Directory.Move(Path.Combine(root, "待保存"), Path.Combine(root, "原目录"));
        Directory.CreateSymbolicLink(Path.Combine(root, "待保存"), upper);
        await pinned.SaveAsync([7], default);
        Check(File.Exists(Path.Combine(root, "原目录", "preview.png")) && !File.Exists(Path.Combine(upper, "preview.png")),
            "renaming a checked directory does not redirect an in-flight download");
    }

    Console.WriteLine("PASS: Linux workspace case, symlink rejection, regular-file reads, no-overwrite writes, hardlinks and FIFO.");
}
finally { fixture.Delete(true); }

static void Check(bool value, string description)
{
    if (!value) throw new Exception(description);
}
static void Refused(Action action)
{
    try { action(); }
    catch (Exception e) when (e is IOException or InvalidOperationException) { return; }
    throw new Exception("Expected filesystem operation to be refused.");
}

[DllImport("libc")] static extern int link(string oldpath, string newpath);
[DllImport("libc")] static extern int mkfifo(string path, uint mode);
