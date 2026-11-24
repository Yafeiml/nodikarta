#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Text.Json.Nodes;
using ProjectHub.Edge.Desktop;

var scratch = Directory.CreateTempSubdirectory("nodicarta-claude-desktop-");
try
{
    var home = scratch.CreateSubdirectory("user").FullName;
    foreach (var variable in new[] { "USERPROFILE", "HOME" })
        Environment.SetEnvironmentVariable(variable, home);
    foreach (var variable in new[] { "LOCALAPPDATA", "APPDATA", "XDG_CONFIG_HOME", "CODEX_HOME", "DSH_HOME", "PI_CODING_AGENT_DIR" })
        Environment.SetEnvironmentVariable(variable, Path.Combine(home, variable));
    foreach (var variable in new[] { "CLAUDE_CONFIG_DIR", "OPENCODE_CONFIG", "OPENCODE_CONFIG_DIR", "OPENCODE_CONFIG_CONTENT" })
        Environment.SetEnvironmentVariable(variable, null);
    var commands = scratch.CreateSubdirectory("bin").FullName;
    Environment.SetEnvironmentVariable("PATH", commands);

    var local = Environment.GetEnvironmentVariable("LOCALAPPDATA")!;
    var roaming = Environment.GetEnvironmentVariable("APPDATA")!;
    const string family = "Claude_pzs8sxrjxfjjc";
    const string filename = "claude_desktop_config.json";
    const string original = """{"preferences":{"theme":"dark"},"mcpServers":{"other":{"command":"keep"}}}""";
    var classicFile = Path.Combine(roaming, "Claude", filename);
    var oldPackagedFile = Path.Combine(local, "Packages", family, "LocalCache", "Roaming", "Claude", filename);
    var currentPackagedFile = Path.Combine(local, "Claude-Data", family.ToLowerInvariant(), filename);
    var cliFile = Path.Combine(home, ".claude.json");
    var packaged = true;
    var state = Path.Combine(scratch.FullName, "state");
    var access = new HarnessAccess(new EdgeDesktopClient(state), () => false, () => packaged);
    StandardHarness Desktop() => StandardHarnesses.Detect(() => packaged).Single(t => t.Id == "claude-desktop");
    HarnessRow Row(string id) => access.Inspect().Single(row => row.Id == id);
    static string Executable(string config) => JsonNode.Parse(config)!["mcpServers"]!["projecthub"]!["command"]!.GetValue<string>();

    Check(Desktop().Detected && Desktop().BlockedReason != null && !Row("claude-desktop").CanBatchInstall,
        "A registered package before first launch is installed and waits for a profile.");
    Check(!File.Exists(currentPackagedFile) && !File.Exists(oldPackagedFile) && !File.Exists(cliFile),
        "Inspect does not create a Claude configuration.");
    Write(oldPackagedFile, original);
    Check(Desktop().ConfigPath == oldPackagedFile && Desktop().BlockedReason == null, "Older MSIX profile is usable.");
    var marker = Path.Combine(Path.GetDirectoryName(oldPackagedFile)!, ".user-data-left." + family.ToLowerInvariant());
    Write(marker, "migrated");
    Check(Desktop().BlockedReason != null, "A migrated-away profile cannot receive a connection.");
    Write(currentPackagedFile, original);
    Check(Desktop().ConfigPath == currentPackagedFile && Desktop().BlockedReason == null, "New profile wins over migrated LocalCache.");
    Check(!access.Inspect().Any(row => row.Id == "claude") && Row("claude-desktop").CanBatchInstall,
        "Desktop-only installation has one actionable Claude row.");

    var desktopCopy = access.CopyConfig("claude-desktop");
    Check(desktopCopy.Contains("--workspace-roots") && !desktopCopy.Contains("CLAUDE_PROJECT_DIR"), "Desktop uses MCP roots.");
    await access.InstallAsync("claude-desktop", default);
    Check(Row("claude-desktop").CanDisconnect && !File.Exists(cliFile) && File.ReadAllText(oldPackagedFile) == original,
        "Connecting Desktop leaves Claude Code and the previous profile alone.");

    var launcher = Path.Combine(commands, "claude.cmd");
    Write(launcher, "@exit /b 1");
    Write(cliFile, original);
    Check(Row("claude").CanBatchInstall && Row("claude-desktop").IsConnected, "Both clients have independent rows.");
    var cliCopy = access.CopyConfig("claude");
    Check(cliCopy.Contains("CLAUDE_PROJECT_DIR") && !cliCopy.Contains("--workspace-roots") && Executable(cliCopy) == Executable(desktopCopy),
        "Both copies use the stable launcher and keep the correct workspace arguments.");
    await access.InstallAsync("claude", default);
    foreach (var (file, copy) in new[] { (cliFile, cliCopy), (currentPackagedFile, desktopCopy) })
    {
        var config = JsonNode.Parse(File.ReadAllText(file))!;
        Check(JsonNode.DeepEquals(config["mcpServers"]!["projecthub"], JsonNode.Parse(copy)!["mcpServers"]!["projecthub"]),
            "Each saved connection matches its own copy.");
        Check(config["preferences"]!["theme"]!.GetValue<string>() == "dark" && config["mcpServers"]!["other"]!["command"]!.GetValue<string>() == "keep",
            "Unrelated preferences and servers survive the merge.");
    }
    var connectedCli = File.ReadAllText(cliFile);
    await access.DisconnectAsync("claude-desktop", default);
    Check(File.ReadAllText(cliFile) == connectedCli && Row("claude").IsConnected, "Disconnecting Desktop preserves Claude Code.");
    Check(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(currentPackagedFile)), JsonNode.Parse(original)),
        "Disconnecting Desktop removes only its managed entry.");
    File.Delete(launcher);
    Check(Row("claude").CanDisconnect, "An existing Claude Code registration stays manageable after removing its launcher.");
    await access.DisconnectAsync("claude", default);
    Check(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(cliFile)), JsonNode.Parse(original)), "Disconnecting Claude Code preserves user fields.");

    packaged = false;
    Write(classicFile, original);
    Check(!StandardHarnesses.Detect(() => packaged).Any(target => target.Id == "claude-desktop" && target.Detected),
        "Profile and configuration leftovers alone do not prove an installation.");
    Write(Path.Combine(local, "AnthropicClaude", "claude.exe"), "");
    Check(Desktop().Detected && Desktop().ConfigPath == classicFile && Desktop().BlockedReason == null,
        "The classic executable selects roaming AppData even when stale packaged profiles exist.");
    Console.WriteLine("Claude Desktop and Claude Code installation detection, profile selection, independent registration and disconnect passed.");
}
finally { scratch.Delete(true); }

static void Write(string path, string text)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
