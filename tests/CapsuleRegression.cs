using RealtimeTranscription.Core;

internal static class CapsuleRegression
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        static void Check(bool ok) { if (!ok) throw new Exception("Capsule feedback regression failed."); }
        await test("胶囊结束标签使用真实投递状态，未知和部分输入不得显示成功", () =>
        {
            var model = new VoicePreviewState();
            model.Begin("first");
            var frame = model.Complete(new("first", "已完成", "已识别的文字") { DeliveryState = "Unknown" })!;
            Check(frame.Text == "已识别的文字" && frame.DeliveryState == "Unknown");
            Check(CapsulePresentation.CompletionLabel(frame.DeliveryState) == "请核对");
            Check(CapsulePresentation.CompletionLabel("Partial") == "部分输入");
            Check(CapsulePresentation.CompletionLabel("Sent") == "已输入");
            Check(CapsulePresentation.CompletionLabel("Dictated") == "待复制");
            Check(CapsulePresentation.CompletionLabel("Blocked") == "待复制");
            Check(CapsulePresentation.CompletionLabel(null) == "结束");
            return Task.CompletedTask;
        });
        await test("胶囊终态标记与文字同轮传递，旧轮次不能覆盖新一轮", () =>
        {
            var model = new VoicePreviewState();
            model.Begin("first");
            Check(model.Complete(new("first", "已输入", "上一轮") { DeliveryState = "Sent" })?.DeliveryState == "Sent");
            model.Begin("second");
            Check(model.Complete(new("first", "已输入", "迟到内容") { DeliveryState = "Sent" }) == null);
            var frame = model.Complete(new("second", "已取消", "") { DeliveryState = "Cancelled" })!;
            Check(frame.Text.Length == 0 && CapsulePresentation.CompletionLabel(frame.DeliveryState) == "已取消");
            Check(CapsulePresentation.EmptyPreview(frame.Status, true, frame.DeliveryState) == "本轮尚未识别到文字");
            return Task.CompletedTask;
        });
        await test("单行胶囊无识别文字时显示真实阶段，错误原因仍可见", () =>
        {
            Check(CapsulePresentation.EmptyPreview("正在听", false, null) == "正在聆听");
            Check(CapsulePresentation.EmptyPreview("尾句处理中", false, null) == "正在整理…");
            Check(CapsulePresentation.EmptyPreview("连接失败，请检查网络", true, "Blocked") == "连接失败，请检查网络");
            Check(CapsulePresentation.EmptyPreview("没有需要输入的文字", true, "Empty") == "本轮尚未识别到文字");
            return Task.CompletedTask;
        });
    }
}
