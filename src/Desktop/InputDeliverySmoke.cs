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
    private sealed record Request(string Operation, string Mode = "plain", string Text = "", int Start = 0, int Length = 0);
    private sealed record Reply(string Code, long Window = 0, long Focus = 0, uint Thread = 0, uint Process = 0, string Text = "");

    internal static bool IsTarget(string[] args) => args.Length == 2 && args[0] == TargetArgument
        && int.TryParse(args[1], out int parent) && parent > 0;

    internal static int RunTarget(string[] args)
    {
        using var form = new Forms.Form { Text = "VoiceInput isolated input fixture", Width = 640, Height = 220,
            StartPosition = Forms.FormStartPosition.CenterScreen, ShowInTaskbar = false };
        using var plain = new Forms.TextBox { Multiline = true, Dock = Forms.DockStyle.Fill };
        using var rich = new Forms.RichTextBox { Dock = Forms.DockStyle.Fill, Visible = false, DetectUrls = false };
        form.Controls.Add(plain); form.Controls.Add(rich);
        Forms.TextBoxBase editor = plain;
        void Respond(Reply reply) { Console.Out.WriteLine(JsonSerializer.Serialize(reply)); Console.Out.Flush(); }
        Reply Current(string code = "Ready")
        {
            uint thread = Win32.GetWindowThreadProcessId(form.Handle, out uint process);
            return new(code, form.Handle.ToInt64(), editor.Handle.ToInt64(), thread, process, editor.Text);
        }
        void Dispatch(Request request)
        {
            if (request.Operation == "Prepare")
            {
                editor = request.Mode == "rich" ? rich : plain;
                plain.Visible = ReferenceEquals(editor, plain); rich.Visible = ReferenceEquals(editor, rich);
                editor.BringToFront(); editor.Text = request.Text;
                editor.Select(request.Start, request.Length); form.Activate(); editor.Focus();
                Respond(Current());
            }
            else if (request.Operation == "Read") Respond(Current());
            else if (request.Operation == "Exit") form.Close();
            else Respond(new("Invalid"));
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
        Forms.Application.Run(form);
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
            foreach (string mode in new[] { "plain", "rich" })
            foreach (var sample in new[]
            {
                (Initial: "", Start: 0, Length: 0, Text: sentence),
                (Initial: "前文旧词后文", Start: 2, Length: 2, Text: sentence),
                (Initial: "", Start: 0, Length: 0, Text: "🧪" + sentence + "e\u0301👩🏽‍🔬"),
                (Initial: "", Start: 0, Length: 0, Text: new string('中', 127) + "🧪" + sentence)
            })
            {
                var prepared = await Query(new("Prepare", mode, sample.Initial, sample.Start, sample.Length));
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
                    Require(result.State == "Sent" && result.Accepted == sample.Text.Length * 2,
                        "The production input path stopped unexpectedly: " + result.State + " / " + result.Message);
                    string expected = sample.Initial[..sample.Start] + sample.Text + sample.Initial[(sample.Start + sample.Length)..];
                    string actual = "";
                    for (int i = 0; i < 50; i++)
                    {
                        actual = (await Query(new("Read"))).Text;
                        if (actual == expected) break;
                        await Task.Delay(20, token);
                    }
                    Require(actual == expected, mode + " text mismatch. Expected fixture=" + JsonSerializer.Serialize(expected)
                        + "; actual fixture=" + JsonSerializer.Serialize(actual));
                }
                finally { await TextDelivery.ReleaseAsync(capture.Target!); }
            }
            return ["Production Unicode delivery reads back the exact reported Chinese sentence in independent plain and rich edit controls, including selection replacement, emoji and a 128-unit boundary"];
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
}
