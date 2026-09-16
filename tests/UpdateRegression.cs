using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

internal static class UpdateRegression
{
    private static void Check(bool value, string message = "更新断言失败") { if (!value) throw new Exception(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("应拒绝：" + typeof(T).Name); }
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("MZ installer fixture (not executable)");
    private static string Hash => Convert.ToHexString(SHA256.HashData(Payload));
    private static string Metadata(string tag = "v2.2.1", bool prerelease = false, bool draft = false, string? url = null, string? digest = null, long? size = null)
    {
        string name = $"VoiceInput-Setup-{tag.TrimStart('v')}.exe";
        object Asset(string n, long length, string? hash) => new { name = n, state = "uploaded", size = length, digest = hash, browser_download_url = url ?? $"https://github.com/{GitHubUpdates.Repository}/releases/download/{tag}/{n}" };
        return JsonSerializer.Serialize(new { tag_name = tag, prerelease, draft, assets = new[] { Asset(name, size ?? Payload.Length, digest), Asset("SHA256SUMS.txt", 100, null) } });
    }
    private static GitHubRelease Release() => GitHubUpdates.ParseRelease(Metadata(digest: "sha256:" + Hash.ToLowerInvariant()), new(2, 2, 0))!;
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Headers.Authorization == null && request.Headers.UserAgent.Count > 0, "更新请求携带身份信息或缺少 UA");
            return response(request, token);
        }
    }
    private static GitHubUpdates Client(Func<HttpRequestMessage, HttpResponseMessage> response) => new(new Handler((r, _) => Task.FromResult(response(r))));
    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static string Sums => $"\uFEFF{Hash}  {Release().InstallerName}\r\n";
    private static async Task InDirectory(Func<string, Task> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "VoiceInputUpdateTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { await action(directory); } finally { Directory.Delete(directory, true); }
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("更新：数值版本比较、同版和降级不更新", () =>
        {
            foreach (string tag in new[] { "v2.2.0", "v2.1.99", "v1.99.99" }) Check(GitHubUpdates.ParseRelease(Metadata(tag), new(2, 2, 0, 0)) == null);
            Check(GitHubUpdates.ParseRelease(Metadata("v2.10.0"), new(2, 9, 9))?.Version == new Version(2, 10, 0));
            return Task.CompletedTask;
        });
        await test("更新：拒绝预览、草稿和不规范标签", () =>
        {
            Check(GitHubUpdates.ParseRelease(Metadata(prerelease: true), new(2, 2, 0)) == null);
            Check(GitHubUpdates.ParseRelease(Metadata(draft: true), new(2, 2, 0)) == null);
            foreach (string tag in new[] { "v2.2.1-beta", "2.2.1", "v02.2.1", "v2.2.1.0", "v2.2.1\n", "v999999999999.0.0", "../x" }) Check(GitHubUpdates.ParseTag(tag) == null, tag);
            return Task.CompletedTask;
        });
        await test("更新：缺失或重复资源、错误仓库及大小均拒绝", async () =>
        {
            foreach (string metadata in new[] { Metadata(url: "https://evil.example/setup.exe"), Metadata(size: 0), Metadata(size: GitHubUpdates.MaximumInstallerBytes + 1), Metadata(digest: "md5:bad"), Metadata().Replace("SHA256SUMS.txt", "other.txt"), Metadata().Replace("uploaded", "new") })
                await Reject<InvalidDataException>(() => Task.FromResult(GitHubUpdates.ParseRelease(metadata, new(2, 2, 0))));
            var root = System.Text.Json.Nodes.JsonNode.Parse(Metadata())!; var assets = root["assets"]!.AsArray(); assets.Add(assets[0]!.DeepClone());
            await Reject<InvalidDataException>(() => Task.FromResult(GitHubUpdates.ParseRelease(root.ToJsonString(), new(2, 2, 0))));
        });
        await test("更新：固定 GitHub 接口、无正式版与限流", async () =>
        {
            using var client = Client(r => { Check(r.RequestUri!.AbsoluteUri == GitHubUpdates.LatestUrl); return Ok(Metadata()); });
            Check((await client.CheckAsync(new(2, 2, 0), CancellationToken.None))?.Tag == "v2.2.1");
            using var missing = Client(_ => new(HttpStatusCode.NotFound)); Check(await missing.CheckAsync(new(2, 2, 0), CancellationToken.None) == null);
            using var limited = Client(_ => new(HttpStatusCode.Forbidden)); await Reject<HttpRequestException>(() => limited.CheckAsync(new(2, 2, 0), CancellationToken.None));
        });
        await test("更新：下载通过大小与双摘要校验后才能交给安装器", () => InDirectory(async directory =>
        {
            using var client = Client(r => r.RequestUri!.AbsolutePath.EndsWith(".txt") ? Ok(Sums) : Bytes(Payload));
            var update = await client.DownloadAsync(Release(), directory, null, CancellationToken.None);
            Check(File.ReadAllBytes(update.Path).SequenceEqual(Payload)); Check(!Directory.GetFiles(directory, "*.part").Any());
            await using (var verified = await GitHubUpdates.OpenVerifiedAsync(update, CancellationToken.None)) Check(verified.Length == Payload.Length);
            File.WriteAllBytes(update.Path, new byte[Payload.Length]);
            await Reject<InvalidDataException>(() => GitHubUpdates.OpenVerifiedAsync(update, CancellationToken.None));
        }));
        await test("更新：重启复用重新校验过的安装包，损坏缓存重新下载", () => InDirectory(async directory =>
        {
            int downloads = 0;
            using var client = Client(r => { if (r.RequestUri!.AbsolutePath.EndsWith(".txt")) return Ok(Sums); downloads++; return Bytes(Payload); });
            var update = await client.DownloadAsync(Release(), directory, null, CancellationToken.None);
            await client.DownloadAsync(Release(), directory, null, CancellationToken.None); Check(downloads == 1);
            File.WriteAllBytes(update.Path, new byte[Payload.Length]);
            await client.DownloadAsync(Release(), directory, null, CancellationToken.None); Check(downloads == 2);
        }));
        await test("更新：流传输中取消删除半包、不生成安装包", () => InDirectory(async directory =>
        {
            byte[] large = new byte[200000]; string hash = Convert.ToHexString(SHA256.HashData(large));
            var release = Release() with { Size = large.Length, Digest = hash };
            using var cancellation = new CancellationTokenSource();
            using var client = Client(r => r.RequestUri!.AbsolutePath.EndsWith(".txt") ? Ok($"{hash}  {release.InstallerName}\n") : Bytes(large));
            await Reject<OperationCanceledException>(() => client.DownloadAsync(release, directory, new ImmediateProgress<double>(_ => cancellation.Cancel()), cancellation.Token));
            Check(Directory.GetFiles(directory).Length == 0);
        }));
        await test("更新：损坏、截断与过大下载清理临时文件", () => InDirectory(async directory =>
        {
            foreach (var payload in new[] { new byte[Payload.Length], Payload[..^1], Payload.Concat(new byte[] { 0 }).ToArray() })
            {
                using var client = Client(r => r.RequestUri!.AbsolutePath.EndsWith(".txt") ? Ok(Sums) : Bytes(payload));
                await Reject<InvalidDataException>(() => client.DownloadAsync(Release(), directory, null, CancellationToken.None));
                Check(Directory.GetFiles(directory).Length == 0);
            }
        }));
        await test("更新：校验清单缺失、重复或摘要冲突时不下载安装包", () => InDirectory(async directory =>
        {
            foreach (string sums in new[] { "", Sums + Sums.TrimStart('\uFEFF'), Sums.Replace(Hash, new string('0', 64)) })
            {
                int installers = 0;
                using var client = Client(r => { if (!r.RequestUri!.AbsolutePath.EndsWith(".txt")) installers++; return Ok(sums); });
                await Reject<InvalidDataException>(() => client.DownloadAsync(Release(), directory, null, CancellationToken.None));
                Check(installers == 0 && Directory.GetFiles(directory).Length == 0);
            }
        }));
        await test("更新：仅接受 GitHub HTTPS 资源重定向", () => InDirectory(async directory =>
        {
            foreach (string target in new[] { "http://github.com/file", "https://evil.example/file", "https://github.com.evil.example/file", "https://user@github.com/file", "https://github.com:444/file" })
            {
                using var client = Client(_ => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new(target); return response; });
                await Reject<InvalidDataException>(() => client.DownloadAsync(Release(), directory, null, CancellationToken.None));
            }
            using var allowed = Client(r =>
            {
                if (r.RequestUri!.AbsolutePath.EndsWith(".txt")) return Ok(Sums);
                if (r.RequestUri.Host == "release-assets.githubusercontent.com") return Bytes(Payload);
                var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://release-assets.githubusercontent.com/fixture"); return response;
            });
            Check(File.Exists((await allowed.DownloadAsync(Release(), directory, null, CancellationToken.None)).Path));
        }));
        await test("更新：检查和下载可取消且不会留下可执行的半包", () => InDirectory(async directory =>
        {
            using var cancellation = new CancellationTokenSource();
            using var client = new GitHubUpdates(new Handler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(".txt")) return Ok(Sums);
                cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return Bytes(Payload);
            }));
            await Reject<OperationCanceledException>(() => client.DownloadAsync(Release(), directory, null, cancellation.Token));
            Check(Directory.GetFiles(directory).Length == 0);
            await Reject<OperationCanceledException>(() => client.CheckAsync(new(2, 2, 0), cancellation.Token));
        }));
        await test("更新：超大元信息与重定向循环有界", async () =>
        {
            using var huge = Client(_ => Ok(new string('x', 2 * 1024 * 1024 + 1)));
            await Reject<InvalidDataException>(() => huge.CheckAsync(new(2, 2, 0), CancellationToken.None));
            int calls = 0;
            using var loop = Client(_ => { calls++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://github.com/loop"); return response; });
            await Reject<InvalidDataException>(() => loop.CheckAsync(new(2, 2, 0), CancellationToken.None)); Check(calls == 6);
        });
        await test("更新：设置兼容旧配置、保存后重启保留", async () =>
        {
            var old = JsonSerializer.Deserialize<AppSettings>("{}", JsonCodec.Options)!;
            Check(old.AutoCheckUpdates && old.AutoDownloadUpdates && !old.AutoOpenUpdateInstaller);
            await using var fixture = await ControllerFixture.Create();
            await fixture.App.SaveSettingsAsync(fixture.App.Settings with { AutoCheckUpdates = false, AutoDownloadUpdates = false, AutoOpenUpdateInstaller = true }, fixture.App.Keys);
            await fixture.Reopen(); Check(!fixture.App.Settings.AutoCheckUpdates && !fixture.App.Settings.AutoDownloadUpdates && fixture.App.Settings.AutoOpenUpdateInstaller);
        });
        await test("更新：保存失败禁止退出安装、恢复后持久化再放行", async () =>
        {
            await using var fixture = await ControllerFixture.Create(); var source = await fixture.Seed("更新前内容。"); await fixture.App.LoadSessionAsync(source.Session);
            fixture.Protector.Fail = j => j.TryGetProperty("finalText", out var text) && text.GetString() == "更新前修订。";
            await fixture.App.EditAsync(source.Segment.Id, "更新前修订。"); await fixture.App.Repository.BarrierAsync(); await fixture.App.SnapshotAsync();
            await Reject<InvalidOperationException>(() => fixture.App.PrepareUpdateAsync());
            fixture.Protector.Fail = null; await fixture.App.PrepareUpdateAsync();
            Check((await fixture.App.Repository.LoadSessionAsync(source.Session.Id))!.Segments.Single().FinalText == "更新前修订。");
        });
    }
}
