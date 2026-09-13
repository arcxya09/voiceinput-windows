namespace RealtimeTranscription.Core;

public record PolishWork(string SessionId, long Generation, string SegmentId, long EditRevision, long Operation, long Deadline, string Raw, string Previous, string[] ProtectedTerms);

/// <summary>Pure state machine. Its caller serializes every operation through one actor.</summary>
public sealed partial class TranscriptEngine
{
    private sealed class TaskState(int order, string[] terms) { public int Order = order; public bool Sealed; public long? GapSince; public string[] Terms = terms; }
    private readonly Dictionary<string, TaskState> tasks = [];
    private readonly Dictionary<string, SegmentData> segments = [];
    private readonly Dictionary<string, PolishWork> pending = [];
    private readonly HashSet<string> knownBeginnings = [], explicitParagraphs = [], automaticParagraphs = [];
    private readonly Func<long> clock;
    private int taskOrder, sequence;
    private bool nextParagraph;
    private bool appliedConfirmedCorrections;
    public SessionData Session { get; private set; }
    public event Action<SegmentData>? Changed;
    public IReadOnlyList<SegmentData> Segments => segments.Values.OrderBy(s => s.TaskOrder).ThenBy(s => s.SentenceId).ToArray();
    private readonly bool wholeTurn;
    public int Pending => pending.Count+(Session.WholePolishState=="Waiting"?1:0);
    public TranscriptEngine(SessionData session, Func<long>? clock = null,bool wholeTurn=false) { Session = session; this.clock = clock ?? (() => Environment.TickCount64); this.wholeTurn=wholeTurn; }
    public void Restore(IEnumerable<SegmentData> saved)
    {
        // Loading history must never reinterpret old text using today's mappings.
        appliedConfirmedCorrections = true;
        if(Session.WholePolishState=="Waiting")Session=Session with{WholePolishState="Fallback",WholePolishReason="上次全文润色未完成，保留原文",Revision=Session.Revision+1};
        foreach (var value in saved)
        {
            var s = value with { SaveState = SaveState.Saved };
            if (s.OutputState is OutputState.Waiting or OutputState.Ready)
                s = s.AsrState == AsrState.Confirmed ? s with { FinalText = s.RawText, OutputState = OutputState.Ready, Reason = "恢复原文" } : s with { AsrState = AsrState.Unresolved, OutputState = OutputState.Unresolved };
            segments[s.Id] = s; taskOrder = Math.Max(taskOrder, s.TaskOrder); sequence = Math.Max(sequence, s.Sequence ?? 0);
            tasks.TryAdd(s.TaskId, new TaskState(s.TaskOrder, []) { Sealed = true });
        }
        Publish();
        if (Session.AppliedCorrections.Count > 0) RefreshAppliedCorrectionSummary("", "", false);
    }
    public void StartTask(string taskId, string[] terms)
    {
        if (tasks.Values.Any(t => !t.Sealed)) throw new InvalidOperationException("前一识别任务尚未结束。");
        tasks.Add(taskId, new TaskState(++taskOrder, terms)); nextParagraph = taskOrder > 1;
    }
    public void Paragraph() => nextParagraph = true;
    public ConfirmedCorrectionResult ApplyConfirmedCorrections(IReadOnlyList<TermData> approvedTerms)
    {
        string original = TranscriptText.Render(Segments);
        if (appliedConfirmedCorrections || Session.WholePolishState != "None") return new(original, []);
        if (tasks.Values.Any(t => !t.Sealed) || pending.Count > 0)
            throw new InvalidOperationException("收齐尾句后才能应用确认纠错。");
        appliedConfirmedCorrections = true;
        // Failed/incomplete audio is kept as confirmed ASR text for review. In particular,
        // an unseen earlier sentence could contain the opening of a quote or code block.
        if (Session.Gaps.Count > 0 || Segments.Any(s => s.AsrState == AsrState.Unresolved)) return new(original, []);
        if (approvedTerms.Count == 0 || original.Length == 0) return new(original, []);

        // Match against the full turn so quoted/code spans remain protected across sentences.
        // Keep segment ownership: corrections spanning ASR segment boundaries are skipped.
        var ranges = new List<(SegmentData Segment, int Start, int Length)>();
        int offset = 0;
        char last = '\0';
        static bool LatinNumber(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
        foreach (var segment in Segments.Where(s => s.OutputState == OutputState.Published && s.FinalText.Length > 0))
        {
            if (offset > 0)
            {
                if (segment.ParagraphBefore) offset += 4;
                else if (LatinNumber(last) && LatinNumber(segment.FinalText[0])) offset++;
            }
            ranges.Add((segment, offset, segment.FinalText.Length));
            offset += segment.FinalText.Length;
            last = segment.FinalText[^1];
        }
        var result = ConfirmedCorrections.Apply(original, approvedTerms, Session.ProjectId);
        var edits = result.Edits.Where(e => ranges.Any(r => !r.Segment.UserLocked && e.Start >= r.Start && e.Start + e.Length <= r.Start + r.Length)).ToArray();
        if (edits.Length == 0) return new(original, []);
        var changed = new List<SegmentData>();
        var records = new List<AppliedCorrectionRecord>();
        foreach (var range in ranges)
        {
            var local = edits.Where(e => e.Start >= range.Start && e.Start + e.Length <= range.Start + range.Length).OrderBy(e => e.Start).ToArray();
            if (local.Length == 0) continue;
            string corrected = range.Segment.FinalText;
            foreach (var edit in local.Reverse()) corrected = corrected.Remove(edit.Start - range.Start, edit.Length).Insert(edit.Start - range.Start, edit.Replacement);
            var occurrences = new List<AppliedCorrectionOccurrence>();
            int delta = 0;
            foreach (var edit in local)
            {
                occurrences.Add(new(edit.Start - range.Start + delta, edit.Replacement));
                delta += edit.Replacement.Length - edit.Length;
            }
            records.Add(new(range.Segment.Id, corrected, occurrences));
            changed.Add(range.Segment with { FinalText = corrected, Reason = "已应用确认纠错", Revision = range.Segment.Revision + 1 });
        }
        Session = Session with
        {
            AppliedCorrectionCount = edits.Length,
            AppliedCorrectionTerms = edits.Select(e => e.Replacement).Distinct(StringComparer.Ordinal).ToList(),
            AppliedCorrections = records,
            Revision = Session.Revision + 1
        };
        foreach (var segment in changed) Put(segment);
        return new(TranscriptText.Render(Segments), edits);
    }
    private void RefreshAppliedCorrectionSummary(string editedId, string text, bool deleted)
    {
        var remaining = new List<AppliedCorrectionOccurrence>();
        foreach (var record in Session.AppliedCorrections.Take(ConfirmedCorrections.MaxRecordedOccurrences))
        {
            if (record.SegmentId == editedId)
            { if (!deleted) remaining.AddRange(ConfirmedCorrections.Remaining(record, text)); }
            else if (segments.TryGetValue(record.SegmentId, out var segment) && segment.OutputState == OutputState.Published)
                remaining.AddRange(ConfirmedCorrections.Remaining(record, segment.FinalText));
            if (remaining.Count > ConfirmedCorrections.MaxRecordedOccurrences) { remaining.Clear(); break; }
        }
        Session = Session with { AppliedCorrectionCount = remaining.Count, AppliedCorrectionTerms = remaining.Select(o => o.Text).Distinct(StringComparer.Ordinal).ToList() };
    }
    public PolishWork? Receive(AsrEvent e, bool polish, bool autoParagraph, string[] protectedTerms, bool previousContext)
    {
        if (!tasks.TryGetValue(e.TaskId, out var task) || task.Sealed || e.Heartbeat || e.SentenceId <= 0) return null;
        var s = segments.Values.FirstOrDefault(s => s.TaskId == e.TaskId && s.SentenceId == e.SentenceId);
        if (s?.AsrState == AsrState.Confirmed) return null;
        if (s == null)
        {
            bool paragraph = nextParagraph;
            nextParagraph = false;
            s = new SegmentData { SessionId = Session.Id, TaskId = e.TaskId, TaskOrder = task.Order, SentenceId = e.SentenceId, BeginMs = e.BeginMs, ParagraphBefore = paragraph, InjectedTerms = task.Terms.ToList() };
            if (paragraph) explicitParagraphs.Add(s.Id);
            if (autoParagraph) automaticParagraphs.Add(s.Id);
        }
        if (e.BeginTimeKnown && e.BeginMs >= 0) { s = s with { BeginMs = e.BeginMs }; knownBeginnings.Add(s.Id); }
        if (!e.Final) { Put(s with { PartialText = e.Text, Revision = s.Revision + 1 }); CalibrateParagraphs(e.TaskId); CheckGaps(); return null; }
        long? end = e.EndMs is >= 0 && (!knownBeginnings.Contains(s.Id) || e.EndMs >= s.BeginMs) ? e.EndMs : null;
        s = s with { RawText = e.Text, PartialText = "", EndMs = end, AsrState = AsrState.Confirmed, SourceRevision = 1, Revision = s.Revision + 1, Operation = s.Operation + 1, Reason = "等待润色" };
        Put(s); CalibrateParagraphs(e.TaskId); CheckGaps();
        if (!wholeTurn && PolishRules.PureFiller(s.RawText)) { Put(s with { OutputState = OutputState.Suppressed, Reason = "纯填充", Revision = s.Revision + 1 }); Publish(); return null; }
        if (wholeTurn || !polish || JsonCodec.Count(s.RawText) > 600 || pending.Count >= 22 || protectedTerms.Length > 100 || protectedTerms.Sum(JsonCodec.Count) > 1000)
        { Original(s.Id, !polish ? "原文" : "处理上限，保留原文"); return null; }
        string previous = previousContext ? Segments.LastOrDefault(x => x.TaskOrder <= s.TaskOrder && (x.TaskOrder < s.TaskOrder || x.SentenceId < s.SentenceId) && x.AsrState == AsrState.Confirmed)?.RawText ?? "" : "";
        if (JsonCodec.Count(previous) > 200) previous = string.Concat(previous.EnumerateRunes().TakeLast(200).Select(r => r.ToString()));
        var work = new PolishWork(Session.Id, Session.Generation, s.Id, s.EditRevision, s.Operation, clock() + 10000, s.RawText, previous, protectedTerms);
        pending[s.Id] = work;
        return work;
    }
    private void CalibrateParagraphs(string taskId)
    {
        // The provider may omit a partial's start time or deliver finals out of order.
        // Compare adjacent sentence ids using their latest known timing, never the last
        // event received. Explicit/manual breaks survive later timestamp corrections.
        SegmentData? previous = null;
        foreach (var segment in Segments.Where(s => s.TaskId == taskId))
        {
            bool gap = automaticParagraphs.Contains(segment.Id) && knownBeginnings.Contains(segment.Id)
                && previous is { AsrState: AsrState.Confirmed, EndMs: >= 0 }
                && previous.SentenceId + 1 == segment.SentenceId && segment.BeginMs - previous.EndMs.Value >= 2000;
            bool paragraph = explicitParagraphs.Contains(segment.Id) || gap;
            if (segment.ParagraphBefore != paragraph) Put(segment with { ParagraphBefore = paragraph, Revision = segment.Revision + 1 });
            previous = segment;
        }
    }
    public bool IsCurrent(PolishWork work) => Session.Id == work.SessionId && Session.Generation == work.Generation && segments.TryGetValue(work.SegmentId, out var s) && !s.UserLocked && s.EditRevision == work.EditRevision && s.Operation == work.Operation && s.OutputState == OutputState.Waiting && pending.ContainsKey(s.Id);
    public void Complete(PolishWork work, string? candidate, string? error = null)
    {
        if (!IsCurrent(work)) return;
        if (clock() >= work.Deadline) { Original(work.SegmentId, "润色超时，保留原文"); return; }
        var s = segments[work.SegmentId];
        var check = candidate is null ? new ValidationResult(false, s.RawText, error ?? "润色失败") : PolishRules.Validate(s.RawText, candidate, work.ProtectedTerms);
        pending.Remove(s.Id);
        Put(s with { Candidate = candidate ?? "", FinalText = check.Accepted ? check.Text : s.RawText, OutputState = check.Accepted && check.Text.Length == 0 ? OutputState.Suppressed : OutputState.Ready, Reason = check.Reason, Revision = s.Revision + 1 }); Publish();
    }
    public void Original(string id, string reason)
    {
        if (!segments.TryGetValue(id, out var s) || s.OutputState is OutputState.Published or OutputState.Deleted or OutputState.Suppressed || s.AsrState != AsrState.Confirmed) return;
        pending.Remove(id); Put(s with { FinalText = s.RawText, OutputState = OutputState.Ready, Reason = reason, Operation = s.Operation + 1, Revision = s.Revision + 1 }); Publish();
    }
    public string[] Tick()
    {
        foreach (var work in pending.Values.Where(w => clock() >= w.Deadline).ToArray()) Original(work.SegmentId, "润色超时，保留原文");
        return tasks.Where(t => !t.Value.Sealed && t.Value.GapSince.HasValue && clock() - t.Value.GapSince.Value >= 3000).Select(t => t.Key).ToArray();
    }
    private void CheckGaps()
    {
        foreach (var task in tasks.Where(t => !t.Value.Sealed))
        {
            var list = segments.Values.Where(s => s.TaskId == task.Key).OrderBy(s => s.SentenceId).ToArray();
            int lastFinal = list.Where(s => s.AsrState == AsrState.Confirmed).Select(s => s.SentenceId).DefaultIfEmpty(0).Max();
            bool gap = lastFinal > 0 && (list.Count(s => s.SentenceId <= lastFinal) < lastFinal || list.Any(s => s.SentenceId < lastFinal && s.AsrState == AsrState.Partial));
            task.Value.GapSince = gap ? task.Value.GapSince ?? clock() : null;
        }
    }
    public void SealTask(string taskId, string reason = "")
    {
        if (!tasks.TryGetValue(taskId, out var task) || task.Sealed) return;
        task.Sealed = true;
        var unresolved = segments.Values.Where(s => s.TaskId == taskId && s.AsrState == AsrState.Partial).ToArray();
        foreach (var s in unresolved) Put(s with { AsrState = AsrState.Unresolved, OutputState = OutputState.Unresolved, Reason = "未取得最终确认", Revision = s.Revision + 1 });
        int max = segments.Values.Where(s => s.TaskId == taskId).Select(s => s.SentenceId).DefaultIfEmpty(0).Max();
        int actual = segments.Values.Count(s => s.TaskId == taskId);
        if (unresolved.Length > 0 || actual < max || reason.Length > 0)
            Session = Session with { Gaps = [.. Session.Gaps, $"任务 {task.Order}：{reason}；未确认 {unresolved.Length}，缺失编号 {Math.Max(0, max-actual)}。"], Revision = Session.Revision + 1 };
        Publish();
    }
    private void Publish()
    {
        foreach (var task in tasks.OrderBy(t => t.Value.Order))
        {
            int expected = 1;
            foreach (var s in segments.Values.Where(s => s.TaskId == task.Key).OrderBy(s => s.SentenceId).ToArray())
            {
                if (s.SentenceId > expected && !task.Value.Sealed) return;
                if (s.OutputState == OutputState.Ready) Put(s with { OutputState = OutputState.Published, Sequence = s.Sequence ?? ++sequence, Revision = s.Revision + 1 });
                else if (s.OutputState == OutputState.Waiting) return;
                expected = s.SentenceId + 1;
            }
            if (!task.Value.Sealed) return;
        }
    }
    public SegmentData Edit(string id, string text, string action = "编辑", bool learnCorrections = false)
    {
        var s = segments[id];
        if (s.AsrState != AsrState.Confirmed || s.OutputState is not (OutputState.Published or OutputState.Deleted)) throw new InvalidOperationException("请选择已发布的片段。");
        bool hasHistory = s.UndoHistory is { Count: > 0 };
        var history = hasHistory ? s.UndoHistory!.ToList() : new List<EditState> { new(s.RawText) };
        int cursor = hasHistory ? Math.Clamp(s.UndoPosition, 0, history.Count - 1) : 0;
        if (!hasHistory)
        {
            // Old audit records remain intact; build an independent undo cursor once.
            history.AddRange(s.Edits.Where(e => e.Action != "撤销").Select(e => new EditState(e.Text, e.Action == "删除")));
            var current = new EditState(s.FinalText, s.OutputState == OutputState.Deleted);
            cursor = history.FindLastIndex(v => v.Text == current.Text && v.Deleted == current.Deleted);
            if (cursor < 0) { history.Add(current); cursor = history.Count - 1; }
        }
        bool deleted = action == "删除";
        if (action == "恢复原文") text = s.RawText;
        if (action == "撤销")
        {
            if (cursor == 0) return s;
            var previous = history[--cursor]; text = previous.Text; deleted = previous.Deleted;
        }
        else
        {
            if (s.FinalText == text && (s.OutputState == OutputState.Deleted) == deleted) return s;
            if (action is not "恢复原文" && JsonCodec.Count(text) > 20000) throw new ArgumentException("单段编辑超过 20,000 字。");
            var corrections = action == "编辑" && !deleted
                ? (history[cursor].Corrections ?? []).Where(c => text.Contains(c.Corrected, StringComparison.Ordinal)).ToList() : [];
            if (learnCorrections && action == "编辑" && s.OutputState != OutputState.Deleted)
                corrections.AddRange(CorrectionRules.Detect(s.FinalText, text));
            corrections = corrections.DistinctBy(c => (c.Original,c.Corrected)).TakeLast(32).ToList();
            history = history.Take(cursor + 1).ToList(); history.Add(new(text, deleted, corrections)); cursor++;
        }
        if (action is not ("撤销" or "恢复原文") && JsonCodec.Count(text) > 20000) throw new ArgumentException("单段编辑超过 20,000 字。");
        Session=Session with{WholePolishState="Fallback",WholePolishText="",WholePolishReason="原始片段已编辑，使用编辑后的正文",WholePolishOperation=Session.WholePolishOperation+1,Revision=Session.Revision+1};
        RefreshAppliedCorrectionSummary(id, text, deleted);
        long edit = s.EditRevision + 1;
        s = s with { FinalText = text, UserLocked = true, Operation = s.Operation + 1, EditRevision = edit, Revision = s.Revision + 1, OutputState = deleted ? OutputState.Deleted : OutputState.Published, Reason = action, Edits = [.. s.Edits, new EditVersion(edit, text, action, DateTimeOffset.UtcNow)], UndoHistory = history, UndoPosition = cursor };
        pending.Remove(id); Put(s); return s;
    }
    public void SetSaveState(string id, long revision, bool success)
    {
        if (!segments.TryGetValue(id, out var s) || s.Revision != revision) return;
        segments[id] = s with { SaveState = success ? SaveState.Saved : SaveState.Failed };
    }
    public void SetMemoryState(bool save)
    {
        foreach (var pair in segments.ToArray()) if (pair.Value.SaveState == SaveState.Pending && !save) segments[pair.Key] = pair.Value with { SaveState = SaveState.NotRequested };
    }
    public void MarkPending(string id, bool save) { if (segments.TryGetValue(id, out var s)) segments[id] = s with { SaveState = save ? SaveState.Pending : SaveState.NotRequested }; }
    public void Learning(bool enabled) => Session = Session with { AllowLearning = enabled, LearningRevision = Session.Revision + 1, Revision = Session.Revision + 1 };
    public void UpdateSession(SessionData session) => Session = session;
    public void FinishAll() { foreach (var id in tasks.Keys.ToArray()) SealTask(id); foreach (var id in pending.Keys.ToArray()) Original(id, "停止后保留原文"); }
    private void Put(SegmentData s) { segments[s.Id] = s; Changed?.Invoke(s); }
}
