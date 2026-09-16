using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

public record AudioDevice(string Id, string Name);
public sealed class AudioCapture : IAudioCapture
{
    private readonly record struct Block(byte[] Buffer, int Count);
    private readonly NativeWasapiCapture capture;
    private readonly MMDevice device;
    private readonly Channel<Block> queue = Channel.CreateBounded<Block>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource abort = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<byte[],CancellationToken,ValueTask> send;
    private readonly Action<string> fault;
    private readonly Action<float> level;
    private readonly RuntimeLog? log;
    private readonly string? turnId;
    private Task? processing;
    private int queuedBytes, faulted;
    private int stopRequested;
    private readonly object captureSync=new();
    private readonly PcmDecoder decoder;
    private readonly int maxQueuedBytes;
    public string FormatDescription=>decoder.Format.ToString()+(UsedDefaultFallback?"；已选麦克风不可用，使用 Windows 默认通信麦克风":"");
    public string Diagnostic { get; private set; }="";
    public string? FailureMessage { get; private set; }
    public string? QualityWarning => capture.ReportedGapFrames > 0
        ? "麦克风报告音频位置缺口，文字已复制，请核对后手动粘贴。" : null;
    public long MetadataAnomalies => capture.MetadataAnomalies;
    public long ReportedGapFrames => capture.ReportedGapFrames;
    public long SamplesSent { get; private set; }
    public string EndpointId { get; }
    public bool UsedDefaultFallback { get; }
    public AudioCapture(string deviceId, Func<byte[],CancellationToken,ValueTask> send, Action<string> fault, Action<float> level,
        RuntimeLog? log=null, string? turnId=null)
    {
        this.log=log;this.turnId=turnId;
        this.send=send;this.fault=fault;this.level=level;
        AudioLog.Write(log,"EnumeratorCreating",turnId);
        using var enumerator = new MMDeviceEnumerator();
        AudioLog.Write(log,"EnumeratorCreated",turnId);
        var opened=AudioEndpointSelection.Open(deviceId,
            id=>OpenEndpoint(enumerator,id),()=>OpenEndpoint(enumerator,""),out bool fallback);
        device=opened.Device;capture=opened.Capture;UsedDefaultFallback=fallback;
        try
        {
            EndpointId=device.ID;
            var format=capture.Format;
            var encoding=format.Encoding;
            if(format is WaveFormatExtensible x)
            {
                if(x.SubFormat==new Guid("00000003-0000-0010-8000-00aa00389b71"))encoding=WaveFormatEncoding.IeeeFloat;
                else if(x.SubFormat==new Guid("00000001-0000-0010-8000-00aa00389b71"))encoding=WaveFormatEncoding.Pcm;
            }
            if(encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat))throw new NotSupportedException("不支持此麦克风采样编码，请在 Windows 声音设置中选择 PCM 格式。");
            decoder=new(new(format.SampleRate,format.Channels,format.BitsPerSample,format.BlockAlign,encoding==WaveFormatEncoding.IeeeFloat?PcmEncoding.Float:PcmEncoding.Integer));
            maxQueuedBytes=checked(format.SampleRate*format.BlockAlign*2);
            AudioLog.Write(log,"FormatValidated",turnId,null,("sampleRate",format.SampleRate),("channels",format.Channels),
                ("bits",format.BitsPerSample),("blockAlign",format.BlockAlign),("encoding",encoding),
                ("endpoint",AudioLog.DeviceKey(EndpointId)),("defaultFallback",fallback));
        }
        catch(Exception e){AudioLog.Write(log,"FormatValidationFailed",turnId,e);if(capture!=null)capture.DisposeAsync().AsTask().GetAwaiter().GetResult();device.Dispose();throw;}
    }
    private (MMDevice Device,NativeWasapiCapture Capture) OpenEndpoint(MMDeviceEnumerator enumerator,string id)
    {
        string stage="FindEndpoint";
        MMDevice? endpoint=null;
        AudioLog.Write(log,"EndpointOpening",turnId,null,("selection",string.IsNullOrEmpty(id)?"CommunicationsDefault":"Explicit"),("endpoint",AudioLog.DeviceKey(id)));
        try
        {
            endpoint=string.IsNullOrEmpty(id)
                ?enumerator.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Communications):enumerator.GetDevice(id);
            stage="ReadEndpointState";
            var state=endpoint.State;
            AudioLog.Write(log,"EndpointFound",turnId,null,("endpoint",AudioLog.DeviceKey(endpoint.ID)),("state",state));
            if(state!=DeviceState.Active)throw new AudioEndpointUnavailableException();
            stage="ActivateAudioClient";
            var native=new NativeWasapiCapture(endpoint,Data,e=>
            {
                if(e!=null&&!abort.IsCancellationRequested)Fail(e is AudioCaptureIntegrityException?e.Message:"麦克风采集已中断，请重新选择设备。",e,"Capture");
                queue.Writer.TryComplete();stopped.TrySetResult();
            },log,turnId);
            return(endpoint,native);
        }
        catch(Exception e){AudioLog.Write(log,"EndpointOpenFailed",turnId,e,("stage",stage),("endpoint",AudioLog.DeviceKey(id)));endpoint?.Dispose();throw;}
    }
    public static List<AudioDevice> Devices()=>Devices(null);
    public static List<AudioDevice> Devices(RuntimeLog? log)
    {
        AudioLog.Write(log,"InventoryStarted");
        try
        {
        using var e=new MMDeviceEnumerator(); var result=new List<AudioDevice>{new("","跟随 Windows 默认通信麦克风")};
        if(log!=null)
        {
            foreach(var role in new[]{Role.Communications,Role.Multimedia,Role.Console})
            {
                try{using var d=e.GetDefaultAudioEndpoint(DataFlow.Capture,role);AudioLog.Write(log,"InventoryDefault",null,null,("role",role),("endpoint",AudioLog.DeviceKey(d.ID)));}
                catch(Exception error){AudioLog.Write(log,"InventoryDefaultFailed",null,error,("role",role));}
            }
            try
            {
                var all=e.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.All);
                AudioLog.Write(log,"InventoryAllStates",null,null,("count",all.Count),("detailsLimit",64));
                foreach(var d in all.Take(64))using(d)
                {
                    try{AudioLog.Write(log,"InventoryEndpointState",null,null,("endpoint",AudioLog.DeviceKey(d.ID)),("state",d.State),("dataFlow","Capture"));}
                    catch(Exception error){AudioLog.Write(log,"InventoryEndpointStateFailed",null,error);}
                }
            }
            catch(Exception error){AudioLog.Write(log,"InventoryAllStatesFailed",null,error);}
        }
        foreach(var d in e.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active))
        {
            using(d)
            {
                string id=d.ID;
                AudioLog.Write(log,"InventoryEndpoint",null,null,("index",result.Count-1),("endpoint",AudioLog.DeviceKey(id)),("state",DeviceState.Active));
                string name;
                try{name=d.FriendlyName;}
                catch(Exception error)when(error is COMException or InvalidOperationException)
                {
                    AudioLog.Write(log,"EndpointNameReadFailed",null,error,("endpoint",AudioLog.DeviceKey(id)));
                    name=$"麦克风 {result.Count}（设备名称暂不可用）";
                }
                result.Add(new(id,name));
            }
        }
        AudioLog.Write(log,"InventoryCompleted",null,null,("activeCount",result.Count-1));
        return result;
        }
        catch(Exception error){AudioLog.Write(log,"InventoryFailed",null,error);throw;}
    }
    public void Start()
    {
        lock(captureSync)
        {
            abort.Token.ThrowIfCancellationRequested();
            if(processing!=null)throw new InvalidOperationException("麦克风已经启动。");
            // Format is validated before any callback. The worker never reads mutable device state.
            processing=Task.Run(Process);
            try{if(stopRequested!=0)capture.RequestStop();capture.Start();}
            catch{Abort();stopped.TrySetResult();throw;}
        }
    }
    private void Data(IntPtr pointer,int frames,bool silent)
    {
        if(abort.IsCancellationRequested||Volatile.Read(ref faulted)!=0)return;
        int bytes=checked(frames*decoder.Format.BlockAlign);
        if(bytes==0)return;
        if(bytes<0||bytes%decoder.Format.BlockAlign!=0){Fail("音频块没有按样本对齐，录音已暂停。");return;}
        int total=Interlocked.Add(ref queuedBytes,bytes);
        if(total>maxQueuedBytes){Interlocked.Add(ref queuedBytes,-bytes);Fail("音频处理积压超过 2 秒，已暂停。中断区间不计为完整识别。");return;}
        byte[] copy=ArrayPool<byte>.Shared.Rent(bytes);
        try{if(silent)decoder.FillSilence(copy.AsSpan(0,bytes));else Marshal.Copy(pointer,copy,0,bytes);}
        catch{Interlocked.Add(ref queuedBytes,-bytes);ArrayPool<byte>.Shared.Return(copy,true);throw;}
        if(!queue.Writer.TryWrite(new(copy,bytes))){Interlocked.Add(ref queuedBytes,-bytes);ArrayPool<byte>.Shared.Return(copy,true);Fail("音频处理队列已满，录音已暂停。");}
    }
    private void Fail(string text,Exception? error=null,string stage="Capture")
    {
        if(Interlocked.Exchange(ref faulted,1)!=0)return;
        FailureMessage=text;
        Diagnostic=$"stage={stage}; format={FormatDescription}; exception={error?.GetType().Name??"None"}; hresult=0x{error?.HResult??0:X8}; pcm_samples={SamplesSent}";
        AudioLog.Write(log,"CaptureFailed",turnId,error,("stage",stage),("pcmSamples",SamplesSent),("queuedBytes",Volatile.Read(ref queuedBytes)));
        // Queue the controller's failure before publishing the stopped event. Delaying
        // this notification on another worker could make an incomplete turn look done.
        try{fault(text);}catch{}
    }
    private async Task Process()
    {
        string stage="Convert";
        try
        {
            var converter=new PcmResampler(decoder.Format.SampleRate);var framer=new PcmFramer();
            await foreach(var block in queue.Reader.ReadAllAsync(abort.Token))
            {
                try
                {
                    stage="Convert";
                    var mono=decoder.Decode(block.Buffer.AsSpan(0,block.Count),out float rms);
                    level(rms);
                    foreach(var frame in framer.Add(converter.Add(mono)))
                    {
                        stage="Upload";await send(frame,abort.Token);SamplesSent+=frame.Length/2;
                    }
                }
                finally{Interlocked.Add(ref queuedBytes,-block.Count);ArrayPool<byte>.Shared.Return(block.Buffer,true);}
            }
            stage="Convert";
            foreach(var frame in framer.Add(converter.Add([],true),true))
            {stage="Upload";await send(frame,abort.Token);SamplesSent+=frame.Length/2;}
        }
        catch(OperationCanceledException)when(abort.IsCancellationRequested){}
        catch(Exception error)
        {
            string message=error is ProviderException?error.Message:stage=="Upload"
                ? "百炼音频上传中断，确认文字已保留。请查看诊断信息。"
                : "麦克风音频转换失败，请更换采样格式或设备，并查看诊断信息。";
            Fail(message,error,stage);
            Abort();
        }
        finally
        {
            while(queue.Reader.TryRead(out var b)){Interlocked.Add(ref queuedBytes,-b.Count);ArrayPool<byte>.Shared.Return(b.Buffer,true);}
            AudioLog.Write(log,"ProcessingStopped",turnId,null,("pcmSamples",SamplesSent),("faulted",Volatile.Read(ref faulted)!=0),("cancelled",abort.IsCancellationRequested));
        }
    }
    public async Task StopAsync(CancellationToken token)
    {
        RequestStop();
        await stopped.Task.WaitAsync(token);
        queue.Writer.TryComplete();
        if(processing!=null)await processing.WaitAsync(token);
    }
    public void RequestStop(){if(Interlocked.Exchange(ref stopRequested,1)==0)capture.RequestStop();}
    public void Abort(){Interlocked.Exchange(ref stopRequested,1);abort.Cancel();capture.Abort();queue.Writer.TryComplete();}
    public async ValueTask DisposeAsync(){Abort();try{if(processing!=null)await processing.WaitAsync(TimeSpan.FromSeconds(1));}catch{}await capture.DisposeAsync();device.Dispose();abort.Dispose();}
}
