using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Win32;

namespace ProjectHub.Edge.Desktop;

internal sealed partial class Windows10WindowFrame : Style
{
    public Windows10WindowFrame() => AvaloniaXamlLoader.Load(this);
}

public partial class App : Application
{
    internal EdgeDesktopClient Client { private get; init; } = null!;
#if LINUX_DESKTOP
    internal string? LinuxStartupError { get; init; }
#endif
    internal EventWaitHandle Wake { private get; init; } = null!;
    internal EventWaitHandle Retire { private get; init; } = null!;
    internal Action Started { private get; init; } = null!;
    internal bool Background { private get; init; }
    private MainWindow? window;
    private NativeMenuItem? accessMenu;
    private string accessLabel = "智能体接入";
    private string trayLabel = "脉络 Edge";
    private CancellationToken stopping;
    internal Task<string>? PendingHarnessBatch { get; private set; }
    internal Task<string?> InitialSkillCheck { get; private set; } = Task.FromResult<string?>(null);

    internal Task<string> StartHarnessBatch(HarnessAccess access) =>
        PendingHarnessBatch ??= access.InstallAllAsync(stopping);

    internal void ClearHarnessBatch(Task<string> observed)
    {
        if (PendingHarnessBatch == observed) PendingHarnessBatch = null;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            Styles.Add(new Windows10WindowFrame());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            void LocalizeEditMenu()
            {
                Resources["StringTextFlyoutCutText"] = UiLanguage.Text("剪切", "Cut");
                Resources["StringTextFlyoutCopyText"] = UiLanguage.Text("复制", "Copy");
                Resources["StringTextFlyoutPasteText"] = UiLanguage.Text("粘贴", "Paste");
            }
            LocalizeEditMenu();
            UiLanguage.Changed += LocalizeEditMenu;
            desktop.Exit += (_, _) => UiLanguage.Changed -= LocalizeEditMenu;
#if LINUX_DESKTOP
            LinuxDesktop.OpenWindow(desktop, Client, LinuxStartupError);
#else
            var lightTaskbarIcon = LoadTrayIcon("edge-tray.ico");
            var darkTaskbarIcon = LoadTrayIcon("edge-tray-dark.ico");
            var open = new NativeMenuItem();
            var access = new NativeMenuItem();
            accessMenu = access;
            var about = new NativeMenuItem();
            var quit = new NativeMenuItem();
            var tray = new TrayIcon
            {
                Icon = LightTaskbar() ? lightTaskbarIcon : darkTaskbarIcon, IsVisible = true,
                Menu = new NativeMenu { Items = { open, access, about, new NativeMenuItemSeparator(), quit } }
            };
            void UpdateMenuLanguage()
            {
                open.Header = UiLanguage.Message("打开主界面");
                access.Header = UiLanguage.Message(accessLabel);
                about.Header = UiLanguage.Message("关于脉络 Edge");
                quit.Header = UiLanguage.Message("退出");
                tray.ToolTipText = UiLanguage.Message(trayLabel);
            }
            UiLanguage.Changed += UpdateMenuLanguage;
            UpdateMenuLanguage();
            // A tray-only session can run without a window to receive Windows settings broadcasts.
            var systemPreferencesTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            systemPreferencesTimer.Tick += (_, _) =>
            {
                UiLanguage.RefreshSystemLanguage();
                var icon = LightTaskbar() ? lightTaskbarIcon : darkTaskbarIcon;
                if (tray.Icon != icon) tray.Icon = icon;
            };
            systemPreferencesTimer.Start();
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            var stop = new CancellationTokenSource();
            stopping = stop.Token;

            MainWindow EnsureWindow()
            {
                UiLanguage.RefreshSystemLanguage();
                if (window is null)
                {
                    window = new MainWindow(Client);
                    window.Activated += (_, _) => PauseUpdateWhileVisible();
                    window.Activated += (_, _) => UiLanguage.RefreshSystemLanguage();
                    window.Closing += (_, e) =>
                    {
                        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;
                        e.Cancel = true;
                        window.Hide();
                        window.ReturnToOverview();
                    };
                }
                return window;
            }

            void ShowMainWindow()
            {
                if (!stop.IsCancellationRequested) EnsureWindow().Reveal();
            }

            open.Click += (_, _) => ShowMainWindow();
            access.Click += (_, _) =>
            {
                if (!stop.IsCancellationRequested) EnsureWindow().OpenAccessSettings();
            };
            about.Click += (_, _) =>
            {
                if (!stop.IsCancellationRequested) EnsureWindow().ShowClientAbout();
            };
            tray.Clicked += (_, _) => ShowMainWindow();
            quit.Click += (_, _) => desktop.Shutdown();
            var registration = ThreadPool.RegisterWaitForSingleObject(Wake,
                (_, _) => Dispatcher.UIThread.Post(ShowMainWindow), null, Timeout.Infinite, false);
            var retireRegistration = ThreadPool.RegisterWaitForSingleObject(Retire,
                (_, _) => Dispatcher.UIThread.Post(() => desktop.Shutdown()), null, Timeout.Infinite, true);
            Started();
            Dispatcher.UIThread.Post(ReleaseInstallation.SignalReady, DispatcherPriority.Background);
            StartApplicationUpdates();
            var worker = Task.Run(() => KeepConnectedAsync(tray, stop.Token));
            var antigravity = Task.Run(() =>
            {
                try
                {
                    var harnesses = new HarnessAccess(Client);
                    harnesses.MigrateRegisteredState();
                    harnesses.MigrateInstalledCommands();
                }
                catch (Exception error)
                {
                    Dispatcher.UIThread.Post(() => tray.ToolTipText = UiLanguage.Message(trayLabel = "脉络 Edge · Antigravity 接入待核对"));
                    System.Diagnostics.Trace.TraceWarning("Antigravity: {0}", error.Message);
                }
            });
            var codex = new GlobalCodexAccess(Client);
            InitialSkillCheck = Task.Run(async () =>
            {
                try
                {
                    codex.RepairMissingCommand();
                    await codex.UpdateSkillsAsync(automatic: true);
                    var versions = codex.ReadSkills();
                    Dispatcher.UIThread.Post(() => ShowSkillsIndicator(versions));
                    return (string?)null;
                }
                catch (Exception error)
                {
                    Dispatcher.UIThread.Post(() => access.Header = UiLanguage.Message(accessLabel = "智能体接入 · Codex 设置待核对"));
                    return error.Message;
                }
            });
            var maintenance = Task.Run(() => MaintainConnectionsAsync(codex, stop.Token));
            desktop.Exit += (_, _) =>
            {
                systemPreferencesTimer.Stop();
                UiLanguage.Changed -= UpdateMenuLanguage;
                registration.Unregister(null);
                retireRegistration.Unregister(null);
                stop.Cancel();
                StopApplicationUpdates();
                tray.IsVisible = false;
                worker.GetAwaiter().GetResult();
                antigravity.GetAwaiter().GetResult();
                InitialSkillCheck.GetAwaiter().GetResult();
                maintenance.GetAwaiter().GetResult();
                // Let the current file write finish before the desktop process exits.
                if (PendingHarnessBatch is Task batch)
                    batch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
                stop.Dispose();
            };
            if (!Background) ShowMainWindow();
#endif
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static WindowIcon LoadTrayIcon(string name)
    {
        using var stream = AssetLoader.Open(new Uri("avares://ProjectHub.Edge.Desktop/Assets/" + name));
        return new WindowIcon(stream);
    }

    private static bool LightTaskbar()
    {
        // The Windows shell and applications can be assigned different themes.
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0) is int value && value != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private async Task MaintainConnectionsAsync(GlobalCodexAccess codex, CancellationToken ct)
    {
        var harnesses = new HarnessAccess(Client);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    harnesses.MigrateInstalledCommands(verifyWorkspaces: false);
                    var skills = codex.ReadSkills();
                    var skillsChanged = skills.AutoUpdate && skills.CanUpdate && await codex.UpdateSkillsAsync(automatic: true) > 0;
                    if (skillsChanged) skills = codex.ReadSkills();
                    Dispatcher.UIThread.Post(() =>
                    {
                        ShowSkillsIndicator(skills);
                        if (window != null) _ = window.RefreshAccessAfterMaintenanceAsync(skillsChanged);
                    });
                }
                catch (Exception e)
                {
                    System.Diagnostics.Trace.TraceWarning("智能体接入维护：{0}", e.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal void ShowSkillsIndicator(SkillInventory versions)
    {
        if (accessMenu != null)
            accessMenu.Header = UiLanguage.Message(accessLabel = versions.CanUpdate
                ? "智能体接入 · Skills 可更新" : "智能体接入");
    }

    private async Task KeepConnectedAsync(TrayIcon tray, CancellationToken ct)
    {
        try { await Client.RunAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(() => tray.ToolTipText = UiLanguage.Message(trayLabel = "脉络 Edge · 后台同步已停止，请重启客户端"));
        }
    }
}
