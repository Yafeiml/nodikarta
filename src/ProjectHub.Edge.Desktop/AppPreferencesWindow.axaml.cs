using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ProjectHub.Edge.Desktop;

internal partial class AppPreferencesWindow : Window
{
    private readonly DesktopStartup startup;
    private readonly App app;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };

    internal AppPreferencesWindow(DesktopStartup startup, App app)
    {
        this.startup = startup;
        this.app = app;
        InitializeComponent();
        timer.Tick += (_, _) => ReadUpdates();
        Activated += (_, _) => ReadSettings();
        Opened += (_, _) =>
        {
            UiLanguage.Changed += ReadSettings;
            ReadSettings();
            timer.Start();
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            UiLanguage.Changed -= ReadSettings;
        };
    }

    private static bool SettingsError(Exception e) => e is IOException or UnauthorizedAccessException or InvalidOperationException
        or COMException or Win32Exception or SecurityException;

    private void ReadSettings()
    {
        ReadStartup();
        ReadUpdates();
    }

    private void ReadStartup()
    {
        try
        {
            var (registered, enabled) = startup.Read();
            StartOnLogin.IsChecked = enabled;
            WindowsStartup.IsVisible = registered && !enabled;
            StartOnLogin.IsEnabled = !WindowsStartup.IsVisible;
            ShowStartupMessage(WindowsStartup.IsVisible ? UiLanguage.Text("Windows 已暂停此启动项。", "Windows has disabled this startup entry.") : null);
        }
        catch (Exception e) when (SettingsError(e))
        {
            StartOnLogin.IsChecked = false;
            StartOnLogin.IsEnabled = WindowsStartup.IsVisible = false;
            ShowStartupMessage(e.Message);
        }
    }

    private void ShowStartupMessage(string? message)
    {
        StartupMessage.Text = message;
        StartupMessage.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void ReadUpdates()
    {
        try
        {
            var available = app.ApplicationUpdatesAvailable;
            ApplicationUpdates.IsEnabled = available;
            ApplicationUpdates.IsChecked = available && app.ApplicationUpdatesEnabled;
            UpdateStatus.Text = app.ApplicationUpdateStatus;
        }
        catch (Exception e) when (SettingsError(e))
        {
            ApplicationUpdates.IsChecked = ApplicationUpdates.IsEnabled = false;
            UpdateStatus.Text = e.Message;
        }
    }

    private void ChangeStartup(object? sender, RoutedEventArgs args)
    {
        try
        {
            startup.Set(StartOnLogin.IsChecked == true);
            ReadStartup();
        }
        catch (Exception e) when (SettingsError(e))
        {
            ReadStartup();
            ShowStartupMessage(e.Message);
        }
    }

    private void ChangeUpdates(object? sender, RoutedEventArgs args)
    {
        try
        {
            app.ApplicationUpdatesEnabled = ApplicationUpdates.IsChecked == true;
            ReadUpdates();
        }
        catch (Exception e) when (SettingsError(e))
        {
            ReadUpdates();
            UpdateStatus.Text = e.Message;
        }
    }

    private void OpenStartupSettings(object? sender, RoutedEventArgs args)
    {
        try { using var process = Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); }
        catch (Exception e) when (SettingsError(e)) { ShowStartupMessage(e.Message); }
    }

    private void Dismiss(object? sender, RoutedEventArgs args) => Close();
}
