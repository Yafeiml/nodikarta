using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProjectHub.Core.Pages;

[Authorize]
public sealed class ProjectModel(CoreStore store) : PageModel
{
    public AdminProject Project { get; private set; } = null!;
    public string Tab { get; private set; } = "knowledge";
    public Guid? Subject { get; private set; }
    public long? Version { get; private set; }
    public JsonElement Detail { get; private set; }
    public IReadOnlyList<AdminReference> References { get; private set; } = [];
    public IReadOnlyList<AdminResource> Resources { get; private set; } = [];
    public IReadOnlyList<ManagedDevice> Devices { get; private set; } = [];
    public ActivityPanel? Activity { get; private set; }
    public string Search { get; private set; } = "";
    public int Number { get; private set; }
    public bool More { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, string tab = "knowledge", Guid? objectId = null, long? version = null,
        string? q = null, int number = 1, string view = "all", Guid? device = null, string[]? client = null,
        string[]? action = null, string[]? result = null, string? since = null, string? until = null, long? before = null)
    {
        if (!ModelState.IsValid || number < 1 || number > 1_000_000 || version <= 0 || q?.Length>512 ||
            (objectId.HasValue && tab is not ("knowledge" or "assets")) || (version.HasValue && !objectId.HasValue) ||
            tab is not ("knowledge" or "assets" or "activity" or "grants")) return BadRequest();
        var owner = AdminSession.OwnerId(User);
        var project = (await store.AdminProjectsAsync(owner, "", 0, 1, id)).FirstOrDefault();
        if (project is null) return NotFound();
        Project = project;
        Tab = tab;
        Number = number;
        Subject = objectId;
        Search = q?.Trim() ?? "";
        if (Tab is "knowledge" or "assets")
        {
            var assets = Tab == "assets";
            if (Subject is Guid resource)
            {
                var details = await store.AdminResourceVersionAsync(owner,id,assets,resource,version);
                if (details is null) return NotFound();
                Detail = details.Value;
                Version = Detail.GetProperty("version").GetInt64();
                References = await store.AdminReferencesAsync(owner,id,assets,resource,Version.Value);
            }
            var rows = await store.AdminResourcesAsync(owner,id,assets,Subject,Search,AdminUi.Offset(number),AdminUi.PageSize + 1);
            More = rows.Count > AdminUi.PageSize;
            Resources = rows.Take(AdminUi.PageSize).ToList();
            store.ObserveAdminResource(owner,id,assets,Subject,Version,Resources.Count);
        }
        else if (Tab == "activity")
        {
            Activity = await ActivityQuery.ReadAsync(store, owner, id, device, view, client, action, result, since, until, before);
            if (Activity is null) return BadRequest();
        }
        else Devices = await store.ListDevicesAsync(owner);
        return Page();
    }

    public async Task<IActionResult> OnGetDownloadAsync(Guid id, Guid assetId, int version)
    {
        if (!ModelState.IsValid || version < 1) return BadRequest();
        var file = await store.AdminFileAsync(AdminSession.OwnerId(User),id,assetId,version);
        if (file is null) return NotFound();
        store.ObserveAdminResource(AdminSession.OwnerId(User),id,true,assetId,version,null,download:true);
        return File(file.Value.Content,"application/octet-stream",file.Value.Name);
    }
}

internal static class ActivityQuery
{
    public static async Task<ActivityPanel?> ReadAsync(CoreStore store, Guid owner, Guid? project, Guid? device, string view,
        string[]? client, string[]? action, string[]? result, string? since, string? until, long? before)
    {
        if (view is not ("important" or "all" or "reads" or "errors" or "security") || before <= 0 ||
            !Valid(client,64) || !Valid(action,120) || !Valid(result,80)) return null;
        if (!ReadDay(since,out var from) || !ReadDay(until,out var end) || from > end) return null;
        client = Normalize(client).Select(ActivityClientNames.Product).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        action = Normalize(action);
        result = Normalize(result);
        var rows = await store.ListActivityAsync(owner,project,device,view,client,action,from,end?.AddDays(1),before,AdminUi.PageSize + 1,result);
        long? next = rows.Count > AdminUi.PageSize ? rows[AdminUi.PageSize - 1].Id : null;
        var options = await store.ActivityFiltersAsync(owner, project, project.HasValue ? null : device, view == "security" ? "security" : "all");
        var devices = project.HasValue ? (await store.ListDevicesAsync(owner)).Where(d =>
            d.Grants.Any(g => g.ProjectId == project) || options.DeviceIds.Contains(d.DeviceId)).ToList() : [];
        return new(rows.Take(AdminUi.PageSize).ToList(),await store.GetActivityCoverageAsync(),project,device,view,
            new("client",WebText.T("客户端","Client"),WebText.T("全部客户端","All clients"),options.Clients,client,AdminUi.ClientName,StringComparer.OrdinalIgnoreCase),
            new("action",WebText.T("动作","Action"),WebText.T("全部动作","All actions"),options.Actions,action,AdminUi.Action,StringComparer.Ordinal),
            new("result",WebText.T("结果","Result"),WebText.T("全部结果","All results"),options.Results,result,AdminUi.Reason,StringComparer.Ordinal),
            since,until,before,next,devices);
    }

    private static bool Valid(string[]? values, int length) =>
        values is null || values.Length <= 128 && values.All(v => v is null || v.Length <= length);

    private static string[] Normalize(string[]? values) =>
        values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToArray() ?? [];

    private static bool ReadDay(string? value, out DateTimeOffset? date)
    {
        date = null;
        if (string.IsNullOrEmpty(value)) return true;
        if (!DateOnly.TryParseExact(value,"yyyy-MM-dd",out var parsed) || parsed == DateOnly.MaxValue) return false;
        date = new DateTimeOffset(parsed.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
        return true;
    }
}
