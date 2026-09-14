using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RealtimeTranscription.Core;

public static class JsonCodec
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false, PropertyNameCaseInsensitive = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
    public static string Id() => Guid.NewGuid().ToString();
    public static int Count(string text) => text.EnumerateRunes().Count();
    public static string Take(string text, int count) => string.Concat(text.EnumerateRunes().Take(count).Select(r => r.ToString()));
}

public record AppSettings
{
    public int SchemaVersion { get; init; } = 4;
    public string Region { get; init; } = "cn-beijing";
    public string WorkspaceId { get; init; } = "";
    public bool LegacyEndpoint { get; init; }
    public string DeviceId { get; init; } = "";
    public string ProjectId { get; init; } = "default";
    public bool PolishEnabled { get; init; } = true;
    public string PolishPrompt { get; init; } = PolishRules.SystemPrompt;
    [JsonIgnore] public string EffectivePolishPrompt => PolishRules.ResolvePrompt(PolishPrompt);
    public string GenerationRequirement { get; init; } = "HR 领域，覆盖招聘、绩效、薪酬、培训及员工关系。采用常用的简体中文术语，可包含常见英文缩写。";
    public int GenerationCount { get; init; } = 100;
    public bool GenerationGlobal { get; init; }
    public bool GenerationMaxThinking { get; init; }
    public bool SaveMemory { get; init; } = true;
    public int? RetentionDays { get; init; }
    public bool AllowLearning { get; init; } = true;
    public bool AutoExtract { get; init; }
    public bool LearnCorrections { get; init; } = true;
    public bool UseLexicon { get; init; } = true;
    public bool DynamicLexicon { get; init; } = true;
    public bool AsrContext { get; init; }
    public bool PreviousContext { get; init; }
    public bool AutoParagraph { get; init; }
    public bool CloseToTray { get; init; } = true;
    public int SilenceMs { get; init; } = 2500;
    public int DailyExtractionTokens { get; init; } = 50000;
    public string Hotkey { get; init; } = "RightCtrl";
    public bool DictationOnly { get; init; }
    public int HoldMs { get; init; } = 150;
    public int MaxHoldSeconds { get; init; } = 600;
    public void Validate()
    {
        _=EffectivePolishPrompt;
        if(GenerationCount is <1 or >500)throw new ArgumentException("词库生成数量应为 1—500 个。");
        if(GenerationRequirement is null||JsonCodec.Count(GenerationRequirement)>1000)throw new ArgumentException("词库生成要求最多 1,000 字。");
        if (Hotkey is not ("RightCtrl" or "F8" or "F9")) throw new ArgumentException("按住说话键支持 RightCtrl、F8 或 F9。旧配置请重新选择快捷键。");
        if (HoldMs is < 100 or > 500 || MaxHoldSeconds is < 30 or > 1800) throw new ArgumentException("长按阈值应为 100—500 ms，最长录音应为 30—1800 秒。");
        if (Region is not ("cn-beijing" or "ap-southeast-1")) throw new ArgumentException("请选择北京或新加坡地域。");
        if (SilenceMs is < 200 or > 6000) throw new ArgumentException("断句停顿应为 200 至 6000 毫秒。");
        if (RetentionDays is <= 0) throw new ArgumentException("保留天数应为正数，留空表示长期保存。");
        if (DailyExtractionTokens < 1000 || DailyExtractionTokens > 10000000) throw new ArgumentException("每日整理预算应在 1,000 至 10,000,000 Token 之间。");
        if (!LegacyEndpoint && WorkspaceId.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(WorkspaceId, "^[A-Za-z0-9-]+$")) throw new ArgumentException("业务空间 ID 只能包含字母、数字和连字符。");
    }
    public Uri AsrUri()
    {
        Validate();
        if (!LegacyEndpoint && string.IsNullOrWhiteSpace(WorkspaceId)) throw new ArgumentException("请在设置中填写百炼业务空间 ID，或选择兼容地址。");
        string host = LegacyEndpoint ? (Region == "cn-beijing" ? "dashscope.aliyuncs.com" : "dashscope-intl.aliyuncs.com") : $"{WorkspaceId}.{Region}.maas.aliyuncs.com";
        return new Uri($"wss://{host}/api-ws/v1/inference");
    }
}

