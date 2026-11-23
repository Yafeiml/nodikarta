namespace ProjectHub.Core.Pages;

public static class AdminUi
{
    public const int PageSize = 25;
    public static int Offset(int page) => (Math.Clamp(page, 1, 1_000_000) - 1) * PageSize;
    public static string Short(Guid id) => id.ToString("N")[..8];
    public static string With(HttpRequest request, string key, string? value)
    {
        var pairs=request.Query.Where(p=>!p.Key.Equals(key,StringComparison.OrdinalIgnoreCase))
            .SelectMany(p=>p.Value.Select(v=>KeyValuePair.Create<string,string?>(p.Key,v))).ToList();
        if (value!=null) pairs.Add(KeyValuePair.Create<string,string?>(key,value));
        return request.PathBase + request.Path + QueryString.Create(pairs);
    }
    public static string Size(long bytes) => bytes switch {
        >= 1048576 => $"{bytes / 1048576d:0.#} MiB", >= 1024 => $"{bytes / 1024d:0.#} KiB", _ => $"{bytes} B" };
    public static string Validity(string? value) => value switch {
        "current" => WebText.T("现行决定", "Current decision"), "historical" => WebText.T("历史依据", "Historical evidence"), "superseded" => WebText.T("已被取代", "Superseded"), "lastObserved" => WebText.T("最后实测", "Last observation"), _ => WebText.T("未分类", "Unclassified") };
    public static string Role(string value) => value switch { "deliverable" => WebText.T("交付件", "Deliverable"), "used" => WebText.T("采用素材", "Adopted asset"), "evidence" => WebText.T("证据", "Evidence"), _ => value };
    public static string Action(string value) => value switch {
        "knowledge.commit" => WebText.T("提交知识","Submit knowledge"), "asset.publish" => WebText.T("发布附件","Publish attachment"), "feedback.submit" => WebText.T("提交反馈","Submit feedback"),
        "grant.readOnly" => WebText.T("授权只读","Grant read access"), "grant.readWrite" => WebText.T("授权读写","Grant write access"), "grant.removed" => WebText.T("撤销项目授权","Remove project access"),
        "device.update_issued" => WebText.T("签发设备更新码","Issue replacement code"), "device.update_claimed" => WebText.T("新设备领取更新码","Claim replacement code"),
        "device.update_confirmed" => WebText.T("确认接替","Confirm replacement"), "device.superseded" => WebText.T("旧身份已被接替","Retire replaced identity"),
        "device.revoked" => WebText.T("撤销设备","Revoke device"), "device.paired" => WebText.T("添加设备","Pair device"), "device.pairing_issued" => WebText.T("签发配对码","Issue pairing code"),
        "device.pairing_revoked" => WebText.T("取消配对","Cancel pairing"), "project.created" => WebText.T("创建项目","Create project"), "workspace.registered" => WebText.T("绑定工作区","Bind workspace"),
        "device.tokens_rotated" => WebText.T("更新设备凭据","Rotate device credentials"), "project.snapshot" => WebText.T("读取项目快照","Read project snapshot"),
        "knowledge.search" => WebText.T("检索知识","Search knowledge"), "knowledge.history" => WebText.T("查阅知识历史","Read knowledge history"), "asset.download" => WebText.T("返回附件原件","Download original attachment"),
        "asset.get" => WebText.T("查看附件","View attachment"), "asset.search" => WebText.T("查询附件","Search attachments"), "asset.decisions" => WebText.T("追溯附件引用","Trace attachment references"),
        "knowledge.snapshot" => WebText.T("读取项目知识","Read project knowledge"), "task.list" => WebText.T("查看任务","List tasks"), "task.claim" => WebText.T("领取任务","Claim task"), "task.result" => WebText.T("回传任务结果","Submit task result"),
        "feedback.list" => WebText.T("查阅反馈","View feedback"), "endpoint.list" => WebText.T("查看接收端点","List endpoints"), "endpoint.register" => WebText.T("登记接收端点","Register endpoint"),
        "workspace.list" => WebText.T("查看目录绑定","View workspace bindings"), "grant.lookup" => WebText.T("读取设备授权","Read device grants"), "device.refresh" => WebText.T("刷新设备凭据","Refresh device credentials"),
        "device.redeem" => WebText.T("兑换配对码","Redeem pairing code"), "device.claim" => WebText.T("领取更新码","Claim update code"), "device.confirm" => WebText.T("确认设备接替","Confirm device replacement"),
        "grant.activated" => WebText.T("配对授权生效","Activate grants"), "grant.transferred" => WebText.T("接替设备继承授权","Transfer grants"), "grant.device_revoked" => WebText.T("设备停用，授权失效","Revoke device grants"),
        "device.refresh_reuse" => WebText.T("检测到重复续期","Detect reused renewal"), "owner.created" => WebText.T("创建管理员","Create administrator"), "owner.password_reset" => WebText.T("重置管理员密码","Reset administrator password"),
        "owner.password_changed" => WebText.T("修改管理员密码","Change administrator password"),
        _ => value };
    public static string Reason(string code) => code switch {
        "forbidden" => WebText.T("权限不足","Permission denied"), "unauthenticated" => WebText.T("身份认证失败","Authentication failed"), "not_found" => WebText.T("资源不可用","Resource unavailable"), "version_conflict" => WebText.T("版本冲突","Version conflict"),
        "invalid_request" or "invalid_input" or "invalid_argument" => WebText.T("请求无效","Invalid request"), "request_conflict" or "idempotency_conflict" => WebText.T("请求冲突","Request conflict"), "rate_limited" => WebText.T("请求过于频繁","Too many requests"),
        "asset_too_large" => WebText.T("附件过大","Attachment too large"),
        "success" => WebText.T("成功","Success"), "invalid_asset_format" => WebText.T("附件格式无效","Invalid attachment format"),
        "pairing_unavailable" => WebText.T("配对码不可用","Pairing code unavailable"),
        "unsupported_media_type" => WebText.T("不支持的文件格式","Unsupported media type"),
        "observation_confirmation_required" => WebText.T("实测记录需要确认","Observation requires confirmation"),
        "already_exists" => WebText.T("记录已存在","Already exists"), "service_error" => WebText.T("服务异常","Service error"),
        _ when code.StartsWith("http_",StringComparison.Ordinal) => WebText.T("请求失败","Request failed") + " · HTTP " + code[5..],
        _ => code };
    public static string ClientName(string name) => name switch {
        "Core 后台" => WebText.T("Core 后台","Core console"), "未采集" => WebText.T("未采集","Not collected"),
        "未上报" => WebText.T("未上报","Not reported"), _ => ActivityClientNames.Product(name) };
    public static string Entry(string? code) => code switch { "mcp" => "MCP", "cli" => WebText.T("命令行","Command line"), "gui" => WebText.T("桌面客户端","Desktop client"),
        "background" => WebText.T("后台续传","Background sync"), "admin" => WebText.T("Core 后台","Core console"), _ => WebText.T("未上报","Not reported") };
}

public sealed record GrantRow(ManagedDevice Device, ProjectEntry Project, bool ShowProject);
public sealed record ActivityPanel(IReadOnlyList<AdminActivity> Rows, ActivityCoverage Coverage, Guid? ProjectId, Guid? DeviceId,
    string View, ActivityFilter Clients, ActivityFilter Actions, ActivityFilter Results, string? Since, string? Until, long? Before, long? Next,
    IReadOnlyList<ManagedDevice> FilterDevices);

public sealed record ActivityFilter(string Key, string Label, string All, string[] Options, string[] Selected,
    Func<string,string> Display, StringComparer Comparer);
