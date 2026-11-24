#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using Microsoft.Win32;
using ProjectHub.Edge.Desktop;

var temp = Directory.CreateTempSubdirectory("edge-desktop-detection-");
var key = @"Software\Nodicarta-Detection-Test-" + Guid.NewGuid().ToString("N");
try
{
    var local = Path.Combine(temp.FullName, "Local");
    var programs = Path.Combine(temp.FullName, "Program Files");
    foreach (var folder in new[] { "ai.opencode.desktop", "@opencodedesktop", "opencode-desktop" })
    {
        var desktop = Write(Path.Combine(local, "Programs", folder, "OpenCode.exe"));
        Check(WindowsDesktopInstallation.OpenCodeDesktop(local, programs), folder);
        File.Delete(desktop);
    }
    var cursor = Write(Path.Combine(programs, "Cursor", "Cursor.exe"));
    Check(WindowsDesktopInstallation.CursorDesktop(local, programs), "Cursor System");
    File.Delete(cursor);

    using var apps = Registry.CurrentUser.CreateSubKey(key, true, RegistryOptions.Volatile);
    const string product = "d074f30d-5f88-5885-b075-be1348cc7676";
    using var app = apps.CreateSubKey(product, true, RegistryOptions.Volatile);
    var install = Path.Combine(temp.FullName, "自定义,安装");
    app.SetValue("InstallLocation", install);
    Write(Path.Combine(install, "settings.json"));
    bool Registered(string executable) => WindowsDesktopInstallation.HasInstalledProduct(apps, [product], executable);
    Check(!Registered("OpenCode.exe"), "stale registration");
    var custom = Write(Path.Combine(install, "OpenCode.exe"));
    Check(Registered("OpenCode.exe"), "custom install");
    Check(!Registered("Cursor.exe"), "different executable");
    Check(!WindowsDesktopInstallation.HasInstalledProduct(apps, ["unrelated"], "OpenCode.exe"), "different product");
    app.DeleteValue("InstallLocation");
    app.SetValue("DisplayIcon", $"\"{custom}\",0");
    Check(Registered("OpenCode.exe"), "NSIS DisplayIcon with index");
    File.Delete(custom);
    Check(!Registered("OpenCode.exe"), "uninstalled");
    Write(Path.Combine(install, "Cursor.exe"));
    foreach (var id in new[] { "{DADADADA-ADAD-ADAD-ADAD-ADADADADADAD}_is1", "{D8D8D8D8-8D8D-8D8D-8D8D-8D8D8D8D8D8D}}_is1" })
    {
        using (var registered = apps.CreateSubKey(id, true, RegistryOptions.Volatile)) registered.SetValue("InstallLocation", install);
        Check(WindowsDesktopInstallation.HasInstalledProduct(apps, WindowsDesktopInstallation.CursorProducts, "Cursor.exe"), id);
        apps.DeleteSubKey(id);
    }
    Console.WriteLine("Cursor and OpenCode desktop installation checks passed.");
}
finally
{
    Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
    temp.Delete(true);
}

static string Write(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "");
    return path;
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}
