using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjectHub.Edge.Desktop;

internal sealed record DesktopUpdateRelease(Version Version, Uri Installer, string Sha256, long Size);

internal sealed class DesktopUpdates
{
    internal const string Channel = "stable";
    private const long MaxInstallerSize = 512L * 1024 * 1024;
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false, MaxDepth = 8 };
    private static readonly HttpClient Network = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string root, build;
    private readonly Version current;
    private readonly Uri feed;
    private readonly byte[] publicKey;
    private readonly bool testChannel;
    private readonly HttpClient http;
    private DesktopUpdateRelease? verified;

    private DesktopUpdates(string root, string build, Version current, Uri feed, byte[] publicKey, bool testChannel, HttpClient http)
    {
        this.root = root;
        this.build = build;
        this.current = current;
        this.feed = feed;
        this.publicKey = publicKey;
        this.testChannel = testChannel;
        this.http = http;
    }

    private static string? Metadata(string name) => typeof(DesktopUpdates).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == name)?.Value;

    internal static string? PublisherKeyId => string.IsNullOrEmpty(Metadata("NodicartaUpdatePublicKey")) ? null :
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(Metadata("NodicartaUpdatePublicKey")!)));

    internal static DesktopUpdates? ForCurrentInstall() => Metadata("NodicartaReleaseLine") != "public-v1" ? null :
        ForInstall(Environment.ProcessPath!, Program.ReleaseVersion, Metadata("NodicartaUpdateFeed"),
            Metadata("NodicartaUpdatePublicKey"), Metadata("NodicartaUpdateTestChannel") == "true", Network);

    internal static DesktopUpdates? ForInstall(string executable, string version, string? feedAddress, string? key,
        bool testChannel, HttpClient http)
    {
        if (string.IsNullOrEmpty(key) || !Uri.TryCreate(feedAddress, UriKind.Absolute, out var feed) ||
            !TryVersion(version, out var current) || !string.IsNullOrEmpty(feed.Query) ||
            !feed.AbsolutePath.EndsWith("/nodicarta-edge-stable-win-x64.json", StringComparison.Ordinal)) return null;
        var file = new FileInfo(executable);
        var release = file.Directory?.Parent;
        if (file.Name != "ProjectHub.Edge.Desktop.exe" || file.Directory?.Name != "bin" || release?.Parent == null ||
            release.Name.Length != 12 || !release.Name.All(char.IsAsciiHexDigitLower)) return null;
        try
        {
            var service = new DesktopUpdates(release.Parent.FullName, release.Name, current!, feed,
                Convert.FromBase64String(key), testChannel, http);
            using var publisher = service.OpenPublisher();
            if (!service.AllowedAddress(feed) || !service.IsCurrentInstall()) return null;
            return service;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or
                                     CryptographicException or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    private bool IsCurrentInstall()
    {
        if (root.IndexOfAny(['"', '\r', '\n']) >= 0 ||
            Encoding.ASCII.GetString(ReadLocal(Path.Combine(root, "active-release"), 12)) != build) return false;
        using var install = JsonDocument.Parse(ReadLocal(Path.Combine(root, "public-install.json"), 8192), StrictJson);
        var mark = install.RootElement;
        var publisher = Convert.ToHexString(SHA256.HashData(publicKey));
        if (mark.GetProperty("schema").GetInt32() != 1 || mark.GetProperty("product").GetString() != "Nodicarta Edge" ||
            mark.GetProperty("channel").GetString() != Channel ||
            !string.Equals(mark.GetProperty("publisher").GetString(), publisher, StringComparison.OrdinalIgnoreCase)) return false;
        using var release = JsonDocument.Parse(ReadLocal(Path.Combine(root, build, "bin", "nodicarta-release.json"), 8192), StrictJson);
        var stamp = release.RootElement;
        return stamp.GetProperty("releaseLine").GetString() == "public-v1" && stamp.GetProperty("channel").GetString() == Channel &&
            stamp.GetProperty("rid").GetString() == "win-x64" && stamp.GetProperty("version").GetString() == current.ToString() &&
            string.Equals(stamp.GetProperty("publisherKeyId").GetString(), publisher, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ReadLocal(string path, int limit)
    {
        CodexAccessFile.CheckPath(path);
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > limit) throw new InvalidDataException("Oversized update metadata.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    private bool AllowedAddress(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (!testChannel) return uri.Scheme == Uri.UriSchemeHttps && !uri.IsLoopback;
        return uri.IsLoopback && uri.Scheme is "http" or "https" && uri.GetLeftPart(UriPartial.Authority) == feed.GetLeftPart(UriPartial.Authority);
    }

    private static bool TryVersion(string? value, [NotNullWhen(true)] out Version? version) =>
        Version.TryParse(value, out version) && version.Build >= 0 && version.Revision == -1 && version.ToString() == value;

    private ECDsa OpenPublisher()
    {
        var publisher = ECDsa.Create();
        try
        {
            publisher.ImportSubjectPublicKeyInfo(publicKey, out int read);
            if (read != publicKey.Length || publisher.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new CryptographicException("An ECDSA P-256 publisher key is required.");
            return publisher;
        }
        catch { publisher.Dispose(); throw; }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct)
    {
        for (int hop = 0; hop <= 5; hop++)
        {
            if (!AllowedAddress(url)) throw new InvalidDataException(UiLanguage.Text("更新地址不可信。", "Untrusted update address."));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd($"Nodicarta-Edge/{current}");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK) return response;
            using (response)
            {
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is Uri location)
                    url = new Uri(url, location);
                else
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
            }
        }
        throw new HttpRequestException(UiLanguage.Text("更新地址重定向次数过多。", "Too many update redirects."));
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        byte[] buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException(UiLanguage.Text("更新文件大小超出限制。", "Update file exceeds its size limit."));
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        return total;
    }

    private async Task<byte[]> SmallFileAsync(Uri uri, int limit, CancellationToken ct)
    {
        using var response = await GetAsync(uri, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Oversized update metadata.");
        using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var result = new MemoryStream();
        await CopyBoundedAsync(input, result, limit, ct).ConfigureAwait(false);
        return result.ToArray();
    }

    internal async Task<DesktopUpdateRelease?> CheckAsync(CancellationToken ct, bool automatic = false)
    {
        verified = null;
        try { CleanOldInstallers(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace.TraceWarning("Update cache: {0}", e.Message); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        byte[] manifest = await SmallFileAsync(feed, 32768, timeout.Token).ConfigureAwait(false);
        byte[] signatureText = await SmallFileAsync(new Uri(feed.AbsoluteUri + ".sig"), 256, timeout.Token).ConfigureAwait(false);
        using var publisher = OpenPublisher();
        var signature = Convert.FromBase64String(Encoding.ASCII.GetString(signatureText));
        if (signature.Length != 64 || !publisher.VerifyData(manifest, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException(UiLanguage.Text("无法验证更新发布者的签名。", "The update publisher signature could not be verified."));
        using var doc = JsonDocument.Parse(manifest, StrictJson);
        var m = doc.RootElement;
        if (m.GetProperty("schema").GetInt32() != 1 || m.GetProperty("product").GetString() != "nodicarta-edge" ||
            m.GetProperty("channel").GetString() != Channel || m.GetProperty("rid").GetString() != "win-x64" ||
            !TryVersion(m.GetProperty("version").GetString(), out var version))
            throw new InvalidDataException(UiLanguage.Text("更新清单与此版本不兼容。", "Incompatible update manifest."));
        var hash = m.GetProperty("sha256").GetString();
        long size = m.GetProperty("size").GetInt64();
        if (hash == null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit) || size is <= 0 or > MaxInstallerSize ||
            !Uri.TryCreate(m.GetProperty("url").GetString(), UriKind.Absolute, out var installer) || !AllowedAddress(installer) ||
            !installer.AbsolutePath.EndsWith($"/Nodicarta-Edge-{version}-win-x64-setup.exe", StringComparison.Ordinal))
            throw new InvalidDataException(UiLanguage.Text("更新清单中的安装包无效。", "Invalid installer in update manifest."));
        if (version <= current) return null;
        var release = new DesktopUpdateRelease(version, installer, hash.ToUpperInvariant(), size);
        if (automatic && AttemptedRecently(release)) return null;
        verified = release;
        return release;
    }

    private string Cache => Path.Combine(root, "updates");
    private string Attempt => Path.Combine(Cache, "last-attempt");

    private bool AttemptedRecently(DesktopUpdateRelease release)
    {
        try
        {
            return File.GetLastWriteTimeUtc(Attempt) > DateTime.UtcNow.AddDays(-1) &&
                Encoding.ASCII.GetString(ReadLocal(Attempt, 256)) == $"{release.Version} {release.Sha256}";
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal async Task InstallAsync(DesktopUpdateRelease release, CancellationToken ct)
    {
        if (!ReferenceEquals(release, verified) || release.Version <= current || !IsCurrentInstall())
            throw new InvalidOperationException(UiLanguage.Text("请先重新检查更新。", "Check for updates again."));
        CodexAccessFile.CheckPath(Cache);
        Directory.CreateDirectory(Cache);
        string staged = Path.Combine(Cache, "setup-" + Guid.NewGuid().ToString("N") + ".exe");
        var attemptTemp = staged + ".attempt";
        var handedOff = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var token = timeout.Token;
            using (var response = await GetAsync(release.Installer, token).ConfigureAwait(false))
            {
                if (response.Content.Headers.ContentLength is long length && length != release.Size) throw SizeMismatch();
                using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                if (await CopyBoundedAsync(input, output, release.Size, token).ConfigureAwait(false) != release.Size) throw SizeMismatch();
            }
            // Keep the verified file locked against writes and replacement until Windows opens the executable.
            using var installer = File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = await SHA256.HashDataAsync(installer, token).ConfigureAwait(false);
            if (Convert.ToHexString(actual) != release.Sha256)
                throw new CryptographicException(UiLanguage.Text("更新包校验失败。", "The installer checksum does not match."));
            token.ThrowIfCancellationRequested();
            if (!IsCurrentInstall()) throw new IOException(UiLanguage.Text("当前安装已更改。", "The active installation changed."));
            CodexAccessFile.CheckPath(Attempt);
            await File.WriteAllTextAsync(attemptTemp, $"{release.Version} {release.Sha256}", token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(Interlocked.CompareExchange(ref verified, null, release), release))
                throw new InvalidOperationException(UiLanguage.Text("请先重新检查更新。", "Check for updates again."));
            File.Move(attemptTemp, Attempt, true);
            try
            {
                // NSIS consumes everything after /D= as the path; it must be last and unquoted.
                using var worker = Process.Start(new ProcessStartInfo(staged) { WorkingDirectory = root,
                    UseShellExecute = false, Arguments = "/S /UPDATE /D=" + root });
                if (worker == null) throw new IOException("Could not start the installer.");
                handedOff = true;
            }
            catch { File.Delete(Attempt); throw; }
        }
        finally
        {
            File.Delete(attemptTemp);
            if (!handedOff) File.Delete(staged);
        }
    }

    private void CleanOldInstallers()
    {
        CodexAccessFile.CheckPath(Cache);
        if (!Directory.Exists(Cache)) return;
        foreach (var file in new DirectoryInfo(Cache).EnumerateFiles("setup-*.exe"))
        {
            var name = Path.GetFileNameWithoutExtension(file.Name);
            if (name.Length != 38 || !Guid.TryParseExact(name[6..], "N", out _) ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)) continue;
            try { file.Delete(); }
            catch (IOException) { }
        }
    }

    private static InvalidDataException SizeMismatch() =>
        new(UiLanguage.Text("更新包大小与签名清单不符。", "The installer size does not match the signed manifest."));
}
