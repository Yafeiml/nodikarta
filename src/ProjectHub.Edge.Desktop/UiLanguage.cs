using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Data.Core;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings;

namespace ProjectHub.Edge.Desktop;

internal static class UiLanguage
{
    internal sealed class Selection : INotifyPropertyChanged
    {
        public bool English => IsEnglish;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Notify() => PropertyChanged?.Invoke(this, new(nameof(English)));
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetUserDefaultUILanguage();

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserPreferredUILanguages(uint flags, out uint count, [Out] char[]? names, ref uint length);

    private static string WindowsDisplayLanguage()
    {
        const uint languageNames = 0x08; // MUI_LANGUAGE_NAME
        uint length = 0;
        if (GetUserPreferredUILanguages(languageNames, out _, null, ref length) && length > 0)
        {
            var names = new char[length];
            if (GetUserPreferredUILanguages(languageNames, out var count, names, ref length) && count > 0)
            {
                var end = Array.IndexOf(names, '\0');
                if (end > 0) return new string(names, 0, end);
            }
        }
        // If the language list changed between reads, use the effective UI language ID for this tick.
        return (GetUserDefaultUILanguage() & 0x03ff) == 0x04 ? "zh" : "und";
    }

    internal static Func<string> ReadSystemLanguage { get; set; } =
        OperatingSystem.IsWindows() ? WindowsDisplayLanguage : () => CultureInfo.CurrentUICulture.Name;
    private static bool SystemIsEnglish()
    {
        var tag = ReadSystemLanguage();
        return !tag.Equals("zh", StringComparison.OrdinalIgnoreCase) && !tag.StartsWith("zh-", StringComparison.OrdinalIgnoreCase);
    }

    internal static Selection Current { get; } = new();
    internal static bool FollowsSystem { get; private set; } = true;
    internal static bool IsEnglish { get; private set; } = SystemIsEnglish();
    internal static event Action? Changed;
    internal static string Text(string chinese, string english) => IsEnglish ? english : chinese;
    internal static string Message(string source) => IsEnglish ? UiStrings.English(source) : source;
    internal static string Format(string format, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Message(format), values);
    internal static string Product => Text("脉络 Edge", "Nodicarta Edge");

    internal static void Load(string directory)
    {
        string? preference = null;
        try { preference = File.ReadAllText(Path.Combine(directory, "ui-language.txt")).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        FollowsSystem = preference is not ("en" or "zh" or "zh-CN");
        if (FollowsSystem) RefreshSystemLanguage();
        else Apply(preference == "en");
    }

    internal static void Save(string directory, bool english)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ui-language.txt");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, english ? "en" : "zh-CN");
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
        FollowsSystem = false;
        Apply(english);
    }

    internal static void FollowSystem(string directory)
    {
        try { File.Delete(Path.Combine(directory, "ui-language.txt")); }
        catch (DirectoryNotFoundException) { }
        FollowsSystem = true;
        RefreshSystemLanguage();
    }

    internal static bool RefreshSystemLanguage() => FollowsSystem && Apply(SystemIsEnglish());

    private static bool Apply(bool english)
    {
        if (IsEnglish == english) return false;
        IsEnglish = english;
        Current.Notify();
        Changed?.Invoke();
        return true;
    }
}

public sealed class TrExtension(string chinese) : MarkupExtension
{
    private static readonly CompiledBindingPath LanguagePath = new CompiledBindingPathBuilder()
        .Property(new ClrPropertyInfo(nameof(UiLanguage.Selection.English), source => ((UiLanguage.Selection)source).English,
            null, typeof(bool)), PropertyInfoAccessorFactory.CreateInpcPropertyAccessor).Build();
    public string? English { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new CompiledBindingExtension(LanguagePath)
        {
            Source = UiLanguage.Current,
            Mode = BindingMode.OneWay,
            Converter = new FuncValueConverter<bool, string>(english => english ? English ?? UiStrings.English(chinese) : chinese)
        };
}
