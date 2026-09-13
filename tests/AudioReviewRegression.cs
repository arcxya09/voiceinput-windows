using RealtimeTranscription.Core;

internal static class AudioReviewRegression
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static Task Sync(Action action) { action(); return Task.CompletedTask; }
        static void Incomplete(Action action)
        {
            try { action(); } catch (AudioCaptureIntegrityException) { return; }
            throw new Exception("Incomplete native audio was accepted as continuous");
        }
        await test("原生采样首包标记兼容，后续位置连续才能接受", () => Sync(() =>
        {
            var timeline = new AudioPacketTimeline(48000);
            Check(timeline.Inspect(480, 0, 1_000_000, true, false).FramesToKeep == 480, "First startup flag");
            Check(timeline.Inspect(480, 480, 1_100_000, false, false).FramesToKeep == 480, "Continuous packet");
            Check(timeline.Covers(1_200_000) && !timeline.Covers(1_200_001), "Observed native endpoint");
        }));
        await test("原生掉帧、重叠与非首包中断标记阻止完整输入", () => Sync(() =>
        {
            foreach (var next in new[] { (Position: 960L, Flag: false), (Position: 240L, Flag: false), (Position: 480L, Flag: true) })
            {
                var timeline = new AudioPacketTimeline(48000);
                timeline.Inspect(480, 0, 1_000_000, false, false);
                Incomplete(() => timeline.Inspect(480, next.Position, 1_100_000, next.Flag, false));
            }
        }));
        await test("无效或倒退的原生时钟不能用于收尾边界", () => Sync(() =>
        {
            Incomplete(() => new AudioPacketTimeline(48000).Inspect(480, 0, 1_000_000, false, true));
            var timeline = new AudioPacketTimeline(48000);
            timeline.Inspect(480, 0, 1_000_000, false, false);
            Incomplete(() => timeline.Inspect(480, 480, 900_000, false, false));
        }));
        await test("松键后读取覆盖边界的设备尾包，排除松键后样本", () => Sync(() =>
        {
            var timeline = new AudioPacketTimeline(48000);
            var first = timeline.Inspect(480, 0, 1_000_000, false, false, 1_150_000);
            Check(first.FramesToKeep == 480 && !first.ReachedStop && !timeline.Covers(1_150_000), "Must wait for tail packet");
            var tail = timeline.Inspect(480, 480, 1_100_000, false, false, 1_150_000);
            Check(tail.FramesToKeep == 240 && tail.ReachedStop, "Keep exact 5 ms of tail");
            Check(first.FramesToKeep + tail.FramesToKeep == 720, "All 15 ms before release preserved");
        }));
        await test("停止边界精确到样本，边界外包不上传", () => Sync(() =>
        {
            var atStart = new AudioPacketTimeline(48000).Inspect(480, 0, 1_000_000, false, false, 1_000_000);
            Check(atStart.FramesToKeep == 0 && atStart.ReachedStop, "Nothing after release");
            var fractional = new AudioPacketTimeline(48000).Inspect(480, 0, 1_000_000, false, false, 1_000_001);
            Check(fractional.FramesToKeep == 1 && fractional.ReachedStop, "Sample beginning before boundary");
            var atEnd = new AudioPacketTimeline(48000).Inspect(480, 0, 1_000_000, false, false, 1_100_000);
            Check(atEnd.FramesToKeep == 480 && atEnd.ReachedStop, "Full last packet");
        }));
        await test("收尾期间设备包跳跃仍报告缺口", () => Sync(() =>
        {
            var timeline = new AudioPacketTimeline(48000);
            timeline.Inspect(480, 0, 1_000_000, false, false);
            Incomplete(() => timeline.Inspect(480, 960, 1_200_000, false, false, 1_150_000));
        }));
        await test("44.1至384kHz降采样保持语音频段并抑制混叠", () => Sync(() =>
        {
            foreach (int rate in new[] { 44100, 48000, 96000, 192000, 384000 })
            {
                static double Rms(short[] signal) => Math.Sqrt(signal.Skip(64).Take(signal.Length - 128).Average(s => Math.Pow(s / 32767.0, 2)));
                double Tone(int hz)
                {
                    var input = Enumerable.Range(0, rate / 10).Select(i => (float)(.5 * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();
                    return Rms(new PcmResampler(rate).Add(input, true));
                }
                double pass = Tone(1000), rejected = Tone(10000);
                Check(Math.Abs(pass - .5 / Math.Sqrt(2)) < .005, $"{rate}: speech passband altered");
                Check(rejected / pass < .005, $"{rate}: 10 kHz noise aliases into speech ({20 * Math.Log10(rejected / pass):F1} dB)");
            }
        }));
        await test("高采样率随机分块与短尾的输出连续且数量准确", () => Sync(() =>
        {
            foreach (int rate in new[] { 192000, 384000 })
            {
                var input = Enumerable.Range(0, rate / 20 + 17).Select(i => (float)(.3 * Math.Sin(2 * Math.PI * 6000 * i / rate))).ToArray();
                var reference = new PcmResampler(rate).Add(input, true);
                var stream = new PcmResampler(rate); var chunks = new List<short>(); var random = new Random(92);
                for (int offset = 0; offset < input.Length;)
                {
                    int size = Math.Min(random.Next(1, 901), input.Length - offset);
                    chunks.AddRange(stream.Add(input.AsSpan(offset, size))); offset += size;
                }
                chunks.AddRange(stream.Add([], true));
                Check(reference.Length == input.Length * 16000L / rate, "Exact duration");
                Check(reference.SequenceEqual(chunks), "Callback boundaries changed output");
            }
        }));
    }
}
