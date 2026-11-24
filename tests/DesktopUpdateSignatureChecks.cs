#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectHub.Edge.Desktop;

using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var root = Directory.CreateTempSubdirectory("nodicarta-update-check-");
try
{
    const string build = "001122aabbcc", version = "1.0.0";
    var bin = Directory.CreateDirectory(Path.Combine(root.FullName, build, "bin")).FullName;
    var exe = Path.Combine(bin, "ProjectHub.Edge.Desktop.exe");
    var key = signer.ExportSubjectPublicKeyInfo();
    var publisher = Convert.ToHexString(SHA256.HashData(key));
    var marker = Path.Combine(root.FullName, "public-install.json");
    var pointer = Path.Combine(root.FullName, "active-release");
    void Mark(string id) => File.WriteAllText(marker, JsonSerializer.Serialize(new { schema = 1, product = "Nodicarta Edge", channel = "stable", publisher = id }));
    File.WriteAllText(pointer, build);
    File.WriteAllText(Path.Combine(bin, "nodicarta-release.json"), JsonSerializer.Serialize(new {
        releaseLine = "public-v1", rid = "win-x64", channel = "stable", version, publisherKeyId = publisher }));
    Mark(publisher);
    using var transport = new MockFeed();
    using var client = new HttpClient(transport);
    const string address = "http://127.0.0.1:39180/nodicarta-edge-stable-win-x64.json";
    DesktopUpdates? Open(bool test = true) => DesktopUpdates.ForInstall(exe, version, address, Convert.ToBase64String(key), test, client);
    if (Open(false) != null) throw new Exception("Public build accepted an HTTP loopback feed");
    Mark("foreign");
    if (Open() != null) throw new Exception("Other publisher install accepted");
    Mark(publisher);
    var service = Open() ?? throw new Exception("Trusted QA installation was unavailable");
    var payload = Encoding.ASCII.GetBytes("fixture content, never executable");
    var sha = Convert.ToHexString(SHA256.HashData(payload));
    JsonObject Offer(string target = "1.0.1") => new() { ["schema"] = 1, ["product"] = "nodicarta-edge", ["rid"] = "win-x64",
        ["channel"] = "stable", ["version"] = target, ["url"] = $"http://127.0.0.1:39180/Nodicarta-Edge-{target}-win-x64-setup.exe",
        ["size"] = payload.Length, ["sha256"] = sha };
    void Sign(string text, ECDsa? privateKey = null)
    {
        transport.Manifest = Encoding.UTF8.GetBytes(text);
        var signature = (privateKey ?? signer).SignData(transport.Manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        transport.Signature = Encoding.ASCII.GetBytes(Convert.ToBase64String(signature) + "\n");
    }
    async Task Rejected(Func<Task> action, string why)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidDataException or CryptographicException or JsonException or HttpRequestException or
                                      IOException or InvalidOperationException or FormatException) { return; }
        throw new Exception("Accepted " + why);
    }
    var ct = CancellationToken.None;
    Sign(Offer().ToJsonString());
    var release = await service.CheckAsync(ct) ?? throw new Exception("Missing genuine newer release");
    await Rejected(() => service.InstallAsync(release with { Size = 10 }, ct), "a forged caller release");
    Sign(Offer().ToJsonString(), wrong);
    await Rejected(() => service.CheckAsync(ct), "a foreign signer");
    Sign(Offer().ToJsonString());
    transport.Manifest = [..transport.Manifest, (byte)' '];
    await Rejected(() => service.CheckAsync(ct), "a changed manifest byte");
    foreach (var target in new[] { "1.0.0", "0.9.9" })
    {
        Sign(Offer(target).ToJsonString());
        if (await service.CheckAsync(ct) != null) throw new Exception("Suggested same/older release " + target);
    }
    foreach (var (field, value) in new[] { ("product","other"), ("channel","preview"), ("channel","rehearsal"), ("rid","linux-x64"), ("version","01.0.1"),
        ("url","http://127.0.0.1:39181/Nodicarta-Edge-1.0.1-win-x64-setup.exe"), ("sha256", "bad") })
    {
        var message = Offer();
        message[field] = value;
        Sign(message.ToJsonString());
        await Rejected(() => service.CheckAsync(ct), "signed but invalid " + field);
    }
    Sign(Offer().ToJsonString().Replace("{", "{\"schema\":1,"));
    await Rejected(() => service.CheckAsync(ct), "duplicate property");
    Sign(Offer().ToJsonString());
    transport.Redirect = new("http://localhost:39181/same-name");
    await Rejected(() => service.CheckAsync(ct), "a cross-origin test redirect");
    transport.Redirect = null;
    foreach (var served in new[] { payload[..^1], payload.Select(b => (byte)(b ^ 1)).ToArray(), [.. payload, (byte)1] })
    {
        transport.Package = served;
        release = (await service.CheckAsync(ct))!;
        await Rejected(() => service.InstallAsync(release, ct), "a truncated, oversized, or altered installer");
        if (Directory.EnumerateFiles(Path.Combine(root.FullName, "updates"), "setup-*").Any())
            throw new Exception("Bad download was left staged");
    }
    release = (await service.CheckAsync(ct))!;
    File.WriteAllText(pointer, "001122aabbdd");
    await Rejected(() => service.InstallAsync(release, ct), "an inactive installation");
    File.WriteAllText(pointer, build);
    var attempt = Path.Combine(root.FullName, "updates", "last-attempt");
    File.WriteAllText(attempt, "1.0.1 " + sha);
    if (await service.CheckAsync(ct, automatic:true) != null || await service.CheckAsync(ct) == null)
        throw new Exception("Rollback cooldown or a manual retry failed");
    File.SetLastWriteTimeUtc(attempt, DateTime.UtcNow.AddDays(-2));
    if (await service.CheckAsync(ct, automatic:true) == null) throw new Exception("Automatic retry never recovered");
    Console.WriteLine("PASS: trusted feed identity, exact-byte signing, architecture/channel/version checks, bounded download, corruption, handoff guard and retry.");
}
finally { root.Delete(true); }

sealed class MockFeed : HttpMessageHandler
{
    internal byte[] Manifest = [], Signature = [], Package = [];
    internal Uri? Redirect;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        if (Redirect != null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = Redirect } });
        var bytes = url.AbsolutePath.EndsWith(".sig") ? Signature : url.AbsolutePath.EndsWith(".json") ? Manifest : Package;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
