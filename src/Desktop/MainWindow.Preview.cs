using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;

public partial class MainWindow
{
    private readonly VoicePreviewState voicePreview = new();
    internal VoiceOverlay RecognitionOverlay => overlay;

    // Both the real input service and the offline desktop checks use this bridge.
    // Every callback enters the same UI queue before touching the preview state.
    internal void ConnectPreviewEvents(IVoicePreviewEvents source)
    {
        source.TurnStarted += turnId => UI(() =>
        {
            if (voicePreview.Begin(turnId)) overlay.BeginTurn();
        });
        source.TurnCompleted += result => UI(() =>
        {
            if (voicePreview.Complete(result) is not { } frame) return;
            ShowPreviewFrame(frame);
            StatusText.Text = frame.Status;
        });
        source.Notice += notice => UI(() =>
        {
            if (notice.TurnId != null)
            {
                if (notice.TurnId != voicePreview.TurnId || voicePreview.IsCompleted) return;
            }
            else
            {
                if (!voicePreview.CanShowNotice(notice)) return;
                overlay.Update(notice.Status, dismiss: true);
            }
            StatusText.Text = notice.Status;
        });
    }

    private void RefreshPreview(TranscriptSnapshot snapshot)
    {
        if (voicePreview.Snapshot(snapshot, ptt?.Busy == true, ptt?.Enabled == true,
            controller.Settings.DictationOnly) is { } frame) ShowPreviewFrame(frame);
    }

    private void ShowPreviewFrame(VoicePreviewFrame frame)
        => overlay.Update(frame.Status, frame.Text, frame.Dismiss);
}
