using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProjectHub.Core.Pages;

[AllowAnonymous]
public sealed class LanguageModel : PageModel
{
    public IActionResult OnGet() => LocalRedirect("~/");

    public IActionResult OnPost(string choice, string? returnUrl)
    {
        if (choice is not ("auto" or "zh" or "en"))
            return BadRequest();
        var cookie = new CookieOptions
        {
            Path = Request.PathBase.HasValue ? Request.PathBase.Value : "/",
            HttpOnly = true,
            Secure = Request.IsHttps,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(365)
        };
        if (choice == "auto") Response.Cookies.Delete(WebText.CookieName, cookie);
        else Response.Cookies.Append(WebText.CookieName, choice, cookie);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "~/");
    }
}
