using System.Globalization;

namespace ProjectHub.Edge;

internal sealed record McpListenerStatus(int? Port, Guid ProjectId, Guid BindingId, Guid? WorkspaceId, Guid? DeviceId)
{
    internal FileStream Register(string stateDirectory)
    {
        // The kernel holds this lease for the host lifetime and removes it even if the process is killed.
        var name = FormattableString.Invariant($"mcp-live.{Guid.NewGuid():N}.{Port ?? 0}.{ProjectId:N}.{BindingId:N}.{WorkspaceId.GetValueOrDefault():N}.{DeviceId.GetValueOrDefault():N}.lease");
        return new FileStream(Path.Combine(stateDirectory, name), FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    internal static IReadOnlyList<McpListenerStatus> Read(string stateDirectory)
    {
        List<McpListenerStatus> result = [];
        foreach (var file in new DirectoryInfo(stateDirectory).EnumerateFiles("mcp-live.*.lease"))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var parts = file.Name.Split('.');
            if (parts.Length != 8 || !Guid.TryParseExact(parts[1], "N", out _)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 0 or > 65535
                || !Guid.TryParseExact(parts[3], "N", out var project) || !Guid.TryParseExact(parts[4], "N", out var binding)
                || !Guid.TryParseExact(parts[5], "N", out var workspace) || !Guid.TryParseExact(parts[6], "N", out var device))
                continue;
            try
            {
                // An unlocked copy from an offline backup or unclean volume shutdown is not a live host.
                using var unlocked = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (FileNotFoundException) { } // The host exited during enumeration.
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)) // ERROR_SHARING_VIOLATION
            {
                result.Add(new(port == 0 ? null : port, project, binding, workspace == Guid.Empty ? null : workspace,
                    device == Guid.Empty ? null : device));
            }
        }
        return result.OrderBy(s => s.Port).ToArray();
    }
}
