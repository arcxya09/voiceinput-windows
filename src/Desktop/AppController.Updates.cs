using RealtimeTranscription.Core;
namespace RealtimeTranscription.Desktop;

public sealed partial class AppController
{
    public async Task PrepareUpdateAsync()
    {
        await OnActor(() =>
        {
            if (state is CaptureState.Connecting or CaptureState.Recording or CaptureState.Draining || activePolish > 0 || extractionGate.CurrentCount == 0)
                throw new InvalidOperationException("请等待识别、润色或词条整理完成后更新。");
        });
        if (Settings.SaveMemory) await RetrySaveAsync();
        if (MemoryAvailable)
        {
            await Repository.BarrierAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Repository.CheckpointAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        var snapshot = await SnapshotAsync();
        if (FailedSaveCount > 0 || snapshot.Unsaved > 0)
            throw new InvalidOperationException("仍有内容未保存，请重试保存或导出后再更新。");
    }
}
