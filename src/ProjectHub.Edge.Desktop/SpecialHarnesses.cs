using Microsoft.Win32;
using System.Text.Json;
using YamlException = YamlDotNet.Core.YamlException;
using YamlDotNet.RepresentationModel;

namespace ProjectHub.Edge.Desktop;

internal static class SpecialHarnesses
{
    private const string DeepSeekHint = "从绑定目录启动独立的 dsh Host。桌面及 Web 的多个聊天共享 Host 工作目录，切换聊天不会切换脉络项目。";
    private const string EntryId = "projecthub-edge";
    private const string Plugin = "@deepseek-ai/dsh-mcp-client";
    private const string Conflict = "已有同名 DeepSeek MCP 或自定义条目，请手动核对。";

    internal static StandardHarness Pi()
    {
        const string variable = "PI_CODING_AGENT_DIR";
        var directory = ConfigDirectory(variable, Path.Combine(".pi", "agent"));
        var launcher = HarnessDetection.FindOnPath("pi");
        return new("pi", "Pi", Path.Combine(directory, "mcp.json"), ["mcpServers"], launcher != null,
            "在绑定目录启动 Pi 0.99 或更高版本，通过 /mcp 检查 projecthub。")
            { BlockedReason = !Path.IsPathFullyQualified(directory) ? variable + " 需要使用绝对路径。" :
                launcher != null && LegacyPi(launcher) ? "当前 Pi 版本早于 0.99，请升级后使用内置 MCP。" : null };
    }

    private static bool LegacyPi(string launcher)
    {
        var dir = Path.GetDirectoryName(launcher)!;
        // Both the official standalone ZIP and the npm distributions include their package manifest.
        foreach (var name in new[] { "package.json", "node_modules/@earendil-works/pi-coding-agent/package.json",
                     "node_modules/@mariozechner/pi-coding-agent/package.json" })
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            try
            {
                using var manifest = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = manifest.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("name", out var package) &&
                    package.ValueKind == JsonValueKind.String && package.GetString() is "@earendil-works/pi-coding-agent" or "@mariozechner/pi-coding-agent" &&
                    root.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String &&
                    Version.TryParse(value.GetString()!.Split('-', 2)[0], out var version)) return version < new Version(0, 99);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return false;
    }

