#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ProjectHub.Edge.Desktop;

var root = Directory.CreateTempSubdirectory("projecthub-skills-version-");
var home = Path.Combine(root.FullName, "user");
var config = Path.Combine(root.FullName, "codex");
var client = new EdgeDesktopClient(Path.Combine(root.FullName, "edge"));
var command = Path.Combine(root.FullName, "ProjectHub.Edge.exe");
File.WriteAllBytes(command, []);
var service = new GlobalCodexAccess(client, config, home, command);
string Target(string name) => UserSkills.Target(home, "nodicarta-" + name);
string Marker(string name) => Path.Combine(Target(name), ".projecthub-managed");
var version = service.ReadSkills().BundledVersion;
void Mark(string name, string? installed)
{
    var file = Marker(name);
    var digest = File.ReadAllText(file).Split(' ')[^1];
    File.WriteAllText(file, installed == null ? digest : $"{installed} {digest}");
}
SkillVersionInfo Local(string name) => service.ReadSkills().Skills.Single(s => s.Name == "nodicarta-" + name);
void Check(bool valid, string detail) { if (!valid) throw new Exception(detail); }
try
{
    var initial = service.ReadSkills();
    Check(initial.AutoUpdate && !initial.Connected && initial.InstalledCount == 0 && initial.Skills.Count == 5 && initial.Skills.All(s => s.State == SkillInstallState.Missing),
        "fresh user defaults to automatic maintenance without connecting Codex");
    var emptySkill = Directory.CreateDirectory(Target("sync"));
    Check(service.ReadSkills().InstalledCount == 0 && Local("sync").State == SkillInstallState.Modified,
        "a preserved user directory without SKILL.md is not an installed Skill");
    emptySkill.Delete();
    await service.InstallAsync();
    Check(service.ReadSkills().Skills.All(s => s.InstalledVersion == version && s.State == SkillInstallState.Current), "installed versions");
    Mark("inbox", "0.1.0");
    Check(await service.UpdateSkillsAsync(automatic: true) == 1 && Local("inbox").State == SkillInstallState.Current, "default-on update");
    service.SetSkillsAutoUpdate(false);
    var preference = Path.Combine(home, ".agents", ".projecthub-skills-autoupdate");
    File.WriteAllText(preference, "oops");
    Check(!service.ReadSkills().AutoUpdate && await service.UpdateSkillsAsync(automatic: true) == 0, "malformed preference fails closed");
    File.WriteAllText(preference, "false");
    var previousInbox = UserSkills.Target(home, "projecthub-inbox");
    Mark("inbox", "1.5.1");
    Directory.Move(Target("inbox"), previousInbox);
    Check(service.ReadSkills().InstalledCount == 5 && Local("inbox") is { InstalledVersion: "1.5.1", CanUpdate: true }, "discover previous name");
    Check(await service.UpdateSkillsAsync(automatic: true) == 0 && Directory.Exists(previousInbox), "rename respects update preference");
    var priorText = Path.Combine(previousInbox, "SKILL.md");
    var priorBytes = File.ReadAllBytes(priorText);
    File.AppendAllText(priorText, "\nPersonal workflow");
    Check(Local("inbox").State == SkillInstallState.Modified && await service.UpdateSkillsAsync() == 0 &&
        File.ReadAllText(priorText).EndsWith("Personal workflow"), "keep customized legacy names");
    File.WriteAllBytes(priorText, priorBytes);
    Check(await service.UpdateSkillsAsync() == 1 && Local("inbox").State == SkillInstallState.Current &&
        !Directory.Exists(previousInbox) && await service.UpdateSkillsAsync() == 0, "move verified legacy install only once");
    void MarkPreviousBundle()
    {
        File.Delete(UserSkills.InitReceipt(home)); // Earlier bundles had no init delivery receipt.
        foreach (var name in new[] { "assets", "context", "inbox", "sync" }) Mark(name, "1.0.0");
    }
    Directory.Move(Target("init"), Path.Combine(root.FullName, "first-init"));
    MarkPreviousBundle();
    Mark("context", version);
    Check(Local("init").State == SkillInstallState.AddedInUpgrade && !File.Exists(UserSkills.InitReceipt(home)),
        "an interrupted migration with mixed versions and no delivered init can retry");
    Mark("context", "1.0.0");
    var assetsDoc = Path.Combine(Target("assets"), "SKILL.md");
    var originalAssets = File.ReadAllBytes(assetsDoc);
    File.AppendAllText(assetsDoc, "\nUSER EDIT");
    Check(service.ReadSkills().InstalledCount == 4 && Local("init").State == SkillInstallState.AddedInUpgrade, "four previous Skills prompt for the new fifth one");
    Check(service.InspectHarness(true) is { IsConnected: true, CanBatchInstall: false, CanInstall: false, Detail: "" },
        "outdated, modified and missing Skills leave the Codex row connected");
    await service.ConnectHarnessAsync(default);
    Check(!Directory.Exists(Target("init")) && Local("context").InstalledVersion == "1.0.0",
        "reconnecting a registered client leaves Skill updates to the Skills page");
    Check(await service.UpdateSkillsAsync(automatic: true) == 0 && !Directory.Exists(Target("init")), "opt-out preserves the old bundle");
    Check(await service.UpdateSkillsAsync() == 4 && Local("init").State == SkillInstallState.Current && service.ReadSkills().InstalledCount == 5,
        "manual upgrade adds init and updates three unmodified old Skills");
    Check(File.ReadAllText(assetsDoc).EndsWith("USER EDIT") && Local("assets").State == SkillInstallState.Modified, "manual upgrade keeps local edit");
    Directory.Move(Target("init"), Path.Combine(root.FullName, "manual-init"));
    Check(Local("init").State == SkillInstallState.Missing && await service.UpdateSkillsAsync() == 0,
        "deleting init after an upgrade keeps it removed");
    MarkPreviousBundle();
    service.SetSkillsAutoUpdate(true);
    Check(await service.UpdateSkillsAsync(automatic: true) == 4 && Local("init").State == SkillInstallState.Current &&
        Local("assets").State == SkillInstallState.Modified, "opted-in upgrade adds init without touching modified files");
    Check(await service.UpdateSkillsAsync(automatic: true) == 0, "complete upgrade is idempotent");
    service.SetSkillsAutoUpdate(false);
    File.WriteAllBytes(assetsDoc, originalAssets);
    Check(await service.UpdateSkillsAsync() == 1, "fixture restores the original managed assets before subsequent checks");

    Directory.Move(Target("init"), Path.Combine(root.FullName, "auto-init"));
    MarkPreviousBundle();
    var docs = new[] { "assets", "context", "inbox", "sync" }.Select(n => Path.Combine(Target(n), "SKILL.md")).ToArray();
    var originals = docs.Select(File.ReadAllBytes).ToArray();
    foreach (var doc in docs) File.AppendAllText(doc, "\nCUSTOMIZED");
    Check(await service.UpdateSkillsAsync() == 1 && File.Exists(UserSkills.InitReceipt(home)), "all four customized: install only the new Skill");
    File.Delete(UserSkills.InitReceipt(home)); // Simulate termination after placing init but before committing its receipt.
    Check(await service.UpdateSkillsAsync() == 0 && File.Exists(UserSkills.InitReceipt(home)), "retry commits a missing receipt for the already delivered init");
    Directory.Move(Target("init"), Path.Combine(root.FullName, "all-edited-init"));
    service.SetSkillsAutoUpdate(true);
    Check(!Local("init").CanUpdate && await service.UpdateSkillsAsync(automatic: true) == 0 && !Directory.Exists(Target("init")),
        "automatic update preserves a deliberate removal even when all four original files still report 1.0");
    Check(docs.All(doc => File.ReadAllText(doc).EndsWith("CUSTOMIZED")), "all four custom files survived");
    service.SetSkillsAutoUpdate(false);
    for (var i = 0; i < docs.Length; i++) File.WriteAllBytes(docs[i], originals[i]);
    await service.InstallAsync();
    var inboxDoc = Path.Combine(Target("inbox"), "SKILL.md");
    var expectedInbox = File.ReadAllBytes(inboxDoc);
    File.WriteAllText(inboxDoc, "Earlier inbox instructions");
    var fileDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inboxDoc)));
    var oldDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("SKILL.md:" + fileDigest)));
    File.WriteAllText(Marker("inbox"), "0.1.0 " + oldDigest);
    Mark("context", null);
    var altered = Path.Combine(Target("assets"), "SKILL.md");
    File.AppendAllText(altered, "\nLOCAL EDIT");
    Directory.Delete(Target("sync"), true);
    Check(Local("inbox").State == SkillInstallState.UpdateAvailable && Local("inbox").InstalledVersion == "0.1.0", "explicit old version");
    Check(Local("context").State == SkillInstallState.Legacy && Local("context").InstalledVersion == null, "authentic hash-only marker");
    Check(Local("assets").State == SkillInstallState.Modified && Local("sync").State == SkillInstallState.Missing, "missing and modified");
    Check(await service.UpdateSkillsAsync(automatic: true) == 0 && Local("inbox").CanUpdate, "automatic remains off after an explicit opt-out");
    Check((await Task.WhenAll(service.UpdateSkillsAsync(), service.UpdateSkillsAsync())).Sum() == 2, "manual, concurrent, idempotent");
    Check(expectedInbox.SequenceEqual(File.ReadAllBytes(inboxDoc)) && Local("inbox").State == SkillInstallState.Current, "updated instruction contents");
    Check(File.ReadAllText(altered).EndsWith("LOCAL EDIT") && !Directory.Exists(Target("sync")), "manual preserves custom and missing");

    Mark("inbox", "0.1.0");
    Mark("context", "99.0.0");
    service.SetSkillsAutoUpdate(true);
    var reopened = new GlobalCodexAccess(client, config, home, command);
    Check(reopened.ReadSkills().AutoUpdate, "opt-in survives a fresh service");
    var stale = new GlobalCodexAccess(client, config, home, Path.Combine(root.FullName, "old", "ProjectHub.Edge.exe"));
    Check(await stale.UpdateSkillsAsync(automatic: true) == 0 && Local("inbox").CanUpdate, "old executable skips automatic write");
    Check(await reopened.UpdateSkillsAsync(automatic: true) == 1 && Local("inbox").State == SkillInstallState.Current, "update on launch");
    Check(Local("context").State == SkillInstallState.Newer && !Directory.Exists(Target("sync")), "newer preserved, missing not restored");
    Check(await service.UpdateSkillsAsync() == 0 && Local("context").InstalledVersion == "99.0.0", "manual preserves newer");

    // A junction occupying a Skill name must not cause inspection or updates outside the user's Skills.
    var foreign = Directory.CreateDirectory(Path.Combine(root.FullName, "external")).FullName;
    File.WriteAllText(Path.Combine(foreign, "SKILL.md"), "keep");
    var link = Target("sync");
    var mklink = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in new[]{"/d","/c","mklink","/j",link,foreign}) mklink.ArgumentList.Add(arg);
    using (var process = Process.Start(mklink)!) { process.WaitForExit(); Check(process.ExitCode == 0, "fixture junction"); }
    try
    {
        Check(Local("sync").State == SkillInstallState.Linked && await service.UpdateSkillsAsync(automatic: true) == 0, "linked Skill skipped");
        Check(File.ReadAllText(Path.Combine(foreign, "SKILL.md")) == "keep", "link target unchanged");
    }
    finally { Directory.Delete(link); }
    var configPath = Path.Combine(config, "config.toml");
    var relocatedCommand = Path.Combine(Directory.CreateDirectory(Path.Combine(root.FullName, "new Edge")).FullName, "ProjectHub.Edge.exe");
    File.WriteAllBytes(relocatedCommand, []);
    var relocated = new GlobalCodexAccess(client, config, home, relocatedCommand);
    const string personal = "# user's settings\n[profiles.personal]\nmodel = \"example\"\n\n";
    var beforeMove = personal + File.ReadAllText(configPath);
    File.WriteAllText(configPath, beforeMove);
    Mark("inbox", "1.3.1");
    async Task CheckRejectedUpdate(GlobalCodexAccess access, string reason)
    {
        Check(!access.ReadSkills().CanUpdate, reason + " disables the button");
        try { await access.UpdateSkillsAsync(); }
        catch (InvalidOperationException) { return; }
        throw new Exception(reason + " must reject manual update");
    }
    Check(relocated.ReadSkills().CanUpdate, "same-state managed configuration remains eligible after moving the desktop");
    File.Delete(command);
    var pending = relocated.ReadSkills();
    Check(!pending.Connected && pending.CanRepairRegistration && pending.CanUpdate,
        "manually extracted Edge offers repair with an eligible Skill and the old command missing");
    Check(await relocated.UpdateSkillsAsync(automatic: true) == 0 && File.ReadAllText(configPath) == beforeMove &&
        Local("inbox").InstalledVersion == "1.3.1", "startup leaves the registration and old Skills alone");
    var otherState = new EdgeDesktopClient(Path.Combine(root.FullName, "another state"));
    await CheckRejectedUpdate(new GlobalCodexAccess(otherState, config, home, relocatedCommand), "foreign Edge state");
    await CheckRejectedUpdate(new GlobalCodexAccess(client, config, home, command + ".missing"), "missing new executable");
    Check(File.ReadAllText(configPath) == beforeMove, "refused repairs preserve the original config");
    var customized = beforeMove + "startup_timeout_sec = 10\n";
    File.WriteAllText(configPath, customized);
    await CheckRejectedUpdate(relocated, "user-customized MCP registration");
    Check(File.ReadAllText(configPath) == customized && Local("inbox").InstalledVersion == "1.3.1", "custom registration stays untouched");
    File.WriteAllText(configPath, beforeMove);
    Check(await relocated.UpdateSkillsAsync() == 1 && relocated.ReadSkills().Connected,
        "manual upgrade repairs the moved executable and updates the eligible Skill");
    var expectedConfig = beforeMove.Replace(service.ConfigToml().ReplaceLineEndings(), relocated.ConfigToml().ReplaceLineEndings(), StringComparison.Ordinal);
    Check(File.ReadAllText(configPath) == expectedConfig, "only the generated registration block changes");
    Check(Local("inbox").State == SkillInstallState.Current && Local("context").State == SkillInstallState.Newer &&
        Local("assets").State == SkillInstallState.Modified && !Directory.Exists(Target("sync")),
        "only the eligible Skill changes");
    Check(await relocated.UpdateSkillsAsync() == 0, "retry after relocation is idempotent");
    File.Delete(configPath);
    Mark("inbox", "0.1.0");
    await CheckRejectedUpdate(relocated, "disconnected Codex profile");
    Check(await service.UpdateSkillsAsync(automatic: true) == 0 && !File.Exists(Path.Combine(config, "config.toml")) && Local("inbox").CanUpdate,
        "no registration means no automatic changes");
    service.SetSkillsAutoUpdate(false);
    Check(!reopened.ReadSkills().AutoUpdate, "disabling persists");
    Console.WriteLine("PASS: local versions, legacy migration, portable relocation, opt-in, concurrency, preserved custom/newer/missing/linked Skills and inactive registration.");
}
finally { root.Delete(true); }
