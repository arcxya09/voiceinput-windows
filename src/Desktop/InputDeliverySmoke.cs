using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using RealtimeTranscription.Desktop.Input;
using Forms = System.Windows.Forms;

namespace RealtimeTranscription.Desktop;

/// <summary>Exercises the real delivery path against disposable, separately owned controls.</summary>
internal static class InputDeliverySmoke
{
    private const string TargetArgument = "--voiceinput-delivery-target";
    private const string ClipboardSeed = "VoiceInput isolated paste fixture seed";
    private sealed record Request(string Operation, string Mode = "plain", string Text = "", int Start = 0, int Length = 0, bool DelayPaste = false);
    private sealed record Reply(string Code, long Window = 0, long Focus = 0, uint Thread = 0, uint Process = 0,
        string Text = "", string ClipboardText = "", int PasteDown = 0, int PasteUp = 0, int PacketKeys = 0,
        int PasteMessages = 0, int DelayedPastes = 0);

    // Observe the native control messages, independently of the production
    // dispatch result. A successful text readback alone would also pass for the
    // old per-character input path that this regression is meant to replace.
    private sealed class InputProbe : IDisposable
    {
        private Forms.Timer? delayedPaste;
        internal bool DelayPaste;
        internal int PasteDown, PasteUp, PacketKeys, PasteMessages, DelayedPastes;
        internal void Reset(bool delayPaste)
        {
            delayedPaste?.Dispose(); delayedPaste = null;
            DelayPaste = delayPaste;
            PasteDown = PasteUp = PacketKeys = PasteMessages = DelayedPastes = 0;
        }
        internal bool Observe(Forms.Message message, Forms.TextBoxBase editor)
        {
            if (message.Msg == 0x0302) PasteMessages++; // WM_PASTE, if emitted by this control.
            if (DelayPaste && message.Msg == 0x0102 && message.WParam.ToInt64() == 0x16) return true; // WM_CHAR Ctrl+V.
            if (message.Msg is not (0x0100 or 0x0101)) return false;
            int key = unchecked((int)message.WParam.ToInt64());
            if (key == 0xE7 && message.Msg == 0x0100) PacketKeys++; // VK_PACKET.
            if (key != 0x56 || (GetKeyState(0x11) & 0x8000) == 0) return false;
            if (message.Msg == 0x0100) PasteDown++; else PasteUp++;
            if (!DelayPaste || message.Msg != 0x0100) return false;
            delayedPaste?.Dispose();
            delayedPaste = new Forms.Timer { Interval = 250 };
            delayedPaste.Tick += (_, _) =>
            {
                delayedPaste?.Stop();
                DelayedPastes++;
                editor.Paste();
            };
            delayedPaste.Start();
            return true;
        }
        public void Dispose() => delayedPaste?.Dispose();
    }

    private sealed class PlainEditor(InputProbe probe) : Forms.TextBox
    {
        protected override void WndProc(ref Forms.Message message)
        { if (!probe.Observe(message, this)) base.WndProc(ref message); }
    }

    private sealed class RichEditor(InputProbe probe) : Forms.RichTextBox
    {
        protected override void WndProc(ref Forms.Message message)
        { if (!probe.Observe(message, this)) base.WndProc(ref message); }
    }

    internal static bool IsTarget(string[] args) => args.Length == 2 && args[0] == TargetArgument
        && int.TryParse(args[1], out int parent) && parent > 0;

