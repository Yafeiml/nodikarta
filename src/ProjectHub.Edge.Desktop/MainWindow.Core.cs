using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ProjectHub.Edge.Desktop;

internal partial class MainWindow
{
    private CoreConnectionWindow? connectionDialog;

    private async void ManageCoreConnection(object? sender, RoutedEventArgs e)
    {
        if (identity is not { } current || connectionDialog != null) return;
        await RunAsync(async () =>
        {
            connectionDialog = new CoreConnectionWindow(client, current);
            try
            {
                if (!await connectionDialog.ShowDialog<bool>(this)) return;
                pendingAccess = null;
                DisplayProjects([], [], "");
                try { await Task.Run(() => new HarnessAccess(client).RestoreAntigravity()); }
                finally { await LoadAsync(); }
            }
            finally { connectionDialog = null; }
        });
    }

    internal async void ShowClientAbout()
    {
        Reveal();
        if (connectionDialog != null)
        {
            connectionDialog.Activate();
            return;
        }
        connectionDialog = new CoreConnectionWindow(client, null);
        try { await connectionDialog.ShowDialog(this); }
        finally { connectionDialog = null; }
    }
}
