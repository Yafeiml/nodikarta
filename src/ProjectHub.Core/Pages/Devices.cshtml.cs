using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProjectHub.Core.Pages;

[Authorize]
public sealed class DevicesModel(CoreStore store) : PageModel
{
    public IReadOnlyList<ProjectEntry> Projects { get; private set; } = [];
    public IReadOnlyList<ManagedDevice> Devices { get; private set; } = [];
    public IReadOnlyList<PendingPairing> Pending { get; private set; } = [];
    public PairingOffer? Issued { get; private set; }
    public Guid? UpdateTarget { get; private set; }
    public string Label { get; private set; } = "";
    public Dictionary<Guid, string> Selection { get; private set; } = [];
    public string? Error { get; private set; }
    public Guid RequestId { get; } = Guid.NewGuid();
    public Dictionary<Guid,DateTimeOffset> Contacts { get; private set; } = [];
    public ActivityPanel? Security { get; private set; }
    public bool History { get; private set; }

    private Guid OwnerId => AdminSession.OwnerId(User);
    private long SessionVersion => AdminSession.Version(User);

    public async Task OnGetAsync(string? view = null, long? before = null, string[]? action = null,
        string[]? result = null, string[]? client = null, string? since = null, string? until = null)
    {
        Projects = await store.ListProjectsAsync(OwnerId);
        Devices = await store.ListDevicesAsync(OwnerId);
        Pending = await store.ListPendingPairingsAsync(OwnerId);
        Contacts = await store.AdminContactsAsync(OwnerId);
        History = view == "history";
        if (view == "security") Security = await ActivityQuery.ReadAsync(store,OwnerId,null,null,"security",client,action,result,since,until,before);
    }

    public async Task<IActionResult> OnGetProgressAsync(Guid pairingId)
    {
        var status = await store.AdminPairingAsync(OwnerId,pairingId);
        return status is null ? NotFound() : new JsonResult(status);
    }

    private bool Interactive => Request.Headers.Accept.ToString().Contains("application/json",StringComparison.Ordinal);

    public Task<IActionResult> OnPostIssueAsync([FromForm] string? label, [FromForm] Guid requestId,
        [FromForm] Dictionary<Guid, string>? grants) =>
        SubmitAsync(async () =>
        {
            Label = label ?? "";
            Selection = grants ?? [];
            var requested = Selection.Where(p => p.Value != "none")
                .Select(p => new DeviceGrantInput(p.Key, p.Value)).ToArray();
            Issued = await store.IssuePairingAsync(OwnerId, SessionVersion, Label, requested, requestId);
        }, "");

    public Task<IActionResult> OnPostIssueUpdateAsync([FromForm] Guid deviceId, [FromForm] Guid requestId,
        [FromForm] bool checkPending, [FromForm] Guid? expectedPairingId) =>
        SubmitAsync(async () =>
        {
            Issued = await store.IssueDeviceUpdateAsync(OwnerId, SessionVersion, deviceId, requestId, checkPending, expectedPairingId);
            UpdateTarget = deviceId;
        }, "");

    public async Task<IActionResult> OnPostChangeGrantAsync([FromForm] Guid deviceId, [FromForm] Guid projectId,
        [FromForm] string accessMode, [FromForm] Guid? expectedBindingId, [FromForm] long expectedVersion)
    {
        var result = await SubmitAsync(() => store.ChangeGrantAsync(OwnerId, SessionVersion, deviceId, projectId,
            accessMode == "none" ? null : accessMode, expectedBindingId, expectedVersion), WebText.T("项目权限已更新。", "Project permissions updated."));
        if (!Interactive || (result is not JsonResult && result is not ObjectResult)) return result;
        var latest = (await store.ListDevicesAsync(OwnerId)).FirstOrDefault(d=>d.DeviceId==deviceId && !d.Revoked);
        var grant = latest?.Grants.FirstOrDefault(g=>g.ProjectId==projectId);
        return new JsonResult(new { message=Error ?? WebText.T("项目权限已更新。", "Project permissions updated."), deviceRevision=latest?.GrantRevision,
            bindingId=grant?.BindingId, version=grant?.Version ?? latest?.GrantRevision, mode=grant?.AccessMode ?? "none" })
            { StatusCode=Response.StatusCode };
    }

    public Task<IActionResult> OnPostRevokePairingAsync([FromForm] Guid pairingId) =>
        SubmitAsync(async () =>
        {
            if (!await store.RevokePairingAsync(OwnerId, SessionVersion, pairingId))
                throw new KeyNotFoundException();
        }, WebText.T("配对码已取消。", "Pairing code cancelled."));

    public Task<IActionResult> OnPostRevokeDeviceAsync([FromForm] Guid deviceId) =>
        SubmitAsync(async () =>
        {
            if (!await store.RevokeDeviceAsync(OwnerId, SessionVersion, deviceId))
                throw new KeyNotFoundException();
        }, WebText.T("设备已撤销。", "Device revoked."));

    private async Task<IActionResult> SubmitAsync(Func<Task> operation, string notice)
    {
        if (!ModelState.IsValid)
            return await ShowErrorAsync(400, WebText.T("表单无效，请核对后重新提交。", "Invalid form. Check the details and try again."));
        try
        {
            await operation();
        }
        catch (UnauthorizedAccessException)
        {
            await HttpContext.SignOutAsync();
            return RedirectToPage("/Login");
        }
        catch (PairingAlreadyIssuedException)
        {
            return await ShowErrorAsync(409, WebText.T("配对状态已变化。请关闭并刷新，核对当前进度后再生成。", "The pairing status has changed. Close this dialog, refresh, and check the current status before generating a code."));
        }
        catch (GrantConflictException)
        {
            return await ShowErrorAsync(409, WebText.T("权限状态已变化。请核对下方最新授权后再保存。", "Permissions have changed. Check the latest grants below before saving."));
        }
        catch (KeyNotFoundException)
        {
            return await ShowErrorAsync(404, WebText.T("该设备、项目或待配对记录已不可用，请核对当前列表。", "The device, project, or pending pairing is no longer available. Check the current list."));
        }
        catch (ArgumentException)
        {
            return await ShowErrorAsync(400, WebText.T("设备名称须为 1 至 120 个字符，请同时核对所选项目和权限。", "Use 1–120 characters for the device name and check the selected projects and permissions."));
        }

        if (Issued is not null)
        {
            // The pairing secret only lives in this POST response, never in TempData or the URL.
            if (Interactive) return new JsonResult(new { Issued.PairingId, Issued.ExpiresAt,
                code=(UpdateTarget.HasValue ? "U1." : "") + Issued.PairingId + "." + Issued.Code });
            await OnGetAsync();
            return Page();
        }
        if (Interactive) return new JsonResult(new { message=notice });
        TempData["Notice"] = notice;
        return RedirectToPage("/Devices");
    }

    private async Task<IActionResult> ShowErrorAsync(int status, string message)
    {
        Error = message;
        Response.StatusCode = status;
        if (Interactive) return new JsonResult(new { message }) { StatusCode=status };
        await OnGetAsync();
        return Page();
    }
}
