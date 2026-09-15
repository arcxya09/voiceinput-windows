using RealtimeTranscription.Core;
internal static class InputCompatibilityRegression
{
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        static void Check(bool ok){if(!ok)throw new Exception("Compatibility policy regression");}
        await test("2.1.7 录音焦点波动恢复、边界降级及跨窗口永久停止自动上屏",()=>
        {
            var focus=new RecordingFocusContinuity();
            Check(focus.Observe(FocusObservation.Stable,FocusObservation.Changed,100));
            Check(focus.Observe(FocusObservation.Stable,FocusObservation.Unavailable,1099));
            Check(focus.Observe(FocusObservation.Stable,FocusObservation.Stable,1100));
            Check(focus.Observe(FocusObservation.Unavailable,FocusObservation.Unavailable,2000));
            Check(!focus.Observe(FocusObservation.Stable,FocusObservation.Changed,3000));
            Check(!focus.Observe(FocusObservation.Stable,FocusObservation.Stable,3001));
            var other=new RecordingFocusContinuity();
            Check(!other.Observe(FocusObservation.Changed,FocusObservation.Stable,0)&&other.ManualOnly);
            return Task.CompletedTask;
        });
        await test("2.1.7 无文字取消和启动失败显示原因，手动复制提示传入实时浮窗",()=>
        {
            string reason="输入位置暂不可用，已继续听写；完成后请手动复制。";
            Check(CapsulePresentation.EmptyPreview("短按已取消。",true,"Cancelled")=="短按已取消。");
            Check(CapsulePresentation.EmptyPreview("麦克风不可用",true,"StartFailed")=="麦克风不可用");
            Check(CapsulePresentation.CompletionLabel("StartFailed")=="未开始");
            var model=new VoicePreviewState();model.Begin("input");
            var snap=new TranscriptSnapshot(new(){Id="input",DeliveryState="Pending",DeliveryReason=reason},[],CaptureState.Recording,0,0,"正在听");
            Check(model.Snapshot(snap,true,true,false)!.Status.Contains("手动复制"));
            return Task.CompletedTask;
        });
    }
}
