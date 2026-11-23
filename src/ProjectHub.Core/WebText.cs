using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace ProjectHub.Core;

public static class WebText
{
    public const string CookieName = "Nodicarta.Language";
    public static string T(string chinese, string english) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? english : chinese;

    public static string Preference(HttpRequest request) => request.Cookies[CookieName] switch
    {
        "en" => "en",
        "zh" => "zh",
        _ => "auto"
    };

    public static RequestLocalizationOptions LanguageOptions()
    {
        var options = new RequestLocalizationOptions()
            .SetDefaultCulture("zh-CN")
            .AddSupportedCultures("zh-CN", "zh-TW", "zh-HK", "zh-MO", "zh-SG", "en")
            .AddSupportedUICultures("zh-CN", "en");
        options.ApplyCurrentCultureToResponseHeaders = true;
        options.RequestCultureProviders.Clear();
        options.AddInitialRequestCultureProvider(new CustomRequestCultureProvider(context =>
        {
            var choice = Preference(context.Request);
            var preferred = choice == "auto" ? PreferredCulture(context.Request) : null;
            var language = choice == "en" || preferred?.TwoLetterISOLanguageName is not (null or "zh") ? "en" : "zh-CN";
            var region = options.SupportedCultures!.FirstOrDefault(c =>
                c.TwoLetterISOLanguageName == "zh" && c.Name == preferred?.Name);
            return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(region?.Name ?? language, language));
        }));
        return options;
    }

    private static CultureInfo? PreferredCulture(HttpRequest request)
    {
        var languages = request.GetTypedHeaders().AcceptLanguage;
        if (languages is null) return null;
        foreach (var item in languages.Where(x => x.Quality != 0)
                     .OrderByDescending(x => x.Quality.GetValueOrDefault(1)))
        {
            if (item.Value.Value is not { Length: > 0 } tag || tag == "*") continue;
            try { return CultureInfo.GetCultureInfo(tag, predefinedOnly: true); }
            catch (CultureNotFoundException) { /* Try the next concrete language. */ }
        }
        return null;
    }
}
