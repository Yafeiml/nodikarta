namespace ProjectHub.Edge.Desktop;

internal sealed record DesktopSnapshot(DeviceIdentity? Identity, IReadOnlyList<DeviceProject> Projects,
    IReadOnlyList<LocalBinding> Bindings);

internal sealed class EdgeDesktopClient
{
    private EdgeSession session;
    private readonly object profileLock = new();
    internal EdgeSession Session { get { lock (profileLock) return session = session.CurrentProfile(); } }
    public string StateDirectory { get; }

    public IReadOnlyList<McpListenerStatus>? ReadMcpListeners()
    {
        try { return McpListenerStatus.Read(Session.StateDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public EdgeDesktopClient(string stateDirectory)
    {
        session = EdgeSession.Open(stateDirectory, pinLegacyProfile: false, source: EdgeRequestSource.Gui);
        StateDirectory = session.RootDirectory;
    }

    public DeviceIdentity? GetIdentity() => Session.GetIdentityOrDefault();
    internal EdgeSession SessionFor(DeviceIdentity? expected)
    {
        var current = Session;
        if (expected == null || current.GetIdentityOrDefault() != expected)
            throw new InvalidOperationException("设备身份已更改，请刷新后再试。");
        return current;
    }
    public string GetDeviceName(Guid deviceId) =>
        Session.GetDeviceLabel(deviceId) is { Length: > 0 } name ? name : Environment.MachineName;

    public async Task<DesktopSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        var current = Session;
        var identity = current.GetIdentityOrDefault();
        if (identity is null) return new(null, [], []);
        var projects = await current.ProjectsAsync(ct);
        if (GetIdentity() != identity)
            throw new InvalidOperationException("设备身份已更改，请刷新后再试。");
        return new(identity, projects, current.ReadBindings(identity.DeviceId, projects));
    }

    internal static Uri ParseCoreAddress(string coreAddress)
    {
        if (!Uri.TryCreate(coreAddress.Trim(), UriKind.Absolute, out var core))
            throw new ArgumentException("请输入完整的 Core 地址。");
        return EdgeSession.ValidateCoreAddress(core);
    }

    public Task<DeviceIdentity> PairAsync(string coreAddress, string pairingCode, string label, CancellationToken ct = default) =>
        PairAsync(null, coreAddress, pairingCode, label, ct);

    public Task<DeviceIdentity> ReplacePairingAsync(DeviceIdentity expected, string coreAddress, string pairingCode, string label,
        CancellationToken ct = default) => PairAsync(expected, coreAddress, pairingCode, label, ct);

    private async Task<DeviceIdentity> PairAsync(DeviceIdentity? expected, string coreAddress, string pairingCode, string label, CancellationToken ct)
    {
        label = label.Trim();
        if (label.Length > 120 || label.Any(char.IsControl))
            throw new ArgumentException("设备名称最多可填写 120 个字符，且不能包含控制字符。");
        var current = Session;
        try { return await current.PairAsync(ParseCoreAddress(coreAddress), pairingCode, label, replace: expected != null, ct, expected); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested && current.HasPendingDeviceUpdate)
        {
            throw new InvalidOperationException("设备接替等待 Core 确认。连接恢复后 Edge 会自动继续，也可用同一码重试。", ex);
        }
    }

    public Task<LocalBinding> BindAsync(Guid projectId, string directory, CancellationToken ct = default) =>
        Session.BindAsync(projectId, directory, ct);

    public Task RunAsync(CancellationToken ct) => ProjectHub.Edge.Program.KeepConnectedAsync(Session, ct);
}
