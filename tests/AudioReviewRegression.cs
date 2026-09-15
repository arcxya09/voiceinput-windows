using RealtimeTranscription.Core;
using RealtimeTranscription.Desktop;
using System.Runtime.InteropServices;

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
        await test("静音设备包经PCM解码与16kHz转换后仍为零电平", () => Sync(() =>
        {
            foreach (var format in new[]
            {
                new PcmInputFormat(48000,2,8,2,PcmEncoding.Integer),
                new PcmInputFormat(48000,2,16,4,PcmEncoding.Integer),
                new PcmInputFormat(48000,2,24,6,PcmEncoding.Integer),
                new PcmInputFormat(48000,2,32,8,PcmEncoding.Integer),
                new PcmInputFormat(48000,2,32,8,PcmEncoding.Float),
                new PcmInputFormat(48000,2,64,16,PcmEncoding.Float)
            })
            {
                var decoder = new PcmDecoder(format);
                int bytes = 480 * format.BlockAlign;
                var storage = Enumerable.Repeat((byte)0x5a, bytes + 2).ToArray();
                decoder.FillSilence(storage.AsSpan(1, bytes));
                var decoded = decoder.Decode(storage.AsSpan(1, bytes), out float rms);
                Check(decoded.Length == 480 && decoded.All(sample => sample == 0) && rms == 0,
                    $"{format}: silent capture became a voice signal");
                var pcm16 = new PcmResampler(format.SampleRate).Add(decoded, true);
                Check(pcm16.Length == 160 && pcm16.All(sample => sample == 0),
                    $"{format}: silent capture sent nonzero PCM to ASR");
                Check(storage[0] == 0x5a && storage[^1] == 0x5a, "Wrote beyond the native packet's rented buffer slice");
            }
        }));
        await test("可用的已选设备保持选择，默认模式只打开默认通信设备", () => Sync(() =>
        {
            var selected = new object(); var defaultDevice = new object();
            int selectedCalls = 0, defaultCalls = 0;
            object OpenSelected(string id) { Check(id == "saved-device", "Selected endpoint changed"); selectedCalls++; return selected; }
            object OpenDefault() { defaultCalls++; return defaultDevice; }
            Check(ReferenceEquals(AudioEndpointSelection.Open("saved-device", OpenSelected, OpenDefault, out bool fallback), selected)
                && !fallback && selectedCalls == 1 && defaultCalls == 0, "An available selected microphone was substituted");
            Check(ReferenceEquals(AudioEndpointSelection.Open("", OpenSelected, OpenDefault, out fallback), defaultDevice)
                && !fallback && selectedCalls == 1 && defaultCalls == 1, "Default mode tried a stored endpoint");
        }));
        await test("仅已断开、禁用或明确失效的选定麦克风回退默认一次", () => Sync(() =>
        {
            foreach (Exception unavailable in new Exception[]
            {
                new AudioEndpointUnavailableException(),
                new COMException("Endpoint no longer exists", unchecked((int)0x80070490)),
                new COMException("Endpoint invalidated during activation", unchecked((int)0x88890004))
            })
            {
                int selectedCalls = 0, defaultCalls = 0; var defaultDevice = new object();
                object OpenSelected(string id) { selectedCalls++; throw unavailable; }
                object OpenDefault() { defaultCalls++; return defaultDevice; }
                var result = AudioEndpointSelection.Open("saved-device", OpenSelected, OpenDefault, out bool fallback);
                Check(ReferenceEquals(result, defaultDevice) && fallback && selectedCalls == 1 && defaultCalls == 1,
                    "An unavailable selected endpoint did not fall back exactly once");
            }
        }));
        await test("麦克风权限、占用、格式和未知故障保留原异常且不切换设备", () => Sync(() =>
        {
            foreach (Exception failure in new Exception[]
            {
                new COMException("Access denied", unchecked((int)0x80070005)),
                new COMException("Device busy", unchecked((int)0x8889000a)),
                new COMException("Unsupported format", unchecked((int)0x88890008)),
                new COMException("Audio service stopped", unchecked((int)0x88890010)),
                new COMException("Invalid argument", unchecked((int)0x80070057)),
                new ExternalException("Unrelated subsystem", unchecked((int)0x80070490)),
                new IOException("Unrelated I/O failure")
            })
            {
                int selectedCalls = 0, defaultCalls = 0; Exception? observed = null;
                object OpenSelected(string id) { selectedCalls++; throw failure; }
                object OpenDefault() { defaultCalls++; return new object(); }
                try { AudioEndpointSelection.Open("saved-device", OpenSelected, OpenDefault, out _); }
                catch (Exception error) { observed = error; }
                Check(ReferenceEquals(observed, failure) && selectedCalls == 1 && defaultCalls == 0,
                    "A failed microphone was silently replaced or its original error was lost");
            }
        }));
        await test("回退默认麦克风失败不会重试，也不丢失默认设备的原异常", () => Sync(() =>
        {
            int defaultCalls = 0;
            var unavailable = new COMException("Selected endpoint missing", unchecked((int)0x80070490));
            var defaultError = new COMException("No default endpoint", unchecked((int)0x80070490));
            object OpenSelected(string id) => throw unavailable;
            object OpenDefault() { defaultCalls++; throw defaultError; }
            Exception? observed = null;
            try { AudioEndpointSelection.Open("saved-device", OpenSelected, OpenDefault, out _); }
            catch (Exception error) { observed = error; }
            Check(ReferenceEquals(observed, defaultError) && defaultCalls == 1, "Default microphone failure was retried or replaced");
        }));
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
