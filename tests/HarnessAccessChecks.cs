#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using ProjectHub.Edge.Desktop;

var scratch = Directory.CreateTempSubdirectory("edge-harness-check-");
try
{
    var root = scratch.FullName;
    var cli = Path.Combine(root, "new 新版", "ProjectHub.Edge.exe");
    var old = Path.Combine(root, "old", "ProjectHub.Edge.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
    File.WriteAllBytes(cli, []);
    var state = Path.Combine(root, "设备 state");
    var profile = Path.Combine(root, "profile");
    var launcher = Directory.CreateDirectory(Path.Combine(root, "commands")).FullName;
    Directory.CreateDirectory(profile);
    foreach (var variable in new[] { "USERPROFILE", "HOME", "LOCALAPPDATA", "APPDATA", "XDG_CONFIG_HOME",
                 "CLAUDE_CONFIG_DIR", "CODEX_HOME", "PI_CODING_AGENT_DIR", "DSH_HOME" })
        Environment.SetEnvironmentVariable(variable, Path.Combine(profile, variable));
    Environment.SetEnvironmentVariable("OPENCODE_CONFIG", Path.Combine(profile, "opencode.jsonc"));
    Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", null);
    Environment.SetEnvironmentVariable("OPENCODE_CONFIG_CONTENT", null);
    Environment.SetEnvironmentVariable("PATH", launcher);
    foreach (var name in new[] { "codex", "claude", "cursor", "opencode", "agy", "pi", "dsh" })
        File.WriteAllText(Path.Combine(launcher, name + ".cmd"), "@exit /b 1"); // Detection must never run a launcher.

    var standard = StandardHarnesses.Detect(() => false).Append(SpecialHarnesses.Pi()).ToList();
    Check(standard.Count == 5 && standard.Select(h => h.Id).Distinct().Count() == 5, "五个 JSON 客户端");
    foreach (var target in standard)
    {
        var copied = HarnessJson.Copy(target, cli, state);
        var node = JsonNode.Parse(copied);
        foreach (var key in target.ServerPath) node = node![key];
        Check(JsonNode.DeepEquals(node![EdgeAccessSetup.ServerName], target.Entry(cli, state))
            && node[EdgeAccessSetup.LegacyServerName] == null, target.Name + " 新配置使用脉络服务名");
        var before = """{"other":{"keep":"中文"}}""";
        var setup = HarnessJson.Inspect(target, before, old, state, out var action);
        Check(action == "接入" && setup.Contains(before[1..]), target.Name + " 首次合并保留其他值");
        var upgrade = HarnessJson.Inspect(target, setup, cli, state, out action);
        Check(action == "已接入" && upgrade.Contains(before[1..]), target.Name + " 自动修复已删除的路径");
        Check(HarnessJson.Inspect(target, upgrade, cli, state, out action) == upgrade && action == "已接入", "重复接入");
        Reject(() => HarnessJson.Inspect(target, upgrade, old, state + "-other", out _), "其他设备的数据目录");
        Reject(() => HarnessJson.Inspect(target, "{\"x\":1,\"x\":2}", cli, state, out _), "重复键");
    }
    var agy = standard.Single(h => h.Id == "antigravity");
    Directory.CreateDirectory(Path.GetDirectoryName(agy.ConfigPath)!);
    var client = new EdgeDesktopClient(state);
    var harnesses = new HarnessAccess(client, detectClaudeDesktopPackage: () => false);
    var current = GlobalCodexAccess.CurrentCommand;
    File.WriteAllText(agy.ConfigPath, """{"mcpServers":{"unrelated":{"command":"keep"}}}""");
    harnesses.RestoreAntigravity();
    Check(!client.Session.AntigravityReady(current), "没有原接入记录时不擅自启用");
    var registered = HarnessJson.Inspect(agy, File.ReadAllText(agy.ConfigPath), old, state, out _)
        .Replace("\"nodicarta\":", "\"projecthub\":");
    File.WriteAllText(agy.ConfigPath, registered);
    harnesses.RestoreAntigravity();
    Check(client.Session.AntigravityReady(current), "此前已接入的用户升级后自动启用目录维护");
    var migrated = File.ReadAllText(agy.ConfigPath);
    Check(JsonNode.Parse(migrated)!["mcpServers"]!["projecthub"]!["command"]!.GetValue<string>() == current
        && migrated.Contains("\"unrelated\":{\"command\":\"keep\"}"), "迁移只更新本设备的全局命令");
    harnesses.RestoreAntigravity();
    Check(File.ReadAllText(agy.ConfigPath) == migrated, "重复启动保持全局配置");
    var opencode = standard.Single(h => h.Id == "opencode") with { ServerPath = ["mcp", "servers"] };
    var jsonc = "{\r\n  // existing\r\n  \"mcp\": {\"servers\": { /* retained */ \"custom\": {}, }},\r\n}";
    var patched = HarnessJson.Inspect(opencode, jsonc, cli, state, out _);
    Check(patched.Contains("/* retained */ \"custom\": {},") && patched.Contains("// existing\r\n"), "JSONC 注释、尾逗号与换行");
    var commented = patched.Replace("\"command\": [", "\"command\": [/* executable */");
    Directory.CreateDirectory(Path.GetDirectoryName(old)!);
    File.WriteAllBytes(old, []);
    var moved = HarnessJson.Inspect(opencode, commented, old, state, out var movedAction, HarnessEdit.Repair);
    Check(movedAction == "已接入" && moved != commented && moved.Contains("/* executable */"), "自动迁移保留服务器条目内的注释");
    // Replacing runtime files leaves the registered command at the same path.
    var installRoot = Directory.CreateDirectory(Path.Combine(root, "Nodicarta")).FullName;
    var stable = Path.Combine(installRoot, "ProjectHub.Edge.exe");
    var launcherBytes = "installation launcher"u8.ToArray();
    File.WriteAllBytes(stable, launcherBytes);
    File.WriteAllBytes(Path.Combine(installRoot, ".edge-lease"), []);
    void Release(string version)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(version);
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        var manifest = System.Text.Encoding.UTF8.GetBytes($"{digest}  {ReleasePackage.Desktop}\n{digest}  {ReleasePackage.NativeCli}\n" +
            Convert.ToHexString(SHA256.HashData(launcherBytes)) + $"  {ReleasePackage.Cli}\n");
        File.WriteAllBytes(Path.Combine(installRoot, "SHA256SUMS"), manifest);
        File.WriteAllBytes(Path.Combine(installRoot, ReleasePackage.Desktop), bytes);
        File.WriteAllBytes(Path.Combine(installRoot, ReleasePackage.NativeCli), bytes);
    }
    Release("first");
    Check(EdgeInstallCommand.DesktopEntry(Path.Combine(installRoot, ReleasePackage.Desktop)) == stable, "桌面和 MCP 使用固定入口");
    Release("second");
    Check(EdgeInstallCommand.DesktopEntry(Path.Combine(installRoot, ReleasePackage.Desktop)) == stable, "更换运行文件后入口保持固定");
    foreach (var target in standard)
    {
        var legacy = HarnessJson.Copy(target, stable, state).Replace("\"nodicarta\":", "\"projecthub\":");
        var migratedEntry = HarnessJson.Inspect(target, legacy, stable, state, out var ready, HarnessEdit.Repair);
        var expected = HarnessJson.Copy(target, stable, state).Replace("\"nodicarta\":", "\"projecthub\":");
        Check(ready == "已接入" && JsonNode.DeepEquals(JsonNode.Parse(migratedEntry), JsonNode.Parse(expected)),
            target.Name + " 切换版本后保留固定启动路径");
        Directory.CreateDirectory(Path.GetDirectoryName(target.ConfigPath)!);
        File.WriteAllText(target.ConfigPath, HarnessJson.Copy(target, old, state));
    }
    var oldYaml = SpecialHarnesses.CopyDeepSeek(stable, state);
    Check(SpecialHarnesses.Change(oldYaml, stable, state, out var yamlStatus, HarnessEdit.Repair) ==
        SpecialHarnesses.CopyDeepSeek(stable, state) && yamlStatus == "已接入", "DeepSeek 切换版本后保留固定启动路径");
    var yamlFile = Path.Combine(Environment.GetEnvironmentVariable("DSH_HOME")!, "cordis.patch.yml");
    Directory.CreateDirectory(Path.GetDirectoryName(yamlFile)!);
    File.WriteAllText(yamlFile, SpecialHarnesses.CopyDeepSeek(old, state));
    harnesses = new HarnessAccess(client, () => false, () => false);
    harnesses.MigrateInstalledCommands();
    foreach (var target in standard)
        Check(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(target.ConfigPath)), JsonNode.Parse(HarnessJson.Copy(target, current, state))),
            target.Name + " 仅启动便接管此前的有效路径");
    Check(File.ReadAllText(yamlFile) == SpecialHarnesses.CopyDeepSeek(current, state) && client.Session.AntigravityReady(current),
        "YAML 和目录维护自动采用当前程序");
    var rows = harnesses.Inspect().Where(r => r.Id != "codex").ToArray();
    Check(rows.Length == 6 && rows.All(r => r.IsConnected && r.CanDisconnect && !r.CanBatchInstall), "已接入的行只提供断开");
    const string customAgy = """{"mcpServers":{"projecthub":{"command":"custom"}}}""";
    File.WriteAllText(agy.ConfigPath, customAgy);
    harnesses.MigrateInstalledCommands();
    var conflict = harnesses.Inspect().Single(r => r.Id == agy.Id);
    Check(conflict.ActionText == "待核对" && !conflict.CanBatchInstall && File.ReadAllText(agy.ConfigPath) == customAgy,
        "已有自定义接入必须保留，安装其余客户端照常完成");
    Reject(() => HarnessJson.Inspect(opencode, """{"mcp":{"servers":{"projecthub":null}}}""", cli, state, out _), "同名空值");
    var global = Directory.CreateDirectory(Path.Combine(profile, "XDG_CONFIG_HOME", "opencode")).FullName;
    var customFile = Environment.GetEnvironmentVariable("OPENCODE_CONFIG")!;
    File.WriteAllText(Path.Combine(global, "opencode.json"), """{"mcp":{"projecthub":{"type":"remote","url":"https://example.test"}}}""");
    File.WriteAllText(customFile, """{"mcp":{"servers":{"nodicarta":{}}}}""");
    StandardHarness DetectOpenCode() => StandardHarnesses.Detect(() => false).Single(h => h.Id == "opencode");
    Check(DetectOpenCode().BlockedReason != null, "OpenCode 显式文件与默认文件的同名项");
    File.WriteAllText(customFile, "{}");
    Check(DetectOpenCode().ConfigPath == customFile && DetectOpenCode().BlockedReason != null, "显式文件不改写其他位置的同名项");
    var customDir = Directory.CreateDirectory(Path.Combine(profile, "other-opencode")).FullName;
    Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", customDir);
    File.WriteAllText(Path.Combine(customDir, "opencode.jsonc"), """{"mcp":{"servers":{"projecthub":{}}}}""");
    Check(DetectOpenCode().BlockedReason != null, "OpenCode 自定义目录与默认文件的同名项");
    File.WriteAllText(Path.Combine(customDir, "opencode.jsonc"), "{}");
    Check(DetectOpenCode().ConfigPath == Path.Combine(customDir, "opencode.jsonc") && DetectOpenCode().BlockedReason != null,
        "显式目录不改写默认配置中的同名项");
    foreach (var name in new[] { EdgeAccessSetup.ServerName, EdgeAccessSetup.LegacyServerName })
    {
        Environment.SetEnvironmentVariable("OPENCODE_CONFIG_CONTENT", $"{{\"mcp\":{{\"{name}\":{{\"enabled\":false}}}}}}");
        Check(DetectOpenCode().BlockedReason?.Contains("OPENCODE_CONFIG_CONTENT") == true, "OpenCode 运行时同名项");
    }
    Console.WriteLine("五个 JSON 适配器通过配置复制、合并、升级、重复接入、冲突保护和 OpenCode JSONC 检查。");
}
finally { scratch.Delete(true); }

static void Check(bool pass, string name)
{
    if (!pass) throw new Exception(name);
}

static void Reject(Action action, string name)
{
    try { action(); }
    catch (Exception e) when (e is InvalidDataException or JsonException) { return; }
    throw new Exception("未拒绝 " + name);
}
