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
    const string conflict = """{"preferences":{"theme":"dark"},"mcpServers":{"nodicarta":{"command":"custom"},"other":{"command":"keep"}}}""";
    var classicFile = Path.Combine(roaming, "Claude", filename);
    var oldPackagedFile = Path.Combine(local, "Packages", family, "LocalCache", "Roaming", "Claude", filename);
    var currentPackagedFile = Path.Combine(local, "Claude-Data", family.ToLowerInvariant(), filename);
    var cliFile = Path.Combine(home, ".claude.json");
    var launcher = Path.Combine(commands, "claude.cmd");
    var packaged = true;
    var state = Path.Combine(scratch.FullName, "state");
    var access = new HarnessAccess(new EdgeDesktopClient(state), () => false, () => packaged);
    StandardHarness Desktop() => StandardHarnesses.Detect(() => packaged).Single(t => t.Id == "claude-desktop");
    HarnessRow Row() => access.Inspect().Single(row => row.Id is "claude" or "claude-desktop");
    static JsonNode? Entry(string config) => JsonNode.Parse(config)!["mcpServers"]![EdgeAccessSetup.ServerName];
    void CheckSaved(string file, string copy)
    {
        var saved = File.ReadAllText(file);
        var config = JsonNode.Parse(saved)!;
        Check(Entry(saved) is { } entry && JsonNode.DeepEquals(entry, Entry(copy)), "Saved registration matches its copy.");
        Check(config["preferences"]!["theme"]!.GetValue<string>() == "dark" && config["mcpServers"]!["other"]!["command"]!.GetValue<string>() == "keep",
            "Unrelated preferences and servers survive the merge.");
    }

    Check(Desktop().Detected && Desktop().BlockedReason != null && Row() is { Id: "claude", Installed: true, CanBatchInstall: false, ActionText: "待核对" },
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
    Check(Row() is { Id: "claude", CanBatchInstall: true }, "Desktop-only installation has one actionable Claude row.");

    var desktopCopy = access.CopyConfig("claude");
    Check(Entry(desktopCopy) != null && desktopCopy.Contains("--workspace-roots") && !desktopCopy.Contains("CLAUDE_PROJECT_DIR"),
        "Desktop-only copy is plain JSON using MCP roots.");
    await access.InstallAsync("claude", default);
    CheckSaved(currentPackagedFile, desktopCopy);
    Check(Row() is { IsConnected: true, CanDisconnect: true } && !File.Exists(cliFile) && File.ReadAllText(oldPackagedFile) == original,
        "Connecting Desktop alone leaves Claude Code and the previous profile alone.");

    var desktopBefore = Stamp(currentPackagedFile);
    Write(launcher, "@exit /b 1");
    Write(cliFile, original);
    Check(Row() is { Id: "claude", CanBatchInstall: true, PartiallyConnected: true },
        "Installing Claude Code while Edge is open makes the same Claude row actionable.");
    const string desktopHeading = "Claude Chat · MCP\n";
    const string cliHeading = "Claude Code · MCP\n";
    var copies = access.CopyConfig("claude").Split("\n\n", StringSplitOptions.None);
    Check(copies is [var desktopSection, var cliSection] &&
        desktopSection.StartsWith(desktopHeading, StringComparison.Ordinal) && cliSection.StartsWith(cliHeading, StringComparison.Ordinal),
        "Both installed variants have separately labeled copies in one clipboard.");
    Check(JsonNode.DeepEquals(Entry(copies[0][desktopHeading.Length..]), Entry(desktopCopy)), "Labeled Desktop copy retains its JSON.");
    var cliCopy = copies[1][cliHeading.Length..];
    Check(Entry(cliCopy) != null && cliCopy.Contains("CLAUDE_PROJECT_DIR") && !cliCopy.Contains("--workspace-roots") &&
        JsonNode.DeepEquals(Entry(cliCopy)!["command"], Entry(desktopCopy)!["command"]),
        "Both copies use the stable launcher and keep the correct workspace arguments.");
    await access.InstallAsync("claude", default);
    CheckSaved(cliFile, cliCopy);
    Check(Stamp(currentPackagedFile) == desktopBefore && Row() is { IsConnected: true, CanDisconnect: true, PartiallyConnected: false },
        "Connecting the later CLI does not rewrite the already connected Desktop configuration.");
    await access.DisconnectAsync("claude", default);
    access.MigrateInstalledCommands();
    Check(new[] { currentPackagedFile, cliFile }.All(file => JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(file)), JsonNode.Parse(original))) &&
        Row() is { IsConnected: false, CanBatchInstall: true }, "Grouped disconnect preserves both user files and background maintenance does not reconnect them.");

    packaged = false;
    Check(JsonNode.DeepEquals(Entry(access.CopyConfig("claude")), Entry(cliCopy)), "CLI-only copy is plain JSON for Claude Code.");
    await access.InstallAsync("claude", default);
    CheckSaved(cliFile, cliCopy);
    Check(Entry(File.ReadAllText(currentPackagedFile)) == null && Row().IsConnected, "Connecting the CLI alone leaves the Desktop config untouched.");
    var cliBefore = Stamp(cliFile);
    packaged = true;
    Check(Row() is { Id: "claude", CanBatchInstall: true, PartiallyConnected: true },
        "Installing Desktop while Edge is open makes the same Claude row actionable.");
    await access.InstallAsync("claude", default);
    CheckSaved(currentPackagedFile, desktopCopy);
    Check(Stamp(cliFile) == cliBefore, "Connecting the later Desktop does not rewrite the already connected CLI config.");

    File.Delete(launcher);
    Check(Row().CanDisconnect, "The grouped registration stays manageable after removing the CLI launcher.");
    await access.DisconnectAsync("claude", default);
    Check(Entry(File.ReadAllText(cliFile)) == null, "Disconnect also removes the managed registration for the removed CLI.");

    Write(launcher, "@exit /b 1");
    Write(currentPackagedFile, original);
    Write(cliFile, original);
    Check(Row() is { Id: "claude", CanBatchInstall: true, IsConnected: false, PartiallyConnected: false },
        "With both editions already installed and no registration, Claude needs one batch action.");
    await access.InstallAllAsync(default);
    CheckSaved(currentPackagedFile, desktopCopy);
    CheckSaved(cliFile, cliCopy);
    desktopBefore = Stamp(currentPackagedFile);
    cliBefore = Stamp(cliFile);
    await access.InstallAllAsync(default);
    Check(Stamp(currentPackagedFile) == desktopBefore && Stamp(cliFile) == cliBefore, "Repeating the batch leaves both registrations unchanged.");
    await access.DisconnectAsync("claude", default);

    Write(currentPackagedFile, conflict);
    var conflictBefore = Stamp(currentPackagedFile);
    Check(Row() is { CanBatchInstall: true, IsConnected: false }, "A conflict in Desktop does not block connecting an available CLI.");
    await access.InstallAllAsync(default);
    CheckSaved(cliFile, cliCopy);
    Check(Stamp(currentPackagedFile) == conflictBefore && Row() is { PartiallyConnected: true, CanDisconnect: true, ActionText: "待核对" },
        "A grouped partial failure preserves the conflicting Desktop config and exposes the remaining managed CLI.");
    await access.DisconnectAsync("claude", default);
    access.MigrateInstalledCommands();
    Check(Stamp(currentPackagedFile) == conflictBefore && Entry(File.ReadAllText(cliFile)) == null,
        "Disconnect and maintenance leave the custom Desktop registration and the removed CLI registration alone.");

    packaged = false;
    Write(classicFile, original);
    Check(!StandardHarnesses.Detect(() => packaged).Any(target => target.Id == "claude-desktop" && target.Detected),
        "Profile and configuration leftovers alone do not prove an installation.");
    Write(Path.Combine(local, "AnthropicClaude", "claude.exe"), "");
    Check(Desktop().Detected && Desktop().ConfigPath == classicFile && Desktop().BlockedReason == null,
        "The classic executable selects roaming AppData even when stale packaged profiles exist.");
    Console.WriteLine("PASS: one Claude row, either edition added later, batch and retry, target-specific copies, conflict preservation, disconnect and profile migration.");
}
finally { scratch.Delete(true); }

static (string Bytes, DateTime Modified) Stamp(string path) => (Convert.ToBase64String(File.ReadAllBytes(path)), File.GetLastWriteTimeUtc(path));

static void Write(string path, string text)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
