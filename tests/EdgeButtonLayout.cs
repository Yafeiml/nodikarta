#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProjectHub.Edge;
using ProjectHub.Edge.Desktop;
using SkiaSharp;

internal static class EdgeButtonLayout
{
    [STAThread]
    private static void Main()
    {
        AppBuilder.Configure<App>().UseWin32().UseSkia().SetupWithoutStarting();
        var state = Directory.CreateTempSubdirectory("projecthub-button-layout-");
        var home = state.CreateSubdirectory("profile").FullName;
        Environment.SetEnvironmentVariable("USERPROFILE", home);
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(home, ".codex"));
        var host = new Window { Width = 824, Height = 570, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-12000, -12000) };
        try
        {
            var project = new DeviceProject(Guid.NewGuid(), "示例项目", Guid.NewGuid(), "readOnly");
            var view = new AccessWindow(new EdgeDesktopClient(Path.Combine(state.FullName, "edge")), project, []);
            host.Content = view;
            view.FindControl<Grid>("CatalogPage")!.IsVisible = true;
            host.ApplyTemplate();
            view.FindControl<ScrollViewer>("ClientPage")!.IsVisible = false;
            host.Measure(new Size(824, 570));
            host.Arrange(new Rect(0, 0, 824, 570));
            VerifyWrap(view.FindControl<WrapPanel>("CategoryBar")!, "工具分类", 188, 3);
            var device = new AccessWindow(new EdgeDesktopClient(Path.Combine(state.FullName, "edge")), null, []);
            host.Content = device;
            host.Measure(new Size(824, 570));
            host.Arrange(new Rect(0, 0, 824, 570));
            var methods = (WrapPanel)device.FindControl<Button>("ClientsFormat")!.Parent!;
            VerifyWrap(methods, "智能体接入页签", 420, 1);
            VerifyWrap(methods, "智能体接入页签", 225, 2);
            VerifyWrap(methods, "智能体接入页签", 150, 3);
            host.Show();
            VerifyDeviceTabs(device, methods);
            VerifyHarnessDisconnect(host, device, state.FullName);
            VerifyOverviewStates(state.FullName, project);
        }
        finally
        {
            host.Close();
            state.Delete(true);
        }
    }

    private static void VerifyHarnessDisconnect(Window host, AccessWindow device, string stateDirectory)
    {
        var list = device.FindControl<ItemsControl>("HarnessRows")!;
        var previousRows = list.ItemsSource;
        var copyButton = list.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "HarnessCopy_codex");
        if (!copyButton.Focus(NavigationMethod.Tab)) throw new Exception("复制按钮没有获得焦点。");
        var refresh = device.RefreshAfterMaintenanceAsync();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!refresh.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        if (!refresh.IsCompletedSuccessfully || list.ItemsSource != previousRows || !copyButton.IsFocused)
            throw new Exception("后台维护刷新了相同的行或中断了键盘焦点。");
        var fixture = list.Items.Cast<HarnessListItem>().Select((item, i) =>
            item with { Client = item.Client with {
                ActionText = i == 0 ? "已接入" : i == 1 ? "待核对" : "接入",
                CanDisconnect = i == 0, CanInstall = i > 1
            } }).ToArray();
        list.ItemsSource = fixture;
        foreach (var (width, height, english) in new[] { (656, 552, true), (460, 410, true), (656, 552, false), (460, 410, false) })
        {
            UiLanguage.Save(stateDirectory, english);
            foreach (var row in fixture) row.RefreshLanguage();
            var size = new Size(width, height);
            host.Width = size.Width;
            host.Height = size.Height;
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            host.UpdateLayout();
            var buttons = list.GetVisualDescendants().OfType<Button>().ToDictionary(b => AutomationProperties.GetAutomationId(b)!);
            var statusX = new List<double>();
            var copyX = new List<double>();
            foreach (var item in fixture)
            {
                var id = item.Client.Id;
                var status = buttons["HarnessInstall_" + id];
                var copy = buttons["HarnessCopy_" + id];
                statusX.Add(status.TranslatePoint(default, list)!.Value.X);
                copyX.Add(copy.TranslatePoint(default, list)!.Value.X);
                if (buttons.Count != fixture.Length * 2 || status.IsEnabled != item.CanAct ||
                    !Equals(status.Content, item.Client.CanDisconnect ? UiLanguage.Text("断开接入", "Disconnect") : UiLanguage.Text("接入", "Connect")) ||
                    (item.Client.CanDisconnect && !item.Status.StartsWith(UiLanguage.Text("已接入", "Connected"), StringComparison.Ordinal)) ||
                    (status.IsEnabled && KeyboardNavigationHandler.GetNext(status, NavigationDirection.Next) != copy) ||
                    Math.Abs(copy.TranslatePoint(default, list)!.Value.X -
                        status.TranslatePoint(new Point(status.Bounds.Width, 0), list)!.Value.X - 8) > 1)
                    throw new Exception($"{size} {id} 的主操作、连接状态或键盘导航不符。");
            }
            if (statusX.Max() - statusX.Min() > 0.01 || copyX.Max() - copyX.Min() > 0.01)
                throw new Exception($"{size} 状态列 X={string.Join(",", statusX)}；复制列 X={string.Join(",", copyX)}。");
        }
        foreach (var confirm in new[] { false, true })
        {
            var dialog = new DisconnectHarnessDialog(fixture[0].Client) { ShowActivated = false };
            var result = dialog.ShowDialog<bool>(host);
            Dispatcher.UIThread.RunJobs();
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToDictionary(b => AutomationProperties.GetAutomationId(b)!);
            if (!buttons["CancelHarnessDisconnect"].IsFocused || !dialog.FindControl<TextBlock>("Note")!.Text!.Contains("Skills"))
                throw new Exception("断开确认窗需说明保留 Skills，并默认聚焦取消。");
            if (confirm) buttons["ConfirmHarnessDisconnect"].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            else buttons["CancelHarnessDisconnect"].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Dispatcher.UIThread.RunJobs();
            if (!result.IsCompletedSuccessfully || result.Result != confirm)
                throw new Exception("断开确认或 Esc 取消没有返回正确结果。");
        }
        Console.WriteLine("中英文的断开操作、状态和复制列对齐均通过，已验证默认及窄窗口、Tab 顺序、确认及 Esc 取消。");
    }

    private static void VerifyDeviceTabs(AccessWindow device, WrapPanel group)
    {
        var tabs = group.Children.Cast<Button>().ToArray();
        if (!tabs.Select(tab => AutomationProperties.GetAutomationId(tab)).SequenceEqual(
                ["ConfigFormatClients", "ConfigFormatStdio", "ConfigFormatSkills"]) ||
            !tabs.Select(tab => tab.Content as string).SequenceEqual(["常用客户端", "其他客户端 · STDIO", "通用 Skills"]))
            throw new Exception("智能体接入的三个页签名称或排列顺序不符。");
        Border[] panels = [device.FindControl<Border>("HarnessPanel")!, device.FindControl<Border>("StdioPanel")!,
            device.FindControl<Border>("SkillsHost")!];
        Ready();
        CheckSelected(tabs[0]);
        for (var i = 0; i < tabs.Length - 1; i++)
        {
            if (KeyboardNavigationHandler.GetNext(tabs[i], NavigationDirection.Next) != tabs[i + 1] ||
                KeyboardNavigationHandler.GetNext(tabs[i + 1], NavigationDirection.Previous) != tabs[i])
                throw new Exception("Tab 或 Shift+Tab 没有按显示顺序经过相邻页签。");
        }
        foreach (var tab in new[] { tabs[1], tabs[2], tabs[0] })
        {
            if (!tab.Focus(NavigationMethod.Tab) || !tab.IsFocused)
                throw new Exception($"{tab.Content} 没有获得键盘焦点。");
            tab.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Ready();
            CheckSelected(tab);
            foreach (var size in new[] { new Size(656, 552), new Size(460, 410) })
                VerifyDeviceCard(device, panels[Array.IndexOf(tabs, tab)], size);
            foreach (var width in new[] { 420, 150 })
            {
                group.Measure(new Size(width, double.PositiveInfinity));
                group.Arrange(new Rect(0, 0, width, group.DesiredSize.Height));
                foreach (var item in tabs) VerifyTabFace(item, item == tab);
                var neighbor = tabs[tab == tabs[0] ? 1 : Array.IndexOf(tabs, tab) - 1];
                VerifyPaintedGap(group, tab, neighbor);
            }
        }
        Console.WriteLine("三个页签的顺序、正反向键盘导航、焦点、Enter 激活与对应页面均通过。");

        void Ready()
        {
            var end = Environment.TickCount64 + 10000;
            do
            {
                Dispatcher.UIThread.RunJobs();
                if (tabs.All(tab => tab.IsEffectivelyEnabled)) return;
                Thread.Sleep(10);
            } while (Environment.TickCount64 < end);
            throw new Exception("等待页签就绪超时。");
        }
        void CheckSelected(Button selected)
        {
            for (var i = 0; i < tabs.Length; i++)
            {
                var current = tabs[i] == selected;
                if (tabs[i].Classes.Contains("current") != current ||
                    tabs[i].Background is not ISolidColorBrush brush || brush.Color != (current ? Color.Parse("#DBE9F8") : Colors.Transparent) ||
                    panels[i].IsEffectivelyVisible != current)
                    throw new Exception($"{selected.Content} 应为唯一选中页签，且只显示对应页面。");
            }
        }
    }

    private static void VerifyDeviceCard(AccessWindow device, Border card, Size size)
    {
        device.Measure(size);
        device.Arrange(new Rect(size));
        var page = device.FindControl<ScrollViewer>("DevicePage")!;
        var back = page.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "CloseGlobalAccessButton");
        var title = page.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == "DeviceAccessHeading");
        var backCenterRight = back.TranslatePoint(new Point(back.Bounds.Width, back.Bounds.Height / 2), device)!.Value;
        var titleCenterLeft = title.TranslatePoint(new Point(0, title.Bounds.Height / 2), device)!.Value;
        if (title.Text != "智能体接入" || backCenterRight.X >= titleCenterLeft.X || Math.Abs(backCenterRight.Y - titleCenterLeft.Y) > 1 ||
            !back.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(p => p.IsEffectivelyVisible && p.Bounds.Width > 0) ||
            back.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)) ||
            string.IsNullOrWhiteSpace(AutomationProperties.GetName(back)))
            throw new Exception($"{size} 返回按钮应仅显示图标、位于标题左侧并与标题居中对齐。");
        var isList = card.Name == "HarnessPanel";
        if (!card.IsEffectivelyVisible || card.Bounds.Width <= 0 ||
            card.Background is not ISolidColorBrush fill || fill.Color != Color.Parse(isList ? "#FFFFFF" : "#F5F8FC") ||
            card.BorderBrush is not ISolidColorBrush outline || outline.Color != Color.Parse(isList ? "#E5E5E5" : "#D5E1EB") ||
            card.BorderThickness != new Thickness(1) || card.CornerRadius != new CornerRadius(8) ||
            card.Padding != new Thickness(isList ? 0 : 16))
            throw new Exception($"{size} {card.Name} 的底色、描边、内边距或圆角不符。");
        if (isList)
        {
            var rows = card.GetVisualDescendants().OfType<Border>().Where(b =>
                AutomationProperties.GetAutomationId(b)?.StartsWith("HarnessRow_", StringComparison.Ordinal) == true).ToArray();
            if (AutomationProperties.GetAutomationId(card) != "HarnessPanel" || rows.Length != 7 ||
                rows.Any(row => Math.Abs(row.Bounds.Height - 52) > 0.01 ||
                    row.Background is ISolidColorBrush paint && paint.Color.A != 0 && paint.Color != fill.Color))
                throw new Exception($"{size} 七个客户端行须保持 52 DIP 高并显示卡片底色。");
            foreach (var row in rows)
            {
                var tile = row.GetVisualDescendants().OfType<Border>().Single(b =>
                    AutomationProperties.GetAutomationId(b)?.StartsWith("HarnessIconTile_", StringComparison.Ordinal) == true);
                if (tile.Bounds.Size != new Size(36, 36) || tile.CornerRadius != new CornerRadius(9) ||
                    tile.Background is not ISolidColorBrush tileFill || tileFill.Color != Color.Parse("#FAFAFA") ||
                    tile.BorderBrush is not ISolidColorBrush tileEdge || tileEdge.Color != Color.Parse("#EAEAEA") ||
                    tile.BorderThickness != new Thickness(1) || tile.Child is not Image icon || icon.Bounds.Size != new Size(24, 24))
                    throw new Exception($"{size} {AutomationProperties.GetAutomationId(row)} 的图标尺寸、圆角或配色不符。");
            }
        }
        Console.WriteLine($"{size} {card.Name} 卡面、边框、圆角及标题左侧的图标返回按钮通过。");
    }

    private static void VerifyTabFace(Button tab, bool selected)
    {
        var face = tab.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
        var label = face.GetVisualDescendants().OfType<TextBlock>().Single();
        var states = (IPseudoClasses)tab.Classes;
        var text = selected ? "#174D88" : "#2C4255";
        Check(selected ? "#DBE9F8" : "Transparent", text);
        states.Set(":pointerover", true);
        Check(selected ? "#D2E4F5" : "#EDF3F7", text);
        states.Set(":pressed", true);
        Check(selected ? "#C4DBF0" : "#DFE9F2", text);
        states.Set(":pressed", false);
        tab.IsEnabled = false;
        Check("#EDF1F5", "#657586");
        states.Set(":pointerover", false);
        Check("#EDF1F5", "#657586");
        tab.IsEnabled = true;

        void Check(string background, string foreground)
        {
            if (face.Background is not ISolidColorBrush fill || fill.Color != Color.Parse(background) ||
                face.Foreground is not ISolidColorBrush ink || ink.Color != Color.Parse(foreground) ||
                label.Foreground is not ISolidColorBrush labelInk || labelInk.Color != Color.Parse(foreground))
                throw new Exception($"{tab.Content} {string.Join(" ", tab.Classes)} 模板底色/文字颜色为 {face.Background}/{label.Foreground}，预期 {background}/{foreground}。");
        }
    }

    private static void VerifyPaintedGap(WrapPanel group, Button selected, Button neighbor)
    {
        const double scale = 1.5;
        var hover = (IPseudoClasses)neighbor.Classes;
        hover.Set(":pointerover", true);
        using var target = new RenderTargetBitmap(PixelSize.FromSize(group.Bounds.Size, scale), new Vector(144, 144));
        target.Render(group);
        hover.Set(":pointerover", false);
        using var png = new MemoryStream();
        target.Save(png);
        png.Position = 0;
        using var pixels = SKBitmap.Decode(png);

        var horizontal = selected.Bounds.Top == neighbor.Bounds.Top;
        var pair = new[] { selected, neighbor }.OrderBy(b => horizontal ? b.Bounds.Left : b.Bounds.Top).ToArray();
        var edge = (int)Math.Round(scale * (horizontal ? pair[0].Bounds.Right : pair[0].Bounds.Bottom));
        // Sample the flat portion of both edges, away from text and rounded corners.
        var cross = (int)(scale * ((horizontal ? pair[0].Bounds.Top : pair[0].Bounds.Left) + 8));
        var line = Enumerable.Range(edge - 4, 11)
            .Select(i => horizontal ? pixels.GetPixel(i, cross) : pixels.GetPixel(cross, i)).ToArray();
        var selectedFill = new SKColor(0xDB, 0xE9, 0xF8);
        var hoverFill = new SKColor(0xED, 0xF3, 0xF7);
        var gap = line.Count(color => color.Alpha < 128);
        if (line[0] != (pair[0] == selected ? selectedFill : hoverFill) ||
            line[^1] != (pair[1] == selected ? selectedFill : hoverFill) || gap != 3)
            throw new Exception($"页签渲染净距不符，{group.Bounds.Width} DIP 组宽，150% 缩放实测 {gap} 像素：{string.Join(" ", line)}。");
        Console.WriteLine($"{group.Bounds.Width} DIP 组宽，「{selected.Content}」选中、「{neighbor.Content}」悬停，150% 渲染实测{(horizontal ? "水平" : "垂直")}净距 {gap} 像素。");
    }

    private static void VerifyOverviewStates(string state, DeviceProject project)
    {
        var window = new MainWindow(new EdgeDesktopClient(Path.Combine(state, "edge")));
        try
        {
            window.FindControl<StackPanel>("Overview")!.IsVisible = true;
            window.FindControl<StackPanel>("BindingSection")!.IsVisible = true;
            var binding = new LocalBinding(project.ProjectId, project.BindingId, Guid.NewGuid(), state, "readOnly", project.Name);
            window.FindControl<ItemsControl>("BoundDirectories")!.ItemsSource = new[] { new DirectoryRow(binding) };
            window.ApplyTemplate();
            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            var page = (Control)window.Content!;
            foreach (var width in new[] { window.Width, 460 })
            {
                page.Measure(new Size(width, window.Height));
                page.Arrange(new Rect(0, 0, width, window.Height));
                VerifyDirectoryButtonAlignment(page, width);
            }
            var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("overviewAction")).ToArray();
            if (buttons.Length != 6) throw new Exception($"预期 6 种概览操作，实际 {buttons.Length}。");
            foreach (var button in buttons)
            {
                var id = AutomationProperties.GetAutomationId(button);
                var primary = id == "BrowseButton";
                var classes = (IPseudoClasses)button.Classes;
                var visuals = button.GetVisualDescendants().ToArray();
                var face = visuals.OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
                var labels = visuals.OfType<TextBlock>().ToArray();
                var icons = visuals.OfType<Avalonia.Controls.Shapes.Path>().ToArray();
                var expectsIcon = id is "BrowseButton" or "RefreshButton" or "OpenCoreButton" or "AccessSettingsButton" or "UnbindDirectoryButton";
                if (labels.Length == 0 || expectsIcon && icons.Length == 0)
                    throw new Exception($"{id} 的实际模板缺少预期的文字或图标。");
                Check(primary ? "#174E9D" : "#F4F7FA", primary ? "#FFFFFF" : "#2C4255");
                classes.Set(":pointerover", true);
                Check(primary ? "#123F82" : "#EDF3F7", primary ? "#FFFFFF" : "#2C4255");
                classes.Set(":pressed", true);
                Check(primary ? "#103667" : "#DFE9F2", primary ? "#FFFFFF" : "#2C4255");
                classes.Set(":pressed", false);
                button.IsEnabled = false;
                Check("#EDF1F5", "#657586");
                classes.Set(":pointerover", false);
                Check("#EDF1F5", "#657586");

                void Check(string background, string foreground)
                {
                    Expect(face.Background, background);
                    Expect(face.Foreground, foreground);
                    foreach (var label in labels) Expect(label.Foreground, foreground);
                    foreach (var icon in icons) Expect(icon.Stroke, foreground);
                }
                void Expect(IBrush? brush, string expected)
                {
                    if (brush is not ISolidColorBrush solid || solid.Color != Color.Parse(expected))
                        throw new Exception($"{id} {string.Join(" ",button.Classes)}：预期 {expected}，实际 {brush}。");
                }
            }
            Console.WriteLine("六种概览按钮的常态、悬停、按下、禁用及禁用悬停均通过。已核对实际模板底色、文字与图标。");
        }
        finally { window.Close(); }
    }

    private static void VerifyDirectoryButtonAlignment(Control page, double width)
    {
        if (Math.Abs(page.Bounds.Width - width) > 0.5) throw new Exception($"概览实际宽 {page.Bounds.Width} DIP，应为 {width}。");
        var browse = Measure("BrowseButton");
        var unbind = Measure("UnbindDirectoryButton");
        if (Math.Abs(browse.Left - unbind.Left) > 0.5 || Math.Abs(browse.Right - unbind.Right) > 0.5 ||
            Math.Abs(browse.IconCenter - unbind.IconCenter) > 0.5 || Math.Abs(browse.TextStart - unbind.TextStart) > 0.5)
            throw new Exception($"{width} DIP 窗口的按钮边缘、图标中心或文字起点不齐：添加目录 {browse}，解除绑定 {unbind}。");
        Console.WriteLine($"{width} DIP 窗口的两个目录操作齐边，图标中心 X={browse.IconCenter}，文字起点 X={browse.TextStart}。");

        (double Left, double Right, double IconCenter, double TextStart) Measure(string id)
        {
            var button = page.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == id);
            var icon = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
            var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
            if (label.Text != (id == "BrowseButton" ? "添加目录" : "解除绑定"))
                throw new Exception($"{width} DIP 窗口中的 {id} 按钮文字不符，实际为「{label.Text}」。");
            var stroke = icon.RenderedGeometry!.GetRenderBounds(new Pen(icon.Stroke, icon.StrokeThickness));
            var line = label.TextLayout.TextLines.Single();
            var left = X(button, 0);
            var right = X(button, button.Bounds.Width);
            var textStart = X(label, label.Padding.Left + line.Start);
            if (stroke.Width <= 0 || line.Width <= 0 || X(icon, stroke.Left) < left || X(icon, stroke.Right) >= textStart ||
                textStart + line.Width > right + 0.5 || left < 0 || right > width + 0.5)
                throw new Exception($"{width} DIP 窗口中的 {id} 内容越界、缺失或图标与文字重叠。");
            return (left, right, X(icon, stroke.Center.X), textStart);
        }
        double X(Visual visual, double localX) => visual.TranslatePoint(new Point(localX, 0), page)!.Value.X;
    }

    private static void VerifyWrap(WrapPanel group, string name, int width, int expectedRows)
    {
        group.Measure(new Size(width, double.PositiveInfinity));
        group.Arrange(new Rect(0, 0, width, group.DesiredSize.Height));
        var items = group.Children.Cast<Button>().Select(button => button.Bounds).ToArray();
        var rows = items.GroupBy(rect => rect.Top).OrderBy(row => row.Key).ToArray();
        if (items.Any(rect => rect.Width <= 0 || rect.Height <= 0 || rect.Right > width) ||
            rows.Length != expectedRows)
            throw new Exception($"{name}未复现 {expectedRows} 行布局：{string.Join("; ", items)}");
        foreach (var row in rows)
            foreach (var gap in row.Skip(1).Zip(row, (next, previous) => next.Left - previous.Right))
                ExpectGap(name, gap);
        foreach (var gap in rows.Skip(1).Zip(rows, (next, previous) => next.Key - previous.Max(rect => rect.Bottom)))
            ExpectGap(name, gap);
        Console.WriteLine($"{name}：{width} DIP 宽，{items.Length} 个按钮实际排为 {rows.Length} 行，相邻边缘净距为 2 DIP。");
    }

    private static void ExpectGap(string name, double gap)
    {
        if (Math.Abs(gap - 2) > 0.001) throw new Exception($"{name}预期 2 DIP 净距，实测 {gap} DIP。");
    }
}
