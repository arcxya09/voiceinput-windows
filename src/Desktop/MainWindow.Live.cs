using System.Windows;
using RealtimeTranscription.Core;
using Forms = System.Windows.Forms;

namespace RealtimeTranscription.Desktop;

public partial class MainWindow
{
    private TranscriptSnapshot? lastSnapshot;
    private Forms.ToolStripMenuItem? dictationMenu;
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
        if (dictationMenu != null) { dictationMenu.Checked = dictation; dictationMenu.Enabled = !ManagementBusy; }
        updatingDictation = false;
        string warning = UiPresentation.SaveWarning(controller.MemoryAvailable, controller.MemoryStatus, controller.FailedSaveCount);
        SaveWarningText.Text = warning;
        SaveWarningPanel.Visibility = warning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetrySaveButton.IsEnabled = !ManagementBusy;
        HistoryTab.IsEnabled = VocabularyTab.IsEnabled = controller.MemoryAvailable;
        HistoryTab.ToolTip = VocabularyTab.ToolTip = controller.MemoryAvailable ? null : controller.MemoryStatus;
        var version = typeof(MainWindow).Assembly.GetName().Version;
        VersionInfo.Text = $"版本 {version?.ToString(3) ?? "未知"} · Windows x64 · Key 和本地文本通过当前 Windows 用户加密保存。";
        UsageHelp.Text = $"1. 在设置中填写百炼 Key；需要全文润色时填写 DeepSeek Key。\n2. { (dictation ? "仅听写模式：可在任意窗口" : "在其他应用的文本框中") }按住 {key}，麦克风就绪后说话。松开即停止录音。\n3. { (dictation ? "结果保留在本程序，使用“复制正文”或托盘“复制最近结果”。" : "尾句和全文整理完成后输入原位置，不自动按回车。目标改变或输入受限时，结果保留供复制。") }\n4. 在“对照与编辑”中保存手动修订，到词库确认有用的纠错候选。\n5. Esc 取消本轮。自动输入模式下其他按键或鼠标操作也会取消投递；上一轮仍在整理时请稍后再按。";
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
            StatusText.Text = enabled ? "仅听写模式已开启：按住说话键，完成后复制正文。" : "自动输入模式已开启：请先选中其他应用的文本框。";
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
