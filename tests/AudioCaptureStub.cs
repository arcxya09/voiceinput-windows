using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;

// Native capture is outside this portable review. No test may start a microphone.
public sealed class AudioCapture : IAudioCapture
{
    public AudioCapture(string device, Func<byte[], CancellationToken, ValueTask> send, Action<string> fault, Action<float> level) => throw new NotSupportedException("Native audio is excluded from this review harness.");
    public string EndpointId => "review-stub";
    public string FormatDescription => "Native audio not tested";
    public string Diagnostic => "Native audio not tested";
    public string? FailureMessage => null;
    public long SamplesSent => 0;
    public void Start() => throw new NotSupportedException();
    public void RequestStop() { }
    public void Abort() { }
    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
