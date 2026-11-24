using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectHub.Edge.Desktop;

internal sealed record StandardHarness(string Id, string Name, string ConfigPath, string[] ServerPath, bool Detected, string Hint)
{
    internal bool SupportsJsonc => Id == "opencode";
    internal string? BlockedReason { get; init; }
    internal string? ConnectedMessage { get; init; }

    internal JsonObject Entry(string command, string state)
    {
        var args = EdgeAccessSetup.GlobalArguments(state);
        if (Id == "opencode")
            return new JsonObject { ["type"] = "local", ["command"] = JsonSerializer.SerializeToNode(new[] { command }.Concat(args).ToArray(), DesktopJson.Default.StringArray) };

        var entry = new JsonObject { ["command"] = command };
        if (Id is "antigravity" or "claude-desktop") args = [.. args, "--workspace-roots"];
        if (Id is "claude" or "cursor")
        {
            entry["type"] = "stdio";
            var variable = Id == "claude" ? "CLAUDE_PROJECT_DIR" : "PROJECTHUB_WORKSPACE";
            args = [.. args, "--workspace-env", variable];
            if (Id == "cursor") entry["env"] = new JsonObject { [variable] = "${workspaceFolder}" };
        }
        entry["args"] = JsonSerializer.SerializeToNode(args, DesktopJson.Default.StringArray);
        return entry;
    }
}

internal static class StandardHarnesses
{
    private const string ClaudePackageFamily = "Claude_pzs8sxrjxfjjc";

    internal static IReadOnlyList<StandardHarness> Detect(Func<bool>? detectClaudeDesktopPackage = null)
    {
        var home = GlobalCodexAccess.UserHome;
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        bool Installed(string relative) => File.Exists(Path.Combine(local, "Programs", relative)) || File.Exists(Path.Combine(programs, relative));
        var claudeDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var customClaude = !string.IsNullOrWhiteSpace(claudeDirectory) && Path.IsPathFullyQualified(claudeDirectory);
        var legacyClaude = Path.Combine(customClaude ? claudeDirectory! : Path.Combine(home, ".claude"), ".config.json");
        var claude = File.Exists(legacyClaude) ? legacyClaude : Path.Combine(customClaude ? claudeDirectory! : home, ".claude.json");
        var antigravity = Path.Combine(home, ".gemini", "config", "mcp_config.json");
        var cursor = Path.Combine(home, ".cursor", "mcp.json");
        var roaming = Environment.GetEnvironmentVariable("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var desktop = ClaudeDesktop(local, roaming, detectClaudeDesktopPackage?.Invoke() ?? HarnessDetection.PackageRegistered(ClaudePackageFamily),
            File.Exists(Path.Combine(local, "AnthropicClaude", "claude.exe")) || Installed(@"Claude\Claude.exe"));
        List<StandardHarness> clients = [
            new StandardHarness("claude", "Claude Code", claude, ["mcpServers"],
                HarnessDetection.OnPath("claude") || File.Exists(Path.Combine(home, ".local", "bin", "claude.exe")) ||
                File.Exists(Path.Combine(local, "Microsoft", "WinGet", "Links", "claude.exe")),
                "从绑定目录启动 Claude Code，在 /mcp 中查看脉络接入项。") {
                BlockedReason = !customClaude && !string.IsNullOrWhiteSpace(claudeDirectory) ? "CLAUDE_CONFIG_DIR 需要指向绝对路径。" : null
            },
            new StandardHarness("antigravity", "Google Antigravity", antigravity, ["mcpServers"],
                HarnessDetection.OnPath("agy", "antigravity", "antigravity-ide") || Installed(@"Antigravity\Antigravity.exe") ||
                Installed(@"Antigravity IDE\Antigravity IDE.exe") || File.Exists(Path.Combine(local, "agy", "bin", "agy.exe")),
                "已绑定目录自动接入") {
                ConnectedMessage = "Antigravity 已接入。绑定目录后，从该项目新建聊天即可查询资料。解绑后会移除连接和查询指引。"
            },
            new StandardHarness("cursor", "Cursor", cursor, ["mcpServers"],
                HarnessDetection.OnPath("cursor", "cursor-agent") || WindowsDesktopInstallation.CursorDesktop(local, programs) ||
                File.Exists(Path.Combine(local, "cursor-agent", "cursor-agent.cmd")),
                "在 Cursor 打开已绑定的文件夹，在 Settings → Tools & MCP 中确认脉络接入项已启用。"),
            OpenCode(home, WindowsDesktopInstallation.OpenCodeDesktop(local, programs))
        ];
        if (desktop.Detected) clients.Insert(0, desktop);
        return clients;
    }

