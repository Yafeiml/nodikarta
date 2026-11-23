#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge/ProjectHub.Edge.csproj
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectHub.Edge;

var scratch = Directory.CreateTempSubdirectory("projecthub-agy-files-");
try
{
    var state = Directory.CreateDirectory(Path.Combine(scratch.FullName, "设备状态")).FullName;
    string Binary(string version)
    {
        var bin = Directory.CreateDirectory(Path.Combine(scratch.FullName, version));
        var cli = Path.Combine(bin.FullName, "ProjectHub.Edge.exe");
        File.WriteAllBytes(cli, []);
        return cli;
    }
    var cli = Binary("old");
    var updated = Binary("new version");
    var directory = Directory.CreateDirectory(Path.Combine(scratch.FullName, "工作目录")).FullName;
    var binding = new LocalBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), directory, "readWrite", "fixture");
    var agents = Path.Combine(directory, ".agents");
    var rules = Directory.CreateDirectory(Path.Combine(agents, "rules")).FullName;
    var userConfig = Path.Combine(agents, "mcp_config.json");
    var plugin = Path.Combine(agents, "plugins", "nodicarta");
    var manifest = Path.Combine(plugin, "plugin.json");
    var config = Path.Combine(plugin, "mcp_config.json");
    var guide = Path.Combine(rules, "nodicarta-workspace.md");
    var userRules = Path.Combine(directory, "AGENTS.md");
    File.WriteAllText(userRules, "# 用户指引\r\n保留原始格式", new UTF8Encoding(true));
    var originalRules = File.ReadAllBytes(userRules);
    var other = Path.Combine(rules, "existing.md");
    File.WriteAllText(other, "User rule");
    const string originalConfig = """{"preference":"项目","mcpServers":{"user_server":{"command":"别的程序"}}}""";
    File.WriteAllText(userConfig, originalConfig, new UTF8Encoding(true));
    var originalBytes = File.ReadAllBytes(userConfig);
    JsonNode Config() => JsonNode.Parse(File.ReadAllText(config))!;
    var serverKey = "kb_" + binding.WorkspaceId.ToString("N");
    JsonNode Entry() => Config()["mcpServers"]![serverKey]!;
    void Apply(string command) => AntigravityWorkspace.Apply(binding, command, state);
    void Remove() => AntigravityWorkspace.Remove(binding, state);

    Apply(cli);
    Check(JsonNode.Parse(File.ReadAllText(manifest))!["name"]?.GetValue<string>() == "nodicarta",
        "native plugin supplies the server namespace");
    Check(Entry()["command"]?.GetValue<string>() == cli && Entry()["cwd"]?.GetValue<string>() == "${PLUGIN_ROOT}/../../..",
        "client expands its actual project directory without embedding a local workspace path");
    Check(JsonNode.DeepEquals(Entry()["args"], new JsonArray("mcp-stdio-global", "--state", state,
        "--bound-workspace", binding.WorkspaceId.ToString("D"))), "workspace-pinned command");
    var instructions = File.ReadAllText(guide);
    Check(instructions.Contains("trigger: always_on") && instructions.Contains("nodicarta_" + serverKey)
        && instructions.Contains("search_project_knowledge"), "Antigravity automatically receives a generic retrieval rule");
    var installedConfig = File.ReadAllBytes(config);
    var installedGuide = File.ReadAllBytes(guide);
    Apply(cli);
    Check(File.ReadAllBytes(config).SequenceEqual(installedConfig) && File.ReadAllBytes(guide).SequenceEqual(installedGuide), "idempotent setup");
    Apply(updated);
    Check(Entry()["command"]?.GetValue<string>() == updated, "reapply after moving Edge");
    Remove();
    Check(!File.Exists(config) && !File.Exists(manifest) && !File.Exists(guide)
        && File.ReadAllBytes(userConfig).SequenceEqual(originalBytes),
        "remove only the managed plugin and rule, preserving the user's workspace config and BOM");
    Check(File.ReadAllBytes(userRules).SequenceEqual(originalRules) && File.ReadAllText(other) == "User rule",
        "pre-existing project instructions stay byte-identical");

    const string conflict = """{"mcpServers":{"workspace":{"url":"https://example.invalid/my-server"}}}""";
    Directory.CreateDirectory(plugin);
    File.WriteAllText(config, conflict);
    Attempt(() => Apply(cli));
    Check(File.ReadAllText(config) == conflict && !File.Exists(guide) && !File.Exists(manifest), "never replace a user's same-name plugin");
    File.Delete(config);
    Directory.Delete(plugin);
    File.WriteAllText(guide, "My own instructions");
    Attempt(() => Apply(cli));
    Check(File.ReadAllText(guide) == "My own instructions" && !File.Exists(config), "rule collision leaves the workspace intact");
    File.Delete(guide);
    Apply(cli);
    var addedLater = Path.Combine(plugin, "user-file.txt");
    File.WriteAllText(addedLater, "Keep this");
    File.AppendAllText(guide, "\nUser edit");
    Attempt(Remove);
    Check(File.ReadAllText(guide).EndsWith("\nUser edit") && File.ReadAllText(addedLater) == "Keep this"
        && !File.Exists(config) && !File.Exists(manifest), "retain user edits and remove only managed plugin files");
    CheckRetiredWorkspace(scratch.FullName, cli, binding);
    CheckBrandMigration(scratch.FullName, cli, state, binding);
    Console.WriteLine("PASS: workspace pin, retrieval rule, managed legacy migration, coexistence, retry, cleanup and custom file preservation.");
}
finally { scratch.Delete(true); }

