using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ProjectHub.Edge.Desktop;

internal sealed record AccessToolRow(string Name, string Title, string Description, string Access, string Scope, IBrush StatusBrush);

internal sealed record AccessChoice(string Source, Guid? Id = null, bool Translate = true) : LocalizedRow
{
    public string Label => Translate ? UiLanguage.Message(Source) : Source;
}

internal partial class AccessWindow : UserControl
{
    internal event Action? CloseRequested;
    private readonly LiveLabels labels = new();

    private static readonly (string Name, EdgeAccessProfile Profile)[] Profiles = [
        ("自动匹配", EdgeAccessProfile.Auto), ("项目知识", EdgeAccessProfile.Knowledge), ("共享素材", EdgeAccessProfile.Assets), ("反馈采集", EdgeAccessProfile.Feedback),
        ("任务处理", EdgeAccessProfile.Tasks), ("跟随项目授权", EdgeAccessProfile.FollowGrant)
    ];
    private readonly EdgeAccessSetup setup;
    private readonly DeviceProject? selectedProject;
    private readonly GlobalCodexAccess global;
    private readonly HarnessAccess harnesses;
    internal bool IsDevicePage => selectedProject is null;
    private readonly CancellationTokenSource lifetime = new();
    private EdgeAccessContext? context;
    private EdgeAccessConfig? preview;
    private bool showConfig;
    private string selectedCategory = EdgeToolCatalog.Categories[0];
    private enum DeviceTab { Clients, Stdio, Skills }
    private DeviceTab deviceTab = DeviceTab.Clients;
    private bool hasInstallableClients;
    private bool initialized;
    private bool busy;
    private bool updating;
    private bool adjustingScope;

    internal AccessWindow(EdgeDesktopClient client, DeviceProject? project, IReadOnlyList<LocalBinding> directories,
        HarnessAccess? harnessAccess = null)
    {
        setup = new(client);
        selectedProject = project;
        global = new(client);
        harnesses = harnessAccess ?? new(client);
        InitializeComponent();
        DirectoryTitle.IsVisible = DirectoryMinimizeButton.IsVisible = !IsDevicePage;
        ProjectName.Text = project?.Name;
        DevicePage.IsVisible = IsDevicePage;
        ClientPage.IsVisible = ProjectHeader.IsVisible = Rail.IsVisible = !IsDevicePage;
        DirectoryList.ItemsSource = directories;
        DirectoryList.SelectedIndex = 0;
        ProfileList.ItemsSource = Profiles.Select(p => new AccessChoice(p.Name)).ToArray();
        ProfileList.SelectedIndex = 0;
        ModeFilter.ItemsSource = new AccessChoice[] { new("全部读写类型"), new("读取工具"), new("写入工具") };
        EndpointFilter.ItemsSource = new AccessChoice[] { new("全部端点条件"), new("需要接收端点"), new("无需接收端点") };
        ModeFilter.SelectedIndex = EndpointFilter.SelectedIndex = 0;
        labels.Set(Grant, "正在核对授权");
        foreach (var category in EdgeToolCatalog.Categories)
        {
            var pill = new Button { Content = UiLanguage.Message(category), Tag = category };
            pill.Classes.Add("pill");
            pill.Classes.Set("current", category == selectedCategory);
            AutomationProperties.SetAutomationId(pill, "ToolGroup_" + category);
            AutomationProperties.SetName(pill, UiLanguage.Message(category));
            pill.Click += SelectCategory;
            CategoryBar.Children.Add(pill);
        }
        initialized = true;
        SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 715;
            Rail.Width = narrow ? 55 : 152;
            Rail.Padding = narrow ? new Thickness(6, 13, 6, 9) : new Thickness(10, 17, 10, 12);
            Body.Padding = e.NewSize.Height < 485 ? new Thickness(16, 9) :
                narrow ? new Thickness(16, 13) : new Thickness(25, 17);
            foreach (var button in new[] { ClientNav, CatalogNav, OverviewNav })
            {
                button.HorizontalContentAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Left;
                button.Padding = narrow ? new Thickness(0, 9) : new Thickness(13, 9);
                if (button.Content is StackPanel panel)
                    foreach (var label in panel.Children.OfType<TextBlock>()) label.IsVisible = !narrow;
            }
        };
        Loaded += async (_, _) =>
        {
            if (IsDevicePage)
            {
                var batch = (Application.Current as App)?.PendingHarnessBatch;
                await RunAsync(async ct =>
                {
                    if (batch != null)
                    {
                        if (!batch.IsCompleted) await RefreshHarnessRowsAsync(ct);
                        return await FollowHarnessBatchAsync(batch, ct);
                    }
                    await RefreshDeviceViewAsync();
                    return "";
                }, batch == null ? "正在检测本机智能体…" : "正在接入已安装的智能体…");
                if (!lifetime.IsCancellationRequested && !IsKeyboardFocusWithin) ClientsFormat.Focus();
            }
            else
            {
                ClientNav.Focus();
                await LoadDirectoryAsync();
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            lifetime.Cancel();
            if (!busy) lifetime.Dispose();
        };
        RenderOptions();
    }

