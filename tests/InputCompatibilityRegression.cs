using RealtimeTranscription.Core;
internal static class InputCompatibilityRegression
{
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        static void Check(bool ok){if(!ok)throw new Exception("Compatibility policy regression");}
        await test("2.5.0 录音期间跟随原窗口：子焦点变化不降级，短暂离开可恢复，持续离开才停止自动输入",()=>
        {
            var focus=new RecordingFocusContinuity();
            Check(focus.Observe(FocusObservation.Stable,100));
            Check(focus.Observe(FocusObservation.Changed,200));
            Check(focus.Observe(FocusObservation.Unavailable,1600));
            Check(focus.Observe(FocusObservation.Stable,1650)&&!focus.ManualOnly);
            Check(focus.Observe(FocusObservation.Unavailable,2000));
            Check(!focus.Observe(FocusObservation.Changed,3500)&&focus.ManualOnly);
            Check(!focus.Observe(FocusObservation.Stable,3501));
            var other=new RecordingFocusContinuity();
            Check(other.Observe(FocusObservation.Changed,0)&&!other.ManualOnly);
            Check(!other.Observe(FocusObservation.Changed,1500)&&other.ManualOnly);
            return Task.CompletedTask;
        });
        await test("2.1.8 复制成功与失败如实呈现，无焦点的完成后复制提示传入浮窗",()=>
        {
            string reason="输入位置暂不可用，已继续听写；完成后复制。";
            Check(CapsulePresentation.EmptyPreview("短按已取消。",true,"Cancelled")=="短按已取消。");
            Check(CapsulePresentation.EmptyPreview("麦克风不可用",true,"StartFailed")=="麦克风不可用");
            Check(CapsulePresentation.CompletionLabel("StartFailed")=="未开始");
            Check(CapsulePresentation.CompletionLabel("Copied")=="已复制");
            Check(CapsulePresentation.CompletionLabel("CopyFailed")=="复制失败");
            var model=new VoicePreviewState();model.Begin("input");
            var snap=new TranscriptSnapshot(new(){Id="input",DeliveryState="Pending",DeliveryReason=reason},[],CaptureState.Recording,0,0,"正在听");
            Check(model.Snapshot(snap,true,true,false)!.Status.Contains("完成后复制"));
            var copying=snap with{State=CaptureState.Stopped,Session=snap.Session! with{DeliveryState="Copying"}};
            Check(model.Snapshot(copying,true,true,true)?.Status=="正在复制");
            var sending=copying with{Session=copying.Session! with{DeliveryState="Sending"}};
            Check(model.Snapshot(sending,true,true,false)?.Status=="正在输入");
            return Task.CompletedTask;
        });
    }
}
