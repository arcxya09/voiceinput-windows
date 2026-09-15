using System.Collections.Concurrent;
using RealtimeTranscription.Core;
using RealtimeTranscription.Desktop.Input;
using RealtimeTranscription.Desktop;
using RealtimeTranscription.Infrastructure;

static class StartupCaptureRegression
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Phase(TranscriptSnapshot snapshot) => UiPresentation.Phase(snapshot, true, true, true);

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("启动延迟：麦克风构造期间松键，设备返回后不能补开录音", async () =>
        {
            var turn = new Turn(holdConstruction: true);
            await using var f = await Fixture.Create(turn);
            var start = f.Start();
            await turn.ConstructionEntered.Task.WaitAsync(Budget);
            var preparing = await f.App.SnapshotAsync();
            Check(preparing.State == CaptureState.Connecting && !preparing.LocalAudioReady && Phase(preparing) == "准备麦克风", "构造未完成就宣称麦克风已就绪。");

            f.App.RequestStopCapture();
            turn.ConstructionRelease.TrySetResult();
            await start.WaitAsync(Budget);
            var capture = await turn.Capture.Task.WaitAsync(Budget);
            var released = await f.App.SnapshotAsync();
            Check(capture.RecordingStarts == 0 && capture.StopRequested, "松键后仍启动了实际采集。");
            Check(capture.SamplesSent == 0 && turn.Socket.Pcm.IsEmpty, "松键后构造的设备采集或上传了音频。");
            Check(!released.LocalAudioReady && released.CaptureReleased && Phase(released) == "尾句处理中", "松键后仍显示正在听。");
            await f.App.StopAsync(false).WaitAsync(Budget);
        });

        await test("启动延迟：云连接等待时首个本地音频立即更新正在听，松键立即更新收尾", async () =>
        {
            var turn = new Turn(holdTransport: true);
            await using var f = await Fixture.Create(turn);
            var start = f.Start();
            var capture = await turn.StartedCapture();
            await turn.ConnectionEntered.Task.WaitAsync(Budget);
            Check(Phase(await f.App.SnapshotAsync()) == "准备麦克风", "尚未收到音频时提前显示正在听。");

            using var ready = new UpdateProbe(f.App, s => s.LocalAudioReady);
            await capture.EmitFrame(.04f);
            var listening = await ready.Next.Task.WaitAsync(Budget);
            Check(listening.State == CaptureState.Connecting && Phase(listening) == "正在听", "本地就绪仍等待云连接才更新界面。");
            Check(!start.IsCompleted && turn.Socket.Actions.IsEmpty && turn.Socket.Pcm.IsEmpty, "连接完成前启动了任务或上传了 PCM。");
            Check(f.Levels.Any(v => v > 0), "本地音频电平未及时呈现。");

            using var released = new UpdateProbe(f.App, s => s.CaptureReleased && !s.LocalAudioReady);
            f.App.RequestStopCapture();
            var tail = await released.Next.Task.WaitAsync(Budget);
            Check(tail.State == CaptureState.Connecting && Phase(tail) == "尾句处理中", "云连接未完成时松键仍显示正在听。");
            turn.ConnectionRelease.TrySetResult();
            Check(await start.WaitAsync(Budget), "已缓冲音频的正常松键不能继续完成识别。");
            await turn.Socket.FirstPcm.Task.WaitAsync(Budget);
            var draining = await f.App.SnapshotAsync();
            Check(draining.State == CaptureState.Draining && !draining.LocalAudioReady, "任务启动后错误恢复了已松键的录音状态。");
            await f.App.StopAsync(false).WaitAsync(Budget);
        });

        await test("启动延迟：长按门槛前取消只清理本地音频，不建立云任务", async () =>
        {
            var turn = new Turn();
            await using var f = await Fixture.Create(turn);
            // A future press time leaves the hold gate closed without relying on a short sleep.
            var start = f.Start(Environment.TickCount64 + 60_000);
            var capture = await turn.StartedCapture();
            await capture.EmitFrame(.03f);
            f.Cancel();
            Check(!await start.WaitAsync(Budget), "长按门槛前取消仍完成启动。");
            Check(!turn.ConnectionEntered.Task.IsCompleted && turn.Socket.Actions.IsEmpty && turn.Socket.Pcm.IsEmpty, "长按门槛前建立了连接、任务或上传音频。");
            var snapshot = await f.App.SnapshotAsync();
            Check(!snapshot.LocalAudioReady && snapshot.State == CaptureState.Stopped && capture.Disposed, "取消后仍保留就绪状态或设备。");
        });

        await test("启动延迟：目标位置确认失败不发送 run-task 或已缓冲 PCM", async () =>
        {
            var turn = new Turn();
            await using var f = await Fixture.Create(turn);
            var target = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = f.Start(target: target.Task);
            var capture = await turn.StartedCapture();
            await capture.EmitFrame(.03f);
            Check(turn.Socket.Actions.IsEmpty && turn.Socket.Pcm.IsEmpty, "目标尚未确认就上传了本地音频。");
            target.TrySetResult(false);
            Check(!await start.WaitAsync(Budget), "目标位置确认失败仍完成启动。");
            Check(turn.Socket.Actions.IsEmpty && turn.Socket.Pcm.IsEmpty, "目标位置确认失败仍创建任务或上传缓冲。");
            var snapshot = await f.App.SnapshotAsync();
            Check(!snapshot.LocalAudioReady && snapshot.Status.Contains("无法确认可编辑的输入位置") && capture.Disposed, "目标失败原因或设备清理状态丢失。");
        });

        await test("启动延迟：连接进行中取消丢弃缓冲，不残留正在听或迟发任务", async () =>
        {
            var turn = new Turn(holdTransport: true);
            await using var f = await Fixture.Create(turn);
            var start = f.Start();
            var capture = await turn.StartedCapture();
            await turn.ConnectionEntered.Task.WaitAsync(Budget);
            await capture.EmitFrame(.03f);
            f.Cancel();
            Check(!await start.WaitAsync(Budget), "取消连接后仍报告启动成功。");
            turn.ConnectionRelease.TrySetResult();
            var snapshot = await f.App.SnapshotAsync();
            Check(!snapshot.LocalAudioReady && snapshot.State == CaptureState.Stopped && capture.Disposed, "取消连接后设备或就绪状态残留。");
            Check(turn.Socket.Actions.IsEmpty && turn.Socket.Pcm.IsEmpty, "取消后迟发了任务或已缓冲 PCM。");
        });

        await test("启动延迟：松键及下一轮开始后，旧设备回调不能恢复电平或就绪状态", async () =>
        {
            var first = new Turn();
            var second = new Turn(holdTransport: true);
            await using var f = await Fixture.Create(first, second);
            var initialStart = f.Start();
            var oldCapture = await first.StartedCapture();
            Check(await initialStart.WaitAsync(Budget), "第一轮未能启动。");
            await oldCapture.EmitFrame(.03f);
            Check((await f.App.SnapshotAsync()).LocalAudioReady, "当前设备的首个音频未设为就绪。");

            f.App.RequestStopCapture();
            await f.App.SnapshotAsync();
            f.Levels.Clear();
            oldCapture.EmitLateLevel(.1f);
            Check(!(await f.App.SnapshotAsync()).LocalAudioReady && !f.Levels.Any(v => v > 0), "松键后的旧回调恢复了就绪状态或电平。");
            await f.App.StopAsync(false).WaitAsync(Budget);

            var nextStart = f.Start();
            var newCapture = await second.StartedCapture();
            await second.ConnectionEntered.Task.WaitAsync(Budget);
            f.Levels.Clear();
            oldCapture.EmitLateLevel(.2f);
            var waiting = await f.App.SnapshotAsync();
            Check(!waiting.LocalAudioReady && !waiting.CaptureReleased && Phase(waiting) == "准备麦克风", "上一轮回调污染了新一轮的采集状态。");
            Check(!f.Levels.Any(v => v > 0), "上一轮回调写入了新一轮的电平。");
            using var currentReady = new UpdateProbe(f.App, s => s.LocalAudioReady);
            await newCapture.EmitFrame(.04f);
            Check((await currentReady.Next.Task.WaitAsync(Budget)).State == CaptureState.Connecting, "屏蔽旧回调时同时屏蔽了当前设备。");
            second.ConnectionRelease.TrySetResult();
            Check(await nextStart.WaitAsync(Budget), "第二轮未能启动。");
            await f.App.StopAsync(false).WaitAsync(Budget);
        });

        await test("启动延迟：音频电平通知与松键重叠时，最后发布的电平保持为零", async () =>
        {
            var turn = new Turn(holdTransport: true);
            await using var f = await Fixture.Create(turn);
            var start = f.Start();
            var capture = await turn.StartedCapture();
            await turn.ConnectionEntered.Task.WaitAsync(Budget);
            var positiveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var positiveRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = new ConcurrentQueue<float>();
            Action<float> pausePositive = value =>
            {
                if (value <= 0) return;
                positiveEntered.TrySetResult();
                positiveRelease.Task.WaitAsync(Budget).GetAwaiter().GetResult();
            };
            f.App.Level += pausePositive;
            f.App.Level += delivered.Enqueue;
            try
            {
                var frame = Task.Run(() => capture.EmitFrame(.03f));
                await positiveEntered.Task.WaitAsync(Budget);
                var release = Task.Run(() => { releaseRequested.TrySetResult(); f.App.RequestStopCapture(); });
                await releaseRequested.Task.WaitAsync(Budget);
                positiveRelease.TrySetResult();
                await Task.WhenAll(frame, release).WaitAsync(Budget);
                Check(delivered.Count >= 2 && delivered.Last() == 0, "松键归零后仍发布了并发的旧正电平。");
                Check(!(await f.App.SnapshotAsync()).LocalAudioReady, "重叠回调使已松键设备恢复就绪。");
            }
            finally
            {
                positiveRelease.TrySetResult();
                f.App.Level -= pausePositive;
                f.App.Level -= delivered.Enqueue;
            }
            turn.ConnectionRelease.TrySetResult();
            Check(await start.WaitAsync(Budget), "重叠松键后未能继续处理缓冲音频。");
            await f.App.StopAsync(false).WaitAsync(Budget);
        });

        await test("启动延迟：本地就绪后的云启动失败清除就绪并保留首个错误", async () =>
        {
            const string reason = "百炼认证失败（HTTP 401），请检查 API Key 与地域是否对应。";
            var turn = new Turn(holdTransport: true);
            await using var f = await Fixture.Create(turn);
            var start = f.Start();
            var capture = await turn.StartedCapture();
            await turn.ConnectionEntered.Task.WaitAsync(Budget);
            using var ready = new UpdateProbe(f.App, s => s.LocalAudioReady);
            await capture.EmitFrame(.03f);
            await ready.Next.Task.WaitAsync(Budget);
            turn.ConnectionRelease.TrySetException(new ProviderException(reason));
            Check(!await start.WaitAsync(Budget), "认证失败仍报告启动成功。");
            // A provider failure may schedule the controller's stop path; wait for its final status too.
            await f.App.StopAsync(true).WaitAsync(Budget);
            var failed = await f.App.SnapshotAsync();
            Check(!failed.LocalAudioReady && capture.Disposed && failed.Status.Contains(reason), "启动失败的就绪状态未清除，或根因被通用收尾提示覆盖。");
            Check(f.App.Diagnostic.Contains(reason), "启动诊断丢失首个云端错误。");
            Check(turn.Socket.Actions.IsEmpty && turn.Socket.Pcm.IsEmpty, "认证失败后仍发出任务或 PCM。");
            f.Levels.Clear();
            capture.EmitLateLevel(.2f);
            Check(!(await f.App.SnapshotAsync()).LocalAudioReady && !f.Levels.Any(v => v > 0), "故障设备的迟到回调恢复了正在听。");
        });
    }

    public static async Task RunInputCompatibility(Func<string,Func<Task>,Task> test)
    {
        foreach(string scenario in new[]{"consumed-ctrl","no-focus","missing-focus","unavailable-provider","not-editable","read-only","disabled","focus-blip","focus-lost","window-changed","escape","password",
            "dictation-only","clipboard-busy","paste-blocked","clipboard-changed","empty","incomplete","no-focus-polish","escape-polish","escape-copy","escape-paste","short-press"})
        await test("2.1.8 生产按住说话："+scenario,async()=>
        {
            TextDelivery.Reset();
            bool polished=scenario is "no-focus-polish" or "escape-polish";
            bool dictationOnly=scenario=="dictation-only";
            if(scenario is "no-focus" or "no-focus-polish" or "escape-polish")Win32.Target=null;
            if(scenario=="missing-focus")Win32.Target=Win32.Target! with{Focus=IntPtr.Zero};
            if(scenario=="unavailable-provider")TextDelivery.CaptureCode="Unavailable";
            if(scenario=="not-editable")TextDelivery.CaptureCode="NotEditable";
            if(scenario=="password")TextDelivery.CaptureCode="Password";
            if(scenario=="read-only")TextDelivery.CaptureCode="ReadOnly";
            if(scenario=="disabled")TextDelivery.CaptureCode="Disabled";
            TextDelivery.ClipboardBusy=scenario=="clipboard-busy";
            TextDelivery.BlockPaste=scenario=="paste-blocked";
            TextDelivery.ClipboardChangesBeforePaste=scenario=="clipboard-changed";
            TextDelivery.PauseBeforeCopy=scenario=="escape-copy";
            TextDelivery.PauseBeforeSend=scenario=="escape-paste";
            var polishEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var polishRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var turn=new Turn();
            if(scenario=="empty")turn.Socket.FinalText="";
            if(scenario=="incomplete")turn.Socket.IncompleteTail=true;
            if(polished)turn.Socket.FinalText="嗯，这个方案我们先试一下。";
            int polishCalls=0;
            await using var fixture=await Fixture.Create([turn],polished?async(_,token)=>
            {
                Interlocked.Increment(ref polishCalls);polishEntered.TrySetResult();
                await polishRelease.Task.WaitAsync(Budget,token);
                return ControllerFixture.Reply("这个方案我们先试一下。");
            }:null);
            await fixture.App.SaveSettingsAsync(fixture.App.Settings with{DictationOnly=dictationOnly,PolishEnabled=polished},
                polished?new("TEST_ONLY","TEST_ONLY"):fixture.App.Keys);
            await using var ptt=new PushToTalkService(fixture.App);
            var completed=new TaskCompletionSource<VoiceTurnCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
            ptt.TurnCompleted+=c=>completed.TrySetResult(c);
            await ptt.InitializeAsync();
            long downAt=Environment.TickCount64;PhysicalHook.Latest!.Emit("down",downAt);
            if(scenario=="short-press")
            {
                // Explicit event times reproduce a real short press without depending on task scheduling.
                PhysicalHook.Latest.Emit("up",downAt+10);
                var cancelled=await completed.Task.WaitAsync(Budget);
                Check(cancelled.DeliveryState=="Cancelled"&&cancelled.Status.Contains("短按"),"短按未保留取消原因。");
                Check(turn.Socket.Actions.IsEmpty&&turn.Socket.Pcm.IsEmpty&&TextDelivery.CopyAttempts==0&&TextDelivery.Sends==0,"短按仍上传、复制或投递。");
                return;
            }
            if(scenario=="password")
            {
                var denied=await completed.Task.WaitAsync(Budget);
                Check(denied.DeliveryState=="StartFailed"&&denied.Status.Contains("密码"),"密码拒绝原因被取消/无文字掩盖。");
                Check(turn.Socket.Actions.IsEmpty&&turn.Socket.Pcm.IsEmpty&&TextDelivery.CopyAttempts==0&&TextDelivery.Sends==0,"密码目标仍上传、复制或投递。");
                return;
            }
            var capture=await turn.StartedCapture();await capture.EmitFrame(.04f);
            await turn.Socket.FirstPcm.Task.WaitAsync(Budget);
            if(scenario is "focus-blip" or "focus-lost")Win32.Focus=FocusObservation.Changed;
            if(scenario=="window-changed")Win32.Window=FocusObservation.Changed;
            if(scenario=="focus-blip")
            {
                await Task.Delay(350);Win32.Focus=FocusObservation.Stable;
            }
            await Task.Delay(scenario=="focus-lost"?1300:700);
            Check(!completed.Task.IsCompleted&&ptt.Busy&&!capture.StopRequested,"未收到真实松键或取消就中断录音。");
            Check(TextDelivery.CopyAttempts==0&&TextDelivery.Sends==0,"仍在录音时提前复制或上屏。");
            bool manual=scenario is "no-focus" or "missing-focus" or "read-only" or "disabled" or "focus-lost" or "window-changed" or "dictation-only" or "no-focus-polish" or "escape-polish";
            if(manual&&!dictationOnly)
            {
                var snapshot=await fixture.App.SnapshotAsync();
                Check(snapshot.Session!.DeliveryReason.Contains("自动复制"),"兼容降级未给出完成后复制提示。");
                // Returning later must not re-enable automatic insertion.
                Win32.Focus=Win32.Window=FocusObservation.Stable;
            }
            PhysicalHook.Latest.Emit(scenario=="escape"?"escape":"up");
            if(polished)
            {
                await polishEntered.Task.WaitAsync(Budget);
                Check(capture.StopRequested&&!completed.Task.IsCompleted&&TextDelivery.CopyAttempts==0,"全文润色尚未结束就复制或完成。");
                if(scenario=="escape-polish")PhysicalHook.Latest.Emit("escape");
                else polishRelease.TrySetResult();
            }
            bool cancelDuringDelivery=scenario is "escape-copy" or "escape-paste";
            if(cancelDuringDelivery)
            {
                bool beforeCopy=scenario=="escape-copy";
                await (beforeCopy?TextDelivery.CopyEntered:TextDelivery.SendEntered).Task.WaitAsync(Budget);
                Check(TextDelivery.CopyAttempts==1&&TextDelivery.Copies==(beforeCopy?0:1)&&TextDelivery.Sends==0&&!completed.Task.IsCompleted,
                    "取消竞态入点之前已有意外复制或粘贴。");
                PhysicalHook.Latest.Emit("escape");
                // Wait until PTT has processed the real cancellation; only then
                // release the simulated native boundary to return its failure.
                await TextDelivery.CancellationObserved.Task.WaitAsync(Budget);
                (beforeCopy?TextDelivery.CopyRelease:TextDelivery.SendRelease).TrySetResult();
            }
            var result=await completed.Task.WaitAsync(Budget);
            bool cancelledResult=scenario is "escape" or "escape-polish" or "escape-copy" or "escape-paste";
            if(cancelDuringDelivery)Check(result.Status=="本轮输入已取消。","复制/粘贴的边界结果覆盖了原始Esc取消原因："+result.Status);
            string expectedText=scenario=="empty"?"":scenario=="no-focus-polish"?"这个方案我们先试一下。":turn.Socket.FinalText;
            Check(result.Text==expectedText,"收尾正文丢失或未采用完整润色："+result.Text);
            string expectedState=cancelledResult?"Cancelled":scenario=="empty"?"Empty":scenario is "incomplete" or "clipboard-changed"?"Blocked":scenario=="clipboard-busy"?"CopyFailed":manual||scenario=="paste-blocked"?"Copied":"PasteSent";
            Check(result.DeliveryState==expectedState,"终态错误："+result.DeliveryState+" / "+result.Status);
            bool shouldCopy=cancelDuringDelivery||!cancelledResult&&scenario is not ("empty" or "incomplete");
            Check(TextDelivery.CopyAttempts==(shouldCopy?1:0),"重复复制，或取消/空白/不完整结果仍改写剪贴板。");
            bool copied=shouldCopy&&scenario is not ("clipboard-busy" or "escape-copy");
            Check(TextDelivery.Copies==(copied?1:0)&&TextDelivery.CopiedText==(copied?expectedText:""),"剪贴板成功状态或完整正文不符。");
            bool shouldPaste=copied&&!cancelledResult&&!manual&&scenario is not ("paste-blocked" or "clipboard-changed");
            Check(TextDelivery.Sends==(shouldPaste?1:0)&&TextDelivery.SentText==(shouldPaste?expectedText:""),"兼容降级后误投递、重复投递或正文不一致。");
            Check(polishCalls==(polished?1:0),"无焦点流程跳过润色或重复请求。");
        });
    }

    private sealed class UpdateProbe : IDisposable
    {
        private readonly AppController app;
        private readonly Action<TranscriptSnapshot> handler;
        internal readonly TaskCompletionSource<TranscriptSnapshot> Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal UpdateProbe(AppController app, Func<TranscriptSnapshot, bool> predicate)
        {
            this.app = app;
            handler = snapshot => { if (predicate(snapshot)) Next.TrySetResult(snapshot); };
            app.Updated += handler;
        }
        public void Dispose() => app.Updated -= handler;
    }

    private sealed class Turn
    {
        internal readonly ScriptedSocket Socket = new();
        internal readonly TaskCompletionSource ConstructionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ConstructionRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ConnectionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ConnectionRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ScriptedCapture> Capture = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Turn(bool holdConstruction = false, bool holdTransport = false)
        {
            if (!holdConstruction) ConstructionRelease.TrySetResult();
            if (!holdTransport) ConnectionRelease.TrySetResult();
        }
        internal async Task<ScriptedCapture> StartedCapture()
        {
            var capture = await Capture.Task.WaitAsync(Budget);
            await capture.Started.Task.WaitAsync(Budget);
            return capture;
        }
    }

    private sealed class ScriptedCapture(Func<byte[], CancellationToken, ValueTask> send, Action<float> level) : IAudioCapture
    {
        private int stopped, disposed, recordingStarts;
        private long samplesSent;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RecordingStarts => Volatile.Read(ref recordingStarts);
        internal bool StopRequested => Volatile.Read(ref stopped) != 0;
        internal bool Disposed => Volatile.Read(ref disposed) != 0;
        public string EndpointId => "scripted-microphone";
        public string FormatDescription => "scripted PCM16 16000 Hz mono";
        public string Diagnostic => FormatDescription;
        public string? FailureMessage => null;
        public long SamplesSent => Interlocked.Read(ref samplesSent);
        public void Start()
        {
            if (!StopRequested) Interlocked.Increment(ref recordingStarts);
            Started.TrySetResult();
        }
        internal async Task EmitFrame(float value)
        {
            Check(RecordingStarts > 0 && !StopRequested && !Disposed, "测试试图让非录音设备生成音频。");
            await send(new byte[3200], CancellationToken.None);
            Interlocked.Add(ref samplesSent, 1600);
            level(value);
        }
        internal void EmitLateLevel(float value) => level(value);
        public void RequestStop() => Interlocked.Exchange(ref stopped, 1);
        public void Abort() => RequestStop();
        public Task StopAsync(CancellationToken token) { RequestStop(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { RequestStop(); Interlocked.Exchange(ref disposed, 1); return ValueTask.CompletedTask; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "VoiceInputStartupCapture-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource cancellation = new();
        private readonly List<Task<bool>> starts = [];
        private Turn[] turns = [];
        internal AppController App { get; private set; } = null!;
        internal readonly ConcurrentQueue<float> Levels = [];
        internal static Task<Fixture> Create(params Turn[] turns)=>Create(turns,null);
        internal static async Task<Fixture> Create(Turn[] turns,Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>? http)
        {
            var f = new Fixture { turns = turns };
            var protector = new TestProtector();
            new SettingsStore(f.folder, protector).Save(new()
            {
                LegacyEndpoint = true, DictationOnly = true, SaveMemory = false, AllowLearning = false,
                LearnCorrections = false, DynamicLexicon = false, PolishEnabled = false, UseLexicon = false
            }, new("TEST_ONLY", ""));
            var pending = new Queue<Turn>(turns);
            Turn? constructing = null;
            f.App = new AppController(f.folder, protector, new RegressionHandler(http??((_, _) => throw new Exception("Unexpected HTTP request"))),
                (_, send, _, level) =>
                {
                    var turn = constructing ?? throw new Exception("Capture created without a client.");
                    turn.ConstructionEntered.TrySetResult();
                    turn.ConstructionRelease.Task.WaitAsync(Budget).GetAwaiter().GetResult();
                    var capture = new ScriptedCapture(send, level);
                    turn.Capture.TrySetResult(capture);
                    return capture;
                },
                receive =>
                {
                    var turn = pending.Dequeue();
                    constructing = turn;
                    return new BailianClient(receive, turn.Socket, async (_, _, token) =>
                    {
                        turn.ConnectionEntered.TrySetResult();
                        await turn.ConnectionRelease.Task.WaitAsync(Budget, token);
                    });
                });
            f.App.Level += f.Levels.Enqueue;
            await f.App.InitializeAsync();
            return f;
        }
        internal Task<bool> Start(long? pressedAt = null, Task<bool>? target = null)
        {
            var start = App.StartAsync(pressedAt ?? Environment.TickCount64 - 1000, cancellation.Token, target ?? Task.FromResult(true), JsonCodec.Id());
            starts.Add(start);
            return start;
        }
        internal void Cancel() => cancellation.Cancel();
        public async ValueTask DisposeAsync()
        {
            Cancel();
            foreach (var turn in turns)
            {
                turn.ConstructionRelease.TrySetResult();
                turn.ConnectionRelease.TrySetResult();
            }
            try { await Task.WhenAll(starts).WaitAsync(Budget); } catch { }
            await App.DisposeAsync();
            cancellation.Dispose();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
