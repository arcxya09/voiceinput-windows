using System.Buffers.Binary;

namespace RealtimeTranscription.Core;

/// <summary>Turn-local 16 kHz mono PCM. Never persists audio or reviews a truncated prefix.</summary>
public sealed class AsrReviewBuffer : IDisposable
{
    public const int MaxSeconds = 180;
    public const int MaxBytes = MaxSeconds * 32000;
    private readonly object sync = new();
    private readonly List<byte[]> chunks = [];
    private int length;
    private bool closed;
    public bool Overflowed { get; private set; }
    public void Add(byte[] pcm)
    {
        lock (sync)
        {
            if (closed) return;
            if (pcm.Length % 2 != 0 || length + pcm.Length > MaxBytes)
            { Overflowed = true; Clear(); closed = true; return; }
            chunks.Add(pcm.ToArray()); length += pcm.Length;
        }
    }
    public byte[]? TakeWav()
    {
        lock (sync)
        {
            if (closed || length == 0) { Dispose(); return null; }
            var wav = new byte[44 + length];
            "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
            "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16000); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32000);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
            "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), length);
            int offset = 44;
            foreach (var chunk in chunks) { chunk.CopyTo(wav, offset); offset += chunk.Length; }
            Clear(); closed = true; return wav;
        }
    }
    private void Clear() { foreach (var chunk in chunks) Array.Clear(chunk); chunks.Clear(); length = 0; }
    public void Dispose() { lock (sync) { Clear(); closed = true; } }
}

public sealed partial class TranscriptEngine
{
    public bool ApplyAsrReview(string text, long expectedRevision)
    {
        if (Session.Revision != expectedRevision || Session.WholePolishState != "None" || Session.Gaps.Count > 0
            || tasks.Values.Any(t => !t.Sealed) || pending.Count > 0 || Segments.Any(s => s.UserLocked || s.AsrState != AsrState.Confirmed)
            || string.IsNullOrWhiteSpace(text) || text.Length > 20000) return false;
        var originals = Segments.Where(s => s.OutputState == OutputState.Published).ToArray();
        if (originals.Length == 0) return false;
        // Retain streaming originals for inspection, but only the reviewed ASR is
        // published, polished and learned. This is not a human correction signal.
        foreach (var segment in originals)
            Put(segment with { OutputState = OutputState.Suppressed, SupersededByAsrReview = true, Reason = "已由整段语音复核替代", Revision = segment.Revision + 1 });
        string taskId = JsonCodec.Id();
        StartTask(taskId, originals.SelectMany(s => s.InjectedTerms).Distinct(StringComparer.Ordinal).ToArray());
        Receive(new("result-generated", taskId, 1, text.Trim(), true, BeginMs: 0,
            EndMs: originals.Max(s => s.EndMs)), false, false, [], false);
        SealTask(taskId);
        return true;
    }
}
