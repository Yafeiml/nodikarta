using System.Text;
using System.Text.Json;

namespace ProjectHub.Edge.Desktop;

internal enum HarnessEdit { Connect, Repair, Remove, MigrateState }

internal sealed record HarnessRow(string Id, string Name, bool Installed, string ActionText, string Detail, bool CanInstall)
{
    public bool IsConnected => ActionText == "已接入";
    public bool CanBatchInstall => Installed && ActionText == "接入" && CanInstall;
    public bool CanDisconnect { get; init; }
    public bool PartiallyConnected { get; init; }

    // The caller has already verified the managed entry's complete shape and state directory.
    internal static void CheckManagedCommand(string previous, string current, HarnessEdit edit)
    {
        if (edit == HarnessEdit.Remove && string.Equals(previous, current, StringComparison.OrdinalIgnoreCase)) return;
        if (EdgeInstallCommand.Classify(previous, current) == EdgeCommandKind.Unknown)
            throw new InvalidDataException("暂时无法确认接入程序，已保留原配置。");
    }
}

internal sealed class HarnessAccess(EdgeDesktopClient client, Func<bool>? detectCodex = null, Func<bool>? detectClaudeDesktopPackage = null)
{
    private readonly GlobalCodexAccess codex = new(client);
    private readonly Func<bool> codexInstalled = detectCodex ?? HarnessDetection.CodexInstalled;
    private static string Command => GlobalCodexAccess.CurrentCommand;
    private string State => client.StateDirectory;
    private IReadOnlyList<StandardHarness> StandardTargets() => StandardHarnesses.Detect(detectClaudeDesktopPackage);
    private IEnumerable<StandardHarness> Targets() => StandardTargets().Append(SpecialHarnesses.Pi());
    private static bool IsClaude(StandardHarness target) => target.Id is "claude" or "claude-desktop";

    internal IReadOnlyList<HarnessRow> Inspect()
    {
        var installed = codexInstalled();
        HarnessRow codexRow;
        try
        {
            if (codex.RepairMissingCommand()) codex.UpdateSkills(automatic: true);
            codexRow = codex.InspectHarness(installed);
        }
        catch (Exception e) when (IsConfigError(e))
        {
            codexRow = codex.InspectHarness(installed) with { Detail = UiLanguage.Text("自动修复未完成：", "Automatic repair did not finish: ") + DetailFor(e) };
        }
        var rows = new List<HarnessRow> { codexRow };
        var standard = StandardTargets();
        rows.Add(InspectClaude(standard));
        foreach (var target in standard.Where(target => !IsClaude(target))) rows.Add(InspectJson(target));
        try { rows.Add(SpecialHarnesses.InspectDeepSeek(Command, State)); }
        catch (Exception e) when (IsConfigError(e))
        {
            rows.Add(new("deepseek", "DeepSeek Harness", SpecialHarnesses.DeepSeekInstalled(), "待核对", DetailFor(e), false));
        }
        rows.Add(InspectJson(SpecialHarnesses.Pi()));
        return rows;
    }

    private HarnessRow InspectClaude(IEnumerable<StandardHarness> targets)
    {
        var variants = targets.Where(IsClaude).Select(InspectJson).ToArray();
        var canInstall = variants.Any(row => row.CanBatchInstall);
        var active = variants.Where(row => row.IsConnected).ToArray();
        var problem = variants.FirstOrDefault(row => row.Installed && row.ActionText == "待核对");
        var action = canInstall ? "接入" : problem != null ? "待核对" : active.Length > 0 ? "已接入" : "接入";
        var detail = problem?.Detail ?? (action == "已接入" ? active.First().Detail : "");
        return new("claude", "Claude", variants.Any(row => row.Installed), action, detail, canInstall)
        {
            CanDisconnect = !canInstall && active.Length > 0,
            PartiallyConnected = action != "已接入" && active.Length > 0
        };
    }

