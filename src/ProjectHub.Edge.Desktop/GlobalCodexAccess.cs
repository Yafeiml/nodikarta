using System.Security.Cryptography;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace ProjectHub.Edge.Desktop;

internal sealed class GlobalCodexAccess
{
    private readonly EdgeDesktopClient client;
    private readonly string userHome;
    private readonly string configPath;
    private readonly string command;
    private sealed record Managed(string Block, string Command, string State);

    internal static string UserHome => Path.GetFullPath(Environment.GetEnvironmentVariable("USERPROFILE")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private static string CodexHome => Path.GetFullPath(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } configured
        ? configured : Path.Combine(UserHome, ".codex"));
    private static string ReleaseCommand => Path.Combine(AppContext.BaseDirectory, "ProjectHub.Edge.exe");
    internal static string CurrentCommand => EdgeInstallCommand.Resolve(ReleaseCommand);
    internal GlobalCodexAccess(EdgeDesktopClient client) : this(client, CodexHome, UserHome, ReleaseCommand) { }

    internal GlobalCodexAccess(EdgeDesktopClient client, string codexHome, string userHome, string command)
    {
        this.client = client;
        this.userHome = Path.GetFullPath(userHome);
        configPath = Path.Combine(Path.GetFullPath(codexHome), "config.toml");
        this.command = EdgeInstallCommand.Resolve(Path.GetFullPath(command));
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string Format(string exe, string state) =>
        CodexAccessFile.Format(exe, EdgeAccessSetup.GlobalArguments(state), [], automatic: true);
    internal string ConfigToml() => Format(command, client.StateDirectory);

    private static Managed? Inspect(string text)
    {
        var root = TomlSerializer.Deserialize(text, CodexToml.Default.TomlTable)!;
        if (!root.TryGetValue("mcp_servers", out var value)) return null;
        if (value is not TomlTable servers) throw new InvalidDataException("Codex 的 mcp_servers 格式无效。");
        if (!servers.TryGetValue("projecthub", out var entry)) return null;
        if (entry is TomlTable { Count: 2 } server &&
            server.TryGetValue("command", out var c) && c is string exe &&
            server.TryGetValue("args", out var a) && a is TomlArray args && args.Cast<object>().ToArray() is ["mcp-stdio-global", "--state", string state] &&
            CodexAccessFile.ExactBlock(text, Format(exe, state)) is { } block)
        {
            var rest = TomlSerializer.Deserialize(text.Replace(block, "", StringComparison.Ordinal), CodexToml.Default.TomlTable)!;
            if (!rest.TryGetValue("mcp_servers", out var r) || r is TomlTable table && !table.ContainsKey("projecthub"))
                return new(block, exe, state);
        }
        throw new InvalidDataException("用户级 Codex 已有同名自定义配置，请手动核对。");
    }

    private Managed? Current()
    {
        CodexAccessFile.CheckPath(configPath);
        try { return Inspect(CodexAccessFile.Decode(File.ReadAllBytes(configPath))); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (TomlException) { throw new InvalidDataException("Codex 用户配置的 TOML 格式有误，请手动核对。"); }
    }

    internal HarnessRow InspectHarness(bool installed)
    {
        HarnessRow Row(string action, string detail, bool canChange) =>
            new("codex", "Codex", installed, action, detail, canChange);
        try
        {
            var previous = Current();
            if (previous == null) return Row("接入", installed ? "当前用户尚未接入" : "未找到 Codex 程序，可手动接入", true);
            if (!EdgeStatePaths.Same(previous.State, client.StateDirectory)) return Row("待核对", "Codex 当前接入另一个 Edge 数据目录。", false);
            switch (Registration(previous, client.StateDirectory, command))
            {
                case SkillRegistration.MissingCommand:
                    return Row("待核对", UiLanguage.Text("正在等待自动修复接入路径。", "Waiting for automatic connection repair."), false);
                case SkillRegistration.Inactive:
                    return Row("待核对", "暂时无法确认接入程序，已保留原配置。", false);
            }
            return Row("已接入", installed ? "" : "已找到接入配置", false) with { CanDisconnect = true };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or TomlException or DecoderFallbackException)
        { return Row("待核对", UiLanguage.Message("无法确认全局接入：") + UiLanguage.Message(ex.Message), false); }
    }

    internal Task<string> InstallAsync(CancellationToken ct = default) => ChangeAsync(true, ct);
    internal Task<string> ConnectHarnessAsync(CancellationToken ct) => ChangeAsync(true, ct, firstConnectionOnly: true);
    internal Task<string> CleanForReconnectAsync(CancellationToken ct = default) => ChangeAsync(false, ct);

    internal void DisconnectHarness() => WithLock(configPath, () => CodexAccessFile.Edit(configPath, before =>
    {
        var entry = Inspect(before);
        if (entry == null) return before;
        if (Registration(entry, client.StateDirectory, command) == SkillRegistration.Inactive)
            throw new InvalidDataException("暂时无法确认接入程序，已保留原配置。");
        return before.Replace(entry.Block, "", StringComparison.Ordinal);
    }));

    // Different CODEX_HOME profiles on this Windows account still share the same ~/.agents/skills.
    private string SkillsPreference => Path.Combine(userHome, ".agents", ".projecthub-skills-autoupdate");
    private T WithUserSkillsLock<T>(Func<T> run) => WithLock(configPath, () => WithLock(SkillsPreference, run));

    private bool SkillsAutoUpdate()
    {
        try
        {
            CodexAccessFile.CheckPath(SkillsPreference);
            using var file = CodexAccessFile.OpenConfig(SkillsPreference);
            if (file == null) return true;
            if (file.Length != 4) return false;
            Span<byte> value = stackalloc byte[4];
            file.ReadExactly(value);
            return value.SequenceEqual("true"u8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal void SetSkillsAutoUpdate(bool enabled) => WithLock(SkillsPreference, () => CodexAccessFile.Edit(SkillsPreference, before =>
    {
        if (before is not ("" or "true" or "false")) throw new InvalidDataException("Skills 自动更新设置格式有误。");
        return enabled ? "true" : "false";
    }));

    private static SkillRegistration Registration(Managed? entry, string state, string command)
    {
        if (entry == null || !EdgeStatePaths.Same(entry.State, state)) return SkillRegistration.Inactive;
        return EdgeInstallCommand.Classify(entry.Command, command) switch
        {
            EdgeCommandKind.Exact or EdgeCommandKind.SameInstall or EdgeCommandKind.OtherCopy => SkillRegistration.Current,
            EdgeCommandKind.Missing => SkillRegistration.MissingCommand,
            _ => SkillRegistration.Inactive
        };
    }

    internal bool RepairMissingCommand() =>
        RepairMissingCommand(configPath, client.StateDirectory, command);

    private static bool RepairMissingCommand(string config, string state, string command)
    {
        if (!File.Exists(config) || !File.Exists(command)) return false;
        try { return WithLock(config, () => EditCommand(config, state, command)); }
        catch (Exception ex) when (ex is InvalidDataException or TomlException or DecoderFallbackException or CodexAccessFile.LinkedPathException)
        { return false; }
    }

    private static bool EditCommand(string config, string state, string command) =>
        CodexAccessFile.Edit(config, before =>
        {
            var old = Inspect(before);
            var registration = Registration(old, state, command);
            if (old != null && registration is SkillRegistration.MissingCommand or SkillRegistration.Current &&
                (!Same(old.Command, command) || !Same(old.State, state)))
                return CodexAccessFile.ReplaceBlock(before, old.Block, Format(command, state));
            return before;
        });

    private SkillRegistration ReadRegistrationForSkills()
    {
        try { return Registration(Current(), client.StateDirectory, command); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
        { return SkillRegistration.Inactive; }
    }

    internal SkillInventory ReadSkills() => WithUserSkillsLock(() =>
    {
        var version = UserSkills.BundledVersion;
        var skills = UserSkills.InspectBundle(userHome, version);
        var installedCount = skills.Count(skill => UserSkills.HasDefinition(userHome, skill.Name));
        return new SkillInventory(version.ToString(), SkillsAutoUpdate(), ReadRegistrationForSkills(), skills, installedCount);
    });

    internal Task<int> UpdateSkillsAsync(bool automatic = false) => Task.Run(() => UpdateSkills(automatic));

    internal int UpdateSkills(bool automatic) => WithUserSkillsLock(() =>
    {
        if (automatic && !SkillsAutoUpdate()) return 0;
        var registration = ReadRegistrationForSkills();
        if (registration != SkillRegistration.Current)
        {
            if (automatic) return 0;
            if (registration != SkillRegistration.MissingCommand)
                throw new InvalidOperationException("请先在 Codex 一行点「接入」，再更新 Skills。");
            if (!EditCommand(configPath, client.StateDirectory, command))
                throw new IOException("Codex 接入状态已变化，请刷新后重试。");
        }
        var version = UserSkills.BundledVersion;
        var count = 0;
        foreach (var skill in UserSkills.InspectBundle(userHome, version))
        {
            var applied = skill.CanUpdate && UserSkills.Install(userHome, skill.Name, updateOnly: skill.State != SkillInstallState.AddedInUpgrade);
            if (applied) count++;
            if (skill.Name == UserSkills.Init && (applied || skill.State is SkillInstallState.Current or SkillInstallState.Newer))
                UserSkills.RememberInit(userHome);
        }
        return count;
    });

    private Task<string> ChangeAsync(bool install, CancellationToken ct, bool firstConnectionOnly = false) => Task.Run(() =>
    {
        if (install && EdgeInstallCommand.Classify(command, command) != EdgeCommandKind.Exact)
            throw new IOException("暂时无法确认 Codex 接入程序，请核对原路径。");
        return WithUserSkillsLock(() => Change(install, ct, firstConnectionOnly));
    }, ct);

    private string Change(bool install, CancellationToken ct, bool firstConnectionOnly)
    {
        var notes = new List<string>();
        Managed? current;
        try { current = Current(); }
        catch (Exception ex) when (!install && ex is (IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException))
        { current = null; notes.Add(UiLanguage.Message("已保留自定义或无法读取的全局配置")); }
        if (install && current != null && !EdgeStatePaths.Same(current.State, client.StateDirectory))
            throw new InvalidOperationException("已有全局接入使用其他 Edge 数据目录，请先核对。");
        if (!install && current != null && EdgeStatePaths.Same(current.State, client.StateDirectory)
            && Registration(current, client.StateDirectory, command) is not (SkillRegistration.Current or SkillRegistration.MissingCommand))
            throw new InvalidOperationException(UiLanguage.Message("暂时无法确认接入程序，已保留原配置。"));
        if (install && current != null)
        {
            switch (Registration(current, client.StateDirectory, command))
            {
                case SkillRegistration.Current when firstConnectionOnly:
                    if (!Same(current.Command, command) || !Same(current.State, client.StateDirectory))
                        EditCommand(configPath, client.StateDirectory, command);
                    return "Codex 已接入。";
                case SkillRegistration.MissingCommand:
                    if (EditCommand(configPath, client.StateDirectory, command))
                        return "Codex 接入路径已修复。";
                    throw new IOException("Codex 接入状态已变化，请刷新后重试。");
                case SkillRegistration.Inactive:
                    throw new InvalidOperationException("暂时无法确认 Codex 接入程序，请核对原路径。");
            }
        }
        var session = client.Session;
        var workspaces = session.EnumerateLocalWorkspaces();
        var pending = new List<(LocalBinding Binding, CodexAccessFile.Existing Existing)>();
        foreach (var binding in workspaces)
        {
            ct.ThrowIfCancellationRequested();
            if (Same(Path.Combine(binding.LocalPath, ".codex", "config.toml"), configPath)) continue;
            var old = Directory.Exists(binding.LocalPath) ? CodexAccessFile.Read(binding, session.StateDirectory)
                : new CodexAccessFile.Existing("目录暂时无法访问", "");
            if (old.Block is null) continue;
            var policy = new WorkspaceAccessPolicy(old.Block == "" ? [] : old.ToolLimit, old.Endpoint);
            session.TryAddWorkspacePolicy(binding, policy);
            if (old.Block.Length == 0 || old.Command is null ||
                EdgeInstallCommand.Classify(old.Command, command) == EdgeCommandKind.Unknown)
            {
                notes.Add(UiLanguage.Message("保留目录配置，请核对 ") + binding.LocalPath);
                continue;
            }
            pending.Add((binding, old));
        }

        var common = new List<string>();
        var sharedWithOtherState = current != null && !EdgeStatePaths.Same(current.State, client.StateDirectory);
        if (sharedWithOtherState) notes.Add(UiLanguage.Message("已保留其他 Edge 数据目录的全局接入及 Skills"));
        foreach (var name in sharedWithOtherState ? Enumerable.Empty<string>() : UserSkills.Names)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var managed = install ? UserSkills.Install(userHome, name) : UserSkills.CleanLocal(userHome, name, userScope: true);
                if (name == UserSkills.Init)
                {
                    if (!install) UserSkills.ClearInitReceipt(userHome);
                    else if (managed) UserSkills.RememberInit(userHome);
                }
                if (managed) common.Add(name);
                else if (install && UserSkills.InspectUser(userHome, name, UserSkills.BundledVersion).State == SkillInstallState.Newer)
                    notes.Add(UiLanguage.Message("已保留较新版 Skill ") + name);
                else notes.Add(UiLanguage.Message("保留用户自定义 Skill ") + name);
            }
            catch (CodexAccessFile.LinkedPathException) { notes.Add(UiLanguage.Message("保留链接 Skill ") + name); }
        }
        ct.ThrowIfCancellationRequested();
        if (install || current != null && EdgeStatePaths.Same(current.State, client.StateDirectory))
        {
            CodexAccessFile.Edit(configPath, before =>
            {
                var observed = Inspect(before);
                if (observed != current) throw new IOException("用户级 MCP 配置已同时变更，请重新接入。");
                if (!install) return before.Replace(observed!.Block, "", StringComparison.Ordinal);
                var updated = CodexAccessFile.ReplaceBlock(before, observed?.Block, ConfigToml());
                if (Inspect(updated) is not { } result || !Same(result.Command, command) || !Same(result.State, client.StateDirectory))
                    throw new InvalidDataException("无法合并现有用户级 TOML，请手动核对。");
                return updated;
            });
        }
        var migrated = 0;
        foreach (var (binding, existing) in pending)
        {
            try
            {
                if (CodexAccessFile.Edit(CodexAccessFile.ConfigPath(binding), before =>
                {
                    var actual = CodexAccessFile.Inspect(before, binding, session.StateDirectory);
                    if (actual.Block == null) return before;
                    if (actual.Block != existing.Block) throw new IOException("项目配置已同时变更。");
                    var updated = before.Replace(actual.Block!, "", StringComparison.Ordinal);
                    if (CodexAccessFile.Inspect(updated, binding, session.StateDirectory).Block != null)
                        throw new IOException("配置段不能独立移除。");
                    return updated;
                })) migrated++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or TomlException or UnauthorizedAccessException)
            { notes.Add(UiLanguage.Message("保留项目配置，请核对 ") + binding.LocalPath); }
        }
        foreach (var workspace in workspaces.Select(b => b.LocalPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(workspace) || Same(workspace, userHome)) continue;
            foreach (var name in install ? (IEnumerable<string>)common : UserSkills.Names)
            {
                try { if (UserSkills.CleanLocal(workspace, name)) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                notes.Add(UiLanguage.Message("保留项目 Skill ") + Path.Combine(workspace, ".agents", "skills", name));
            }
        }
        var result = install ? UiLanguage.Format("已接入当前用户的 Codex，通用 Skills 就绪 {0}/{1} 个，已迁移 {2} 个目录配置。", common.Count, UserSkills.Names.Count, migrated)
            : UiLanguage.Format("已清理此设备受管的 Codex 接入和通用 Skills，已处理 {0} 个旧目录配置，原工具范围保留在 Edge。", migrated);
        return result + (notes.Count == 0 ? "" : " " + string.Join(UiLanguage.Text("；", "; "), notes));
    }

    internal static T WithLock<T>(string path, Func<T> run)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var mutex = new Mutex(false, "ProjectHub.CodexInstall." + key, new NamedWaitHandleOptions { CurrentUserOnly = true });
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("另一次全局安装尚未完成，请稍后重试。"); }
        catch (AbandonedMutexException) { }
        try { return run(); } finally { mutex.ReleaseMutex(); }
    }

    internal static void RefreshManagedCommand(string installRoot, string? stateDirectory = null) =>
        RefreshManagedCommand(installRoot, ReleaseCommand, Path.Combine(CodexHome, "config.toml"), stateDirectory);

    internal static void RefreshManagedCommand(string installRoot, string command, string config, string? stateDirectory = null)
    {
        if (!Path.IsPathFullyQualified(command)) return;
        var parts = Path.GetRelativePath(installRoot, command).Split(Path.DirectorySeparatorChar);
        if (parts.Length != 3 || parts[0].Length != 12 || !parts[0].All(Uri.IsHexDigit) ||
            !Same(parts[1], "bin") || !Same(parts[2], "ProjectHub.Edge.exe")) return;
        var state = EdgeStatePaths.Resolve(stateDirectory ?? Program.DefaultStateDirectory);
        RepairMissingCommand(config, state, EdgeInstallCommand.Resolve(command));
    }
}
