using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectHub.Edge.Desktop;

// Locate the managed value in the original UTF-8 text so even JSONC comments outside it survive an update.
internal static class HarnessJson
{
    private static readonly JsonSerializerOptions Display = new(JsonSerializerOptions.Default)
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Copy(StandardHarness target, string command, string state) =>
        Wrap(target.ServerPath.Append("projecthub"), target.Entry(command, state)).ToJsonString(Display);

    private static JsonNode Wrap(IEnumerable<string> path, JsonNode node)
    {
        foreach (var key in path.Reverse()) node = new JsonObject { [key] = node };
        return node;
    }

    internal static string Inspect(StandardHarness target, string text, string command, string state, out string action,
        HarnessEdit edit = HarnessEdit.Connect, string? retiringCommand = null)
    {
        var desired = target.Entry(command, state);
        var path = target.ServerPath.Append("projecthub").ToArray();
        var options = new JsonDocumentOptions
        {
            AllowDuplicateProperties = false,
            AllowTrailingCommas = target.SupportsJsonc,
            CommentHandling = target.SupportsJsonc ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow
        };
        var input = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        using var doc = JsonDocument.Parse(input, options);
        var readerOptions = new JsonReaderOptions { AllowTrailingCommas = options.AllowTrailingCommas, CommentHandling = options.CommentHandling };
        var reader = new Utf8JsonReader(input, readerOptions);
        reader.Read();
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        for (var depth = 0; depth < path.Length; depth++)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("现有配置的服务器分组格式不正确。");
            var afterBrace = (int)reader.BytesConsumed;
            if (!FindProperty(ref reader, path[depth], out var propertyStart, out var previousEnd))
            {
                action = "接入";
                if (edit != HarnessEdit.Connect) return text;
                var indent = new string(' ', 2 * (depth + 1));
                var entry = Wrap(path.Skip(depth + 1), desired);
                var newProperty = newline + indent + JsonSerializer.Serialize(path[depth]) + ": " +
                    entry.ToJsonString(Display).ReplaceLineEndings(newline + indent) +
                    (previousEnd >= 0 ? "," : newline + new string(' ', 2 * depth));
                return Splice(input, afterBrace, afterBrace, newProperty);
            }
            if (depth < path.Length - 1) continue;
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            var end = (int)reader.BytesConsumed;
            var current = JsonNode.Parse(input.AsSpan(start, end - start), documentOptions: options);
            if (edit == HarnessEdit.MigrateState)
            {
                action = "接入";
                if (current is not JsonObject server) return text;
                var inline = target.Id == "opencode";
                var field = inline ? "command" : "args";
                var index = inline ? 3 : 2;
                if (server[field] is not JsonArray args || args.Count <= index ||
                    args[index] is not JsonValue s || !s.TryGetValue<string>(out var registered) ||
                    registered.Equals(state, StringComparison.OrdinalIgnoreCase) || !EdgeStatePaths.Same(registered, state) ||
                    (inline ? args[0] : server["command"]) is not JsonValue c || !c.TryGetValue<string>(out var exePath) ||
                    !Path.IsPathFullyQualified(exePath) || !Path.GetFileName(exePath).Equals("ProjectHub.Edge.exe", StringComparison.OrdinalIgnoreCase) ||
                    !JsonNode.DeepEquals(current, target.Entry(exePath, registered))) return text;
                var arguments = new Utf8JsonReader(input.AsSpan(start, end - start), readerOptions);
                arguments.Read();
                FindProperty(ref arguments, field, out _, out _);
                for (var i = 0; i <= index; i++) arguments.Read();
                var migrated = Splice(input, start + (int)arguments.TokenStartIndex, start + (int)arguments.BytesConsumed,
                    JsonSerializer.Serialize(state, Display));
                return Inspect(target, migrated, command, state, out action, HarnessEdit.Repair);
            }
            var serverEntry = current as JsonObject;
            var exe = serverEntry?["command"];
            var registeredArgs = (target.Id == "opencode" ? exe : serverEntry?["args"]) as JsonArray;
            var stateIndex = target.Id == "opencode" ? 3 : 2;
            if (exe is JsonArray parts && parts.Count > 0) exe = parts[0];
            if (exe is not JsonValue value || !value.TryGetValue<string>(out var previous) ||
                !Path.IsPathFullyQualified(previous) ||
                !string.Equals(Path.GetFileName(previous), "ProjectHub.Edge.exe", StringComparison.OrdinalIgnoreCase) ||
                registeredArgs == null || registeredArgs.Count <= stateIndex ||
                registeredArgs[stateIndex] is not JsonValue stateValue || !stateValue.TryGetValue<string>(out var registeredState) ||
                !EdgeStatePaths.Same(registeredState, state) || !JsonNode.DeepEquals(current, target.Entry(previous, registeredState)))
                throw new InvalidDataException("同名配置已自定义或连接其他 Edge 数据目录，请手动核对。");
            var owner = edit == HarnessEdit.Remove && string.Equals(retiringCommand, previous, StringComparison.OrdinalIgnoreCase) ? previous : command;
            HarnessRow.CheckManagedCommand(previous, owner, edit);
            action = "已接入";
            if (edit == HarnessEdit.Remove)
            {
                if (target.SupportsJsonc)
                {
                    // A comment inside the managed property is a user edit. Adjacent comments stay in place.
                    var comments = new Utf8JsonReader(input, new JsonReaderOptions { AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Allow });
                    while (comments.Read() && comments.TokenStartIndex < end)
                        if (comments.TokenStartIndex >= propertyStart && comments.TokenType == JsonTokenType.Comment)
                            throw new InvalidDataException("此接入项含自定义注释，请手动核对。");
                }
                // Remove the sibling separator alone, preserving comments and whitespace beside it.
                var comma = SkipTrivia(input, end);
                if (input[comma] != ',')
                    comma = previousEnd >= 0 ? SkipTrivia(input, previousEnd) : -1;
                if (comma >= 0 && input[comma] == ',')
                {
                    if (comma >= end) return Splice(input, propertyStart, comma + 1, Encoding.UTF8.GetString(input, end, comma - end));
                    return Splice(input, comma, end, Encoding.UTF8.GetString(input, comma + 1, propertyStart - comma - 1));
                }
                return Splice(input, propertyStart, end, "");
            }
            if (string.Equals(previous, command, StringComparison.OrdinalIgnoreCase)) return text;
            var commandReader = new Utf8JsonReader(input.AsSpan(start, end - start), readerOptions);
            commandReader.Read();
            if (!FindProperty(ref commandReader, "command", out _, out _)) throw new InvalidDataException("找不到原有启动命令。");
            if (commandReader.TokenType == JsonTokenType.StartArray) commandReader.Read();
            return Splice(input, start + (int)commandReader.TokenStartIndex, start + (int)commandReader.BytesConsumed,
                JsonSerializer.Serialize(command, Display));
        }
        throw new InvalidDataException("缺少服务器配置路径。");
    }

    private static int SkipTrivia(byte[] input, int start)
    {
        for (;;)
        {
            if (input[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') { start++; continue; }
            if (input[start] != '/') return start;
            if (input[start + 1] == '/') { while (input[start] is not ((byte)'\r' or (byte)'\n')) start++; }
            else { start += 2; while (input[start] != '*' || input[start + 1] != '/') start++; start += 2; }
        }
    }

    private static bool FindProperty(ref Utf8JsonReader reader, string key, out int propertyStart, out int previousEnd)
    {
        propertyStart = previousEnd = -1;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            propertyStart = (int)reader.TokenStartIndex;
            var match = reader.ValueTextEquals(key);
            reader.Read();
            if (match) return true;
            reader.Skip();
            previousEnd = (int)reader.BytesConsumed;
        }
        return false;
    }

    private static string Splice(byte[] input, int start, int end, string value) =>
        Encoding.UTF8.GetString(input, 0, start) + value + Encoding.UTF8.GetString(input, end, input.Length - end);
}
