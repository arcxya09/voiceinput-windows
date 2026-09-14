using System.Text.Json;
using RealtimeTranscription.Core;
using RealtimeTranscription.Desktop;
using RealtimeTranscription.Infrastructure;

internal static class Review212SessionRegression
{
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        static void Check(bool condition,string message){if(!condition)throw new Exception(message);}

        await test("2.1.2 慢历史读取与新录音串行，历史不能替换新轮次引擎",async()=>
        {
            await using var f=await Fixture.Create();
            var old=await f.Seed("当前正文");var history=await f.Seed("准备载入的历史");
            await f.App.LoadSessionAsync(old);
            f.Protector.Block(history.Id);
            var loading=f.App.LoadSessionAsync(history);
            await f.Protector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            const string turn="session-regression-new-turn";
            var starting=f.App.StartAsync(Environment.TickCount64,CancellationToken.None,Task.FromResult(true),turn);
            try
            {
                Check(!starting.IsCompleted,"A new start entered the lifecycle while history still owned it");
                Check((await f.App.SnapshotAsync()).Session?.Id==old.Id,"Slow read replaced the current engine before completion");
                f.Protector.Release.Set();
                await loading.WaitAsync(TimeSpan.FromSeconds(5));
                // The existing portable harness rejects native audio construction;
                // reaching that rejection still proves Start owns its own new engine.
                Check(!await starting.WaitAsync(TimeSpan.FromSeconds(5)),"Native audio must remain excluded from portable tests");
                var snapshot=await f.App.SnapshotAsync();
                Check(snapshot.Session?.Id==turn&&snapshot.State==CaptureState.Faulted,"Late history completion replaced the new turn");
            }
            finally{f.Protector.Release.Set();await Task.WhenAll(loading,starting);}
        });

        await test("2.1.2 取消慢历史读取保留旧正文，迟到读结果不能覆盖后续录音",async()=>
        {
            await using var f=await Fixture.Create();
            var old=await f.Seed("保留的当前正文");var history=await f.Seed("取消的历史正文");
            await f.App.LoadSessionAsync(old);
            f.Protector.Block(history.Id);
            using var cancellation=new CancellationTokenSource();
            var loading=f.App.LoadSessionAsync(history,cancellation.Token);
            try
            {
                await f.Protector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                try{await loading.WaitAsync(TimeSpan.FromSeconds(5));throw new Exception("Cancelled history load was accepted");}
                catch(OperationCanceledException){}
                Check((await f.App.SnapshotAsync()).Session?.Id==old.Id,"Cancellation discarded the current engine");
                const string turn="session-regression-after-cancel";
                Check(!await f.App.StartAsync(Environment.TickCount64,CancellationToken.None,Task.FromResult(true),turn).WaitAsync(TimeSpan.FromSeconds(5)),"Native audio must remain excluded");
                f.Protector.Release.Set();
                await f.Protector.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // Allow the abandoned repository read to finish; no continuation is
                // allowed to install its old result after the method was cancelled.
                await f.App.Repository.BarrierAsync();
                Check((await f.App.SnapshotAsync()).Session?.Id==turn,"A cancelled read installed its late result");
            }
            finally{f.Protector.Release.Set();}
        });

        await test("2.1.2 已取消的历史请求不停止或替换当前会话",async()=>
        {
            await using var f=await Fixture.Create();
            var current=await f.Seed("保留的历史正文");var other=await f.Seed("不应载入的正文");
            await f.App.LoadSessionAsync(current);
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try{await f.App.LoadSessionAsync(other,cancelled.Token);throw new Exception("Already-cancelled request was accepted");}
            catch(OperationCanceledException){}
            var snapshot=await f.App.SnapshotAsync();
            Check(snapshot.Session?.Id==current.Id&&snapshot.State==CaptureState.Paused,"Already-cancelled load stopped or replaced the current session");
            Check(!await f.App.StartAsync(Environment.TickCount64,CancellationToken.None,Task.FromResult(true),"after-history-cancel").WaitAsync(TimeSpan.FromSeconds(5)),"Native audio must remain excluded");
        });
    }

    private sealed class Fixture:IAsyncDisposable
    {
        private readonly string folder=Path.Combine(Path.GetTempPath(),"VoiceInputSession212-"+JsonCodec.Id());
        public readonly BlockingHistoryProtector Protector=new();
        public AppController App {get;private set;}=null!;
        public static async Task<Fixture> Create()
        {
            var f=new Fixture();
            new SettingsStore(f.folder,f.Protector).Save(new(){LegacyEndpoint=true,AutoExtract=false,PolishEnabled=false},new("TEST_ONLY","TEST_ONLY"));
            f.App=new(f.folder,f.Protector,new RegressionHandler((_,_)=>throw new Exception("Unexpected cloud request")));
            await f.App.InitializeAsync();return f;
        }
        public async Task<SessionData> Seed(string text)
        {
            var session=new SessionData();
            await App.Repository.SaveSegmentAsync(session,new(){SessionId=session.Id,TaskId=JsonCodec.Id(),TaskOrder=1,SentenceId=1,RawText=text,FinalText=text,AsrState=AsrState.Confirmed,OutputState=OutputState.Published,SaveState=SaveState.Saved,SourceRevision=1});
            return session;
        }
        public async ValueTask DisposeAsync()
        {
            Protector.Release.Set();await App.DisposeAsync();
            try{Directory.Delete(folder,true);}catch(IOException){}
        }
    }

    private sealed class BlockingHistoryProtector:IProtector
    {
        private readonly TestProtector inner=new();
        private string? blockedId;
        private int entered;
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Exited=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release=new();
        public void Block(string id)=>blockedId=id;
        public byte[] Protect(byte[] plain)=>inner.Protect(plain);
        public byte[] Unprotect(byte[] cipher)
        {
            var plain=inner.Unprotect(cipher);using var doc=JsonDocument.Parse(plain);
            if(blockedId!=null&&doc.RootElement.TryGetProperty("id",out var id)&&id.GetString()==blockedId&&Interlocked.Exchange(ref entered,1)==0)
            {
                Entered.TrySetResult();
                try{if(!Release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException("History test did not release its read");}
                finally{Exited.TrySetResult();}
            }
            return plain;
        }
    }
}
