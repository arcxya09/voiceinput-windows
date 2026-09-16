using System.Runtime.InteropServices;
using System.Text.Json;
using RealtimeTranscription.Core;
using RealtimeTranscription.Desktop;
using RealtimeTranscription.Infrastructure;

static class ControllerLogRegression
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private const string Secret = "LOG_SECRET_";
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("运行日志：启动 COM 失败在后续麦克风测试失败后仍可导出", async () =>
        {
            var start = new Turn { ConstructionFailure = new COMException(Secret + "START", unchecked((int)0x80070005)) };
            var microphoneTest = new Turn { ConstructionFailure = new COMException(Secret + "MIC_TEST", unchecked((int)0x80070490)) };
            await using var f = await Fixture.Create(start, microphoneTest);
            Check(!await f.Start().WaitAsync(Budget), "COM 构造失败仍报告启动成功。");
            var before = await f.Export();
            var original = StartFailure(before, "OpeningMicrophone");
            Check(HasMetadata(before, "COMException", 0x80070005), "首次启动日志缺少 COM 类型或 HRESULT。");

            string result = await f.App.TestMicrophoneAsync("scripted-microphone").WaitAsync(Budget);
            Check(result.Contains("0x80070490"), "后续麦克风测试没有触发预期的独立故障。");
            var after = await f.Export();
            Check(after.Any(x => x.GetRawText() == original.GetRawText()), "后续麦克风测试覆盖或删除了已导出的启动失败记录。");
            Check(after.Any(x => IsEvent(x, "Diagnostic") && StringField(x, "Stage") == "MicrophoneTestFailed"), "后续麦克风测试没有追加独立诊断。");
            Check(HasMetadata(after, "COMException", 0x80070490), "后续故障的独立 HRESULT 未保存。");
            AssertPrivate(before.Concat(after));
        });

        await test("运行日志：WASAPI 启动内层 HRESULT 与清理异常同时保留且异常消息脱敏", async () =>
        {
            var turn = new Turn
            {
                StartFailure = new InvalidOperationException(Secret + "WRAPPER", new COMException(Secret + "INNER", unchecked((int)0x88890008))),
                DisposeFailure = new COMException(Secret + "CLEANUP", unchecked((int)0x80004005))
            };
            await using var f = await Fixture.Create(turn);
            Check(!await f.Start().WaitAsync(Budget), "WASAPI 启动失败仍报告启动成功。");
            var entries = await f.Export();
            StartFailure(entries, "StartingMicrophone");
            Check(HasMetadata(entries, "InvalidOperationException", 0x80131509), "外层异常类型及 HRESULT 未保存。");
            Check(HasMetadata(entries, "COMException", 0x88890008), "真实 WASAPI 内层异常类型及 HRESULT 丢失。");
            Check(HasMetadata(entries, "COMException", 0x80004005), "清理异常元数据丢失。");
            Check((await turn.Capture.Task.WaitAsync(Budget)).Disposed, "日志路径阻止了失败设备的清理。");
            AssertPrivate(entries);
        });

        await test("运行日志：真实识别收尾及投递状态只记录计数，不写正文、Key 或服务端原文", async () =>
        {
            var turn = new Turn();
            turn.Socket.FinalText = Secret + "PRIVATE_TRANSCRIPT";
            await using var f = await Fixture.Create(turn);
            Check(await f.Start().WaitAsync(Budget), "正常识别未启动。");
            var capture = await turn.Capture.Task.WaitAsync(Budget);
            await capture.EmitFrame();
            await turn.Socket.FirstPcm.Task.WaitAsync(Budget);
            await f.App.StopAsync(false).WaitAsync(Budget);
            var snapshot = await f.App.SnapshotAsync();
            Check(TranscriptText.Render(snapshot) == turn.Socket.FinalText, "未通过真实识别收尾路径收到预期正文。");
            await f.App.SetDeliveryAsync("Copied", Secret + "DELIVERY_REASON", accepted: 2, turnId: f.TurnId);

            var entries = await f.Export();
            var recognized = entries.Single(x => IsEvent(x, "RecognitionFinal"));
            Check(NumberField(recognized, "CharacterCount") == turn.Socket.FinalText.Length, "识别日志未记录正确的字符计数。");
            Check(NumberField(recognized, "Sequence") == 1, "识别日志未保留正确的句子序号。");
            var delivered = entries.Last(x => IsEvent(x, "DeliveryChanged"));
            Check(StringField(delivered, "State") == "Copied" && NumberField(delivered, "AcceptedInputEvents") == 2,
                "投递日志缺少终态或接受的输入事件计数。");
            Check(Guid.Parse(Property(recognized, "correlationId").GetString()!) == Guid.Parse(f.TurnId)
                && Guid.Parse(Property(delivered, "correlationId").GetString()!) == Guid.Parse(f.TurnId),
                "识别和投递日志未关联到同一轮本地会话。");
            AssertPrivate(entries);

            var rejected = new Turn();
            rejected.Socket.RejectCode = "CLIENT_ERROR";
            await using var failed = await Fixture.Create(rejected);
            Check(!await failed.Start().WaitAsync(Budget), "服务端拒绝仍报告启动成功。");
            await failed.App.StopAsync(true).WaitAsync(Budget);
            var rejectedEntries = await failed.Export();
            StartFailure(rejectedEntries, "ConnectingRecognition");
            AssertPrivate(rejectedEntries);
        });
    }

    private static JsonElement StartFailure(IEnumerable<JsonElement> entries, string stage)
    {
        var failures = entries.Where(x => IsEvent(x, "StartFailed") && StringField(x, "FailureStage") == stage).ToArray();
        Check(failures.Length == 1, "启动失败日志缺失、重复或阶段不正确：" + stage);
        return failures[0];
    }

    private static bool IsEvent(JsonElement entry, string name) =>
        Property(entry, "component").GetString() == "Controller" && Property(entry, "event").GetString() == name;

    private static JsonElement Property(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private static string? StringField(JsonElement entry, string field) => Property(Property(entry, "fields"), field).GetString();
    private static long NumberField(JsonElement entry, string field) => Property(Property(entry, "fields"), field).GetInt64();

    private static IEnumerable<JsonElement> Values(JsonElement value)
    {
        yield return value;
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                foreach (var nested in Values(property.Value)) yield return nested;
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                foreach (var nested in Values(item)) yield return nested;
    }

    private static bool HasMetadata(IEnumerable<JsonElement> entries, string type, uint hresult) => entries.Any(entry =>
    {
        var values = Values(entry).ToArray();
        bool hasType = values.Any(x => x.ValueKind == JsonValueKind.String &&
            (x.GetString() == type || x.GetString()!.EndsWith("." + type, StringComparison.Ordinal)));
        bool hasCode = values.Any(x => x.ValueKind == JsonValueKind.String && x.GetString()!.Equals("0x" + hresult.ToString("X8"), StringComparison.OrdinalIgnoreCase)
            || x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out var code) && (code == hresult || code == unchecked((int)hresult)));
        return hasType && hasCode;
    });

    private static void AssertPrivate(IEnumerable<JsonElement> entries)
    {
        foreach (var value in entries.SelectMany(Values).Where(x => x.ValueKind == JsonValueKind.String))
        {
            var text = value.GetString()!;
            Check(!text.Contains(Secret, StringComparison.Ordinal) && !text.Contains("SECRET_REQUEST_DATA", StringComparison.Ordinal),
                "运行日志泄漏了正文、API Key、异常消息、设备描述或服务端错误原文。");
        }
    }

    private sealed class Turn
    {
        internal Exception? ConstructionFailure, StartFailure, DisposeFailure;
        internal readonly ScriptedSocket Socket = new();
        internal readonly TaskCompletionSource<ScriptedCapture> Capture = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ScriptedCapture(Func<byte[], CancellationToken, ValueTask> send, Action<float> level, Turn turn) : IAudioCapture
    {
        private bool recording;
        internal bool Disposed;
        public string EndpointId => "scripted-microphone";
        public string FormatDescription => Secret + "DEVICE_DESCRIPTION";
        public string Diagnostic => Secret + "DEVICE_DIAGNOSTIC";
        public string? FailureMessage => null;
        public long SamplesSent { get; private set; }
        public void Start() { if (turn.StartFailure is { } failure) throw failure; recording = true; }
        internal async Task EmitFrame()
        {
            Check(recording && !Disposed, "测试设备尚未开始录音。");
            await send(new byte[3200], CancellationToken.None);
            SamplesSent += 1600;
            level(.04f);
        }
        public void RequestStop() => recording = false;
        public void Abort() => RequestStop();
        public Task StopAsync(CancellationToken token) { RequestStop(); return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            RequestStop(); Disposed = true;
            if (turn.DisposeFailure is { } failure) throw failure;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "VoiceInputControllerLogs-" + Guid.NewGuid().ToString("N"));
        private int exports;
        internal readonly string TurnId = JsonCodec.Id();
        internal AppController App { get; private set; } = null!;
        private RuntimeLog log = null!;

        internal static async Task<Fixture> Create(params Turn[] turns)
        {
            var f = new Fixture();
            var protector = new TestProtector();
            new SettingsStore(f.folder, protector).Save(new()
            {
                LegacyEndpoint = true, DictationOnly = true, SaveMemory = false, AllowLearning = false,
                LearnCorrections = false, DynamicLexicon = false, PolishEnabled = false, UseLexicon = false
            }, new(Secret + "API_KEY", ""));
            f.log = new RuntimeLog(Path.Combine(f.folder, "logs"));
            var pending = new Queue<Turn>(turns);
            Turn? constructing = null;
            f.App = new AppController(f.folder, protector, new RegressionHandler((_, _) => throw new Exception("Unexpected HTTP request")),
                (_, send, _, level) =>
                {
                    var turn = constructing ?? pending.Dequeue();
                    constructing = null;
                    if (turn.ConstructionFailure is { } failure) throw failure;
                    var capture = new ScriptedCapture(send, level, turn);
                    turn.Capture.TrySetResult(capture);
                    return capture;
                },
                receive =>
                {
                    var turn = pending.Dequeue();
                    constructing = turn;
                    return new BailianClient(receive, turn.Socket, (_, _, _) => Task.CompletedTask);
                }, log: f.log);
            await f.App.InitializeAsync();
            return f;
        }

        internal Task<bool> Start() => App.StartAsync(Environment.TickCount64 - 1000, CancellationToken.None, Task.FromResult(true), TurnId);

        internal async Task<JsonElement[]> Export()
        {
            string path = Path.Combine(folder, "diagnostics-" + ++exports + ".log");
            await log.FlushAsync().WaitAsync(Budget);
            await log.ExportAsync(path).WaitAsync(Budget);
            var entries = new List<JsonElement>();
            using var reader = new StreamReader(path);
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                entries.Add(document.RootElement.Clone());
            }
            Check(entries.Count > 0, "诊断导出中没有可读取的 JSONL 事件。");
            return entries.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            try { await App.DisposeAsync(); }
            finally
            {
                await log.DisposeAsync();
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
        }
    }
}
