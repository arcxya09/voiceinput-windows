namespace RealtimeTranscription.Core;

public sealed class AudioCaptureIntegrityException(string message) : Exception(message) { }

public readonly record struct AudioPacketDecision(int FramesToKeep, bool ReachedStop);

/// <summary>Validates native capture continuity and clips packets to a QPC stop boundary.
/// Timestamps use WASAPI's 100-nanosecond QPC units, not wall-clock time.</summary>
public sealed class AudioPacketTimeline
{
    public const long TicksPerSecond = 10_000_000;
    private readonly int sampleRate;
    private long? nextDevicePosition;
    private long lastTimestamp;
    private double lastEnd;
    public AudioPacketTimeline(int sampleRate)
    {
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.sampleRate = sampleRate;
    }
    public bool Covers(long stopAt) => nextDevicePosition.HasValue && lastEnd >= stopAt;
    public AudioPacketDecision Inspect(int frames, long devicePosition, long timestamp,
        bool discontinuity, bool timestampError, long? stopAt = null)
    {
        if (frames <= 0 || devicePosition < 0 || timestamp < 0 || timestampError)
            throw new AudioCaptureIntegrityException("麦克风采样时间无效，本轮音频可能不完整，请重新录音。");
        if (nextDevicePosition.HasValue && (discontinuity || devicePosition != nextDevicePosition.Value || timestamp < lastTimestamp))
            throw new AudioCaptureIntegrityException("麦克风采样出现中断或丢帧，确认文字已保留，本轮不自动输入。");
        // Some devices mark the first packet discontinuous as the stream starts.
        // Establish its position once; subsequent flags and position jumps are errors.
        nextDevicePosition = checked(devicePosition + frames);
        lastTimestamp = timestamp;
        lastEnd = timestamp + frames * (double)TicksPerSecond / sampleRate;
        if (!stopAt.HasValue) return new(frames, false);
        long delta = stopAt.Value - timestamp;
        int keep = delta <= 0 ? 0 : (int)Math.Min(frames, Math.Ceiling(delta * (double)sampleRate / TicksPerSecond));
        return new(keep, lastEnd >= stopAt.Value);
    }
}
