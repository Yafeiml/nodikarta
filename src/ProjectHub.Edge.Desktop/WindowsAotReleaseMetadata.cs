using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ProjectHub.Edge.Desktop;

internal static class WindowsAotReleaseMetadata
{
    // The packager reads the same VERSIONINFO field without executing the candidate binary.
    private static readonly string[]? Native = OperatingSystem.IsWindows() && !RuntimeFeature.IsDynamicCodeSupported
        ? FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).Comments?.Split('|') ?? []
        : null;

    internal static string? Get(string name)
    {
        if (Native is null)
            return typeof(WindowsAotReleaseMetadata).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(a => a.Key == name)?.Value;
        if (Native.Length != 4 || Native[0] != "Nodicarta/public-v1") return null;
        return name switch
        {
            "NodicartaReleaseLine" => "public-v1",
            "NodicartaUpdateFeed" => Native[1],
            "NodicartaUpdatePublicKey" => Native[2],
            "NodicartaUpdateTestChannel" => Native[3],
            _ => null
        };
    }

    internal static bool IsFixture => Get("NodicartaUpdateTestChannel") == "true";
}
