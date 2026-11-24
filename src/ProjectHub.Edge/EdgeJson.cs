using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace ProjectHub.Edge;

// The storage context retains the original on-disk casing. HTTP and CLI use web defaults.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(EdgeSession.Tokens))]
[JsonSerializable(typeof(EdgeSession.ProjectList))]
[JsonSerializable(typeof(EdgeSession.ReplacementAttempt))]
[JsonSerializable(typeof(EdgeSession.ReplacementProblem))]
[JsonSerializable(typeof(AntigravityWorkspace.Receipt))]
[JsonSerializable(typeof(EdgeRequestSource))]
[JsonSerializable(typeof(DeviceIdentity))]
[JsonSerializable(typeof(DeviceProject[]))]
[JsonSerializable(typeof(LocalBinding[]))]
[JsonSerializable(typeof(CodexObservation))]
[JsonSerializable(typeof(KnowledgeValidity))]
[JsonSerializable(typeof(KnowledgeAssetRef[]))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonElement[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Uri))]
[JsonSerializable(typeof(object))]
internal partial class EdgeJson : JsonSerializerContext
{
    internal static readonly EdgeJson Web = new(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    internal static readonly EdgeJson Indented = new(new JsonSerializerOptions { WriteIndented = true });

    internal static JsonSerializerOptions CreateToolOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Insert(0, Default);
        options.MakeReadOnly();
        return options;
    }

}
