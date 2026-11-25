using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjectHub.Edge;

internal static class AntigravityWorkspace
{

    // File order also controls installation: publish the plugin manifest after its server and rule are ready.
    private sealed class Paths(LocalBinding workspace, string state, bool legacy = false)
    {
        private readonly string agents = Path.Combine(workspace.LocalPath, ".agents");
        internal string Plugin { get; } = legacy ? "projecthub" : "nodicarta";
        internal string[] Folders => [agents, Path.Combine(agents, "rules"), Path.Combine(agents, "plugins"), Path.Combine(agents, "plugins", Plugin)];
        internal string[] Files => [Path.Combine(agents, "plugins", Plugin, "mcp_config.json"),
            Path.Combine(agents, "rules", Plugin + "-workspace.md"), Path.Combine(agents, "plugins", Plugin, "plugin.json")];
        internal string Receipt { get; } = Path.Combine(state, "antigravity-project-plugins", workspace.WorkspaceId.ToString("D")
            + (legacy ? ".json" : ".nodicarta.json"));
    }

    internal sealed record Receipt(bool[] CreatedFolders, string[] Hashes, string[]? Previous = null)
    {
        internal bool Owns(int index, string hash) => Hashes[index] == hash || Previous?[index] == hash;
    }

    private static Receipt Decode(byte[] bytes) =>
        JsonSerializer.Deserialize(bytes, EdgeJson.Default.Receipt) is { CreatedFolders.Length: 4, Hashes.Length: 3, Previous: null or { Length: 3 } } receipt
            ? receipt : throw new InvalidDataException("Antigravity 目录接入记录无效。");

    private static Receipt? LegacyReceipt(byte[]? bytes)
    {
        if (bytes == null) return null;
        try { return Decode(bytes); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return null; }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static bool HasPriorFiles(LocalBinding workspace, string stateDirectory)
    {
        return new[] { new Paths(workspace, stateDirectory), new Paths(workspace, stateDirectory, legacy: true) }
            .Any(paths => !File.Exists(paths.Receipt) && paths.Files.Any(File.Exists));
    }

    internal static void Apply(LocalBinding workspace, string command, string stateDirectory)
    {
        if (!Directory.Exists(workspace.LocalPath)) throw new DirectoryNotFoundException("Antigravity 工作目录已移走。");
        var paths = new Paths(workspace, stateDirectory);
        var files = paths.Files;
        var dirs = paths.Folders;
        var stored = Read(paths.Receipt);
        var receipt = stored == null ? null : Decode(stored);
        var legacy = new Paths(workspace, stateDirectory, legacy: true);
        var legacyBytes = Read(legacy.Receipt);
        var legacyReceipt = LegacyReceipt(legacyBytes);
        var original = files.Select(Read).ToArray();
        if (receipt == null && Directory.Exists(dirs[^1]) && Directory.EnumerateFileSystemEntries(dirs[^1]).Any())
            throw new InvalidDataException("已保留同名自定义 Antigravity 插件：" + dirs[^1]);
        // Desktop namespaces servers by the plugin folder and caches each resulting name across projects.
        var serverKey = $"kb_{workspace.WorkspaceId:N}";
        var serverName = $"{paths.Plugin}_{serverKey}";
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new JsonObject {
            ["name"] = paths.Plugin, ["displayName"] = "脉络 · Nodicarta", ["version"] = "1.0.0",
            ["description"] = "Use the Nodicarta tools authorized for this workspace."
        }, EdgeJson.Indented.JsonObject);
        var rule = $"""
            ---
            trigger: always_on
            ---
            The active Nodicarta MCP server for this workspace is `{serverName}`. This name supersedes older workspace server names in retrieval instructions. For questions about earlier project work, design choices, or shared assets, use its `search_project_knowledge` tool when available and follow the evidence for the relevant decision.

            {McpHost.ProjectMemoryInstructions}
            """;
        var config = JsonSerializer.SerializeToUtf8Bytes(new JsonObject {
            ["mcpServers"] = new JsonObject { [serverKey] = new JsonObject {
                ["command"] = command,
                ["args"] = new JsonArray("mcp-stdio-global", "--state", stateDirectory, "--bound-workspace", workspace.WorkspaceId.ToString("D")),
                ["cwd"] = "${PLUGIN_ROOT}/../../.."
            } }
        }, EdgeJson.Indented.JsonObject);
        byte[][] desired = [config, Encoding.UTF8.GetBytes(rule.ReplaceLineEndings("\n") + "\n"), manifest];
        var hashes = desired.Select(Digest).ToArray();
        var previous = new string[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            previous[i] = original[i] is { } data ? Digest(data) : hashes[i];
            if (original[i] != null && (receipt == null || !receipt.Owns(i, previous[i])))
                throw new InvalidDataException("已保留自定义 Antigravity 文件：" + files[i]);
        }
        var created = receipt?.CreatedFolders ?? dirs.Select(dir => !Directory.Exists(dir)).ToArray();
        if (legacyReceipt != null)
            for (int i = 0; i < dirs.Length - 1; i++) created[i] |= legacyReceipt.CreatedFolders[i];
        var final = new Receipt(created, hashes);
        // Store both known digests before replacing anything: an interrupted update can be retried or unbound.
        var pending = previous.SequenceEqual(hashes) ? final : final with { Previous = previous };
        var pendingBytes = JsonSerializer.SerializeToUtf8Bytes(pending, EdgeJson.Default.Receipt);
        Replace(paths.Receipt, stored, pendingBytes);
        for (int i = 0; i < files.Length; i++) Replace(files[i], original[i], desired[i]);
        if (pending.Previous != null) Replace(paths.Receipt, pendingBytes, JsonSerializer.SerializeToUtf8Bytes(final, EdgeJson.Default.Receipt));
        // Keep the old working entry until the new plugin is complete. Both receipts are independently retryable.
        if (legacyReceipt != null) RemoveFiles(legacy, legacyBytes!, legacyReceipt, retiringLegacy: true);
    }

