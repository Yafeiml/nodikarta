#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Text;
using System.Text.Json;
using ProjectHub.Edge;
using ProjectHub.Edge.Desktop;
using Tomlyn;
using Tomlyn.Model;

// The discovery test owns this new temporary workspace and opens it in an isolated Codex profile.
if (args is ["--skills-fixture", var fixturePath])
{
    var root = new DirectoryInfo(fixturePath);
    if (root.Parent?.FullName != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
        !root.Name.StartsWith("projecthub-skills-", StringComparison.Ordinal) || root.GetFileSystemInfos().Length != 0)
        throw new InvalidOperationException("A fresh temporary skills fixture is required.");
    var skillRoot = Path.Combine(root.FullName, ".agents", "skills");
    ProjectSkills.Install(root.FullName, ["get_project_snapshot", "list_assets"]);
    if (Directory.Exists(Path.Combine(skillRoot, "nodicarta-sync")) || Directory.Exists(Path.Combine(skillRoot, "nodicarta-init")) ||
        !Directory.Exists(Path.Combine(skillRoot, "nodicarta-context")) || !Directory.Exists(Path.Combine(skillRoot, "nodicarta-assets")))
        throw new Exception("Read-only skill selection failed.");
    ProjectSkills.Install(root.FullName, ["list_projects", "list_assets"]);
    if (!File.Exists(Path.Combine(skillRoot, "nodicarta-init", "SKILL.md")))
        throw new Exception("The init Skill must support read-only inventory when the project can be identified.");
    string[] all = ["list_projects", "get_project_snapshot", "submit_knowledge_commit", "list_feedback", "list_assets"];
    ProjectSkills.Install(root.FullName, all);
    var contextFile = Path.Combine(skillRoot, "nodicarta-context", "SKILL.md");
    File.AppendAllText(contextFile, "\n项目自定义说明");
    var custom = File.ReadAllBytes(contextFile);
    ProjectSkills.Install(root.FullName, all);
    if (!File.ReadAllBytes(contextFile).SequenceEqual(custom) ||
        !File.Exists(Path.Combine(skillRoot, "nodicarta-sync", "scripts", "call-knowledge.cjs")))
        throw new Exception("Existing content or bundled scripts were lost.");
    return;
}

// Optional live check reads a paired device, discovers tools and calls list_projects only.
if (args is [("--probe" or "--probe-auto"), var liveState, var workspace, var projectId])
{
    var client = new EdgeDesktopClient(liveState);
    var setup = new EdgeAccessSetup(client);
    var snapshot = await client.RefreshAsync();
    var liveProject = snapshot.Projects.Single(p => p.ProjectId == Guid.Parse(projectId));
    var selected = snapshot.Bindings.Single(b => b.ProjectId == liveProject.ProjectId &&
        string.Equals(b.LocalPath, Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase));
    var current = await setup.LoadAsync(liveProject, selected);
    Console.WriteLine(current.CodexStatus);
    var automatic = args[0] == "--probe-auto";
    Console.WriteLine(await setup.ProbeAsync(current, automatic ? EdgeAccessProfile.Auto : EdgeAccessProfile.All,
        automatic ? null : current.Endpoints.FirstOrDefault()?.Id));
    return;
}

// Run with dotnet tests/EdgeAccessChecks.cs. Every configuration write targets a new temporary workspace.
var directory = Directory.CreateTempSubdirectory("projecthub-access-📁-");
var testTools = Path.Combine(directory.FullName, "devtools");
Environment.SetEnvironmentVariable("DEVTOOLS_HOME", testTools);
var state = Path.Combine(directory.FullName, "state");
var project = new DeviceProject(Guid.NewGuid(), "中文项目", Guid.NewGuid(), "readWrite");
var binding = new LocalBinding(project.ProjectId, project.BindingId, Guid.NewGuid(), directory.FullName, "readWrite", project.Name);
var endpoint = new EdgeAccessEndpoint(Guid.NewGuid(), "测试端点");
var context = new EdgeAccessContext(project, binding, [endpoint], "");
var exe = Path.Combine(directory.FullName, "Edge 测试", "ProjectHub.Edge.exe");
var codex = Directory.CreateDirectory(Path.Combine(directory.FullName, ".codex"));
var file = Path.Combine(codex.FullName, "config.toml");
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
void Refuses(Action action)
{
    try { action(); }
    catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { return; }
    throw new Exception("Expected refusal");
}
EdgeAccessConfig Select(EdgeAccessContext ctx, EdgeAccessProfile profile, Guid? id = null) =>
    EdgeAccessSetup.CreateConfig(ctx, profile, id, exe, state);
