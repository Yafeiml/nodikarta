#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectHub.Edge;
using ProjectHub.Edge.Desktop;

var tmp = Directory.CreateTempSubdirectory("edge-disconnect-");
try
{
    var root = tmp.FullName;
    foreach (var variable in new[] { "USERPROFILE", "HOME", "LOCALAPPDATA", "APPDATA", "CODEX_HOME", "DSH_HOME",
                 "XDG_CONFIG_HOME", "CLAUDE_CONFIG_DIR", "PI_CODING_AGENT_DIR" })
        Environment.SetEnvironmentVariable(variable, Path.Combine(root, variable));
    Environment.SetEnvironmentVariable("OPENCODE_CONFIG", Path.Combine(root, "opencode.jsonc"));
    Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", null);
    Environment.SetEnvironmentVariable("OPENCODE_CONFIG_CONTENT", null);
    Environment.SetEnvironmentVariable("PATH", root);
    foreach (var name in new[] { "claude", "cursor", "agy", "opencode", "dsh", "pi" })
        File.WriteAllText(Path.Combine(root, name + ".cmd"), "@exit /b 1");
    var other = Path.Combine(root, "ProjectHub.Edge.exe");
    File.WriteAllBytes(other, []);
    var command = GlobalCodexAccess.CurrentCommand;
    var client = new EdgeDesktopClient(Path.Combine(root, "state🚀"));
    var state = client.StateDirectory;
    var access = new HarnessAccess(client, () => true);
    var all = StandardHarnesses.Detect().Append(SpecialHarnesses.Pi()).ToArray();
    foreach (var target in all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target.ConfigPath)!);
        var entry = target.Entry(command, state).ToJsonString();
        string Wrap(string properties)
        {
            foreach (var parent in target.ServerPath.Reverse()) properties = "\"" + parent + "\":{" + properties + "}";
            return "{" + properties + "}";
        }
        foreach (var fields in new[] { $"\"projecthub\":{entry}", $"\"projecthub\":{entry},\"b\":2",
                    $"\"a\":1,\"projecthub\":{entry},\"b\":2", $"\"a\":1,\"projecthub\":{entry}" })
        {
            var original = Wrap(fields);
            File.WriteAllText(target.ConfigPath, original, new UTF8Encoding(true));
            Check(access.Inspect().Single(r => r.Id == target.Id).CanDisconnect, target.Name + " offers disconnect");
            await access.DisconnectAsync(target.Id, default);
            var bytes = File.ReadAllBytes(target.ConfigPath);
            var result = File.ReadAllText(target.ConfigPath);
            Check(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) && !result.Contains("\"projecthub\""), target.Name + " removed with BOM intact");
            var node = JsonNode.Parse(result);
            foreach (var key in target.ServerPath) node = node![key];
            Check(node!.AsObject().Count == fields.Count(c => c == ':') - entry.Count(c => c == ':') - 1, "sibling count");
        }
        if (target.SupportsJsonc)
        {
            var comment = "/* keep , 🎨 */";
            var notes = "// keep ,\r\n";
            foreach (var fields in new[] { $"\"a\":1{comment},{notes}\"projecthub\":{entry}",
                         $"\"projecthub\":{entry}{comment},{notes}\"a\":1,", $"\"projecthub\":{entry}{comment},{notes}" })
            {
                var removed = HarnessJson.Inspect(target, Wrap(fields), command, state, out _, HarnessEdit.Remove);
                Check(removed.Contains(comment) && removed.Contains(notes), "all neighboring JSONC comments survive");
                HarnessJson.Inspect(target, removed, command, state, out var action);
                Check(action == "接入", "valid JSONC after removal");
            }
            var userNote = Wrap("\"projecthub\":/* my note */" + entry);
            File.WriteAllText(target.ConfigPath, userNote);
            await Reject(() => access.DisconnectAsync(target.Id, default));
            Check(File.ReadAllText(target.ConfigPath) == userNote, "custom comment inside the registration survives");
        }
        foreach (var conflict in new[] { Wrap("\"projecthub\":{\"command\":\"custom\"}"),
                     HarnessJson.Copy(target, command, state + "-another") })
        {
            File.WriteAllText(target.ConfigPath, conflict);
            await Reject(() => access.DisconnectAsync(target.Id, default));
            Check(File.ReadAllText(target.ConfigPath) == conflict && !access.Inspect().Single(r => r.Id == target.Id).CanDisconnect,
                "custom settings and other states protected: " + target.Name);
        }
        File.Delete(target.ConfigPath);
    }

    var codex = new GlobalCodexAccess(client);
    var config = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME")!, "config.toml");
    Directory.CreateDirectory(Path.GetDirectoryName(config)!);
    var skills = Directory.CreateDirectory(Path.Combine(GlobalCodexAccess.UserHome, ".agents", "skills", "nodicarta-context"));
    var skillFile = Path.Combine(skills.FullName, "SKILL.md");
    File.WriteAllText(skillFile, "User's existing skill");
    var sibling = "model = \"fixture\"\r\n";
    File.WriteAllText(config, sibling + codex.ConfigToml().ReplaceLineEndings("\r\n"));
    await access.DisconnectAsync("codex", default);
    Check(File.ReadAllText(config) == sibling && File.ReadAllText(skillFile) == "User's existing skill", "Codex removes only its managed block");
    var otherCopy = new GlobalCodexAccess(client, Path.GetDirectoryName(config)!, GlobalCodexAccess.UserHome, other);
    File.WriteAllText(config, otherCopy.ConfigToml());
    access.MigrateInstalledCommands();
    Check(File.ReadAllText(config) == codex.ConfigToml(), "Codex adopts the newly launched location");
    await access.DisconnectAsync("codex", default);
    access.MigrateInstalledCommands(verifyWorkspaces: false);
    Check(!File.ReadAllText(config).Contains("mcp-stdio-global") && File.ReadAllText(skillFile) == "User's existing skill",
        "automatic maintenance leaves a disconnected Codex and shared Skills alone");

    var patch = Path.Combine(Environment.GetEnvironmentVariable("DSH_HOME")!, "cordis.patch.yml");
    Directory.CreateDirectory(Path.GetDirectoryName(patch)!);
    foreach (var baseline in new[] { "", "# note\n", "# 配置 🎨\r\n- id: mine\r\n", "---\n  - id: mine\n...\n", "[]", "[{id: mine}] # keep" })
    {
        File.WriteAllText(patch, SpecialHarnesses.Change(baseline, command, state, out _));
        Check(access.Inspect().Single(r => r.Id == "deepseek").CanDisconnect, "DeepSeek offers disconnect");
        await access.DisconnectAsync("deepseek", default);
        Check(File.ReadAllText(patch) == baseline, "DeepSeek preserves block and flow style: " + baseline);
    }
    foreach (var text in new[] { SpecialHarnesses.CopyDeepSeek(command, state + "-other"),
                 SpecialHarnesses.CopyDeepSeek(command, state).Replace("transport: stdio", "extra: custom\n        transport: stdio") })
    {
        File.WriteAllText(patch, text);
        await Reject(() => access.DisconnectAsync("deepseek", default));
        Check(File.ReadAllText(patch) == text, "DeepSeek custom and foreign settings preserved");
    }

    var agy = all.Single(t => t.Id == "antigravity");
    var dbSession = client.Session;
    var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace"));
    var binding = new LocalBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), workspace.FullName, "readOnly", "demo");
    using (var db = dbSession.Connect())
    using (var insert = db.CreateCommand())
    {
        var device = Guid.NewGuid();
        var tokens = JsonSerializer.SerializeToUtf8Bytes(new { DeviceId = device, EdgeId = Guid.NewGuid(), AccessToken = "fixture",
            RefreshToken = "fixture", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        insert.CommandText = "INSERT INTO identity VALUES(1,'http://127.0.0.1:1/',$blob,'active'); INSERT INTO workspaces(device_id,project_id,workspace_id,local_path) VALUES($d,$p,$w,$path)";
        insert.Parameters.AddWithValue("$blob", ProtectedData.Protect(tokens, null, DataProtectionScope.LocalMachine));
        insert.Parameters.AddWithValue("$d", device.ToString("D"));
        insert.Parameters.AddWithValue("$p", binding.ProjectId.ToString("D"));
        insert.Parameters.AddWithValue("$w", binding.WorkspaceId.ToString("D"));
        insert.Parameters.AddWithValue("$path", binding.LocalPath);
        insert.ExecuteNonQuery();
    }
    await access.InstallAsync("antigravity", default);
    AntigravityWorkspace.Apply(binding, command, state);
    var guide = Path.Combine(workspace.FullName, ".agents", "rules", "nodicarta-workspace.md");
    var plugin = Path.Combine(workspace.FullName, ".agents", "plugins", "nodicarta", "plugin.json");
    var managed = File.ReadAllBytes(guide);
    dbSession.EnableAntigravity(other);
    File.WriteAllText(agy.ConfigPath, HarnessJson.Copy(agy, other, state));
    var workspaceConfig = Path.Combine(Path.GetDirectoryName(plugin)!, "mcp_config.json");
    File.AppendAllText(guide, "User edits");
    access.MigrateInstalledCommands(verifyWorkspaces: false);
    var pendingRow = access.Inspect().Single(r => r.Id == agy.Id);
    Check(pendingRow.IsConnected && pendingRow.CanDisconnect && !pendingRow.CanBatchInstall && dbSession.AntigravityMaintenance().Pending &&
        File.ReadAllText(guide).EndsWith("User edits"), "workspace edits stay intact while automatic relocation is pending");
    File.WriteAllBytes(guide, managed);
    access.MigrateInstalledCommands(verifyWorkspaces: false);
    var workspaceServers = JsonNode.Parse(File.ReadAllText(workspaceConfig))!["mcpServers"]!.AsObject();
    Check(workspaceServers.Single().Value!["command"]!.GetValue<string>() == command && dbSession.AntigravityReady(command),
        "background retry adopts the executable in bound workspace plugins after the conflict is resolved");
    File.AppendAllText(guide, "User edits");
    await Reject(() => access.DisconnectAsync("antigravity", default));
    var maintenance = dbSession.AntigravityMaintenance();
    Check(maintenance.Enabled == null && maintenance.Disabled == command && maintenance.Pending && !File.Exists(plugin)
        && File.ReadAllText(guide).EndsWith("User edits") && !File.ReadAllText(agy.ConfigPath).Contains("projecthub"),
        "global disconnect retires owned files and persists retry for the edited file");
    Check(access.Inspect().Single(r => r.Id == agy.Id) is { ActionText: "待核对", CanDisconnect: false, CanInstall: false },
        "pending directory cleanup retries automatically without prompting for another disconnect");
    File.WriteAllBytes(guide, managed);
    new HarnessAccess(new EdgeDesktopClient(state)).RestoreAntigravity();
    Check(!File.Exists(guide) && !dbSession.AntigravityMaintenance().Pending && !dbSession.AntigravityReady(command),
        "restart finishes pending cleanup and keeps maintenance disabled");
    await access.DisconnectAsync(agy.Id, default);
    File.WriteAllText(agy.ConfigPath, HarnessJson.Copy(agy, other, state));
    access.RestoreAntigravity();
    Check(File.ReadAllText(agy.ConfigPath) == HarnessJson.Copy(agy, command, state) && dbSession.AntigravityReady(command),
        "an explicitly restored same-state Antigravity entry migrates to the new location and re-enables workspace maintenance");
    dbSession.PrepareAntigravityDisconnect(command); // process exited before committing the global edit
    Check(dbSession.AntigravityMaintenance().RegistrationPending && File.ReadAllText(agy.ConfigPath).Contains("projecthub"), "interrupted fixture");
    access.RestoreAntigravity();
    Check(!dbSession.AntigravityMaintenance().RegistrationPending && !File.ReadAllText(agy.ConfigPath).Contains("projecthub"), "startup completes interrupted removal");
    foreach (var row in access.Inspect().Where(r => r.CanDisconnect))
        await access.DisconnectAsync(row.Id, default);
    for (int i = 0; i < 2; i++) access.MigrateInstalledCommands(verifyWorkspaces: false);
    Check(access.Inspect().All(r => !r.CanDisconnect && !r.IsConnected), "background maintenance never reconnects removed registrations");
    var unusedDrive = "ZYXWVUTSRQPONMLKJIHGFEDCBA".FirstOrDefault(c => !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c));
    if (unusedDrive != default)
    {
        await access.InstallAsync("antigravity", default);
        dbSession.EnableAntigravity(other);
        File.WriteAllText(agy.ConfigPath, HarnessJson.Copy(agy, other, state));
        dbSession.PrepareAntigravityDisconnect(other);
        var profileRoot = Environment.GetEnvironmentVariable("USERPROFILE");
        using (File.Open(other, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                Environment.SetEnvironmentVariable("USERPROFILE", $@"{unusedDrive}:\offline-profile");
                await Reject(() => Task.Run(() => access.RestoreAntigravity(verifyWorkspaces: false)));
                Check(dbSession.AntigravityMaintenance().RegistrationPending, "an offline profile never confirms global configuration removal");
            }
            finally { Environment.SetEnvironmentVariable("USERPROFILE", profileRoot); }
            access.RestoreAntigravity(verifyWorkspaces: false);
            Check(!dbSession.AntigravityMaintenance().RegistrationPending && !File.ReadAllText(agy.ConfigPath).Contains("projecthub"),
                "after moving Edge a recorded disconnect completes even when the old executable is inaccessible");
        }
        access.MigrateInstalledCommands(verifyWorkspaces: false);
        Check(!access.Inspect().Single(r => r.Id == agy.Id).IsConnected && !File.Exists(plugin),
            "maintenance keeps the relocated disconnected client and all its workspaces retired");
        await access.InstallAsync("antigravity", default);
        AntigravityWorkspace.Apply(binding, command, state);
        using var db = dbSession.Connect();
        using var update = db.CreateCommand();
        update.CommandText = "UPDATE workspaces SET local_path=$path WHERE workspace_id=$id";
        update.Parameters.AddWithValue("$id", binding.WorkspaceId.ToString("D"));
        update.Parameters.AddWithValue("$path", $@"{unusedDrive}:\offline");
        update.ExecuteNonQuery();
        await Reject(() => access.DisconnectAsync("antigravity", default));
        Check(dbSession.AntigravityMaintenance().Pending && File.Exists(guide), "offline volume keeps a durable cleanup job");
        update.CommandText = "UPDATE antigravity_pending SET local_path=$path WHERE workspace_id=$id";
        update.Parameters["$path"].Value = binding.LocalPath;
        update.ExecuteNonQuery();
        access.RestoreAntigravity();
        Check(!dbSession.AntigravityMaintenance().Pending && !File.Exists(guide), "returned volume cleaned without re-enabling");
    }
    Console.WriteLine("PASS: seven client disconnects, foreign/edit protection, shared skills, and durable Antigravity cleanup.");
}
finally { tmp.Delete(true); }

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

static async Task Reject(Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) when (ex is InvalidDataException or IOException) { return; }
    throw new Exception("Expected a configuration conflict.");
}
