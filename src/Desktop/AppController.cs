using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

public sealed partial class AppController : IAsyncDisposable
{
    public readonly MemoryRepository Repository;
    public RuntimeLog Log { get; }
    private readonly bool ownsLog;
    private readonly SettingsStore settingsStore;
    private readonly DeepSeekClient deepseek;
    private readonly Channel<Action> events = Channel.CreateBounded<Action>(4096);
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim captureGate = new(1,1), knowledgeGate = new(1,1), extractionGate = new(1,1);
    private readonly Task eventLoop, timer, maintenance;
    private TranscriptEngine? engine;
    private CaptureState state = CaptureState.Idle;
    private string status = "配置 Key 后即可开始录音。";
    private string? captureFailure, captureWarning;
    internal Task<string?> CaptureWarningAsync(string turnId) => OnActor(() => captureAttempt?.Id == turnId ? captureWarning : null);
    private string diagnostic="尚无录音诊断。";
    private readonly object diagnosticSync=new();
    private sealed class CaptureAttempt(string id,long pressedAt)
    {
        public readonly string Id=id;
        public readonly long PressedAt=pressedAt;
        public long FirstAudioAt,AsrReadyAt,FirstRecognitionAt;
        public int RecognitionEvents,FinalEvents;
        public string Timing
        {
            get
            {
                string Elapsed(long at)=>at==0?"尚未就绪":$"{Math.Max(0,at-PressedAt)} ms";
                return $"按键至首帧录音：{Elapsed(Interlocked.Read(ref FirstAudioAt))}；按键至识别就绪：{Elapsed(Interlocked.Read(ref AsrReadyAt))}";
            }
        }
    }
    private CaptureAttempt? captureAttempt;
    public string Diagnostic {get{lock(diagnosticSync)return diagnostic+(captureAttempt is {} attempt?"\n"+attempt.Timing:"");}}
    private void SetDiagnostic(string stage,Exception? error=null,string? detail=null)
    {
        lock(diagnosticSync)diagnostic=$"VoiceInput {typeof(AppController).Assembly.GetName().Version?.ToString(3)}\n时间：{DateTimeOffset.Now:O}\n阶段：{stage}（{StageLabel(stage)}）\n{detail??audio?.FormatDescription??"音频格式尚未读取"}\n{ExceptionMetadata(error)}";
        LogDiagnostic(stage,error);
    }
    private void FailCapture(string text)
    {
        if(captureFailure!=null)return;
        captureFailure=text;
        if(engine!=null)engine.UpdateSession(engine.Session with{Gaps=[..engine.Session.Gaps,text],Revision=engine.Session.Revision+1});
        Status(text);InputInterrupted?.Invoke(text);
    }
    private BailianClient? asr;
    private IAudioCapture? audio;
    private readonly Func<string,Func<byte[],CancellationToken,ValueTask>,Action<string>,Action<float>,IAudioCapture> captureFactory;
    private readonly Func<Func<AsrEvent,Task>,BailianClient> clientFactory=receive=>new BailianClient(receive);
    private CancellationTokenSource? startup, extraction, generation;
    private readonly object stopSync = new();
    private readonly object audioFeedbackSync = new();
    private Task? stopping;
    private bool requestedPause;
    private volatile bool captureReleased;
    private long lastResponse, lastVoice, lastProgress, lastDraftSave, knowledgeEpoch, lastContextUpdate;
    private bool voiceSeen, polishCircuit;
    private int activePolish;
    private double billedSeconds;
    private readonly Dictionary<string,SegmentData> failedWrites = [];
    private readonly Dictionary<string,TranscriptEngine> failedSources = [];
    private readonly Dictionary<(string Id,bool PermissionOnly),SessionData> pendingSessions = [];
    private readonly HashSet<(string Id,bool PermissionOnly)> failedSessionWrites = [];
    private int failedSaveCount;
    public int FailedSaveCount => Volatile.Read(ref failedSaveCount);
    private readonly SemaphoreSlim settingsGate = new(1,1);
    private List<TermData> terms = [];
    private long termReloadSequence,appliedTermReload;
    private HashSet<string> suppressed = [];
    public AppSettings Settings { get; private set; } = new();
    public Credentials Keys { get; private set; } = new();
    public List<Project> Projects { get; private set; } = [];
    public IReadOnlyList<TermData> Terms => terms.ToArray();
    public string? ActiveDeviceId=>audio?.EndpointId;
    public event Action<TranscriptSnapshot>? Updated;
    public event Action? TermsUpdated;
    public event Action? CorrectionsUpdated;
    public event Action<float>? Level;
    public event Action<string>? Message;
    public event Action<string>? InputInterrupted;
    public event Action<bool>? Extracting;
    public AppController(string folder,IProtector? protector=null,HttpMessageHandler? provider=null,RuntimeLog? log=null)
    {
        Log=log??new RuntimeLog(Path.Combine(folder,"logs"));ownsLog=log==null;
        captureFactory=(device,send,fault,level)=>new AudioCapture(device,send,fault,level,Log,captureAttempt?.Id);
        protector??=new WindowsProtector();deepseek=new(provider);settingsStore = new(folder,protector); Repository = new(Path.Combine(folder,"sessions.db"),protector);
        eventLoop = Task.Run(async()=>{await foreach(var action in events.Reader.ReadAllAsync()){try{action();}catch(Exception e){LogEvent("ActorFailed",e);Message?.Invoke("操作未完成，已保留现有文字。");}}});
        timer = Task.Run(TimerLoop);
        maintenance = Task.Run(MaintenanceLoop);
        deepseek.Used += u => { _ = RecordUsage(u); };
    }
    // Deterministic startup checks use the production controller with an isolated
    // audio source and wire. Normal application construction retains native audio.
    internal AppController(string folder,IProtector? protector,HttpMessageHandler? provider,
        Func<string,Func<byte[],CancellationToken,ValueTask>,Action<string>,Action<float>,IAudioCapture> captureFactory,
        Func<Func<AsrEvent,Task>,BailianClient> clientFactory,RuntimeLog? log=null):this(folder,protector,provider,log)
    {this.captureFactory=captureFactory;this.clientFactory=clientFactory;}
    private async Task RecordUsage(UsageData u){if(!MemoryAvailable)return;try{await Repository.SaveUsageAsync(u);}catch{}}
    private async Task<T> OnActor<T>(Func<T> fn)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await events.Writer.WriteAsync(()=>{try{done.TrySetResult(fn());}catch(Exception e){done.TrySetException(e);}});
        return await done.Task;
    }
    private Task OnActor(Action fn) => OnActor(()=>{fn();return true;});
    public Task<TranscriptSnapshot> SnapshotAsync()=>OnActor(Snapshot);
    private TranscriptSnapshot Snapshot()
    {
        Volatile.Write(ref failedSaveCount,failedWrites.Count+failedSessionWrites.Count);
        return new(engine?.Session,engine?.Segments??[],state,engine?.Pending??0,UnsavedCount(),status)
        {
            LocalAudioReady=captureAttempt is {} attempt&&Interlocked.Read(ref attempt.FirstAudioAt)!=0
                &&!captureReleased&&captureFailure==null&&state is CaptureState.Connecting or CaptureState.Recording,
            CaptureReleased=captureReleased
        };
    }
    private int UnsavedCount() => (engine?.Segments.Count(s=>s.SaveState is SaveState.Pending or SaveState.Failed)??0)
        + failedWrites.Keys.Count(id=>!ReferenceEquals(failedSources[id],engine)) + pendingSessions.Count;
    private void Notify()
    {
        // Save failures are application state even when no window is subscribed.
        var snapshot=Snapshot();Updated?.Invoke(snapshot);
    }
    private void Status(string text){status=text;Notify();}
    private void NewEngine(SessionData session,IEnumerable<SegmentData>? saved=null)
    {
        var next=new TranscriptEngine(session,wholeTurn:true);
        next.Changed+=s=>Persist(next,s);
        engine=next;if(saved!=null)next.Restore(saved);
        knowledgeEpoch++;
    }
    private void Persist(TranscriptEngine source,SegmentData s)
    {
        bool save=CanSaveMemory;
        if(s.AsrState==AsrState.Partial&&Environment.TickCount64-lastDraftSave<1000)return;
        if(s.AsrState==AsrState.Partial)lastDraftSave=Environment.TickCount64;
        source.MarkPending(s.Id,save);
        if(!save)return;
        var snapshot=s with {SaveState=SaveState.Saved};var session=source.Session;
        _=SaveSegment(source,session,snapshot);
    }
    private async Task<bool> SaveSegment(TranscriptEngine source,SessionData session,SegmentData s)
    {
        bool ok=true;
        var options=Settings;
        try{await Repository.SaveSegmentAsync(session,s,options.SaveMemory&&options.AllowLearning&&options.LearnCorrections,options.SaveMemory&&options.AllowLearning&&options.DynamicLexicon);}
        catch(Exception e){ok=false;LogEvent("PersistenceFailed",e,session.Id,("Operation","SaveSegment"),("Sequence",s.SentenceId),("Revision",s.Revision));}
        await OnActor(()=>
        {
            if(IsForgotten(session))return;
            if(source.Segments.Any(current=>current.Id==s.Id&&current.Revision>s.Revision&&current.SaveState==SaveState.Saved))ok=true;
            source.SetSaveState(s.Id,s.Revision,ok);
            if(ok){if(failedWrites.TryGetValue(s.Id,out var old)&&old.Revision<=s.Revision){failedWrites.Remove(s.Id);failedSources.Remove(s.Id);}}
            else if(!failedWrites.TryGetValue(s.Id,out var newer)||newer.Revision<=s.Revision){failedWrites[s.Id]=s;failedSources[s.Id]=source;}
            if(ReferenceEquals(source,engine)&&!ok)status="保存失败：正文仍可复制和导出，请重试保存。";
            // A previous turn can fail after the user switches project or starts
            // another turn. Always refresh the global warning and exit guard.
            Notify();
            if(Repository.Queued>=900||failedWrites.Count>=1000||failedWrites.Values.Sum(x=>Encoding.UTF8.GetByteCount(x.RawText+x.FinalText))>=4*1024*1024)_=StopAsync(true);
        });
        if(ok&&(s.EditRevision>0||s.AsrState==AsrState.Confirmed))await ReloadTerms();
        return ok;
    }
    public async Task InitializeAsync()
    {
        LogEvent("InitializeStarted");
        try
        {
        var loaded=await Task.Run(settingsStore.LoadForStartup);
        Settings=loaded.Settings;Keys=loaded.Credentials;StartupWarning=loaded.Warning;
        if(loaded.Warning!=null)LogEvent("ConfigurationRecovery",fields:[("Reason","UnreadableConfiguration")]);
        await InitializeMemoryAsync();
        await OnActor(()=>
        {
            string readiness=Keys.BailianKey.Length==0?"请先在设置中填写百炼 Key。":!MemoryAvailable?MemoryStatus:$"准备就绪。按住 {HotkeyLabel} 说话，松开后{(Settings.DictationOnly?"自动复制结果":"输入")}。";
            Status(StartupWarning is { } warning ? warning + (!MemoryAvailable ? "\n" + MemoryStatus : "") : readiness);
        });
        LogEvent("InitializeCompleted",fields:[("MemoryAvailable",MemoryAvailable),("ConfigurationRecovered",loaded.Warning!=null)]);
        }
        catch(Exception e){LogEvent("InitializeFailed",e);throw;}
    }
    private async Task ReloadTerms()
    {
        if(!MemoryAvailable)return;
        long request=Interlocked.Increment(ref termReloadSequence);
        try
        {
            string project=Settings.ProjectId;var t=await Repository.TermsAsync(project);var s=await Repository.SuppressedAsync(project);var mappings=await Repository.ActiveCorrectionTermsAsync(project);
            await OnActor(()=>{if(!MemoryAvailable||Settings.ProjectId!=project||request<appliedTermReload)return;appliedTermReload=request;terms=t;suppressed=s;approvedCorrections=mappings;TermsUpdated?.Invoke();CorrectionsUpdated?.Invoke();});
        }
        catch(Exception e){await DisableMemoryAsync(e);}
    }
    public async Task SaveSettingsAsync(AppSettings options,Credentials keys)
    {
        await settingsGate.WaitAsync();
        try
        {
        options.Validate();
        if(keys.BailianKey.Any(char.IsControl)||keys.DeepSeekKey.Any(char.IsControl))throw new ArgumentException("Key 不能包含换行或控制字符。");
        var current=await SnapshotAsync();if(current.State is CaptureState.Recording or CaptureState.Connecting or CaptureState.Draining)throw new InvalidOperationException("请先暂停录音，再修改设置。");
        await Task.Run(()=>settingsStore.Save(options,keys));
        await OnActor(()=>{Settings=options;Keys=keys;StartupWarning=null;polishCircuit=false;knowledgeEpoch++;CancelToken(extraction);engine?.SetMemoryState(CanSaveMemory);Status("设置已保存。");});
        await MaintainMemoryAsync();await ReloadTerms();
        }
        catch(Exception e){LogEvent("SettingsSaveFailed",e);throw;}
        finally{settingsGate.Release();}
    }
    public async Task ChangeProjectAsync(string id)
    {
        await StopAsync(false);await OnActor(()=>{engine?.FinishAll();engine=null;Settings=Settings with{ProjectId=id};knowledgeEpoch++;state=CaptureState.Idle;Status("已切换项目。");});
        try{await Task.Run(()=>settingsStore.Save(Settings,Keys));}
        catch(Exception e){LogEvent("SettingsSaveFailed",e);throw;}
        await ReloadTerms();
    }
    public async Task CreateProjectAsync(string name)
    {
        if(string.IsNullOrWhiteSpace(name)||JsonCodec.Count(name)>64)throw new ArgumentException("项目名称应为 1—64 个字符。");
        var p=new Project(JsonCodec.Id(),name.Trim());await Repository.SaveProjectAsync(p);Projects=await Repository.ProjectsAsync();await ChangeProjectAsync(p.Id);
    }
    public async Task<bool> StartAsync(long pressedAt, CancellationToken cancelled, Task<bool> targetReady, string turnId)
    {
        captureReleased=false;
        await captureGate.WaitAsync(cancelled);
        Task<IReadOnlyList<TermData>>? preparation=null;
        string startStage="Configuration";
        void Stage(string value){startStage=value;SetDiagnostic(value);}
        try
        {
            var allowed=await OnActor(()=>state is not (CaptureState.Recording or CaptureState.Connecting or CaptureState.Draining or CaptureState.Closing));
            if(!allowed){LogEvent("StartRejected",turnId:turnId,fields:[("Reason","CaptureBusy")]);return false;}
            var attempt=new CaptureAttempt(turnId,pressedAt);
            await OnActor(()=>{captureAttempt=attempt;captureFailure=null;captureWarning=null;});
            Stage("Configuration");
            Settings.AsrUri();if(Keys.BailianKey.Length==0)throw new ArgumentException("请先在设置页填写百炼 API Key。");
            startup=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancelled);startup.CancelAfter(15000);
            var startupToken=startup.Token;
            // Start local capture before database work; pending observations are reconciled
            // while audio is safely buffered, before the request vocabulary is selected.
            Stage("PreparingSession");
            await OnActor(()=>engine?.FinishAll());
            startup.Token.ThrowIfCancellationRequested();
            await OnActor(()=>
            {
                CancelToken(extraction);NewEngine(new SessionData{Id=turnId,ProjectId=Settings.ProjectId,AllowLearning=Settings.AllowLearning});
                state=CaptureState.Connecting;
                lastResponse=lastVoice=lastProgress=Environment.TickCount64;voiceSeen=false;billedSeconds=0;
                Status("正在准备麦克风…");
            });
            Stage("CreatingRecognitionClient");
            var client=clientFactory(ReceiveAsync);asr=client;
            // Local capture precedes database refresh and the cloud handshake. Startup PCM
            // is bounded to the same fifteen seconds as the overall startup deadline.
            Stage("OpeningMicrophone");
            var capture=await Task.Run(()=>captureFactory(Settings.DeviceId,client.AudioAsync,text=>AudioFault(client.TaskId,text),value=>AudioLevel(attempt,client.TaskId,value)),startupToken);audio=capture;
            // A release can arrive while the device is being constructed and audio
            // is still null. Forward it before Start so no post-release PCM is read.
            Stage("StartingMicrophone");
            if(captureReleased)capture.RequestStop();
            startupToken.ThrowIfCancellationRequested();capture.Start();Stage("PreparingRecognition");
            await OnActor(()=>{if(captureFailure==null)Status(captureReleased?"录音已停止，正在准备识别…":"麦克风已启动，正在连接识别服务；音频暂存本地。");});
            // Vocabulary reconciliation starts immediately, but need not delay
            // the transport once the hold and target checks permit a cloud turn.
            preparation=PrepareRecognitionAsync(client,turnId,startupToken);
            Stage("ValidatingTarget");
            int delay=(int)Math.Max(0,Settings.HoldMs-(Environment.TickCount64-pressedAt));
            if(delay>0)await Task.Delay(delay,startup.Token);
            startup.Token.ThrowIfCancellationRequested();
            if(!await targetReady.WaitAsync(startup.Token))throw new InvalidOperationException("无法确认可编辑的输入位置，未上传语音。");
            Stage("ConnectingRecognition");
            await client.StartPreparedAsync(Settings,Keys.BailianKey,preparation,startupToken);
            Interlocked.Exchange(ref attempt.AsrReadyAt,Environment.TickCount64);
            LogEvent("RecognitionReady",turnId:attempt.Id,fields:[("ElapsedMs",Math.Max(0,attempt.AsrReadyAt-attempt.PressedAt))]);
            SetDiagnostic("Recognizing");
            await OnActor(()=>engine!.UpdateSession(engine.Session with{HotwordState=!Settings.UseLexicon?"Disabled":MemoryAvailable?"Sent":"Unavailable",Revision=engine.Session.Revision+1}));
            await OnActor(()=>{state=captureReleased?CaptureState.Draining:CaptureState.Recording;Status(captureReleased?"录音已停止，正在收齐尾句…":Settings.DictationOnly?"正在听 · 松开后自动复制，Esc 取消。":"正在听 · 松开快捷键后输入，Esc 取消。");});
            return true;
        }
        catch(Exception e)
        {
            // The preparation runs alongside the transport. Attribute its own
            // exception to preparation, without mislabelling a transport failure.
            if(preparation?.Exception is {} preparationError&&ExceptionChain(preparationError).Any(x=>ReferenceEquals(x,e)))
                startStage="PreparingRecognition";
            string? priorCaptureFailure=e is OperationCanceledException?await OnActor(()=>captureFailure):null;
            if(priorCaptureFailure!=null)startStage="ReadingMicrophone";
            LogEvent("StartFailed",e,turnId,("FailureStage",startStage),("State",state.ToString()),("Cancelled",e is OperationCanceledException));
            string error=priorCaptureFailure??(IsMicrophoneStage(startStage)?MicrophoneError(e,startStage):asr?.FailureMessage??SafeError(e));
            string audioDetail=audio?.Diagnostic.Length>0?audio.Diagnostic:audio?.FormatDescription??"音频格式尚未读取";
            CancelToken(startup);
            string cleanup=await CleanupFailedStartAsync();
            SetDiagnostic("StartFailed",e,$"失败阶段：{startStage}（{StageLabel(startStage)}）\n{audioDetail}\n错误：{error}{cleanup}");
            await OnActor(()=>
            {
                if(IsMicrophoneStage(startStage)&&e is not OperationCanceledException)captureFailure=error;
                if(engine?.Session.HotwordState=="Prepared")engine.UpdateSession(engine.Session with{HotwordState="Failed",Revision=engine.Session.Revision+1});
                state=e is OperationCanceledException&&captureFailure==null?CaptureState.Stopped:CaptureState.Faulted;Status(captureFailure??error);
            });
            return false;
        }
        finally
        {
            // Observe preparation even if hold/target checks failed first. Its
            // actor callback rechecks cancellation before touching the session.
            if(preparation is {IsCompleted:false})CancelToken(startup);
            if(preparation!=null)try{await preparation;}catch{}
            if(audio==null)Level?.Invoke(0);startup?.Dispose();startup=null;captureGate.Release();
        }
    }
    private async Task<IReadOnlyList<TermData>> PrepareRecognitionAsync(BailianClient client,string turnId,CancellationToken token)
    {
        await RefreshMemoryBeforeRecognitionAsync().WaitAsync(token);
        return await OnActor(()=>
        {
            token.ThrowIfCancellationRequested();
            if(!ReferenceEquals(asr,client)||engine?.Session.Id!=turnId)throw new OperationCanceledException(token);
            var chosen=NextHotwords();
            engine.StartTask(client.TaskId,chosen.Select(t=>t.Text).ToArray());
            engine.UpdateSession(engine.Session with{Hotwords=chosen.Select(t=>new HotwordUsage(t.Text,t.Weight)).ToList(),EligibleHotwordCount=EligibleHotwords(),HotwordState=!Settings.UseLexicon?"Disabled":MemoryAvailable?"Prepared":"Unavailable",Revision=engine.Session.Revision+1});
            return (IReadOnlyList<TermData>)chosen;
        }).WaitAsync(token);
    }
    public void RequestStopCapture()
    {
        lock(audioFeedbackSync)
        {
            if(!captureReleased)LogEvent("CaptureStopRequested",fields:[("SamplesSent",audio?.SamplesSent??0)]);
            captureReleased=true;Level?.Invoke(0);
        }
        try{audio?.RequestStop();}catch(Exception e){LogEvent("CaptureStopRequestFailed",e);}
        _=OnActor(()=>{if(state is CaptureState.Connecting or CaptureState.Recording)Notify();});
    }
    public async Task SetDeliveryAsync(string state,string reason,int accepted=0,string? turnId=null)
    {
        var session=await OnActor(()=>
        {
            if(engine==null||(turnId!=null&&engine.Session.Id!=turnId))return null;
            var current=engine.Session with{DeliveryState=state,DeliveryReason=reason,AcceptedInputEvents=accepted,Revision=engine.Session.Revision+1};
            LogEvent("DeliveryChanged",turnId:turnId,fields:[("State",LogDeliveryState(state)),("AcceptedInputEvents",accepted)]);
            engine.UpdateSession(current);Status(reason);return current;
        });
        if(session!=null&&CanSaveMemory)await SaveSessionSafe(session);
    }
    private void AudioFault(string taskId,string text)=>_=OnActor(()=>
    {
        if(asr?.TaskId!=taskId)return;
        SetDiagnostic("AudioFailed",detail:audio?.Diagnostic);FailCapture(text);CancelToken(startup);_=StopAsync(true);
    });
    private void AudioLevel(CaptureAttempt attempt,string taskId,float value)
    {
        lock(audioFeedbackSync)
        {
        if(!ReferenceEquals(captureAttempt,attempt)||asr?.TaskId!=taskId||captureReleased||captureFailure!=null)return;
        if(Interlocked.CompareExchange(ref attempt.FirstAudioAt,Environment.TickCount64,0)==0)
        {
            LogEvent("FirstAudioFrame",turnId:attempt.Id,fields:[("ElapsedMs",Math.Max(0,attempt.FirstAudioAt-attempt.PressedAt))]);
            _=OnActor(()=>
            {
                if(!ReferenceEquals(captureAttempt,attempt)||asr?.TaskId!=taskId||captureReleased||captureFailure!=null
                    ||state is not (CaptureState.Connecting or CaptureState.Recording))return;
                SetDiagnostic("MicrophoneReady");Notify();
            });
        }
        if(value>.006f){Interlocked.Exchange(ref lastVoice,Environment.TickCount64);voiceSeen=true;}
        Level?.Invoke(Math.Min(1,value*5));
        }
    }
    private Task ReceiveAsync(AsrEvent e)=>OnActor(()=>
    {
        if(asr?.TaskId!=e.TaskId||engine==null)return;
        lastResponse=Environment.TickCount64;
        if(captureAttempt is {} attempt)
        {
            attempt.RecognitionEvents++;
            if(attempt.FirstRecognitionAt==0)
            {
                attempt.FirstRecognitionAt=lastResponse;
                LogEvent("RecognitionFirstEvent",turnId:attempt.Id,fields:[("ElapsedMs",Math.Max(0,lastResponse-attempt.PressedAt))]);
            }
        }
        if(e.Duration.HasValue)billedSeconds=Math.Max(billedSeconds,e.Duration.Value);
        if(e.Event=="connection-failed"){LogEvent("RecognitionFailed",fields:[("Reason","ConnectionFailed")]);SetDiagnostic("AsrFailed",detail:e.Error);FailCapture(e.Error);_=StopAsync(true);return;}
        if(e.Event=="task-finished"){engine.SealTask(e.TaskId);Notify();return;}
        if(e.Event!="result-generated"||e.Heartbeat)return;
        var previous=engine.Segments.FirstOrDefault(s=>s.TaskId==e.TaskId&&s.SentenceId==e.SentenceId);
        if(e.Final&&previous?.AsrState!=AsrState.Confirmed)
        {
            if(captureAttempt is {} currentAttempt)currentAttempt.FinalEvents++;
            LogEvent("RecognitionFinal",fields:[("Sequence",e.SentenceId),("CharacterCount",e.Text.Length)]);
        }
        if(previous?.AsrState!=AsrState.Confirmed&&(e.Final||e.Text.Length>0&&(previous==null||e.Text!=previous.PartialText)))lastProgress=Environment.TickCount64;
        engine.Receive(e,false,Settings.AutoParagraph,[],false);
        if(engine.Segments.Count>=50000||engine.Segments.Sum(s=>(long)(s.RawText.Length+s.FinalText.Length+s.PartialText.Length)*2)>64*1024*1024){Status("本次会话达到容量上限，停止后可导出并开启新会话。");_=StopAsync(false);}
        Notify();
    });
    public Task StopAsync(bool pause,bool emergency=false)
    {
        lock(stopSync)
        {
            RequestStopCapture();
            if(emergency)CancelToken(startup);
            if(stopping is {IsCompleted:false}){requestedPause&=pause;return stopping;}
            if(asr!=null||audio!=null)LogEvent("StopRequested",fields:[("Pause",pause),("Emergency",emergency),("SamplesSent",audio?.SamplesSent??0)]);
            requestedPause=pause;return stopping=StopInternal(emergency);
        }
    }
    private async Task StopInternal(bool emergency)
    {
        using var budget=new CancellationTokenSource(emergency?1000:15000);bool gate=false;string gap="";
        long stoppingAt=Environment.TickCount64,samples=0;
        bool completed=false;
        try
        {
            await captureGate.WaitAsync(budget.Token);gate=true;
            if(asr==null){await OnActor(()=>{if(state!=CaptureState.Closing)state=requestedPause?CaptureState.Paused:CaptureState.Stopped;Notify();});return;}
            var client=asr;await OnActor(()=>{state=CaptureState.Draining;Status(captureFailure??"正在收齐尾部语音…");});
            if(emergency){audio?.Abort();client.Abort();gap="系统挂起，尾部可能不完整";}
            else
            {
                if(audio!=null)
                {
                    await audio.StopAsync(budget.Token);
                    string? warning=audio.QualityWarning;
                    await OnActor(()=>captureWarning=warning);
                    if(audio.FailureMessage is {} failure){gap=failure;await OnActor(()=>FailCapture(failure));}
                }
                await client.FinishAsync(budget.Token);
            }
        }
        catch(Exception e){LogEvent("StopFailed",e);gap="连接或收尾超时，尾部可能不完整";CancelToken(startup);audio?.Abort();asr?.Abort();}
        finally
        {
            try
            {
            samples=audio?.SamplesSent??0;
            string? task=asr?.TaskId;
            if(!gate){await OnActor(()=>{state=CaptureState.Faulted;Status("收尾超时，自动输入已取消。");});}
            else
            {
            if(audio!=null){await audio.DisposeAsync();audio=null;}
            if(asr!=null){await asr.DisposeAsync();asr=null;}
            await OnActor(()=>
            {
                if(task!=null)engine?.SealTask(task,gap);
                if(task!=null&&engine!=null){engine.UpdateSession(engine.Session with{EndedAt=DateTimeOffset.UtcNow,Revision=engine.Session.Revision+1});if(CanSaveMemory)_=SaveSessionSafe(engine.Session);}
                state=requestedPause?CaptureState.Paused:CaptureState.Stopped;
                Status(captureFailure??(gap.Length>0?gap:engine?.Pending>0?"录音已结束，正在完成剩余润色…":requestedPause?"已停止采集，确认文字已保留。":"本次录音已完成。"));
            });
            }
            if(billedSeconds>0){_=RecordUsage(new("asr",0,0,false,DateTimeOffset.UtcNow,billedSeconds));billedSeconds=0;}
            completed=gate;
            }
            catch(Exception e){LogEvent("StopCleanupFailed",e);throw;}
            finally
            {
                LogEvent("StopCompleted",fields:[("Completed",completed),("Incomplete",gap.Length>0),("ElapsedMs",Math.Max(0,Environment.TickCount64-stoppingAt)),
                    ("SamplesSent",samples),("RecognitionEvents",captureAttempt?.RecognitionEvents??0),("FinalEvents",captureAttempt?.FinalEvents??0),("State",state.ToString())]);
                if(gate)captureGate.Release();Level?.Invoke(0);
            }
        }
    }
    public void Suspend(){CancelToken(startup);audio?.Abort();asr?.Abort();CancelToken(extraction);_=StopAsync(true,true);}
    private async Task TimerLoop()
    {
        using var clock=new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try{while(await clock.WaitForNextTickAsync(lifetime.Token))await OnActor(()=>
        {
            if(engine==null)return;int before=engine.Pending;bool gap=engine.Tick().Length>0;
            bool finalWait=RecognitionTimeout.Stalled(Environment.TickCount64,lastResponse,lastProgress,Interlocked.Read(ref lastVoice),voiceSeen,engine.Segments.Any(s=>s.AsrState==AsrState.Partial),Settings.SilenceMs);
            if(state==CaptureState.Recording&&(gap||finalWait||Environment.TickCount64-lastResponse>20000)){LogEvent("RecognitionTimeout",fields:[("Reason",gap?"SequenceGap":finalWait?"FinalResultStalled":"ResponseTimeout")]);InputInterrupted?.Invoke("识别等待超时，自动输入已取消，确认文字可复制。");Status("识别响应等待超时，正在保留已有结果并暂停。");_=StopAsync(true);}
            if(before!=engine.Pending)Notify();
        });}catch(OperationCanceledException){}
    }
    public async Task FinishCurrentAsync(string? turnId=null,bool allowPolish=true,CancellationToken token=default,bool forDelivery=false,CancellationToken expedite=default)
    {
        long processingAt=Environment.TickCount64;
        var prepared=await OnActor(()=>
        {
            if(engine==null||(turnId!=null&&engine.Session.Id!=turnId)||state is CaptureState.Connecting or CaptureState.Recording or CaptureState.Draining)return (Source:(TranscriptEngine?)null,Work:(WholePolishWork?)null,Key:"",Prompt:"");
            engine.FinishAll();
            if(allowPolish&&!token.IsCancellationRequested&&Settings.UseLexicon)engine.ApplyConfirmedCorrections(approvedCorrections);
            var protectedTerms=Settings.UseLexicon?Lexicon.Matches(TranscriptText.Render(engine.Segments),terms,engine.Session.ProjectId):[];
            engine.UpdateSession(engine.Session with{ProtectedTermCount=protectedTerms.Length,Revision=engine.Session.Revision+1});
            var work=engine.Segments.Count==0?null:engine.BeginWholePolish(allowPolish&&!token.IsCancellationRequested&&!expedite.IsCancellationRequested&&Settings.PolishEnabled&&!polishCircuit&&Keys.DeepSeekKey.Length>0,protectedTerms);
            if(work!=null){Interlocked.Increment(ref activePolish);Status("正在统一润色本次完整输入…");}else Notify();
            return (Source:(TranscriptEngine?)engine,Work:work,Key:Keys.DeepSeekKey,Prompt:Settings.EffectivePolishPrompt);
        });
        if(prepared.Source==null)return;
        LogEvent("TextProcessingStarted",turnId:turnId,fields:[("PolishRequested",prepared.Work!=null)]);
        if(prepared.Work is {} work)
        {
            string? result=null,error=null;
            using var cancelled=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,token,expedite);
            if(forDelivery&&Settings.PreferFastDelivery)cancelled.CancelAfter(TimeSpan.FromSeconds(3));
            try{result=await deepseek.PolishWholeAsync(work,prepared.Key,prepared.Prompt,cancelled.Token);}
            catch(Exception e){LogEvent("TextProcessingFailed",e,turnId,("Cancelled",cancelled.IsCancellationRequested));error=cancelled.IsCancellationRequested&&!token.IsCancellationRequested&&!lifetime.IsCancellationRequested?"已优先上屏，保留完整识别正文及已确认纠错。":SafeError(e);}
            finally
            {
                await OnActor(()=>{prepared.Source.CompleteWholePolish(work,cancelled.IsCancellationRequested?null:result,error);if(ReferenceEquals(engine,prepared.Source))Status(prepared.Source.Session.WholePolishReason);});
                Interlocked.Decrement(ref activePolish);
            }
        }
        var saved=await OnActor(()=>prepared.Source.Session);
        LogEvent("TextProcessingCompleted",turnId:turnId,fields:[("State",saved.WholePolishState is "None" or "Waiting" or "Completed" or "Fallback"?saved.WholePolishState:"Other"),
            ("ElapsedMs",Math.Max(0,Environment.TickCount64-processingAt))]);
        if(CanSaveMemory)await SaveSessionSafe(saved);
        // Next StartAsync awaits the repository barrier and refreshes memory.
        // Do not put a redundant vocabulary reload ahead of automatic delivery.
        if(!forDelivery)await RefreshMemoryBeforeRecognitionAsync();
        if(MemoryAvailable&&Settings.AutoExtract&&!token.IsCancellationRequested&&allowPolish)_=AutoExtractWhenReady();
    }
    public Task ParagraphAsync()=>OnActor(()=>{engine?.Paragraph();Status("将在下一个识别片段开始时换段。");});
    public Task EditAsync(string id,string text,string action="编辑")=>OnActor(()=>
    {
        if(engine==null)return;engine.Edit(id,text,action,CanSaveMemory&&Settings.AllowLearning&&Settings.LearnCorrections&&engine.Session.AllowLearning);knowledgeEpoch++;CancelToken(extraction);Notify();
    });
    public Task UndoAsync(string id)=>EditAsync(id,"","撤销");
    public async Task<string> StopAndTextAsync(){await StopAsync(false);await FinishCurrentAsync();return TranscriptText.Render(await SnapshotAsync());}
    public async Task LoadSessionAsync(SessionData session,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var previous=await OnActor(()=>(Engine:engine,Project:Settings.ProjectId)).WaitAsync(token);
        // Stop owns captureGate internally. Only acquire it after the stop completes,
        // then keep it through the read and installation so StartAsync cannot replace
        // the engine while this operation is suspended on database I/O.
        await StopAsync(false).WaitAsync(token);
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        var loadToken=budget.Token;
        await captureGate.WaitAsync(loadToken);
        try
        {
            void VerifyTarget()
            {
                // Check inside the actor as well: cancellation of an awaiting caller
                // does not remove an action that is already in the actor queue.
                loadToken.ThrowIfCancellationRequested();
                if(!ReferenceEquals(engine,previous.Engine)||Settings.ProjectId!=previous.Project||state is CaptureState.Connecting or CaptureState.Recording or CaptureState.Draining or CaptureState.Closing)
                    throw new InvalidOperationException("当前会话已变化，未载入历史。请结束本轮输入后重试。");
            }
            await OnActor(()=>{VerifyTarget();CancelToken(extraction);}).WaitAsync(loadToken);
            await Repository.BarrierAsync().WaitAsync(loadToken);
            if(await OnActor(()=>HasPending(session.Id)).WaitAsync(loadToken))
            {
                await RetrySaveAsync().WaitAsync(loadToken);
                if(await OnActor(()=>HasPending(session.Id)).WaitAsync(loadToken))throw new InvalidOperationException("该会话仍有内容未保存，已保留现有文字。请先重试保存，再重新载入历史。");
            }
            var saved=await Repository.LoadSessionAsync(session.Id).WaitAsync(loadToken)??throw new InvalidOperationException("该历史记录已删除，请刷新列表。");
            await OnActor(()=>
            {
                VerifyTarget();
                var loaded=saved.Session;
                engine?.FinishAll();NewEngine(loaded.DeliveryState=="Sending"?loaded with{DeliveryState="Unknown",DeliveryReason="上次输入结果未知，不自动重发。"}:loaded,saved.Segments);
                Settings=Settings with{ProjectId=loaded.ProjectId};state=CaptureState.Paused;Status("已载入历史供查看与编辑；历史文字不会自动输入。");
            }).WaitAsync(loadToken);
            await ReloadTerms().WaitAsync(loadToken);
        }
        finally{captureGate.Release();}
    }
    public async Task DeleteSessionAsync(SessionData session)
    {
        await knowledgeGate.WaitAsync();
        try
        {
            if((await SnapshotAsync()).Session?.Id==session.Id)await StopAsync(false);
            await OnActor(()=>{knowledgeEpoch++;CancelToken(extraction);if(engine?.Session.Id==session.Id){engine.FinishAll();engine=null;Notify();}});
            await Repository.DeleteSessionAsync(session.Id);await ReloadTerms();await OnActor(()=>{ForgetPending(session.Id);Status("会话及其派生来源已清除。");});
        }
        finally{knowledgeGate.Release();}
    }
    public async Task DeleteCurrentProjectAsync()
    {
        if(Projects.Count<=1)throw new InvalidOperationException("请至少保留一个项目，可先创建其他项目。");
        await StopAsync(false);CancelToken(extraction);string id=Settings.ProjectId;
        await OnActor(()=>{engine=null;knowledgeEpoch++;Notify();});await Repository.DeleteProjectAsync(id);
        await OnActor(()=>ForgetProjectPending(id));Projects=await Repository.ProjectsAsync();await ChangeProjectAsync(Projects[0].Id);
    }
    public async Task SaveTermAsync(TermData term)
    {
        term=term with{Text=term.Text.Trim()};term.Validate();await knowledgeGate.WaitAsync();
        try
        {
            await OnActor(()=>{if(term.Scope!="*"&&term.Scope!=Settings.ProjectId)throw new ArgumentException("词条范围已变化，请重新选择。");knowledgeEpoch++;});
            await Repository.SaveUserTermAsync(term);await ReloadTerms();await OnActor(()=>Status("词条已保存，下次识别任务使用新词库。"));
            if(Settings.UseLexicon&&Settings.AsrContext&&Environment.TickCount64-lastContextUpdate>=5000&&asr!=null&&(await SnapshotAsync()).State==CaptureState.Recording){lastContextUpdate=Environment.TickCount64;try{await asr.UpdateContextAsync(Lexicon.Context(NextHotwords()),lifetime.Token);}catch{}}
        }
        finally{knowledgeGate.Release();}
    }
    public async Task<int> ImportTermsAsync(IReadOnlyList<TermData> input,bool ignoreCase=false)
    {
        await knowledgeGate.WaitAsync();
        try
        {
            await OnActor(()=>
            {
                foreach(var t in input){t.Validate();if(t.Scope!=Settings.ProjectId&&t.Scope!="*")throw new ArgumentException("导入范围无效。");}
                knowledgeEpoch++;CancelToken(extraction);
            });
            int count=await Repository.ImportUserTermsAsync(input);await ReloadTerms();return count;
        }
        finally{knowledgeGate.Release();}
    }
    public async Task DeleteTermAsync(TermData term)
    {
        await knowledgeGate.WaitAsync();try{await OnActor(()=>{knowledgeEpoch++;CancelToken(extraction);});await Repository.DeleteTermAsync(term);await ReloadTerms();}finally{knowledgeGate.Release();}
    }
    public async Task RememberAsync(string correct,string original)
    {
        var term=new TermData{Text=correct,Alias=original,Scope=Settings.ProjectId,Origin="UserCorrection"};await SaveTermAsync(term);
    }
    private async Task AutoExtractWhenReady()
    {
        try{for(int i=0;i<120;i++){var snap=await SnapshotAsync();if(snap.State!=CaptureState.Stopped)return;if(snap.Pending==0&&Volatile.Read(ref activePolish)==0){await Repository.BarrierAsync();await ExtractAsync(true);return;}await Task.Delay(100);}}catch{Message?.Invoke("自动词条整理未完成，可稍后手动整理。");}
    }
    public async Task ExtractAsync(bool automatic=false)
    {
        var snapshot=await SnapshotAsync();
        if(snapshot.Session==null)throw new InvalidOperationException("请先完成或载入一个会话。");
        await RunExtractionAsync(snapshot.Session.Id,null,CancellationToken.None);
    }
    private static void CancelToken(CancellationTokenSource? source){try{source?.Cancel();}catch(ObjectDisposedException){}}
    public void CancelExtraction()=>CancelToken(extraction);
    public void CancelGeneration()=>CancelToken(generation);
    public async Task<GeneratedLexicon> GenerateLexiconAsync(TermGenerationOptions options,string key,IProgress<TermGenerationProgress>? progress,CancellationToken token)
    {
        options.Validate();
        if(string.IsNullOrWhiteSpace(key))throw new ArgumentException("请在上方填写 DeepSeek API Key。");
        if(key.Any(char.IsControl))throw new ArgumentException("DeepSeek Key 不能包含换行或控制字符。");
        if(options.Scope!="*"&&options.Scope!=Settings.ProjectId)throw new ArgumentException("词库生成范围已变化，请重新选择。");
        if(!await extractionGate.WaitAsync(0,token))throw new InvalidOperationException("另一个词条整理或生成任务正在进行。");
        generation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,token);generation.CancelAfter(TimeSpan.FromMinutes(options.MaxThinking?45:6));
        try
        {
            var generator=new LexiconGenerator(async(batch,cancelled)=>
            {
                int reserve=DeepSeekClient.Request(TermGenerationRules.SystemPrompt,batch.Input,true,batch.MaxTokens,batch.MaxThinking).Length+batch.MaxTokens;
                var at=DateTimeOffset.UtcNow;
                await Repository.ReserveTermBudgetAsync(reserve,Settings.DailyExtractionTokens,at,cancelled);
                UsageData? actual=null;
                try{return await deepseek.CallAsync(TermGenerationRules.SystemPrompt,batch.Input,true,batch.MaxTokens,"term_generation",key.Trim(),batch.TimeoutMs,cancelled,u=>actual=u,batch.MaxThinking);}
                finally{if(actual is{Unknown:false})await Repository.SaveUsageAsync(new("term_budget",actual.InputTokens+actual.OutputTokens-reserve,0,false,at));}
            });
            return await generator.GenerateAsync(options,progress,generation.Token);
        }
        finally{generation.Dispose();generation=null;extractionGate.Release();}
    }
    public Task SavePolishPromptAsync(string prompt)=>SaveSettingsAsync(Settings with{PolishPrompt=PolishRules.ResolvePrompt(prompt)},Keys);
    public async Task<ValidationResult> PreviewPolishAsync(string prompt,string text,string key,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(text)||JsonCodec.Count(text)>600)throw new ArgumentException("试用文本应为 1—600 字。");
        string system=PolishRules.ResolvePrompt(prompt);
        var protectedTerms=Settings.UseLexicon?Lexicon.Matches(text,terms,Settings.ProjectId):[];
        string result=await deepseek.CallAsync(system,new{current_text=text,protected_terms=protectedTerms},false,1536,"polish_preview",key,12000,token);
        return PolishRules.Validate(text,result,protectedTerms);
    }
    public async Task TestDeepSeekAsync(){await deepseek.CallAsync(Settings.EffectivePolishPrompt,new{current_text="嗯，这个方案我们先试一下。",protected_terms=Array.Empty<string>()},false,128,"polish",Keys.DeepSeekKey,8000,lifetime.Token);}
    public async Task<string> TestMicrophoneAsync(string deviceId)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await captureGate.WaitAsync(timeout.Token);
        string testStage="Configuration",result="";
        IAudioCapture? mic=null;
        bool hadFailure=false;
        void Stage(string value){testStage=value;SetDiagnostic(value);}
        try
        {
            var current=await SnapshotAsync();
            if(current.State is CaptureState.Recording or CaptureState.Connecting or CaptureState.Draining)throw new InvalidOperationException("请先结束当前录音。");
            float peak=0;
            Stage("OpeningMicrophone");
            mic=await Task.Run(()=>captureFactory(deviceId,(_,_)=>ValueTask.CompletedTask,_=>{},v=>{peak=Math.Max(peak,v);Level?.Invoke(Math.Min(1,v*5));}),timeout.Token);
            Stage("StartingMicrophone");mic.Start();
            Stage("ReadingMicrophone");await Task.Delay(3000,timeout.Token);
            Stage("StoppingMicrophone");await mic.StopAsync(timeout.Token);
            if(mic.FailureMessage is {} fault)
            {
                hadFailure=true;
                SetDiagnostic("MicrophoneTestFailed",detail:$"失败阶段：ReadingMicrophone（读取麦克风音频）\n{mic.Diagnostic}");
                result=fault+" 可在设置中查看诊断信息。";
            }
            else
            {
                SetDiagnostic("MicrophoneTest",detail:$"{mic.FormatDescription}; pcm_samples={mic.SamplesSent}; peak={peak:F4}");
                result=mic.SamplesSent==0?"没有收到麦克风音频。请检查设备、Windows 麦克风权限与静音开关。":
                    $"本地麦克风测试通过：{mic.FormatDescription}，已转换 {mic.SamplesSent/16000.0:F1} 秒。"+(peak<.006f?"电平很低，请检查静音或输入音量。":"");
            }
        }
        catch(Exception e)
        {
            hadFailure=true;
            SetDiagnostic("MicrophoneTestFailed",e,$"失败阶段：{testStage}（{StageLabel(testStage)}）\n{mic?.Diagnostic??"音频格式尚未读取"}");
            if(!IsMicrophoneStage(testStage))throw;
            result=MicrophoneError(e,testStage);
        }
        finally
        {
            try
            {
                if(mic!=null)try{await mic.DisposeAsync();}catch(Exception e)
                {
                    if(!hadFailure)
                    {
                        SetDiagnostic("MicrophoneTestFailed",e,"失败阶段：StoppingMicrophone（释放麦克风设备）");
                        result=MicrophoneError(e,"StoppingMicrophone");
                    }
                    else lock(diagnosticSync)diagnostic+="\n清理阶段：DisposeMicrophone\n"+ExceptionMetadata(e);
                }
            }
            finally{Level?.Invoke(0);captureGate.Release();}
        }
        return result;
    }
    public async Task TestBailianAsync(){await using var c=new BailianClient(_=>Task.CompletedTask);using var token=new CancellationTokenSource(15000);await c.StartAsync(Settings,Keys.BailianKey,[],token.Token);await c.AudioAsync(new byte[3200],token.Token);await c.FinishAsync(token.Token);}
    public static string SafeError(Exception e)=>e is ProviderException?e.Message:e is OperationCanceledException or TimeoutException?"操作超时或已取消，确认文字已保留。":e is ArgumentException or InvalidOperationException or NotSupportedException?e.Message:"操作失败，请检查网络、设备或保存位置。";
    public async ValueTask DisposeAsync()
    {
        LogEvent("DisposeStarted");
        try
        {
        CancelToken(extraction);CancelToken(generation);await StopAsync(false);await OnActor(()=>{engine?.FinishAll();state=CaptureState.Closing;});lifetime.Cancel();try{await Task.WhenAll(timer,maintenance);}catch{}
        // Let cancelled dictionary work settle its usage before disposing the repository.
        if(await extractionGate.WaitAsync(TimeSpan.FromSeconds(2)))extractionGate.Release();
        try{await Repository.BarrierAsync().WaitAsync(TimeSpan.FromSeconds(2));await Repository.CheckpointAsync().WaitAsync(TimeSpan.FromSeconds(1));}
        catch(Exception e){LogEvent("PersistenceFailed",e,fields:[("Operation","ShutdownCheckpoint")]);}
        // Keep the actor alive until provider completions have observed cancellation.
        long end=Environment.TickCount64+1500;while(Volatile.Read(ref activePolish)>0&&Environment.TickCount64<end)await Task.Delay(20);
        deepseek.Dispose();events.Writer.TryComplete();await eventLoop;await Repository.DisposeAsync();lifetime.Dispose();
        LogEvent("DisposeCompleted");
        }
        catch(Exception e){LogEvent("DisposeFailed",e);throw;}
        finally
        {
            try{await Log.FlushAsync();}catch{ /* Logging IO cannot replace a business failure or prevent exit. */ }
            if(ownsLog)try{await Log.DisposeAsync();}catch{ }
        }
    }
}
