namespace RealtimeTranscription.Core;

/// <summary>A turn-owned audio source; construction must not start recording.</summary>
public interface IAudioCapture : IAsyncDisposable
{
    string EndpointId { get; }
    string FormatDescription { get; }
    string Diagnostic { get; }
    string? FailureMessage { get; }
    string? QualityWarning => null;
    long SamplesSent { get; }
    void Start();
    void RequestStop();
    void Abort();
    Task StopAsync(CancellationToken token);
}
