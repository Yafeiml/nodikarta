using System.ComponentModel;
using Avalonia.Controls;

namespace ProjectHub.Edge.Desktop;

internal sealed record ViewMessage(Func<string> Render)
{
    public static implicit operator ViewMessage(string key) => new(() => UiLanguage.Message(key));
    public override string ToString() => Render();
}

// Keep the presentation of the last result so changing language never repeats the operation.
internal sealed class LiveLabels
{
    private readonly Dictionary<TextBlock, Func<string>> labels = [];

    public void Set(TextBlock label, Func<string> render)
    {
        labels[label] = render;
        label.Text = render();
    }

    public void Set(TextBlock label, string key, params object?[] values) =>
        Set(label, () => values.Length == 0 ? UiLanguage.Message(key) : UiLanguage.Format(key, values));

    public void Refresh()
    {
        foreach (var (label, render) in labels) label.Text = render();
    }
}

internal abstract record LocalizedRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLanguage() => PropertyChanged?.Invoke(this, new(string.Empty));
}
