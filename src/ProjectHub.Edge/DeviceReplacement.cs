using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjectHub.Edge;

public sealed partial class EdgeSession
{
    private const string AttemptFile = "device-update.attempt";
    private string PendingReplacementFile => Path.Combine(StateDirectory, "pending-device-update");
    internal bool HasPendingDeviceUpdate => File.Exists(PendingReplacementFile);
    internal static bool IsUpdateCode(string? code) => code?.TrimStart().StartsWith("U1.", StringComparison.Ordinal) == true;

    private sealed record ReplacementAttempt(Uri Core, Guid PairingId, string Code, string Label,
        string InstanceNonce, string AttemptSecret, Tokens? Candidate);
    private sealed record ReplacementError(string Code);
    private sealed record ReplacementProblem(ReplacementError? Error);

    private static void StoreAttempt(string path, ReplacementAttempt attempt)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(attempt);
        try { WriteDurably(path, ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static ReplacementAttempt ReadAttempt(string path)
    {
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.LocalMachine);
        try { return JsonSerializer.Deserialize<ReplacementAttempt>(plain) ?? throw new InvalidDataException("接替记录不完整。"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static Guid ReplacementProfileId(Uri core, Guid pairingId, string secret)
    {
        var key = Encoding.UTF8.GetBytes(core.AbsoluteUri + "\nU1." + pairingId.ToString("N") + "." + secret);
        return new Guid(SHA256.HashData(key).AsSpan(0, 16));
    }

    private async Task<DeviceIdentity> UpdateDeviceAsync(Uri core, Guid pairingId, string secret, string label, Guid targetId, CancellationToken ct)
    {
        var target = new EdgeSession(RootDirectory, targetId, defaultSource);
        var previous = Path.Combine(target.StateDirectory, PreviousFile);
        if (!File.Exists(previous)) WriteProfileMarker(previous, Profile);
        if (File.ReadAllText(previous) != Profile.ToString("N"))
            throw new InvalidOperationException("该配对码已用于此前的设备身份，请生成新码。");
        var path = Path.Combine(target.StateDirectory, AttemptFile);
        if (!File.Exists(path) && target.ReadStoredCredentials() == null)
        {
            StoreAttempt(path, new(core, pairingId, secret, label, Guid.NewGuid().ToString("N"),
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), null));
        }
        WriteProfileMarker(PendingReplacementFile, targetId);
        return await FinishReplacementAsync(target, ct);
    }

    internal async Task ResumeReplacementAsync(CancellationToken ct)
    {
        if (!File.Exists(PendingReplacementFile)) return;
        using var locked = await LockAsync(ct);
        if (IsActiveProfile && PendingReplacementId() is { } id)
            await FinishReplacementAsync(new EdgeSession(RootDirectory, id, defaultSource), ct);
    }

    private Guid? PendingReplacementId()
    {
        string marker;
        try { marker = File.ReadAllText(PendingReplacementFile); }
        catch (FileNotFoundException) { return null; }
        if (!Guid.TryParseExact(marker, "N", out var id))
            throw new InvalidDataException("无法识别待接替的本机身份记录。");
        var path = ProfilePath(RootDirectory, id);
        if (id == Guid.Empty || id == Profile || !Directory.Exists(path) ||
            File.ReadAllText(Path.Combine(path, PreviousFile)) != Profile.ToString("N"))
            throw new InvalidDataException("无法识别待接替的本机身份记录。");
        return id;
    }

    private async Task ReconcileOtherReplacementAsync(EdgeSession target, CancellationToken ct)
    {
        try { await FinishReplacementAsync(target, ct); }
        catch (PairingRejected) { return; } // Only a terminal Core response releases the pending attempt.
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("上次设备接替尚待核实，本次配对码尚未使用。请恢复与上次 Core 的连接后重试。", ex);
        }
        throw new InvalidOperationException("上次设备接替已确认并启用。本次配对码尚未使用，请刷新设备状态后再配对。");
    }

    // Called under the root identity lock. The working profile remains selected until Core returns its commit receipt.
    private async Task<DeviceIdentity> FinishReplacementAsync(EdgeSession target, CancellationToken ct)
    {
        var path = Path.Combine(target.StateDirectory, AttemptFile);
        var saved = target.ReadStoredCredentials();
        if (saved == null)
        {
            var attempt = ReadAttempt(path);
            var uri = new Uri(attempt.Core, $"/v1/device-pairings/{attempt.PairingId:D}/");
            try
            {
                if (attempt.Candidate == null)
                {
                    var candidate = await ReplacementRequestAsync(new Uri(uri, "claim"), new
                    {
                        oneTimeCode = attempt.Code, deviceLabel = attempt.Label,
                        instanceNonce = attempt.InstanceNonce, attemptSecret = attempt.AttemptSecret
                    }, ct, claim: true);
                    attempt = attempt with { Candidate = candidate };
                    StoreAttempt(path, attempt);
                }
                var receipt = await ReplacementRequestAsync(new Uri(uri, "confirm"), new { attemptSecret = attempt.AttemptSecret }, ct);
                if (attempt.Candidate! with { ExpiresAt = receipt.ExpiresAt } != receipt)
                    throw new InvalidDataException("接替回执与已保存的新设备凭据不一致。");
                saved = new(attempt.Core, receipt, "active");
                target.Save(saved, attempt.Label);
            }
            catch (PairingRejected)
            {
                File.Delete(PendingReplacementFile);
                File.Delete(path);
                RemoveRejectedProfile(target, Path.Combine(target.StateDirectory, PreviousFile));
                throw;
            }
        }
        // Once the receipt is saved locally, recovery can finish the switch without another request.
        File.Delete(path);
        WriteProfileMarker(Path.Combine(RootDirectory, SelectionFile), target.Profile);
        File.Delete(PendingReplacementFile);
        return new(saved.Core, saved.Tokens.DeviceId, saved.Tokens.EdgeId) { Profile = target.Profile };
    }

    private async Task<Tokens> ReplacementRequestAsync(Uri uri, object body, CancellationToken ct, bool claim = false)
    {
        using var response = await PostCoreJsonAsync(uri, body, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            // A generic error from an intermediate HTTP proxy must not erase a recoverable confirmation.
            var problem = await response.Content.ReadFromJsonAsync<ReplacementProblem>(cancellationToken: ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && problem?.Error?.Code == "pairing_unavailable")
                throw new PairingRejected("设备更新码已失效或已被其他设备领取，请在 Core 核对设备状态。");
            if (response.StatusCode == HttpStatusCode.BadRequest && problem?.Error?.Code == "invalid_argument")
                throw new PairingRejected("Core 拒绝了配对信息，请核对设备名称和配对码。");
        }
        if (claim && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            throw new PairingRejected("此 Core 不支持设备接替，请核对地址和 Core 版本。");
        response.EnsureSuccessStatusCode();
        // A valid receipt can outlive its access token. Normal token renewal uses the saved refresh token.
        return await ReceiveTokensAsync(response, ct, acceptExpired: true);
    }
}