    private HarnessRow InspectJson(StandardHarness target)
    {
        HarnessRow Row(string action, string detail, bool change) =>
            new(target.Id, target.Name, target.Detected, action, detail, target.Detected && change);
        if (target.BlockedReason is { } reason) return Row("待核对", reason, false);
        try
        {
            if (target.Id == "antigravity")
            {
                try { RestoreAntigravity(verifyWorkspaces: false); }
                catch (Exception e) when (IsConfigError(e))
                {
                    var status = client.Session.AntigravityMaintenance();
                    if (status.Disabled != null && (status.Pending || status.RegistrationPending))
                        return Row("待核对", UiLanguage.Message("本机目录接入仍待清理。") + " " + DetailFor(e), false);
                    if (status.Pending && SameCommand(status.Enabled))
                    {
                        CodexAccessFile.CheckPath(target.ConfigPath);
                        var before = CodexAccessFile.Decode(File.ReadAllBytes(target.ConfigPath));
                        HarnessJson.Inspect(target, before, Command, State, out var global, HarnessEdit.Repair);
                        if (global == "已接入")
                            return Row(global, UiLanguage.Text("目录配置将在后台重试。", "The workspace configuration will be retried automatically.") +
                                " " + DetailFor(e), false) with { CanDisconnect = true };
                    }
                    throw;
                }
            }
            var action = "接入";
            GlobalCodexAccess.WithLock(target.ConfigPath, () => CodexAccessFile.Edit(target.ConfigPath,
                text => HarnessJson.Inspect(target, text, Command, State, out action, HarnessEdit.Repair)));
            return Row(action, target.Detected ? target.Hint : "", action == "接入") with { CanDisconnect = action == "已接入" };
        }
        catch (Exception e) when (IsConfigError(e)) { return Row("待核对", DetailFor(e), false); }
    }

