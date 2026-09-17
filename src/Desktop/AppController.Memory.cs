using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;

public sealed partial class AppController
{
    private volatile bool memoryAvailable;
    private string memoryStatus = "本地记忆正在初始化。";
    private readonly SemaphoreSlim memoryMaintenanceGate = new(1, 1);
    public bool MemoryAvailable => memoryAvailable && Repository.IsAvailable;
    public string MemoryStatus => memoryStatus;
    public string HotkeyLabel => Settings.Hotkey == "RightCtrl" ? "右侧 Ctrl" : Settings.Hotkey;
    private bool CanSaveMemory => MemoryAvailable && Settings.SaveMemory;

    private async Task InitializeMemoryAsync()
    {
        try
        {
            await Repository.InitializeAsync();
            var projects = await Repository.ProjectsAsync();
            if (projects.Count == 0)
            {
                var project = new Project("default", "默认项目");
                await Repository.SaveProjectAsync(project); projects.Add(project);
            }
            await OnActor(() =>
            {
                Projects = projects;
                if (!Projects.Any(p => p.Id == Settings.ProjectId)) Settings = Settings with { ProjectId = Projects[0].Id };
                memoryAvailable = true; memoryStatus = "本地记忆可用。";
            });
            await MaintainMemoryAsync(); await ReloadTerms();
        }
        catch (Exception e) { await DisableMemoryAsync(e); }
    }

    private Task DisableMemoryAsync(Exception error) => OnActor(() =>
    {
        LogEvent("MemoryUnavailable",error);
        memoryAvailable = false;
        memoryStatus = $"本地记忆不可用（{error.GetType().Name}）。听写和复制仍可使用；新内容暂不保存，词库和学习已暂停。修复数据目录后可点击重试保存。";
        terms = []; suppressed = []; approvedCorrections = []; domainProfile = null;
        if (Projects.Count == 0) Projects = [new Project(Settings.ProjectId, "临时听写")];
        CancelToken(extraction); CancelToken(generation); knowledgeEpoch++;
        engine?.SetMemoryState(false);
        TermsUpdated?.Invoke(); CorrectionsUpdated?.Invoke(); Status(memoryStatus);
    });

    // This is also called after a retention setting changes. Repository deletion reconciles
    // provenance and tombstones, so queued writes cannot resurrect expired source records.
    public async Task MaintainMemoryAsync()
    {
        if (!MemoryAvailable || Settings.RetentionDays == null || !await memoryMaintenanceGate.WaitAsync(0)) return;
        try
        {
            await Repository.BarrierAsync();
            await Repository.RetainAsync(Settings.RetentionDays,lifetime.Token,id=>OnActor(()=>
            {
                ForgetPending(id);
                if(engine?.Session.Id==id&&state is not (CaptureState.Connecting or CaptureState.Recording or CaptureState.Draining))engine=null;
                Notify();
            }));
            await ReloadTerms();
            await OnActor(() =>
            {
                if (engine != null && Settings.RetentionDays is int days && engine.Session.CreatedAt < DateTimeOffset.UtcNow.AddDays(-days)
                    && state is not (CaptureState.Connecting or CaptureState.Recording or CaptureState.Draining))
                {
                    ForgetPending(engine.Session.Id); engine = null;
                    Status("超过保留期限的历史及其派生来源已清理。");
                }
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) { await DisableMemoryAsync(e); }
        finally { memoryMaintenanceGate.Release(); }
    }

    private async Task MaintenanceLoop()
    {
        using var clock = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await clock.WaitForNextTickAsync(lifetime.Token))
                await MaintainMemoryAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public Task RefreshMemoryAfterDeliveryAsync()=>RefreshMemoryBeforeRecognitionAsync();

    private async Task RefreshMemoryBeforeRecognitionAsync()
    {
        if (!MemoryAvailable) return;
        try { await Repository.BarrierAsync(); await ReloadTerms(); }
        catch (Exception e) { await DisableMemoryAsync(e); }
    }
}
