namespace RealtimeTranscription.Core;

public sealed class AudioCaptureIntegrityException(string message) : Exception(message) { }

[Flags]
public enum AudioPacketAnomaly
{
    None = 0, Discontinuity = 1, RepeatedPosition = 2, PositionJump = 4,
    InvalidPosition = 8, TimestampError = 16, NonMonotonicTimestamp = 32,
    ImplausibleTimestamp = 64
}

public readonly record struct AudioPacketDecision(int FramesToKeep, bool ReachedStop,
    AudioPacketAnomaly Anomaly = AudioPacketAnomaly.None, long? ExpectedPosition = null,
    long? PositionDelta = null, long? TimestampDelta = null);

/// <summary>Clips reliable WASAPI timestamps (100 ns QPC units). Metadata glitches
/// preserve PCM and switch the turn to stopping the device before draining its buffer.</summary>
public sealed class AudioPacketTimeline
{
    public const long TicksPerSecond = 10_000_000;
    private readonly int sampleRate;
    private long? nextDevicePosition, lastDevicePosition, lastTimestamp;
    private double lastEnd;
    public bool RequiresDeviceStop { get; private set; }
    public long ReportedGapFrames { get; private set; }
    public AudioPacketTimeline(int sampleRate)
    {
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.sampleRate = sampleRate;
    }
    public void UseDeviceStop() => RequiresDeviceStop = true;
    public bool Covers(long stopAt) => !RequiresDeviceStop && lastTimestamp.HasValue && lastEnd >= stopAt;
    public AudioPacketDecision Inspect(int frames, long devicePosition, long timestamp,
        bool discontinuity, bool timestampError, long? stopAt = null,
        long? observedAt = null, long maxTimestampAge = TicksPerSecond)
    {
        if (frames <= 0) throw new AudioCaptureIntegrityException("麦克风返回了无效的音频包长度。");
        var anomaly = AudioPacketAnomaly.None;
        long? expected = nextDevicePosition;
        long? delta = devicePosition >= 0 && expected.HasValue ? devicePosition - expected.Value : null;
        long? timeDelta = timestamp >= 0 && lastTimestamp is >= 0 ? timestamp - lastTimestamp.Value : null;
        bool invalidPosition = devicePosition < 0 || devicePosition > long.MaxValue - frames;
        if (invalidPosition) anomaly |= AudioPacketAnomaly.InvalidPosition;
        // The first discontinuity is a normal stream-start marker on many devices.
        if (lastTimestamp.HasValue && discontinuity) anomaly |= AudioPacketAnomaly.Discontinuity;
        if (lastDevicePosition.HasValue && !invalidPosition)
        {
            if (devicePosition == lastDevicePosition.Value) anomaly |= AudioPacketAnomaly.RepeatedPosition;
            else if (delta is not null and not 0)
            {
                anomaly |= AudioPacketAnomaly.PositionJump;
                if (delta > 0 && !timestampError)
                    ReportedGapFrames = (long)Math.Min(long.MaxValue, (decimal)ReportedGapFrames + delta.Value);
            }
        }
        if (timestampError || timestamp < 0) anomaly |= AudioPacketAnomaly.TimestampError;
        if (lastTimestamp.HasValue && timestamp <= lastTimestamp.Value) anomaly |= AudioPacketAnomaly.NonMonotonicTimestamp;
        if (observedAt.HasValue && ((double)timestamp - observedAt.Value > TicksPerSecond / 100 ||
            (double)observedAt.Value - timestamp > Math.Max(TicksPerSecond, maxTimestampAge)))
            anomaly |= AudioPacketAnomaly.ImplausibleTimestamp;
        if (anomaly != AudioPacketAnomaly.None) RequiresDeviceStop = true;
        if (invalidPosition) nextDevicePosition = null;
        else if (devicePosition == lastDevicePosition && expected.HasValue)
            nextDevicePosition = expected.Value <= long.MaxValue - frames ? expected.Value + frames : null;
        else nextDevicePosition = devicePosition + frames;
        lastDevicePosition = invalidPosition ? null : devicePosition;
        lastTimestamp = timestamp;
        lastEnd = timestamp + frames * (double)TicksPerSecond / sampleRate;
        if (RequiresDeviceStop || !stopAt.HasValue) return new(frames, false, anomaly, expected, delta, timeDelta);
        double remaining = (double)stopAt.Value - timestamp;
        int keep = remaining <= 0 ? 0 : (int)Math.Min(frames, Math.Ceiling(remaining * sampleRate / TicksPerSecond));
        return new(keep, lastEnd >= stopAt.Value, anomaly, expected, delta, timeDelta);
    }
}
