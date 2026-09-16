using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

/// <summary>Owns the native capture loop so device gaps and the final device packet
/// remain observable. NAudio supplies the COM interop; packet processing is local.</summary>
internal sealed class NativeWasapiCapture : IAsyncDisposable
{
    private readonly INativeCaptureClient client;
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
    private long reportedGapFrames,metadataAnomalies;
    public long MetadataAnomalies => Interlocked.Read(ref metadataAnomalies);
    private readonly record struct PacketTrace(long Packet,int Frames,AudioClientBufferFlags Flags,long Position,long Timestamp,
        AudioPacketDecision Decision,double ElapsedMs,double ReadIntervalMs,double LeaseMs,int BatchPackets,string Phase);
    public long ReportedGapFrames => Interlocked.Read(ref reportedGapFrames);
    public NativeWasapiCapture(MMDevice device, Action<IntPtr, int, bool> data, Action<Exception?> completed,
        RuntimeLog? log=null,string? turnId=null) : this(OpenClient(device,log,turnId),data,completed,log,turnId) { }
    private static INativeCaptureClient OpenClient(MMDevice device,RuntimeLog? log,string? turnId)
    {
        AudioLog.Write(log,"ActivateAudioClientStarted",turnId);
        try
        {
            var result=new NativeCaptureClient(device.AudioClient);
            AudioLog.Write(log,"ActivateAudioClientSucceeded",turnId);
            return result;
        }
        catch(Exception e){AudioLog.Write(log,"NativeOpenFailed",turnId,e,("stage","ActivateAudioClient"));throw;}
    }
    internal NativeWasapiCapture(INativeCaptureClient client,Action<IntPtr,int,bool> data,Action<Exception?> completed,
        RuntimeLog? log=null,string? turnId=null)
    {
        this.client=client;this.data=data;this.completed=completed;this.log=log;this.turnId=turnId;
        try
        {
            AudioLog.Write(log,"GetMixFormatStarted",turnId);
            Format=client.MixFormat;
            AudioLog.Write(log,"GetMixFormatSucceeded",turnId,null,("sampleRate",Format.SampleRate),("channels",Format.Channels),
                ("bits",Format.BitsPerSample),("blockAlign",Format.BlockAlign),("encoding",Format.Encoding));
        }
        catch(Exception e){AudioLog.Write(log,"NativeOpenFailed",turnId,e,("stage","GetMixFormat"));Cleanup();throw;}
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
    [DllImport("avrt.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName,out uint taskIndex);
    [DllImport("avrt.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
    private void Run()
    {
        Exception? error=null;
        bool running=false,deviceStopped=false;
        IntPtr mmcss=IntPtr.Zero;
        string stage="StartingThread";
        long packets=0,framesRead=0,silentPackets=0,anomalies=0,drainedFrames=0,started=Stopwatch.GetTimestamp();
        long lastRead=0,readGapCount=0,batches=0,emptyWakes=0,deviceStarted=0;
        double maxReadIntervalMs=0,maxLeaseMs=0,maxBatchMs=0;
        int maxBatchPackets=0,traceCount=0;
        var traces=new PacketTrace[16];
        PacketTrace? firstTrace=null;
        void FlushPacketLogs()
        {
            if(firstTrace is {} first)
            {
                AudioLog.Write(log,"FirstNativePacket",turnId,null,("frames",first.Frames),("flags",first.Flags),
                    ("position",first.Position),("timestamp100ns",first.Timestamp),("elapsedMs",first.ElapsedMs));
                firstTrace=null;
            }
            for(int i=0;i<traceCount;i++)
            {
                var t=traces[i];
                AudioLog.Write(log,"PacketMetadataAnomaly",turnId,null,("packet",t.Packet),("frames",t.Frames),("flags",t.Flags),
                    ("anomaly",t.Decision.Anomaly),("position",t.Position),("expectedPosition",t.Decision.ExpectedPosition),
                    ("positionDelta",t.Decision.PositionDelta),("timestamp100ns",t.Timestamp),("timestampDelta100ns",t.Decision.TimestampDelta),
                    ("elapsedMs",t.ElapsedMs),("readIntervalMs",t.ReadIntervalMs),("leaseMs",t.LeaseMs),
                    ("batchPacketsSoFar",t.BatchPackets),("phase",t.Phase),("reportedGapFrames",ReportedGapFrames),("fallback","StopThenDrain"));
            }
            traceCount=0;
        }
        void Stage(string value){stage=value;AudioLog.Write(log,value+"Started",turnId);}
        try
        {
            AudioLog.Write(log,stage,turnId);
            if(Volatile.Read(ref aborted)!=0||Interlocked.Read(ref stopAt)!=0)
            {AudioLog.Write(log,"StartSkippedAfterRelease",turnId);ready.TrySetResult();return;}
            // MMCSS is best effort: unavailable scheduling support must not
            // prevent an otherwise usable microphone from starting.
            try
            {
                mmcss=AvSetMmThreadCharacteristics("Audio",out _);
                AudioLog.Write(log,"CaptureScheduling",turnId,null,("mmcss",mmcss!=IntPtr.Zero),
                    ("win32Error",mmcss==IntPtr.Zero?Marshal.GetLastWin32Error():0));
            }
            catch(Exception e){AudioLog.Write(log,"CaptureSchedulingUnavailable",turnId,e);}
            // Shared event mode requires both Initialize time arguments to be zero.
            Stage("InitializeSharedEvent");client.Initialize(Format);
            AudioLog.Write(log,"InitializeSharedEventSucceeded",turnId,null,("bufferDuration",0),("periodicity",0),
                ("autoConvertPcm",true),("srcDefaultQuality",true));
            Stage("SetEventHandle");client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle());
            Stage("GetCaptureService");client.GetCaptureService();
            var timeline=new AudioPacketTimeline(Format.SampleRate);
            Stage("GetDevicePeriod");
            long period=client.DefaultDevicePeriod;
            int bufferFrames=client.BufferSize;
            if(bufferFrames<=0)throw new AudioCaptureIntegrityException("麦克风返回了无效的缓冲区长度。");
            int tailBudgetMs=(int)Math.Clamp(period/10_000*3,250,1000);
            long maxTimestampAge=Math.Max(AudioPacketTimeline.TicksPerSecond,
                (long)(bufferFrames*(double)AudioPacketTimeline.TicksPerSecond/Format.SampleRate)+AudioPacketTimeline.TicksPerSecond/10);
            AudioLog.Write(log,"CaptureBuffer",turnId,null,("bufferFrames",bufferFrames),
                ("bufferMs",bufferFrames*1000.0/Format.SampleRate),("devicePeriod100ns",period));
            WaitHandle[] waits=[packetReady,wake];
            Stage("AudioClientStart");client.Start();deviceStarted=Stopwatch.GetTimestamp();running=true;ready.TrySetResult();
            AudioLog.Write(log,"AudioClientStarted",turnId,null,("elapsedMs",Stopwatch.GetElapsedTime(started).TotalMilliseconds),("tailBudgetMs",tailBudgetMs));
            stage="ReadPackets";
            while(Volatile.Read(ref aborted)==0)
            {
                long boundary=Interlocked.Read(ref stopAt);
                if(boundary!=0&&timeline.RequiresDeviceStop&&!deviceStopped)
                {
                    // Stop freezes production; GetBuffer/ReleaseBuffer can drain
                    // pending data. Reset would discard that data and is never used.
                    stage="StopForDrain";client.Stop();deviceStopped=true;stage="ReadPackets";
                    AudioLog.Write(log,"CompatibilityDrainStarted",turnId);
                }
                if(boundary!=0&&timeline.Covers(boundary))break;
                bool reachedStop=false;
                int batchPackets=0;long batchAt=Stopwatch.GetTimestamp();
                while(Volatile.Read(ref aborted)==0&&client.GetNextPacketSize()>0)
                {
                    long readAt=Stopwatch.GetTimestamp();
                    double readIntervalMs=lastRead==0?0:Stopwatch.GetElapsedTime(lastRead,readAt).TotalMilliseconds;
                    if(!deviceStopped)
                    {
                        maxReadIntervalMs=Math.Max(maxReadIntervalMs,readIntervalMs);
                        if(readIntervalMs>bufferFrames*1000.0/Format.SampleRate)readGapCount++;
                    }
                    lastRead=readAt;
                    IntPtr pointer=client.GetBuffer(out int frames,out var flags,out long position,out long timestamp);
                    AudioPacketDecision decision=default;
                    try
                    {
                        if(frames==0)break;
                        if(frames<0||frames>bufferFrames)throw new AudioCaptureIntegrityException("麦克风返回了无效的音频包长度。");
                        packets++;batchPackets++;framesRead+=frames;
                        if((flags&AudioClientBufferFlags.Silent)!=0)silentPackets++;
                        if(deviceStopped)
                        {
                            drainedFrames+=frames;
                            if(drainedFrames>bufferFrames)throw new AudioCaptureIntegrityException("麦克风停止后仍持续返回音频，已结束本轮录音。");
                        }
                        boundary=Interlocked.Read(ref stopAt);
                        decision=timeline.Inspect(frames,position,timestamp,
                            (flags&AudioClientBufferFlags.DataDiscontinuity)!=0,
                            (flags&AudioClientBufferFlags.TimestampError)!=0,
                            boundary==0?null:boundary,QpcNow(),maxTimestampAge);
                        Interlocked.Exchange(ref reportedGapFrames,timeline.ReportedGapFrames);
                        if(decision.FramesToKeep>0)data(pointer,decision.FramesToKeep,(flags&AudioClientBufferFlags.Silent)!=0);
                        reachedStop=decision.ReachedStop;
                    }
                    finally{client.ReleaseBuffer(frames);}
                    double leaseMs=Stopwatch.GetElapsedTime(readAt).TotalMilliseconds;
                    maxLeaseMs=Math.Max(maxLeaseMs,leaseMs);
                    double elapsedMs=Stopwatch.GetElapsedTime(deviceStarted).TotalMilliseconds;
                    var trace=new PacketTrace(packets,frames,flags,position,timestamp,decision,elapsedMs,readIntervalMs,leaseMs,batchPackets,
                        boundary!=0?"Draining":elapsedMs<1000?"FirstSecond":"Recording");
                    if(packets==1)firstTrace=trace with{ElapsedMs=Stopwatch.GetElapsedTime(started).TotalMilliseconds};
                    if(decision.Anomaly!=AudioPacketAnomaly.None)
                    {
                        anomalies++;Interlocked.Exchange(ref metadataAnomalies,anomalies);
                        if(anomalies<=16)traces[traceCount++]=trace;
                    }
                    if(reachedStop||(!deviceStopped&&Interlocked.Read(ref stopAt)!=0&&timeline.RequiresDeviceStop))break;
                }
                if(batchPackets>0)
                {
                    batches++;maxBatchPackets=Math.Max(maxBatchPackets,batchPackets);
                    maxBatchMs=Math.Max(maxBatchMs,Stopwatch.GetElapsedTime(batchAt).TotalMilliseconds);
                }
                else emptyWakes++;
                // Drain ready PCM before allocating/logging diagnostic fields.
                FlushPacketLogs();
                if(reachedStop||deviceStopped)break;
                boundary=Interlocked.Read(ref stopAt);
                if(boundary!=0&&timeline.RequiresDeviceStop)continue;
                if(boundary!=0&&QpcNow()-boundary>tailBudgetMs*10_000L)
                {
                    timeline.UseDeviceStop();
                    AudioLog.Write(log,"TailClockFallback",turnId,null,("tailBudgetMs",tailBudgetMs));
                    continue;
                }
                WaitHandle.WaitAny(waits,20);
            }
        }
        catch(Exception e){error=e;AudioLog.Write(log,"NativeCaptureFailed",turnId,e,("stage",stage),("running",running));ready.TrySetException(e);}
        finally
        {
            if(running&&!deviceStopped)try{client.Stop();}catch(Exception e){error??=e;AudioLog.Write(log,"AudioClientStopFailed",turnId,e);}
            FlushPacketLogs();
            if(mmcss!=IntPtr.Zero&&!AvRevertMmThreadCharacteristics(mmcss))
                AudioLog.Write(log,"CaptureSchedulingRevertFailed",turnId,null,("win32Error",Marshal.GetLastWin32Error()));
            AudioLog.Write(log,"NativeCaptureStopped",turnId,null,("running",running),("packets",packets),("frames",framesRead),
                ("silentPackets",silentPackets),("metadataAnomalies",anomalies),("reportedGapFrames",ReportedGapFrames),
                ("compatibilityDrain",deviceStopped),("drainedFrames",drainedFrames),("aborted",Volatile.Read(ref aborted)!=0),
                ("maxReadIntervalMs",maxReadIntervalMs),("readIntervalsOverBuffer",readGapCount),
                ("maxPacketLeaseMs",maxLeaseMs),("packetBatches",batches),("maxBatchPackets",maxBatchPackets),
                ("maxBatchMs",maxBatchMs),("emptyWakes",emptyWakes));
            Cleanup();
            // Startup failures belong to Start(); avoid a second fault callback.
            try{completed(running&&Volatile.Read(ref aborted)==0?error:null);}
            finally{stopped.TrySetResult();}
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
