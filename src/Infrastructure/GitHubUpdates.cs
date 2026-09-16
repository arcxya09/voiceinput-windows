using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RealtimeTranscription.Infrastructure;

public sealed record GitHubRelease(Version Version, string Tag, string InstallerName, Uri InstallerUrl, long Size, Uri ChecksumsUrl, string? Digest)
{
    public string PageUrl => $"https://github.com/{GitHubUpdates.Repository}/releases/tag/{Tag}";
}
public sealed record DownloadedUpdate(GitHubRelease Release, string Path, string Sha256);

/// <summary>Only stable releases and exact assets from this application's repository are accepted.</summary>
public sealed class GitHubUpdates : IDisposable
{
    public const string Repository = "arcxya09/voiceinput-windows";
    public const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const long MaximumInstallerBytes = 1024L * 1024 * 1024;
    private readonly HttpClient client;
    public GitHubUpdates(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceInput-Updater/2.2.0");
    }
    public static Version? ParseTag(string? tag) => tag != null && Regex.IsMatch(tag, @"^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z") && Version.TryParse(tag[1..], out var version) ? version : null;
    public static GitHubRelease? ParseRelease(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseTag(tag);
        if (version == null || version <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return null;
        string name = $"VoiceInput-Setup-{version.ToString(3)}.exe";
        JsonElement Asset(string filename)
        {
            var matches = root.GetProperty("assets").EnumerateArray().Where(x => x.GetProperty("name").GetString() == filename).ToArray();
            if (matches.Length != 1 || matches[0].GetProperty("state").GetString() != "uploaded") throw new InvalidDataException("新版安装包尚未发布完整，请稍后重试。");
            return matches[0];
        }
        Uri Url(JsonElement asset, string filename)
        {
            string expected = $"https://github.com/{Repository}/releases/download/{tag}/{filename}";
            if (asset.GetProperty("browser_download_url").GetString() != expected) throw new InvalidDataException("更新资源地址校验失败。");
            return new Uri(expected);
        }
        var installer = Asset(name); var sums = Asset("SHA256SUMS.txt");
        long size = installer.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaximumInstallerBytes) throw new InvalidDataException("更新安装包大小异常。");
        string? digest = installer.TryGetProperty("digest", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (digest != null && !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) throw new InvalidDataException("安装包摘要格式异常。");
        return new(version, tag, name, Url(installer, name), size, Url(sums, "SHA256SUMS.txt"), digest?[7..].ToUpperInvariant());
    }
    private static bool AllowedRedirect(Uri url) => url.Scheme == "https" && url.IsDefaultPort && url.UserInfo.Length == 0 &&
        (url.Host == "github.com" || url.Host == "release-assets.githubusercontent.com" || url.Host == "objects.githubusercontent.com");
    private async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken token)
    {
        for (int count = 0; count < 6; count++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location == null) throw new InvalidDataException("更新下载重定向无效。");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (!AllowedRedirect(url)) throw new InvalidDataException("更新下载重定向地址不受信任。");
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode; response.Dispose();
                throw new HttpRequestException(status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ? "GitHub 暂时限制请求频率，请稍后重试。" : $"GitHub 更新请求失败（{(int)status}）。", null, status);
            }
            return response;
        }
        throw new InvalidDataException("更新下载重定向次数过多。");
    }
    private async Task<string> TextAsync(Uri url, int limit, CancellationToken token)
    {
        using var response = await GetAsync(url, token);
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("更新信息过大。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); byte[] buffer = new byte[8192];
        int length;
        while ((length = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + length > limit) throw new InvalidDataException("更新信息过大。");
            output.Write(buffer, 0, length);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    public async Task<GitHubRelease?> CheckAsync(Version current, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return ParseRelease(await TextAsync(new(LatestUrl), 2 * 1024 * 1024, timeout.Token), current); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public static string ReadChecksum(string content, string name)
    {
        var matches = content.TrimStart('\uFEFF').Split('\n').Select(x => Regex.Match(x.TrimEnd('\r'), @"^([0-9a-fA-F]{64}) [ *](.+)$"))
            .Where(x => x.Success && x.Groups[2].Value == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("安装包 SHA-256 校验信息缺失或重复。");
        return matches[0].Groups[1].Value.ToUpperInvariant();
    }
    public async Task<DownloadedUpdate> DownloadAsync(GitHubRelease release, string directory, IProgress<double>? progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(15)); token = timeout.Token;
        string hash = ReadChecksum(await TextAsync(release.ChecksumsUrl, 64 * 1024, token), release.InstallerName);
        if (release.Digest != null && hash != release.Digest) throw new InvalidDataException("GitHub 摘要与校验清单不一致，已停止更新。");
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, release.InstallerName), partial = path + "." + Guid.NewGuid().ToString("N") + ".part";
        if (File.Exists(path))
        {
            try
            {
                var cached = new DownloadedUpdate(release, path, hash);
                await using var verified = await OpenVerifiedAsync(cached, token);
                progress?.Report(100); return cached;
            }
            catch (InvalidDataException) { /* Replace a damaged cache only after the new download passes validation. */ }
        }
        try
        {
            using var response = await GetAsync(release.InstallerUrl, token);
            if (response.Content.Headers.ContentLength is long size && size != release.Size) throw new InvalidDataException("安装包长度与发布信息不一致。");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += count;
                    if (total > release.Size) throw new InvalidDataException("安装包超过预期大小。");
                    sha.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token); progress?.Report(100d * total / release.Size);
                }
                if (total != release.Size || Convert.ToHexString(sha.GetHashAndReset()) != hash) throw new InvalidDataException("安装包校验失败，请重新下载。");
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested(); File.Move(partial, path, true);
            return new(release, path, hash);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    // Keep this handle open until the installer is started, preventing replacement after validation on Windows.
    public static async Task<FileStream> OpenVerifiedAsync(DownloadedUpdate update, CancellationToken token)
    {
        var file = new FileStream(update.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        try
        {
            if (file.Length != update.Release.Size || Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != update.Sha256) throw new InvalidDataException("安装包已变化，请重新下载。");
            return file;
        }
        catch { await file.DisposeAsync(); throw; }
    }
    public void Dispose() => client.Dispose();
}
