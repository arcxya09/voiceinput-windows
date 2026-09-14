using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

static class StartupConnectionRegression
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<IReadOnlyList<TermData>> Terms() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("2.1.4 连接与词库准备重叠，按实际选择发送词汇并在 task-started 后依序上传首帧", async () =>
        {
            foreach (bool termsFirst in new[] { true, false })
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var entered = Gate(); var connected = Gate(); var open = Gate(); var terms = Terms();
                var socket = new StartupSocket();
                await using var client = new BailianClient(_ => Task.CompletedTask, socket, async (_, _, token) =>
                {
                    entered.TrySetResult();
                    await open.Task.WaitAsync(token);
                    connected.TrySetResult();
                });
                byte[][] frames = [[1, 0, 2, 0], [3, 0, 4, 0], [5, 0, 6, 0]];
                await client.AudioAsync(frames[0], deadline.Token);
                var start = client.StartPreparedAsync(new() { LegacyEndpoint = true, AsrContext = true }, "TEST_ONLY", terms.Task, deadline.Token);
                await entered.Task.WaitAsync(deadline.Token);
                Check(!terms.Task.IsCompleted && !start.IsCompleted, "连接必须在词库准备完成前开始。");
                await client.AudioAsync(frames[1], deadline.Token);

                var selected = Lexicon.Select([
                    new() { Text = "项目术语", Scope = "*", Weight = 1 },
                    new() { Text = "项目术语", Scope = "current", Weight = 5 },
                    new() { Text = "全局术语", Scope = "*", Weight = 3 },
                    new() { Text = "禁用术语", Scope = "current", State = TermState.Disabled },
                    new() { Text = "其他项目", Scope = "other" }
                ], "current");
                if (termsFirst) terms.SetResult(selected);
                else { open.SetResult(); await connected.Task.WaitAsync(deadline.Token); }
                Check(!start.IsCompleted && socket.Script.Actions.IsEmpty && socket.Script.Pcm.IsEmpty,
                    "连接和词库两者尚未就绪时不能发送 run-task 或 PCM。");
                if (termsFirst) open.SetResult(); else terms.SetResult(selected);
                await socket.RunSent.Task.WaitAsync(deadline.Token);
                Check(!start.IsCompleted && socket.Script.Pcm.IsEmpty, "task-started 前不能上传缓存 PCM。");
                using (var doc = JsonDocument.Parse(socket.RunPayload!))
                {
                    var payload = doc.RootElement.GetProperty("payload");
                    var vocabulary = payload.GetProperty("parameters").GetProperty("vocabulary");
                    Check(vocabulary.EnumerateObject().Count() == 2 && vocabulary.GetProperty("项目术语").GetInt32() == 5 &&
                        vocabulary.GetProperty("全局术语").GetInt32() == 3, "run-task 未使用准备完成的实际项目词汇及权重。");
                    Check(payload.GetProperty("input").GetProperty("context")[0].GetProperty("content")[0].GetProperty("text").GetString() == Lexicon.Context(selected),
                        "ASR 上下文未使用同一份准备词库。");
                }
                await client.AudioAsync(frames[2], deadline.Token);
                socket.AllowStarted.SetResult();
                await start.WaitAsync(deadline.Token);
                await client.FinishAsync(deadline.Token);
                Check(socket.Script.Pcm.SelectMany(x => x).SequenceEqual(frames.SelectMany(x => x)), "首字缓存缺失、重复或顺序变化。");
                Check(socket.Script.Actions.SequenceEqual(new[] { "run-task", "finish-task" }), "额外发送了任务控制请求。");
            }
        });

        await test("2.1.4 词库准备失败立即中止未完成或已建立的连接，并保留原始本地错误", async () =>
        {
            foreach (bool connected in new[] { false, true })
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var entered = Gate(); var canceled = Gate(); var connection = Gate(); var terms = Terms();
                var socket = new StartupSocket(); var faults = new ConcurrentQueue<string>();
                await using var client = new BailianClient(e => { if (e.Event == "connection-failed") faults.Enqueue(e.Error); return Task.CompletedTask; }, socket,
                    (_, _, token) => { token.Register(() => canceled.TrySetResult()); entered.TrySetResult(); return connected ? Task.CompletedTask : connection.Task; });
                await client.AudioAsync([1, 0], deadline.Token);
                var start = client.StartPreparedAsync(new() { LegacyEndpoint = true }, "TEST_ONLY", terms.Task, deadline.Token);
                await entered.Task.WaitAsync(deadline.Token);
                var original = new InvalidOperationException("词库读取失败");
                terms.SetException(original);
                try { await start.WaitAsync(deadline.Token); throw new Exception("词库失败应中止启动。"); }
                catch (InvalidOperationException error) { Check(ReferenceEquals(error, original), "本地错误被网络错误或取消异常覆盖。"); }
                await canceled.Task.WaitAsync(deadline.Token);
                Check(socket.State == WebSocketState.Aborted && socket.Script.Actions.IsEmpty && socket.Script.Pcm.IsEmpty, "准备失败后连接或音频泄漏。");
                Check(faults.IsEmpty && client.FailureMessage == null, "本地词库错误被误报为百炼故障。");
                // A wire implementation may fault after its cancellation; this is observed without waiting for it.
                if (!connected) connection.TrySetException(new WebSocketException("late connect failure"));
            }
        });

        await test("2.1.4 已失败或取消的词库准备不建立连接，保留本地失败原因", async () =>
        {
            foreach (bool canceled in new[] { false, true })
            {
                var original = new InvalidOperationException("已失败的词库准备");
                var terms = Terms(); var socket = new StartupSocket(); bool connected = false;
                if (canceled) terms.SetCanceled(); else terms.SetException(original);
                await using var client = new BailianClient(_ => Task.CompletedTask, socket,
                    (_, _, _) => { connected = true; return Task.CompletedTask; });
                try
                {
                    await client.StartPreparedAsync(new() { LegacyEndpoint = true }, "TEST_ONLY", terms.Task, CancellationToken.None);
                    throw new Exception("不应继续启动。");
                }
                catch (OperationCanceledException) when (canceled) { }
                catch (InvalidOperationException error) when (!canceled) { Check(ReferenceEquals(error, original), "原始准备失败被覆盖。"); }
                Check(!connected && socket.State == WebSocketState.Aborted && socket.Script.Actions.IsEmpty && client.FailureMessage == null,
                    "已失败的准备仍建立了连接或产生网络错误。");
            }
        });

        await test("2.1.4 词库任务自行取消立即关闭仍在连接的传输，不误报网络故障", async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var entered = Gate(); var canceled = Gate(); var connection = Gate(); var terms = Terms();
            var socket = new StartupSocket(); var faults = new ConcurrentQueue<string>();
            await using var client = new BailianClient(e => { if (e.Event == "connection-failed") faults.Enqueue(e.Error); return Task.CompletedTask; }, socket,
                (_, _, token) => { token.Register(() => canceled.TrySetResult()); entered.TrySetResult(); return connection.Task; });
            await client.AudioAsync([1, 0], deadline.Token);
            var start = client.StartPreparedAsync(new() { LegacyEndpoint = true }, "TEST_ONLY", terms.Task, deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            terms.SetCanceled();
            try { await start.WaitAsync(deadline.Token); throw new Exception("应取消启动。"); }
            catch (OperationCanceledException) { Check(!deadline.IsCancellationRequested, "等待了整个连接预算才响应词库取消。"); }
            await canceled.Task.WaitAsync(deadline.Token);
            connection.TrySetResult();
            Check(socket.State == WebSocketState.Aborted && socket.Script.Actions.IsEmpty && socket.Script.Pcm.IsEmpty && faults.IsEmpty,
                "词库取消后存在请求、PCM 或虚假网络故障。");
        });

        await test("2.1.4 用户取消连接或词库等待立即返回，迟到的准备失败不会发送请求", async () =>
        {
            foreach (bool connected in new[] { false, true })
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)); using var cancel = new CancellationTokenSource();
                var entered = Gate(); var connection = Gate(); var terms = Terms(); var socket = new StartupSocket();
                await using var client = new BailianClient(_ => Task.CompletedTask, socket,
                    (_, _, _) => { entered.TrySetResult(); return connected ? Task.CompletedTask : connection.Task; });
                await client.AudioAsync([1, 0], deadline.Token);
                var start = client.StartPreparedAsync(new() { LegacyEndpoint = true }, "TEST_ONLY", terms.Task, cancel.Token);
                await entered.Task.WaitAsync(deadline.Token);
                cancel.Cancel();
                try { await start.WaitAsync(deadline.Token); throw new Exception("应取消启动。"); }
                catch (OperationCanceledException) { Check(!deadline.IsCancellationRequested, "取消依赖未完成的连接或词库任务。"); }
                terms.SetException(new InvalidOperationException("late preparation failure")); connection.TrySetResult();
                Check(socket.State == WebSocketState.Aborted && socket.Script.Actions.IsEmpty && socket.Script.Pcm.IsEmpty && client.FailureMessage == null,
                    "取消后连接仍存活、发送内容或产生错误。");
            }
        });

        await test("2.1.4 连接先失败不等待词库，保留首个供应商错误并观察迟到的准备失败", async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var terms = Terms(); var connection = Gate(); var socket = new StartupSocket(); var faults = new ConcurrentQueue<string>();
            var original = new ProviderException("测试连接首个错误");
            await using var client = new BailianClient(e => { if (e.Event == "connection-failed") faults.Enqueue(e.Error); return Task.CompletedTask; }, socket, (_, _, _) => connection.Task);
            await client.AudioAsync([1, 0], deadline.Token);
            var start = client.StartPreparedAsync(new() { LegacyEndpoint = true }, "TEST_ONLY", terms.Task, deadline.Token);
            connection.SetException(original);
            try { await start.WaitAsync(deadline.Token); throw new Exception("连接失败应中止启动。"); }
            catch (ProviderException error) { Check(ReferenceEquals(error, original), "首个连接错误被覆盖。"); }
            Check(!terms.Task.IsCompleted && socket.State == WebSocketState.Aborted && socket.Script.Actions.IsEmpty && socket.Script.Pcm.IsEmpty,
                "连接失败仍等待词库或泄漏内容。");
            terms.SetException(new InvalidOperationException("late preparation failure"));
            try { await client.AudioAsync([2, 0], deadline.Token); throw new Exception("失败后仍接受音频。"); }
            catch (ProviderException error) { Check(ReferenceEquals(error, original), "音频调用未保留首个错误。"); }
            Check(faults.Count == 1 && faults.Single() == original.Message, "连接故障通知丢失或重复。");
        });

        await test("2.1.4 已发送 run-task 后取消等待 task-started，首帧缓存不会泄漏", async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)); using var cancel = new CancellationTokenSource();
            var socket = new StartupSocket(); var faults = new ConcurrentQueue<string>();
            await using var client = new BailianClient(e => { if (e.Event == "connection-failed") faults.Enqueue(e.Error); return Task.CompletedTask; }, socket, (_, _, _) => Task.CompletedTask);
            await client.AudioAsync([1, 0], deadline.Token);
            var start = client.StartPreparedAsync(new() { LegacyEndpoint = true }, "TEST_ONLY", Task.FromResult<IReadOnlyList<TermData>>([]), cancel.Token);
            await socket.RunSent.Task.WaitAsync(deadline.Token);
            cancel.Cancel();
            try { await start.WaitAsync(deadline.Token); throw new Exception("应取消启动。"); }
            catch (OperationCanceledException) { }
            socket.AllowStarted.SetResult();
            Check(socket.State == WebSocketState.Aborted && socket.Script.Pcm.IsEmpty && faults.IsEmpty && client.FailureMessage == null,
                "取消 task-started 等待后发送了 PCM 或虚假网络故障。");
        });
    }

    // Keep ScriptedSocket's real protocol responses, while controlling delivery of task-started.
    private sealed class StartupSocket : WebSocket
    {
        internal readonly ScriptedSocket Script = new();
        internal readonly TaskCompletionSource AllowStarted = Gate();
        internal readonly TaskCompletionSource RunSent = Gate();
        internal byte[]? RunPayload;
        public override WebSocketCloseStatus? CloseStatus => Script.CloseStatus;
        public override string? CloseStatusDescription => Script.CloseStatusDescription;
        public override string? SubProtocol => Script.SubProtocol;
        public override WebSocketState State => Script.State;
        public override void Abort() => Script.Abort();
        public override void Dispose() => Script.Dispose();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Script.CloseAsync(status, description, token);
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Script.CloseOutputAsync(status, description, token);
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            bool run = false;
            if (type == WebSocketMessageType.Text)
            {
                using var doc = JsonDocument.Parse(buffer.AsMemory());
                run = doc.RootElement.GetProperty("header").GetProperty("action").GetString() == "run-task";
            }
            if (run) RunPayload = buffer.ToArray();
            await Script.SendAsync(buffer, type, end, token);
            if (run) RunSent.TrySetResult();
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            await AllowStarted.Task.WaitAsync(token);
            return await Script.ReceiveAsync(buffer, token);
        }
    }
}
