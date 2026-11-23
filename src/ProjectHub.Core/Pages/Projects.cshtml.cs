using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProjectHub.Core.Pages;

[Authorize]
public sealed class ProjectsModel(CoreStore store) : PageModel
{
    public IReadOnlyList<AdminProject> Projects { get; private set; } = [];
    public string Search { get; private set; } = "";
    public int Number { get; private set; }
    public bool More { get; private set; }
    public string Name { get; private set; } = "";
    public Guid RequestId { get; private set; } = Guid.NewGuid();
    public string? Error { get; private set; }

    public async Task OnGetAsync(string? q = null, int number = 1)
    {
        Search = q?.Trim() ?? "";
        Number = Math.Clamp(number, 1, 1_000_000);
        var rows = await store.AdminProjectsAsync(AdminSession.OwnerId(User), Search, AdminUi.Offset(Number), AdminUi.PageSize + 1);
        Projects = rows.Take(AdminUi.PageSize).ToList();
        More = rows.Count > AdminUi.PageSize;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var form = await Request.ReadFormAsync();
        var rawName = form["name"].ToString();
        Name = rawName.Trim();
        var status = StatusCodes.Status400BadRequest;
        if (rawName.Any(char.IsControl) || !CoreStore.IsValidProjectName(Name))
            Error = WebText.T("项目名称须为 1 至 120 个字符，不可包含控制字符。", "Use 1–120 characters for the project name, without control characters.");
        else if (!Guid.TryParse(form["requestId"], out var requestId) || requestId == Guid.Empty)
            Error = WebText.T("表单已失效，请再次提交。", "The form has expired. Submit it again.");
        else
        {
            try
            {
                var project = await store.CreateProjectAsync(AdminSession.OwnerId(User), AdminSession.Version(User), Name, requestId);
                TempData["Notice"] = WebText.T("项目已创建。", "Project created.");
                return RedirectToPage("/Project", new { id = project.ProjectId });
            }
            catch (ProjectRequestConflictException)
            {
                status = StatusCodes.Status409Conflict;
                Error = WebText.T("这份表单已用于其他项目。请核对项目列表后重新提交。", "This form was already used for another project. Check the project list and try again.");
            }
            catch (UnauthorizedAccessException)
            {
                return await OnPostLogoutAsync();
            }
        }
        await OnGetAsync();
        Response.StatusCode = status;
        return Page();
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync();
        return RedirectToPage("/Login");
    }
}
