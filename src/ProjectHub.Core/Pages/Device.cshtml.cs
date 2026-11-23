using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProjectHub.Core.Pages;

[Authorize]
public sealed class DeviceModel(CoreStore store) : PageModel
{
    public ManagedDevice Device { get; private set; } = null!;
    public IReadOnlyList<ProjectEntry> Projects { get; private set; } = [];
    public DateTimeOffset? Contact { get; private set; }
    public ActivityPanel Activity { get; private set; } = null!;
    public async Task<IActionResult> OnGetAsync(Guid id,string view="all",string[]? client=null,string[]? action=null,string[]? result=null,
        string? since=null,string? until=null,long? before=null)
    {
        var owner=AdminSession.OwnerId(User);
        var device=(await store.ListDevicesAsync(owner)).FirstOrDefault(d=>d.DeviceId==id);
        if (device is null) return NotFound();
        Device=device;
        Projects=await store.ListProjectsAsync(owner);
        if ((await store.AdminContactsAsync(owner)).TryGetValue(id,out var when)) Contact=when;
        var panel=await ActivityQuery.ReadAsync(store,owner,null,id,view,client,action,result,since,until,before);
        if (panel is null) return BadRequest();
        Activity=panel;
        return Page();
    }
}
