#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Globalization;
using ProjectHub.Edge.Desktop;

var state = Directory.CreateTempSubdirectory("nodicarta-language-");
var systemReader = UiLanguage.ReadSystemLanguage;
var regionalFormat = CultureInfo.CurrentCulture;
var threadLanguage = CultureInfo.CurrentUICulture;
var events = 0;
void Changed() => events++;
try
{
    var file = Path.Combine(state.FullName, "ui-language.txt");
    var system = "en-GB";
    var reads = 0;
    UiLanguage.ReadSystemLanguage = () => { reads++; return system; };
    // A Windows regional format and the thread resource culture can both differ from the Windows display language.
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-CA");
    UiLanguage.Load(state.FullName);
    Check(UiLanguage.FollowsSystem && reads == 1 && !File.Exists(file), "first launch follows the Windows display language");
    Equal("Nodicarta Edge", UiLanguage.Product);
    UiLanguage.Changed += Changed;
    foreach (var name in new[] { "en", "en-US", "ja-JP", "fr-CA", "de-DE", "zha" })
    {
        system = name;
        var before = reads;
        Check(!UiLanguage.RefreshSystemLanguage() && reads == before + 1 && events == 0,
            $"automatic mode refreshes {name} and keeps English");
        Equal("Nodicarta Edge", UiLanguage.Product);
    }
    foreach (var name in new[] { "zh", "zh-Hans", "zh-Hant", "zh-CN", "zh-TW", "zh-HK", "zh-MO", "zh-SG", "ZH-Hant-HK" })
    {
        system = name;
        Check(UiLanguage.RefreshSystemLanguage() == (name == "zh") && events == 1,
            $"automatic mode uses Chinese for {name} with a single effective language change");
        Equal("脉络 Edge", UiLanguage.Product);
    }
    UiLanguage.Save(state.FullName, true);
    Equal("en", File.ReadAllText(file));
    int manualReads = reads;
    UiLanguage.Load(state.FullName);
    Check(!UiLanguage.FollowsSystem && !UiLanguage.RefreshSystemLanguage() && reads == manualReads && events == 2,
        "existing English preference survives restart and ignores OS notifications");
    Equal("Online", UiLanguage.Message("在线"));
    Equal(" · Endpoint 目录设置", UiLanguage.Format(" · 端点 {0}", "目录设置"));
    UiLanguage.Save(state.FullName, false);
    Equal("zh-CN", File.ReadAllText(file));
    system = "en-AU";
    UiLanguage.Load(state.FullName);
    Check(!UiLanguage.FollowsSystem && !UiLanguage.RefreshSystemLanguage() && reads == manualReads && events == 3,
        "existing Chinese preference survives restart and ignores OS notifications");
    Equal("在线", UiLanguage.Message("在线"));
    File.WriteAllText(file, "zh");
    UiLanguage.Load(state.FullName);
    Check(!UiLanguage.FollowsSystem && !UiLanguage.IsEnglish && reads == manualReads, "legacy zh remains a manual preference");
    using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        try { UiLanguage.FollowSystem(state.FullName); throw new Exception("Windows accepted deleting a locked language file"); }
        catch (IOException) { }
        Check(!UiLanguage.FollowsSystem && !UiLanguage.IsEnglish && events == 3, "failed reset retains the previous mode");
    }
    UiLanguage.FollowSystem(state.FullName);
    Check(UiLanguage.FollowsSystem && !File.Exists(file) && reads == manualReads + 1 && events == 4,
        "reset to automatic on non-Chinese Windows immediately switches to English");
    Equal("Nodicarta Edge", UiLanguage.Product);
    UiLanguage.Save(state.FullName, true);
    Check(!UiLanguage.FollowsSystem && UiLanguage.IsEnglish && events == 4, "matching manual override persists without a re-render");
    UiLanguage.FollowSystem(state.FullName);
    Check(UiLanguage.FollowsSystem && UiLanguage.IsEnglish && !File.Exists(file) && events == 4, "mode-only reset keeps English");
    system = "zh-Hant-HK";
    UiLanguage.Load(state.FullName);
    Check(UiLanguage.FollowsSystem && !UiLanguage.IsEnglish && events == 5, "automatic mode reads the new Windows language on restart");
    File.WriteAllText(file, "invalid");
    system = "ja-JP";
    UiLanguage.Load(state.FullName);
    Check(UiLanguage.FollowsSystem && UiLanguage.IsEnglish && events == 6, "invalid preference falls back to the current Windows language");
    Equal("invalid", File.ReadAllText(file));
    UiLanguage.ReadSystemLanguage = systemReader;
    var native = systemReader();
    UiLanguage.FollowSystem(state.FullName);
    Check(UiLanguage.IsEnglish == (CultureInfo.GetCultureInfo(native).TwoLetterISOLanguageName != "zh"),
        "the native Windows display language determines the app language despite artificial thread cultures");
    Console.WriteLine($"PASS Windows UI language {native}, Chinese tags, all other languages use English, manual overrides and restart, reset and event deduplication.");
}
finally
{
    UiLanguage.Changed -= Changed;
    UiLanguage.ReadSystemLanguage = systemReader;
    CultureInfo.CurrentCulture = regionalFormat;
    CultureInfo.CurrentUICulture = threadLanguage;
    state.Delete(true);
}

static void Check(bool condition, string check)
{
    if (!condition) throw new Exception(check);
}

static void Equal(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new Exception($"Expected {expected}, got {actual}");
}
