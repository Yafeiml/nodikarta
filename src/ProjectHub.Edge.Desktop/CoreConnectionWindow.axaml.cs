using Avalonia.Controls;
using Avalonia.Interactivity;
using static ProjectHub.Edge.Desktop.UiLanguage;

namespace ProjectHub.Edge.Desktop;

internal partial class CoreConnectionWindow : Window
{
    private readonly EdgeDesktopClient client;
    private readonly DeviceIdentity? original;
    private readonly LiveLabels labels = new();
    private bool submitting;

    internal CoreConnectionWindow(EdgeDesktopClient client, DeviceIdentity? original)
    {
        this.client = client;
        this.original = original;
        InitializeComponent();
        Code.TextChanged += (_, _) => UpdatePairAction();
        RefreshLanguage();
        Opened += (_, _) =>
        {
            RefreshLanguage();
            UiLanguage.Changed += RefreshLanguage;
        };
        Closed += (_, _) => UiLanguage.Changed -= RefreshLanguage;
        if (original == null)
        {
            labels.Set(Product, "脉络 Edge");
            var assembly = typeof(CoreConnectionWindow).Assembly;
            var version = Program.ReleaseVersion;
            var build = ReadBuildId(assembly);
            labels.Set(VersionInfo, () => Text($"版本 {version} · 构建 {build}", $"Version {version} · Build {build}"));
            ExecutableLocation.Text = Environment.ProcessPath;
            AboutInfo.IsVisible = true;
            PairingForm.IsVisible = ConfirmButton.IsVisible = false;
            Opened += (_, _) => CancelButton.Focus();
            return;
        }
        CurrentCore.Text = Address.Text = original.CoreUrl.AbsoluteUri;
        DeviceLabel.Text = client.GetDeviceName(original.DeviceId);
        var originalSession = client.SessionFor(original);
        Opened += async (_, _) =>
        {
            Address.Focus();
            Address.SelectAll();
            try
            {
                var (directories, unsent) = await Task.Run(() => originalSession.ReadPairingInventory(original.DeviceId));
                var folders = directories == 1 ? "folder binding" : "folder bindings";
                var records = unsent == 1 ? "unsent record" : "unsent records";
                labels.Set(Inventory, () => Text($"原配对有 {directories} 个目录绑定，{unsent} 条未发送记录。",
                    $"Current pairing: {directories} {folders}, {unsent} {records}."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                labels.Set(Inventory, () => Text("暂时无法统计原配对的本地记录。", "Local counts for the current pairing are unavailable."));
            }
        };
        Closing += (_, args) =>
        {
            if (submitting && args.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown))
                args.Cancel = true;
        };
    }

    private void RefreshLanguage()
    {
        Title = original == null ? Text("关于脉络 Edge", "About Nodicarta Edge") : Text("重新配对或更换 Core", "Pair again or change Core");
        UpdatePairAction();
        if (original == null) CancelButton.Content = Text("关闭", "Close");
        labels.Refresh();
    }

    private void UpdatePairAction()
    {
        ReplacementHint.IsVisible = EdgeSession.IsUpdateCode(Code.Text);
        ConfirmButton.Content = ReplacementHint.IsVisible ? Message("确认接替") : Text("更换并配对", "Switch and pair");
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);

    private static string ReadBuildId(System.Reflection.Assembly assembly)
    {
        var parentManifest = Path.Combine(AppContext.BaseDirectory, "..", ReleasePackage.Manifest);
#if LINUX_DESKTOP
        var manifest = parentManifest;
#else
        var manifest = Path.Combine(AppContext.BaseDirectory, ReleasePackage.Manifest);
        if (!File.Exists(manifest) && !File.Exists(Path.Combine(AppContext.BaseDirectory, ReleasePackage.Lease)) &&
            File.Exists(Path.Combine(AppContext.BaseDirectory, ReleasePackage.NativeCli)))
            manifest = parentManifest;
#endif
        try
        {
            using var stream = File.OpenRead(manifest);
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream))[..12];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];
        }
    }

    private async void Confirm(object? sender, RoutedEventArgs e)
    {
        if (original == null || submitting) return;
        var address = Address.Text ?? "";
        var code = Code.Text?.Trim() ?? "";
        var name = DeviceLabel.Text?.Trim() ?? "";
        try { EdgeDesktopClient.ParseCoreAddress(address); }
        catch (ArgumentException)
        {
            Error(() => Text("请输入完整的 Core 根地址，例如 https://core.example.com。", "Enter the Core root URL, for example https://core.example.com."), Address);
            return;
        }
        if (code.Length == 0)
        {
            Error(() => Text("请填写目标 Core 生成的新配对码。", "Enter a new pairing code from the target Core."), Code);
            return;
        }
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            Error(() => Text("请输入有效的设备名称。", "Enter a valid device name."), DeviceLabel);
            return;
        }
        submitting = true;
        PairingForm.IsEnabled = ConfirmButton.IsEnabled = CancelButton.IsEnabled = false;
        labels.Set(Problem, () => Text("正在与目标 Core 配对…", "Pairing with the target Core…"));
        Problem.IsVisible = true;
        try
        {
            await client.ReplacePairingAsync(original, address, code, name);
        }
        catch (Exception ex)
        {
            Error(() => ex switch
            {
                HttpRequestException or OperationCanceledException => Text("无法联系目标 Core。请检查地址和网络，然后重试。", "Could not reach the target Core. Check the address and network, then try again."),
                ArgumentException => Text("配对码格式无效。请从目标 Core 重新复制完整配对码。", "Invalid pairing code format. Copy the complete code from the target Core."),
                InvalidOperationException when EdgeSession.IsUpdateCode(code) || ex.Message.StartsWith("上次设备接替", StringComparison.Ordinal) => Message(ex.Message),
                InvalidOperationException when ex.Message.Contains("配对码") => Text("目标 Core 未接受配对码。请生成新码并重试。", "The target Core did not accept the code. Generate a new code and try again."),
                _ => Text("未完成切换。请关闭此窗口，刷新设备状态后再试。", "The switch could not be completed. Close this window, refresh the device status, and try again.")
            });
            return;
        }
        finally
        {
            submitting = false;
            PairingForm.IsEnabled = ConfirmButton.IsEnabled = CancelButton.IsEnabled = true;
        }
        Code.Text = null;
        Close(true);
    }

    private void Error(Func<string> message, Control? focus = null)
    {
        labels.Set(Problem, message);
        Problem.IsVisible = true;
        focus?.Focus();
    }
}
