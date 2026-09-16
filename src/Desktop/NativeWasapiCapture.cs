using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

/// <summary>Owns the native capture loop so device gaps and the final device packet
/// remain observable. NAudio supplies the COM interop; packet processing is local.</summary>
internal sealed class NativeWasapiCapture : IAsyncDisposable
{
    private readonly AudioClient client;
    private readonly Action<IntPtr, int, bool> data;
    private readonly Action<Exception?> completed;
    private readonly RuntimeLog? log;
    private readonly string? turnId;
    private readonly EventWaitHandle packetReady = new(false, EventResetMode.AutoReset);
    private readonly EventWaitHandle wake = new(false, EventResetMode.AutoReset);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int startCalled, aborted, cleaned;
    private long stopAt;
    public WaveFormat Format { get; }
    public NativeWasapiCapture(MMDevice device, Action<IntPtr, int, bool> data, Action<Exception?> completed,
        RuntimeLog? log=null,string? turnId=null)
    {
        this.log=log;this.turnId=turnId;
        this.data = data; this.completed = completed;
        string stage="ActivateAudioClient";
        try
        {
            AudioLog.Write(log,stage+"Started",turnId);
            client = device.AudioClient;
            AudioLog.Write(log,stage+"Succeeded",turnId);
            stage="GetMixFormat";AudioLog.Write(log,stage+"Started",turnId);
            Format = client.MixFormat;
            AudioLog.Write(log,stage+"Succeeded",turnId,null,("sampleRate",Format.SampleRate),("channels",Format.Channels),
                ("bits",Format.BitsPerSample),("blockAlign",Format.BlockAlign),("encoding",Format.Encoding));
        }
        catch(Exception e) { AudioLog.Write(log,"NativeOpenFailed",turnId,e,("stage",stage));Cleanup();throw; }
    }
    private static long QpcNow() => (long)(Stopwatch.GetTimestamp() * (double)AudioPacketTimeline.TicksPerSecond / Stopwatch.Frequency);
    public void Start()
    {
        if (Interlocked.Exchange(ref startCalled, 1) != 0) throw new InvalidOperationException("麦克风已经启动。");
        new Thread(Run) { IsBackground = true, Name = "VoiceInput WASAPI", Priority = ThreadPriority.AboveNormal }.Start();
        try { ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch(Exception e) { AudioLog.Write(log,"StartWaitFailed",turnId,e);Abort();throw; }
    }
    public void RequestStop()
    {
        Interlocked.CompareExchange(ref stopAt, QpcNow(), 0);
        Signal();
    }
    public void Abort() { Interlocked.Exchange(ref aborted, 1); Signal(); }
    private void Signal() { try { wake.Set(); } catch (ObjectDisposedException) { } }
    private void Run()
    {
        Exception? error = null;
        bool running = false;
        string stage="StartingThread";
        long packets=0,framesRead=0,silentPackets=0,started=Stopwatch.GetTimestamp();
        void Stage(string value){stage=value;AudioLog.Write(log,value+"Started",turnId);}
        try
        {
            AudioLog.Write(log,stage,turnId);
            if (Volatile.Read(ref aborted) != 0 || Interlocked.Read(ref stopAt) != 0) { AudioLog.Write(log,"StartSkippedAfterRelease",turnId);ready.TrySetResult();return; }
            // Shared, event-driven streams let the audio engine choose the buffer
            // duration. Both time parameters must be zero for this WASAPI mode.
            Stage("InitializeSharedEvent");
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                0, 0, Format, Guid.Empty);
            AudioLog.Write(log,"InitializeSharedEventSucceeded",turnId,null,("bufferDuration",0),("periodicity",0),
                ("autoConvertPcm",true),("srcDefaultQuality",true));
            Stage("SetEventHandle");
            client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle());
            Stage("GetCaptureService");
            var capture = client.AudioCaptureClient;
            var timeline = new AudioPacketTimeline(Format.SampleRate);
            Stage("GetDevicePeriod");
            int tailBudgetMs = (int)Math.Clamp(client.DefaultDevicePeriod / 10_000 * 3, 250, 1000);
            WaitHandle[] waits = [packetReady, wake];
            Stage("AudioClientStart");
            client.Start(); running = true; ready.TrySetResult();
            AudioLog.Write(log,"AudioClientStarted",turnId,null,("elapsedMs",Stopwatch.GetElapsedTime(started).TotalMilliseconds),("tailBudgetMs",tailBudgetMs));
            stage="ReadPackets";
            while (Volatile.Read(ref aborted) == 0)
            {
                long boundary = Interlocked.Read(ref stopAt);
                if (boundary != 0 && timeline.Covers(boundary)) break;
                bool reachedStop = false;
                while (Volatile.Read(ref aborted) == 0 && capture.GetNextPacketSize() > 0)
                {
                    IntPtr pointer = capture.GetBuffer(out int frames, out var flags, out long position, out long timestamp);
                    try
                    {
                        if (frames == 0) break;
                        packets++;framesRead+=frames;
                        if((flags & AudioClientBufferFlags.Silent)!=0)silentPackets++;
                        if(packets==1)AudioLog.Write(log,"FirstNativePacket",turnId,null,("frames",frames),("flags",flags),
                            ("elapsedMs",Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                        boundary = Interlocked.Read(ref stopAt);
                        var decision = timeline.Inspect(frames, position, timestamp,
                            (flags & AudioClientBufferFlags.DataDiscontinuity) != 0,
                            (flags & AudioClientBufferFlags.TimestampError) != 0,
                            boundary == 0 ? null : boundary);
                        if (decision.FramesToKeep > 0)
                            data(pointer, decision.FramesToKeep, (flags & AudioClientBufferFlags.Silent) != 0);
                        reachedStop = decision.ReachedStop;
                    }
                    finally { capture.ReleaseBuffer(frames); }
                    if (reachedStop) break;
                }
                if (reachedStop) break;
                boundary = Interlocked.Read(ref stopAt);
                if (boundary != 0 && QpcNow() - boundary > tailBudgetMs * 10_000L)
                    throw new AudioCaptureIntegrityException("麦克风尾部采样未能完整收齐，确认文字已保留，本轮不自动输入。");
                WaitHandle.WaitAny(waits, 20);
            }
        }
        catch (Exception e) { error=e;AudioLog.Write(log,"NativeCaptureFailed",turnId,e,("stage",stage),("running",running));ready.TrySetException(e); }
        finally
        {
            if (running) try { client.Stop(); } catch (Exception e) { error??=e;AudioLog.Write(log,"AudioClientStopFailed",turnId,e); }
            AudioLog.Write(log,"NativeCaptureStopped",turnId,null,("running",running),("packets",packets),("frames",framesRead),
                ("silentPackets",silentPackets),("aborted",Volatile.Read(ref aborted)!=0));
            Cleanup();
            // Before Start succeeds, ready/Start() owns the failure. A second
            // fault callback could otherwise replace its actionable diagnostic
            // with a generic interruption message. Completion still drains the queue.
            try { completed(running && Volatile.Read(ref aborted) == 0 ? error : null); }
            finally { stopped.TrySetResult(); }
        }
    }
    private void Cleanup()
    {
        if (Interlocked.Exchange(ref cleaned, 1) != 0) return;
        try { client?.Dispose(); } catch(Exception e) { AudioLog.Write(log,"AudioClientDisposeFailed",turnId,e); }
        packetReady.Dispose(); wake.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        Abort();
        if (Volatile.Read(ref startCalled) == 0) { Cleanup(); stopped.TrySetResult(); }
        // The capture thread owns COM cleanup, including when a driver call is stuck.
        // Do not block application shutdown indefinitely or dispose its live handles.
        try { await stopped.Task.WaitAsync(TimeSpan.FromSeconds(1)); } catch (TimeoutException e) { AudioLog.Write(log,"CaptureThreadStopTimeout",turnId,e); }
    }
}
