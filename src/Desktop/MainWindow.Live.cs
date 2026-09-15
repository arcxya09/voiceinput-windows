using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;

public partial class MainWindow
{
    private TranscriptSnapshot? lastSnapshot;
    private bool updatingDictation;

    private void RefreshLiveState()
    {
        bool busy = ptt?.Busy == true;
        string key = UiPresentation.HotkeyName(controller.Settings.Hotkey);
        bool dictation = controller.Settings.DictationOnly;
        HotkeyHint.Text = dictation ? $"按住 {key} 听写 · 完成后复制正文" : $"按住 {key} 说话 · 松开后输入原位置";
        StateLabel.Text = ptt == null ? "快捷键未启动" : UiPresentation.Phase(lastSnapshot, busy, ptt.Enabled, dictation);
        RecordStageText.Text = lastSnapshot?.State switch { CaptureState.Connecting => "麦克风 / 连接", CaptureState.Recording => "实时识别", CaptureState.Draining => "正在收齐尾句", _ => "本轮识别预览" };
        updatingDictation = true;
        DictationOnlyBox.IsChecked = dictation;
        DictationOnlyBox.IsEnabled = !ManagementBusy;
        trayMenu?.SetState(ptt?.Enabled == true, dictation, !ManagementBusy);
        updatingDictation = false;
        string warning = UiPresentation.SaveWarning(controller.MemoryAvailable, controller.MemoryStatus, controller.FailedSaveCount);
        SaveWarningText.Text = warning;
        SaveWarningPanel.Visibility = warning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetrySaveButton.IsEnabled = !ManagementBusy;
        HistoryTab.IsEnabled = VocabularyTab.IsEnabled = controller.MemoryAvailable;
        ToolTipService.SetToolTip(HistoryTab,controller.MemoryAvailable ? null : controller.MemoryStatus);
        ToolTipService.SetToolTip(VocabularyTab,controller.MemoryAvailable ? null : controller.MemoryStatus);
        var version = typeof(MainWindow).Assembly.GetName().Version;
        VersionInfo.Text = $"版本 {version?.ToString(3) ?? "未知"} · Windows x64 · Key 和本地文本通过当前 Windows 用户加密保存。";
        UsageHelp.Text = $"1. 在设置中填写百炼 Key；需要全文润色时填写 DeepSeek Key。\n2. { (dictation ? "仅听写模式：可在任意窗口" : "在其他应用的文本框中") }按住 {key}，麦克风就绪后说话。松开即停止录音。\n3. { (dictation ? "结果保留在本程序，使用“复制正文”或托盘“复制最近结果”。" : "整理完成后整段粘贴到原位置，不自动按回车。确认正文已上屏后恢复原剪贴板；无法确认时保留正文。目标改变时停止粘贴。") }\n4. 在“对照与编辑”中保存手动修订，到词库确认有用的纠错候选。\n5. Esc 取消本轮。松手等待上屏时，普通回车跳过剩余润色（不会转发回车发送消息）。其他按键或鼠标操作仍会取消投递；上一轮仍在整理时请稍后再按。";
        bool captureActive = busy && lastSnapshot?.State is CaptureState.Recording or CaptureState.Connecting;
        MicLevel.Value = captureActive || microphoneTesting ? Volatile.Read(ref level) : 0;
        overlay.SetMeter(Volatile.Read(ref level), captureActive);
        overlay.SetPersistentWarning(controller.FailedSaveCount > 0 ? $"{controller.FailedSaveCount} 项保存失败。请打开管理窗口重试保存，或先复制正文。" : "");
        if (tray != null) tray.Text = controller.FailedSaveCount > 0 ? "语音输入法 · 有内容未保存" : "语音输入法 · " + StateLabel.Text;
    }

    private async Task SetDictationMode(bool enabled)
    {
        try
        {
            EnsureIdle();savingSettings = true;
            await controller.SaveSettingsAsync(controller.Settings with { DictationOnly = enabled }, controller.Keys);
            StatusText.Text = enabled ? "仅听写模式已开启：按住说话键，完成后复制正文。" : "自动粘贴已开启：请先选中其他应用的文本框，识别正文将替换剪贴板内容。";
        }
        finally { savingSettings = false; RefreshLiveState(); }
    }

    private async void DictationMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready || updatingDictation) return;
        bool enabled = DictationOnlyBox.IsChecked == true;
        await Safe(() => SetDictationMode(enabled));
    }
}
