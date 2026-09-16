namespace RealtimeTranscription.Core;

/// <summary>Compact terminal labels come from delivery results, never inferred from prose.</summary>
public static class CapsulePresentation
{
    public static TimeSpan CompletionDuration(string? state) => TimeSpan.FromMilliseconds(state switch
    {
        "Sent" or "PasteSent" => 450,
        "Copied" => 1200,
        "Cancelled" or "Empty" => 900,
        _ => 3000
    });

    public static string CompletionLabel(string? state) => state switch
    {
        "Sent" => "已输入",
        "PasteSent" => "已发起粘贴",
        "Copied" => "已复制",
        "CopyFailed" or "Dictated" or "Blocked" => "待复制",
        "Cancelled" => "已取消",
        "Failed" => "识别失败",
        "StartFailed" => "未开始",
        "Partial" => "部分输入",
        "Unknown" => "请核对",
        "Empty" => "无文字",
        _ => "结束"
    };

    public static string EmptyPreview(string status, bool completed, string? state) =>
        completed && state == "Empty" ? "本轮尚未识别到文字" : status switch
        {
            "正在听" => "正在聆听",
            "准备麦克风" or "准备 / 连接" => "准备麦克风…",
            "尾句处理中" or "正在完成本轮" => "正在整理…",
            "" => completed ? "本轮尚未识别到文字" : "正在聆听",
            _ => UiPresentation.LatestText(status, 80)
        };
}
