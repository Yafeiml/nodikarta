using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProjectHub.Core.Pages;

[AllowAnonymous]
public sealed class SetupModel(CoreStore store) : PageModel
{
    public string Username { get; private set; } = "";
    public string? Error { get; private set; }
    public bool AlreadyInstalled { get; private set; }

    public async Task<IActionResult> OnGetAsync() =>
        await store.IsInstalledAsync() ? RedirectToPage("/Login") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (await store.IsInstalledAsync())
            return SetupClosed();

        var form = await Request.ReadFormAsync();
        var code = form["code"].ToString().Trim();
        var rawUsername = form["username"].ToString();
        Username = rawUsername.Trim();
        var password = form["password"].ToString();
        if (code.Length is 0 or > 128)
            Error = WebText.T("请输入容器日志中的首次安装码。", "Enter the setup code from the container log.");
        else if (rawUsername.Any(char.IsControl) || !CoreStore.IsValidUsername(Username))
            Error = WebText.T("管理员账号须为 1 至 64 个字符，不可包含控制字符。", "Use 1–64 characters for the username, without control characters.");
        else if (!CoreStore.IsValidPassword(password))
            Error = WebText.T("请使用 12 至 128 个字符的密码。", "Use a password of 12–128 characters.");
        else if (!string.Equals(password, form["confirmPassword"].ToString(), StringComparison.Ordinal))
            Error = WebText.T("两次输入的密码不一致。", "The passwords do not match.");
        else if (await store.SetupAsync(code, Username, password))
        {
            TempData["Notice"] = WebText.T("管理员账号已创建，请使用新账号登录。", "Administrator account created. Sign in with your new account.");
            return RedirectToPage("/Login");
        }
        else if (await store.IsInstalledAsync())
            return SetupClosed();
        else
            Error = WebText.T("安装码无效或已过期。请核对 Core 容器日志中的最新安装码。", "The setup code is invalid or expired. Check the latest code in the Core container log.");

        Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }

    private IActionResult SetupClosed()
    {
        AlreadyInstalled = true;
        Response.StatusCode = StatusCodes.Status409Conflict;
        return Page();
    }
}
