using RealtimeTranscription.Core;

internal static class CorrectionMappingStorageRegression
{
    private static void Check(bool condition, string why = "自动纠错存储断言失败")
    {
        if (!condition) throw new Exception(why);
    }

    private static async Task<CorrectionCandidate> Candidate(ControllerFixture fixture)
    {
        var source = await fixture.Seed("请安排人材盘点。");
        await fixture.App.LoadSessionAsync(source.Session);
        await fixture.App.EditAsync(source.Segment.Id, "请安排人才盘点。");
        await fixture.Settle();
        return (await fixture.App.Repository.CorrectionsAsync("default")).Single();
    }

    private static CorrectionApproval Approval(CorrectionCandidate candidate, bool automatic, string scope = "default") =>
        new(candidate.Corrected, candidate.Original, "专业术语", scope, 4, automatic);

    private static async Task Reject(Func<Task> operation)
    {
        try { await operation(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("应拒绝未获确认或已失效的自动纠错操作");
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("旧版五参数纠错确认仍只启用热词，不自动替换", async () =>
        {
            await using var f = await ControllerFixture.Create();
            var candidate = await Candidate(f);
            var approval = new CorrectionApproval(candidate.Corrected, candidate.Original, "专业术语", "default", 4);
            Check(!approval.AutomaticReplacement);
            await f.App.ConfirmCorrectionAsync(candidate, approval);
            Check(f.App.Terms.Count == 1 && (await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 0);
            await f.Reopen();
            Check(!(await f.App.Repository.CorrectionsAsync("default")).Single().AutomaticReplacement);
            Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 0);
        });

        await test("显式确认自动纠正后返回原词映射，重启后仍生效", async () =>
        {
            await using var f = await ControllerFixture.Create();
            var candidate = await Candidate(f);
            Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 0);
            await f.App.ConfirmCorrectionAsync(candidate, Approval(candidate, true));
            var learned = f.App.Terms.Single();
            var active = (await f.App.Repository.ActiveCorrectionTermsAsync("default")).Single();
            Check(active.Id == learned.Id && active.Text == "人才盘点" && active.Alias == "人材盘点");
            var saved = (await f.App.Repository.CorrectionsAsync("default")).Single();
            Check(saved.AutomaticReplacement && saved.LearnedAlias == active.Alias && saved.State == CorrectionState.Learned);
            await f.Reopen();
            Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Single().Id == learned.Id);
        });

        await test("候选、禁用词和导入别名均不能伪造已确认的自动纠错", async () =>
        {
            await using var f = await ControllerFixture.Create();
            var pending = await Candidate(f);
            await f.App.Repository.ImportUserTermsAsync([
                new TermData { Text = "待确认词", Alias = "待确认错词", State = TermState.Candidate, Origin = "Imported" },
                new TermData { Text = "禁用词", Alias = "禁用错词", State = TermState.Disabled, Origin = "Imported" },
                new TermData { Text = "导入词", Alias = "导入错词", State = TermState.Enabled, Origin = "Imported" },
                new TermData { Text = "伪学习词", Alias = "伪学习错词", State = TermState.Enabled, Origin = "CorrectionLearning" }
            ]);
            Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 0);
            await Reject(() => f.App.Repository.SetAutomaticCorrectionAsync(pending.Id, "default", true));
            Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 0);
        });

        await test("已学习旧词可显式开启自动纠错，关闭后即时停止", async () =>
        {
            await using var f = await ControllerFixture.Create();
            var candidate = await Candidate(f);
            await f.App.ConfirmCorrectionAsync(candidate, Approval(candidate, false));
            await f.App.Repository.SetAutomaticCorrectionAsync(candidate.Id, "default", true);
            Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 1);
            await Reject(() => f.App.Repository.SetAutomaticCorrectionAsync(candidate.Id, "other", false));
            Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 1);
            await f.App.Repository.SetAutomaticCorrectionAsync(candidate.Id, "default", false);
            Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 0);
            Check((await f.App.Repository.TermsAsync("default")).Single().State == TermState.Enabled);
            await f.Reopen();
            Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 0);
        });

        await test("已确认映射对应词条禁用或退回候选后不再生效", async () =>
        {
            foreach (var state in new[] { TermState.Disabled, TermState.Candidate })
            {
                await using var f = await ControllerFixture.Create();
                var candidate = await Candidate(f);
                await f.App.ConfirmCorrectionAsync(candidate, Approval(candidate, true));
                await f.App.SaveTermAsync(f.App.Terms.Single() with { State = state });
                Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 0, state.ToString());
                await Reject(() => f.App.Repository.SetAutomaticCorrectionAsync(candidate.Id, "default", true));
            }
        });

        await test("修改标准词或旧写法使旧自动纠错确认失效", async () =>
        {
            foreach (bool rename in new[] { true, false })
            {
                await using var f = await ControllerFixture.Create();
                var candidate = await Candidate(f);
                await f.App.ConfirmCorrectionAsync(candidate, Approval(candidate, true));
                var term = f.App.Terms.Single();
                await f.App.SaveTermAsync(rename ? term with { Text = "人才盘点系统" } : term with { Alias = "另一旧写法" });
                Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 0, rename ? "改标准词" : "改旧写法");
                Check((await f.App.Repository.CorrectionsAsync("default")).Single().ReplacementLabel == "规则已失效");
                await f.Reopen();
                Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 0);
            }
        });

        await test("撤销学习或删除词条同时移除自动纠错映射", async () =>
        {
            foreach (bool revoke in new[] { true, false })
            {
                await using var f = await ControllerFixture.Create();
                var candidate = await Candidate(f);
                await f.App.ConfirmCorrectionAsync(candidate, Approval(candidate, true));
                if (revoke) await f.App.RevokeCorrectionAsync(candidate);
                else await f.App.DeleteTermAsync(f.App.Terms.Single());
                Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 0);
                var saved = (await f.App.Repository.CorrectionsAsync("default")).Single();
                Check(saved.State == CorrectionState.Ignored && saved.TermId == null);
                await Reject(() => f.App.Repository.SetAutomaticCorrectionAsync(candidate.Id, "default", true));
            }
        });

        await test("全局自动纠错跨项目生效，本项目映射保持隔离", async () =>
        {
            foreach (string scope in new[] { "*", "default" })
            {
                await using var f = await ControllerFixture.Create();
                var candidate = await Candidate(f);
                await f.App.ConfirmCorrectionAsync(candidate, Approval(candidate, true, scope));
                Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 1);
                await f.App.CreateProjectAsync("另一个项目");
                string project = f.App.Settings.ProjectId;
                Check(project != "default");
                Check((await f.App.Repository.ActiveCorrectionTermsAsync(project)).Count == (scope == "*" ? 1 : 0));
                await Reject(() => f.App.Repository.SetAutomaticCorrectionAsync(candidate.Id, project, false));
            }
        });

        await test("关闭动态调整时确认纠错不会回填词频", async () =>
        {
            await using var f = await ControllerFixture.Create();
            var candidate = await Candidate(f);
            await f.App.Repository.ConfirmCorrectionAsync(candidate.Id,"default",Approval(candidate,true),learnUsage:false);
            var term=(await f.App.Repository.TermsAsync("default")).Single();
            Check(term.UsageCount==0&&term.CorrectionCount==0);
            Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count==1);
        });

        await test("新增词频统计不改变词条修订号，之后仍可撤销学习", async () =>
        {
            await using var f = await ControllerFixture.Create();
            var candidate = await Candidate(f);
            await f.App.ConfirmCorrectionAsync(candidate, Approval(candidate, true));
            var before = (await f.App.Repository.TermsAsync("default")).Single();
            var source = await f.Seed("人才盘点已经完成。");
            await f.App.Repository.SaveSegmentAsync(source.Session, source.Segment, learnUsage: true);
            var after = (await f.App.Repository.TermsAsync("default")).Single();
            Check(after.UsageCount == before.UsageCount + 1 && after.Revision == before.Revision);
            Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Single().Id == after.Id);
            await f.App.RevokeCorrectionAsync(candidate);
            Check((await f.App.Repository.TermsAsync("default")).Count == 0);
            Check((await f.App.Repository.ActiveCorrectionTermsAsync("default")).Count == 0);
        });
    }
}
