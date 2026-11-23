using System.Runtime.InteropServices;

namespace ProjectHub.Edge.Desktop;

internal static class DesktopLocation
{
    internal static void UpdateShortcuts(string? previousExecutable, string newExecutable)
    {
        if (previousExecutable == null || Same(previousExecutable, newExecutable)) return;
        var menus = new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.Startup };
        var links = menus.SelectMany(folder => new[] { "Nodicarta Edge.lnk", "ProjectHub Edge.lnk" }
            .Select(name => Path.Combine(Environment.GetFolderPath(folder), name)))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ProjectHub.Edge.Desktop.exe.lnk"));
        foreach (var link in links.Where(File.Exists))
        {
            try { Retarget(link, previousExecutable, newExecutable); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            { System.Diagnostics.Trace.TraceWarning("Edge shortcut {0}: {1}", link, ex.Message); }
        }
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal static T WithShortcut<T>(string path, Func<dynamic, T> readOrWrite)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            try { return readOrWrite(link); }
            finally { Marshal.FinalReleaseComObject((object)link); }
        }
        finally { Marshal.FinalReleaseComObject((object)shell); }
    }

    internal static void Retarget(string path, string previous, string current)
    {
        CodexAccessFile.CheckPath(path);
        var original = File.ReadAllBytes(path);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            File.WriteAllBytes(temp, original);
            var changed = WithShortcut<bool>(temp, link =>
            {
                if (!Same((string)link.TargetPath, previous)) return false;
                link.TargetPath = current;
                if (Same((string)link.WorkingDirectory, Path.GetDirectoryName(previous)!))
                    link.WorkingDirectory = Path.GetDirectoryName(current);
                string icon = link.IconLocation;
                if (icon.StartsWith(previous + ",", StringComparison.OrdinalIgnoreCase))
                    link.IconLocation = current + icon[previous.Length..];
                link.Save();
                return true;
            });
            if (changed && original.AsSpan().SequenceEqual(File.ReadAllBytes(path))) File.Replace(temp, path, null);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
