using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProjectHub.Core.Pages;

[AllowAnonymous]
public sealed class LoginModel(CoreStore store) : PageModel
{
    private const string BrowserCookie = "Nodicarta.LoginBrowser";
    public string Username { get; private set; } = "";
    public string? Error { get; private set; }
    public LoginChallenge Captcha { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await store.IsInstalledAsync())
            return RedirectToPage("/Setup");
        return User.Identity?.IsAuthenticated == true ? RedirectToPage("/Projects") : await ChallengePageAsync();
    }

    public async Task<IActionResult> OnPostRefreshAsync(Guid? challengeId, string? username)
    {
        if (!await store.IsInstalledAsync())
            return RedirectToPage("/Setup");
        Username = CoreStore.IsValidUsername(username) ? username!.Trim() : "";
        return await ChallengePageAsync(challengeId);
    }

    private async Task<IActionResult> ChallengePageAsync(Guid? replace = null)
    {
        if (!Guid.TryParse(Request.Cookies[BrowserCookie], out var browser))
        {
            browser = Guid.NewGuid();
            Response.Cookies.Append(BrowserCookie, browser.ToString(), new CookieOptions
            {
                Path = Request.PathBase.HasValue ? Request.PathBase.Value : "/", HttpOnly = true, Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict, IsEssential = true
            });
        }
        Captcha = await store.IssueLoginChallengeAsync(browser, replace);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await store.IsInstalledAsync())
            return RedirectToPage("/Setup");

        var form = await Request.ReadFormAsync();
        var rawUsername = form["username"].ToString();
        Username = rawUsername.Trim();
        var password = form["password"].ToString();
        if (!Guid.TryParse(Request.Cookies[BrowserCookie], out var browser)
            || !Guid.TryParse(form["challengeId"], out var challengeId)
            || !await store.ConsumeLoginChallengeAsync(browser, challengeId, form["captcha"].ToString()))
        {
            Error = WebText.T("验证码错误或已过期，请输入新图中的字符。","The verification code is incorrect or expired. Enter the new code.");
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return await ChallengePageAsync();
        }
        if (!rawUsername.Any(char.IsControl) && CoreStore.IsValidUsername(Username)
            && CoreStore.IsValidPassword(password) && await store.LoginAsync(Username, password) is { } identity)
        {
            await AdminSession.SignInAsync(HttpContext, identity);
            return RedirectToPage("/Projects");
        }

        Error = WebText.T("账号或密码不正确。","The username or password is incorrect.");
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return await ChallengePageAsync();
    }
}
