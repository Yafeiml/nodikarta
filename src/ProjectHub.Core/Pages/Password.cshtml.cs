using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProjectHub.Core.Pages;

public sealed class PasswordModel(CoreStore store) : PageModel
{
    public string? Error { get; private set; }

    public async Task<IActionResult> OnPostAsync()
    {
        var form = await Request.ReadFormAsync();
        var current = form["currentPassword"].ToString();
        var password = form["newPassword"].ToString();
        if (!CoreStore.IsValidPassword(password))
            Error = WebText.T("请使用 12 至 128 个字符的新密码。","Use 12 to 128 characters for your new password.");
        else if (password != form["confirmPassword"].ToString())
            Error = WebText.T("两次输入的新密码不一致。","The new passwords do not match.");
        else if (password == current)
            Error = WebText.T("新密码须与当前密码不同。","Choose a password different from your current password.");
        else if (!await store.ChangePasswordAsync(AdminSession.OwnerId(User), AdminSession.Version(User), current, password))
            Error = WebText.T("当前密码不正确，或登录已失效。","Your current password is incorrect or your session has expired.");
        else
        {
            await HttpContext.SignOutAsync();
            TempData["Notice"] = WebText.T("密码已修改，请使用新密码重新登录。","Password changed. Sign in with your new password.");
            return RedirectToPage("/Login");
        }
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }
}
