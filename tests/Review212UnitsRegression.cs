using RealtimeTranscription.Core;

internal static class Review212UnitsRegression
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); }

    private static void Reject(string before, string after)
    {
        Check(!ConfirmedCorrections.IsSafePair(before, after), $"单位变化被判为安全：{before} → {after}");
        var mapping = new TermData { Alias = before, Text = after, Scope = "default" };
        Check(!ConfirmedCorrections.AnalyzeMappings([mapping], "default").Single().Applicable,
            $"单位变化可启用：{before} → {after}");
        string raw = $"测量结果：{before}。";
        var applied = ConfirmedCorrections.Apply(raw, [mapping], "default");
        Check(applied.Text == raw && applied.Edits.Count == 0, $"单位变化被执行：{before} → {after}");
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        // Run a positive case first so a cold-start timeout cannot masquerade as a safe rejection.
        await test("单位保护保留普通专业词和数值单位不变的纠错", () =>
        {
            foreach (var (before, after) in new[]
            {
                ("原字能院", "原子能院"), ("朱娜", "JUNA"), ("DeepSeak", "DeepSeek"), ("卡搭尔", "卡塔尔"),
                ("Newtonien", "Newtonian"), ("Barnet", "Barnett"), ("AMANDAA", "AMANDA"),
                ("束流10皮安测亮", "束流10皮安测量"), ("截面5毫巴测亮", "截面5毫巴测量"),
                ("10millibarn测亮", "10millibarn测量"), ("10mA测亮", "10mA测量")
            })
            {
                Check(ConfirmedCorrections.IsSafePair(before, after), $"普通纠错被误拦截：{before} → {after}");
                var mapping = new TermData { Alias = before, Text = after, Scope = "default" };
                Check(ConfirmedCorrections.Apply(before + "。", [mapping], "default").Text == after + "。", before);
            }
            return Task.CompletedTask;
        });
        await test("中文单位词头变化不进入纠错学习或自动替换", () =>
        {
            foreach (var (before, after) in new[]
            {
                ("毫巴", "微巴"), ("10皮安", "10飞安"), ("皮安培", "飞安培"),
                ("阿托秒", "飞秒"), ("微欧姆", "毫欧姆"), ("纳库仑", "皮库仑"),
                ("5毫巴", "5纳巴"), ("10 毫巴", "10 微巴"), ("毫 巴", "微 巴"),
                ("束流十皮安", "束流十飞安"), ("电容皮法", "电容纳法"),
                ("容克", "昆克"), ("柔克", "亏克"), ("10安", "10伏")
            })
            {
                Reject(before, after);
                Reject(after, before);
                Check(CorrectionRules.Detect(before, after).Count == 0, $"仍提议学习：{before} → {after}");
            }
            var obsolete = new TermData { Alias = "毫巴", Text = "微巴", Scope = "default" };
            Check(ConfirmedCorrections.Apply("截面是5毫巴。", [obsolete], "default").Text == "截面是5毫巴。",
                "旧版已确认的毫巴映射仍改变数量级");
            return Task.CompletedTask;
        });
        await test("英文单位全名及复数的 SI 词头变化受到保护", () =>
        {
            foreach (var (before, after) in new[]
            {
                ("millibarn", "microbarn"), ("millibarns", "microbarns"),
                ("Millibarn", "Microbarn"), ("MILLIBARN", "MICROBARN"),
                ("picoampere", "femtoampere"), ("10 picoamperes", "10 femtoamperes"),
                ("milli barn", "micro barn"), ("milli-barn", "micro-barn"),
                ("microhenries", "millihenries"), ("megapascal", "kilopascal"),
                ("centimetres", "decimetres"), ("dekameters", "hectometers"),
                ("ronnagrams", "quettagrams"), ("rontoseconds", "quectoseconds"),
                ("electronvolts", "kiloelectronvolts"), ("barn", "bar")
            }) Reject(before, after);
            foreach (var (before, after) in new[] { ("millibarn", "microbarn"), ("picoamperes", "femtoamperes"), ("MILLIBARN", "MICROBARN") })
                Check(CorrectionRules.Detect(before, after).Count == 0, $"仍提议学习：{before} → {after}");
            return Task.CompletedTask;
        });
        await test("单位符号大小写、词头及兼容字符规范化保持数值安全", () =>
        {
            foreach (var (before, after) in new[]
            {
                ("10mA", "10MA"), ("10pA", "10fA"), ("10mb", "10μb"),
                ("10µb", "10nb"), ("10kPa", "10MPa"), ("10mΩ", "10MΩ"),
                ("10daN", "10hN"), ("10cm", "10dm"), ("10Rmol", "10Qmol"),
                ("10rs", "10qs"), ("１０ｐＡ", "１０ｆＡ"), ("10μm", "10μs"),
                ("5毫巴", "6毫巴"), ("5millibarn", "6millibarn")
            }) Reject(before, after);
            Check(ConfirmedCorrections.IsSafePair("１０μｂ测亮", "10µb测量"), "兼容字符的相同单位不应阻止术语修正");
            return Task.CompletedTask;
        });
    }
}
