#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProjectHub.Edge;
using ProjectHub.Edge.Desktop;
using SkiaSharp;

internal static class EdgeWindowFrame
{
    [STAThread]
    private static void Main()
    {
        var state = Directory.CreateTempSubdirectory("projecthub-frame-");
        var home = state.CreateSubdirectory("profile").FullName;
        Environment.SetEnvironmentVariable("USERPROFILE", home);
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(home, ".codex"));
        AppBuilder.Configure<App>().UseWin32().With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .UseSkia().SetupWithoutStarting();
        var client = new EdgeDesktopClient(Path.Combine(state.FullName, "edge"));
        var main = new MainWindow(client) { ShowActivated = false, ShowInTaskbar = false };
        var binding = new LocalBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), state.FullName, "readOnly", "边框验收");
        var dialog = new UnbindDialog(binding) { ShowActivated = false };
        try
        {
            main.Show();
            var windows10 = !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
            var frame = CheckFrame(main, windows10);
            if (!windows10)
            {
                _ = dialog.ShowDialog(main);
                var initialDialogFrame = CheckFrame(dialog, false);
                var style = AvaloniaXamlLoader.Load(new Uri("avares://ProjectHub.Edge.Desktop/Windows10WindowFrame.axaml"));
                Application.Current!.Styles.Add((Style)style);
                CheckFrame(dialog, true, initialDialogFrame);
                dialog.Close();
                dialog = new UnbindDialog(binding) { ShowActivated = false };
            }
            CheckFrame(main, true, frame);
            var host = main.FindControl<Decorator>("PageHost")!;
            var overview = host.Child;
            host.Child = new AccessWindow(client, null, []);
            CheckFrame(main, true, frame);
            var handle = main.TryGetPlatformHandle()!.Handle;
            if (main.CanResize || main.CanMaximize) throw new Exception("主窗口仍允许手动调整大小或最大化。");
            // Set the isolated HWND's viewport directly, just as the screenshot fixtures do.
            var constrained = PixelSize.FromSize(new Size(506, 540), main.RenderScaling);
            if (!SetWindowPos(handle, 0, 0, 0, constrained.Width, constrained.Height, 0x16))
                throw new Exception("窄视口未设置。");
            CheckFrame(main, true, frame);
            if (Math.Abs(main.ClientSize.Width - 506) > 3) throw new Exception($"窄视口的实际宽度为 {main.ClientSize.Width} DIP。");
            for (int i = 0; i < 3; i++)
            {
                SetNativeState(7, WindowState.Minimized);
                SetNativeState(9, WindowState.Normal);
                // Force an OS state transition to exercise the old Win10 AdornerLayer crash path.
                SetNativeState(3, WindowState.Maximized);
                SetNativeState(9, WindowState.Normal);
                main.Hide();
                Pump();
                if (main.IsVisible) throw new Exception("主窗口没有隐藏。");
                main.Show();
                CheckFrame(main, true, frame);
            }
            host.Child = overview;
            CheckFrame(main, true, frame);
            _ = dialog.ShowDialog(main);
            var dialogFrame = CheckFrame(dialog, true);
            // The fixed dialog has no minimize button; native Windows can still minimize and restore its handle.
            var dialogHandle = dialog.TryGetPlatformHandle()!.Handle;
            ShowWindow(dialogHandle, 7);
            CheckFrame(dialog, false, dialogFrame);
            ShowWindow(dialogHandle, 9);
            CheckFrame(dialog, true, dialogFrame);
            dialog.Close();
            CheckFrame(main, true, frame);
            Console.WriteLine($"Windows {Environment.OSVersion.Version}：固定窗口、窄视口、页面切换、强制原生状态还原、隐藏重开及模态框四边像素检查通过。");

            void SetNativeState(int command, WindowState expected)
            {
                ShowWindow(handle, command);
                Pump();
                if (main.WindowState != expected || IsIconic(handle) != (expected == WindowState.Minimized) ||
                    IsZoomed(handle) != (expected == WindowState.Maximized))
                    throw new Exception($"原生窗口未进入 {expected}，实际 {main.WindowState}。");
                CheckFrame(main, expected == WindowState.Normal, frame);
            }
        }
        finally
        {
            dialog.Close();
            main.Close();
            state.Delete(true);
        }
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(30));
        Dispatcher.UIThread.PushFrame(frame);
    }

    private static Border CheckFrame(Window window, bool expected, Border? original = null)
    {
        Pump();
        window.UpdateLayout();
        var borders = window.GetVisualDescendants().OfType<Border>().ToArray();
        if (borders.Any(b => b.GetVisualParent() is AdornerLayer && IsFrameColor(b)))
            throw new Exception("窗口仍使用临时 Adorner 绘制外框。");
        var frame = borders.Single(b => b.Name == "Windows10Frame");
        if (frame.IsHitTestVisible || frame.IsVisible != expected || original != null && frame != original)
            throw new Exception($"{window.Title} ({window.WindowState}) 外框参与命中测试、可见性错误或已被替换。");
        if (!expected) return frame;
        var topLeft = frame.TranslatePoint(default, window);
        var bottomRight = frame.TranslatePoint(new Point(frame.Bounds.Width, frame.Bounds.Height), window);
        var tolerance = 0.51 / window.RenderScaling;
        if (!IsFrameColor(frame) || frame.BorderThickness != new Thickness(1) ||
            topLeft is not { } start || bottomRight is not { } end ||
            Math.Abs(start.X) > tolerance || Math.Abs(start.Y) > tolerance ||
            Math.Abs(end.X - window.ClientSize.Width) > tolerance || Math.Abs(end.Y - window.ClientSize.Height) > tolerance)
            throw new Exception($"{window.Title} 外框必须穿透鼠标，四边均为 1 DIP 并对齐客户区。实测 {topLeft} 到 {bottomRight}，客户区 {window.ClientSize}。");

        var size = PixelSize.FromSize(window.ClientSize, window.RenderScaling);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96) * window.RenderScaling);
        bitmap.Render(window);
        using var png = new MemoryStream();
        bitmap.Save(png);
        png.Position = 0;
        using var pixels = SKBitmap.Decode(png);
        var width = pixels.Width;
        var height = pixels.Height;
        foreach (var (x, y) in new[] { (0, height / 2), (width - 1, height / 2), (width / 2, 0), (width / 2, height - 1) })
        {
            var color = pixels.GetPixel(x, y);
            if (color != new SKColor(0x83, 0x95, 0xA8))
                throw new Exception($"{window.Title} 边缘像素 ({x}, {y}) 实测 {color}。");
        }
        return frame;
    }

    private static bool IsFrameColor(Border border) =>
        border.BorderBrush is ISolidColorBrush brush && brush.Color == Color.Parse("#8395A8");
}