    internal static StandardHarness ClaudeDesktop(string local, string roaming, bool packaged, bool classicInstalled)
    {
        var directory = Path.Combine(roaming, "Claude");
        string? blocked = null;
        if (packaged)
        {
            // Recent Store releases migrate out of LocalCache and leave the old directory behind.
            var family = ClaudePackageFamily.ToLowerInvariant();
            var current = Path.Combine(local, "Claude-Data", family);
            var legacy = Path.Combine(local, "Packages", ClaudePackageFamily, "LocalCache", "Roaming", "Claude");
            if (Directory.Exists(current)) directory = current;
            else if (Directory.Exists(legacy) && !File.Exists(Path.Combine(legacy, ".user-data-left." + family))) directory = legacy;
            else blocked = "请先打开一次 Claude 桌面版，再返回接入。";
        }
        return new StandardHarness("claude-desktop", "Claude Desktop", Path.Combine(directory, "claude_desktop_config.json"),
            ["mcpServers"], packaged || classicInstalled, "接入后请完全退出并重新打开 Claude。") { BlockedReason = blocked };
    }

    private static StandardHarness OpenCode(string home, bool desktopInstalled)
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var global = Path.Combine(!string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg) ? xdg : Path.Combine(home, ".config"), "opencode");
        var custom = Environment.GetEnvironmentVariable("OPENCODE_CONFIG");
        var customDirectory = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR");
        var explicitDirectory = !string.IsNullOrWhiteSpace(customDirectory) && Path.IsPathFullyQualified(customDirectory);
        var directory = Path.GetFullPath(explicitDirectory ? customDirectory! : global);
        var json = Path.Combine(directory, "opencode.json");
        var jsonc = Path.Combine(directory, "opencode.jsonc");
        var explicitFile = !string.IsNullOrWhiteSpace(custom) && Path.IsPathFullyQualified(custom);
        var path = explicitFile && !explicitDirectory ? Path.GetFullPath(custom!) : File.Exists(jsonc) ? jsonc : json;
        var target = new StandardHarness("opencode", "OpenCode", path, ["mcp"], desktopInstalled ||
            HarnessDetection.OnPath("opencode") || File.Exists(Path.Combine(home, ".opencode", "bin", "opencode.exe")) ||
            File.Exists(Path.Combine(home, ".local", "bin", "opencode.exe")),
            "在已绑定的目录启动 OpenCode，在 MCP 列表中查看脉络接入项。");
        if (!explicitFile && !string.IsNullOrWhiteSpace(custom))
            return target with { BlockedReason = "OPENCODE_CONFIG 需要指向绝对路径。" };
        if (!explicitDirectory && !string.IsNullOrWhiteSpace(customDirectory))
            return target with { BlockedReason = "OPENCODE_CONFIG_DIR 需要指向绝对路径。" };
        try
        {
            static bool HasEdgeEntry(JsonElement servers) => servers.TryGetProperty(EdgeAccessSetup.ServerName, out _) ||
                servers.TryGetProperty(EdgeAccessSetup.LegacyServerName, out _);
            // V2 reads both MCP shapes. Preserve the installed entry's shape and its original file.
            var files = new[] { Path.Combine(global, "config.json"), Path.Combine(global, "opencode.json"), Path.Combine(global, "opencode.jsonc"),
                explicitFile ? custom! : path, json, jsonc }.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
            var claimed = false;
            var outsideProfile = false;
            var inline = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT");
            foreach (var file in files.Where(File.Exists).Append(null))
            {
                if (file == null && string.IsNullOrWhiteSpace(inline)) break;
                if (file != null) CodexAccessFile.CheckPath(file);
                var text = file == null ? inline! : CodexAccessFile.Decode(File.ReadAllBytes(file));
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions {
                    CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, AllowDuplicateProperties = false
                });
                if (!doc.RootElement.TryGetProperty("mcp", out var mcp)) continue;
                var legacy = HasEdgeEntry(mcp);
                var v2 = mcp.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Object;
                // A V1 server can itself be named "servers".
                v2 = v2 && !(servers.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String) &&
                    !(servers.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False);
                var modern = v2 && HasEdgeEntry(servers);
                if (file == null)
                {
                    if (legacy || modern) return target with { BlockedReason = "OPENCODE_CONFIG_CONTENT 已定义脉络接入项，请先核对运行时配置。" };
                    break;
                }
                if ((legacy && modern) || (claimed && (legacy || modern)))
                    return target with { BlockedReason = "多个 OpenCode 配置同时定义脉络接入项，请先核对生效的配置。" };
                var writable = explicitDirectory
                    ? StringComparer.OrdinalIgnoreCase.Equals(file, json) || StringComparer.OrdinalIgnoreCase.Equals(file, jsonc)
                    : !explicitFile || StringComparer.OrdinalIgnoreCase.Equals(file, path);
                if ((legacy || modern) && !writable) outsideProfile = true;
                if (writable && (legacy || modern || (!claimed && StringComparer.OrdinalIgnoreCase.Equals(file, path) && v2)))
                    target = target with { ConfigPath = file, ServerPath = v2 && !legacy ? ["mcp", "servers"] : ["mcp"] };
                claimed |= legacy || modern;
            }
            return outsideProfile ? target with { BlockedReason = "其他 OpenCode 配置位置已定义脉络接入项，请核对当前指定的配置。" } : target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Text.DecoderFallbackException)
        { return target with { BlockedReason = "无法确认现有 OpenCode 配置的格式或文件路径，请先核对。" }; }
    }
}
