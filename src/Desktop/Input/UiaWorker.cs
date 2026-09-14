using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop.Input;

internal record UiaRequest(string Operation, long Window = 0, long Focus = 0, uint Thread = 0,
    uint Process = 0, string CaptureId = "", bool CheckSelection = true, string? Text = null, uint ClipboardSequence = 0);
internal record UiaResponse(string Code, string Message = "", string CaptureId = "", uint? ClipboardSequence = null);

// The same executable hosts one hidden worker. Only target metadata and the final
// text to copy are sent here; no audio or credentials. Requests are never logged.
// Its stdin/stdout handles are private inherited pipes, and parent exit terminates the worker.
public static class UiaWorker
{
    public const string Argument = "--voiceinput-uia-worker";
    private static int parentId;
    private sealed record Captured(string Id, NativeTarget Native, int[] RuntimeId,
        AutomationElement Element, TextPatternRange? Selection);
    private static Captured? captured;
    public static bool IsWorker(string[] args)
    {
        if (args.Length != 2 || args[0] != Argument || !int.TryParse(args[1], out int id) || id <= 0) return false;
        parentId = id; return true;
    }
    public static void Run()
    {
        var monitor = new Thread(() =>
        {
            try { using var parent = Process.GetProcessById(parentId); parent.WaitForExit(); }
            catch { }
            Environment.Exit(0);
        }) { IsBackground = true, Name = "UIA parent lifetime" };
        monitor.Start();
        // Invoked on one MTA thread. UIA objects and cloned ranges never leave this process/thread.
        try
        {
            while (Console.In.ReadLine() is { } line)
            {
                UiaResponse reply;
                try
                {
                    var request = line.Length <= ClipboardPaste.MaxCharacters * 6 + 4096 ? JsonSerializer.Deserialize<UiaRequest>(line) : null;
                    reply = request == null ? new("Unavailable") : Handle(request);
                }
                catch { captured = null; reply = new("Unavailable"); }
                Console.Out.WriteLine(JsonSerializer.Serialize(reply)); Console.Out.Flush();
            }
        }
        catch (IOException) { }
    }
    private static UiaResponse Handle(UiaRequest request)
    {
        if (request.Operation == "Ping") return new("Ready");
        if (request.Operation == "Release") { captured = null; return new("Ready"); }
        if (request.Operation == "Capture")
        {
            captured = null;
            return Capture(new(new IntPtr(request.Window), new IntPtr(request.Focus), request.Thread, request.Process));
        }
        if (request.Operation == "Validate" && captured is { } target && target.Id == request.CaptureId)
            return Validate(target, request.CheckSelection);
        if (request.Operation == "PreparePaste" && captured is { } pasteTarget && pasteTarget.Id == request.CaptureId)
        {
            if (request.Text is null || !ClipboardPaste.IsSupportedText(request.Text)) return new("ClipboardUnavailable");
            var validation = Validate(pasteTarget, true);
            if (validation.Code != "Ready") return validation;
            uint? sequence = NativeClipboard.Prepare(request.Text, pasteTarget.Native, request.ClipboardSequence);
            return sequence is null ? new("ClipboardUnavailable") : new("Ready", ClipboardSequence: sequence);
        }
        return new("Changed");
    }
    private static UiaResponse Capture(NativeTarget native)
    {
        if (Win32.Observe(native) != FocusObservation.Stable) return new("Unavailable");
        if (Win32.Composing(native.Focus)) return new("Composing", "请先确认或取消输入法候选词，再按住说话。");
        var element = AutomationElement.FocusedElement;
        if (element == null) return new("Unavailable");
        if (element.Current.IsPassword) return new("Password", "密码输入框不支持自动语音输入，未上传语音。");
        if (!element.Current.IsEnabled) return new("Disabled", "当前输入框不可用，未上传语音。");
        if (!BelongsToTarget(element, native)) return new("Unavailable");
        bool editable = false; TextPatternRange? selection = null;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) editable = !((ValuePattern)value).Current.IsReadOnly;
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text))
        {
            var pattern = (TextPattern)text;
            editable |= pattern.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is bool readOnly && !readOnly;
            var ranges = pattern.GetSelection(); if (ranges.Length == 1) selection = ranges[0].Clone();
        }
        if (!editable) return new("NotEditable", "当前控件无法验证为可编辑区域。可在管理窗口开启“只听写，不自动输入”后重新录音。");
        if (Win32.Observe(native) != FocusObservation.Stable) return new("Unavailable");
        captured = new(Guid.NewGuid().ToString("N"), native, element.GetRuntimeId(), element, selection);
        return new("Ready", CaptureId: captured.Id);
    }
    private static bool BelongsToTarget(AutomationElement element, NativeTarget native)
    {
        for (int depth = 0; depth < 24 && element != null; depth++)
        {
            var hwnd = new IntPtr(element.Current.NativeWindowHandle);
            if (hwnd == native.Focus || hwnd == native.Window) return true;
            element = TreeWalker.RawViewWalker.GetParent(element);
        }
        return false;
    }
    private static UiaResponse Validate(Captured target, bool checkSelection)
    {
        var observation = Win32.Observe(target.Native);
        if (observation == FocusObservation.Unavailable) return new("Unavailable");
        if (observation == FocusObservation.Changed || Win32.Composing(target.Native.Focus)) return new("Changed");
        var current = AutomationElement.FocusedElement;
        if (current == null) return new("Unavailable");
        if (current.Current.IsPassword || !current.Current.IsEnabled || !current.GetRuntimeId().SequenceEqual(target.RuntimeId)) return new("Changed");
        if (current.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && ((ValuePattern)value).Current.IsReadOnly) return new("ReadOnly");
        if (current.TryGetCurrentPattern(TextPattern.Pattern, out var text))
        {
            var pattern = (TextPattern)text;
            if (pattern.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is bool readOnly && readOnly) return new("ReadOnly");
            if (checkSelection && target.Selection != null)
            {
                var ranges = pattern.GetSelection();
                if (ranges.Length != 1 || !ranges[0].Compare(target.Selection)) return new("SelectionChanged");
            }
        }
        else if (checkSelection && target.Selection != null) return new("SelectionUnavailable");
        return Win32.Observe(target.Native) == FocusObservation.Stable ? new("Ready", CaptureId: target.Id) : new("Unavailable");
    }
}

internal sealed class UiaProcess : IIsolatedInputWorker
{
    private readonly Process process;
    public UiaProcess()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位辅助功能工作进程。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, System.Reflection.Assembly.GetEntryAssembly()!.GetName().Name + ".dll"));
        start.ArgumentList.Add(UiaWorker.Argument); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        process = Process.Start(start) ?? throw new InvalidOperationException("辅助功能工作进程未启动。");
    }
    public async Task<string?> QueryAsync(string request, CancellationToken token)
    {
        if (process.HasExited) return null;
        await process.StandardInput.WriteLineAsync(request.AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
        string? reply = await process.StandardOutput.ReadLineAsync(token);
        return reply?.Length <= 4096 ? reply : null;
    }
    public async Task<bool> StopAsync()
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
            return process.HasExited;
        }
        catch { try { return process.HasExited; } catch { return false; } }
    }
    public ValueTask DisposeAsync() { process.Dispose(); return ValueTask.CompletedTask; }
}
