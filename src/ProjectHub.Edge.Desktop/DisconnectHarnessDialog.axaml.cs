using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ProjectHub.Edge.Desktop;

internal partial class DisconnectHarnessDialog : Window
{
    internal DisconnectHarnessDialog(HarnessRow client)
    {
        InitializeComponent();
        void RefreshLanguage()
        {
            Heading.Text = UiLanguage.Text($"断开 {client.Name} 接入？", $"Disconnect {client.Name}?");
            Note.Text = UiLanguage.Text("Edge 将移除为此客户端写入的接入配置。", "Edge will remove the connection settings it manages for this client.")
                + (client.Id switch
                {
                    "codex" => UiLanguage.Text("已安装的通用 Skills 会保留。", " Installed shared skills will be kept."),
                    "antigravity" => UiLanguage.Text("同时停止目录接入维护，并清理已绑定目录中的受管插件和指引。", " Workspace maintenance will stop and the managed plugin and rules will be removed from bound folders."),
                    _ => ""
                });
        }
        RefreshLanguage();
        UiLanguage.Changed += RefreshLanguage;
        Closed += (_, _) => UiLanguage.Changed -= RefreshLanguage;
        Opened += (_, _) => CancelButton.Focus();
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);
    private void Confirm(object? sender, RoutedEventArgs e) => Close(true);
}
