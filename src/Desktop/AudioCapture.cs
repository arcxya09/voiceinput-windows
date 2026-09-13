using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

public record AudioDevice(string Id, string Name);
public sealed class AudioCapture : IAsyncDisposable
{
    private record Block(byte[] Buffer, int Count);
    private readonly NativeWasapiCapture capture;
    private readonly MMDevice device;
    private readonly Channel<Block> queue = Channel.CreateBounded<Block>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource abort = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<byte[],CancellationToken,ValueTask> send;
    private readonly Action<string> fault;
    private readonly Action<float> level;
    private Task? processing;
    private int queuedBytes, faulted;
    private int stopRequested;
    private readonly object captureSync=new();
    private readonly PcmDecoder decoder;
    private readonly int maxQueuedBytes;
    public string FormatDescription=>decoder.Format.ToString();
    public string Diagnostic { get; private set; }="";
    public string? FailureMessage { get; private set; }
    public long SamplesSent { get; private set; }
    public string EndpointId { get; }
    public AudioCapture(string deviceId, Func<byte[],CancellationToken,ValueTask> send, Action<string> fault, Action<float> level)
    {
        using var enumerator = new MMDeviceEnumerator();
        device = string.IsNullOrEmpty(deviceId) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : enumerator.GetDevice(deviceId);
        EndpointId=device.ID;
        try
        {
            capture = new NativeWasapiCapture(device,Data,e=>
            {
                if(e!=null&&!abort.IsCancellationRequested)Fail(e is AudioCaptureIntegrityException?e.Message:"麦克风采集已中断，请重新选择设备。",e,"Capture");
                queue.Writer.TryComplete();stopped.TrySetResult();
            });
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
        }
        catch{if(capture!=null)capture.DisposeAsync().AsTask().GetAwaiter().GetResult();device.Dispose();throw;}
        this.send=send;this.fault=fault;this.level=level;
    }
    public static List<AudioDevice> Devices()
    {
        using var e=new MMDeviceEnumerator(); var result=new List<AudioDevice>{new("","跟随 Windows 默认通信麦克风")};
        foreach(var d in e.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active)){result.Add(new(d.ID,d.FriendlyName));d.Dispose();}return result;
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
        try{if(silent)Array.Clear(copy,0,bytes);else Marshal.Copy(pointer,copy,0,bytes);}
        catch{Interlocked.Add(ref queuedBytes,-bytes);ArrayPool<byte>.Shared.Return(copy,true);throw;}
        if(!queue.Writer.TryWrite(new(copy,bytes))){Interlocked.Add(ref queuedBytes,-bytes);ArrayPool<byte>.Shared.Return(copy,true);Fail("音频处理队列已满，录音已暂停。");}
    }
    private void Fail(string text,Exception? error=null,string stage="Capture")
    {
        if(Interlocked.Exchange(ref faulted,1)!=0)return;
        FailureMessage=text;
        Diagnostic=$"stage={stage}; format={FormatDescription}; exception={error?.GetType().Name??"None"}; hresult=0x{error?.HResult??0:X8}; pcm_samples={SamplesSent}";
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