static void CheckBrandMigration(string root, string cli, string state, LocalBinding binding)
{
    foreach (var fixture in new[] { "managed", "customized", "foreign", "unknown receipt", "collision" })
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, fixture)).FullName;
        var workspace = binding with { WorkspaceId = Guid.NewGuid(), LocalPath = directory };
        var agents = Directory.CreateDirectory(Path.Combine(directory, ".agents")).FullName;
        var plugin = Directory.CreateDirectory(Path.Combine(agents, "plugins", "projecthub")).FullName;
        var rules = Directory.CreateDirectory(Path.Combine(agents, "rules")).FullName;
        var legacyFiles = new[] { Path.Combine(plugin, "mcp_config.json"), Path.Combine(rules, "projecthub-workspace.md"), Path.Combine(plugin, "plugin.json") };
        var key = "kb_" + workspace.WorkspaceId.ToString("N");
        var contents = new[] { """{"mcpServers":{"workspace":{}}}""", "Use projecthub_" + key, """{"name":"projecthub"}""" };
        for (int i = 0; i < contents.Length; i++) File.WriteAllText(legacyFiles[i], contents[i]);
        var hashes = legacyFiles.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        var receipts = Directory.CreateDirectory(Path.Combine(state, "antigravity-project-plugins")).FullName;
        var legacyReceipt = Path.Combine(receipts, workspace.WorkspaceId.ToString("D") + ".json");
        if (fixture != "foreign")
            File.WriteAllBytes(legacyReceipt, JsonSerializer.SerializeToUtf8Bytes(new { CreatedFolders = new[] {true,true,true,true}, Hashes = hashes }));
        if (fixture == "unknown receipt") File.WriteAllText(legacyReceipt, "{ unknown format");
        if (fixture == "customized")
        {
            File.AppendAllText(legacyFiles[0], " user config edit");
            File.AppendAllText(legacyFiles[1], " user rule edit");
        }
        var newRule = Path.Combine(rules, "nodicarta-workspace.md");
        if (fixture == "collision")
        {
            File.WriteAllText(newRule, "not owned");
            Attempt(() => AntigravityWorkspace.Apply(workspace, cli, state));
            Check(File.ReadAllText(newRule) == "not owned" && File.Exists(legacyReceipt) && legacyFiles.All(File.Exists),
                "a target-name collision leaves the original plugin and receipt usable");
            File.Delete(newRule);
        }
        AntigravityWorkspace.Apply(workspace, cli, state);
        AntigravityWorkspace.Apply(workspace, cli, state);
        var manifest = Path.Combine(agents, "plugins", "nodicarta", "plugin.json");
        var name = JsonNode.Parse(File.ReadAllBytes(manifest))!["displayName"]!.GetValue<string>();
        Check(name.Contains("脉络") && File.ReadAllText(newRule).Contains("nodicarta_" + key), "new manifest and rule agree on the installed name");
        Check(File.Exists(legacyReceipt) == (fixture == "unknown receipt"), "retire only legacy receipts of a known format");
        AntigravityWorkspace.Remove(workspace, state);
        Check(!File.Exists(manifest) && !File.Exists(newRule), "new installation can be disconnected independently");
        if (fixture is "foreign" or "customized" or "unknown receipt")
        {
            Check(legacyFiles.All(File.Exists) && File.ReadAllText(legacyFiles[2]) == contents[2]
                && File.ReadAllText(legacyFiles[0]) == contents[0] + (fixture == "customized" ? " user config edit" : "")
                && File.ReadAllText(legacyFiles[1]) == contents[1] + (fixture == "customized" ? " user rule edit" : ""),
                "unmanaged or customized old plugin and guide survive installation and disconnect");
        }
        else Check(!Directory.Exists(agents), "owned legacy files retired and shared directories cleaned on final disconnect");
    }
}

