using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ProjectHub.Edge.Desktop;

internal partial class UnbindDialog : Window
{
    public UnbindDialog(LocalBinding binding)
    {
        InitializeComponent();
        DataContext = binding;
        Opened += (_, _) => CancelButton.Focus();
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);
    private void Confirm(object? sender, RoutedEventArgs e) => Close(true);
}
