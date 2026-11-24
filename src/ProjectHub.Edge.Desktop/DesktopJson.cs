using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ProjectHub.Edge.Desktop;

[JsonSerializable(typeof(DesktopRunRecord))]
[JsonSerializable(typeof(ReleaseInstallation.Transaction), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(char))]
internal partial class DesktopJson : JsonSerializerContext
{
    internal static readonly DesktopJson Display = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    });
}
