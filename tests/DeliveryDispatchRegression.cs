using RealtimeTranscription.Core;

internal static class DeliveryDispatchRegression
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); }
    private static Task<bool> Valid(CancellationToken _) => Task.FromResult(true);
    private static Task NoWait(CancellationToken _) => Task.CompletedTask;

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("2.1.4 原句逐字符依序派发，句号保留在末尾且字符之间让步", async () =>
        {
            const string sentence = "感觉好像就不太准确。";
            var sequence = new List<string>(); int validations = 0;
            var result = await PacedTextInput.SendAsync(sentence, scalar =>
            {
                sequence.Add("send:" + scalar); return scalar.Length * 2;
            }, () => true, _ => { validations++; return Task.FromResult(true); }, CancellationToken.None,
                _ => { sequence.Add("pace"); return Task.CompletedTask; });
            var expected = sentence.SelectMany((character, index) => index == sentence.Length - 1
                ? new[] { "send:" + character } : new[] { "send:" + character, "pace" });
            Check(result == new PacedInputResult("Sent", 20) && sequence.SequenceEqual(expected),
                "原句、派发顺序或字符之间的让步发生变化。");
            Check(validations == 0, "短句不应额外执行每字符的辅助功能查询。");
        });

        await test("2.1.4 派发等待字符间让步完成，不预先排队后续字符", async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = new List<string>(); int pauses = 0;
            var sending = PacedTextInput.SendAsync("感。", scalar => { sent.Add(scalar); return 2; },
                () => true, Valid, timeout.Token, cancellation =>
                { pauses++; return proceed.Task.WaitAsync(cancellation); });
            Check(sent.SequenceEqual(new[] { "感" }) && pauses == 1 && !sending.IsCompleted,
                "前一个字符尚未让步，后续字符已被排队。");
            proceed.SetResult();
            Check(await sending == new PacedInputResult("Sent", 4) && sent.SequenceEqual(new[] { "感", "。" }) && pauses == 1,
                "让步后未依序完成派发，或末字之后额外等待。");
        });

        await test("2.1.4 辅助平面字符的代理对同包派发且不会零长度循环", async () =>
        {
            const string sentence = "😀A𠮷。";
            var sent = new List<string>();
            var result = await PacedTextInput.SendAsync(sentence, scalar =>
            { sent.Add(scalar); return scalar.Length * 2; }, () => true, Valid, CancellationToken.None, NoWait);
            Check(result == new PacedInputResult("Sent", sentence.Length * 2) &&
                sent.SequenceEqual(new[] { "😀", "A", "𠮷", "。" }), "代理对被拆开、顺序改变或丢失字符。");
            foreach (string malformed in new[] { "\ud83d", "\ude00", "A\ud83dB" })
            {
                sent.Clear();
                result = await PacedTextInput.SendAsync(malformed, scalar =>
                { sent.Add(scalar); return scalar.Length * 2; }, () => true, Valid, CancellationToken.None, NoWait);
                Check(result == new PacedInputResult(malformed.StartsWith('A') ? "Partial" : "Blocked", malformed.StartsWith('A') ? 2 : 0)
                    && sent.All(scalar => scalar == "A"), "不完整的代理对不应成为原生输入事件。");
            }
        });

        await test("2.1.4 取消和焦点变化停止后续文字并保留实际接收计数", async () =>
        {
            foreach (bool cancel in new[] { false, true })
            {
                using var cancellation = new CancellationTokenSource();
                bool safe = true; var sent = new List<string>();
                var result = await PacedTextInput.SendAsync("感觉。", scalar =>
                { sent.Add(scalar); return 2; }, () => safe, Valid, cancellation.Token, token =>
                {
                    if (cancel) { cancellation.Cancel(); return Task.FromCanceled(token); }
                    safe = false; return Task.CompletedTask;
                });
                Check(result == new PacedInputResult("Partial", 2) && sent.SequenceEqual(new[] { "感" }),
                    "取消或焦点改变后继续输入，或部分接收计数丢失。");
            }
            using var alreadyCanceled = new CancellationTokenSource(); alreadyCanceled.Cancel();
            int sends = 0;
            var blocked = await PacedTextInput.SendAsync("感", _ => { sends++; return 2; }, () => true,
                Valid, alreadyCanceled.Token, NoWait);
            var changed = await PacedTextInput.SendAsync("感", _ => { sends++; return 2; }, () => false,
                Valid, CancellationToken.None, NoWait);
            Check(blocked == new PacedInputResult("Blocked") && changed == blocked && sends == 0,
                "开始前取消或目标已变化时仍发送了文字。");
        });

        await test("2.1.4 原生短发送立即终止，不补发正文或丢失奇数接收计数", async () =>
        {
            foreach (int prefix in new[] { 0, 1 })
            foreach (int shortCount in new[] { 0, 1, 2, 3 })
            {
                var sent = new List<string>();
                var result = await PacedTextInput.SendAsync((prefix == 1 ? "A" : "") + "😀。", scalar =>
                {
                    sent.Add(scalar); return scalar == "A" ? 2 : shortCount;
                }, () => true, Valid, CancellationToken.None, NoWait);
                int accepted = prefix * 2 + shortCount;
                Check(result == new PacedInputResult(accepted > 0 ? "Partial" : "Blocked", accepted) &&
                    sent.SequenceEqual(prefix == 1 ? new[] { "A", "😀" } : new[] { "😀" }),
                    "原生短发送后重发了正文、继续了末尾句号，或计数不正确。");
            }
        });

        await test("2.1.4 派发或让步异常保留Partial状态，尚未输入时保持Blocked", async () =>
        {
            foreach (string stage in new[] { "send", "pace", "safe" })
            {
                int sends = 0; bool yielded = false;
                var result = await PacedTextInput.SendAsync("感觉。", _ =>
                {
                    if (stage == "send" && sends == 1) throw new InvalidOperationException("native failure");
                    sends++; return 2;
                }, () => stage == "safe" && yielded ? throw new InvalidOperationException("focus failure") : true,
                    Valid, CancellationToken.None, _ =>
                    {
                        yielded = true;
                        if (stage == "pace") throw new InvalidOperationException("pace failure");
                        return Task.CompletedTask;
                    });
                Check(result == new PacedInputResult("Partial", 2) && sends == 1,
                    "已有原生接收后异常抹掉了Partial状态或继续发送。");
            }
            var blocked = await PacedTextInput.SendAsync("感", _ => throw new InvalidOperationException("before acceptance"),
                () => true, Valid, CancellationToken.None, NoWait);
            Check(blocked == new PacedInputResult("Blocked"), "第一次原生调用失败时不应记录虚假的接收计数。");
        });

        await test("2.1.4 辅助功能校验保持128单位批次节奏并避开代理对中间", async () =>
        {
            foreach (bool supplementary in new[] { false, true })
            {
                string sentence = supplementary ? new string('A', 127) + "😀" + new string('B', 128) : new string('A', 257);
                int units = 0; var positions = new List<int>();
                var result = await PacedTextInput.SendAsync(sentence, scalar => { units += scalar.Length; return scalar.Length * 2; },
                    () => true, _ => { positions.Add(units); return Task.FromResult(true); }, CancellationToken.None, NoWait);
                Check(result == new PacedInputResult("Sent", sentence.Length * 2) &&
                    positions.SequenceEqual(supplementary ? new[] { 127, 255 } : new[] { 128, 256 }),
                    "辅助功能查询频率变为每字符，或在代理对中间执行。");
            }
        });

        await test("2.1.4 后续辅助功能校验失败或取消后保留已输入批次且不再派发", async () =>
        {
            foreach (string failure in new[] { "changed", "throw", "cancel" })
            {
                using var cancellation = new CancellationTokenSource(); int sends = 0, validations = 0;
                var result = await PacedTextInput.SendAsync(new string('A', 129), _ => { sends++; return 2; },
                    () => true, _ =>
                    {
                        validations++;
                        if (failure == "throw") throw new InvalidOperationException("provider failure");
                        if (failure == "cancel") { cancellation.Cancel(); return Task.FromResult(true); }
                        return Task.FromResult(false);
                    }, cancellation.Token, NoWait);
                Check(result == new PacedInputResult("Partial", 256) && sends == 128 && validations == 1,
                    "辅助功能校验失败后仍继续发送或丢失已接收批次。");
            }
        });

        await test("2.1.4 总时限包含逐字让步，超时及末字取消保留接收计数", async () =>
        {
            long now = 0; int sends = 0;
            var expired = await PacedTextInput.SendAsync("感觉", _ => { sends++; return 2; }, () => true,
                Valid, CancellationToken.None, _ => { now = InputSafety.DeliveryBudgetMilliseconds(2); return Task.CompletedTask; }, () => now);
            Check(expired == new PacedInputResult("Partial", 2) && sends == 1, "超过总时限后仍派发后续字符。");
            using var cancellation = new CancellationTokenSource();
            var canceledAtEnd = await PacedTextInput.SendAsync("感", _ => { cancellation.Cancel(); return 2; },
                () => true, Valid, cancellation.Token, NoWait);
            Check(canceledAtEnd == new PacedInputResult("Partial", 2), "末字派发时发生取消仍被记录为Sent。");
            Check(InputSafety.DeliveryBudgetMilliseconds(128) >= 6000 + 127 * 16 &&
                InputSafety.DeliveryBudgetMilliseconds(int.MaxValue) == 600000 && InputSafety.DeliveryBudgetMilliseconds(0) == 2000,
                "逐字让步预算缺失、溢出，或总时限失去上限。");
        });
    }
}