public record Credentials(string BailianKey = "", string DeepSeekKey = "");
public record Project(string Id, string Name, long Revision = 1);
public enum CaptureState { Idle, Connecting, Recording, Draining, Paused, Stopped, Faulted, Closing }
public enum AsrState { Partial, Confirmed, Unresolved }
public enum OutputState { Waiting, Ready, Published, Suppressed, Unresolved, Deleted }
public enum SaveState { NotRequested, Pending, Saved, Failed }

public record HotwordUsage(string Text,int Weight);
public record SessionData
{
    public List<HotwordUsage> Hotwords { get; init; } = [];
    public string HotwordState { get; init; } = "None";
    public int EligibleHotwordCount { get; init; }
    public int ProtectedTermCount { get; init; }
    public int AppliedCorrectionCount { get; init; }
    public List<string> AppliedCorrectionTerms { get; init; } = [];
    public List<AppliedCorrectionRecord> AppliedCorrections { get; init; } = [];
    public string WholePolishState { get; init; } = "None";
    public string WholePolishText { get; init; } = "";
    public string WholePolishReason { get; init; } = "";
    public long WholePolishOperation { get; init; }
    public string DeliveryState { get; init; } = "Pending";
    public string DeliveryReason { get; init; } = "";
    public int AcceptedInputEvents { get; init; }
    public string DeliveryId { get; init; } = JsonCodec.Id();
    public string Id { get; init; } = JsonCodec.Id();
    public string ProjectId { get; init; } = "default";
    public string Title { get; init; } = DateTime.Now.ToString("MM月dd日 HH:mm 口述");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; init; }
    public bool AllowLearning { get; init; } = true;
    public long LearningRevision { get; init; }
    public long Generation { get; init; } = 1;
    public long Revision { get; init; } = 1;
    public List<string> Gaps { get; init; } = [];
    public List<string> LearnedVersions { get; init; } = [];
}
public record EditVersion(long Revision, string Text, string Action, DateTimeOffset At);
public record EditState(string Text, bool Deleted = false, List<CorrectionChange>? Corrections = null);
public record SegmentData
{
    public string Id { get; init; } = JsonCodec.Id();
    public string SessionId { get; init; } = "";
    public string TaskId { get; init; } = "";
    public int TaskOrder { get; init; }
    public int SentenceId { get; init; }
    public long BeginMs { get; init; }
    public long? EndMs { get; init; }
    public string PartialText { get; init; } = "";
    public string RawText { get; init; } = "";
    public string FinalText { get; init; } = "";
    public string Candidate { get; init; } = "";
    public AsrState AsrState { get; init; }
    public OutputState OutputState { get; init; }
    public SaveState SaveState { get; init; }
    public long Revision { get; init; } = 1;
    public long SourceRevision { get; init; }
    public long EditRevision { get; init; }
    public long Operation { get; init; }
    public bool UserLocked { get; init; }
    public bool ParagraphBefore { get; init; }
    public int? Sequence { get; init; }
    public string Reason { get; init; } = "";
    public List<EditVersion> Edits { get; init; } = [];
    public List<EditState>? UndoHistory { get; init; }
    public int UndoPosition { get; init; }
    public List<string> InjectedTerms { get; init; } = [];
    [JsonIgnore] public int ExtractionStart { get; init; }
    [JsonIgnore] public string ViewLabel => $"{TaskOrder}.{SentenceId}  {(OutputState == OutputState.Deleted ? "已删除" : AsrState == AsrState.Unresolved ? "未确认" : Reason)}  {JsonCodec.Take(FinalText.Length > 0 ? FinalText : RawText.Length > 0 ? RawText : PartialText, 60)}";
}
public enum TermState { Candidate, Enabled, Disabled }
public record TermEvidence(string SessionId, string SegmentId, long SourceRevision, long EditRevision, string Quote, bool Influenced, int SliceStart = 0);
public record TermData
{
    [JsonIgnore] public long UsageCount { get; init; }
    [JsonIgnore] public long CorrectionCount { get; init; }
    [JsonIgnore] public DateTimeOffset? LastUsedAt { get; init; }
    [JsonIgnore] public DateTimeOffset? LastCorrectedAt { get; init; }
    [JsonIgnore] public int SuggestedWeight => Lexicon.EffectiveWeight(this);
    public string Id { get; init; } = JsonCodec.Id();
    public string Scope { get; init; } = "default";
    public string Text { get; init; } = "";
    public string Category { get; init; } = "专业术语";
    public TermState State { get; init; } = TermState.Enabled;
    public string Origin { get; init; } = "Manual";
    public string GenerationRequirement { get; init; } = "";
    public int Weight { get; init; } = 3;
    public bool Protect { get; init; } = true;
    public bool Pinned { get; init; }
    public string Alias { get; init; } = "";
    public long Revision { get; init; } = 1;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<TermEvidence> Evidence { get; init; } = [];
    [JsonIgnore] public string Key => Scope + "\n" + Text.Trim().Normalize(NormalizationForm.FormC);
    [JsonIgnore] public string StateLabel => State switch { TermState.Candidate => "待确认", TermState.Enabled => "已启用", _ => "已禁用" };
    [JsonIgnore] public string ScopeLabel => Scope == "*" ? "全局" : "本项目";
    public void Validate()
    {
        if (JsonCodec.Count(Text.Trim()) is < 1 or > 64 || Text.Any(char.IsControl) || !Text.Any(char.IsLetterOrDigit)) throw new ArgumentException("词条应为 1—64 个字符，不能只含标点或控制字符。");
        if (Weight is < 1 or > 5) throw new ArgumentException("热词权重应为 1 至 5。");
        if (JsonCodec.Count(Alias) > 128) throw new ArgumentException("旧写法过长。");
    }
}
public record UsageData(string Purpose, long InputTokens, long OutputTokens, bool Unknown, DateTimeOffset At, double AudioSeconds = 0);
public record AsrEvent(string Event, string TaskId, int SentenceId = 0, string Text = "", bool Final = false, bool Heartbeat = false, long BeginMs = 0, long? EndMs = null, double? Duration = null, string Error = "", bool BeginTimeKnown = true);
public record TranscriptSnapshot(SessionData? Session, IReadOnlyList<SegmentData> Segments, CaptureState State, int Pending, int Unsaved, string Status)
{
    // Local capture and remote recognition have independent readiness. Neither
    // changes the business-state gates that require a confirmed ASR task.
    public bool LocalAudioReady { get; init; }
    public bool CaptureReleased { get; init; }
}

public static class TranscriptText
{
    public static string Render(TranscriptSnapshot snapshot) => Render(snapshot.Session,snapshot.Segments);
    public static string Render(SessionData? session,IEnumerable<SegmentData> segments) => session?.WholePolishState=="Completed" ? session.WholePolishText : Render(segments);
    public static string Render(IEnumerable<SegmentData> segments)
    {
        var b = new StringBuilder();
        foreach (var s in segments.Where(s => s.OutputState == OutputState.Published).OrderBy(s => s.TaskOrder).ThenBy(s => s.SentenceId))
        {
            if (s.FinalText.Length == 0) continue;
            if (b.Length > 0)
            {
                if (s.ParagraphBefore) b.Append("\r\n\r\n");
                else if (IsLatinNumber(b[^1]) && IsLatinNumber(s.FinalText[0])) b.Append(' ');
            }
            b.Append(s.FinalText);
        }
        return b.ToString();
    }
    private static bool IsLatinNumber(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
