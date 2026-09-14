using System.Security.Cryptography;
using System.Text;
using RealtimeTranscription.Core;
using RealtimeTranscription.Desktop;
using RealtimeTranscription.Infrastructure;

static class Review212StartupRegression
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("2.1.2 损坏凭据独立恢复，重填 Key 保留隐私设置、期限、快捷键和历史词库", async () =>
        {
            await using var f = await Fixture.Create();
            var original = new AppSettings
            {
                LegacyEndpoint = true, SaveMemory = false, AllowLearning = false,
                RetentionDays = 7, DictationOnly = true, Hotkey = "F9"
            };
            f.Store.Save(original, f.Credentials);
            byte[] settingsBytes = await File.ReadAllBytesAsync(f.SettingsPath);
            byte[] damaged = await File.ReadAllBytesAsync(f.CredentialsPath);
            damaged[^1] ^= 0x80;
            await File.WriteAllBytesAsync(f.CredentialsPath, damaged);

            await using var app = await f.Open();
            Check(app.Settings == original, "凭据解密失败丢弃了有效设置。");
            Check(app.Keys == new Credentials(), "无法读取的 API Key 未安全置空。");
            Check(app.StartupWarning?.Contains("重新填写并保存") == true, "缺少可操作的凭据恢复提示。");
            await f.CheckHistoryAndTerms(app);
            Check((await File.ReadAllBytesAsync(f.SettingsPath)).SequenceEqual(settingsBytes), "启动恢复修改了原设置文件。");
            Check((await File.ReadAllBytesAsync(f.CredentialsPath)).SequenceEqual(damaged), "启动恢复覆盖了原凭据文件。");

            var replacement = new Credentials("REPLACEMENT_ASR", "REPLACEMENT_AI");
            await app.SaveSettingsAsync(app.Settings, replacement);
            Check(f.Store.Load() == original, "重新保存 Key 重置了原有偏好设置。");
            Check(f.Store.LoadCredentials() == replacement, "修复后的 Key 未保存。");
            Check(app.StartupWarning == null, "修复并保存后仍保留过期启动告警。");
            await f.CheckHistoryAndTerms(app);
        });

        await test("2.1.2 损坏设置保留有效 Key 和历史，未知隐私偏好采用关闭状态且不自动写盘", async () =>
        {
            await using var f = await Fixture.Create();
            f.Store.Save(new() { RetentionDays = 1 }, f.Credentials);
            byte[] credentialsBytes = await File.ReadAllBytesAsync(f.CredentialsPath);
            const string damaged = "{broken settings";
            await File.WriteAllTextAsync(f.SettingsPath, damaged);
            var old = new SessionData { Title = "损坏配置时保留旧历史", CreatedAt = DateTimeOffset.UtcNow.AddDays(-30) };
            await using (var repository = new MemoryRepository(f.DatabasePath, f.Protector))
            {
                await repository.InitializeAsync();
                await repository.SaveSessionAsync(old);
            }

            await using var app = await f.Open();
            Check(app.Keys == f.Credentials, "设置错误丢弃了有效 Key。");
            CheckConservativeDefaults(app.Settings);
            Check(app.StartupWarning?.Contains("请检查设置后保存") == true, "缺少配置恢复提示。");
            Check(app.MemoryAvailable && await app.Repository.LoadSessionAsync(old.Id) != null, "未知保留期限触发了历史清理。");
            Check((await File.ReadAllBytesAsync(f.CredentialsPath)).SequenceEqual(credentialsBytes), "启动恢复改写了有效凭据。");
            Check(await File.ReadAllTextAsync(f.SettingsPath) == damaged, "启动恢复覆盖了原配置。");
            Check((await app.Repository.SearchAsync(null, "", null, CancellationToken.None)).Count == 2, "历史未正常初始化。");
        });

        await test("2.1.2 设置和凭据同时损坏仍初始化正常数据库并分别提示", async () =>
        {
            await using var f = await Fixture.Create();
            await File.WriteAllTextAsync(f.SettingsPath, "null");
            await File.WriteAllBytesAsync(f.CredentialsPath, [1, 2, 3]);
            await using var app = await f.Open();
            CheckConservativeDefaults(app.Settings);
            Check(app.Keys == new Credentials(), "双重加载失败未清空 Key。");
            Check(app.StartupWarning?.Contains("设置文件无法读取") == true && app.StartupWarning.Contains("API Key 无法读取"), "恢复提示未覆盖两种失败。");
            await f.CheckHistoryAndTerms(app);
        });

        await test("2.1.2 无效配置字段和空凭据字段可恢复，不留半初始化状态", async () =>
        {
            foreach (string invalid in new[] { "{\"holdMs\":1}", "{\"workspaceId\":null}" })
            {
                await using var f = await Fixture.Create();
                f.Store.Save(new(), f.Credentials);
                await File.WriteAllTextAsync(f.SettingsPath, invalid);
                await using var app = await f.Open();
                CheckConservativeDefaults(app.Settings);
                Check(app.Keys == f.Credentials && app.MemoryAvailable, "无效设置导致凭据或数据库未初始化。");
            }
            await using var nullKeys = await Fixture.Create();
            nullKeys.Store.Save(new() { Hotkey = "F8" }, nullKeys.Credentials);
            await File.WriteAllBytesAsync(nullKeys.CredentialsPath,
                nullKeys.Protector.Protect(Encoding.UTF8.GetBytes("{\"bailianKey\":null,\"deepSeekKey\":\"TEST\"}")));
            await using var restored = await nullKeys.Open();
            Check(restored.Keys == new Credentials() && restored.Settings.Hotkey == "F8" && restored.MemoryAvailable,
                "JSON 中的空 Key 未独立恢复。");
        });

        await test("2.1.2 首次启动仍使用产品默认设置且没有虚假恢复提示", async () =>
        {
            await using var f = await Fixture.Create(seed: false);
            await using var app = await f.Open();
            Check(app.Settings == new AppSettings() && app.Keys == new Credentials(), "首次启动默认设置发生变化。");
            Check(app.StartupWarning == null && app.MemoryAvailable, "首次启动被误报为配置故障。");
        });
    }

    private static void CheckConservativeDefaults(AppSettings settings)
    {
        Check(!settings.SaveMemory && !settings.AllowLearning && !settings.LearnCorrections && !settings.UseLexicon && !settings.DynamicLexicon &&
            !settings.AutoExtract && !settings.AsrContext && !settings.PreviousContext && !settings.PolishEnabled &&
            settings.DictationOnly && settings.RetentionDays == null, "无法读取隐私偏好时错误地启用保存、学习或自动输入。");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "VoiceInput212Startup-" + Guid.NewGuid().ToString("N"));
        internal readonly IntegrityProtector Protector = new();
        internal readonly Credentials Credentials = new("TEST_ASR", "TEST_AI");
        internal SettingsStore Store => new(folder, Protector);
        internal string SettingsPath => Path.Combine(folder, "settings.json");
        internal string CredentialsPath => Path.Combine(folder, "credentials.dat");
        internal string DatabasePath => Path.Combine(folder, "sessions.db");
        internal static async Task<Fixture> Create(bool seed = true)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.folder);
            if (seed)
            {
                await using var repository = new MemoryRepository(fixture.DatabasePath, fixture.Protector);
                await repository.InitializeAsync();
                await repository.SaveProjectAsync(new("default", "正常项目"));
                await repository.SaveSessionAsync(new() { Title = "正常历史" });
                await repository.SaveTermAsync(new() { Text = "核天体物理" });
            }
            return fixture;
        }
        internal async Task<AppController> Open()
        {
            var app = new AppController(folder, Protector);
            try { await app.InitializeAsync(); return app; }
            catch { await app.DisposeAsync(); throw; }
        }
        internal async Task CheckHistoryAndTerms(AppController app)
        {
            Check(app.MemoryAvailable, "正常数据库未独立完成初始化。");
            Check((await app.Repository.SearchAsync(null, "", null, CancellationToken.None)).Single().Session.Title == "正常历史", "正常历史不可查询。");
            Check(app.Terms.Single().Text == "核天体物理", "正常词库未载入。");
        }
        public ValueTask DisposeAsync() { Directory.Delete(folder, true); return ValueTask.CompletedTask; }
    }

    private sealed class IntegrityProtector : IProtector
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(byte[] plain)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12), cipher = new byte[plain.Length], tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, cipher, tag);
            return [.. nonce, .. tag, .. cipher];
        }
        public byte[] Unprotect(byte[] bytes)
        {
            if (bytes.Length < 28) throw new CryptographicException("Invalid encrypted fixture.");
            byte[] plain = new byte[bytes.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
            return plain;
        }
    }
}