    private static bool IsConfigError(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException
        or JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException;

    private static string DetailFor(Exception e) => e is JsonException or DecoderFallbackException
        ? UiLanguage.Message("现有配置格式有误，请核对后重试。") : UiLanguage.Message(e.Message);

    internal string CopyConfig(string id) => id switch
    {
        "codex" => codex.ConfigToml(),
        "deepseek" => SpecialHarnesses.CopyDeepSeek(Command, State),
        "claude" => CopyClaude(),
        _ => HarnessJson.Copy(Targets().Single(t => t.Id == id), Command, State)
    };

    private string CopyClaude()
    {
        var variants = StandardTargets().Where(IsClaude).ToArray();
        var installed = variants.Where(target => target.Detected).ToArray();
        if (installed.Length == 2)
            return string.Join("\n\n", installed.Select(target =>
                (target.Id == "claude-desktop" ? "Claude Chat" : "Claude Code") + " · MCP\n" +
                HarnessJson.Copy(target, Command, State)));
        var only = installed.SingleOrDefault() ?? variants.Single(target => target.Id == "claude");
        return HarnessJson.Copy(only, Command, State);
    }

    private static bool SameCommand(string? command) => command != null &&
        EdgeInstallCommand.Classify(command, Command) != EdgeCommandKind.Unknown;

    private static void Migrate(string name, Action action)
    {
        try { action(); }
        catch (Exception e) when (IsConfigError(e))
        { System.Diagnostics.Trace.TraceWarning("{0} registration migration: {1}", name, e.Message); }
    }

    private void MigrateHarnesses(HarnessEdit edit)
    {
        foreach (var target in Targets().Where(t => t.BlockedReason == null && File.Exists(t.ConfigPath) &&
                     (edit == HarnessEdit.MigrateState || t.Id != "antigravity")))
            Migrate(target.Name, () => GlobalCodexAccess.WithLock(target.ConfigPath, () => CodexAccessFile.Edit(target.ConfigPath,
                text => HarnessJson.Inspect(target, text, Command, State, out _, edit))));
        Migrate("DeepSeek Harness", () => GlobalCodexAccess.WithLock("ProjectHub.DeepSeekHarness", () => SpecialHarnesses.RepairDeepSeek(Command, State, edit)));
    }

    internal void MigrateInstalledCommands(bool verifyWorkspaces = true)
    {
        Migrate("Codex", () => codex.RepairMissingCommand());
        MigrateHarnesses(HarnessEdit.Repair);
        Migrate("Google Antigravity", () => RestoreAntigravity(verifyWorkspaces));
    }

    internal void MigrateRegisteredState()
    {
        Migrate("Codex", () => codex.RepairMissingCommand());
        if (EdgeStatePaths.Same(State, EdgeStatePaths.LocalDefault) || EdgeStatePaths.Same(State, EdgeStatePaths.MachineDefault))
            MigrateHarnesses(HarnessEdit.MigrateState);

        var session = client.Session;
        foreach (var binding in session.EnumerateLocalWorkspaces())
            Migrate("Codex workspace", () =>
            {
                var file = CodexAccessFile.ConfigPath(binding);
                if (!File.Exists(file)) return;
                GlobalCodexAccess.WithLock(file, () => CodexAccessFile.Edit(file, text =>
                {
                    session.EnsureActiveProfile();
                    var prior = CodexAccessFile.Inspect(text, binding, session.StateDirectory);
                    return prior.RelocatedBlock != null ? CodexAccessFile.ReplaceBlock(text, prior.Block, prior.RelocatedBlock) : text;
                }));
            });
    }

    internal void RestoreAntigravity(bool verifyWorkspaces = true)
    {
        var target = StandardTargets().Single(t => t.Id == "antigravity");
        GlobalCodexAccess.WithLock(target.ConfigPath, () =>
        {
            var session = client.Session;
            var maintenance = session.AntigravityMaintenance();
            if (maintenance.Disabled != null && (maintenance.Pending || maintenance.RegistrationPending))
            {
                try
                {
                    if (maintenance.RegistrationPending)
                    {
                        if (target.BlockedReason is { } reason) throw new InvalidDataException(reason);
                        // The persisted disconnect identifies the exact registration even if its old drive is offline.
                        CodexAccessFile.Edit(target.ConfigPath, before => HarnessJson.Inspect(target, before, Command, State, out _,
                            HarnessEdit.Remove, retiringCommand: maintenance.Disabled));
                        session.FinishAntigravityDisconnect(maintenance.Disabled);
                    }
                }
                finally { if (maintenance.Pending) session.RetryAntigravityCleanup(); }
                return false;
            }
            if (target.BlockedReason != null || !File.Exists(target.ConfigPath)) return false;
            CodexAccessFile.CheckPath(target.ConfigPath);
            var before = CodexAccessFile.Decode(File.ReadAllBytes(target.ConfigPath));
            var updated = HarnessJson.Inspect(target, before, Command, State, out var action, HarnessEdit.Repair);
            if (action != "已接入") return false;
            if (updated != before)
                CodexAccessFile.Edit(target.ConfigPath, text => text == before ? updated : throw new IOException("接入配置已更改，请刷新后重试。"));
            if (verifyWorkspaces || !string.Equals(maintenance.Enabled, Command, StringComparison.OrdinalIgnoreCase))
                session.EnableAntigravity(Command);
            else if (maintenance.Pending) session.RetryAntigravityCleanup();
            return true;
        });
    }

    internal Task DisconnectAsync(string id, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (id == "codex") { codex.DisconnectHarness(); return; }
        if (id == "deepseek")
        {
            GlobalCodexAccess.WithLock("ProjectHub.DeepSeekHarness", () => SpecialHarnesses.DisconnectDeepSeek(Command, State));
            return;
        }
        if (id == "claude")
        {
            var variants = StandardTargets().Where(IsClaude).Select(target => (Target: target, Row: InspectJson(target))).ToArray();
            var connected = variants.Where(variant => variant.Row.CanDisconnect).ToArray();
            if (connected.Length == 0 && variants.FirstOrDefault(variant => variant.Row.ActionText == "待核对").Row is { } problem)
                throw new InvalidDataException(UiLanguage.Message(problem.Detail));
            var problems = new List<string>();
            foreach (var (target, _) in connected)
            {
                ct.ThrowIfCancellationRequested();
                try { DisconnectJson(target); }
                catch (Exception e) when (IsConfigError(e)) { problems.Add(DetailFor(e)); }
            }
            if (problems.Count > 0) throw new InvalidDataException(string.Join("\n", problems));
            return;
        }
        DisconnectJson(Targets().Single(t => t.Id == id));
    }, ct);