    private static string ConfigDirectory(string variable, string fallback)
    {
        var home = GlobalCodexAccess.UserHome;
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return Path.Combine(home, fallback);
        if (value == "~") return home;
        if (value.StartsWith("~/") || value.StartsWith(@"~\")) return Path.Combine(home, value[2..]);
        return value;
    }

    internal static bool DeepSeekInstalled()
    {
        if (HarnessDetection.OnPath("dsh")) return true;
        const string exe = "DeepSeek Harness.exe";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (File.Exists(Path.Combine(local, "Programs", "DeepSeek Harness", exe))) return true;
        // The per-user installer permits choosing another installation directory.
        try
        {
            using var apps = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (apps != null)
                foreach (var key in apps.GetSubKeyNames())
                {
                    using var app = apps.OpenSubKey(key);
                    if (app?.GetValue("DisplayName") as string == "DeepSeek Harness" &&
                        app.GetValue("InstallLocation") is string path && Path.IsPathFullyQualified(path) &&
                        File.Exists(Path.Combine(path, exe))) return true;
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return false;
    }

    internal static HarnessRow InspectDeepSeek(string command, string state)
    {
        var installed = DeepSeekInstalled();
        var action = "接入";
        GlobalCodexAccess.WithLock("ProjectHub.DeepSeekHarness", () => CodexAccessFile.Edit(DeepSeekFile(),
            text => Change(text, command, state, out action, HarnessEdit.Repair)));
        var hint = installed ? DeepSeekHint : "";
        return new("deepseek", "DeepSeek Harness", installed, action, hint, installed && action != "已接入")
            { CanDisconnect = action == "已接入" };
    }

    internal static string InstallDeepSeek(string command, string state, HarnessEdit edit = HarnessEdit.Connect)
    {
        CodexAccessFile.Edit(DeepSeekFile(), text => Change(text, command, state, out _, edit));
        return "DeepSeek Harness 配置已就绪。" + DeepSeekHint;
    }

    internal static bool DisconnectDeepSeek(string command, string state) => CodexAccessFile.Edit(DeepSeekFile(),
        text => Change(text, command, state, out _, HarnessEdit.Remove));

    internal static bool RepairDeepSeek(string command, string state, HarnessEdit edit) => CodexAccessFile.Edit(DeepSeekFile(),
        text => Change(text, command, state, out _, edit));

    private static string DeepSeekFile()
    {
        var directory = ConfigDirectory("DSH_HOME", ".dsh");
        if (!Path.IsPathFullyQualified(directory)) throw new InvalidDataException("DSH_HOME 需要使用绝对路径。");
        var file = Path.Combine(directory, "cordis.patch.yml");
        CodexAccessFile.CheckPath(file);
        var profiles = Path.Combine(directory, "profiles");
        CodexAccessFile.CheckPath(profiles);
        if (Directory.Exists(profiles))
            foreach (var profile in Directory.GetDirectories(profiles))
            {
                var patch = Path.Combine(profile, "cordis.patch.yml");
                CodexAccessFile.CheckPath(patch);
                if (File.Exists(patch) && Parse(CodexAccessFile.Decode(File.ReadAllBytes(patch))) is { } items)
                    CheckConflicts(items, null);
            }
        return file;
    }

    private static string Quote(string value)
    {
        if (value.Any(char.IsControl)) throw new InvalidDataException("路径中不能包含控制字符。");
        return "'" + value.Replace("'", "''") + "'";
    }

    internal static string CopyDeepSeek(string command, string state) => $"""
        - insert:
            - id: {EntryId}
              name: '{Plugin}'
              config:
                serverName: projecthub
                transport: stdio
                command: {Quote(command)}
                args: [{string.Join(", ", EdgeAccessSetup.GlobalArguments(state).Select(Quote))}]
        """;

    private static YamlSequenceNode? Parse(string text)
    {
        try
        {
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            if (yaml.Documents.Count == 0) return null;
            if (yaml.Documents.Count == 1 && yaml.Documents[0].RootNode is YamlSequenceNode root) return root;
            throw new InvalidDataException("DeepSeek 补丁应只含一个 YAML 列表。");
        }
        catch (YamlException) { throw new InvalidDataException("DeepSeek 补丁 YAML 格式有误，请先核对原文件。"); }
    }

    private static YamlNode? Field(YamlNode? node, string key) =>
        node is YamlMappingNode map && map.Children.TryGetValue(key, out var child) ? child : null;

    private static string? Value(YamlNode? node) => (node as YamlScalarNode)?.Value;

    private static void CheckConflicts(YamlSequenceNode root, YamlMappingNode? managed)
    {
        var pending = new Stack<YamlNode>();
        var visited = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (ReferenceEquals(node, managed) || !visited.Add(node)) continue;
            if (node is YamlMappingNode map)
            {
                if (Value(Field(map, "id")) == EntryId || Value(Field(map, "serverName")) == "projecthub" ||
                    Field(Field(map, "servers"), "projecthub") != null) throw new InvalidDataException(Conflict);
                foreach (var child in map.Children.Values) pending.Push(child);
            }
            else if (node is YamlSequenceNode list)
                foreach (var child in list.Children) pending.Push(child);
        }
    }

    private static string FlowPatch(string command, string state)
    {
        var args = string.Join(", ", EdgeAccessSetup.GlobalArguments(state).Select(Quote));
        return $"{{insert: [{{id: {EntryId}, name: '{Plugin}', config: {{serverName: projecthub, transport: stdio, command: {Quote(command)}, args: [{args}]}}}}]}}";
    }

    internal static string Change(string text, string command, string state, out string action, HarnessEdit edit = HarnessEdit.Connect)
    {
        action = "接入";
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var block = CopyDeepSeek(command, state).ReplaceLineEndings(newline);
        var root = Parse(text);
        if (root == null)
        {
            return edit != HarnessEdit.Connect ? text : text + (text.Length > 0 && !text.EndsWith('\n') ? newline : "") + block + newline;
        }

        var named = root.Children.OfType<YamlMappingNode>().Where(row => row.Children.Count == 1 &&
                row.Anchor.IsEmpty && row.Tag.IsEmpty && Field(row, "insert") is YamlSequenceNode list && list.Anchor.IsEmpty && list.Tag.IsEmpty)
            .SelectMany(row => ((YamlSequenceNode)Field(row, "insert")!).Children)
            .OfType<YamlMappingNode>().Where(row => Value(Field(row, "id")) == EntryId).ToArray();
        if (named.Length > 1) throw new InvalidDataException(Conflict);
        var owned = named.SingleOrDefault();
        CheckConflicts(root, owned);
        if (owned != null)
        {
            var config = Field(owned, "config") as YamlMappingNode;
            var executable = Field(config, "command") as YamlScalarNode;
            if (owned.Children.Count != 3 || !owned.Anchor.IsEmpty || !owned.Tag.IsEmpty ||
                Value(Field(owned, "name")) != Plugin || config?.Children.Count != 4 || !config.Anchor.IsEmpty || !config.Tag.IsEmpty ||
                owned.Children.Any(p => !p.Key.Tag.IsEmpty || !p.Value.Tag.IsEmpty) ||
                config.Children.Any(p => !p.Key.Tag.IsEmpty || !p.Value.Tag.IsEmpty) ||
                Value(Field(config, "serverName")) != "projecthub" || Value(Field(config, "transport")) != "stdio" ||
                executable?.Value is not { } prior || !executable.Anchor.IsEmpty || !Path.IsPathFullyQualified(prior) ||
                !Path.GetFileName(prior).Equals("ProjectHub.Edge.exe", StringComparison.OrdinalIgnoreCase) ||
                Field(config, "args") is not YamlSequenceNode args || !args.Anchor.IsEmpty ||
                args.Children.Any(n => !n.Tag.IsEmpty || !n.Anchor.IsEmpty) || args.Children.Count != 3 ||
                Value(args.Children[2]) is not { } registered ||
                !args.Children.Select(Value).SequenceEqual(EdgeAccessSetup.GlobalArguments(registered))) throw new InvalidDataException(Conflict);
            if (edit == HarnessEdit.MigrateState)
            {
                if (registered.Equals(state, StringComparison.OrdinalIgnoreCase) || !EdgeStatePaths.Same(registered, state)) return text;
                var token = args.Children[2];
                var first = checked((int)token.Start.Index);
                var last = checked((int)token.End.Index);
                if (text[first..last] != Quote(registered)) throw new InvalidDataException(Conflict);
                var migrated = text[..first] + Quote(state) + text[last..];
                return Change(migrated, command, state, out action, HarnessEdit.Repair);
            }
            if (!EdgeStatePaths.Same(registered, state)) throw new InvalidDataException(Conflict);
            HarnessRow.CheckManagedCommand(prior, command, edit);
            action = "已接入";
            if (edit == HarnessEdit.Remove)
            {
                var wrapper = root.Children.OfType<YamlMappingNode>().Single(row =>
                    Field(row, "insert") is YamlSequenceNode list && list.Children.Any(x => ReferenceEquals(x, owned)));
                if (((YamlSequenceNode)Field(wrapper, "insert")!).Children.Count != 1) throw new InvalidDataException(Conflict);
                var offset = checked((int)wrapper.Start.Index);
                if (root.Style == YamlDotNet.Core.Events.SequenceStyle.Flow)
                {
                    var row = FlowPatch(prior, registered);
                    if (!text.AsSpan(offset).StartsWith(row)) throw new InvalidDataException(Conflict);
                    var endOfRow = offset + row.Length;
                    if (text.AsSpan(endOfRow).StartsWith(", ")) endOfRow += 2;
                    else if (endOfRow < text.Length && text[endOfRow] == ',') endOfRow++;
                    else if (root.Children.Count > 1) throw new InvalidDataException(Conflict);
                    return text.Remove(offset, endOfRow - offset);
                }
                var startOfLine = text.LastIndexOf('\n', offset) + 1;
                var indent = new string(' ', checked((int)root.Start.Column - 1));
                var original = indent + CopyDeepSeek(prior, registered).ReplaceLineEndings(newline + indent);
                if (!text.AsSpan(startOfLine).StartsWith(original)) throw new InvalidDataException(Conflict);
                var endOfBlock = startOfLine + original.Length;
                if (endOfBlock < text.Length && !text.AsSpan(endOfBlock).StartsWith(newline)) throw new InvalidDataException(Conflict);
                if (endOfBlock < text.Length) endOfBlock += newline.Length;
                return text.Remove(startOfLine, endOfBlock - startOfLine);
            }
            if (prior.Equals(command, StringComparison.OrdinalIgnoreCase)) return text;
            var start = checked((int)executable.Start.Index);
            var end = checked((int)executable.End.Index);
            if (text[start..end] != Quote(prior))
                throw new InvalidDataException(Conflict);
            return text[..start] + Quote(command) + text[end..];
        }

        if (edit != HarnessEdit.Connect) return text;
        var listOffset = checked((int)root.Start.Index);
        if (root.Style == YamlDotNet.Core.Events.SequenceStyle.Flow && text[listOffset] == '[')
        {
            return text.Insert(listOffset + 1, FlowPatch(command, state) + (root.Children.Count > 0 ? ", " : ""));
        }
        if (text[listOffset] != '-') throw new InvalidDataException("补丁列表有自定义标签或锚点，请手动合并。");
        var listIndent = new string(' ', checked((int)root.Start.Column - 1));
        return text.Insert(listOffset, block.Replace(newline, newline + listIndent) + newline + listIndent);
    }
}
