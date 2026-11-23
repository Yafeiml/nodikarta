using Avalonia.Interactivity;

namespace ProjectHub.Edge.Desktop;

internal partial class AccessWindow
{
    private SkillInventory? displayedSkills;
    private string? startupSkillError;

    private async Task RefreshDeviceViewAsync()
    {
        var app = Avalonia.Application.Current as App;
        var ct = lifetime.Token;
        var startupError = app == null ? null : await app.InitialSkillCheck;
        ct.ThrowIfCancellationRequested();
        if (deviceTab == DeviceTab.Clients)
        {
            await RefreshHarnessRowsAsync(ct);
            return;
        }
        if (deviceTab != DeviceTab.Skills) return;
        SkillInventory inventory;
        try { inventory = await Task.Run(global.ReadSkills, ct); }
        catch
        {
            displayedSkills = null;
            foreach (var label in new[] { SkillsCount, SkillsLocalVersion, SkillsBundleVersion }) labels.Set(label, "无法读取");
            UpdateSkillsButton.IsEnabled = AutoUpdateSkillsBox.IsEnabled = false;
            SkillsDetails.IsVisible = false;
            throw;
        }
        if (lifetime.IsCancellationRequested) return;
        app?.ShowSkillsIndicator(inventory);
        displayedSkills = inventory;
        startupSkillError = startupError;
        AutoUpdateSkillsBox.IsEnabled = true;
        AutoUpdateSkillsBox.IsChecked = inventory.AutoUpdate;
        UpdateSkillsButton.IsEnabled = inventory.CanUpdate;
        RefreshSkillLabels();
    }

    private void RefreshSkillLabels()
    {
        if (displayedSkills is not { } inventory) return;
        SkillsCount.Text = $"{inventory.InstalledCount}/{inventory.Skills.Count}";
        SkillsBundleVersion.Text = inventory.BundledVersion;
        var versions = inventory.Skills.Select(LocalVersionLabel).Distinct().ToArray();
        SkillsLocalVersion.Text = versions.Length == 1 ? versions[0] : UiLanguage.Message("多个版本或状态");
        var details = inventory.Skills.Where(s => versions.Length > 1 || s.State is not
            (SkillInstallState.Current or SkillInstallState.Missing)).Select(s => $"{s.Name}   {DescribeSkill(s)}");
        SkillsDetails.Text = string.Join(Environment.NewLine, details);
        if (startupSkillError != null && inventory.AutoUpdate && inventory.Connected && inventory.Skills.Any(s => s.CanUpdate))
            SkillsDetails.Text += Environment.NewLine + UiLanguage.Message("启动时更新未完成：") + UiLanguage.Message(startupSkillError);
        SkillsDetails.IsVisible = SkillsDetails.Text.Length > 0;
        UpdateSkillsButton.Content = UiLanguage.Message("更新 Skills");
    }

    private static string LocalVersionLabel(SkillVersionInfo skill) => skill.InstalledVersion ?? skill.State switch
    {
        SkillInstallState.Missing or SkillInstallState.AddedInUpgrade => UiLanguage.Message("未安装"),
        SkillInstallState.Legacy => UiLanguage.Message("旧版（未记录版本号）"),
        SkillInstallState.Unreadable => UiLanguage.Message("无法读取"),
        _ => UiLanguage.Message("版本未知")
    };

    private static string DescribeSkill(SkillVersionInfo skill)
    {
        var state = skill.State switch
        {
            SkillInstallState.AddedInUpgrade => UiLanguage.Message("本版新增，可安装"),
            SkillInstallState.Modified => UiLanguage.Message("本机已修改，保留"),
            SkillInstallState.Linked => UiLanguage.Message("本机链接，保留"),
            SkillInstallState.Newer => UiLanguage.Message("高于内置版本"),
            SkillInstallState.UpdateAvailable or SkillInstallState.Legacy => UiLanguage.Message("可更新"),
            _ => ""
        };
        return state.Length == 0 ? LocalVersionLabel(skill) : $"{LocalVersionLabel(skill)} · {state}";
    }

    private async void CheckSkills(object? sender, RoutedEventArgs e) =>
        await RunAsync(async _ =>
        {
            await RefreshDeviceViewAsync();
            return "已将本机 Skills 与当前 Edge 随包提供的版本进行比较。";
        }, "正在检查本机 Skills…");

    private async void UpdateSkills(object? sender, RoutedEventArgs e) =>
        await RunAsync(async _ =>
        {
            int count;
            try { count = await global.UpdateSkillsAsync(); }
            finally { if (!lifetime.IsCancellationRequested) await RefreshDeviceViewAsync(); }
            return new(() => UiLanguage.Format("已更新 {0} 项 Skills。", count));
        }, "正在更新本机 Skills…");

    private async void ToggleAutoUpdateSkills(object? sender, RoutedEventArgs e)
    {
        var enabled = AutoUpdateSkillsBox.IsChecked == true;
        await RunAsync(async _ =>
        {
            var count = 0;
            try
            {
                await Task.Run(() => global.SetSkillsAutoUpdate(enabled));
                if (enabled) count = await global.UpdateSkillsAsync(automatic: true);
            }
            finally { if (!lifetime.IsCancellationRequested) await RefreshDeviceViewAsync(); }
            if (!enabled) return "已关闭 Skills 自动更新。";
            return count == 0 ? "已开启 Skills 自动更新。" : new ViewMessage(() => UiLanguage.Format("已开启自动更新，并更新了 {0} 项 Skills。", count));
        }, "正在保存更新设置…");
    }
}
