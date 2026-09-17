using System.Text.Json;
using RealtimeTranscription.Core;

static class DomainLexiconRegression
{
    static void Check(bool ok, string message = "领域词库断言失败") { if (!ok) throw new Exception(message); }
    const string Text = "本次地下实验讨论束流强度和磁刚度测量，准备检查离子源与束流传输系统，并继续研究相关实验装置的稳定运行。";
    static string Result(string id, string quote = Text, params string[] words)
    {
        var names = words.Length == 0 ? new[] { "束流强度", "磁刚度", "发射度" } : words;
        var evidence = new[] { new { source_segment_id = id, evidence_text = quote } };
        return JsonSerializer.Serialize(new { topics = new[] { new { name = "离子束输运", relevance = 5, evidence } },
            terms = names.Select(text => new { text, extended = !quote.Contains(text), topic = "离子束输运", relevance = 5, difficulty = 4,
                risk_type = "homophone", risk_reason = "近音常用词竞争，具有识别风险", relation = "与当前束流传输话题紧密相关", confusion = "", evidence }) });
    }
    static async Task<string> Respond(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        Check(body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() == DomainLexicon.Prompt);
        using var data = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        var first = data.RootElement.GetProperty("segments")[0]; return Result(first.GetProperty("source_segment_id").GetString()!, first.GetProperty("text").GetString()!);
    }
    static Task Enable(ControllerFixture f) => f.App.SaveSettingsAsync(f.App.Settings with { DomainLexiconEnabled = true }, f.App.Keys);

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("领域预测保留原文词与有限扩展，全部低权重且不自动替换", async () =>
        {
            await using var f = await ControllerFixture.Create(async (r,t) => ControllerFixture.Reply(await Respond(r,t)));
            await f.Seed(Text); await Enable(f); await f.App.RefreshDomainLexiconAsync();
            var terms = f.App.Terms; Check(terms.Count == 3 && terms.All(t => t.Origin == "Predicted" && t.Weight == 1 && !t.Protect && t.Alias == ""));
            Check(terms.Single(t => t.Text == "发射度").Evidence.All(e => !e.Quote.Contains("发射度")));
            Check(f.App.NextHotwords().Count == 3 && f.App.DomainReport().Contains("离子束输运"));
            await f.Reopen(); Check(f.App.NextHotwords().Count == 3 && f.Calls == 1);
        });
        await test("预测开关默认关闭，关闭后个人词保留且预测词停止注入", async () =>
        {
            await using var f = await ControllerFixture.Create(async (r,t) => ControllerFixture.Reply(await Respond(r,t)));
            Check(!f.App.Settings.DomainLexiconEnabled); await f.Seed(Text); await Enable(f); await f.App.RefreshDomainLexiconAsync();
            await f.App.SaveTermAsync(new() { Text = "AZURE2" });
            await f.App.SaveSettingsAsync(f.App.Settings with { DomainLexiconEnabled = false }, f.App.Keys);
            await f.App.RefreshDomainLexiconAsync(false); Check(f.App.NextHotwords().Single().Text == "AZURE2" && f.Calls == 1);
        });
        await test("禁用和删除的预测词不会被下一次分析复活，人工确认后保留", async () =>
        {
            await using var f = await ControllerFixture.Create(async (r,t) => ControllerFixture.Reply(await Respond(r,t)));
            await f.Seed(Text); await Enable(f); await f.App.RefreshDomainLexiconAsync();
            await f.App.SaveTermAsync(f.App.Terms.Single(t => t.Text == "束流强度") with { State = TermState.Disabled });
            await f.App.DeleteTermAsync(f.App.Terms.Single(t => t.Text == "磁刚度"));
            await f.App.SaveTermAsync(f.App.Terms.Single(t => t.Text == "发射度") with { Weight = 4 });
            await f.App.RefreshDomainLexiconAsync();
            Check(f.App.NextHotwords().Single().Text == "发射度" && f.App.NextHotwords().Single().Weight == 4);
            Check(f.App.Terms.Single(t => t.Text == "发射度").Origin == "Manual");
        });
        await test("话题变化替换活跃词，空话题结果清空预测注入", async () =>
        {
            int count = 0;
            await using var f = await ControllerFixture.Create(async (r,t) => ControllerFixture.Reply(++count == 1 ? await Respond(r,t) : "{\"topics\":[],\"terms\":[]}"));
            await f.Seed(Text); await Enable(f); await f.App.RefreshDomainLexiconAsync(); Check(f.App.NextHotwords().Count == 3);
            await f.Seed("今天讨论培训安排以及人员分工，先确定时间地点，再商量本周的工作计划和下周的具体安排。");
            await f.App.RefreshDomainLexiconAsync(); Check(f.App.NextHotwords().Count == 0);
        });
        await test("撤回学习许可或删除来源使领域分析和预测词失效", async () =>
        {
            foreach (bool delete in new[] { false, true })
            {
                await using var f = await ControllerFixture.Create(async (r,t) => ControllerFixture.Reply(await Respond(r,t)));
                var source = await f.Seed(Text); await f.App.LoadSessionAsync(source.Session); await Enable(f); await f.App.RefreshDomainLexiconAsync();
                if (delete) await f.App.DeleteSessionAsync(source.Session); else await f.App.SetSessionLearningAsync(false);
                Check(f.App.NextHotwords().Count == 0 && await f.App.Repository.DomainProfileAsync("default") == null);
            }
        });
        await test("历史采样隔离项目和学习许可，限制字符且不用润色正文", async () =>
        {
            await using var f = await ControllerFixture.Create();
            var raw = await f.Seed(Text); await f.App.Repository.SaveSegmentAsync(raw.Session, raw.Segment with { FinalText = "禁止作为原始历史", Revision = 2 });
            var hidden = await f.Seed("禁止学习的文本"); await f.App.Repository.SaveSessionAsync(hidden.Session with { AllowLearning = false, LearningRevision = 1, Revision = 2 });
            var samples = await f.App.Repository.DomainSamplesAsync("default", CancellationToken.None);
            Check(samples.Count == 1 && samples[0].Text == Text && (await f.App.Repository.DomainSamplesAsync("other", CancellationToken.None)).Count == 0);
            for (int i = 0; i < 30; i++) await f.Seed(new string('字', 1000));
            samples = await f.App.Repository.DomainSamplesAsync("default", CancellationToken.None);
            Check(samples.Where(s => s.Recent).Sum(s => JsonCodec.Count(s.Text)) <= 3000 && samples.Where(s => !s.Recent).Sum(s => JsonCodec.Count(s.Text)) <= 1000);
        });
        await test("伪造引用和泛用词不得入库，扩展词比例受限", async () =>
        {
            await using var f = await ControllerFixture.Create(); await f.Seed(Text);
            var samples = await f.App.Repository.DomainSamplesAsync("default", CancellationToken.None); string id = samples[0].Segment.Id;
            var fake = DomainLexicon.Parse(Result("fake"), samples, "default", DateTimeOffset.UtcNow); Check(fake.Terms.Count == 0);
            var ordinary = DomainLexicon.Parse(Result(id, Text, "实验", "研究", "数据"), samples, "default", DateTimeOffset.UtcNow); Check(ordinary.Terms.Count == 0);
            var expansion = DomainLexicon.Parse(Result(id, Text, "束流强度", "磁刚度", "发射度", "色散函数", "束流包络"), samples, "default", DateTimeOffset.UtcNow);
            Check(expansion.Terms.Count == 3 && expansion.Terms.Count(t => t.PredictionExtended) == 1);
        });
        await test("无新增历史不重复自动请求，预测使用频率不会自行提高权重", async () =>
        {
            await using var f = await ControllerFixture.Create(async (r,t) => ControllerFixture.Reply(await Respond(r,t)));
            await f.Seed(Text); await Enable(f); await f.App.RefreshDomainLexiconAsync(); await f.App.RefreshDomainLexiconAsync(false);
            var samples = await f.App.Repository.DomainSamplesAsync("default", CancellationToken.None); var profile = await f.App.Repository.DomainProfileAsync("default");
            Check(f.Calls == 1 && !DomainLexicon.ShouldRefresh(profile, samples, DateTimeOffset.UtcNow.AddHours(1), false));
            Check(Lexicon.EffectiveWeight(f.App.Terms[0] with { UsageCount = 1000, LastUsedAt = DateTimeOffset.UtcNow }) == 1);
            Check(!DomainLexicon.Active(f.App.Terms[0], profile, DateTimeOffset.UtcNow.AddDays(15)));
        });
        await test("分析期间修订历史，过时结果无法提交", async () =>
        {
            await using var f = await ControllerFixture.Create(); var source = await f.Seed(Text);
            var samples = await f.App.Repository.DomainSamplesAsync("default", CancellationToken.None);
            var prediction = DomainLexicon.Parse(Result(source.Segment.Id), samples, "default", DateTimeOffset.UtcNow);
            await f.App.Repository.SaveSegmentAsync(source.Session, source.Segment with { FinalText = "已经修改的内容", EditRevision = 1, Revision = 2 });
            bool failed = false; try { await f.App.Repository.CommitDomainPredictionAsync(samples, prediction, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
            Check(failed && (await f.App.Repository.TermsAsync("default")).Count == 0);
        });
        await test("分析请求取消不写入结果，关闭开关取消在途请求", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var f = await ControllerFixture.Create(async (r,t) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, t); return ControllerFixture.Reply(""); });
            await f.Seed(Text); await Enable(f); var task = f.App.RefreshDomainLexiconAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.App.SaveSettingsAsync(f.App.Settings with { DomainLexiconEnabled = false }, f.App.Keys); await task;
            Check(f.App.Terms.Count == 0 && await f.App.Repository.DomainProfileAsync("default") == null);
        });
        await test("预算不足不调用云端，失败保留原词库", async () =>
        {
            await using var f = await ControllerFixture.Create(); await f.Seed(Text);
            await f.App.SaveTermAsync(new() { Text = "JUNA" });
            await f.App.SaveSettingsAsync(f.App.Settings with { DomainLexiconEnabled = true, DailyExtractionTokens = 1000 }, f.App.Keys);
            bool failed = false; try { await f.App.RefreshDomainLexiconAsync(); } catch (InvalidOperationException) { failed = true; }
            Check(failed && f.Calls == 0 && f.App.NextHotwords().Single().Text == "JUNA");
        });
        await test("预测来源修订后立即停止注入，数据库事务失败不留下半批结果", async () =>
        {
            await using var f = await ControllerFixture.Create(async (r,t) => ControllerFixture.Reply(await Respond(r,t)));
            var source = await f.Seed(Text); await Enable(f); await f.App.RefreshDomainLexiconAsync();
            await f.App.LoadSessionAsync(source.Session); await f.App.EditAsync(source.Segment.Id, Text + "修改。"); await f.Settle();
            Check(f.App.NextHotwords().Count == 0);
            f.Protector.Fail = e => e.TryGetProperty("fingerprint", out _);
            bool failed = false; try { await f.App.RefreshDomainLexiconAsync(); } catch (System.IO.IOException) { failed = true; }
            Check(failed && await f.App.Repository.DomainProfileAsync("default") == null);
        });
    }
}
