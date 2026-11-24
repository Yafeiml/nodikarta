#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using ProjectHub.Edge.Desktop;

var tmp = Directory.CreateTempSubdirectory("projecthub-special-harness-");
try
{
    var directory = tmp.FullName;
    var state = Path.Combine(directory, "数据 🚀");
    var exe = Path.Combine(directory, "老版本", "ProjectHub.Edge.exe");
    var updated = Path.Combine(directory, "用户's Edge 🚀", "ProjectHub.Edge.exe");
    foreach (var file in new[] { exe, updated })
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
    }
    Environment.SetEnvironmentVariable("PATH", directory);
    Environment.SetEnvironmentVariable("DSH_HOME", directory);
    Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", directory);
    Environment.SetEnvironmentVariable("LOCALAPPDATA", directory);
    Environment.SetEnvironmentVariable("APPDATA", directory);
    Environment.SetEnvironmentVariable("npm_config_prefix", Path.Combine(directory, "npm"));
    var piOriginallyDetected = SpecialHarnesses.Pi().Detected;
    File.WriteAllText(Path.Combine(directory, "mcp.json"), "{}");
    Check(SpecialHarnesses.Pi().Detected == piOriginallyDetected, "a leftover Pi profile is not installation evidence");
    var desktopFolder = Directory.CreateDirectory(Path.Combine(directory, "Programs", "pi-desktop"));
    var desktop = Path.Combine(desktopFolder.FullName, "Pi Desktop.exe");
    File.WriteAllText(desktop, "");
    Check(SpecialHarnesses.Pi() is { Detected: true, BlockedReason: not null }, "Pi Desktop without its engine cannot connect");
    var npm = Directory.CreateDirectory(Path.Combine(directory, "npm"));
    var package = Directory.CreateDirectory(Path.Combine(npm.FullName, "node_modules", "@earendil-works", "pi-coding-agent"));
    File.WriteAllText(Path.Combine(npm.FullName, "pi.cmd"), "");
    File.WriteAllText(Path.Combine(package.FullName, "package.json"), """{"name":"@earendil-works/pi-coding-agent","version":"1.6.0"}""");
    var runtime = Path.Combine(Directory.CreateDirectory(Path.Combine(package.FullName, "dist", "bundle")).FullName, "cli.js");
    Check(SpecialHarnesses.Pi().BlockedReason != null, "a leftover npm shim cannot connect");
    File.WriteAllText(runtime, "");
    Check(SpecialHarnesses.Pi() is { Detected: true, BlockedReason: null }, "Pi Desktop resolves npm outside Edge's PATH");
    File.Delete(runtime);
    Directory.CreateDirectory(Path.Combine(directory, "bin"));
    File.WriteAllText(Path.Combine(directory, "bin", "pi.cmd"), "");
    var install = Directory.CreateDirectory(Path.Combine(directory, "install"));
    File.WriteAllText(Path.Combine(install.FullName, "current-version"), "1.6.0");
    var managed = Directory.CreateDirectory(Path.Combine(install.FullName, "releases", "1.6.0", "node_modules", "@earendil-works", "pi-coding-agent"));
    File.Copy(Path.Combine(package.FullName, "package.json"), Path.Combine(managed.FullName, "package.json"));
    File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(managed.FullName, "dist")).FullName, "cli.js"), "");
    Check(SpecialHarnesses.Pi().BlockedReason == null, "Pi Desktop resolves an official managed engine");
    File.Delete(Path.Combine(install.FullName, "current-version"));
    File.Delete(desktop);
    var desktopWasInstalled = SpecialHarnesses.DeepSeekInstalled();
    var patch = Path.Combine(directory, "cordis.patch.yml");
    File.WriteAllText(patch, "[]");
    Check(SpecialHarnesses.DeepSeekInstalled() == desktopWasInstalled, "leftover DeepSeek config is not an installation");
    File.WriteAllText(Path.Combine(directory, "pi.cmd"), "");
    File.WriteAllText(Path.Combine(directory, "dsh.cmd"), "");
    Check(SpecialHarnesses.Pi().Detected && SpecialHarnesses.DeepSeekInstalled(), "executable detection");
    Check(SpecialHarnesses.Pi().ConfigPath == Path.Combine(directory, "mcp.json"), "custom Pi directory");
    var manifest = Path.Combine(directory, "package.json");
    File.WriteAllText(manifest, """{"name":"@mariozechner/pi-coding-agent","version":"0.55.0"}""");
    Check(SpecialHarnesses.Pi().BlockedReason != null, "legacy Pi requires an upgrade");
    File.WriteAllText(manifest, """{"name":"@earendil-works/pi-coding-agent","version":"1.6.0"}""");
    Check(SpecialHarnesses.Pi().BlockedReason == null, "native Pi MCP supported");
    const string other = "# 配置 🎨\r\n- id: other\r\n  config: !!js process.env.TEST\r\n";
    foreach (var baseline in new[] { "", "# notes", other, "---\n  - id: other\n...\n", "[]", "[{id: other}] # hi" })
    {
        var added = SpecialHarnesses.Change(baseline, exe, state, out var action);
        Check(action == "接入" && added.Contains("id: nodicarta-edge") && added.Contains("serverName: nodicarta"), "initial setup");
        Check(SpecialHarnesses.Change(added, exe, state, out action) == added && action == "已接入", "idempotence");
        var upgraded = SpecialHarnesses.Change(added, updated, state, out action, HarnessEdit.Repair);
        Check(action == "已接入" && upgraded != added && upgraded.Contains("用户''s Edge"), "automatic relocation while previous executable still exists");
        Check(SpecialHarnesses.Change(upgraded, updated, state, out action) == upgraded && action == "已接入", "upgraded readback");
        if (baseline == other) Check(upgraded.Replace(SpecialHarnesses.CopyDeepSeek(updated, state).ReplaceLineEndings("\r\n") + "\r\n", "") == baseline, "unrelated patch bytes");
        Refuses(() => SpecialHarnesses.Change(added, updated, state + "-different", out _));
    }
    var oldEntry = SpecialHarnesses.CopyDeepSeek(exe, state, EdgeAccessSetup.LegacyServerName, "projecthub-edge");
    Check(SpecialHarnesses.Change(oldEntry, updated, state, out var oldAction, HarnessEdit.Repair) ==
        SpecialHarnesses.CopyDeepSeek(updated, state, EdgeAccessSetup.LegacyServerName, "projecthub-edge") &&
        oldAction == "已接入", "repair preserves the legacy entry ID and server name");
    foreach (var invalid in new[] { "{broken", "[]\n---\n[]", "- id: projecthub-edge", "- config: {servers: {projecthub: {}}}",
             "- insert: [{id: custom, config: {serverName: projecthub}}]", oldEntry + "\n- config: {servers: {nodicarta: {}}}",
             oldEntry + "\n" + SpecialHarnesses.CopyDeepSeek(exe, state) })
        Refuses(() => SpecialHarnesses.Change(invalid, exe, state, out _));
    var scoped = SpecialHarnesses.CopyDeepSeek(exe, state).Replace("- insert:", "- id: my-group\n  insert:");
    Refuses(() => SpecialHarnesses.Change(scoped, exe, state, out _));
    Refuses(() => SpecialHarnesses.Change(scoped, updated, state, out _));
    var computed = SpecialHarnesses.CopyDeepSeek(exe, state).Replace("command: '", "command: !!js '");
    Refuses(() => SpecialHarnesses.Change(computed, exe, state, out _));
    File.WriteAllText(patch, other);
    SpecialHarnesses.InstallDeepSeek(exe, state);
    Check(SpecialHarnesses.InspectDeepSeek(exe, state).ActionText == "已接入", "real file readback");
    var existing = File.ReadAllBytes(patch);
    var profile = Directory.CreateDirectory(Path.Combine(directory, "profiles", "web"));
    File.WriteAllText(Path.Combine(profile.FullName, "cordis.patch.yml"), "- config: {servers: {projecthub: {}}}");
    Refuses(() => SpecialHarnesses.InstallDeepSeek(updated, state));
    Check(File.ReadAllBytes(patch).SequenceEqual(existing), "profile conflict leaves home patch unchanged");
    Console.WriteLine("DeepSeek and Pi configuration checks passed.");
}
finally { tmp.Delete(true); }

static void Check(bool condition, string check)
{
    if (!condition) throw new Exception(check);
}
static void Refuses(Action check)
{
    try { check(); }
    catch (InvalidDataException) { return; }
    throw new Exception("Expected a configuration conflict.");
}