    private void DisconnectJson(StandardHarness target)
    {
        if (target.BlockedReason is { } reason) throw new InvalidDataException(reason);
        GlobalCodexAccess.WithLock(target.ConfigPath, () =>
        {
            string Remove(string text) => HarnessJson.Inspect(target, text, Command, State, out _, HarnessEdit.Remove);
            if (target.Id != "antigravity") return CodexAccessFile.Edit(target.ConfigPath, Remove);
            var session = client.Session;
            // Establish ownership before retiring any directory, then persist the intent before touching the global file.
            CodexAccessFile.CheckPath(target.ConfigPath);
            var before = File.Exists(target.ConfigPath) ? CodexAccessFile.Decode(File.ReadAllBytes(target.ConfigPath)) : "";
            _ = Remove(before);
            var maintenance = session.AntigravityMaintenance();
            var owner = maintenance.Enabled ?? maintenance.Disabled ?? Command;
            session.PrepareAntigravityDisconnect(owner);
            try
            {
                CodexAccessFile.Edit(target.ConfigPath, Remove);
                session.FinishAntigravityDisconnect(owner);
            }
            finally { session.RetryAntigravityCleanup(); }
            return true;
        });
    }

    internal Task<string> InstallAsync(string id, CancellationToken ct, bool installedOnly = false) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(Command)) throw new FileNotFoundException("缺少桌面版随附的 ProjectHub.Edge.exe。");
        if (id == "codex")
        {
            if (installedOnly && !codexInstalled()) throw new InvalidOperationException("未检测到 Codex。");
            return await codex.ConnectHarnessAsync(ct);
        }
        if (id == "deepseek")
        {
            if (!SpecialHarnesses.DeepSeekInstalled()) throw new InvalidOperationException("未检测到 DeepSeek Harness。");
            return GlobalCodexAccess.WithLock("ProjectHub.DeepSeekHarness", () => SpecialHarnesses.InstallDeepSeek(Command, State));
        }
        if (id == "claude")
        {
            var installed = StandardTargets().Where(target => IsClaude(target) && target.Detected).ToArray();
            if (installed.Length == 0) throw new InvalidOperationException(UiLanguage.Format("未检测到 {0}。", "Claude"));
            var problems = new List<string>();
            foreach (var target in installed)
            {
                ct.ThrowIfCancellationRequested();
                try { InstallJson(target); }
                catch (Exception e) when (IsConfigError(e)) { problems.Add(DetailFor(e)); }
            }
            if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
            return UiLanguage.Format("{0} 配置已就绪。{1}", "Claude",
                UiLanguage.Message(installed.First().Hint));
        }
        return InstallJson(Targets().Single(t => t.Id == id));
    }, ct);

    private string InstallJson(StandardHarness target)
    {
        if (!target.Detected) throw new InvalidOperationException(UiLanguage.Format("未检测到 {0}。", target.Name));
        if (target.BlockedReason is { } reason) throw new InvalidOperationException(reason);
        GlobalCodexAccess.WithLock(target.ConfigPath, () =>
        {
            var changed = CodexAccessFile.Edit(target.ConfigPath, before => HarnessJson.Inspect(target, before, Command, State, out _));
            if (target.Id == "antigravity") client.Session.EnableAntigravity(Command);
            return changed;
        });
        return target.ConnectedMessage is { } message ? UiLanguage.Message(message)
            : UiLanguage.Format("{0} 配置已就绪。{1}", target.Name, UiLanguage.Message(target.Hint));
    }

    internal Task<string> InstallAllAsync(CancellationToken ct) => Task.Run(async () =>
    {
        var detected = Inspect().Where(r => r.Installed).ToList();
        if (detected.Count == 0) return "未检测到已安装的智能体。";
        var ready = 0;
        var problems = new List<string>();
        foreach (var row in detected)
        {
            ct.ThrowIfCancellationRequested();
            if (row.IsConnected)
            {
                ready++;
                continue;
            }
            if (!row.CanBatchInstall)
            {
                problems.Add(row.Name + UiLanguage.Text("：", ": ") + UiLanguage.Message(row.Detail));
                continue;
            }
            try
            {
                await InstallAsync(row.Id, ct, installedOnly: true);
                ready++;
            }
            catch (Exception e) when (IsConfigError(e)) { problems.Add(row.Name + UiLanguage.Text("：", ": ") + DetailFor(e)); }
        }
        return UiLanguage.Format("已就绪 {0}/{1} 个已安装智能体。", ready, detected.Count) + string.Join(UiLanguage.Text("；", "; "), problems);
    }, ct);
}