    internal static int RunTarget(string[] args)
    {
        using var form = new Forms.Form { Text = "VoiceInput isolated input fixture", Width = 640, Height = 220,
            StartPosition = Forms.FormStartPosition.CenterScreen, ShowInTaskbar = false };
        using var probe = new InputProbe();
        using var plain = new PlainEditor(probe) { Multiline = true, Dock = Forms.DockStyle.Fill };
        using var rich = new RichEditor(probe) { Dock = Forms.DockStyle.Fill, Visible = false, DetectUrls = false };
        form.Controls.Add(plain); form.Controls.Add(rich);
        Forms.TextBoxBase editor = plain;
        // Smoke tests run on an isolated Windows desktop. Preserve an initial
        // empty/text clipboard, and refuse richer formats instead of discarding
        // them if somebody launches this fixture on their own desktop.
        string? originalText = null;
        uint ownedClipboardSequence = 0;
        bool clipboardPrepared = false;
        void PreserveClipboard()
        {
            var original = Forms.Clipboard.GetDataObject();
            var formats = original?.GetFormats(autoConvert: false) ?? [];
            string[] textFormats = [Forms.DataFormats.Text, Forms.DataFormats.UnicodeText,
                Forms.DataFormats.OemText, Forms.DataFormats.Locale, Forms.DataFormats.StringFormat];
            if (formats.Any(format => !textFormats.Contains(format, StringComparer.Ordinal)))
                throw new InvalidOperationException("The isolated paste fixture requires an empty or text-only clipboard.");
            originalText = formats.Length == 0 ? null : Forms.Clipboard.GetText(Forms.TextDataFormat.UnicodeText);
            clipboardPrepared = true;
        }
        void RestoreClipboard()
        {
            if (!clipboardPrepared || ownedClipboardSequence == 0 || GetClipboardSequenceNumber() != ownedClipboardSequence) return;
            try
            {
                if (originalText == null || originalText.Length == 0) Forms.Clipboard.Clear();
                else Forms.Clipboard.SetText(originalText, Forms.TextDataFormat.UnicodeText);
            }
            catch { } // Do not mask an earlier assertion or overwrite a newer clipboard on retry.
        }
        void Respond(Reply reply) { Console.Out.WriteLine(JsonSerializer.Serialize(reply)); Console.Out.Flush(); }
        Reply Current(string code = "Ready", string clipboardText = "")
        {
            uint thread = Win32.GetWindowThreadProcessId(form.Handle, out uint process);
            return new(code, form.Handle.ToInt64(), editor.Handle.ToInt64(), thread, process, editor.Text,
                clipboardText, probe.PasteDown, probe.PasteUp, probe.PacketKeys, probe.PasteMessages, probe.DelayedPastes);
        }
        void Dispatch(Request request)
        {
            try
            {
                if (request.Operation == "Prepare")
                {
                    if (!clipboardPrepared) PreserveClipboard();
                    Forms.Clipboard.SetText(ClipboardSeed, Forms.TextDataFormat.UnicodeText);
                    ownedClipboardSequence = GetClipboardSequenceNumber();
                    probe.Reset(request.DelayPaste);
                    editor = request.Mode == "rich" ? rich : plain;
                    plain.Visible = ReferenceEquals(editor, plain); rich.Visible = ReferenceEquals(editor, rich);
                    editor.BringToFront(); editor.Text = request.Text;
                    editor.Select(request.Start, request.Length); form.Activate(); editor.Focus();
                    Respond(Current());
                }
                else if (request.Operation == "Read")
                {
                    uint beforeRead = GetClipboardSequenceNumber();
                    string clipboardText = Forms.Clipboard.GetText(Forms.TextDataFormat.UnicodeText);
                    uint afterRead = GetClipboardSequenceNumber();
                    if (beforeRead == afterRead && clipboardText == request.Text) ownedClipboardSequence = afterRead;
                    Respond(Current(clipboardText: clipboardText));
                }
                else if (request.Operation == "Exit") form.Close();
                else Respond(new("Invalid"));
            }
            catch (Exception ex) { Respond(new("Error", Text: ex.Message)); }
        }
        form.Shown += (_, _) =>
        {
            plain.Focus(); Respond(Current());
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await Console.In.ReadLineAsync() is { } line)
                    {
                        var request = line.Length <= 8192 ? JsonSerializer.Deserialize<Request>(line) : null;
                        if (request == null) break;
                        form.BeginInvoke(new Action(() => Dispatch(request)));
                    }
                }
                catch { }
                finally { try { form.BeginInvoke(new Action(form.Close)); } catch { } }
            });
        };
        new Thread(() =>
        {
            try { using var parent = Process.GetProcessById(int.Parse(args[1], CultureInfo.InvariantCulture)); parent.WaitForExit(); }
            catch { }
            Environment.Exit(0);
        }) { IsBackground = true, Name = "Input fixture parent lifetime" }.Start();
        try { Forms.Application.Run(form); }
        finally { RestoreClipboard(); }
        return 0;
    }

    internal static async Task<IReadOnlyList<string>> CheckAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = budget.Token;
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RealtimeTranscription.dll"));
        start.ArgumentList.Add(TargetArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The isolated input target did not start.");
        _ = AllowSetForegroundWindow((uint)process.Id);
        var stderr = process.StandardError.ReadToEndAsync();
        async Task<Reply> Read()
        {
            string? line = await process.StandardOutput.ReadLineAsync(token);
            return line == null ? throw new InvalidOperationException("The isolated input target exited early.")
                : JsonSerializer.Deserialize<Reply>(line) ?? throw new InvalidOperationException("Invalid input target reply.");
        }
        async Task<Reply> Query(Request request)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            return await Read();
        }
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("Native delivery: " + message); }
        try
        {
            var initial = await Read();
            Require(initial.Code == "Ready" && initial.Process == (uint)process.Id, "The fixture identity is not the child process.");
            await TextDelivery.InitializeAsync();
            const string sentence = "感觉好像就不太准确。";
            const string twoSentences = "测试一下这次的输入是否准确。看起来没有什么问题。";
            foreach (string mode in new[] { "plain", "rich" })
            foreach (var sample in new[]
            {
                (Initial: "", Start: 0, Length: 0, Text: sentence, Delayed: false),
                (Initial: "", Start: 0, Length: 0, Text: twoSentences, Delayed: false),
                (Initial: "前文旧词后文", Start: 2, Length: 2, Text: sentence, Delayed: false),
                (Initial: "前文旧词后文", Start: 2, Length: 2, Text: twoSentences, Delayed: false),
                (Initial: "", Start: 0, Length: 0, Text: "🧪" + sentence + "e\u0301👩🏽‍🔬", Delayed: false),
                (Initial: "", Start: 0, Length: 0, Text: new string('中', 127) + "🧪" + sentence, Delayed: false),
                (Initial: "", Start: 0, Length: 0, Text: sentence + "\r\n" + twoSentences + "\r\n🧪e\u0301", Delayed: false),
                (Initial: "", Start: 0, Length: 0, Text: sentence + "\n" + twoSentences + "\r🧪e\u0301", Delayed: false),
                (Initial: "", Start: 0, Length: 0, Text: twoSentences, Delayed: true)
            })
            {
                var prepared = await Query(new("Prepare", mode, sample.Initial, sample.Start, sample.Length, sample.Delayed));
                Require(prepared.Code == "Ready", "The fixture could not prepare " + mode + ": " + prepared.Text);
                Require(prepared.Process == (uint)process.Id && prepared.Window == initial.Window, "The fixture moved to a foreign window.");
                var native = new NativeTarget(new IntPtr(prepared.Window), new IntPtr(prepared.Focus), prepared.Thread, prepared.Process);
                _ = SetForegroundWindow(native.Window);
                for (int i = 0; Win32.Observe(native) != RealtimeTranscription.Core.FocusObservation.Stable && i < 50; i++)
                    await Task.Delay(20, token);
                bool Safe() => !process.HasExited && native.Process == (uint)process.Id && Win32.Current() == native;
                Require(Safe(), "The isolated control could not obtain foreground focus.");
                var capture = await TextDelivery.CaptureAsync(native, Safe, token);
                Require(capture.Target != null, "The production target capture rejected " + mode + ": " + capture.Code);
                try
                {
                    var result = await TextDelivery.SendAsync(capture.Target!, sample.Text, Safe, token);
                    Require(result.State == "PasteSent" && result.Accepted == 4,
                        "The production input path stopped unexpectedly: " + result.State + " / " + result.Message);
                    string expected = sample.Initial[..sample.Start] + sample.Text + sample.Initial[(sample.Start + sample.Length)..];
                    Reply readback = new("Pending");
                    // WinForms plain edit exposes CRLF; rich edit exposes LF.
                    // CF_UNICODETEXT uses Windows CRLF. Every non-newline
                    // character must remain exact in clipboard and control.
                    static string Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                    string expectedClipboard = Lines(sample.Text).Replace("\n", "\r\n", StringComparison.Ordinal);
                    for (int i = 0; i < 50; i++)
                    {
                        readback = await Query(new("Read", Text: expectedClipboard));
                        Require(readback.Code == "Ready", "The fixture read failed: " + readback.Text);
                        if (Lines(readback.Text) == Lines(expected) && readback.PasteUp == 1) break;
                        await Task.Delay(20, token);
                    }
                    Require(Lines(readback.Text) == Lines(expected), mode + " text mismatch. Expected fixture=" + JsonSerializer.Serialize(expected)
                        + "; actual fixture=" + JsonSerializer.Serialize(readback.Text));
                    Require(readback.PasteDown == 1 && readback.PasteUp == 1 && readback.PacketKeys == 0 && readback.PasteMessages <= 1,
                        mode + " must receive one Ctrl+V gesture and no per-character Unicode input: "
                        + $"down={readback.PasteDown}, up={readback.PasteUp}, packets={readback.PacketKeys}, WM_PASTE={readback.PasteMessages}");
                    Require(readback.ClipboardText == expectedClipboard, mode + " clipboard did not retain the full recognition result with Windows line endings.");
                    Require(readback.DelayedPastes == (sample.Delayed ? 1 : 0), mode + " delayed clipboard consumption did not run as requested.");
                }
                finally { await TextDelivery.ReleaseAsync(capture.Target!); }
            }
            var beforeFocusChange = await Query(new("Prepare", Text: "original control"));
            Require(beforeFocusChange.Code == "Ready", "The focus-change fixture could not prepare.");
            var originalNative = new NativeTarget(new IntPtr(beforeFocusChange.Window), new IntPtr(beforeFocusChange.Focus),
                beforeFocusChange.Thread, beforeFocusChange.Process);
            bool OriginalSafe() => !process.HasExited && originalNative.Process == (uint)process.Id && Win32.Current() == originalNative;
            var originalCapture = await TextDelivery.CaptureAsync(originalNative, OriginalSafe, token);
            Require(originalCapture.Target != null, "The original control could not be captured before the focus-change check.");
            try
            {
                var changed = await Query(new("Prepare", "rich", "new control remains unchanged"));
                Require(changed.Code == "Ready" && changed.Process == (uint)process.Id && changed.Window == initial.Window
                    && changed.Focus != beforeFocusChange.Focus && !OriginalSafe(), "The fixture did not move focus to its other control.");
                var blocked = await TextDelivery.SendAsync(originalCapture.Target!, twoSentences, OriginalSafe, token);
                Require(blocked.State == "Blocked" && blocked.Accepted == 0, "A changed input focus must block clipboard preparation and paste.");
                var unchanged = await Query(new("Read", Text: ClipboardSeed));
                Require(unchanged.Code == "Ready" && unchanged.Text == "new control remains unchanged"
                    && unchanged.ClipboardText == ClipboardSeed && unchanged.PasteDown == 0 && unchanged.PasteUp == 0
                    && unchanged.PacketKeys == 0 && unchanged.PasteMessages == 0,
                    "Changing focus must leave both the new control and clipboard unchanged.");
            }
            finally { await TextDelivery.ReleaseAsync(originalCapture.Target!); }
            return ["Production full-text clipboard delivery reads back both reported Chinese examples in independent plain and rich edit controls, including selection replacement, emoji, long text, multiple lines and delayed paste; each receives exactly one Ctrl+V, zero Unicode packet keys, and retains the complete result on the clipboard",
                "Changing focus to another isolated control blocks delivery before changing the clipboard or sending a paste gesture"];
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } catch { } }
            _ = await stderr;
            await TextDelivery.ShutdownAsync();
        }
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
