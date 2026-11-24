#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using ProjectHub.Edge;
using ProjectHub.Edge.Desktop;
using DesktopProgram = ProjectHub.Edge.Desktop.Program;

var scratch = Directory.CreateTempSubdirectory("nodicarta-desktop-state-");
try
{
    var root = scratch.FullName;
    var user = Directory.CreateDirectory(Path.Combine(root, "user")).FullName;
    var local = Path.Combine(user, "local");
    var machine = Path.Combine(root, "machine");
    using var known = EdgeStatePaths.UseKnownFoldersForTest(local, machine);
    var old = Path.Combine(local, "ProjectHub", "Edge");
    var canonical = EdgeStatePaths.LocalDefault;
    foreach (var name in new[] { "USERPROFILE", "LOCALAPPDATA", "APPDATA", "XDG_CONFIG_HOME", "CODEX_HOME", "CLAUDE_CONFIG_DIR", "DSH_HOME", "PI_CODING_AGENT_DIR" })
        Environment.SetEnvironmentVariable(name, Directory.CreateDirectory(Path.Combine(user, name)).FullName);
    Environment.SetEnvironmentVariable("OPENCODE_CONFIG", Path.Combine(user, "opencode.jsonc"));
    Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", null);
    Environment.SetEnvironmentVariable("OPENCODE_CONFIG_CONTENT", null);
    Environment.SetEnvironmentVariable("PATH", root);
    foreach (var name in new[] { "codex", "claude", "cursor", "agy", "dsh", "opencode", "pi" })
        File.WriteAllText(Path.Combine(root, name + ".cmd"), "");
    Environment.SetEnvironmentVariable("PROJECTHUB_EDGE_STATE", old);
    Check(DesktopProgram.DefaultStateDirectory == old, "old environment override still works");
    Environment.SetEnvironmentVariable("NODICARTA_EDGE_STATE", canonical);
    Check(DesktopProgram.DefaultStateDirectory == canonical, "new process override has priority");
    Check(!Directory.Exists(old), "simulate a registered old path without its database");
    var client = new EdgeDesktopClient(DesktopProgram.ResolveDesktopDirectory(old));
    var current = GlobalCodexAccess.CurrentCommand;
    var previous = Path.Combine(root, "old version", "ProjectHub.Edge.exe");
    var missing = Path.Combine(root, "removed version", "ProjectHub.Edge.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
    File.WriteAllBytes(previous, []);
    var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME")!;
    var config = Path.Combine(codexHome, "config.toml");
    const string preamble = "model = 'custom'\r\n";
    static string Global(string exe, string state) => CodexAccessFile.Format(exe, EdgeAccessSetup.GlobalArguments(state), [], true);
    var codex = new GlobalCodexAccess(client);
    var targets = StandardHarnesses.Detect(() => false).Append(SpecialHarnesses.Pi()).ToArray();
    var patch = Path.Combine(Environment.GetEnvironmentVariable("DSH_HOME")!, "cordis.patch.yml");
    var harnesses = new HarnessAccess(client, () => true, () => false);
    foreach (var exe in new[] { previous, missing })
    {
        var expectedCommand = current;
        File.WriteAllText(config, preamble + Global(exe, old).ReplaceLineEndings("\r\n"), new UTF8Encoding(true));
        var original = new Dictionary<string, string>();
        foreach (var target in targets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target.ConfigPath)!);
            var baseJson = target.SupportsJsonc ? "{ /* old folder: " + old + " */ \"keep\":42,}" : "{\"keep\":42}";
            original[target.Id] = HarnessJson.Inspect(target, baseJson, exe, old, out _);
            File.WriteAllText(target.ConfigPath, original[target.Id], new UTF8Encoding(true));
        }
        var yaml = SpecialHarnesses.Change("# keep: " + old + "\n- id: other\n", exe, old, out _);
        File.WriteAllText(patch, yaml, new UTF8Encoding(true));
        harnesses.MigrateRegisteredState();
        var global = File.ReadAllText(config);
        Check(global == preamble + Global(expectedCommand, canonical).ReplaceLineEndings("\r\n") &&
            File.ReadAllBytes(config).AsSpan().StartsWith(Encoding.UTF8.Preamble), "Codex managed hash, other copy, BOM and siblings");
        Check(codex.InspectHarness(true) is { IsConnected: true, CanDisconnect: true }, "Codex UI after path migration");
        foreach (var target in targets)
        {
            var text = File.ReadAllText(target.ConfigPath);
            var baseJson = target.SupportsJsonc ? "{ /* old folder: " + old + " */ \"keep\":42,}" : "{\"keep\":42}";
            var expected = HarnessJson.Inspect(target, baseJson, expectedCommand, canonical, out _);
            Check(text == expected && File.ReadAllBytes(target.ConfigPath).AsSpan().StartsWith(Encoding.UTF8.Preamble),
                "only managed paths changed: " + target.Name);
        }
        Check(File.ReadAllText(patch) == SpecialHarnesses.Change("# keep: " + old + "\n- id: other\n", expectedCommand, canonical, out _),
            "DeepSeek preserves custom text and adopts the current executable");
        harnesses.MigrateRegisteredState();
        Check(File.ReadAllText(config) == global, "repeatable");
    }
    var registered = targets.Single(t => t.Id == "cursor");
    foreach (var foreign in new[] { HarnessJson.Copy(registered, previous, Path.Combine(root, "other", "ProjectHub", "Edge")),
                 HarnessJson.Copy(registered, previous, old).Replace("\"type\":", "\"timeout\": 9, \"type\":"),
                 HarnessJson.Copy(registered, previous, EdgeStatePaths.MachineDefault) })
    {
        File.WriteAllText(registered.ConfigPath, foreign);
        harnesses.MigrateRegisteredState();
        Check(File.ReadAllText(registered.ConfigPath) == foreign, "other state or edited registration kept");
    }
    var folder = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
    var pid = Guid.NewGuid();
    var binding = new LocalBinding(pid, Guid.NewGuid(), Guid.NewGuid(), folder, "readOnly", "example");
    var endpoint = Guid.NewGuid();
    string[] scopedArgs = ["mcp-stdio", "--state", old, "--project", pid.ToString(), "--binding", binding.BindingId.ToString(),
        "--mode", "readOnly", "--endpoint", endpoint.ToString(), "--workspace", binding.WorkspaceId.ToString(), "--tools", "list_projects"];
    var scoped = CodexAccessFile.Format(previous, scopedArgs, ["list_projects"]);
    var before = CodexAccessFile.Inspect(scoped, binding, canonical);
    scopedArgs[2] = canonical;
    Check(before.RelocatedBlock == CodexAccessFile.Format(current, scopedArgs, ["list_projects"]) &&
        before.ToolLimit is ["list_projects"] && before.Endpoint == endpoint, "legacy directory policy, endpoint and workspace retained");
    var device = Guid.NewGuid();
    using (var db = client.Session.Connect())
    using (var sql = db.CreateCommand())
    {
        sql.CommandText = "INSERT INTO identity VALUES(1,'http://127.0.0.1:1/',$blob,'active'); INSERT INTO workspaces VALUES($d,$p,$path,$w)";
        var tokens = JsonSerializer.SerializeToUtf8Bytes(new { DeviceId = device, EdgeId = Guid.NewGuid(), AccessToken = "fixture",
            RefreshToken = "fixture", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        sql.Parameters.AddWithValue("$blob", ProtectedData.Protect(tokens, null, DataProtectionScope.LocalMachine));
        sql.Parameters.AddWithValue("$d", device.ToString("D"));
        sql.Parameters.AddWithValue("$p", binding.ProjectId.ToString("D"));
        sql.Parameters.AddWithValue("$w", binding.WorkspaceId.ToString("D"));
        sql.Parameters.AddWithValue("$path", binding.LocalPath);
        sql.ExecuteNonQuery();
    }
    var file = CodexAccessFile.ConfigPath(binding);
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, preamble + scoped.ReplaceLineEndings("\r\n"));
    client.Session.SetWorkspacePolicy(binding, new(["list_projects"], endpoint));
    harnesses.MigrateRegisteredState();
    Check(File.ReadAllText(file) == preamble + before.RelocatedBlock!.ReplaceLineEndings("\r\n") &&
        client.Session.GetWorkspacePolicy(binding) is { Tools: ["list_projects"], EndpointId: var actual } && actual == endpoint,
        "startup rewrites the registered local workspace without changing its saved policy");
    var profileId = ".edge-core-" + Guid.NewGuid().ToString("N");
    scopedArgs[2] = Path.Combine(old, profileId);
    scoped = CodexAccessFile.Format(previous, scopedArgs, ["list_projects"]);
    Check(CodexAccessFile.Inspect(scoped, binding, canonical).RelocatedBlock == null, "pinned profile is never routed to root");
    var migrated = CodexAccessFile.Inspect(scoped, binding, Path.Combine(canonical, profileId));
    scopedArgs[2] = Path.Combine(canonical, profileId);
    Check(migrated.RelocatedBlock == CodexAccessFile.Format(current, scopedArgs, ["list_projects"]), "same profile retains its pin after executable relocation");

    // A successful startup reads the preference from the normalized directory; a genuine conflict reports in its former language.
    UiLanguage.Save(canonical, true);
    Check(DesktopProgram.ResolveDesktopDirectory(old) == canonical && UiLanguage.IsEnglish, "canonical UI language and singleton input");
    var physical = Path.Combine(canonical, profileId);
    EdgeSession.EnsurePrivateDirectory(physical);
    File.WriteAllBytes(Path.Combine(physical, "edge.sqlite3"), []);
    File.WriteAllText(Path.Combine(canonical, "active-core-profile"), profileId[".edge-core-".Length..]);
    Check(DesktopProgram.ResolveDesktopDirectory(Path.Combine(old, profileId)) == canonical && UiLanguage.IsEnglish,
        "active physical profile uses the same desktop language and singleton as its root");
    try { DesktopProgram.ResolveDesktopDirectory(Path.Combine(old, ".edge-core-" + Guid.NewGuid().ToString("N"))); throw new Exception("stale profile pin accepted"); }
    catch (InvalidOperationException) { }
    EdgeSession.EnsurePrivateDirectory(old);
    UiLanguage.Save(old, false);
    try { DesktopProgram.ResolveDesktopDirectory(old); throw new Exception("conflicting profiles accepted"); }
    catch (IOException ex)
    {
        Check(!UiLanguage.IsEnglish && ex.Message.Contains("同时存在"), "conflict observes the old user's language");
        UiLanguage.Save(old, true);
        Check(UiLanguage.Message(ex.Message).Contains("Both Edge data folders"), "localized conflict message");
    }
    Console.WriteLine("PASS: seven managed registrations, missing legacy database, live/deleted Edge targets, custom settings, pinned workspace policies and startup language.");
}
finally { scratch.Delete(true); }

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
