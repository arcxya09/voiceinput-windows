using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

internal static class Review212LexiconRegression
{
    private static void Check(bool value, string why = "纠错位置回归失败") { if (!value) throw new Exception(why); }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("212 手动改回原文清除已撤回纠错，不向另一个同名词转移来源", async () =>
        {
            await using var f = await Fixture.Create("DeepSeak 与 DeepSeek");
            await f.Edit("DeepSeek 与 DeepSeek");
            var candidate = (await f.Repository.CorrectionsAsync("default")).Single();
            Check((await f.Word()).CorrectionCount == 1);
            await f.Edit("DeepSeak 与 DeepSeek");
            Check(CorrectionRules.Active(f.Current).Count == 0);
            Check((await f.Repository.CorrectionsAsync("default")).Count == 0);
            Check((await f.Word()).CorrectionCount == 0 && (await f.Word()).UsageCount == 1);
            bool rejected = false;
            try { await f.Repository.ConfirmCorrectionAsync(candidate.Id, "default", new("DeepSeek", "DeepSeak", "专业术语", "default", 4, true)); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "已撤回的候选不得再授权自动纠正");
        });
        await test("212 同一词多个纠错位置分别保留，重启后改回一处不撤销另一处", async () =>
        {
            await using var f = await Fixture.Create("DeepSeak 与 DeepSeak");
            await f.Edit("DeepSeek 与 DeepSeek");
            Check(CorrectionRules.Active(f.Current).Count == 2);
            Check((await f.Repository.CorrectionsAsync("default")).Single().Count == 1);
            await f.Reopen();
            await f.Edit("DeepSeak 与 DeepSeek");
            var remaining = CorrectionRules.Active(f.Current).Single();
            Check(remaining.Original == "DeepSeak" && remaining.Corrected == "DeepSeek" && remaining.CorrectedStart == f.Current.FinalText.LastIndexOf("DeepSeek", StringComparison.Ordinal));
            Check((await f.Repository.CorrectionsAsync("default")).Single().Original == "DeepSeak" && (await f.Word()).CorrectionCount == 1);
            await f.Edit("DeepSeak 与 DeepSeak");
            Check(CorrectionRules.Active(f.Current).Count == 0 && (await f.Repository.CorrectionsAsync("default")).Count == 0 && (await f.Word()).CorrectionCount == 0);
        });
        await test("212 纠错位置跟随 Unicode 前缀移动，其他词的独立修正仍可学习", async () =>
        {
            await using var f = await Fixture.Create("🔬 cafe\u0301，DeepSeak。使用 Qwin。");
            await f.Edit("🔬 cafe\u0301，DeepSeek。使用 Qwin。");
            Check(CorrectionRules.Active(f.Current).Single().CorrectedStart == f.Current.FinalText.IndexOf("DeepSeek", StringComparison.Ordinal));
            await f.Edit("📌 🔬 cafe\u0301，DeepSeek。使用 Qwen。");
            Check(CorrectionRules.Active(f.Current).Count == 2);
            await f.Edit("🔬 cafe\u0301，DeepSeek。使用 Qwen。");
            Check(CorrectionRules.Active(f.Current).Count == 2 && CorrectionRules.Active(f.Current).All(c => c.CorrectedStart == f.Current.FinalText.IndexOf(c.Corrected, StringComparison.Ordinal)));
            await f.Edit("🔬 cafe\u0301，DeepSeak。使用 Qwen。");
            var remaining = CorrectionRules.Active(f.Current).Single();
            Check(remaining.Corrected == "Qwen" && remaining.CorrectedStart == f.Current.FinalText.IndexOf("Qwen", StringComparison.Ordinal));
            Check((await f.Repository.CorrectionsAsync("default")).Single().Corrected == "Qwen");
        });
        await test("212 同一位置连续改字保留原始错误写法，改回原始写法不学习反向链", async () =>
        {
            await using var f = await Fixture.Create("使用 DeepSeak。");
            await f.Edit("使用 DeepSeek。");
            await f.Edit("使用 DeepSee。");
            var revised = CorrectionRules.Active(f.Current).Single();
            Check(revised.Original == "DeepSeak" && revised.Corrected == "DeepSee");
            await f.Edit("使用 DeepSeak。");
            Check(CorrectionRules.Active(f.Current).Count == 0 && (await f.Repository.CorrectionsAsync("default")).Count == 0);
        });
        await test("212 旧历史没有位置字段时按前后证据恢复实际位置", async () =>
        {
            await using var f = await Fixture.Create("DeepSeak 与 DeepSeek");
            await f.Edit("DeepSeek 与 DeepSeek");
            var json = JsonNode.Parse(JsonSerializer.Serialize(f.Current, JsonCodec.Options))!;
            foreach (var state in json["undoHistory"]!.AsArray())
                if (state?["corrections"] is JsonArray changes)
                    foreach (var change in changes) change?.AsObject().Remove("correctedStart");
            var legacy = JsonSerializer.Deserialize<SegmentData>(json.ToJsonString(), JsonCodec.Options)!;
            Check(legacy.UndoHistory![legacy.UndoPosition].Corrections!.Single().CorrectedStart == -1);
            var restored = new TranscriptEngine(f.Engine.Session); restored.Restore([legacy]);
            Check(CorrectionRules.Active(restored.Segments.Single()).Single().CorrectedStart == 0);
            var reverted = restored.Edit(legacy.Id, "DeepSeak 与 DeepSeek", "编辑", true);
            Check(CorrectionRules.Active(reverted).Count == 0);
        });
        await test("212 撤销和关闭发现开关继续维护已知纠错位置，暂停期间不产生新候选", async () =>
        {
            await using var f = await Fixture.Create("DeepSeak 与 DeepSeak");
            await f.Edit("DeepSeek 与 DeepSeek");
            await f.Edit("DeepSeak 与 DeepSeek");
            var undone = f.Engine.Edit(f.Current.Id, "", "撤销", true);
            await f.Save(undone);
            Check(CorrectionRules.Active(undone).Count == 2);
            await f.Edit("DeepSeak 与 DeepSeek", false);
            Check(CorrectionRules.Active(f.Current).Count == 1);
            await f.Edit("DeepSeak 与 DeepSeak", false);
            Check(CorrectionRules.Active(f.Current).Count == 0);
        });
        await test("212 升级后拒绝旧数据库中已手动撤回但残留的待确认来源", async () =>
        {
            await using var f = await Fixture.Create("DeepSeak 与 DeepSeek");
            await f.Edit("DeepSeek 与 DeepSeek");
            var candidate = (await f.Repository.CorrectionsAsync("default")).Single();
            var old = f.Current;
            var history = old.UndoHistory!.ToList();
            history[old.UndoPosition] = history[old.UndoPosition] with
            {
                Text = old.RawText,
                Corrections = history[old.UndoPosition].Corrections!.Select(c => c with { CorrectedStart = -1 }).ToList()
            };
            // Reproduce an old persisted state without running the new write-time repair.
            await f.ReplaceLegacy(old with { FinalText = old.RawText, UndoHistory = history });
            bool rejected = false;
            try { await f.Repository.ConfirmCorrectionAsync(candidate.Id, "default", new("DeepSeek", "DeepSeak", "专业术语", "default", 4, true)); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && (await f.Repository.CorrectionsAsync("default")).Single().State == CorrectionState.Pending);
        });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "Review212Lexicon-" + JsonCodec.Id());
        private string Database => Path.Combine(directory, "test.db");
        public MemoryRepository Repository { get; private set; } = null!;
        public TranscriptEngine Engine { get; private set; } = null!;
        public SegmentData Current => Engine.Segments.Single();
        public static async Task<Fixture> Create(string raw)
        {
            var f = new Fixture(); f.Repository = new(f.Database, new FixtureProtector());
            await f.Repository.InitializeAsync(); await f.Repository.SaveProjectAsync(new("default", "默认项目"));
            await f.Repository.SaveUserTermAsync(new() { Text = "DeepSeek" });
            var session = new SessionData();
            var source = new SegmentData { SessionId = session.Id, TaskId = "source", SentenceId = 1, TaskOrder = 1, RawText = raw, FinalText = raw, AsrState = AsrState.Confirmed, OutputState = OutputState.Published, SourceRevision = 1 };
            f.Engine = new(session); f.Engine.Restore([source]); await f.Save(source); return f;
        }
        public Task Save(SegmentData value) => Repository.SaveSegmentAsync(Engine.Session, value, true, true);
        public async Task Edit(string text, bool learn = true) => await Save(Engine.Edit(Current.Id, text, "编辑", learn));
        public async Task<TermData> Word() => (await Repository.TermsAsync("default")).Single(t => t.Text == "DeepSeek");
        public async Task Reopen()
        {
            string session = Engine.Session.Id; await Repository.DisposeAsync(); Repository = new(Database, new FixtureProtector());
            await Repository.InitializeAsync(); var stored = (await Repository.LoadSessionAsync(session))!;
            Engine = new(stored.Session); Engine.Restore(stored.Segments);
        }
        public async Task ReplaceLegacy(SegmentData segment)
        {
            await Repository.BarrierAsync();
            using var connection = new SqliteConnection("Data Source=" + Database); connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE segments SET payload=$payload WHERE id=$id";
            command.Parameters.AddWithValue("$payload", JsonSerializer.SerializeToUtf8Bytes(segment, JsonCodec.Options));
            command.Parameters.AddWithValue("$id", segment.Id); command.ExecuteNonQuery();
        }
        public async ValueTask DisposeAsync() { await Repository.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
    private sealed class FixtureProtector : IProtector
    {
        public byte[] Protect(byte[] plain) => plain;
        public byte[] Unprotect(byte[] cipher) => cipher;
    }
}
