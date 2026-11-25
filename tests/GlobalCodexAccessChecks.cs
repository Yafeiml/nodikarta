#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProjectHub.Edge;
using ProjectHub.Edge.Desktop;

// Every change, including the synthetic device, lives inside this disposable user layout.
var tmp = Directory.CreateTempSubdirectory("projecthub-global-codex-");
var profile = Directory.CreateDirectory(Path.Combine(tmp.FullName, "user")).FullName;
var codex = Directory.CreateDirectory(Path.Combine(tmp.FullName, "codex profile")).FullName;
var folder = Directory.CreateDirectory(Path.Combine(tmp.FullName, "project")).FullName;
var other = Directory.CreateDirectory(Path.Combine(tmp.FullName, "custom")).FullName;
var client = new EdgeDesktopClient(Path.Combine(tmp.FullName, "state"));
var exe = Path.Combine(tmp.FullName, "ProjectHub.Edge.exe");
File.WriteAllBytes(exe, []);
var service = new GlobalCodexAccess(client, codex, profile, exe);
var globals = Path.Combine(codex, "config.toml");
var device = Guid.NewGuid();
var project = new DeviceProject(Guid.NewGuid(), "test", Guid.NewGuid(), "readWrite");
LocalBinding Bind(string path) => new(project.ProjectId, project.BindingId, Guid.NewGuid(), path, "readWrite", project.Name);
var binding = Bind(folder);
var custom = Bind(other);
var endpoint = new EdgeAccessEndpoint(Guid.NewGuid(), "fixed");
void Check(bool value, string message) { if (!value) throw new Exception(message); }
try
{
    // Persist the local pre-upgrade shape. The Core is intentionally unreachable throughout the migration.
    using (var db = new SqliteConnection($"Data Source={Path.Combine(client.StateDirectory, "edge.sqlite3")};Pooling=False"))
    {
        db.Open();
        using var sql = db.CreateCommand();
        sql.CommandText = "INSERT INTO identity VALUES(1,'http://127.0.0.1:1/',$secret,'active')";
        var tokens = JsonSerializer.SerializeToUtf8Bytes(new { DeviceId = device, EdgeId = Guid.NewGuid(), AccessToken = "TEST", RefreshToken = "TEST", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        sql.Parameters.AddWithValue("$secret", ProtectedData.Protect(tokens, null, DataProtectionScope.LocalMachine));
        sql.ExecuteNonQuery();
        foreach (var b in new[] { binding, custom })
        {
            sql.Parameters.Clear();
            sql.CommandText = "INSERT INTO workspaces VALUES($d,$p,$path,$w)";
            sql.Parameters.AddWithValue("$d", device.ToString()); sql.Parameters.AddWithValue("$p", b.ProjectId.ToString());
            sql.Parameters.AddWithValue("$path", b.LocalPath); sql.Parameters.AddWithValue("$w", b.WorkspaceId.ToString());
            sql.ExecuteNonQuery();
        }
    }

    var existingText = "model = 'a user setting'\r\n[mcp_servers.projecthub_other]\r\ncommand = 'keep me'\r\n";
    File.WriteAllText(globals, existingText, new UTF8Encoding(true));
    var initial = File.ReadAllBytes(globals);
    var legacy = EdgeAccessSetup.CreateConfig(new(project, binding, [endpoint], ""), EdgeAccessProfile.Tasks, endpoint.Id, exe, client.StateDirectory);
    CodexAccessFile.Install(binding, client.StateDirectory, legacy);
    var localFile = CodexAccessFile.ConfigPath(binding);
    File.AppendAllText(localFile, "\r\n[unrelated]\r\nkeep = true\r\n");
    Directory.CreateDirectory(Path.Combine(other, ".codex"));
    var foreign = "[mcp_servers.projecthub]\nurl='http://localhost:1/custom'\n";
    File.WriteAllText(CodexAccessFile.ConfigPath(custom), foreign);
    ProjectSkills.Install(folder, ["list_projects","get_project_snapshot","submit_knowledge_commit"]);
    File.AppendAllText(Path.Combine(folder, ".agents", "skills", "nodicarta-sync", "SKILL.md"), "\nUSER EDIT");
    Check(service.InspectHarness(false) is { Installed: false, CanInstall: true, ActionText: "接入" }, "manual setup remains available without a detected launcher");
    var first = await service.ConnectHarnessAsync(default);
    Check(service.ReadSkills().InstalledCount == 5 && first.Contains("已迁移 1"), first);
    Check(service.InspectHarness(false) is { Installed: false, CanInstall: false, ActionText: "已接入", Detail: "已找到接入配置" },
        "existing managed configuration remains visible without a detected launcher");
    var saved = File.ReadAllBytes(globals);
    var installed = File.ReadAllText(globals);
    Check(saved.AsSpan().StartsWith(initial) && installed.Contains("[mcp_servers.nodicarta]") &&
        !installed.Contains("[mcp_servers.projecthub]") && installed.Contains("mcp-stdio-global") && !installed.Contains("--project") &&
        !installed.Contains("enabled_tools") && !installed.Contains("cwd ="), "user config BOM/content and cwd routing");
    Check(File.ReadAllText(localFile).Contains("keep = true") && !File.ReadAllText(localFile).Contains("[mcp_servers."), "scoped block migration");
    var stored = client.Session.GetWorkspacePolicy(binding);
    Check(stored?.Tools != null && stored.Tools.ToHashSet().SetEquals(legacy.Tools) && stored.EndpointId == endpoint.Id, "preserve manual limit and endpoint offline");
    Check(File.ReadAllText(CodexAccessFile.ConfigPath(custom)) == foreign && client.Session.GetWorkspacePolicy(custom)?.Tools?.Length == 0, "protect custom config");
    Check(!Directory.Exists(UserSkills.Target(folder, "nodicarta-context")) && !Directory.Exists(UserSkills.Target(folder, "nodicarta-init")) &&
        File.ReadAllText(Path.Combine(UserSkills.Target(folder,"nodicarta-sync"),"SKILL.md")).EndsWith("USER EDIT"), "exact legacy skills only");
    Check(UserSkills.Names.Contains("nodicarta-init") && UserSkills.Names.All(n => File.Exists(Path.Combine(UserSkills.Target(profile, n), "SKILL.md"))), "install all five");
    client.Session.SetWorkspacePolicy(binding, new WorkspaceAccessPolicy(["list_projects"], null));
    await Task.WhenAll(service.InstallAsync(), service.InstallAsync());
    Check(saved.SequenceEqual(File.ReadAllBytes(globals)) && client.Session.GetWorkspacePolicy(binding)?.Tools is ["list_projects"], "retry/concurrency keeps exact bytes and explicit new scope");

    var customSkill = Path.Combine(UserSkills.Target(profile, "nodicarta-assets"), "SKILL.md");
    File.AppendAllText(customSkill, "\nUSER EDIT");
    await service.InstallAsync();
    await service.CleanForReconnectAsync();
    Check(!File.ReadAllText(globals).Contains("mcp-stdio-global") && File.ReadAllBytes(globals).AsSpan().StartsWith(initial), "disconnect surgically");
    Check(File.ReadAllText(customSkill).EndsWith("USER EDIT") && !Directory.Exists(UserSkills.Target(profile,"nodicarta-context")), "keep user-modified global Skill");
    Check(!File.Exists(UserSkills.InitReceipt(profile)), "disconnect removes the managed delivery receipt");
    await service.InstallAsync();
    Check(client.Session.GetWorkspacePolicy(binding)?.Tools is ["list_projects"], "reconnect keeps the migrated scope");
    File.WriteAllText(globals, foreign);
    Check(!service.InspectHarness(false).CanInstall, "manual setup protects custom registrations even when the launcher is unavailable");
    try { await service.InstallAsync(); throw new Exception("accepted foreign global config"); }
    catch (InvalidDataException) { }
    Check(File.ReadAllText(globals) == foreign, "global config custom kept");
    await service.CleanForReconnectAsync();
    Check(File.ReadAllText(globals) == foreign && File.Exists(customSkill), "disconnect preserves foreign global entry and customized user Skill");
    var manual = "[mcp_servers.nodicarta]\ncommand = " + JsonSerializer.Serialize(exe) +
        "\nargs = " + JsonSerializer.Serialize(EdgeAccessSetup.GlobalArguments(client.StateDirectory)) + "\n";
    foreach (var userConfig in new[] { manual, service.ConfigToml() + "\n" + foreign })
    {
        File.WriteAllText(globals, userConfig);
        Check(service.InspectHarness(true) is { ActionText: "待核对", CanInstall: false, CanDisconnect: false }
            && !service.RepairMissingCommand(), "manual or duplicate connection must remain user-owned");
        try { await service.ConnectHarnessAsync(default); throw new Exception("accepted user-owned configuration"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(globals) == userConfig, "connecting preserves manual or duplicate entries");
    }
    var separateBin = Directory.CreateDirectory(Path.Combine(tmp.FullName, "other portable", "bin")).FullName;
    var separateCommand = Path.Combine(separateBin, "ProjectHub.Edge.exe");
    File.WriteAllText(separateCommand, "independent portable binary");
    var otherProjectEntry = EdgeAccessSetup.CreateConfig(new(project, binding, [endpoint], ""),
        EdgeAccessProfile.Tasks, endpoint.Id, separateCommand, client.StateDirectory);
    CodexAccessFile.Install(binding, client.StateDirectory, otherProjectEntry);
    File.WriteAllText(globals, existingText);
    await service.InstallAsync();
    await service.CleanForReconnectAsync();
    Check(!File.ReadAllText(localFile).Contains("[mcp_servers.") && File.ReadAllText(localFile).Contains("keep = true"),
        "global Codex migrates the same-state managed project configuration even while its previous executable exists");
    var installRoot = Directory.CreateDirectory(Path.Combine(tmp.FullName, "managed")).FullName;
    string Release(string id)
    {
        var dir = Directory.CreateDirectory(Path.Combine(installRoot, id, "bin")).FullName;
        var file = Path.Combine(dir, "ProjectHub.Edge.exe");
        File.WriteAllBytes(file, []);
        return file;
    }
    var oldExe = Release("123456789abc");
    var newExe = Release("0123456789ab");
    var alternateState = Path.Combine(tmp.FullName, "alternative state");
    var originalBlock = CodexAccessFile.Format(oldExe, ["mcp-stdio-global", "--state", alternateState], [], true, EdgeAccessSetup.LegacyServerName);
    var oldConfig = existingText + originalBlock + "\n";
    File.WriteAllText(globals, oldConfig);
    var moved = new GlobalCodexAccess(new EdgeDesktopClient(alternateState), codex, profile, newExe);
    using (File.Open(oldExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        Check(!moved.RepairMissingCommand() && moved.InspectHarness(true) is { ActionText: "待核对", CanInstall: false }
            && File.ReadAllText(globals) == oldConfig, "a temporarily unreadable old executable keeps its registration");
    }
    var missingSkill = UserSkills.Target(profile, "nodicarta-inbox");
    if (Directory.Exists(missingSkill)) Directory.Delete(missingSkill, true);
    Check(moved.RepairMissingCommand() && !moved.RepairMissingCommand(), "automatic adoption of a live previous path writes just once");
    Check(moved.InspectHarness(false) is { IsConnected: true, Installed: false, CanDisconnect: true, CanInstall: false } &&
        moved.InspectHarness(true) is { IsConnected: true, CanBatchInstall: false },
        "existing Codex connection stays connected even without a detected launcher");
    var refreshed = File.ReadAllText(globals);
    Check(refreshed.StartsWith(existingText) && refreshed.Contains("[mcp_servers.projecthub]") &&
        !refreshed.Contains("[mcp_servers.nodicarta]") && refreshed.Contains("0123456789ab") && refreshed.Contains("alternative state")
        && !refreshed.Contains("123456789abc") && !Directory.Exists(missingSkill),
        "automatic relocation changes the managed target without reinstalling Skills or changing user settings");
    File.WriteAllText(globals, oldConfig);
    Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(oldExe)!)!, recursive: true);
    GlobalCodexAccess.RefreshManagedCommand(installRoot, newExe, globals, client.StateDirectory);
    Check(File.ReadAllText(globals) == oldConfig && !service.RepairMissingCommand(), "other Edge state cannot adopt a missing target");
    File.WriteAllText(globals, oldConfig);
    Check(moved.RepairMissingCommand() && !moved.RepairMissingCommand() && File.ReadAllText(globals) == refreshed,
        "repair detects a removed parent directory, preserves the user config and is idempotent");
    var mounted = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
    var offlineLetter = "ZYXWVUTSRQPONMLKJIHGFEDCBA".FirstOrDefault(c => !mounted.Contains(c));
    if (offlineLetter != default)
    {
        var absent = CodexAccessFile.Format($@"{offlineLetter}:\test\ProjectHub.Edge.exe", ["mcp-stdio-global", "--state", alternateState], [], true);
        File.WriteAllText(globals, existingText + absent);
        Check(!moved.RepairMissingCommand() && !moved.InspectHarness(false).CanInstall && File.ReadAllText(globals) == existingText + absent,
            "an unavailable drive keeps its registration until the target can be checked");
    }
    File.WriteAllText(globals, foreign);
    Check(moved.InspectHarness(false) is { ActionText: "待核对", CanInstall: false }, "custom registration cannot be switched");
    try { moved.RepairMissingCommand(); } catch (InvalidDataException) { }
    Check(File.ReadAllText(globals) == foreign, "background repair preserves a custom registration");
    File.WriteAllText(globals, existingText);
    Check(!moved.RepairMissingCommand() && File.ReadAllText(globals) == existingText, "background repair never restores a removed registration");
    File.WriteAllText(globals, refreshed);
    var globalForeignSkill = Path.Combine(UserSkills.Target(profile, "nodicarta-context"), "SKILL.md");
    Directory.CreateDirectory(Path.GetDirectoryName(globalForeignSkill)!);
    File.Copy(Path.Combine(AppContext.BaseDirectory, "skills", "nodicarta-context", "SKILL.md"), globalForeignSkill);
    await service.CleanForReconnectAsync();
    Check(File.ReadAllText(globals) == refreshed && File.Exists(globalForeignSkill), "other Edge state survives cleanup");
    await moved.ConnectHarnessAsync(default);
    Check(File.ReadAllText(globals) == refreshed, "reconnecting an owned legacy entry keeps its original name and bytes");
    moved.DisconnectHarness();
    Check(File.ReadAllText(globals).TrimEnd() == existingText.TrimEnd(), "disconnect removes an owned legacy entry alone");

    var managedRoot = Directory.CreateDirectory(Path.Combine(tmp.FullName, "normal installation")).FullName;
    var stable = Path.Combine(managedRoot, "ProjectHub.Edge.exe");
    File.WriteAllText(stable, "test launcher");
    File.WriteAllBytes(Path.Combine(managedRoot, ".edge-lease"), []);
    string Publish(string contents)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents)));
        var launcherHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(stable)));
        var manifest = $"{hash}  {ReleasePackage.Desktop}\n{hash}  {ReleasePackage.NativeCli}\n{launcherHash}  {ReleasePackage.Cli}\n";
        File.WriteAllText(Path.Combine(managedRoot, "SHA256SUMS"), manifest);
        File.WriteAllText(Path.Combine(managedRoot, ReleasePackage.Desktop), contents);
        File.WriteAllText(Path.Combine(managedRoot, ReleasePackage.NativeCli), contents);
        return stable;
    }
    var firstRelease = Publish("installed version 1");
    Check(firstRelease == stable && EdgeInstallCommand.DesktopEntry(Path.Combine(managedRoot, ReleasePackage.Desktop)) == stable,
        "the flat runtime uses the fixed root command");
    var nextRelease = Publish("installed version 2");
    var normal = new GlobalCodexAccess(new EdgeDesktopClient(alternateState), codex, profile, nextRelease);
    string InstalledConfig(string path) => existingText + CodexAccessFile.Format(path, ["mcp-stdio-global", "--state", alternateState], [], true) + "\r\n";
    File.WriteAllText(globals, oldConfig, new UTF8Encoding(true));
    GlobalCodexAccess.RefreshManagedCommand(managedRoot, nextRelease, globals, client.StateDirectory);
    Check(File.ReadAllText(globals) == oldConfig, "upgrading a different state does not take over this profile");
    GlobalCodexAccess.RefreshManagedCommand(managedRoot, nextRelease, globals, alternateState);
    var stableBytes = File.ReadAllBytes(globals);
    Check(File.ReadAllText(globals).ReplaceLineEndings("\n").Contains(CodexAccessFile.Format(stable, ["mcp-stdio-global", "--state", alternateState], [], true, EdgeAccessSetup.LegacyServerName)) &&
        stableBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "refresh repairs a missing managed command and preserves BOM");
    var thirdRelease = Publish("installed version 3");
    GlobalCodexAccess.RefreshManagedCommand(managedRoot, thirdRelease, globals, alternateState);
    Check(normal.InspectHarness(true) is { IsConnected: true, CanDisconnect: true, CanBatchInstall: false } &&
        !normal.RepairMissingCommand() && stableBytes.SequenceEqual(File.ReadAllBytes(globals)),
        "switching to another verified release keeps the fixed registration byte-for-byte");
    normal.DisconnectHarness();
    var disconnected = File.ReadAllBytes(globals);
    GlobalCodexAccess.RefreshManagedCommand(managedRoot, thirdRelease, globals, alternateState);
    Check(!normal.RepairMissingCommand() && disconnected.SequenceEqual(File.ReadAllBytes(globals)),
        "upgrading a disconnected client does not add a registration");
    var manifestPath = Path.Combine(managedRoot, "SHA256SUMS");
    var originalManifest = File.ReadAllBytes(manifestPath);
    var launcherBytes = File.ReadAllBytes(stable);
    foreach (var fault in new[] { "invalid manifest", "missing manifest", "changed launcher" })
    {
        if (fault == "invalid manifest") File.AppendAllText(manifestPath, "invalid\r\n");
        else if (fault == "missing manifest") File.Delete(manifestPath);
        else File.WriteAllText(stable, "unexpected executable");
        var reopened = new GlobalCodexAccess(new EdgeDesktopClient(alternateState), codex, profile, thirdRelease);
        var keep = InstalledConfig(stable);
        File.WriteAllText(globals, keep);
        foreach (var view in new[] { normal, reopened })
        {
            Check(view.InspectHarness(true) is { ActionText: "待核对", CanInstall: false } &&
                !view.RepairMissingCommand(), $"{fault}: a running and a reopened client preserve the registration");
        }
        GlobalCodexAccess.RefreshManagedCommand(managedRoot, thirdRelease, globals, alternateState);
        Check(File.ReadAllText(globals) == keep, $"{fault}: neither installer nor automatic repair moved the target");
        File.WriteAllBytes(manifestPath, originalManifest);
        File.WriteAllBytes(stable, launcherBytes);
    }
    File.WriteAllBytes(globals, [0xff, 0x9f]);
    GlobalCodexAccess.RefreshManagedCommand(installRoot, newExe, globals, alternateState);
    Check(File.ReadAllBytes(globals).SequenceEqual(new byte[] { 0xff, 0x9f }), "foreign encoding survives upgrade");
    Console.WriteLine("PASS: isolated user setup, BOM/foreign config, five Skills, offline migration, explicit endpoint, concurrent retry, clean and reconnect.");
}
finally { tmp.Delete(true); }
