using Avalonia.Threading;

namespace ProjectHub.Edge.Desktop;

public partial class App
{
    private enum UpdatePhase { Waiting, Checking, Current, Available, Installing, HandedOff, Failed }
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private DesktopUpdates? updates;
    private DesktopUpdateRelease? pendingRelease;
    private CancellationTokenSource? updateOperation;
    private DateTimeOffset nextUpdateCheck;
    private UpdatePhase updatePhase;
    private bool allowUpdates;
    private static string UpdatePreference => Path.Combine(DesktopRunRecord.UserDirectory, "application-auto-update");

    internal bool ApplicationUpdatesAvailable => updates != null;
    internal bool ApplicationUpdatesEnabled
    {
        get => allowUpdates;
        set
        {
            CodexAccessFile.Edit(UpdatePreference, _ => value ? "true" : "false");
            allowUpdates = value;
            updateOperation?.Cancel();
            pendingRelease = null;
            updatePhase = UpdatePhase.Waiting;
            nextUpdateCheck = default;
        }
    }

    internal string ApplicationUpdateStatus
    {
        get
        {
            if (!ApplicationUpdatesAvailable)
                return UiLanguage.Text("使用正式安装包即可启用自动更新。", "Install the release edition to enable automatic updates.");
            if (!allowUpdates) return UiLanguage.Text("自动更新已关闭。", "Automatic updates are off.");
            return updatePhase switch
            {
                UpdatePhase.Checking => UiLanguage.Text("正在检查新版本…", "Checking for updates…"),
                UpdatePhase.Current => UiLanguage.Text("已检查稳定版渠道。", "Stable release channel checked."),
                UpdatePhase.Available => UiLanguage.Text($"已发现 {pendingRelease?.Version}，收回托盘后会自动更新。",
                    $"Version {pendingRelease?.Version} is available. Close to the tray to update."),
                UpdatePhase.Installing => UiLanguage.Text("正在验证并准备安装。", "Verifying and preparing the update."),
                UpdatePhase.HandedOff => UiLanguage.Text("已交给安装程序。", "The installer is applying the update."),
                UpdatePhase.Failed => UiLanguage.Text("本次更新未完成，稍后会自动重试。", "The update could not finish. Edge will retry."),
                _ => UiLanguage.Text("等待检查稳定版渠道。", "Waiting to check the stable release channel.")
            };
        }
    }

    private void StartApplicationUpdates()
    {
        try { updates = DesktopUpdates.ForCurrentInstall(); }
        catch (Exception e) { System.Diagnostics.Trace.TraceWarning("Update configuration: {0}", e.Message); }
        if (updates == null) return;
        try
        {
            CodexAccessFile.CheckPath(UpdatePreference);
            using var stream = File.OpenRead(UpdatePreference);
            Span<byte> enabled = stackalloc byte[4];
            if (stream.Length == enabled.Length) { stream.ReadExactly(enabled); allowUpdates = enabled.SequenceEqual("true"u8); }
        }
        catch (FileNotFoundException) { allowUpdates = true; }
        catch (DirectoryNotFoundException) { allowUpdates = true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("Update preference: {0}", e.Message); }
        updateTimer.Tick += CheckApplicationUpdate;
        updateTimer.Start();
    }

    private void StopApplicationUpdates()
    {
        updateTimer.Stop();
        updateTimer.Tick -= CheckApplicationUpdate;
        updateOperation?.Cancel();
    }

    private void PauseUpdateWhileVisible()
    {
        if (updatePhase == UpdatePhase.Installing) updateOperation?.Cancel();
    }

    private async void CheckApplicationUpdate(object? sender, EventArgs args)
    {
        if (!allowUpdates || updates == null || updateOperation != null || stopping.IsCancellationRequested) return;
        if (pendingRelease == null && DateTimeOffset.UtcNow < nextUpdateCheck) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        updateOperation = operation;
        try
        {
            if (pendingRelease == null || DateTimeOffset.UtcNow >= nextUpdateCheck)
            {
                updatePhase = UpdatePhase.Checking;
                var release = await updates.CheckAsync(operation.Token, automatic: true);
                operation.Token.ThrowIfCancellationRequested();
                pendingRelease = release;
                // Recheck a release that was deferred while the window remained open.
                nextUpdateCheck = DateTimeOffset.UtcNow + (release == null ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(10));
                updatePhase = release == null ? UpdatePhase.Current : UpdatePhase.Available;
            }
            if (pendingRelease == null || window?.IsVisible == true || PendingHarnessBatch is { IsCompleted: false }) return;
            updatePhase = UpdatePhase.Installing;
            await updates.InstallAsync(pendingRelease, operation.Token);
            pendingRelease = null;
            updatePhase = UpdatePhase.HandedOff;
            nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(1);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { updatePhase = pendingRelease == null ? UpdatePhase.Waiting : UpdatePhase.Available; }
        catch (Exception e)
        {
            pendingRelease = null;
            updatePhase = UpdatePhase.Failed;
            nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(1);
            System.Diagnostics.Trace.TraceWarning("Application update: {0}", e.Message);
        }
        finally { updateOperation = null; }
    }
}
