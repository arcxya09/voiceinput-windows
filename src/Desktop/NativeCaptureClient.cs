using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RealtimeTranscription.Desktop;

// The adapter keeps COM service acquisition, packet leases and disposal on the
// capture thread. The same loop can be exercised without a physical microphone.
internal interface INativeCaptureClient : IDisposable
{
    WaveFormat MixFormat { get; }
    int BufferSize { get; }
    long DefaultDevicePeriod { get; }
    void Initialize(WaveFormat format);
    void SetEventHandle(IntPtr handle);
    void GetCaptureService();
    int GetNextPacketSize();
    IntPtr GetBuffer(out int frames, out AudioClientBufferFlags flags, out long position, out long timestamp);
    void ReleaseBuffer(int frames);
    void Start();
    void Stop();
}

internal sealed class NativeCaptureClient(AudioClient client) : INativeCaptureClient
{
    private AudioCaptureClient? capture;
    public WaveFormat MixFormat => client.MixFormat;
    public int BufferSize => client.BufferSize;
    public long DefaultDevicePeriod => client.DefaultDevicePeriod;
    public void Initialize(WaveFormat format) => client.Initialize(AudioClientShareMode.Shared,
        AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
        0, 0, format, Guid.Empty);
    public void SetEventHandle(IntPtr handle) => client.SetEventHandle(handle);
    public void GetCaptureService() => capture = client.AudioCaptureClient;
    public int GetNextPacketSize() => capture!.GetNextPacketSize();
    public IntPtr GetBuffer(out int frames, out AudioClientBufferFlags flags, out long position, out long timestamp)
        => capture!.GetBuffer(out frames, out flags, out position, out timestamp);
    public void ReleaseBuffer(int frames) => capture!.ReleaseBuffer(frames);
    public void Start() => client.Start();
    public void Stop() => client.Stop();
    public void Dispose() => client.Dispose(); // NAudio owns its cached capture service.
}
