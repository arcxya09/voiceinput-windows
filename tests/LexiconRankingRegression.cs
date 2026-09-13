using RealtimeTranscription.Core;

internal static class LexiconRankingRegression
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        var now = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        static void Check(bool value) { if (!value) throw new Exception("Lexicon ranking regression assertion failed"); }
        static Task Sync(Action action) { action(); return Task.CompletedTask; }

        await test("动态热词仅选择启用及当前范围，规范化后由项目词覆盖", () => Sync(() =>
        {
            var project = new TermData { Id = "project", Text = "é", Scope = "p", Weight = 1, UpdatedAt = now };
            var global = new TermData { Id = "global", Text = " e\u0301 ", Scope = "*", Weight = 5, Pinned = true, UsageCount = 100, LastUsedAt = now };
            var terms = new[] { global, project, new TermData { Text = "候选", Scope = "p", State = TermState.Candidate },
                new TermData { Text = "禁用", Scope = "p", State = TermState.Disabled }, new TermData { Text = "别的项目", Scope = "other" } };
            var picked = Lexicon.Select(terms, "p", now);
            Check(picked.Count == 1 && ReferenceEquals(picked[0], project));
            var cased = Lexicon.Select([project with { Text = "JUNA" }, global with { Text = "juna" }], "p", now);
            Check(cased.Count == 1 && cased[0].Text == "JUNA" && cased[0].Scope == "p");
            Check(terms.Count(t => t.State == TermState.Candidate) == 1 && terms.Count(t => t.State == TermState.Disabled) == 1);
        }));
        await test("常用全局词可进入两百条置顶项目词占满的热词列表", () => Sync(() =>
        {
            var terms = Enumerable.Range(0, 220).Select(i => new TermData { Id = $"p{i:D3}", Text = "项目词" + i, Scope = "p", Weight = 5, Pinned = true, UpdatedAt = now }).ToList();
            var frequent = new TermData { Id = "global", Text = "常用全局词", Scope = "*", Weight = 3, UsageCount = 100, LastUsedAt = now, UpdatedAt = now };
            terms.Add(frequent);
            var picked = Lexicon.Select(terms, "p", now);
            Check(picked.Count == 200 && picked[0].Id == frequent.Id && picked.Select(t => t.Id).Distinct().Count() == 200);
        }));
        await test("使用频率与纠错反馈提高排序，久未使用的加成逐渐减弱", () => Sync(() =>
        {
            var basic = new TermData { Id = "base", Text = "术语", Scope = "*", UpdatedAt = now };
            var frequent = basic with { UsageCount = 100, LastUsedAt = now };
            var stale = frequent with { LastUsedAt = now.AddDays(-365) };
            var corrected = basic with { CorrectionCount = 3, LastCorrectedAt = now };
            Check(Lexicon.RankingScore(frequent, "p", now) > Lexicon.RankingScore(stale, "p", now));
            Check(Lexicon.RankingScore(corrected, "p", now) > Lexicon.RankingScore(basic, "p", now));
            Check(Lexicon.RankingScore(stale, "p", now) < Lexicon.RankingScore(basic with { Pinned = true, Scope = "p" }, "p", now));
            Check(Lexicon.RankingScore(frequent, "p", now.AddDays(90)) < Lexicon.RankingScore(frequent, "p", now));
        }));
        await test("实际发送权重随可信使用提高且保留手动权重", () => Sync(() =>
        {
            var term = new TermData { Text = "术语", Scope = "p", Weight = 2, UsageCount = 3, LastUsedAt = now };
            Check(Lexicon.EffectiveWeight(term, now) == 3 && term.Weight == 2);
            Check(Lexicon.EffectiveWeight(term with { UsageCount = 100 }, now) == 4);
            Check(Lexicon.EffectiveWeight(term with { Weight = 5, UsageCount = long.MaxValue, CorrectionCount = long.MaxValue, LastCorrectedAt = now }, now) == 5);
            Check(Lexicon.EffectiveWeight(term with { LastUsedAt = now.AddYears(-2) }, now) == 2);
            Check(Lexicon.EffectiveWeight(term with { UsageCount = 0, LastUsedAt = now }, now) == 2);
            Check(Lexicon.Select([term], "p", now)[0].Weight == 2);
            Check(term.UsageCount == 3 && term.LastUsedAt == now && term.CorrectionCount == 0 && term.LastCorrectedAt == null);
        }));
        await test("频率加成有上限，未来时间戳不放大评分", () => Sync(() =>
        {
            var term = new TermData { Text = "术语", Scope = "p", Weight = 3, Pinned = true, UsageCount = long.MaxValue, CorrectionCount = long.MaxValue, LastUsedAt = now, LastCorrectedAt = now };
            double current = Lexicon.RankingScore(term, "p", now);
            Check(double.IsFinite(current) && current == 96);
            Check(Lexicon.RankingScore(term with { LastUsedAt = now.AddDays(1), LastCorrectedAt = now.AddDays(1) }, "p", now) == current);
            Check(Lexicon.RankingScore(term with { UsageCount = 0, CorrectionCount = 0 }, "p", now) == 42);
        }));
        await test("同分热词排序与输入枚举顺序无关", () => Sync(() =>
        {
            var terms = Enumerable.Range(0, 230).Select(i => new TermData { Id = $"id{i:D3}", Text = "术语" + i, Scope = "*", UpdatedAt = now }).ToArray();
            var first = Lexicon.Select(terms, "p", now).Select(t => t.Id).ToArray();
            Check(first.SequenceEqual(Lexicon.Select(terms.Reverse(), "p", now).Select(t => t.Id)));
            Check(first.First() == "id000" && first.Last() == "id199");
        }));
    }
}
