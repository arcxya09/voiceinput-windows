namespace RealtimeTranscription.Core;

/// <summary>Streaming windowed-sinc resampling. Positions and filter history survive callback boundaries.</summary>
public sealed class PcmResampler
{
    private readonly int inputRate;
    private readonly int outputRate;
    private readonly List<float> samples = [];
    private long baseIndex, inputCount, outputCount;
    private bool finished;
    private const int Half = 24;
    public PcmResampler(int inputRate, int outputRate = 16000)
    {
        if (inputRate < 8000 || inputRate > 384000 || outputRate < 8000) throw new ArgumentOutOfRangeException(nameof(inputRate));
        this.inputRate = inputRate; this.outputRate = outputRate;
    }
    public short[] Add(ReadOnlySpan<float> mono, bool finish = false)
    {
        if (finished) throw new InvalidOperationException("Resampler already finished.");
        foreach (var x in mono) samples.Add(float.IsFinite(x) ? Math.Clamp(x, -1, 1) : 0);
        inputCount += mono.Length;
        var result = new List<short>();
        long target = (long)Math.Floor(inputCount * (double)outputRate / inputRate);
        double cutoff = Math.Min(1.0, (double)outputRate / inputRate) * 0.94;
        while (outputCount < target)
        {
            double position = outputCount * (double)inputRate / outputRate;
            long center = (long)Math.Floor(position);
            if (!finish && center + Half >= inputCount) break;
            double sum = 0, norm = 0;
            for (long k = center - Half + 1; k <= center + Half; k++)
            {
                double d = position - k;
                double sinc = Math.Abs(d) < 1e-12 ? cutoff : Math.Sin(Math.PI * cutoff * d) / (Math.PI * d);
                double window = 0.5 + 0.5 * Math.Cos(Math.PI * d / Half);
                double weight = sinc * window;
                norm += weight;
                long index = k - baseIndex;
                if (index >= 0 && index < samples.Count && k < inputCount) sum += samples[(int)index] * weight;
            }
            double value = norm == 0 ? 0 : sum / norm;
            result.Add((short)Math.Clamp((int)Math.Round(value * 32767), short.MinValue, short.MaxValue));
            outputCount++;
        }
        long retainFrom = Math.Max(0, (long)Math.Floor(outputCount * (double)inputRate / outputRate) - Half);
        int remove = (int)Math.Min(samples.Count, retainFrom - baseIndex);
        if (remove > 0) { samples.RemoveRange(0, remove); baseIndex += remove; }
        finished = finish;
        return result.ToArray();
    }
}

public sealed class PcmFramer
{
    private readonly List<byte> pending = [];
    public List<byte[]> Add(ReadOnlySpan<short> samples, bool finish = false)
    {
        foreach (short value in samples) { pending.Add((byte)value); pending.Add((byte)(value >> 8)); }
        var frames = new List<byte[]>();
        while (pending.Count >= 3200) { frames.Add(pending.GetRange(0, 3200).ToArray()); pending.RemoveRange(0, 3200); }
        if (finish && pending.Count > 0) { frames.Add(pending.ToArray()); pending.Clear(); }
        return frames;
    }
}
