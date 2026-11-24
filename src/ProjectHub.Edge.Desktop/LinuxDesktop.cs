#if LINUX_DESKTOP
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;

namespace ProjectHub.Edge.Desktop;

internal static class LinuxDesktop
{
    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libX11.so.6")]
    private static extern int XInitThreads();
    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay(nint displayName);
    [DllImport("libX11.so.6")]
    private static extern nuint XInternAtom(nint display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int onlyIfExists);
    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);

    internal static int Run(string[] args)
    {
        try
        {
            using var resident = TryLockDesktop();
            if (resident == null)
            {
                Console.Error.WriteLine(UiLanguage.Text("脉络 Edge 已在运行。", "Nodicarta Edge is already running."));
                return 0;
            }
            var client = OpenClient(args, out var startupError);
            if (startupError != null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
                return 1;
            InitializeClipboardAtoms();
            var result = AppBuilder.Configure(() => new App { Client = client!, LinuxStartupError = startupError }).UseX11().UseSkia()
                .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnMainWindowClose);
            return startupError == null ? result : 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static void InitializeClipboardAtoms()
    {
        XInitThreads();
        var display = XOpenDisplay(0);
        if (display == 0) return;
        try
        {
            // Avalonia 11.3 caches these atoms at startup. Minimal X11 sessions may not have created them yet.
            foreach (var name in new[] { "CLIPBOARD", "TARGETS", "MULTIPLE", "UTF8_STRING", "ATOM_PAIR", "CLIPBOARD_MANAGER", "SAVE_TARGETS" })
                XInternAtom(display, name, onlyIfExists: 0);
        }
        finally { XCloseDisplay(display); }
    }

    private static FileStream? TryLockDesktop()
    {
        // .NET 10 Native AOT named mutexes on Unix are process-local.
        var uid = geteuid();
        var runtime = $"/run/user/{uid}";
        var directory = Directory.Exists(runtime) ? Path.Combine(runtime, "nodicarta-edge") : $"/tmp/nodicarta-edge-{uid}";
        LinuxState.EnsurePrivateDirectory(directory);
        var path = Path.Combine(directory, "desktop.lock");
        LinuxState.EnsurePrivateFile(path);
        try { return File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when (ex.HResult == 11) { return null; } // EWOULDBLOCK
    }

    private static EdgeDesktopClient? OpenClient(string[] args, out string? startupError)
    {
        startupError = null;
        try
        {
            var state = args switch
            {
                [] => Environment.GetEnvironmentVariable("NODICARTA_EDGE_STATE")
                    ?? Environment.GetEnvironmentVariable("PROJECTHUB_EDGE_STATE") ?? EdgeStatePaths.LocalDefault,
                ["--state", var path] when !string.IsNullOrWhiteSpace(path) => path,
                _ => throw new ArgumentException("Usage: ProjectHub.Edge.Desktop [--state DIRECTORY]")
            };
            var client = new EdgeDesktopClient(state);
            UiLanguage.Load(client.StateDirectory);
            return client;
        }
        catch (Exception error)
        {
            startupError = UiLanguage.Message(error.Message);
            Console.Error.WriteLine(startupError);
            return null;
        }
    }

    internal static void OpenWindow(IClassicDesktopStyleApplicationLifetime desktop, EdgeDesktopClient client, string? error)
    {
        if (error != null)
        {
            var close = new Button { Content = UiLanguage.Text("关闭", "Close"), HorizontalAlignment = HorizontalAlignment.Right };
            var window = new Window
            {
                Title = UiLanguage.Text("无法启动脉络 Edge", "Could not start Nodicarta Edge"),
                Width = 440, SizeToContent = SizeToContent.Height, MaxHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = {
                    new SelectableTextBlock { Text = error, TextWrapping = TextWrapping.Wrap }, close } }
            };
            close.Click += (_, _) => window.Close();
            desktop.MainWindow = window;
            return;
        }
        desktop.MainWindow = new MainWindow(client);
        var stop = new CancellationTokenSource();
        var worker = Task.Run(() => client.RunAsync(stop.Token));
        desktop.Exit += (_, _) =>
        {
            stop.Cancel();
            try { worker.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { stop.Dispose(); }
        };
    }
}
#endif
