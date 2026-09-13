using RealtimeTranscription.Core;

internal static class UiReviewRegression
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        static void Check(bool valid) { if (!valid) throw new Exception("UI presentation regression failed"); }
        static Task Sync(Action action) { action(); return Task.CompletedTask; }
        await test("长输入预览显示最新文字并保留完整字形", () => Sync(() =>
        {
            Check(UiPresentation.LatestText(new string('旧', 200) + "最新内容", 4) == "…最新内容");
            Check(UiPresentation.LatestText("旧e\u0301👩‍🔬", 2) == "…e\u0301👩‍🔬");
            Check(UiPresentation.LatestText("短句") == "短句");
        }));
        await test("无新识别快照时暂停恢复及仅听写状态仍正确", () => Sync(() =>
        {
            var snapshot = new TranscriptSnapshot(new(), [], CaptureState.Stopped, 0, 0, "文字已交给目标应用。");
            Check(UiPresentation.Phase(snapshot, false, false, false) == "快捷键已暂停");
            Check(UiPresentation.Phase(snapshot, false, true, true) == "仅听写已就绪");
            Check(UiPresentation.Phase(snapshot with { Session = snapshot.Session! with { WholePolishState = "Waiting" } }, true, true, false) == "全文润色中");
            Check(UiPresentation.Phase(snapshot, false, true, false) == "按住说话已就绪");
            Check(UiPresentation.HotkeyName("F8") == "F8" && UiPresentation.HotkeyName("F9") == "F9");
        }));
        await test("投递成功不清除未保存或降级警告", () => Sync(() =>
        {
            Check(UiPresentation.SaveWarning(true, "", 2).Contains("2 项尚未保存"));
            Check(UiPresentation.SaveWarning(false, "本地记忆暂不可用", 2).Contains("本地记忆暂不可用"));
            Check(UiPresentation.SaveWarning(false, "本地记忆暂不可用", 0) == "本地记忆暂不可用");
            Check(UiPresentation.SaveWarning(true, "", 0) == "");
        }));
    }
}
