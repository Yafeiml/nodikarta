using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ProjectHub.Edge.Desktop;

internal sealed record DirectoryRow(LocalBinding Binding) : LocalizedRow
{
    public string UnlinkName => UiLanguage.Text($"解除绑定 {Binding.LocalPath}", $"Unlink {Binding.LocalPath}");
    public string FolderName => new DirectoryInfo(Binding.LocalPath).Name;
}

internal partial class MainWindow : Window
{
    private readonly EdgeDesktopClient client;
    private readonly LiveLabels labels = new();
    private DeviceIdentity? identity;
    private IReadOnlyList<LocalBinding> bindings = [];
    private bool busy;
    private AccessWindow? accessWindow;
    private readonly Control overviewPage;
    private readonly Size preferredSize;
    private (bool Device, Guid? ProjectId)? pendingAccess;
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DateTimeOffset nextCoreCheck;

    public MainWindow(EdgeDesktopClient client)
    {
        this.client = client;
        InitializeComponent();
        preferredSize = new(Width, Height);
        FitToWorkingArea();
        PositionChanged += (_, _) => FitToWorkingArea();
        ScalingChanged += (_, _) => FitToWorkingArea();
        Screens.Changed += ScreenChanged;
        Win32Properties.AddWndProcHookCallback(this, FilterWindowCommands);
        DesktopVersion.Text = $"v{Program.ReleaseVersion}";
        overviewPage = PageHost.Child!;
        DeviceName.Text = Environment.MachineName;
        PairingCode.TextChanged += (_, _) => UpdatePairAction();
        UpdatePairAction();
        statusTimer.Tick += (_, _) =>
        {
            RefreshTransportStatus();
            if (accessWindow is null && !busy && !ProjectList.IsDropDownOpen && DateTimeOffset.UtcNow >= nextCoreCheck)
                _ = RunAsync(LoadAsync);
        };
        UiLanguage.Changed += RefreshLanguage;
        Closed += (_, _) =>
        {
            statusTimer.Stop();
            UiLanguage.Changed -= RefreshLanguage;
            Screens.Changed -= ScreenChanged;
        };
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty || change.Property == WindowStateProperty)
            {
                if (IsVisible && WindowState != WindowState.Minimized) statusTimer.Start();
                else statusTimer.Stop();
            }
            if (change.Property == IsVisibleProperty && IsVisible && accessWindow is null)
                _ = RunAsync(LoadAsync);
        };
    }

    private void OpenLanguageMenu(object? sender, RoutedEventArgs e)
    {
        LanguageButton.ContextMenu!.Open(LanguageButton);
        e.Handled = true;
    }

    private async void OpenAppPreferences(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            await new AppPreferencesWindow(DesktopStartup.ForCurrentUser(client.StateDirectory), app).ShowDialog(this);
    }

    private void ShowLanguagePreference(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var preference = UiLanguage.FollowsSystem ? "auto" : UiLanguage.IsEnglish ? "en" : "zh-CN";
        foreach (var option in menu.Items.OfType<MenuItem>())
            option.IsChecked = Equals(option.Tag, preference);
    }

    private void ChangeLanguage(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string choice }) return;
        try
        {
            if (choice == "auto") UiLanguage.FollowSystem(client.StateDirectory);
            else UiLanguage.Save(client.StateDirectory, choice == "en");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            labels.Set(NoticeText, "语言设置未能保存。");
            Notice.IsVisible = true;
        }
    }

    private void HideWindow(object? sender, RoutedEventArgs e) => Close();

    private void RefreshLanguage()
    {
        Title = accessWindow is null ? UiLanguage.Product : UiLanguage.Message(accessWindow.IsDevicePage
            ? "脉络 Edge · 智能体接入" : "脉络 Edge · 目录设置");
        UpdatePairAction();
        labels.Refresh();
        foreach (var row in BoundDirectories.Items.OfType<DirectoryRow>()) row.RefreshLanguage();
        accessWindow?.RefreshLanguage();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || accessWindow is null) return;
        if (e.Key == Key.Escape || (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt))
        {
            e.Handled = true;
            ReturnToOverview();
        }
    }

    private void DragTitle(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 1 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void ScreenChanged(object? sender, EventArgs e) => FitToWorkingArea();

    private void FitToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0) return;
        var available = screen.WorkingArea.Size.ToSize(screen.Scaling);
        var width = Math.Min(preferredSize.Width, Math.Floor(available.Width));
        var height = Math.Min(preferredSize.Height, Math.Floor(available.Height));
        if (MaxWidth != width) Width = MaxWidth = width;
        if (MaxHeight != height) Height = MaxHeight = height;
    }

    private nint FilterWindowCommands(nint hwnd, uint message, nint wParam, nint lParam, ref bool handled)
    {
        const uint wmSysCommand = 0x0112, wmNcLButtonDoubleClick = 0x00A3;
        const int scSize = 0xF000, scMinimize = 0xF020, scMaximize = 0xF030, htCaption = 2;
        if (message == wmSysCommand)
        {
            var command = wParam.ToInt64() & 0xFFF0;
            if (command is scSize or scMaximize || (command == scMinimize && !CanMinimize))
                handled = true;
        }
        else if (message == wmNcLButtonDoubleClick && wParam == htCaption) handled = true;
        return 0;
    }

    private void ShowPage(Control page)
    {
        CanMinimize = page is not AccessWindow { IsDevicePage: true };
        PageHost.Child = page;
    }

    private void SetCoreStatus(string text, string fill, string foreground, params object?[] values)
    {
        labels.Set(CoreApiStatus, text, values);
        CoreApiStatus.Foreground = Brush.Parse(foreground);
        CoreStatusBadge.Background = Brush.Parse(fill);
    }

    private void DisplayProjects(IReadOnlyList<DeviceProject> projects, IReadOnlyList<LocalBinding> directories, string emptyMessage)
    {
        var previous = (ProjectList.SelectedItem as DeviceProject)?.ProjectId;
        bindings = directories;
        ProjectList.ItemsSource = projects;
        ProjectList.SelectedItem = projects.FirstOrDefault(p => p.ProjectId == previous) ?? projects.FirstOrDefault();
        GrantControls.IsVisible = projects.Count > 0;
        NoProjects.IsVisible = projects.Count == 0;
        labels.Set(ProjectHint, emptyMessage);
        ShowDirectories();
    }

    private void ShowDirectories()
    {
        var project = ProjectList.SelectedItem as DeviceProject;
        BindingSection.IsVisible = project != null;
        if (project == null)
        {
            BoundDirectories.ItemsSource = null;
            return;
        }
        labels.Set(AccessMode, project.AccessMode switch { "readOnly" => "只读", "readWrite" => "读写", _ => "权限未知" });
        var local = bindings.Where(b => b.ProjectId == project.ProjectId).ToList();
        labels.Set(BindingCount, local.Count == 1 ? "1 个目录" : "{0} 个目录", local.Count);
        BoundDirectories.ItemsSource = local.Select(b => new DirectoryRow(b)).ToArray();
        EmptyBindings.IsVisible = local.Count == 0;
    }

    private void SelectedProjectChanged(object? sender, SelectionChangedEventArgs e)
    {
        // DisplayProjects restores the selection before rendering, so a refresh keeps keyboard focus.
        if (!busy) ShowDirectories();
    }

    private void RefreshTransportStatus()
    {
        if (accessWindow != null || identity is null) return;
        var listeners = client.ReadMcpListeners();
        var processes = listeners?.Count(s => s.Port is null);
        labels.Set(McpStdioStatus, processes switch
        {
            null => "进程状态未知 · 无端口",
            0 => "按需启动 · 无端口",
            1 => "1 个进程 · 无端口",
            _ => "{0} 个进程 · 无端口"
        }, processes);
    }

    private async Task LoadAsync()
    {
        var resuming = client.Session.HasPendingDeviceUpdate;
        nextCoreCheck = DateTimeOffset.UtcNow.AddSeconds(resuming ? 3 : 60);
        identity = client.GetIdentity();
        PairPanel.IsVisible = identity == null;
        Overview.IsVisible = identity != null;
        if (identity == null)
        {
            labels.Set(Message, resuming ? "设备接替等待 Core 确认。连接恢复后 Edge 会自动继续，也可用同一码重试。" : "关闭窗口后继续在托盘等待配对");
            return;
        }
        MachineName.Text = client.GetDeviceName(identity.DeviceId);
        ToolTip.SetTip(MachineName, MachineName.Text);
        RefreshTransportStatus();
        SetCoreStatus("连接中", "#EDF1F6", "#52647A");
        DesktopSnapshot snapshot;
        try
        {
            snapshot = await client.RefreshAsync();
            if (snapshot.Identity != identity)
                throw new InvalidOperationException("本机设备身份已变化，请重新刷新。");
        }
        catch (Exception ex) when (ex is not (HttpRequestException or OperationCanceledException))
        {
            SetCoreStatus("状态未知", "#EDF1F6", "#52647A");
            throw;
        }
        SetCoreStatus("在线", "#E1F4E9", "#176346");
        DisplayProjects(snapshot.Projects, snapshot.Bindings, "暂无授权项目。请管理员在 Core 中授权后刷新。");
        labels.Set(Message, "关闭窗口后继续在托盘运行");
    }

    private async Task RunAsync(Func<Task> action, Action? onError = null)
    {
        if (busy) return;
        busy = true;
        LanguageButton.IsEnabled = RefreshButton.IsEnabled = PairButton.IsEnabled = BrowseButton.IsEnabled = BoundDirectories.IsEnabled = ProjectList.IsEnabled = false;
        Notice.IsVisible = false;
        try { await action(); }
        catch (Exception ex)
        {
            string message;
            if (ex is HttpRequestException or OperationCanceledException)
            {
                var (status, detail) = ex switch
                {
                    HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
                        ("未授权", "Core 未接受设备身份，请在 Core 中核对该设备的配对状态。"),
                    HttpRequestException { StatusCode: not null } =>
                        ("服务异常 · HTTP {0}", "Core 返回错误，请检查服务状态后重试。"),
                    _ => ("无法连接", "无法连接 Core，请核对服务地址和网络后重试。")
                };
                message = detail;
                if (identity != null)
                {
                    SetCoreStatus(status, "#FDF1DB", "#805012", (int?)(ex as HttpRequestException)?.StatusCode);
                    DisplayProjects([], [], "重新连接后将显示当前授权项目。");
                }
            }
            else if (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                message = ex.Message;
            else if (ex is JsonException)
                message = "Core 返回了无法识别的数据，请检查客户端与 Core 的版本。";
            else
                message = "操作未完成，请重试或检查本机配置。";
            labels.Set(NoticeText, message);
            Notice.IsVisible = true;
            onError?.Invoke();
        }
        finally
        {
            LanguageButton.IsEnabled = RefreshButton.IsEnabled = PairButton.IsEnabled = BrowseButton.IsEnabled = BoundDirectories.IsEnabled = ProjectList.IsEnabled = true;
            busy = false;
            if (pendingAccess.HasValue)
                Dispatcher.UIThread.Post(OpenPendingAccess);
        }
    }

    private async void Refresh(object? sender, RoutedEventArgs e) => await RunAsync(LoadAsync);

    private void ShowAccessSettings(object? sender, RoutedEventArgs e) => OpenAccessSettings(false);

    private void ShowGlobalSettings(object? sender, RoutedEventArgs e) => OpenAccessSettings();

    internal void Reveal()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        FitToWorkingArea();
        if (!IsVisible) Show();
        if (!IsActive) Activate();
    }

    internal Task RefreshAccessAfterMaintenanceAsync(bool skillsChanged) => accessWindow == null ? Task.CompletedTask :
        skillsChanged ? accessWindow.RefreshAfterCodexRepairAsync() : accessWindow.RefreshAfterMaintenanceAsync();

    internal void OpenAccessSettings(bool device = true)
    {
        pendingAccess = (device, (ProjectList.SelectedItem as DeviceProject)?.ProjectId);
        OpenPendingAccess();
    }

    private void OpenPendingAccess()
    {
        if (busy || pendingAccess is not { } request) return;
        if (accessWindow?.IsDevicePage == request.Device)
        {
            pendingAccess = null;
            Reveal();
            return;
        }
        var needsReveal = !IsVisible || !IsActive || WindowState == WindowState.Minimized;
        _ = RunAsync(async () =>
        {
            var paired = client.GetIdentity() != null;
            if (!paired || !request.Device) await LoadAsync();
            if (pendingAccess != request) return;
            if (!paired)
                throw new InvalidOperationException("请先在此窗口完成设备配对，再接入 AI 工具。");
            AccessWindow view;
            if (request.Device) view = new AccessWindow(client, null, []);
            else
            {
                if (ProjectList.SelectedItem is not DeviceProject project || project.ProjectId != request.ProjectId)
                    throw new InvalidOperationException("原项目授权已变化，请重新选择要设置的项目。");
                var local = bindings.Where(b => b.ProjectId == project.ProjectId && b.BindingId == project.BindingId).ToArray();
                if (local.Length == 0)
                    throw new InvalidOperationException("请先点击「添加目录」，绑定此项目的本机目录。");
                view = new AccessWindow(client, project, local);
            }
            if (accessWindow is { } previous) previous.CloseRequested -= ReturnToOverview;
            view.CloseRequested += ReturnToOverview;
            accessWindow = view;
            Title = request.Device ? UiLanguage.Message("脉络 Edge · 智能体接入") : UiLanguage.Message("脉络 Edge · 目录设置");
            ShowPage(view);
            if (needsReveal) Reveal();
            pendingAccess = null;
        }, onError: () =>
        {
            if (pendingAccess != request) return;
            ReturnToOverview();
            if (needsReveal) Reveal();
        });
    }

    internal void ReturnToOverview()
    {
        pendingAccess = null;
        if (accessWindow is null) return;
        var device = accessWindow.IsDevicePage;
        accessWindow.CloseRequested -= ReturnToOverview;
        accessWindow = null;
        ShowPage(overviewPage);
        Title = UiLanguage.Message("脉络 Edge");
        if (IsVisible)
        {
            (device ? GlobalAccessButton : AccessSettingsButton).Focus();
            _ = RunAsync(LoadAsync);
        }
    }

    private void UpdatePairAction()
    {
        var update = EdgeSession.IsUpdateCode(PairingCode.Text);
        PairButton.Content = UiLanguage.Message(update ? "确认接替" : "配对设备");
        labels.Set(PairHint, update ? "确认后，此 Edge 将接替原设备。旧身份随即停用，项目授权和历史记录保留。"
            : "在 Core 的「设备管理」中选择「添加设备」或「更新设备」生成配对码。");
    }

    private async void Pair(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(DeviceName.Text)) throw new ArgumentException("请填写设备名称。");
        if (string.IsNullOrWhiteSpace(PairingCode.Text)) throw new ArgumentException("请粘贴 Core 生成的一次性配对码。");
        await client.PairAsync(CoreAddress.Text ?? "", PairingCode.Text, DeviceName.Text);
        PairingCode.Text = "";
        await LoadAsync();
    });

    private async void ChooseFolder(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (ProjectList.SelectedItem is not DeviceProject project || identity is not { } original) return;
        var chosen = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiLanguage.Text($"为「{project.Name}」选择本机目录", $"Choose a local folder for {project.Name}"), AllowMultiple = false
        });
        if (chosen.Count == 0) return;
        using var folder = chosen[0];
        var path = folder.TryGetLocalPath() ?? throw new ArgumentException("请选择本机可访问的目录。");
        try { await client.SessionFor(original).BindAsync(project.ProjectId, path); }
        finally { await LoadAsync(); }
        labels.Set(Message, "已绑定所选目录");
    });

    private async void RemoveDirectory(object? sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { DataContext: DirectoryRow row } button || identity is not { } original) return;
        var binding = row.Binding;
        var confirmed = false;
        await RunAsync(async () =>
        {
            confirmed = await new UnbindDialog(binding).ShowDialog<bool>(this);
            if (!confirmed) return;
            var session = client.SessionFor(original);
            var projects = ProjectList.Items.OfType<DeviceProject>().ToArray();
            try
            {
                await Task.Run(() => session.Unbind(binding.ProjectId, binding.WorkspaceId, binding.LocalPath));
                labels.Set(Message, "已解除目录绑定");
            }
            finally
            {
                bindings = session.ReadBindings(original.DeviceId, projects);
                ShowDirectories();
            }
        });
        (confirmed ? BrowseButton : button).Focus();
    }

    private void OpenCore(object? sender, RoutedEventArgs e)
    {
        try
        {
            var core = identity?.CoreUrl ?? EdgeDesktopClient.ParseCoreAddress(CoreAddress.Text ?? "");
            var target = new Uri(core, "/devices");
            Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            labels.Set(NoticeText, ex is ArgumentException ? ex.Message : "无法打开默认浏览器。");
            Notice.IsVisible = true;
        }
    }
}
