using System.Security.Cryptography;
using System.Text;

namespace ProjectHub.Edge.Desktop;

internal enum SkillInstallState { Missing, AddedInUpgrade, Current, UpdateAvailable, Legacy, Newer, Modified, Linked, Unreadable }

internal sealed record SkillVersionInfo(string Name, string? InstalledVersion, SkillInstallState State)
{
    public bool CanUpdate => State is SkillInstallState.AddedInUpgrade or SkillInstallState.UpdateAvailable or SkillInstallState.Legacy;
}

internal enum SkillRegistration { Inactive, Current, MissingCommand }

internal sealed record SkillInventory(string BundledVersion, bool AutoUpdate, SkillRegistration Registration,
    IReadOnlyList<SkillVersionInfo> Skills, int InstalledCount)
{
    public bool Connected => Registration == SkillRegistration.Current;
    public bool CanRepairRegistration => Registration == SkillRegistration.MissingCommand;
    public bool CanUpdate => (Connected || CanRepairRegistration) && Skills.Any(skill => skill.CanUpdate);
}

internal static class UserSkills
{
    private const string Marker = ".projecthub-managed";
    internal const string Init = "nodicarta-init";
    private const string InitReceiptContent = "projecthub-init 1.1.0";
    internal static Version BundledVersion => Version.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "skills", "VERSION")).Trim());
    // Match exact unmarked directories from older bundles when migrating project-scoped Skills.
    private static readonly Dictionary<string, string[]> Previous = new()
    {
        [Init] = ["35039EE9E0F91BC6B7C8210730F98CBBEA693DE2FD961B5BA8CA0AA0F0AAA933", "A5E4CFA746318C852F77963B43E144BB716B56B0F48D4FE9CC4A03F14F281E27", "13D6E2FB6E4DE2476E54356046E7BB7C0361B834F32CC1E7E57EB40A80D803D5", "A76F3BBBC8FAEDF0B3B68C86052EF594CCBD5F671A77F5D3C211B74F99601E63"],
        ["nodicarta-assets"] = ["D98382322B63E812D3B09D966F994AEE6459E82AD3FFDA5A4A0C5612648EF557", "20AD03D2AE9CF8ABEBB1AA38DCC8FE163CFC7786A1A865C302FBD75392702093", "BD9FE63F0BD59E27603CDE755916DE641C3EF1206BB3B87FB69267DA7AA6CE0F", "5267421FE3CC38B9D0B205D1282E8D61B84BED780F52E9395D7EE80AB0BC7B06", "42BED703F3524F891CBE3C9BADA593AE1F74CED9C7E46E82A10C3E3F78728063", "E51052CA4F011E9B7ED3BA7F84A7219E6AB34F29C82558C82D0674617CE07D8C", "42A92BD191E7EDC7C2E3FF24F5982C3047E4C5C3FE2FBC1EB85F4F3B67412CEB"],
        ["nodicarta-context"] = ["1CAE66464D522C9210EFCD0BBBE5A0A026ACA3D16843BCA84F6307EC697A7BAD", "F0A544A689480257524AE986DDA93CD14E0013E2400807ABBCCF2D207592FAF6", "265D0D449382645536ED3B8B91A4E6B34CECBA6B9B6278E539BC245781DE67CC", "4887527E6FD14FDAB873B0A2C15736517F13CFD3F9728FDACC3C1B30A5D39E39", "4150ACED4B1C7121809813C34B5828CD1090E5750C8C859123AE9CD17F12DF82", "F86C2E6B276A92C395ECC7F71CBB525D684E0D53D083D941EFE88A49D3135695", "E86AC1231FAEC08185932E8D99B7426C7935F22BFB29726E7BCCC5FBDA407333", "26B259EC52A8C30800C28B1C81F7B7514301370B556B433C62FAB35A06877742"],
        ["nodicarta-inbox"] = ["03BF30A02C741BC9AA24829EF79BDC33E8026E7F14D4D021E60727EF20FB2859", "C44E15A533D5AF3776EF8AC8F80139FCB3FE9A144077A5F3527EEF3F58010C95", "7146DE182CD75A40BB07CE8549811E487D3D062EDFF31302F28254967014160B"],
        ["nodicarta-sync"] = ["3BDBE35EFC4F374EA70EE54086B25F29307832F3568DE3397BCDFF16358D628E", "46CCF31D57AA5C4AD95432B95E7373C1E8BEC5AFE441A027E7857516A2307AC7", "8A1E32C9C788592DC52BA7CD1C76E6172E5782A195385B06CD914C2A39C9D9B8", "74CAE99BB4A97F97DC7C2106CC66D4C304645756501F99C3F493E278CE776AD5", "916542549FBD9751363DED4D52C85A033DDBE123E5F8E2C21BC8E0983B337528", "2BD2FE8756F9ED2717CCB7C17AD7460FADC4EE07E6BCAAD86086B2FB8C660E4C", "4FF8F1F97016FE91160ECCE639E7F0D5A7F9614B1533DF379DAD65EEA041FBB7"]
    };

    internal static IReadOnlyList<string> Names { get; } = [.. Previous.Keys];
    private static string Source(string name) => Path.Combine(AppContext.BaseDirectory, "skills", name);
    internal static string Target(string home, string name) => Path.Combine(home, ".agents", "skills", name);
    private static string OldTarget(string home, string name) => Target(home, "projecthub-" + name["nodicarta-".Length..]);
    internal static bool HasDefinition(string home, string name) =>
        File.Exists(Path.Combine(Target(home, name), "SKILL.md")) || File.Exists(Path.Combine(OldTarget(home, name), "SKILL.md"));
    internal static string InitReceipt(string home) => Path.Combine(home, ".agents", ".projecthub-init-installed");

    private static string Fingerprint(string directory, string? copyTo = null)
    {
        var entries = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(directory));
        while (pending.TryPop(out var parent))
        {
            foreach (var item in parent.EnumerateFileSystemInfos())
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new CodexAccessFile.LinkedPathException(item.FullName);
                var relative = Path.GetRelativePath(directory, item.FullName).Replace('\\', '/');
                if (relative == Marker && item is FileInfo) continue;
                var dest = copyTo == null ? null : Path.Combine(copyTo, relative);
                if (item is DirectoryInfo child)
                {
                    entries.Add(relative + "/");
                    if (dest != null) Directory.CreateDirectory(dest);
                    pending.Push(child);
                }
                else
                {
                    using var input = File.OpenRead(item.FullName);
                    entries.Add(relative + ":" + Convert.ToHexString(SHA256.HashData(input)));
                    if (dest != null)
                    {
                        input.Position = 0;
                        using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        input.CopyTo(output);
                    }
                }
            }
        }
        entries.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries))));
    }

    // The old marker held only the digest. Never infer its release number from the current bundle.
    private static SkillVersionInfo Inspect(string directory, string name, string expected, Version bundled)
    {
        CodexAccessFile.CheckPath(directory);
        if (File.Exists(directory)) return new(name, null, SkillInstallState.Modified);
        if (!Directory.Exists(directory)) return new(name, null, SkillInstallState.Missing);
        var marker = Path.Combine(directory, Marker);
        var digest = Fingerprint(directory);
        if (!File.Exists(marker))
            return new(name, null, digest == expected || Previous[name].Contains(digest) ? SkillInstallState.Legacy : SkillInstallState.Modified);
        if (new FileInfo(marker).Length > 128) return new(name, null, SkillInstallState.Modified);
        var recorded = File.ReadAllText(marker);
        if (recorded == digest) return new(name, null, SkillInstallState.Legacy);
        var parts = recorded.Split(' ');
        if (parts.Length != 2 || !Version.TryParse(parts[0], out var version)) return new(name, null, SkillInstallState.Modified);
        var state = parts[1] != digest ? SkillInstallState.Modified : version > bundled ? SkillInstallState.Newer
            : version == bundled && digest == expected ? SkillInstallState.Current : SkillInstallState.UpdateAvailable;
        return new(name, version.ToString(), state);
    }

    internal static SkillVersionInfo InspectUser(string home, string name, Version bundled)
    {
        try
        {
            var expected = Fingerprint(Source(name));
            var current = Inspect(Target(home, name), name, expected, bundled);
            if (current.State is not (SkillInstallState.Current or SkillInstallState.Missing)) return current;
            var legacy = Inspect(OldTarget(home, name), name, expected, bundled);
            if (legacy.CanUpdate || legacy.State == SkillInstallState.Current)
                return new(name, legacy.InstalledVersion, SkillInstallState.UpdateAvailable);
            return current.State == SkillInstallState.Missing ? legacy : current;
        }
        catch (CodexAccessFile.LinkedPathException) { return new(name, null, SkillInstallState.Linked); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(name, null, SkillInstallState.Unreadable); }
    }

    internal static IReadOnlyList<SkillVersionInfo> InspectBundle(string home, Version bundled)
    {
        var skills = Names.Select(name => InspectUser(home, name, bundled)).ToList();
        var init = skills.FindIndex(s => s.Name == Init && s.State == SkillInstallState.Missing);
        if (init < 0 || Path.Exists(InitReceipt(home))) return skills;

        // Older installs have no receipt. A partially upgraded bundle can contain mixed per-Skill versions.
        var newest = skills.Where(s => s.InstalledVersion != null).Select(s => Version.Parse(s.InstalledVersion!)).Max();
        if (newest != null ? newest <= bundled : skills.Any(s => s.State == SkillInstallState.Legacy))
            skills[init] = skills[init] with { State = SkillInstallState.AddedInUpgrade };
        return skills;
    }

    internal static void RememberInit(string home)
    {
        var receipt = InitReceipt(home);
        if (Path.Exists(receipt) || !File.Exists(Path.Combine(Target(home, Init), "SKILL.md"))) return;
        CodexAccessFile.Edit(receipt, before => before.Length == 0 ? InitReceiptContent : before);
    }

    internal static void ClearInitReceipt(string home)
    {
        var receipt = new FileInfo(InitReceipt(home));
        if (!receipt.Exists || receipt.Length != InitReceiptContent.Length) return;
        CodexAccessFile.CheckPath(receipt.FullName);
        if (File.ReadAllText(receipt.FullName) == InitReceiptContent) receipt.Delete();
    }

    internal static bool Install(string home, string name, bool updateOnly = false)
    {
        var source = Source(name);
        if (!File.Exists(Path.Combine(source, "SKILL.md"))) throw new IOException(UiLanguage.Message("发行包缺少 ") + name);
        var target = Target(home, name);
        var bundled = BundledVersion;
        var expected = Fingerprint(source);
        var existing = Inspect(target, name, expected, bundled);
        var old = OldTarget(home, name);
        var legacy = Inspect(old, name, expected, bundled);
        var migrate = legacy.CanUpdate || legacy.State == SkillInstallState.Current;
        if (existing.State == SkillInstallState.Current)
            return migrate ? CleanAtPath(old, home, name, true, preserveNewer: true) : !updateOnly;
        if (existing.State == SkillInstallState.Missing && !migrate && legacy.State != SkillInstallState.Missing) return false;
        if (!existing.CanUpdate && (existing.State != SkillInstallState.Missing || (updateOnly && !migrate))) return false;
        var skills = Directory.CreateDirectory(Path.GetDirectoryName(target)!).FullName;
        var agents = Path.GetDirectoryName(skills)!;
        var incoming = Path.Combine(agents, ".projecthub-new-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(agents, ".projecthub-previous-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(incoming);
        try
        {
            var copied = Fingerprint(source, incoming);
            if (copied != expected) throw new IOException("Skill 发行源已变更，请重试。");
            File.WriteAllText(Path.Combine(incoming, Marker), $"{bundled} {copied}");
            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
                if (!Inspect(backup, name, expected, bundled).CanUpdate) { Directory.Move(backup, target); return false; }
            }
            else if (updateOnly && !migrate) return false;
            Directory.Move(incoming, target);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (migrate && !CleanAtPath(old, home, name, true, preserveNewer: true)) return false;
            return true;
        }
        catch
        {
            if (Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target);
            throw;
        }
        finally { if (Directory.Exists(incoming)) Directory.Delete(incoming, true); }
    }

    internal static bool CleanLocal(string workspace, string name, bool userScope = false)
    {
        var current = CleanAtPath(Target(workspace, name), workspace, name, userScope);
        var previous = CleanAtPath(OldTarget(workspace, name), workspace, name, userScope, preserveNewer: true);
        return current && previous;
    }

    private static bool CleanAtPath(string path, string workspace, string name, bool userScope, bool preserveNewer = false)
    {
        CodexAccessFile.CheckPath(path);
        if (!Directory.Exists(path)) return !File.Exists(path);
        var expected = Fingerprint(Source(name));
        bool Known(string dir)
        {
            if (userScope)
            {
                var state = Inspect(dir, name, expected, BundledVersion).State;
                return state is SkillInstallState.Current or SkillInstallState.Legacy or SkillInstallState.UpdateAvailable ||
                    (!preserveNewer && state == SkillInstallState.Newer);
            }
            var digest = Fingerprint(dir);
            return !File.Exists(Path.Combine(dir, Marker)) && (digest == expected || Previous[name].Contains(digest));
        }
        if (!Known(path)) return false;
        var holding = Path.Combine(workspace, ".agents", ".projecthub-previous-" + Guid.NewGuid().ToString("N"));
        Directory.Move(path, holding);
        try
        {
            if (!Known(holding)) return false;
            Directory.Delete(holding, true);
            return true;
        }
        finally { if (Directory.Exists(holding) && !Directory.Exists(path)) Directory.Move(holding, path); }
    }
}
