using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

static partial class StartupCaptureRegression
{
    private static HttpResponseMessage ReviewReply(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { output = new { text, sentence = new { text = "只有最后一句" } }, usage = new { duration = 1.2 } })) };
    private static async Task<string> PrepareReview(Fixture f, Turn turn)
    {
        await f.App.SaveSettingsAsync(f.App.Settings with { HighAccuracyEnabled = true }, f.App.Keys);
        Check(await f.Start(), "启动失败");
        var capture = await turn.StartedCapture();
        await capture.EmitPcm(Enumerable.Range(0, 3200).Select(i => (byte)(i % 251)).ToArray());
        await f.App.StopAsync(false);
        return (await f.App.SnapshotAsync()).Session!.Id;
    }
    public static async Task RunAsrReview(Func<string, Func<Task>, Task> test)
    {
        await test("高精度：新配置和旧配置缺省开启，显式关闭保持", () =>
        {
            Check(new AppSettings().HighAccuracyEnabled && JsonSerializer.Deserialize<AppSettings>("{}", JsonCodec.Options)!.HighAccuracyEnabled, "缺省未开启");
            Check(!JsonSerializer.Deserialize<AppSettings>("{\"highAccuracyEnabled\":false}", JsonCodec.Options)!.HighAccuracyEnabled, "关闭设置丢失");
            return Task.CompletedTask;
        });
        await test("高精度：WAV 保留所有首尾PCM，超限整段回退且缓存只消费一次", () =>
        {
            using var buffer = new AsrReviewBuffer();
            byte[] first = [1, 2, 3, 4], tail = [5, 6]; buffer.Add(first); buffer.Add(tail); first[0] = 9;
            byte[] wav = buffer.TakeWav()!;
            Check(Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)) == 16000
                && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)) == 6 && wav.AsSpan(44).SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 }), "WAV 或尾包被改写");
            Check(buffer.TakeWav() == null, "音频重复消费");
            using var longBuffer = new AsrReviewBuffer(); longBuffer.Add(new byte[AsrReviewBuffer.MaxBytes]); longBuffer.Add([1, 2]);
            Check(longBuffer.Overflowed && longBuffer.TakeWav() == null, "超限后提交了截断录音");
            return Task.CompletedTask;
        });
        await test("高精度：官方 HTTP 协议、地域、热词及400字符上下文", async () =>
        {
            foreach (bool legacy in new[] { true, false }) foreach (string region in new[] { "cn-beijing", "ap-southeast-1" })
            {
                var settings = new AppSettings { LegacyEndpoint = legacy, WorkspaceId = "workspace", Region = region, AsrContext = true };
                Check(AsrReviewClient.Endpoint(settings).Scheme == "https" && AsrReviewClient.Endpoint(settings).Host == settings.AsrUri().Host, "地域或空间丢失");
            }
            using var buffer = new AsrReviewBuffer(); buffer.Add([1, 2]); var wav = buffer.TakeWav()!;
            using var client = new AsrReviewClient(new RegressionHandler(async (request, token) =>
            {
                Check(request.Headers.Authorization?.Parameter == "TEST" && request.Headers.GetValues("X-DashScope-SSE").Single() == "disable", "请求头错误");
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = doc.RootElement;
                Check(root.GetProperty("model").GetString() == AsrReviewClient.Model, "模型错误");
                var p = root.GetProperty("parameters"); Check(p.GetProperty("sample_rate").GetString() == "16000" && p.GetProperty("vocabulary").GetProperty("发射度").GetInt32() == 3, "参数错误");
                var messages = root.GetProperty("input").GetProperty("messages");
                Check(messages.GetArrayLength() == 2 && JsonCodec.Count(messages[0].GetProperty("content")[0].GetProperty("text").GetString()!) <= 400, "上下文过长");
                string uri = messages[1].GetProperty("content")[0].GetProperty("input_audio").GetProperty("data").GetString()!;
                Check(Convert.FromBase64String(uri.Split(',')[1]).SequenceEqual(wav), "音频不匹配");
                return ReviewReply("第一句。第二句。");
            }));
            var result = await client.RecognizeAsync(new() { LegacyEndpoint = true, AsrContext = true }, "TEST", wav,
                [new("发射度", 3), new(new string('词', 500), 1)], CancellationToken.None);
            Check(result.Text == "第一句。第二句。" && result.Duration == 1.2, "误用了最后一句或用量丢失");
        });
        await test("高精度：复核结果只发布一次，原始流式结果不重复学习，保存重载一致", async () =>
        {
            int calls = 0; var turn = new Turn();
            await using var f = await Fixture.Create([turn], (_, _) => { calls++; return Task.FromResult(ReviewReply("核天体物理实验。")); });
            await f.App.SaveSettingsAsync(f.App.Settings with { SaveMemory = true }, f.App.Keys);
            string id = await PrepareReview(f, turn);
            await f.App.Repository.BarrierAsync();
            var streaming = (await f.App.SnapshotAsync()).Segments.Single();
            await f.App.Repository.SaveTermAsync(new() { Text = "完整结果", Origin = "Extracted",
                Evidence = [new(id, streaming.Id, streaming.SourceRevision, streaming.EditRevision, streaming.RawText, false)] });
            await f.App.FinishCurrentAsync(id, forDelivery: true);
            var snapshot = await f.App.SnapshotAsync();
            Check(snapshot.Session!.AsrReviewState == "Completed" && TranscriptText.Render(snapshot) == "核天体物理实验", "复核未应用或标点路径遗漏");
            Check(snapshot.Segments.Count(s => s.OutputState == OutputState.Published) == 1 && snapshot.Segments.Count(s => s.SupersededByAsrReview) == 1, "重复正文");
            Check(TranscriptComparison.Original(snapshot.Segments) == "核天体物理实验。", "对照正文重复");
            await f.App.Repository.BarrierAsync();
            var saved = (await f.App.Repository.LoadSessionAsync(id))!;
            Check(!(await f.App.Repository.TermsAsync("default")).Any(t => t.Text == "完整结果"), "被替代原文的旧证据未清理");
            Check(ExtractionPlanner.Pending(saved.Session, saved.Segments).All(s => !s.Segment.SupersededByAsrReview), "学习了已替代的流式原文");
            await f.App.FinishCurrentAsync(id, forDelivery: true); Check(calls == 1, "重复扣费");
            await f.App.LoadSessionAsync(saved.Session); Check(TranscriptText.Render(await f.App.SnapshotAsync()) == "核天体物理实验", "重载正文改变");
        });
        foreach (string scenario in new[] { "http", "empty", "malformed", "sentence-only", "truncated" })
        await test("高精度：异常回退 " + scenario, async () =>
        {
            var turn = new Turn(); if (scenario == "truncated") turn.Socket.FinalText = new string('文', 80) + "。";
            await using var f = await Fixture.Create([turn], (_, _) => Task.FromResult(scenario switch
            {
                "http" => new HttpResponseMessage(HttpStatusCode.Forbidden),
                "malformed" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") },
                "sentence-only" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"output\":{\"sentence\":{\"text\":\"尾句\"}}}") },
                "truncated" => ReviewReply("缺失"), _ => ReviewReply("")
            }));
            string id = await PrepareReview(f, turn); await f.App.FinishCurrentAsync(id, forDelivery: true);
            var snapshot = await f.App.SnapshotAsync();
            Check(snapshot.Session!.AsrReviewState == "Fallback" && snapshot.Segments.Single().RawText == turn.Socket.FinalText, "异常丢弃了实时结果");
        });
        foreach (string scenario in new[] { "timeout", "cancel", "expedite", "edit", "next-turn" })
        await test("高精度：忽略取消的迟到结果不覆盖 " + scenario, async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var turn = new Turn(); var next = new Turn();
            await using var f = await Fixture.Create([turn, next], (_, _) => { entered.TrySetResult(); return response.Task; });
            string id = await PrepareReview(f, turn); f.App.AsrReviewTimeoutMs = scenario == "timeout" ? 80 : 3000;
            using var cancel = new CancellationTokenSource(); using var expedite = new CancellationTokenSource();
            var finish = f.App.FinishCurrentAsync(id, token: cancel.Token, forDelivery: true, expedite: expedite.Token);
            await entered.Task.WaitAsync(Budget);
            if (scenario == "cancel") cancel.Cancel();
            if (scenario == "expedite") expedite.Cancel();
            if (scenario == "edit") { await f.App.EditAsync((await f.App.SnapshotAsync()).Segments.Single().Id, "保留手动编辑。"); response.SetResult(ReviewReply("迟到复核。")); }
            if (scenario == "next-turn") { Check(await f.Start(), "下一轮启动失败"); response.SetResult(ReviewReply("迟到复核。")); }
            await finish.WaitAsync(Budget);
            response.TrySetResult(ReviewReply("迟到复核。"));
            await Task.Delay(40);
            var snapshot = await f.App.SnapshotAsync();
            Check(!TranscriptText.Render(snapshot).Contains("迟到复核"), "迟到正文覆盖当前输入");
            if (scenario == "edit") Check(TranscriptText.Render(snapshot) == "保留手动编辑。", "用户编辑丢失");
            if (scenario != "next-turn") Check(snapshot.Session!.AsrReviewState == "Fallback", "回退状态丢失");
        });
        await test("高精度：关闭、取消、空白、缺口和超长录音不发起复核", async () =>
        {
            foreach (string scenario in new[] { "disabled", "cancelled", "empty", "gap", "long" })
            {
                int calls = 0; var turn = new Turn();
                if (scenario == "empty") turn.Socket.FinalText = "";
                if (scenario == "gap") turn.Socket.IncompleteTail = true;
                await using var f = await Fixture.Create([turn], (_, _) => { calls++; return Task.FromResult(ReviewReply("不应请求")); });
                await f.App.SaveSettingsAsync(f.App.Settings with { HighAccuracyEnabled = scenario != "disabled" }, f.App.Keys);
                Check(await f.Start(), "启动失败"); var capture = await turn.StartedCapture();
                int count = scenario == "long" ? AsrReviewBuffer.MaxBytes / 3200 + 1 : 1;
                for (int i = 0; i < count; i++) { await capture.EmitPcm(new byte[3200]); if (i % 10 == 0) await Task.Delay(1); }
                await f.App.StopAsync(false); var id = (await f.App.SnapshotAsync()).Session!.Id;
                await f.App.FinishCurrentAsync(id, allowPolish: scenario != "cancelled", forDelivery: true);
                Check(calls == 0 && (await f.App.SnapshotAsync()).Session!.AsrReviewState == "Skipped", "不合格音频仍上传复核");
            }
        });
    }
}
