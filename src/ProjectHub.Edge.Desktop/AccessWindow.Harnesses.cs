using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ProjectHub.Edge.Desktop;

internal sealed record HarnessListItem(HarnessRow Client, IImage Icon, bool HasClaudeDesktop) : LocalizedRow
{
    public string DisplayName => Client.Id switch
    {
        "claude" when !HasClaudeDesktop => "Claude",
        "claude-desktop" => UiLanguage.Text("Claude 桌面版", "Claude Desktop"),
        "antigravity" => "Antigravity",
        "deepseek" => "DeepSeek",
        _ => Client.Name
    };

    public string ActionLabel => Client.CanDisconnect ? UiLanguage.Text("断开接入", "Disconnect") : UiLanguage.Message("接入");
    public string ActionName => Client.CanDisconnect
        ? UiLanguage.Text($"断开 {Client.Name} 接入", $"Disconnect {Client.Name}")
        : $"{ActionLabel} {Client.Name}";
    public bool CanAct => Client.CanDisconnect || (Client.ActionText == "接入" && Client.CanInstall);
    public string CopyText => UiLanguage.Text("复制", "Copy");
    public string CopyName => UiLanguage.Text($"复制 {Client.Name} 配置", $"Copy {Client.Name} configuration");
    public string Status
    {
        get
        {
            var detail = Client.Detail;
            string state;
            if (Client.IsConnected)
            {
                state = "已接入";
                if (!Client.Installed && detail is "" or "已找到接入配置") detail = "未检测到安装";
            }
            else if (Client.ActionText == "待核对") state = "待核对";
            else if (Client.Installed) state = "已安装";
            else if (detail.Length > 0) return UiLanguage.Message(detail);
            else state = "未检测到安装";
            return detail.Length == 0 ? UiLanguage.Message(state) : $"{UiLanguage.Message(state)} · {UiLanguage.Message(detail)}";
        }
    }
}

internal partial class AccessWindow
{
    private int harnessRefreshVersion;

    internal Task RefreshAfterMaintenanceAsync() =>
        initialized && IsDevicePage && deviceTab == DeviceTab.Clients && !busy && !lifetime.IsCancellationRequested
            ? RefreshHarnessRowsAsync(lifetime.Token, silent: true) : Task.CompletedTask;

    internal Task RefreshAfterCodexRepairAsync()
    {
        if (!initialized || !IsDevicePage || deviceTab != DeviceTab.Skills) return RefreshAfterMaintenanceAsync();
        return RunAsync(async _ => { await RefreshDeviceViewAsync(); return ""; }, "");
    }

    private async Task RefreshHarnessRowsAsync(CancellationToken ct, bool silent = false)
    {
        var version = ++harnessRefreshVersion;
        IReadOnlyList<HarnessRow> rows;
        try { rows = await Task.Run(harnesses.Inspect, ct); }
        catch (Exception ex) when (silent)
        {
            if (!ct.IsCancellationRequested) System.Diagnostics.Trace.TraceWarning("Client status refresh: {0}", ex.Message);
            return;
        }
        catch
        {
            hasInstallableClients = false;
            HarnessRows.ItemsSource = null;
            throw;
        }
        if (ct.IsCancellationRequested || version != harnessRefreshVersion) return;
        if (!HarnessRows.Items.Cast<HarnessListItem>().Select(item => item.Client).SequenceEqual(rows))
        {
            var hasClaudeDesktop = rows.Any(row => row.Id == "claude-desktop");
            HarnessRows.ItemsSource = rows.Select(row => new HarnessListItem(row,
                (IImage)Resources[row.Id == "claude-desktop" ? "claude" : row.Id]!, hasClaudeDesktop)).ToArray();
        }
        hasInstallableClients = rows.Any(row => row.CanBatchInstall);
        if (silent) InstallDetectedButton.IsEnabled = hasInstallableClients;
    }

    private async void InstallHarnesses(object? sender, RoutedEventArgs e)
    {
        if (sender == InstallDetectedButton)
        {
            var app = (App)Application.Current!;
            await RunAsync(ct => FollowHarnessBatchAsync(app.StartHarnessBatch(harnesses), ct), "正在接入已安装的智能体…");
            return;
        }
        if (sender is not Button { DataContext: HarnessListItem item }) return;
        if (item.Client.CanDisconnect)
        {
            if (Host is { } owner) await RunAsync(ct => DisconnectHarnessAsync(item, owner, ct), "");
            return;
        }
        await RunAsync(async ct =>
        {
            try
            {
                return await harnesses.InstallAsync(item.Client.Id, ct);
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                {
                    if (item.Client.Id == "codex") await RefreshDeviceViewAsync();
                    else await RefreshHarnessRowsAsync(ct);
                }
            }
        }, new(() => UiLanguage.Format("正在配置 {0}…", item.Client.Name)));
    }

    private async Task<ViewMessage> FollowHarnessBatchAsync(Task<string> batch, CancellationToken viewToken)
    {
        try { return await batch.WaitAsync(viewToken); }
        finally
        {
            if (!viewToken.IsCancellationRequested)
            {
                try { await RefreshDeviceViewAsync(); }
                finally
                {
                    if (!viewToken.IsCancellationRequested) ((App)Application.Current!).ClearHarnessBatch(batch);
                }
            }
        }
    }

    private async void CopyHarnessConfig(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HarnessListItem item }) return;
        await RunAsync(async ct =>
        {
            var config = await Task.Run(() => harnesses.CopyConfig(item.Client.Id), ct);
            ct.ThrowIfCancellationRequested();
            await WriteClipboardAsync(config);
            return new(() => UiLanguage.Text($"已复制 {item.Client.Name} 配置。", $"Copied the configuration for {item.Client.Name}."));
        }, "正在复制配置…");
    }

    private async Task<ViewMessage> DisconnectHarnessAsync(HarnessListItem item, Window owner, CancellationToken ct)
    {
        if (!await new DisconnectHarnessDialog(item.Client).ShowDialog<bool>(owner)) return "";
        try { await harnesses.DisconnectAsync(item.Client.Id, ct); }
        finally { if (!ct.IsCancellationRequested) await RefreshDeviceViewAsync(); }
        return new(() => UiLanguage.Text($"已断开 {item.Client.Name} 接入。", $"Disconnected {item.Client.Name}."));
    }

    private Task WriteClipboardAsync(string config)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("无法访问系统剪贴板。");
        return clipboard.SetTextAsync(config);
    }
}