    private EdgeAccessProfile Profile => Profiles[ProfileList.SelectedIndex].Profile;
    private Guid? EndpointId => (EndpointList.SelectedItem as AccessChoice)?.Id;

    internal void RefreshLanguage()
    {
        labels.Refresh();
        if (IsDevicePage)
        {
            foreach (var row in HarnessRows.Items.OfType<HarnessListItem>()) row.RefreshLanguage();
            ToggleConfigButton.Content = UiLanguage.Message(showConfig ? "收起配置" : "查看配置");
            RefreshSkillLabels();
            return;
        }
        foreach (var list in new[] { ProfileList, EndpointList, ModeFilter, EndpointFilter })
            foreach (var choice in list.Items.OfType<AccessChoice>()) choice.RefreshLanguage();
        foreach (var pill in CategoryBar.Children.OfType<Button>())
        {
            var category = UiLanguage.Message((string)pill.Tag!);
            pill.Content = category;
            AutomationProperties.SetName(pill, category);
        }
        ShowTools();
    }

    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    private void CloseWindow(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void HideWindow(object? sender, RoutedEventArgs e) => Host?.Close();
    private void Minimize(object? sender, RoutedEventArgs e)
    {
        if (!IsDevicePage && Host is { } window) window.WindowState = WindowState.Minimized;
    }
    private void DragTitle(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 1 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) Host?.BeginMoveDrag(e);
    }

    private void Navigate(object? sender, RoutedEventArgs e)
    {
        ClientPage.IsVisible = sender == ClientNav;
        CatalogPage.IsVisible = sender == CatalogNav;
        foreach (var nav in new[] { ClientNav, CatalogNav }) nav.Classes.Set("current", nav == sender);
        if (!busy) ShowResult("");
    }

