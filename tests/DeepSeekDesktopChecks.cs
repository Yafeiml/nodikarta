#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using Microsoft.Win32;
using ProjectHub.Edge.Desktop;

var temp = Directory.CreateTempSubdirectory("nodicarta-deepseek-desktop-");
var fixtureKey = @"Software\Nodicarta-DeepSeek-Test-" + Guid.NewGuid().ToString("N");
try
{
    var profile = temp.CreateSubdirectory("user").FullName;
    var local = Path.Combine(profile, "AppData", "Local");
    Environment.SetEnvironmentVariable("LOCALAPPDATA", local);
    Environment.SetEnvironmentVariable("USERPROFILE", profile);
    Environment.SetEnvironmentVariable("PATH", temp.FullName);
    Environment.SetEnvironmentVariable("DSH_HOME", null);
    const string exe = "DeepSeek Harness.exe";
    Write(Path.Combine(local, "Programs", "DeepSeek Harness", exe), "");
    Check(SpecialHarnesses.DeepSeekInstalled(), "Default desktop installation without dsh on PATH.");

    using var apps = Registry.CurrentUser.CreateSubKey(fixtureKey, true, RegistryOptions.Volatile);
    using var registered = apps.CreateSubKey(SpecialHarnesses.DeepSeekWindowsProduct, true, RegistryOptions.Volatile);
    var custom = Path.Combine(temp.FullName, "custom, installation", exe);
    registered.SetValue("DisplayIcon", $"\"{custom}\",0");
    bool Found() => WindowsDesktopInstallation.HasInstalledProduct(apps, [SpecialHarnesses.DeepSeekWindowsProduct], exe);
    Check(!Found(), "Removed binary with a leftover registry entry.");
    Write(custom, "");
    Check(Found(), "Custom desktop directory in a DisplayIcon-only NSIS entry.");

    var edge = Path.Combine(temp.FullName, "ProjectHub.Edge.exe");
    Write(edge, "");
    var state = Path.Combine(temp.FullName, "state");
    foreach (var harnessHome in new[] { Path.Combine(profile, ".dsh"), Path.Combine(temp.FullName, "custom home") })
    {
        if (Path.GetFileName(harnessHome) != ".dsh") Environment.SetEnvironmentVariable("DSH_HOME", harnessHome);
        var patch = Path.Combine(harnessHome, "cordis.patch.yml");
        var desktopPatch = Path.Combine(harnessHome, "profiles", "desktop", "cordis.patch.yml");
        const string original = "# Keep existing settings\r\n- id: custom\r\n";
        Write(patch, original);
        Write(desktopPatch, "# Desktop profile\r\n[]");
        SpecialHarnesses.InstallDeepSeek(edge, state);
        var installed = File.ReadAllText(patch);
        Check(installed.Contains("@deepseek-ai/dsh-mcp-client") && installed.Contains("mcp-stdio-global") &&
            SpecialHarnesses.InspectDeepSeek(edge, state).CanDisconnect, "Global patch is ready in selected home.");
        SpecialHarnesses.DisconnectDeepSeek(edge, state);
        Check(File.ReadAllText(patch) == original && File.ReadAllText(desktopPatch) == "# Desktop profile\r\n[]",
            "Disconnect preserves the home patch and the separate desktop profile.");
    }
    Console.WriteLine("DeepSeek Harness desktop detection and shared home patch registration/disconnection passed.");
}
finally
{
    Registry.CurrentUser.DeleteSubKeyTree(fixtureKey, false);
    temp.Delete(true);
}

static void Write(string path, string text)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
