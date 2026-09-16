using Microsoft.UI.Xaml;

namespace RealtimeTranscription.Desktop;

public partial class MainWindow
{
    private bool exportingLogs;
    private CancellationTokenSource? exportCancellation;
    private void RefreshLogStatus()
    {
        if(LogStatusText==null)return;
        var log=controller.Log;
        string status = log.LastError is {} error
            ? $"日志写入遇到问题：{error}。可尝试导出已保存的日志。"
            : log.IsAvailable ? "运行日志已开启。出现问题后可导出最近的日志。" : "正在准备运行日志…";
        if(log.DroppedCount>0)status+=$" 有 {log.DroppedCount} 条日志未能写入。";
        if(LogStatusText.Text!=status)LogStatusText.Text=status;
        // A stuck microphone driver must not block diagnostic export.
        ExportLogButton.IsEnabled=!exportingLogs&&!pickerOpen&&!Dialogs.IsOpen&&!shuttingDown;
    }

    private async void ExportLog_Click(object sender,RoutedEventArgs args)
        => await Safe(async () =>
        {
            if(exportingLogs)return;
            exportingLogs=true;RefreshLogStatus();
            try
            {
            var file=await PickSaveAsync($"VoiceInput-log-{DateTime.Now:yyyyMMdd-HHmmss}.log",".log");
            if(file==null)return;
            await ExportRuntimeLogAsync(file);
            if(!closed&&!shuttingDown)
                await Dialogs.MessageAsync(this,"运行日志","日志已导出。请将导出的 .log 文件附在问题反馈中，并说明出错的大致时间。\n日志不包含 API Key、录音或识别正文。");
            }
            catch(Exception)when(closed||shuttingDown) { }
            finally { exportingLogs=false;RefreshLogStatus(); }
        });

    internal async Task ExportRuntimeLogAsync(string file)
    {
        controller.Log.Write("Application","LogExportRequested");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        exportCancellation=timeout;
        try
        {
            var export=controller.Log.ExportAsync(file,timeout.Token);
            // A stalled network filesystem can outlive cancellation. Keep the UI
            // wait bounded, and observe eventual cleanup without blocking exit.
            _=export.ContinueWith(t=>{_=t.Exception;},CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            await export.WaitAsync(timeout.Token);
            controller.Log.Write("Application","LogExportSucceeded");
        }
        catch(OperationCanceledException)
        {
            controller.Log.Write("Application","LogExportCancelled");
            throw new InvalidOperationException("运行日志导出已取消或超时。请尝试导出到本机文件夹。程序可以正常退出。");
        }
        catch(Exception error)
        {
            controller.Log.Write("Application","LogExportFailed",exception:error);
            throw new InvalidOperationException("运行日志导出失败。请检查目标文件夹是否可写、磁盘空间是否充足，然后重试。",error);
        }
        finally { if(ReferenceEquals(exportCancellation,timeout))exportCancellation=null;RefreshLogStatus(); }
    }
}