    private void SelectCategory(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category } selected) return;
        selectedCategory = category;
        foreach (var button in CategoryBar.Children.OfType<Button>()) button.Classes.Set("current", button == selected);
        ShowTools();
        CatalogScroll.Offset = default;
    }

    private async void SwitchFormat(object? sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button tab) return;
        var next = tab == ClientsFormat ? DeviceTab.Clients : tab == StdioFormat ? DeviceTab.Stdio : DeviceTab.Skills;
        if (deviceTab == next) return;
        deviceTab = next;
        showConfig = false;
        ShowResult("");
        RenderOptions();
        DevicePage.Offset = default;
        if (next == DeviceTab.Stdio) return;
        await RunAsync(async _ => { await RefreshDeviceViewAsync(); return ""; },
            next == DeviceTab.Skills ? "正在检查本机 Skills…" : "正在检测本机智能体…");
        if (!lifetime.IsCancellationRequested && !IsKeyboardFocusWithin) tab.Focus();
    }

    private void ToggleConfig(object? sender, RoutedEventArgs e)
    {
        showConfig = !showConfig;
        RenderOptions();
    }

    private async void RefreshAccess(object? sender, RoutedEventArgs e) => await LoadDirectoryAsync();
    private async void DirectoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!initialized || busy) return;
        updating = true;
        ProfileList.SelectedIndex = 0;
        adjustingScope = false;
        updating = false;
        await LoadDirectoryAsync();
    }

    private void OptionsChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updating || busy) return;
        if (sender == ProfileList) adjustingScope = false;
        ShowResult("");
        RenderOptions();
    }

    private void AdjustScope(object? sender, RoutedEventArgs e)
    {
        adjustingScope = !adjustingScope;
        RenderOptions();
        if (adjustingScope) ProfileList.Focus();
    }

    private void FilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initialized) { ShowTools(); CatalogScroll.Offset = default; }
    }

    private void ShowResult(ViewMessage text, bool error = false)
    {
        labels.Set(Outcome, text.Render);
        OutcomeBar.IsVisible = !string.IsNullOrEmpty(Outcome.Text);
        Outcome.Foreground = Brush.Parse(error ? "#92272D" : "#355068");
    }

    private async Task LoadDirectoryAsync()
    {
        await RunAsync(async ct =>
        {
            await ReloadContextAsync(ct);
            return "";
        }, "正在核对项目授权和接收端点…");
    }

    private async Task ReloadContextAsync(CancellationToken ct, bool keepSelection = false)
    {
        if (DirectoryList.SelectedItem is not LocalBinding directory)
            throw new InvalidOperationException("请先选择已绑定的本机目录。");
        var previous = EndpointId;
        context = null;
        RenderOptions();
        var current = await setup.LoadAsync(selectedProject ?? throw new InvalidOperationException("请选择已授权项目。"), directory, ct);
        ct.ThrowIfCancellationRequested();
        updating = true;
        try
        {
            context = current;
            ProjectName.Text = current.Project.Name;
            var choices = new List<AccessChoice> { new("自动匹配") };
            choices.AddRange(current.Endpoints.Select(e => new AccessChoice(e.Name, e.Id, Translate: false)));
            var preferred = keepSelection ? previous : current.ConfiguredEndpoint;
            if (preferred.HasValue && choices.All(e => e.Id != preferred))
                choices.Add(new("原端点已失效", preferred));
            EndpointList.ItemsSource = choices;
            EndpointList.SelectedItem = choices.First(e => e.Id == preferred);
            GrantBadge.Classes.Set("confirmed", true);
            labels.Set(Grant, current.Project.AccessMode == "readWrite" ? "项目读写" : "项目只读");
        }
        finally { updating = false; }
    }

    private void RenderOptions()
    {
        if (IsDevicePage)
        {
            HarnessPanel.IsVisible = InstallDetectedButton.IsVisible = deviceTab == DeviceTab.Clients;
            StdioPanel.IsVisible = deviceTab == DeviceTab.Stdio;
            SkillsHost.IsVisible = deviceTab == DeviceTab.Skills;
            ClientsFormat.Classes.Set("current", HarnessPanel.IsVisible);
            StdioFormat.Classes.Set("current", StdioPanel.IsVisible);
            SkillsFormat.Classes.Set("current", SkillsHost.IsVisible);
            ConfigText.Text = showConfig ? setup.GlobalStdioJson() : "";
            DeviceConfigPanel.IsVisible = showConfig;
            ToggleConfigButton.Content = showConfig ? UiLanguage.Message("收起配置") : UiLanguage.Message("查看配置");
            CopyConfigButton.IsEnabled = ToggleConfigButton.IsEnabled = !busy;
            ClientsFormat.IsEnabled = StdioFormat.IsEnabled = SkillsFormat.IsEnabled = HarnessPanel.IsEnabled = SkillsHost.IsEnabled = !busy;
            InstallDetectedButton.IsEnabled = !busy && hasInstallableClients;
            return;
        }
        preview = null;
        if (context == null)
        {
            GrantBadge.Classes.Set("confirmed", false);
            labels.Set(Grant, "授权待确认");
            labels.Set(ConfigHint, "请先刷新并确认此项目的授权。");
        }
        else
        {
            try
            {
                preview = setup.Build(context with { ConfiguredEndpoint = EndpointId }, Profile, SelectedEndpoint());
                var writes = EdgeToolCatalog.Tools.Count(tool => tool.RequiresWrite && preview.Tools.Contains(tool.Name));
                var reads = preview.Tools.Length - writes;
                var scope = Profile;
                var current = context;
                var endpoint = SelectedEndpoint();
                var route = current.Endpoints.FirstOrDefault(e => e.Id == endpoint);
                labels.Set(ConfigHint, () =>
                {
                    var reason = scope switch
                    {
                        EdgeAccessProfile.Auto => UiLanguage.Message(current.AutoExplanation) + " ",
                        EdgeAccessProfile.FollowGrant => UiLanguage.Message("每次连接时按当前授权匹配。 "),
                        _ => ""
                    };
                    return reason + UiLanguage.Format("{0} 个读取工具 · {1} 个写入工具", reads, writes)
                        + (route is null ? "" : UiLanguage.Format(" · 端点 {0}", route.Name));
                });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                labels.Set(ConfigHint, ex.Message);
            }
        }
        var available = !busy && preview != null;
        SaveButton.IsEnabled = CheckButton.IsEnabled = available;
        RefreshAccessButton.IsEnabled = DirectoryList.IsEnabled = !busy;
        ProfileList.IsEnabled = !busy && context != null;
        AdjustScopeButton.IsEnabled = ProfileList.IsEnabled;
        labels.Set(ScopeAction, adjustingScope ? "收起范围" : "调整范围");
        var (scopeName, selectedScope) = Profiles[ProfileList.SelectedIndex];
        labels.Set(ScopeSummary, () => selectedScope is EdgeAccessProfile.Auto or EdgeAccessProfile.FollowGrant
            ? UiLanguage.Message(scopeName) : UiLanguage.Format("手动范围 · {0}", UiLanguage.Message(scopeName)));
        var taskScope = Profile == EdgeAccessProfile.Tasks
            || (Profile == EdgeAccessProfile.FollowGrant || Profile == EdgeAccessProfile.Auto && context?.AutoUsesEndpoints == true)
                && (context is { Endpoints.Count: > 1 } || context?.ConfiguredEndpoint != null);
        ProfileGrid.IsVisible = adjustingScope || taskScope;
        ProfileField.IsVisible = adjustingScope;
        EndpointField.IsVisible = taskScope;
        Grid.SetColumnSpan(ProfileField, taskScope ? 1 : 2);
        Grid.SetColumn(EndpointField, adjustingScope ? 1 : 0);
        Grid.SetColumnSpan(EndpointField, adjustingScope ? 1 : 2);
        EndpointList.IsEnabled = !busy && (context is { Endpoints.Count: > 0 } || context?.ConfiguredEndpoint != null);
        ShowTools();
    }

    private static string FriendlyToolName(string name) => name switch
    {
        "list_projects" => UiLanguage.Message("列出授权项目"),
        "get_project_snapshot" => UiLanguage.Message("读取项目快照"),
        "search_project_knowledge" => UiLanguage.Message("搜索项目知识"),
        "get_knowledge_history" => UiLanguage.Message("查看知识历史"),
        "submit_knowledge_commit" => UiLanguage.Message("提交知识变更"),
        "list_assets" => UiLanguage.Message("搜索共享素材"),
        "get_asset" => UiLanguage.Message("查看素材详情"),
        "get_asset_decisions" => UiLanguage.Message("查看此版交付物的决定"),
        "download_asset" => UiLanguage.Message("下载素材原件"),
        "publish_asset" => UiLanguage.Message("发布项目素材"),
        "list_feedback" => UiLanguage.Message("列出反馈"),
        "get_feedback" => UiLanguage.Message("查看反馈详情"),
        "submit_feedback" => UiLanguage.Message("提交反馈"),
        "list_tasks" => UiLanguage.Message("查看接收任务"),
        "claim_analysis_task" => UiLanguage.Message("领取分析任务"),
        "submit_task_result" => UiLanguage.Message("回传任务结果"),
        "list_agent_endpoints" => UiLanguage.Message("列出接收端点"),
        _ => name
    };

    private void ShowTools()
    {
        var all = EdgeToolCatalog.Tools.Where(t => t.Category == selectedCategory).ToArray();
        var visible = all.Where(t => ModeFilter.SelectedIndex switch { 1 => !t.RequiresWrite, 2 => t.RequiresWrite, _ => true })
            .Where(t => EndpointFilter.SelectedIndex switch { 1 => t.RequiresEndpoint, 2 => !t.RequiresEndpoint, _ => true })
            .Select(t => new AccessToolRow(t.Name, FriendlyToolName(t.Name), UiLanguage.Message(t.Description),
                (t.RequiresWrite ? UiLanguage.Message("写入") : UiLanguage.Message("读取")) + (t.RequiresEndpoint ? UiLanguage.Message(" · 需端点") : ""),
                DescribeScope(t), preview?.Tools.Contains(t.Name) == true ? EnabledDot : NeutralDot)).ToArray();
        ToolRows.ItemsSource = visible;
        CatalogCount.Text = UiLanguage.Format("当前分类 {0} 项 / 共 {1} 项", visible.Length, EdgeToolCatalog.Tools.Count);
        CatalogEmpty.IsVisible = visible.Length == 0;
        CatalogEmpty.Text = all.Length == 0 ? UiLanguage.Message("此分类尚未注册工具。") : UiLanguage.Message("当前筛选没有匹配工具。");
    }

    private string DescribeScope(EdgeToolInfo tool)
    {
        if (context == null) return UiLanguage.Message("授权待确认");
        if (tool.RequiresWrite && context.Project.AccessMode != "readWrite") return UiLanguage.Message("项目只读");
        if (tool.RequiresEndpoint && SelectedEndpoint() == null) return UiLanguage.Message("未指定端点");
        return preview?.Tools.Contains(tool.Name, StringComparer.Ordinal) == true ? UiLanguage.Message("已选用") : UiLanguage.Message("未选用");
    }

    private static readonly IBrush EnabledDot = Brush.Parse("#32816B");
    private static readonly IBrush NeutralDot = Brush.Parse("#8293A1");

    private async Task RunAsync(Func<CancellationToken, Task<ViewMessage>> action, ViewMessage progress)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        ++harnessRefreshVersion;
        busy = true;
        RenderOptions();
        ShowResult(progress);
        try
        {
            var result = await action(lifetime.Token);
            if (!lifetime.IsCancellationRequested) ShowResult(result);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var message = ex switch
            {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized } => "授权已变化。请在 Core 中核对设备和项目授权，再刷新。",
                HttpRequestException or OperationCanceledException => "无法连接 Core 或操作超时，请检查连接再重试。",
                ArgumentException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException => ex.Message,
                _ when IsDevicePage => "操作未完成，请核对本机客户端配置后重试。",
                _ => "操作未完成，请刷新授权后重试。"
            };
            ShowResult(message, error: true);
        }
        finally
        {
            busy = false;
            if (lifetime.IsCancellationRequested) lifetime.Dispose();
            else RenderOptions();
        }
    }

    private async void CopyConfig(object? sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        await WriteClipboardAsync(setup.GlobalStdioJson());
        return "已复制全局 STDIO 配置。";
    }, "正在复制配置…");

    private Guid? SelectedEndpoint() => Profile switch
    {
        EdgeAccessProfile.Tasks or EdgeAccessProfile.FollowGrant when context != null =>
            EndpointId ?? EdgeToolCatalog.MatchEndpoint(context.Endpoints, null),
        EdgeAccessProfile.Auto when context?.AutoUsesEndpoints == true =>
            EndpointId ?? EdgeToolCatalog.MatchEndpoint(context.Endpoints, null),
        _ => null
    };

    private async void SavePolicy(object? sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var endpoint = Profile switch
        {
            EdgeAccessProfile.Tasks => SelectedEndpoint(),
            EdgeAccessProfile.Auto or EdgeAccessProfile.FollowGrant => EndpointId,
            _ => null
        };
        var result = await setup.SavePolicyAsync(context!, Profile, endpoint, ct);
        updating = true;
        ProfileList.SelectedIndex = 0;
        updating = false;
        adjustingScope = false;
        await ReloadContextAsync(ct);
        return result;
    }, "正在保存此目录的工具范围和端点…");

    private async void Probe(object? sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        await ReloadContextAsync(ct, keepSelection: true);
        return await setup.ProbeAsync(context! with { ConfiguredEndpoint = EndpointId }, Profile, SelectedEndpoint(), ct);
    }, "正在启动 MCP 并执行只读自检…");
}
