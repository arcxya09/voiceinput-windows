using RealtimeTranscription.Core;

internal static class ClipboardPasteRegression
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("2.1.5 两条实际错字原句整段写入一次并只发起一次粘贴", async () =>
        {
            foreach (string sentence in new[] { "感觉好像就不太准确。", "测试一下这次的输入是否准确。看起来没有什么问题。" })
            {
                var prepared = new List<string>(); int sends = 0;
                var result = await ClipboardPaste.SendAsync(sentence, (text, _) =>
                { prepared.Add(text); return Task.FromResult<uint?>(17); }, sequence => sequence == 17,
                    () => true, () => { sends++; return 4; }, CancellationToken.None);
                Check(result == new PasteDispatchResult("Sent", 4) && sends == 1 &&
                    prepared.SequenceEqual(new[] { sentence }), "原句被分段、更改，或重复写入、粘贴。");
            }
        });

        await test("2.1.5 多行、长段、辅助平面和组合字符原样整段粘贴", async () =>
        {
            string sentence = string.Concat(Enumerable.Repeat("😀𠮷中文，e\u0301。\r\n\t", 3000));
            int preparations = 0, sends = 0;
            var result = await ClipboardPaste.SendAsync(sentence, (text, _) =>
            {
                preparations++;
                Check(text == sentence, "整段正文被规范化、截断或重排。");
                return Task.FromResult<uint?>(uint.MaxValue);
            }, sequence => sequence == uint.MaxValue, () => true, () => { sends++; return 4; }, CancellationToken.None);
            Check(result == new PasteDispatchResult("Sent", 4) && preparations == 1 && sends == 1,
                "长段正文产生了多次剪贴板写入或多次快捷键。");
        });

        await test("2.1.5 空内容、NUL、不完整代理对和超长内容在任何副作用前拒绝", async () =>
        {
            foreach (string text in new[] { "", "A\0B", "\0", "\ud83d", "\ude00", "A\ud83dB", "A\ude00B",
                "\ud83d\ud83d", new string('字', ClipboardPaste.MaxCharacters + 1) })
            {
                int callbacks = 0;
                var result = await ClipboardPaste.SendAsync(text, (_, _) =>
                { callbacks++; return Task.FromResult<uint?>(1); }, _ => { callbacks++; return true; },
                    () => { callbacks++; return true; }, () => { callbacks++; return 4; }, CancellationToken.None);
                Check(result == new PasteDispatchResult(text.Length == 0 ? "Empty" : "Blocked") && callbacks == 0 &&
                    !ClipboardPaste.IsSupportedText(text), "不支持的文本触发了目标查询、剪贴板操作或快捷键。");
            }
            Check(ClipboardPaste.IsSupportedText(new string('字', ClipboardPaste.MaxCharacters)) &&
                ClipboardPaste.IsSupportedText("😀𠮷\r\n\t ") && !ClipboardPaste.IsSupportedText(null),
                "合法长度边界或完整代理对被拒绝。");
        });

        await test("2.1.5 开始前取消、目标变化和校验异常均不操作剪贴板", async () =>
        {
            foreach (string interruption in new[] { "canceled", "unsafe", "throw", "cancel-in-safe" })
            {
                using var cancellation = new CancellationTokenSource();
                if (interruption == "canceled") cancellation.Cancel();
                int preparations = 0, sends = 0;
                var result = await ClipboardPaste.SendAsync("完整结果。", (_, _) =>
                { preparations++; return Task.FromResult<uint?>(1); }, _ => true, () =>
                {
                    if (interruption == "throw") throw new InvalidOperationException("target unavailable");
                    if (interruption == "cancel-in-safe") cancellation.Cancel();
                    return interruption != "unsafe";
                }, () => { sends++; return 4; }, cancellation.Token);
                Check(result == new PasteDispatchResult("Blocked") && preparations == 0 && sends == 0,
                    "开始前中断后仍写入剪贴板或粘贴。");
            }
        });

        await test("2.1.5 准备完成前不粘贴，准备期间取消或焦点变化后不派发", async () =>
        {
            foreach (bool cancel in new[] { false, true })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var proceed = new TaskCompletionSource<uint?>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool safe = true; int preparations = 0, sends = 0;
                var sending = ClipboardPaste.SendAsync("整段。", (_, _) =>
                { preparations++; return proceed.Task; }, _ => true, () => safe,
                    () => { sends++; return 4; }, timeout.Token);
                Check(preparations == 1 && sends == 0 && !sending.IsCompleted, "准备尚未完成就派发粘贴。");
                if (cancel) timeout.Cancel(); else safe = false;
                proceed.SetResult(11);
                Check(await sending == new PasteDispatchResult("Blocked") && preparations == 1 && sends == 0,
                    "准备期间中断后仍粘贴或重新写入。");
            }
        });

        await test("2.1.5 准备失败、取消和异常均终止且不重试", async () =>
        {
            foreach (string failure in new[] { "null", "throw", "canceled" })
            {
                int preparations = 0, sends = 0;
                var result = await ClipboardPaste.SendAsync("正文。", (_, _) =>
                {
                    preparations++;
                    return failure switch
                    {
                        "throw" => throw new InvalidOperationException("clipboard busy"),
                        "canceled" => Task.FromCanceled<uint?>(new CancellationToken(true)),
                        _ => Task.FromResult<uint?>(null)
                    };
                }, _ => true, () => true, () => { sends++; return 4; }, CancellationToken.None);
                Check(result == new PasteDispatchResult("Blocked") && preparations == 1 && sends == 0,
                    "准备失败后重试、回退或发起了粘贴。");
            }
        });

        await test("2.1.5 准备后剪贴板被改写时放弃粘贴且不覆盖新复制内容", async () =>
        {
            foreach (string failure in new[] { "changed", "throw", "cancel" })
            {
                using var cancellation = new CancellationTokenSource();
                string clipboard = "此前内容"; int preparations = 0, sends = 0;
                var result = await ClipboardPaste.SendAsync("识别内容。", (text, _) =>
                { preparations++; clipboard = text; return Task.FromResult<uint?>(20); }, sequence =>
                {
                    Check(sequence == 20, "未校验本次写入的剪贴板序号。");
                    clipboard = "用户新复制内容";
                    if (failure == "throw") throw new InvalidOperationException("clipboard unavailable");
                    if (failure == "cancel") cancellation.Cancel();
                    return failure == "cancel";
                }, () => true, () => { sends++; return 4; }, cancellation.Token);
                Check(result == new PasteDispatchResult("Blocked") && preparations == 1 && sends == 0 &&
                    clipboard == "用户新复制内容", "剪贴板变更后继续粘贴、重写或恢复了旧内容。");
            }
        });

        await test("2.1.5 快捷键仅尝试一次并保留全部原生接收计数", async () =>
        {
            foreach (int count in new[] { 0, 1, 2, 3, 4, -1, 5 })
            {
                int preparations = 0, sends = 0;
                var result = await ClipboardPaste.SendAsync("完整正文。", (_, _) =>
                { preparations++; return Task.FromResult<uint?>(7); }, sequence => sequence == 7,
                    () => true, () => { sends++; return count; }, CancellationToken.None);
                string state = count is 0 or 1 ? "Blocked" : count == 4 ? "Sent" : "Unknown";
                Check(result == new PasteDispatchResult(state, count) && preparations == 1 && sends == 1,
                    "短发送被当作成功、接收计数丢失或重新派发了快捷键。");
            }
        });

        await test("2.1.5 原生派发抛异常标为未知且不重发整段文本", async () =>
        {
            int preparations = 0, sends = 0;
            var result = await ClipboardPaste.SendAsync("正文。", (_, _) =>
            { preparations++; return Task.FromResult<uint?>(3); }, _ => true, () => true, () =>
            { sends++; throw new InvalidOperationException("native submission outcome unavailable"); }, CancellationToken.None);
            Check(result == new PasteDispatchResult("Unknown") && preparations == 1 && sends == 1,
                "原生派发异常误报未发送，或重复粘贴。");
        });

        await test("2.1.5 派发时取消、用户操作或剪贴板变化后保留未知状态", async () =>
        {
            foreach (string interruption in new[] { "cancel", "physical", "clipboard", "throw-physical", "throw-clipboard" })
            {
                using var cancellation = new CancellationTokenSource();
                int sends = 0, preparations = 0;
                var result = await ClipboardPaste.SendAsync("正文。", (_, _) =>
                { preparations++; return Task.FromResult<uint?>(9); }, _ =>
                {
                    if (sends > 0 && interruption == "throw-clipboard") throw new InvalidOperationException("clipboard unavailable");
                    return sends == 0 || interruption != "clipboard";
                }, () => true, () =>
                { sends++; if (interruption == "cancel") cancellation.Cancel(); return 4; }, cancellation.Token, () =>
                {
                    if (interruption == "throw-physical") throw new InvalidOperationException("target unavailable");
                    return interruption != "physical";
                });
                Check(result == new PasteDispatchResult("Unknown", 4) && preparations == 1 && sends == 1,
                    "派发期间中断后误报成功、丢失已接收计数或重试。");
            }
        });

        await test("2.1.5 派发后不把仍排队的注入Ctrl状态当作用户操作", async () =>
        {
            bool sent = false;
            var result = await ClipboardPaste.SendAsync("正文。", (_, _) => Task.FromResult<uint?>(1),
                _ => true, () => !sent, () => { sent = true; return 4; }, CancellationToken.None, () => true);
            Check(result == new PasteDispatchResult("Sent", 4), "派发后的检查误用了包含注入修饰键的前置校验。");
        });
    }
}
