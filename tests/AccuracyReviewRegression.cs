using System.Text;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

static class AccuracyReviewRegression
{
    static void Check(bool value, string reason = "准确度审查回归断言失败") { if (!value) throw new Exception(reason); }
    static TermData Mapping(string original, string corrected) => new() { Alias = original, Text = corrected };
    static TranscriptEngine Engine()
    {
        var engine = new TranscriptEngine(new(), wholeTurn: true); engine.StartTask("time", []); return engine;
    }
    static AsrEvent Sentence(int id, bool final, long begin, long? end, bool known = true) =>
        new("result-generated", "time", id, $"第{id}句。", final, BeginMs: begin, EndMs: end, BeginTimeKnown: known);
    static void Receive(TranscriptEngine engine, AsrEvent value, bool automatic = true) => engine.Receive(value, false, automatic, [], false);

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("中文正负号、指数与数量比较不成为自动纠错或学习候选", () =>
        {
            foreach (var (before, after) in new[]
            {
                ("温度负三度", "温度正三度"), ("十的负三次方", "十的正三次方"),
                ("截面小于三毫巴", "截面大于三毫巴"), ("束流至少三毫安", "束流至多三毫安"),
                ("数值－３", "数值＋３"), ("截面≤三毫巴", "截面≥三毫巴"),
                ("长度三平方毫米", "长度三立方毫米"), ("结果负0.3", "结果正0.3"),
                ("温度- 三度", "温度+ 三度"), ("β+衰变", "β-衰变"), ("贝塔负衰变", "贝塔正衰变")
            })
            {
                Check(!ConfirmedCorrections.IsSafePair(before, after), before);
                Check(ConfirmedCorrections.Apply(before, [Mapping(before, after)], "default").Text == before, before);
                Check(CorrectionRules.Detect(before, after).Count == 0, "不应提议学习：" + before);
            }
            return Task.CompletedTask;
        });
        await test("语义保护仍允许数值单位不变的专业术语修正", () =>
        {
            foreach (var (before, after) in new[] { ("朱娜", "JUNA"), ("负三度测亮", "负三度测量"), ("束流10keV测亮", "束流10keV测量") })
            {
                Check(ConfirmedCorrections.IsSafePair(before, after), before);
                Check(ConfirmedCorrections.Apply(before, [Mapping(before, after)], "default").Text == after, before);
            }
            return Task.CompletedTask;
        });
        await test("映射分析支持同词多别名并复用执行判定", () =>
        {
            var a = Mapping("朱娜", "JUNA"); var b = a with { Alias = "尤娜" };
            var states = ConfirmedCorrections.AnalyzeMappings([a, b], "default");
            Check(states.Count == 2 && states.All(s => s.Applicable && s.Reason.Length == 0));
            var result = ConfirmedCorrections.Apply("朱娜和尤娜。", [a, b], "default");
            Check(result.Text == "JUNA和JUNA。" && result.Edits.Count == 2);
            return Task.CompletedTask;
        });
        await test("冲突与替换链在分析和执行中有一致的失效原因", () =>
        {
            foreach (var terms in new[]
            {
                new[] { Mapping("朱娜", "JUNA"), Mapping("朱娜", "LUNA") },
                new[] { Mapping("朱娜", "JUNA"), Mapping("JUNA", "JUNO") },
                new[] { Mapping("人才", "人才盘点") },
                new[] { Mapping("温度负三度", "温度正三度") }
            })
            {
                Check(ConfirmedCorrections.AnalyzeMappings(terms, "default").All(s => !s.Applicable && s.Reason.Length > 0));
                string raw = string.Join("，", terms.Select(t => t.Alias));
                Check(ConfirmedCorrections.Apply(raw, terms, "default").Text == raw);
            }
            return Task.CompletedTask;
        });
        await test("保守润色删除明确相邻主语、助动词和指示词口吃", () =>
        {
            foreach (var (before, after) in new[]
            {
                ("我们我们先测试。", "我们先测试。"), ("你们你们可以检查设备。", "你们可以检查设备。"),
                ("我们需要需要测试。", "我们需要测试。"), ("可以可以保存数据。", "可以保存数据。"),
                ("这个这个实验需要调整。", "这个实验需要调整。"),
                ("嗯，我们我们先测试。", "我们先测试。")
            }) Check(PolishRules.Validate(before, after).Accepted, before);
            return Task.CompletedTask;
        });
        await test("已验证的标点补全和对应规范化可以与口吃删除组合", () =>
        {
            foreach (var (before, after) in new[]
            {
                ("我们先测试再记录结果", "我们先测试，再记录结果。"),
                ("我们我们先测试", "我们先测试。"),
                ("我们我们先测试再记录结果", "我们先测试，再记录结果。"),
                ("首先我们先测试。", "首先，我们先测试。"),
                ("实验已经完成", "实验已经完成。"),
                ("我们先测试,再记录结果.", "我们先测试，再记录结果。"),
                ("测试结束了吗?", "测试结束了吗？"),
                ("立即停止!", "立即停止！")
            }) Check(PolishRules.Validate(before, after).Accepted, before);
            Check(PolishRules.Validate("我们先测试再记录结果", "我们先测试，再记录结果").Accepted);
            return Task.CompletedTask;
        });
        await test("润色仍拒绝数值单位否定公式和任意文字重写", () =>
        {
            foreach (var (before, after) in new[]
            {
                ("温度负三度。", "温度正三度。"), ("束流10keV。", "束流10MeV。"),
                ("截面小于三毫巴。", "截面大于三毫巴。"), ("我们不不测试。", "我们不测试。"),
                ("我们我们不测试。", "我们不测试。"), ("x > 2", "x < 2"),
                ("我们先测试。", "我们先测量。"), ("测试结束了吗？", "测试结束了吗。"),
                ("人人都参加。", "人都参加。"), ("非常非常好。", "非常好。"),
                ("we we test", "we test"), ("电压3,000 V", "电压3，000 V")
            }) Check(!PolishRules.Validate(before, after).Accepted, before);
            Check(!PolishRules.Validate("我们我们先测试。", "我们先测试。", ["我们我们"]).Accepted);
            Check(!PolishRules.Validate("呃，呃。", "", ["呃，呃。"]).Accepted);
            return Task.CompletedTask;
        });
        await test("润色保护闭合和未闭合的引文代码公式，不在句中插入歧义标点", () =>
        {
            foreach (string raw in new[] { "“我们我们先测试。”,", "「我们我们先测试。", "'我们我们先测试。'", "`我们我们先测试。`", "$我们我们先测试。$", "(我们我们先测试。)", "“参数，，保持。", "```参数，，保持。" })
                Check(!PolishRules.Validate(raw, raw.Replace("我们我们", "我们").Replace("，，", "，")).Accepted, raw);
            foreach (var (before, after) in new[]
            {
                ("如果我们先测试再记录结果", "如果我们先测试，再记录结果。"),
                ("实验是否已经完成", "实验是否已经完成。"),
                ("我们先测试然后", "我们先测试，然后。"),
                ("下雨天留客天留我不留", "下雨天，留客天，留我不留。")
            }) Check(!PolishRules.Validate(before, after).Accepted, before);
            return Task.CompletedTask;
        });
        await test("长语音润色按登记编辑验证，不以编辑距离放行额外修改", () =>
        {
            string raw = string.Concat(Enumerable.Repeat("我们我们先测试。", 1500));
            string candidate = raw.Replace("我们我们", "我们");
            var timer = System.Diagnostics.Stopwatch.StartNew(); Check(PolishRules.Validate(raw, candidate).Accepted);
            Check(timer.ElapsedMilliseconds < 5000, "长文本校验过慢");
            Check(!PolishRules.Validate(raw, candidate[..^4] + "先删除。").Accepted);
            return Task.CompletedTask;
        });
        await test("ASR时间解析保留未知标记，合法零时刻与缺失时间不同", () =>
        {
            foreach (string value in new[] { "null", "-1", "\"missing\"" })
            {
                string json = """{"header":{"event":"result-generated","task_id":"time"},"payload":{"output":{"sentence":{"sentence_id":1,"text":"测试","sentence_end":false,"begin_time":VALUE,"end_time":-1}}}}""".Replace("VALUE", value);
                var e = BailianProtocol.Parse(Encoding.UTF8.GetBytes(json)); Check(e.BeginMs == 0 && !e.BeginTimeKnown && e.EndMs == null);
            }
            var valid = BailianProtocol.Parse(Encoding.UTF8.GetBytes("""{"header":{"event":"result-generated","task_id":"time"},"payload":{"output":{"sentence":{"sentence_id":1,"text":"测试","sentence_end":true,"begin_time":0,"end_time":850}}}}"""));
            Check(valid.BeginTimeKnown && valid.BeginMs == 0 && valid.EndMs == 850);
            return Task.CompletedTask;
        });
        await test("首次partial缺时间时final校准真实停顿并自动分段", () =>
        {
            var e = Engine(); Receive(e, Sentence(1, true, 0, 800)); Receive(e, Sentence(2, false, 0, null, false));
            Check(!e.Segments[1].ParagraphBefore); Receive(e, Sentence(2, true, 3000, 4000));
            Check(e.Segments[1].BeginMs == 3000 && e.Segments[1].ParagraphBefore);
            Check(TranscriptText.Render(e.Segments) == "第1句。\r\n\r\n第2句。");
            return Task.CompletedTask;
        });
        await test("final修正初始时间时撤回错误自动分段且未知时间不覆盖有效值", () =>
        {
            var e = Engine(); Receive(e, Sentence(1, true, 0, 800)); Receive(e, Sentence(2, false, 5000, null));
            Check(e.Segments[1].ParagraphBefore); Receive(e, Sentence(2, true, 1000, 1800));
            Check(e.Segments[1].BeginMs == 1000 && !e.Segments[1].ParagraphBefore);
            Receive(e, Sentence(3, false, 4000, null)); Receive(e, Sentence(3, true, 0, 4800, false));
            Check(e.Segments[2].BeginMs == 4000 && e.Segments[2].ParagraphBefore);
            return Task.CompletedTask;
        });
        await test("乱序final按相邻句时间校准分段并保持正文顺序", () =>
        {
            var e = Engine(); Receive(e, Sentence(2, true, 3000, 4000)); Check(!e.Segments.Single().ParagraphBefore);
            Receive(e, Sentence(1, true, 0, 800));
            Check(e.Segments[1].ParagraphBefore && TranscriptText.Render(e.Segments) == "第1句。\r\n\r\n第2句。");
            return Task.CompletedTask;
        });
        await test("时间校准保留手动分段并尊重自动分段开关和阈值", () =>
        {
            var manual = Engine(); Receive(manual, Sentence(1, true, 0, 800), false); manual.Paragraph();
            Receive(manual, Sentence(2, false, 5000, null), false); Receive(manual, Sentence(2, true, 1000, 1800), false);
            Check(manual.Segments[1].ParagraphBefore);
            foreach (var (gap, enabled, expected) in new[] { (1999, true, false), (2000, true, true), (5000, false, false) })
            {
                var e = Engine(); Receive(e, Sentence(1, true, 0, 800), enabled); Receive(e, Sentence(2, true, 800 + gap, 7000), enabled);
                Check(e.Segments[1].ParagraphBefore == expected);
            }
            return Task.CompletedTask;
        });
    }
}
