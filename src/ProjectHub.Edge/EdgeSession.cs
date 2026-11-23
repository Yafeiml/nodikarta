using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ProjectHub.Edge;

public sealed record DeviceIdentity(Uri CoreUrl, Guid DeviceId, Guid EdgeId)
{
    internal Guid Profile { get; init; }
}
public sealed record DeviceProject(Guid ProjectId, string Name, Guid BindingId, string AccessMode);
public sealed record LocalBinding(Guid ProjectId, Guid BindingId, Guid WorkspaceId, string LocalPath, string AccessMode, string Name);

public sealed partial class EdgeSession
{
    private sealed record Tokens(Guid DeviceId, Guid EdgeId, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
    private sealed record Credentials(Uri Core, Tokens Tokens, string Status);
    private sealed record ProjectList(DeviceProject[] Items);
    private sealed class PairingRejected(string message) : InvalidOperationException(message);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string connectionString;
    private readonly EdgeRequestSource defaultSource;
    internal EdgeRequestSource RequestSource => EdgeRequestSource.Current ?? defaultSource;
    public string StateDirectory { get; }

    private EdgeSession(string root, Guid profile, EdgeRequestSource defaultSource)
    {
        this.defaultSource = defaultSource;
        RootDirectory = root;
        Profile = profile;
        var directory = StateDirectory = ProfilePath(root, profile);
        if (profile != Guid.Empty) EnsurePrivateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder {
            DataSource = Path.Combine(directory, "edge.sqlite3"), Pooling = false, DefaultTimeout = 15
        }.ToString();
        InitializeStorage();
    }

    internal static void EnsurePrivateDirectory(string directory)
    {
        if (directory.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(directory)!).DriveType == DriveType.Network)
            throw new InvalidOperationException("Edge 的 SQLite 数据目录必须位于本机磁盘。");
        var folder = new DirectoryInfo(directory);
        if (!folder.Exists)
        {
            using var windowsIdentity = WindowsIdentity.GetCurrent();
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { windowsIdentity.User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                         new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }.Distinct())
                acl.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            folder.Create(acl);
            folder.Refresh();
        }
        var security = folder.GetAccessControl();
        var broad = new[] { "S-1-1-0", "S-1-5-11", "S-1-5-32-545", "S-1-5-32-546", "S-1-5-4", "S-1-5-6", "S-1-5-7" };
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint) || !security.AreAccessRulesProtected ||
            rules.Any(rule => rule.AccessControlType == AccessControlType.Allow && broad.Contains(rule.IdentityReference.Value)))
            throw new InvalidOperationException("Edge 数据目录需要独立的受限 NTFS 权限。请选择尚未创建的专用子目录或运行安装脚本。");
    }

    private void InitializeStorage()
    {
        using var db = ConnectStorage();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        if (Convert.ToInt64(cmd.ExecuteScalar()) > 1) throw new InvalidOperationException("当前 Edge 无法打开更高版本的数据，请使用新版程序。");
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            BEGIN IMMEDIATE;
            CREATE TABLE IF NOT EXISTS identity (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), core TEXT NOT NULL,
                credentials BLOB NOT NULL, status TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS workspaces (
                device_id TEXT NOT NULL, project_id TEXT NOT NULL, local_path TEXT COLLATE NOCASE NOT NULL,
                workspace_id TEXT NOT NULL UNIQUE, PRIMARY KEY(device_id,project_id,local_path)
            );
            CREATE TABLE IF NOT EXISTS workspace_access (
                workspace_id TEXT PRIMARY KEY, tools TEXT, endpoint TEXT
            );
            CREATE TABLE IF NOT EXISTS device_label (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), device_id TEXT NOT NULL, name TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS antigravity_access (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), command TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS antigravity_pending (
                workspace_id TEXT PRIMARY KEY, project_id TEXT NOT NULL, local_path TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS antigravity_disabled (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), command TEXT NOT NULL, registration_pending INTEGER NOT NULL
            );
            PRAGMA user_version=1;
            COMMIT;
            """;
        cmd.ExecuteNonQuery();
    }

    internal SqliteConnection Connect()
    {
        EnsureActiveProfile();
        return ConnectStorage();
    }

    private SqliteConnection ConnectStorage()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private Credentials? ReadCredentials()
    {
        EnsureActiveProfile();
        return ReadStoredCredentials();
    }

    private Credentials? ReadStoredCredentials()
    {
        using var db = ConnectStorage();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT core,credentials,status FROM identity WHERE singleton=1";
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return null;
        var plain = ProtectedData.Unprotect((byte[])row.GetValue(1), null, DataProtectionScope.LocalMachine);
        try { return new(new Uri(row.GetString(0)), JsonSerializer.Deserialize<Tokens>(plain)!, row.GetString(2)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private Credentials Required() => ReadCredentials() ?? throw new InvalidOperationException("尚未配对。请先运行 pair。");
    internal DeviceIdentity? GetIdentityOrDefault()
    {
        var device = ReadCredentials();
        return device is null ? null : new(device.Core, device.Tokens.DeviceId, device.Tokens.EdgeId) { Profile = Profile };
    }
    public DeviceIdentity GetIdentity() => GetIdentityOrDefault() ?? throw new InvalidOperationException("尚未配对。请先运行 pair。");

    internal string? GetDeviceLabel(Guid deviceId)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM device_label WHERE singleton=1 AND device_id=$id";
        cmd.Parameters.AddWithValue("$id", deviceId.ToString("D"));
        return cmd.ExecuteScalar() as string;
    }

    private void Save(Credentials device, string? label = null)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(device.Tokens);
        byte[] secret;
        try { secret = ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        using var db = ConnectStorage();
        using var transaction = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO identity(singleton,core,credentials,status) VALUES(1,$core,$credentials,$status)
            ON CONFLICT(singleton) DO UPDATE SET core=excluded.core,credentials=excluded.credentials,status=excluded.status
            """;
        cmd.Parameters.AddWithValue("$core", device.Core.AbsoluteUri);
        cmd.Parameters.AddWithValue("$credentials", secret);
        cmd.Parameters.AddWithValue("$status", device.Status);
        cmd.ExecuteNonQuery();
        if (label is not null)
        {
            cmd.CommandText = "INSERT OR REPLACE INTO device_label(singleton,device_id,name) VALUES(1,$id,$label)";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", device.Tokens.DeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$label", label.Trim());
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private Task<FileStream> LockAsync(CancellationToken ct) => AcquireProfileLockAsync("identity.lock", ct);

    private async Task<FileStream> AcquireProfileLockAsync(string name, CancellationToken ct)
    {
        var file = Path.Combine(RootDirectory, name);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 32 && attempt < 450)
            { await Task.Delay(100, ct).ConfigureAwait(false); }
        }
    }

    internal static Uri ValidateCoreAddress(Uri core)
    {
        if (!core.IsAbsoluteUri || core.Scheme is not ("http" or "https") ||
            core.AbsolutePath != "/" || core.Query.Length != 0 || core.Fragment.Length != 0 || core.UserInfo.Length != 0)
            throw new ArgumentException("请输入 HTTP 或 HTTPS 的 Core 根地址，仅包含主机和可选端口。");
        return core;
    }

    public async Task<DeviceIdentity> PairAsync(Uri core, string code, string label, bool replace, CancellationToken ct = default,
        DeviceIdentity? expected = null)
    {
        ValidateCoreAddress(core);
        var parts = code.Trim().Split('.');
        var update = parts.Length == 3 && parts[0] == "U1";
        if ((!update && parts.Length != 2) || !Guid.TryParse(parts[^2], out var id) || parts[^1].Length != 32 || !parts[^1].All(Uri.IsHexDigit))
            throw new ArgumentException("配对码格式无效。");
        using var locked = await LockAsync(ct);
        EnsureActiveProfile();
        if (expected != null && GetIdentityOrDefault() != expected) throw new InvalidOperationException("设备身份已更改，请重新确认配对。");
        Guid? requested = update ? ReplacementProfileId(core, id, parts[^1]) : null;
        if (PendingReplacementId() is { } pending)
        {
            var target = new EdgeSession(RootDirectory, pending, defaultSource);
            if (pending == requested) return await FinishReplacementAsync(target, ct);
            await ReconcileOtherReplacementAsync(target, ct);
        }
        if (requested == Profile) return GetIdentity();
        if (!replace && ReadCredentials() != null) throw new InvalidOperationException("此 Edge 已配对。需要替换现有身份时请明确使用 --replace。");
        if (requested.HasValue) return await UpdateDeviceAsync(core, id, parts[^1], label, requested.Value, ct);
        if (replace) return await ReplaceProfileAsync(core, id, parts[1], label, ct);
        var tokens = await RedeemAsync(core, id, parts[1], label, ct);
        Save(new(core, tokens, "active"), label);
        return new(core, tokens.DeviceId, tokens.EdgeId) { Profile = Profile };
    }

    private async Task<Tokens> RedeemAsync(Uri core, Guid id, string secret, string label, CancellationToken ct)
    {
        using var response = await PostCoreJsonAsync(new Uri(core, $"/v1/device-pairings/{id:D}/redeem"),
            new { oneTimeCode = secret, deviceLabel = label, instanceNonce = Guid.NewGuid().ToString("N") }, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new PairingRejected("配对码无效、已过期或已使用，请在 Core 的设备管理页重新生成。");
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new PairingRejected("Core 拒绝了配对信息，请核对设备名称和配对码。");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Core 拒绝兑换配对码，HTTP {(int)response.StatusCode}。");
        return await ReceiveTokensAsync(response, ct);
    }

    private static async Task<Tokens> ReceiveTokensAsync(HttpResponseMessage response, CancellationToken ct, bool acceptExpired = false)
    {
        var tokens = await response.Content.ReadFromJsonAsync<Tokens>(cancellationToken: ct);
        if (tokens is not { DeviceId: var id, EdgeId: var edge, AccessToken.Length: 64, RefreshToken.Length: 64 } ||
            id == Guid.Empty || edge == Guid.Empty || tokens.ExpiresAt == default || (!acceptExpired && tokens.ExpiresAt <= DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Core 返回的设备凭据不完整，请核对服务时间和协议。");
        return tokens;
    }

    private async Task<Credentials?> RenewAsync(Credentials previous, CancellationToken ct)
    {
        using var locked = await LockAsync(ct);
        var device = Required();
        if (device.Tokens.DeviceId != previous.Tokens.DeviceId || device.Status == "invalid") return null;
        if (device.Status == "pending") throw new InvalidOperationException("上次凭据轮换结果未知。请在 Core 为此设备生成更新码，重新配对。");
        if (device.Tokens.AccessToken != previous.Tokens.AccessToken) return device;
        // A disconnected Edge can probe again later without risking replay of a possibly spent refresh token.
        using var probe = new HttpRequestMessage(HttpMethod.Get, new Uri(device.Core, "/v1/projects"));
        EdgeRequestSource.Background.ApplyTo(probe);
        probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.Tokens.AccessToken);
        using var reachable = await Client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!reachable.IsSuccessStatusCode && reachable.StatusCode != HttpStatusCode.Unauthorized)
            reachable.EnsureSuccessStatusCode();
        // Core revokes the device when a spent refresh token is replayed. Persist intent before sending it.
        Save(device with { Status = "pending" });
        using var response = await PostCoreJsonAsync(new Uri(device.Core, "/v1/tokens/refresh"),
            new { refreshToken = device.Tokens.RefreshToken }, ct, EdgeRequestSource.Background);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { Save(device with { Status = "invalid" }); return null; }
        if (response.StatusCode == HttpStatusCode.TooManyRequests) Save(device);
        response.EnsureSuccessStatusCode();
        var tokens = await ReceiveTokensAsync(response, ct);
        if (tokens.DeviceId != device.Tokens.DeviceId || tokens.EdgeId != device.Tokens.EdgeId)
            throw new InvalidOperationException("Core 的设备身份与当前绑定不一致。");
        var updated = new Credentials(device.Core, tokens, "active");
        Save(updated);
        return updated;
    }

    private async Task<HttpResponseMessage> PostCoreJsonAsync<T>(Uri uri, T body, CancellationToken ct, EdgeRequestSource? source = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) };
        (source ?? RequestSource).ApplyTo(request);
        return await Client.SendAsync(request, ct);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, CancellationToken ct = default,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var device = Required();
        var source = RequestSource;
        if (!path.StartsWith("/v1/", StringComparison.Ordinal) || path.Contains('\\') ||
            !Uri.TryCreate(device.Core, path, out var target) || !target.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal))
            throw new ArgumentException("设备请求必须指向当前 Core 的 /v1/ 接口。");
        if (device.Status == "invalid") return new(HttpStatusCode.Unauthorized);
        if (device.Status != "active" || device.Tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(45))
            device = await RenewAsync(device, ct);
        if (device is null) return new(HttpStatusCode.Unauthorized);
        if (content != null) await content.LoadIntoBufferAsync(ct);
        async Task<HttpResponseMessage> Send(string token)
        {
            using var request = new HttpRequestMessage(method, target) { Content = content };
            source.ApplyTo(request);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try { return await Client.SendAsync(request, completion, ct); }
            finally { request.Content = null; }
        }
        var result = await Send(device.Tokens.AccessToken);
        if (result.StatusCode != HttpStatusCode.Unauthorized) return result;
        try
        {
            var updated = await RenewAsync(device, ct);
            if (updated is null) return result;
            var retry = await Send(updated.Tokens.AccessToken);
            result.Dispose();
            return retry;
        }
        catch { result.Dispose(); throw; }
    }

    public async Task<IReadOnlyList<DeviceProject>> ProjectsAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/projects", ct: ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectList>(cancellationToken: ct))?.Items
            ?? throw new InvalidOperationException("Core 没有返回项目列表。");
    }

    public async Task<LocalBinding> BindAsync(Guid projectId, string path, CancellationToken ct = default)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("请选择当前主机上的现有目录。");
        var canonical = WorkspacePath.Verified(directory);
        var identity = GetIdentity();
        var project = (await ProjectsAsync(ct)).SingleOrDefault(p => p.ProjectId == projectId)
            ?? throw new InvalidOperationException("此项目未授权或已移除。");
        using var db = Connect();
        using var transaction = db.BeginTransaction(deferred: false);
        var existing = LocalWorkspaces(db, transaction, identity.DeviceId);
        if (existing.Any(row => row.ProjectId != projectId && WorkspacePath.Overlaps(row.LocalPath, directory, canonical)))
            throw new InvalidOperationException("该目录或其上、下级目录已绑定到其他项目。请选择独立目录。");
        var alias = existing.FirstOrDefault(row => row.ProjectId == projectId &&
            string.Equals(WorkspacePath.TryFinal(row.LocalPath), canonical, StringComparison.Ordinal));
        if (alias != null)
        {
            QueueAntigravity(db, transaction, alias);
            transaction.Commit();
            FinishAntigravityBinding(alias);
            return alias with { BindingId = project.BindingId, AccessMode = project.AccessMode, Name = project.Name };
        }
        if (existing.Any(row => row.ProjectId == projectId && row.LocalPath.Equals(directory, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("目录与已有绑定仅大小写不同，实际位置不一致。");
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO workspaces(device_id, project_id, local_path, workspace_id) VALUES($device,$project,$path,$workspace)
            ON CONFLICT(device_id,project_id,local_path) DO UPDATE SET local_path=excluded.local_path RETURNING workspace_id
            """;
        cmd.Parameters.AddWithValue("$device", identity.DeviceId.ToString("D"));
        cmd.Parameters.AddWithValue("$project", projectId.ToString("D"));
        cmd.Parameters.AddWithValue("$path", directory);
        cmd.Parameters.AddWithValue("$workspace", Guid.NewGuid().ToString("D"));
        var workspace = Guid.Parse((string)cmd.ExecuteScalar()!);
        var binding = new LocalBinding(projectId, project.BindingId, workspace, directory, project.AccessMode, project.Name);
        QueueAntigravity(db, transaction, binding);
        transaction.Commit();
        FinishAntigravityBinding(binding);
        return binding;
    }

    public async Task<IReadOnlyList<LocalBinding>> BindingsAsync(CancellationToken ct = default)
    {
        var identity = GetIdentity();
        return ReadBindings(identity.DeviceId, await ProjectsAsync(ct));
    }

    public void Unbind(Guid projectId, Guid workspaceId, string localPath)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localPath));
        using var db = Connect();
        using var transaction = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            DELETE FROM workspaces WHERE device_id=$device AND project_id=$project AND workspace_id=$workspace AND local_path=$path
            RETURNING local_path
            """;
        cmd.Parameters.AddWithValue("$device", GetIdentity().DeviceId.ToString("D"));
        cmd.Parameters.AddWithValue("$project", projectId.ToString("D"));
        cmd.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        cmd.Parameters.AddWithValue("$path", directory);
        if (cmd.ExecuteScalar() is not string originalPath)
            throw new InvalidOperationException("此目录绑定已变化，请刷新后重试。");
        // Retire legacy routes durably before releasing this directory for reuse.
        MvpDispatch.RemoveWorkspaceRoutes(StateDirectory, projectId, workspaceId, originalPath);
        cmd.CommandText = "DELETE FROM workspace_access WHERE workspace_id=$workspace";
        cmd.ExecuteNonQuery();
        var binding = new LocalBinding(projectId, Guid.Empty, workspaceId, originalPath, "", "");
        QueueAntigravity(db, transaction, binding);
        transaction.Commit();
        try { FlushAntigravity(originalPath); }
        catch (IOException ex) { throw new IOException("目录已解绑，项目访问已撤销。Antigravity 文件清理待重试：" + ex.Message, ex); }
    }

    internal IReadOnlyList<LocalBinding> ReadBindings(Guid deviceId, IReadOnlyList<DeviceProject> authorizedProjects)
    {
        var projects = authorizedProjects.ToDictionary(p => p.ProjectId);
        using var db = Connect();
        var bindings = new List<LocalBinding>();
        foreach (var row in LocalWorkspaces(db, null, deviceId))
            if (projects.TryGetValue(row.ProjectId, out var p))
                bindings.Add(row with { BindingId = p.BindingId, AccessMode = p.AccessMode, Name = p.Name });
        return bindings;
    }
}
