using System.Globalization;

namespace RealtimeTranscription.Core;

public static class UiPresentation
{
    public static string LatestText(string text, int limit = 120)
    {
        if (limit <= 0) return "";
        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var starts = StringInfo.ParseCombiningCharacters(text);
        return starts.Length <= limit ? text : "…" + text[starts[^limit]..];
    }

    public static string HotkeyName(string key) => key switch { "F8" => "F8", "F9" => "F9", _ => "右侧 Ctrl" };

    public static string Phase(TranscriptSnapshot? snapshot, bool busy, bool enabled, bool dictationOnly)
    {
        if (snapshot?.State == CaptureState.Connecting)
            return snapshot.CaptureReleased ? "尾句处理中" : snapshot.LocalAudioReady ? "正在听" : "准备麦克风";
        if (snapshot?.State == CaptureState.Recording) return snapshot.CaptureReleased ? "尾句处理中" : "正在听";
        if (snapshot?.State == CaptureState.Draining) return "尾句处理中";
        if (busy) return snapshot?.Session?.WholePolishState == "Waiting" ? "全文润色中" : snapshot?.Session?.DeliveryState == "Sending" ? "正在粘贴" : "正在完成本轮";
        if (!enabled) return "快捷键已暂停";
        if (snapshot?.State == CaptureState.Faulted) return "本轮未完成";
        return dictationOnly ? "仅听写已就绪" : "按住说话已就绪";
    }

    public static string SaveWarning(bool memoryAvailable, string memoryStatus, int unsaved)
    {
        string warning = memoryAvailable ? "" : memoryStatus;
        if (unsaved > 0) warning += (warning.Length > 0 ? "\n" : "") + $"有 {unsaved} 项尚未保存。请重试保存，或先复制、导出正文；退出前请确认已保留。";
        return warning;
    }
}