static void CheckRetiredWorkspace(string scratch, string cli, LocalBinding original)
{
    var session = EdgeSession.Open(Path.Combine(scratch, "retry-state"));
    var parent = Directory.CreateDirectory(Path.Combine(scratch, "removed-parent"));
    var directory = parent.CreateSubdirectory("workspace");
    var retired = original with { LocalPath = directory.FullName, WorkspaceId = Guid.NewGuid() };
    AntigravityWorkspace.Apply(retired, cli, session.StateDirectory);
    var receipt = Path.Combine(session.StateDirectory, "antigravity-project-plugins", retired.WorkspaceId.ToString("D") + ".nodicarta.json");
    // Previous releases kept this cleanup job after the binding itself had already been revoked.
    using var db = session.Connect();
    using var job = db.CreateCommand();
    job.CommandText = "INSERT INTO antigravity_pending(workspace_id,project_id,local_path) VALUES($id,$project,$path)";
    job.Parameters.AddWithValue("$id", retired.WorkspaceId.ToString("D"));
    job.Parameters.AddWithValue("$project", retired.ProjectId.ToString("D"));
    job.Parameters.AddWithValue("$path", retired.LocalPath);
    job.ExecuteNonQuery();
    parent.Delete(recursive: true);

    var mounted = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
    var offline = "ZYXWVUTSRQPONMLKJIHGFEDCBA".FirstOrDefault(letter => !mounted.Contains(letter));
    if (offline != default)
    {
        job.CommandText = "UPDATE antigravity_pending SET local_path=$path WHERE workspace_id=$id";
        job.Parameters["$path"].Value = $@"{offline}:\offline-project";
        job.ExecuteNonQuery();
        Attempt(() => session.EnableAntigravity(cli));
        Check(File.Exists(receipt), "an unavailable volume keeps its cleanup receipt");
        job.Parameters["$path"].Value = retired.LocalPath;
        Check(job.ExecuteNonQuery() == 1, "an unavailable volume keeps its pending job");
    }
    session.EnableAntigravity(cli);
    Check(session.AntigravityReady(cli) && !File.Exists(receipt) && !Directory.Exists(parent.FullName),
        "retry clears old retirement jobs for a deleted workspace and parent without recreating either directory");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Attempt(Action action)
{
    try { action(); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException) { }
}
