using RealtimeTranscription.Core;

internal static class Review212UiRegression
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        static void Check(bool value) { if (!value) throw new Exception("2.1.2 UI feedback regression failed"); }
        static Task Sync(Action action) { action(); return Task.CompletedTask; }

        await test("2.1.2 终态反馈直接携带全文，不依赖75ms前的partial快照", () => Sync(() =>
        {
            var state = new VoicePreviewState();
            Check(state.Begin("turn"));
            var final = new TranscriptSnapshot(new SessionData { Id = "turn", DeliveryState = "Dictated" },
                [new SegmentData { RawText = "你好", FinalText = "你好", AsrState = AsrState.Confirmed, OutputState = OutputState.Published }],
                CaptureState.Stopped, 0, 0, "听写完成");
            Check(state.Snapshot(final, false, true, true) == null);
            var frame = state.Complete(new("turn", final.Status, TranscriptText.Render(final)));
            Check(frame is { Text: "你好", Dismiss: true });
        }));

        await test("2.1.2 终态后迟到快照及重复完成不重启隐藏计时", () => Sync(() =>
        {
            var state = new VoicePreviewState(); state.Begin("turn");
            Check(state.Complete(new("turn", "完成", "最终正文")) != null);
            var late = new TranscriptSnapshot(new SessionData { Id = "turn" }, [], CaptureState.Recording, 0, 0, "旧状态");
            Check(state.Snapshot(late, true, true, false) == null);
            Check(state.Complete(new("turn", "重复完成", "旧正文")) == null);
            Check(!state.CanShowNotice(new("上一段仍在整理", "turn")));
        }));

        await test("2.1.2 新轮次不接受旧完成、旧快照或旧轮次提示", () => Sync(() =>
        {
            var state = new VoicePreviewState(); state.Begin("old"); state.Begin("new");
            Check(state.Complete(new("old", "旧完成", "旧正文")) == null);
            Check(!state.CanShowNotice(new("旧状态", "old")));
            Check(!state.CanShowNotice(new("未归属提示")));
            Check(state.Snapshot(new(new SessionData { Id = "old" }, [], CaptureState.Recording, 0, 0, ""), true, true, false) == null);
            Check(!state.IsCompleted && state.TurnId == "new");
            Check(state.Complete(new("new", "本轮已取消", "已识别的末句")) is { Text: "已识别的末句", Dismiss: true });
        }));

        await test("2.1.2 空取消显式清空本轮文字，全文润色结果保留", () => Sync(() =>
        {
            var state = new VoicePreviewState(); state.Begin("first");
            var polished = new TranscriptSnapshot(new SessionData { Id = "first", WholePolishState = "Completed", WholePolishText = "整理后的完整结果" }, [], CaptureState.Stopped, 0, 0, "");
            Check(state.Complete(new("first", "完成", TranscriptText.Render(polished))) is { Text: "整理后的完整结果" });
            state.Begin("empty");
            Check(state.Complete(new("empty", "短按已取消", "")) is { Text: "", Dismiss: true });
        }));

        await test("2.1.2 原文对照保留已删除已确认片段并排除未确认草稿", () => Sync(() =>
        {
            var deleted = new SegmentData { TaskOrder = 1, SentenceId = 1, RawText = "第一句。", FinalText = "", AsrState = AsrState.Confirmed, OutputState = OutputState.Deleted };
            var edited = new SegmentData { TaskOrder = 1, SentenceId = 2, RawText = "原来的第二句。", FinalText = "改写后的第二句。", AsrState = AsrState.Confirmed, OutputState = OutputState.Published };
            var draft = new SegmentData { TaskOrder = 1, SentenceId = 3, RawText = "不可计入的草稿", PartialText = "不可计入的草稿", AsrState = AsrState.Unresolved, OutputState = OutputState.Unresolved };
            Check(TranscriptComparison.Original([deleted, edited, draft]) == "第一句。原来的第二句。");
            Check(TranscriptText.Render([deleted, edited, draft]) == "改写后的第二句。");
        }));
    }
}
