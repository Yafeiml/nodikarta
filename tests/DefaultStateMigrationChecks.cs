#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge/ProjectHub.Edge.csproj
using System.Security.AccessControl;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProjectHub.Edge;

static void Check(bool value, string name) { if (!value) throw new Exception(name); }
static void Reject(Action action, string name)
{
    try { action(); }
    catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) { return; }
    throw new Exception(name);
}
static string Acl(string path) => new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
static (string old, string canonical) Paths(string known) => (Path.Combine(known, "ProjectHub", "Edge"), Path.Combine(known, "Nodicarta", "Edge"));
var temp = Directory.CreateTempSubdirectory("nodicarta-state-migration-");
try
{
    var local = Path.Combine(temp.FullName, "Local 带空格&符号");
    var machine = Path.Combine(temp.FullName, "Program Data");
    var (old, canonical) = Paths(local);
    var (oldMachine, canonicalMachine) = Paths(machine);
    var oldSession = EdgeSession.Open(old); // Fixtures are explicit custom paths until the known folders are injected below.
    using (var db = oldSession.Connect())
    using (var sql = db.CreateCommand())
    {
        sql.CommandText = "INSERT INTO device_label VALUES(1,'test','preserved')";
        sql.ExecuteNonQuery();
    }
    var profile = ".edge-core-" + Guid.NewGuid().ToString("N");
    var oldProfile = Path.Combine(old, profile);
    EdgeSession.EnsurePrivateDirectory(oldProfile);
    var bytes = new byte[] { 11, 9, 0, 5 };
    File.WriteAllBytes(Path.Combine(oldProfile, "profile-receipt"), bytes);
    File.WriteAllText(Path.Combine(old, "ui-language.txt"), "en");
    var workspace = Directory.CreateDirectory(Path.Combine(temp.FullName, "工作目录")).FullName;
    var binding = new LocalBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), workspace, "readWrite", "migration");
    var command = Path.Combine(temp.FullName, "ProjectHub.Edge.exe");
    var plugin = Path.Combine(workspace, ".agents", "plugins", "nodicarta", "mcp_config.json");
    var rule = Path.Combine(workspace, ".agents", "rules", "nodicarta-workspace.md");
    string PluginState() => JsonNode.Parse(File.ReadAllText(plugin))!["mcpServers"]!.AsObject().Single().Value!["args"]![2]!.GetValue<string>();
    AntigravityWorkspace.Apply(binding, command, old);
    Check(PluginState() == old, "old directory plugin uses the old state");
    _ = EdgeSession.Open(oldMachine);
    var peer = Directory.CreateDirectory(Path.Combine(local, "ProjectHub", "Dispatch")).FullName;
    File.WriteAllText(Path.Combine(peer, "keep.txt"), "still here");
    var permissions = Acl(old);
    using var scope = EdgeStatePaths.UseKnownFoldersForTest(local, machine);
    Check(EdgeStatePaths.Normalize(oldProfile) == Path.Combine(canonical, profile), "pinned direct profile maps");
    var nested = Path.Combine(oldProfile, "selected-by-user");
    Check(EdgeStatePaths.Resolve(nested) == nested && EdgeStatePaths.Same(oldProfile, Path.Combine(canonical, profile)) == false,
        "custom descendants are left alone and as-yet-physical roots do not count as migrated");
    using (var lease = File.Open(Path.Combine(oldProfile, "mcp-live.fixture.lease"), FileMode.Create, FileAccess.Write, FileShare.None))
        Reject(() => EdgeStatePaths.Resolve(canonical), "a live old MCP must stop the physical migration");
    using (var db = oldSession.Connect())
        Reject(() => EdgeStatePaths.Resolve(old), "an open legacy SQLite database must stop migration");
    Check(!Directory.Exists(canonical) && new DirectoryInfo(old).LinkTarget == null, "busy old root stays at original path");
    var tasks = Enumerable.Range(0, 24).Select(i => Task.Run(() => EdgeStatePaths.Resolve(i % 2 == 0 ? old : canonical)));
    var all = await Task.WhenAll(tasks);
    Check(all.All(path => path == canonical), "concurrent startup through both spellings returns one canonical root");
    var moved = EdgeSession.Open(old);
    using (var db = moved.Connect())
    using (var sql = db.CreateCommand())
    {
        sql.CommandText = "SELECT name FROM device_label";
        Check((string?)sql.ExecuteScalar() == "preserved", "SQLite state survived");
    }
    Check(moved.RootDirectory == canonical && new DirectoryInfo(canonical).LinkTarget == null &&
        new DirectoryInfo(old).ResolveLinkTarget(false)?.FullName == canonical && Acl(canonical) == permissions, "real new root, compatible old leaf and original ACL");
    Check(File.ReadAllText(Path.Combine(canonical, "ui-language.txt")) == "en" &&
        File.ReadAllBytes(Path.Combine(canonical, profile, "profile-receipt")).SequenceEqual(bytes) &&
        File.ReadAllText(Path.Combine(peer, "keep.txt")) == "still here" && new DirectoryInfo(Path.GetDirectoryName(old)!).LinkTarget == null,
        "preferences, profile bytes and untouched sibling");
    Check(EdgeStatePaths.Same(oldProfile, Path.Combine(canonical, profile)) &&
        !EdgeStatePaths.Same(oldProfile, canonical) && !EdgeStatePaths.Same(old, canonicalMachine), "only corresponding profile and scope are equivalent");
    AntigravityWorkspace.Apply(binding, command, canonical);
    Check(PluginState() == canonical, "moved receipt authorizes rewriting only the exact old managed directory plugin");
    File.AppendAllText(rule, "\nPERSONAL RULE");
    Reject(() => AntigravityWorkspace.Remove(binding, canonical), "modified directory rule should be preserved");
    Check(!File.Exists(plugin) && File.ReadAllText(rule).EndsWith("PERSONAL RULE"), "disconnect removes the managed plugin and preserves custom guidance");
    Reject(() => EdgeSession.EnsurePrivateDirectory(old), "historic binaries refuse the junction before creating a database");
    // Simulate interruption after moving the data but before publishing the prepared junction.
    Directory.Move(old, Path.Combine(Path.GetDirectoryName(old)!, ".Edge-Nodicarta-migration"));
    Check(EdgeStatePaths.Resolve(canonical) == canonical && new DirectoryInfo(old).ResolveLinkTarget(false)?.FullName == canonical, "resume staged link publication");
    Check(EdgeStatePaths.Resolve(oldMachine) == canonicalMachine && EdgeStatePaths.Same(oldMachine, canonicalMachine), "ProgramData migration");

    using (EdgeStatePaths.UseKnownFoldersForTest(Path.Combine(temp.FullName, "fresh"), machine))
    {
        var fresh = EdgeSession.Open(EdgeStatePaths.LocalDefault);
        Check(!Directory.Exists(Path.GetDirectoryName(EdgeStatePaths.LegacyLocalDefault)) &&
            EdgeStatePaths.Same(EdgeStatePaths.LegacyLocalDefault, fresh.RootDirectory), "fresh install supports obsolete registrations without creating old-brand folders");
    }
    var collision = Path.Combine(temp.FullName, "collision");
    var (first, second) = Paths(collision);
    EdgeSession.EnsurePrivateDirectory(first); EdgeSession.EnsurePrivateDirectory(second);
    File.WriteAllText(Path.Combine(first, "keep"), "old"); File.WriteAllText(Path.Combine(second, "keep"), "new");
    using (EdgeStatePaths.UseKnownFoldersForTest(collision, machine))
    {
        Reject(() => EdgeStatePaths.Resolve(second), "two independent stores must not merge");
        Check(!EdgeStatePaths.Same(first, second) && File.ReadAllText(Path.Combine(first, "keep")) == "old" &&
            File.ReadAllText(Path.Combine(second, "keep")) == "new", "both colliding originals remain intact");
    }
    // Reuse a real directory junction with a different destination as a hostile old location.
    var unexpected = Path.Combine(temp.FullName, "unexpected");
    var (foreign, target) = Paths(unexpected);
    Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
    Directory.Move(old, foreign);
    using (EdgeStatePaths.UseKnownFoldersForTest(unexpected, machine))
    {
        Reject(() => EdgeStatePaths.Resolve(target), "unexpected reparse point accepted");
        Check(!EdgeStatePaths.Same(foreign, target) && !Directory.Exists(target), "unexpected target remains untouched");
    }
    Console.WriteLine("PASS: LocalAppData and ProgramData migration, protected SQLite/profile data and sibling files, busy readers, concurrent startup, recovery, conflicting stores, default aliases and rejected foreign junctions.");
}
finally
{
    SqliteConnection.ClearAllPools();
    var folders = new Stack<DirectoryInfo>();
    folders.Push(temp);
    while (folders.TryPop(out var folder))
        foreach (var child in folder.EnumerateDirectories())
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(child.FullName);
            else folders.Push(child);
    temp.Delete(recursive: true);
}
