using System.Text;

namespace RealtimeTranscription.Desktop;

public sealed partial class AppController
{
    private const string NoMicrophoneAdvice = "未找到可用麦克风。请连接麦克风；使用虚拟机时先接入或映射麦克风，再在程序中刷新设备。";
    private static string StageLabel(string stage) => stage switch
    {
        "Configuration" => "检查配置",
        "PreparingSession" => "准备本轮会话",
        "CreatingRecognitionClient" => "准备识别连接",
        "OpeningMicrophone" => "打开麦克风设备",
        "StartingMicrophone" => "启动 WASAPI 采集",
        "ReadingMicrophone" => "读取麦克风音频",
        "StoppingMicrophone" => "结束麦克风测试",
        "PreparingRecognition" => "准备识别词库",
        "ValidatingTarget" => "确认本轮输入位置",
        "ConnectingRecognition" => "连接识别服务并启动任务",
        "Recognizing" => "识别服务已就绪",
        "StartFailed" => "启动失败",
        "MicrophoneTestFailed" => "麦克风测试失败",
        "MicrophoneTest" => "麦克风测试完成",
        _ => stage
    };

    private static bool IsMicrophoneStage(string stage) => stage is
        "OpeningMicrophone" or "StartingMicrophone" or "ReadingMicrophone" or "StoppingMicrophone";

    // Keep exception identity and HRESULTs, never Exception.Message/ToString: a
    // driver, provider or inner exception can include paths, credentials or text.
    private static IEnumerable<Exception> ExceptionChain(Exception error)
    {
        var pending = new Queue<Exception>(); pending.Enqueue(error);
        for (int count = 0; pending.Count > 0 && count < 8; count++)
        {
            var current = pending.Dequeue(); yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Take(8)) pending.Enqueue(inner);
            }
            else if (current.InnerException is { } inner) pending.Enqueue(inner);
        }
    }

    private static string ExceptionMetadata(Exception? error)
    {
        if (error == null) return "异常类型：None\nHRESULT：0x00000000";
        var text = new StringBuilder(); int depth = 0;
        foreach (var current in ExceptionChain(error))
        {
            if (depth > 0) text.AppendLine();
            text.Append(depth == 0 ? "异常类型：" : $"内部异常[{depth}]：")
                .Append(current.GetType().FullName).Append("\nHRESULT：0x")
                .Append(current.HResult.ToString("X8"));
            depth++;
        }
        return text.ToString();
    }

    private static string? MicrophoneAdvice(Exception error) => unchecked((uint)error.HResult) switch
    {
        0x80070005 => "Windows 拒绝访问麦克风。请在“设置 → 隐私 → 麦克风”中允许麦克风访问，并允许桌面应用访问麦克风。",
        0x80070490 or 0x80070002 => NoMicrophoneAdvice,
        0x88890004 or 0x88890026 => "麦克风设备已失效或音频资源被撤销。请重新连接或重新选择麦克风后重试。",
        0x8889000A => "麦克风正被其他应用以独占方式使用。请关闭占用麦克风的应用后重试，或在 Windows 声音设置中关闭该设备的独占模式。",
        0x88890008 => "麦克风音频格式不受支持。请在 Windows 声音设置中选择受支持的默认格式，或更换输入设备。",
        0x88890010 => "Windows Audio 服务未运行。请检查并启动 Windows Audio 和 Windows Audio Endpoint Builder 服务后重试。",
        0x8889000F => "无法创建麦克风音频端点。请重新连接设备，检查 Windows 声音设置，必要时重启 Windows 音频服务。",
        _ => error is AudioEndpointUnavailableException ? NoMicrophoneAdvice : error is NotSupportedException
            ? "当前麦克风音频格式不受支持。请检查 Windows 声音设置中的默认格式，或更换输入设备。"
            : null
    };

    private static string MicrophoneError(Exception error, string stage)
    {
        if (error is OperationCanceledException or TimeoutException)
            return "麦克风操作超时或已取消。请重试；若持续失败，可在设置中查看诊断信息。";
        var chain = ExceptionChain(error).ToArray();
        var cause = chain.FirstOrDefault(e => MicrophoneAdvice(e) != null) ?? chain.Last();
        string advice = MicrophoneAdvice(cause) ?? $"{StageLabel(stage)}失败。请在设置中查看诊断信息，并提供失败阶段和 HRESULT。";
        return $"{advice}（HRESULT：0x{cause.HResult:X8}）";
    }

    // Each resource is attempted even when another cleanup fails. Preserve the
    // startup failure as the outcome and append only safe cleanup metadata.
    private async Task<string> CleanupFailedStartAsync()
    {
        var failedAudio = audio; audio = null;
        var failedClient = asr; asr = null;
        var cleanup = new StringBuilder();
        void Failed(string operation, Exception error) => cleanup.AppendLine()
            .Append("清理阶段：").AppendLine(operation).Append(ExceptionMetadata(error));
        try { failedAudio?.Abort(); } catch (Exception e) { Failed("AbortMicrophone", e); }
        try { failedClient?.Abort(); } catch (Exception e) { Failed("AbortRecognition", e); }
        if (failedAudio != null)
            try { await failedAudio.DisposeAsync(); } catch (Exception e) { Failed("DisposeMicrophone", e); }
        if (failedClient != null)
        {
            try { await failedClient.DisposeAsync(); } catch (Exception e) { Failed("DisposeRecognition", e); }
            await OnActor(() => engine?.SealTask(failedClient.TaskId, "启动未完成"));
        }
        return cleanup.ToString();
    }
}
