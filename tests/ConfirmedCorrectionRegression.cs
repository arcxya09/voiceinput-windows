using RealtimeTranscription.Core;

internal static class ConfirmedCorrectionRegression
{
    private static void Check(bool value, string? message = null)
    { if (!value) throw new Exception(message ?? "Confirmed correction regression assertion failed"); }
    private static TermData Mapping(string old = "朱娜", string text = "JUNA", string scope = "default") => new() { Alias = old, Text = text, Scope = scope };
    private static TranscriptEngine Turn(params string[] parts)
    {
        var engine = new TranscriptEngine(new(), wholeTurn: true);
        engine.StartTask("task", []);
        for (int i = 0; i < parts.Length; i++) engine.Receive(new("result-generated", "task", i + 1, parts[i], true), false, false, [], false);
        engine.SealTask("task");
        return engine;
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("确认纠错仅使用调用方授权的启用词条，按项目与全局范围生效", () =>
        {
            string raw = "在朱娜做实验。";
            Check(ConfirmedCorrections.Apply(raw, [], "default").Text == raw);
            foreach (var term in new[] { Mapping() with { State = TermState.Candidate }, Mapping() with { State = TermState.Disabled }, Mapping(scope: "other") })
                Check(ConfirmedCorrections.Apply(raw, [term], "default").Text == raw);
            foreach (var term in new[] { Mapping(), Mapping(scope: "*") })
            {
                var result = ConfirmedCorrections.Apply(raw, [term], "default");
                Check(result.Text == "在JUNA做实验。" && result.Edits.Single().TermId == term.Id);
            }
            return Task.CompletedTask;
        });
        await test("确认纠错保持英文单词边界和精确大小写，支持中文与 Unicode 人名", () =>
        {
            var result = ConfirmedCorrections.Apply("XDeapSeek DeapSeek_2 DeapSeek DeapSeek2 deapseek，使用DeapSeek。", [Mapping("DeapSeek", "DeepSeek")], "default");
            Check(result.Text == "XDeapSeek DeapSeek_2 DeepSeek DeapSeek2 deapseek，使用DeepSeek。" && result.Edits.Count == 2);
            Check(ConfirmedCorrections.Apply("联系𠮷田。", [Mapping("𠮷田", "吉田")], "default").Text == "联系吉田。");
            Check(ConfirmedCorrections.Apply("DeepSee。", [Mapping("DeepSee", "DeepSeek")], "default").Text == "DeepSeek。");
            return Task.CompletedTask;
        });
        await test("确认纠错保留引号、行内代码、代码块、括号与未闭合引用", () =>
        {
            foreach (string literal in new[] { "\"朱娜\"", "'朱娜'", "“朱娜”", "‘朱娜’", "「朱娜」", "『朱娜』", "`朱娜`", "```text\n朱娜\n```", "~~~\n朱娜\n~~~", "（朱娜）", "(朱娜)", "[朱娜]", "(外层(朱娜)朱娜)", "\"引用\\\"朱娜\\\"正文\"" })
            {
                string raw = literal + "，使用朱娜。";
                Check(ConfirmedCorrections.Apply(raw, [Mapping()], "default").Text == literal + "，使用JUNA。", literal);
            }
            foreach (string literal in new[] { "“朱娜", "`朱娜", "```朱娜", "'朱娜" })
                Check(ConfirmedCorrections.Apply(literal, [Mapping()], "default").Text == literal, literal);
            Check(ConfirmedCorrections.Apply("don't use DeapSeek", [Mapping("DeapSeek", "DeepSeek")], "default").Text == "don't use DeepSeek");
            return Task.CompletedTask;
        });
        await test("确认纠错拒绝改变数字、单位和否定，允许保留相同数值的术语纠错", () =>
        {
            foreach (var pair in new[] { ("温度30度", "温度40度"), ("长度两米", "长度两秒"), ("长度30 米", "长度30 秒"), ("束流10keV", "束流10MeV"), ("束流30ＭｅＶ", "束流30ｋｅＶ"), ("毫安", "微安"), ("可以提交", "不可以提交"), ("not ready", "ready now"), ("can't submit", "can submit"), ("don't use", "do use"), ("isn't ready", "is ready"), ("won’t run", "will run"), ("cannot submit", "can submit"), ("ｃａｎｎｏｔ ｓｕｂｍｉｔ", "ｃａｎ ｓｕｂｍｉｔ"), ("第一实验室", "第二实验室"), ("温度30℃", "温度30℉") })
            {
                Check(!ConfirmedCorrections.IsSafePair(pair.Item1, pair.Item2), pair.ToString());
                Check(ConfirmedCorrections.Apply(pair.Item1, [Mapping(pair.Item1, pair.Item2)], "default").Text == pair.Item1);
            }
            Check(ConfirmedCorrections.Apply("使用DeapSeek2。", [Mapping("DeapSeek2", "DeepSeek2")], "default").Text == "使用DeepSeek2。");
            return Task.CompletedTask;
        });
        await test("确认纠错跳过冲突别名、全局冲突、替换链与环", () =>
        {
            Check(ConfirmedCorrections.Apply("朱娜", [Mapping(), Mapping(text: "LUNA", scope: "*")], "default").Text == "朱娜");
            Check(ConfirmedCorrections.Apply("AaBb BbCc", [Mapping("AaBb", "BbCc"), Mapping("BbCc", "CcDd")], "default").Text == "AaBb BbCc");
            Check(ConfirmedCorrections.Apply("AaBb BbCc", [Mapping("AaBb", "BbCc"), Mapping("BbCc", "AaBb")], "default").Text == "AaBb BbCc");
            Check(ConfirmedCorrections.Apply("人才", [Mapping("人才", "人才盘点")], "default").Text == "人才");
            var duplicate = ConfirmedCorrections.Apply("朱娜", [Mapping(), Mapping(scope: "*")], "default");
            Check(duplicate.Text == "JUNA" && duplicate.Edits.Count == 1);
            return Task.CompletedTask;
        });
        await test("确认纠错跳过所有重叠匹配，独立出现的明确词条仍生效", () =>
        {
            var result = ConfirmedCorrections.Apply("原字能院。字能院。朱娜。", [Mapping("原字能院", "原子能院"), Mapping("字能院", "字能所"), Mapping()], "default");
            Check(result.Text == "原字能院。字能所。JUNA。" && result.Edits.Count == 2);
            return Task.CompletedTask;
        });
        await test("关闭云润色仍本地纠错，原始 ASR 和用户修订来源保持不变", () =>
        {
            var engine = Turn("在朱娜做实验。", "继续使用朱娜。");
            var before = engine.Segments.ToArray();
            var result = engine.ApplyConfirmedCorrections([Mapping()]);
            Check(result.Edits.Count == 2 && engine.Session.AppliedCorrectionCount == 2 && engine.Session.AppliedCorrectionTerms.SequenceEqual(new[] { "JUNA" }));
            Check(engine.BeginWholePolish(false, []) == null && TranscriptText.Render(engine.Session, engine.Segments) == "在JUNA做实验。继续使用JUNA。");
            for (int i = 0; i < before.Length; i++)
            {
                var after = engine.Segments[i];
                Check(after.RawText == before[i].RawText && after.EditRevision == before[i].EditRevision && after.SourceRevision == before[i].SourceRevision && after.UndoHistory == before[i].UndoHistory && !after.UserLocked);
                Check(CorrectionRules.Active(after).Count == 0);
            }
            return Task.CompletedTask;
        });
        await test("全文润色输入包含确认纠错，云端失败或拒绝修改时仍保留纠错", () =>
        {
            foreach (string? candidate in new string?[] { null, "在朱娜做实验。", "在JUNA做实验。" })
            {
                var engine = Turn("在朱娜做实验。");
                engine.ApplyConfirmedCorrections([Mapping()]);
                var work = engine.BeginWholePolish(true, ["JUNA"])!;
                Check(work.Raw == "在JUNA做实验。");
                engine.CompleteWholePolish(work, candidate, "离线");
                Check(TranscriptText.Render(engine.Session, engine.Segments) == "在JUNA做实验。" && engine.Segments.Single().RawText == "在朱娜做实验。" && engine.Session.AppliedCorrectionCount == 1);
            }
            return Task.CompletedTask;
        });
        await test("跨 ASR 片段的引用保持原样，重复应用与载入历史不触发二次替换", () =>
        {
            var engine = Turn("引用：“", "朱娜”。", "使用朱娜。");
            engine.ApplyConfirmedCorrections([Mapping()]);
            Check(TranscriptText.Render(engine.Segments) == "引用：“朱娜”。使用JUNA。");
            Check(engine.ApplyConfirmedCorrections([Mapping("JUNA", "CASPAR")]).Edits.Count == 0);
            Check(TranscriptText.Render(engine.Segments) == "引用：“朱娜”。使用JUNA。");
            var restored = new TranscriptEngine(JsonCodec.Clone(engine.Session), wholeTurn: true);
            restored.Restore(engine.Segments);
            Check(restored.ApplyConfirmedCorrections([Mapping("JUNA", "CASPAR")]).Edits.Count == 0 && TranscriptText.Render(restored.Segments) == TranscriptText.Render(engine.Segments));
            return Task.CompletedTask;
        });
        await test("确认纠错等待录音结束，用户恢复原文撤回自动纠错且不再次应用", () =>
        {
            var engine = new TranscriptEngine(new(), wholeTurn: true);
            engine.StartTask("task", []);
            engine.Receive(new("result-generated", "task", 1, "朱娜。", true), false, false, [], false);
            bool rejected = false;
            try { engine.ApplyConfirmedCorrections([Mapping()]); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected);
            engine.SealTask("task"); engine.ApplyConfirmedCorrections([Mapping()]);
            engine.Edit(engine.Segments.Single().Id, "", "恢复原文");
            Check(TranscriptText.Render(engine.Segments) == "朱娜。" && engine.Session.AppliedCorrectionCount == 0);
            Check(engine.ApplyConfirmedCorrections([Mapping()]).Edits.Count == 0 && TranscriptText.Render(engine.Segments) == "朱娜。");
            return Task.CompletedTask;
        });
        await test("不完整或失败音频整轮跳过确认纠错，保留确认原文供检查", () =>
        {
            foreach (string failure in new[] { "missing", "partial", "failed" })
            {
                var engine = new TranscriptEngine(new(), wholeTurn: true);
                engine.StartTask("task", []);
                if (failure == "partial") engine.Receive(new("result-generated", "task", 1, "引用：“", false), false, false, [], false);
                engine.Receive(new("result-generated", "task", failure == "failed" ? 1 : 2, "朱娜。", true), false, false, [], false);
                engine.SealTask("task", failure == "failed" ? "音频发送失败" : "");
                string original = TranscriptText.Render(engine.Segments);
                Check(engine.ApplyConfirmedCorrections([Mapping()]).Edits.Count == 0 && engine.Session.AppliedCorrectionCount == 0, failure);
                Check(TranscriptText.Render(engine.Segments) == original && engine.Segments.Last().FinalText == "朱娜。", failure);
            }
            return Task.CompletedTask;
        });
        await test("人工修改与撤销按实际保留的纠错更新计数，重启后仍一致", () =>
        {
            var engine = Turn("在朱娜做实验。", "继续使用朱娜。");
            engine.ApplyConfirmedCorrections([Mapping()]);
            string first = engine.Segments[0].Id;
            engine.Edit(first, "在JUNA做实验！");
            Check(engine.Session.AppliedCorrectionCount == 2);
            engine.Edit(first, "在朱娜做实验！");
            Check(engine.Session.AppliedCorrectionCount == 1 && engine.Session.AppliedCorrectionTerms.SequenceEqual(new[] { "JUNA" }));
            var restored = new TranscriptEngine(JsonCodec.Clone(engine.Session), wholeTurn: true);
            restored.Restore(JsonCodec.Clone(engine.Segments));
            Check(restored.Session.AppliedCorrectionCount == 1);
            restored.Edit(first, "", "撤销");
            Check(restored.Session.AppliedCorrectionCount == 2 && restored.Segments[0].FinalText == "在JUNA做实验！");
            restored.Edit(first, "", "删除");
            Check(restored.Session.AppliedCorrectionCount == 1);
            restored.Edit(first, "", "撤销");
            Check(restored.Session.AppliedCorrectionCount == 2);
            restored.Edit(restored.Segments[1].Id, "", "恢复原文");
            Check(restored.Session.AppliedCorrectionCount == 1 && restored.Segments[1].FinalText == "继续使用朱娜。");
            return Task.CompletedTask;
        });
        await test("分段边界附近的纠错不破坏段落和英文间隔", () =>
        {
            var engine = Turn("prefix", "DeapSeek", "，正文。");
            engine.ApplyConfirmedCorrections([Mapping("DeapSeek", "DeepSeek")]);
            Check(TranscriptText.Render(engine.Segments) == "prefix DeepSeek，正文。");
            var paragraphs = new TranscriptEngine(new(), wholeTurn: true);
            paragraphs.StartTask("task", []);
            paragraphs.Receive(new("result-generated", "task", 1, "DeapSeek", true), false, false, [], false);
            paragraphs.Paragraph();
            paragraphs.Receive(new("result-generated", "task", 2, "DeapSeek", true), false, false, [], false);
            paragraphs.SealTask("task"); paragraphs.ApplyConfirmedCorrections([Mapping("DeapSeek", "DeepSeek")]);
            Check(TranscriptText.Render(paragraphs.Segments) == "DeepSeek\r\n\r\nDeepSeek");
            return Task.CompletedTask;
        });
    }
}
