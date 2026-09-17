using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;

public sealed partial class AppController
{
    private DomainProfile? domainProfile;
    private DateTimeOffset lastDomainAttempt;
    // A late database read must not restore hints derived from text already edited in memory.
    private bool ProfileMatchesCurrentEdits(DomainProfile? profile) => profile == null || engine == null
        || !profile.Sources.Any(e => e.SessionId == engine.Session.Id && (!engine.Session.AllowLearning
            || engine.Segments.Any(s => s.Id == e.SegmentId && (s.EditRevision != e.EditRevision || s.SourceRevision != e.SourceRevision || s.OutputState != OutputState.Published))));
    public string DomainReport()
    {
        var profile = domainProfile;
        if (profile == null) return "尚无领域分析。启用智能领域词库后，积累历史输入即可在后台分析，也可点击立即更新。";
        return $"最近分析：{profile.UpdatedAt.ToLocalTime():g}\n当前项目的领域：\n" + string.Join("\n", profile.Topics.Select(t => $"{t.Name} · 近期相关等级 {t.Relevance}/5"))
            + $"\n\n本次生成后保留 {profile.ActiveTerms.Count} 个预测易错词，权重为 1。禁用、删除、确认后的实际选词以“查看下轮热词”为准。\n"
            + "超过 14 天未更新暂停注入。词库中的旧预测词可能已退出当前话题。\n等级是 AI 预测，不代表实测识别错误率。普通历史提取仍需确认；预测词不用于自动文字替换。\n关闭智能领域词库可停止分析和注入；确认词条可转为长期个人词条。\n\n"
            + string.Join("、", profile.ActiveTerms);
    }

    public async Task RefreshDomainLexiconAsync(bool force = true)
    {
        if (!MemoryAvailable || !Settings.SaveMemory || !Settings.AllowLearning || !Settings.DomainLexiconEnabled)
        { if (force) throw new InvalidOperationException("请先启用本地文本记忆、允许学习和智能领域词库，并保存设置。"); return; }
        if (Keys.DeepSeekKey.Length == 0) { if (force) throw new InvalidOperationException("请先保存 DeepSeek API Key。"); return; }
        if (!force && DateTimeOffset.UtcNow - lastDomainAttempt < TimeSpan.FromMinutes(2)) return;
        if (!await extractionGate.WaitAsync(0, lifetime.Token)) { if (force) throw new InvalidOperationException("另一个词库 AI 任务正在进行。"); return; }
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancelled.CancelAfter(TimeSpan.FromSeconds(40)); extraction = cancelled; Extracting?.Invoke(true);
        try
        {
            var snap = await SnapshotAsync();
            if (snap.State is CaptureState.Recording or CaptureState.Connecting or CaptureState.Draining || snap.Pending > 0 || snap.Unsaved > 0 || Volatile.Read(ref activePolish) > 0)
            { if (force) throw new InvalidOperationException("请等待当前输入和保存完成后更新领域词库。"); return; }
            string project = Settings.ProjectId, key = Keys.DeepSeekKey; long epoch = await OnActor(() => knowledgeEpoch);
            await Repository.BarrierAsync(); var samples = await Repository.DomainSamplesAsync(project, cancelled.Token);
            var prior = await Repository.DomainProfileAsync(project);
            if (!DomainLexicon.ShouldRefresh(prior, samples, DateTimeOffset.UtcNow, force))
            { if (force) await OnActor(() => Status("可学习历史不足 40 字，请积累一些输入后再更新。")); return; }
            var disabled = terms.Where(t => t.State == TermState.Disabled).Select(t => t.Text)
                .Concat(suppressed.Select(s => s.Contains('\n') ? s.Split('\n', 2)[^1] : s));
            object input = DomainLexicon.Input(samples, disabled);
            int reserve = Infrastructure.DeepSeekClient.Request(DomainLexicon.Prompt, input, true, DomainLexicon.MaxTokens).Length + DomainLexicon.MaxTokens;
            var at = DateTimeOffset.UtcNow; lastDomainAttempt = at;
            await Repository.ReserveTermBudgetAsync(reserve, Settings.DailyExtractionTokens, at, cancelled.Token);
            UsageData? actual = null; string json;
            try { json = await deepseek.CallAsync(DomainLexicon.Prompt, input, true, DomainLexicon.MaxTokens, "domain_lexicon", key, 30000, cancelled.Token, u => actual = u); }
            finally { if (actual is { Unknown: false }) await Repository.SaveUsageAsync(new("term_budget", actual.InputTokens + actual.OutputTokens - reserve, 0, false, at)); }
            cancelled.Token.ThrowIfCancellationRequested();
            var prediction = DomainLexicon.Parse(json, samples, project, DateTimeOffset.UtcNow);
            await knowledgeGate.WaitAsync(cancelled.Token);
            try
            {
                bool valid = await OnActor(() => knowledgeEpoch == epoch && Settings.ProjectId == project && Settings.DomainLexiconEnabled && Settings.SaveMemory && Settings.AllowLearning);
                if (!valid) throw new OperationCanceledException(cancelled.Token);
                int count = await Repository.CommitDomainPredictionAsync(samples, prediction, cancelled.Token);
                await ReloadTerms();
                if (force) await OnActor(() => Status($"领域词库已更新：{prediction.Profile.Topics.Count} 个话题，{count} 个预测易错词。下轮识别生效。"));
                LogEvent("DomainLexiconUpdated", fields: [("Topics", prediction.Profile.Topics.Count), ("Terms", count)]);
            }
            finally { knowledgeGate.Release(); }
        }
        catch (OperationCanceledException) when (!force || cancelled.IsCancellationRequested) { if (force) await OnActor(() => Status("领域分析已取消或超时，原词库保留。")); }
        catch (Exception e) when (!force) { LogEvent("DomainLexiconFailed", e); }
        finally { extraction = null; extractionGate.Release(); Extracting?.Invoke(false); }
    }
}