void Save(EdgeAccessConfig config) => CodexAccessFile.Install(binding, state, config);
try
{
    var setupState = Path.Combine(directory.FullName, "接入 配置");
    var accessSetup = new EdgeAccessSetup(new EdgeDesktopClient(setupState));
    var expected = accessSetup.GlobalStdioJson();
    using (var payload = JsonDocument.Parse(expected))
    {
        var servers = payload.RootElement.GetProperty("mcpServers");
        Check(servers.EnumerateObject().Select(entry => entry.Name).SequenceEqual(new[] { "nodicarta" }),
            "New global configuration must contain only the Nodicarta entry");
        var server = servers.GetProperty("nodicarta");
        Check(Path.IsPathFullyQualified(server.GetProperty("command").GetString()!), "Setup command must be absolute");
        Check(server.GetProperty("args").EnumerateArray().Select(arg => arg.GetString())
            .SequenceEqual(new[] { "mcp-stdio-global", "--state", setupState }), "Setup arguments must be device-wide and preserve the state path");
    }
    foreach (var english in new[] { false, true })
    {
        UiLanguage.Save(setupState, english);
        var instructions = accessSetup.GlobalSetupInstructions();
        var body = instructions[..instructions.IndexOf("\n```json\n", StringComparison.Ordinal)];
        Check(body.StartsWith(english ? "Register" : "请在", StringComparison.Ordinal)
            && body.Contains("nodicarta", StringComparison.Ordinal)
            && body.Contains("nodicarta-context", StringComparison.Ordinal)
            && body.Contains("nodicarta-sync", StringComparison.Ordinal)
            && body.Contains(english ? "stand-up summary" : "两句晨会摘要", StringComparison.Ordinal)
            && body.Contains(english ? "read-only, local-only, and do-not-record" : "仅本地、只读或不要记录", StringComparison.Ordinal)
            && !body.Contains("projecthub", StringComparison.OrdinalIgnoreCase)
            && instructions.EndsWith("\n```json\n" + expected + "\n```", StringComparison.Ordinal),
            "The copied instructions must include handoff guidance and the exact MCP configuration in either language");
    }
    UiLanguage.Load(state);

    foreach (var address in new[] { "http://192.0.2.10:29480", "http://core.example.com:29480", "http://[2001:db8::2]:29480", "http://127.0.0.1:29480", "https://core.example" })
        Check(EdgeDesktopClient.ParseCoreAddress(address) == new Uri(address), "HTTP and HTTPS Core origins accepted");
    foreach (var address in new[] { "ftp://core.example.com", "http://user:pass@core.example.com", "http://core.example.com/devices", "http://core.example.com/?key=1", "https://core.example.com/#devices", "192.0.2.10:29480" })
    {
        try { EdgeDesktopClient.ParseCoreAddress(address); }
        catch (ArgumentException) { continue; }
        throw new Exception("Invalid Core origin accepted: " + address);
    }
    var all = Select(context, EdgeAccessProfile.All, endpoint.Id);
    var basic = Select(context, EdgeAccessProfile.Knowledge);
    string[] special = [@"C:\中文📁\O'Brien\Edge.exe", "引号\"反斜杠\\及补充字符🚀", @"\uD83D\uDE00 是文字"];
    var escaped = TomlSerializer.Deserialize<TomlTable>(CodexAccessFile.Format(special[0], special, basic.Tools))!;
    var table = (TomlTable)((TomlTable)escaped["mcp_servers"])["nodicarta"];
    Check((string)table["command"] == special[0] && ((TomlArray)table["args"]).Cast<string>().SequenceEqual(special),
        "TOML round trip for non-BMP characters, apostrophes, quotes and literal backslashes");
    Check(all.Tools.Length == 17 && basic.Tools.Length == 5, "full and legacy tool counts");
    var assets = Select(context, EdgeAccessProfile.Assets);
    Check(assets.Tools.ToHashSet().SetEquals(["list_projects","list_assets","get_asset","get_asset_decisions","publish_asset","download_asset"])
        && assets.Arguments.Contains(binding.WorkspaceId.ToString()), "asset tools use the selected workspace");
    var readOnly = context with { Project = project with { AccessMode = "readOnly" } };
    var auto = Select(context, EdgeAccessProfile.Auto);
    Check(auto.Tools.Length == 17 && !auto.Arguments.Contains("--tools") && !auto.CodexToml.Contains("enabled_tools")
        && !auto.Arguments.Contains("--endpoint") && auto.Arguments.Contains("auto"), "adaptive launch, single endpoint");
    var ambiguous = context with { Endpoints = [endpoint, new EdgeAccessEndpoint(Guid.NewGuid(), "另一端")] };
    Check(Select(ambiguous, EdgeAccessProfile.Auto).Tools.Length == 14, "ambiguous endpoint is never guessed");
    Check(Select(ambiguous, EdgeAccessProfile.Auto, endpoint.Id).Arguments.Contains(endpoint.Id.ToString()), "explicit endpoint is pinned");
    Check(Select(context with { ConfiguredEndpoint = Guid.NewGuid() }, EdgeAccessProfile.Auto).Tools.Length == 14, "expired pin is not redirected");
    Check(Select(readOnly with { AutomaticTools = basic.Tools }, EdgeAccessProfile.Auto).Tools.Length == 4, "existing cap intersects grant");
    Check(Select(context with { AutomaticTools = basic.Tools }, EdgeAccessProfile.FollowGrant).Tools.Length == 17, "explicitly opt into adaptive access");
    var priorAssets = assets.Tools.Where(t => t != "get_asset_decisions").ToArray();
    Check(Select(context with { AutomaticTools = priorAssets }, EdgeAccessProfile.Auto).Tools.SequenceEqual(priorAssets),
        "keep an old explicit asset allowlist exactly");
    Save(auto);
    Check(CodexAccessFile.Read(binding, state) is { ToolLimit: null } current && current.Block?.Length > 0, "adaptive TOML can be reopened");
    File.Delete(file);
    Check(Select(readOnly, EdgeAccessProfile.All, endpoint.Id).Tools.Length == 12, "read-only count");
    Check(Select(readOnly, EdgeAccessProfile.All).Tools.Length == 11, "read-only without endpoint");
    Check(!Select(readOnly, EdgeAccessProfile.Assets).Tools.Contains("publish_asset"), "read-only asset client");
    Check(!Select(context, EdgeAccessProfile.All).Arguments.Contains("--endpoint"), "no invented endpoint");
    Refuses(() => Select(context, EdgeAccessProfile.Tasks));
    Refuses(() => Select(context, EdgeAccessProfile.Tasks, Guid.NewGuid()));
    using (var json = JsonDocument.Parse(all.StdioJson))
    {
        var jsonArgs = json.RootElement.GetProperty("mcpServers").GetProperty("nodicarta").GetProperty("args").EnumerateArray();
        Check(jsonArgs.Select(x => x.GetString()).SequenceEqual(all.Arguments), "JSON argument round trip");
    }
    var preamble = "model = \"user-model\"\r\n# 用户的其他服务\r\n[mcp_servers.documents]\r\ncommand = 'C:\\bin\\documents.exe'\r\n";
    File.WriteAllText(file, preamble, new UTF8Encoding(true));
    var initial = File.ReadAllBytes(file);
    Save(basic);
    Check(File.ReadAllBytes(file).AsSpan().StartsWith(initial), "unrelated TOML and BOM preserved");
    Save(all);
    var saved = File.ReadAllBytes(file);
    Save(all);
    Check(File.ReadAllBytes(file).SequenceEqual(saved) && CodexAccessFile.Status(binding, state).Contains("17 个"), "idempotent update");
    var added = "\r\n[another]\r\nvalue = true\r\n";
    File.AppendAllText(file, added);
    Save(basic);
    Check(File.ReadAllText(file).EndsWith(added), "following user table preserved");
    var custom = File.ReadAllText(file).Replace("command = ", "command  = ");
    File.WriteAllText(file, custom);
    Refuses(() => Save(basic));
    Check(File.ReadAllText(file) == custom, "custom edit preserved");
    foreach (var conflict in new[] { "[mcp_servers.'projecthub']\ncommand='other'", "mcp_servers={projecthub={url='x'}}",
                 "unclosed = [" })
    {
        File.WriteAllText(file, conflict);
        Refuses(() => Save(basic));
        Check(File.ReadAllText(file) == conflict, "conflicting table preserved");
    }
    var legacy = $"""
        [mcp_servers.projecthub]
        command = '{Path.Combine(testTools, "projecthub-edge", "bin", "ProjectHub.Edge.exe")}'
        args = [
          "mcp-stdio",
          "--state", '{state}',
          "--project", "{project.ProjectId}",
          "--binding", "{project.BindingId}",
          "--mode", "readWrite",
        ]
        enabled_tools = [
          "list_projects", "get_project_snapshot", "search_project_knowledge",
          "get_knowledge_history", "submit_knowledge_commit",
        ]
        """;
    File.WriteAllText(file, preamble + legacy + added);
    Check(CodexAccessFile.Status(binding, state).Contains("5 个知识"), "known legacy profile recognized");
    Check(CodexAccessFile.Read(binding,state).ToolLimit?.Length == 5, "preserve legacy tool boundary when choosing automatically");
    Save(all);
    var upgradedLegacy = File.ReadAllText(file);
    Check(upgradedLegacy.StartsWith(preamble) && upgradedLegacy.EndsWith(added)
        && upgradedLegacy.Contains("[mcp_servers.projecthub]") && !upgradedLegacy.Contains("[mcp_servers.nodicarta]"), "surgical legacy upgrade preserves the server name");
    var otherProject = File.ReadAllText(file).Replace(project.ProjectId.ToString(), Guid.NewGuid().ToString());
    File.WriteAllText(file, otherProject);
    Refuses(() => Save(basic));
    Check(File.ReadAllText(file) == otherProject, "another project's entry preserved");
    File.Delete(file);
    Save(basic);
    Check(CodexAccessFile.Status(binding, state).Contains("5 个工具"), "fresh install");
    Console.WriteLine("PASS: profiles, permission and endpoint bounds, STDIO JSON, new/legacy/idempotent Codex installs, foreign configs and user edits.");
}
finally { directory.Delete(recursive: true); }