    internal static void Remove(LocalBinding workspace, string stateDirectory)
    {
        foreach (bool legacy in new[] { true, false })
        {
            var paths = new Paths(workspace, stateDirectory, legacy);
            var stored = Read(paths.Receipt);
            if (stored == null) continue;
            var receipt = legacy ? LegacyReceipt(stored) : Decode(stored);
            if (receipt == null) continue;
            if (WorkspaceDirectoryPresent(workspace.LocalPath))
                RemoveFiles(paths, stored, receipt, retiringLegacy: legacy);
            else Replace(paths.Receipt, stored, null);
        }
    }

    private static void RemoveFiles(Paths paths, byte[] stored, Receipt receipt, bool retiringLegacy)
    {
        var files = paths.Files;
        Exception? problem = null;
        // A customized legacy plugin can still need its companion manifest/config. Leave both in place.
        bool keepLegacyPlugin = retiringLegacy && new[] { 0, 2 }.Any(i => Read(files[i]) is { } bytes && !receipt.Owns(i, Digest(bytes)));
        // Take the manifest out of discovery first; retire the remaining owned files even when a user edited one.
        for (int i = files.Length - 1; i >= 0; i--)
        {
            try
            {
                if (keepLegacyPlugin && i != 1) continue;
                var bytes = Read(files[i]);
                if (bytes == null) continue;
                if (!receipt.Owns(i, Digest(bytes)))
                {
                    if (retiringLegacy) continue;
                    throw new InvalidDataException("已保留已修改的文件：" + files[i]);
                }
                Replace(files[i], bytes, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { problem ??= ex; }
        }
        var dirs = paths.Folders;
        for (int i = dirs.Length - 1; i >= 0; i--)
            if (receipt.CreatedFolders[i]) RemoveEmpty(dirs[i]);
        if (problem != null) throw new IOException("未完全清理 Antigravity 目录接入。" + problem.Message, problem);
        Replace(paths.Receipt, stored, null);
    }

    private static bool WorkspaceDirectoryPresent(string path)
    {
        CheckPath(path);
        var directory = new DirectoryInfo(path);
        var current = directory;
        while (true)
        {
            try
            {
                var attributes = File.GetAttributes(current.FullName);
                return current == directory && attributes.HasFlag(FileAttributes.Directory);
            }
            catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // A missing child on a reachable volume is gone; a missing volume may return later.
                current = current.Parent ?? throw new IOException("Antigravity 工作目录所在磁盘或共享位置暂不可访问，恢复后可清理接入文件。", ex);
            }
        }
    }

    private static void CheckPath(string path)
    {
        for (FileSystemInfo? item = new FileInfo(path); item != null; item = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent)
            if (item.LinkTarget != null || (item.Attributes != (FileAttributes)(-1) && item.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw new IOException("Antigravity 配置路径经过链接：" + item.FullName);
    }

    private static byte[]? Read(string file)
    {
        CheckPath(file);
        try { return File.ReadAllBytes(file); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void Replace(string file, byte[]? before, byte[]? after)
    {
        if (before == null && after == null || before != null && after != null && before.AsSpan().SequenceEqual(after)) return;
        CheckPath(file);
        using var source = before == null ? null : new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        void CheckOriginal()
        {
            CheckPath(file);
            if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(before))
                throw new IOException("Antigravity 配置刚被其他程序更改，请重试。");
        }
        if (after == null) { CheckOriginal(); File.Delete(file); return; }
        var dir = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".nodicarta-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(after);
                output.Flush(flushToDisk: true);
            }
            if (source == null) File.Move(temp, file);
            else { CheckOriginal(); File.Replace(temp, file, null); }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void RemoveEmpty(string directory)
    {
        CheckPath(directory);
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}
