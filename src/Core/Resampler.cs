namespace RealtimeTranscription.Core;

/// <summary>Streaming windowed-sinc resampling. Positions and filter history survive callback boundaries.</summary>
public sealed class PcmResampler
{
    private readonly int inputRate;
    private readonly int outputRate;
    private readonly List<float> samples = [];
    private long baseIndex, inputCount, outputCount;
    private bool finished;
    private readonly int half;
    private readonly Dictionary<int,(double[] Weights,double Norm)> kernels = [];
    public PcmResampler(int inputRate, int outputRate = 16000)
    {
        if (inputRate < 8000 || inputRate > 384000 || outputRate < 8000) throw new ArgumentOutOfRangeException(nameof(inputRate));
        this.inputRate = inputRate; this.outputRate = outputRate;
        // Keep at least the original 48 kHz filter's time span when downsampling
        // high-rate devices. A fixed 48-tap kernel aliases 192/384 kHz input.
        half = Math.Max(24, (int)Math.Ceiling(8.0 * inputRate / outputRate));
    }
    public short[] Add(ReadOnlySpan<float> mono, bool finish = false)
    {
        if (finished) throw new InvalidOperationException("Resampler already finished.");
        foreach (var x in mono) samples.Add(float.IsFinite(x) ? Math.Clamp(x, -1, 1) : 0);
        inputCount += mono.Length;
        var result = new List<short>();
        long target = inputCount * outputRate / inputRate;
        while (outputCount < target)
        {
            long numerator = outputCount * inputRate;
            long center = numerator / outputRate;
            if (!finish && center + half >= inputCount) break;
            var kernel = Kernel((int)(numerator % outputRate));
            double sum = 0;
            for (int tap = 0; tap < kernel.Weights.Length; tap++)
            {
                long k = center - half + 1 + tap;
                long index = k - baseIndex;
                if (index >= 0 && index < samples.Count && k < inputCount) sum += samples[(int)index] * kernel.Weights[tap];
            }
            double value = kernel.Norm == 0 ? 0 : sum / kernel.Norm;
            result.Add((short)Math.Clamp((int)Math.Round(value * 32767), short.MinValue, short.MaxValue));
            outputCount++;
        }
        long retainFrom = Math.Max(0, outputCount * inputRate / outputRate - half);
        int remove = (int)Math.Min(samples.Count, retainFrom - baseIndex);
        if (remove > 0) { samples.RemoveRange(0, remove); baseIndex += remove; }
        finished = finish;
        return result.ToArray();
    }
    private (double[] Weights,double Norm) Kernel(int phase)
    {
        if (kernels.TryGetValue(phase, out var cached)) return cached;
        double fraction = (double)phase / outputRate;
        double cutoff = Math.Min(1.0, (double)outputRate / inputRate) * 0.94;
        var weights = new double[2 * half];
        double norm = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            double d = fraction - (i - half + 1);
            double sinc = Math.Abs(d) < 1e-12 ? cutoff : Math.Sin(Math.PI * cutoff * d) / (Math.PI * d);
            weights[i] = sinc * (0.5 + 0.5 * Math.Cos(Math.PI * d / half));
            norm += weights[i];
        }
        // Common 44.1/48/96/192/384 kHz formats use at most 160 phases.
        // Bound memory for unusual custom rates rather than caching every phase.
        var kernel = (weights, norm);
        if (kernels.Count < 512) kernels[phase] = kernel;
        return kernel;
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
