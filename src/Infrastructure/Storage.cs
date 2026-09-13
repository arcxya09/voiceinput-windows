using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Infrastructure;

public interface IProtector { byte[] Protect(byte[] plain); byte[] Unprotect(byte[] cipher); }
public sealed class WindowsProtector : IProtector
{
    public byte[] Protect(byte[] plain) => OperatingSystem.IsWindows() ? ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser) : throw new PlatformNotSupportedException("DPAPI requires Windows.");
    public byte[] Unprotect(byte[] cipher) => OperatingSystem.IsWindows() ? ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser) : throw new PlatformNotSupportedException("DPAPI requires Windows.");
}

public sealed class SettingsStore(string folder, IProtector protector)
{
    private readonly object saveSync = new();
    private string FilePath(string name) => Path.Combine(folder, name);
    public AppSettings Load()
    {
        Directory.CreateDirectory(folder);
        if (!File.Exists(FilePath("settings.json"))) return new();
        var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(FilePath("settings.json")), JsonCodec.Options) ?? new();
        loaded=loaded.SchemaVersion < 3 ? loaded with { SchemaVersion=3, Hotkey="RightCtrl" } : loaded;
        if(loaded.SchemaVersion<4)loaded=loaded with{SchemaVersion=4,MaxHoldSeconds=loaded.MaxHoldSeconds==120?600:loaded.MaxHoldSeconds,SilenceMs=loaded.SilenceMs==800?2500:loaded.SilenceMs};
        return loaded with{PolishPrompt=PolishRules.ResolvePrompt(loaded.PolishPrompt)};
    }
    public Credentials LoadCredentials() => !File.Exists(FilePath("credentials.dat")) ? new() : JsonSerializer.Deserialize<Credentials>(protector.Unprotect(File.ReadAllBytes(FilePath("credentials.dat"))), JsonCodec.Options) ?? new();
    public void Save(AppSettings settings, Credentials credentials)
    {
        lock(saveSync)
        {
        settings.Validate(); Directory.CreateDirectory(folder);
        Atomic(FilePath("credentials.dat"), protector.Protect(JsonSerializer.SerializeToUtf8Bytes(credentials, JsonCodec.Options)));
        Atomic(FilePath("settings.json"), JsonSerializer.SerializeToUtf8Bytes(settings, JsonCodec.Options));
        }
    }
    public static void Atomic(string path, byte[] bytes)
    {
        string temp = path + ".new";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
        File.Move(temp, path, true);
    }
}

public record MemoryHit(SessionData Session, string Snippet, int SegmentCount);
public sealed partial class MemoryRepository : IAsyncDisposable
{
    private record Work(Action<SqliteConnection> Run, TaskCompletionSource Done, bool Initialize);
    private readonly string path;
    private readonly IProtector protector;
    private readonly Channel<Work> writes = Channel.CreateBounded<Work>(new BoundedChannelOptions(1000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writer;
    private int queued;
    private int initialized;
    public bool IsAvailable => Volatile.Read(ref initialized) == 1;
    public int Queued => Volatile.Read(ref queued);
    public MemoryRepository(string path, IProtector protector)
    {
        this.path = path; this.protector = protector;
        writer = Task.Run(WriteLoop);
    }
    private SqliteConnection Open(bool initializing = false)
    {
        if (!initializing && !IsAvailable) throw new InvalidOperationException("本地记忆尚未成功初始化，已暂停数据库读写；听写结果仍可复制。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 1, Pooling = true }.ToString());
        try
        {
            c.Open();
            using var pragma = c.CreateCommand(); pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=1000; PRAGMA secure_delete=ON;"; pragma.ExecuteNonQuery(); return c;
        }
        catch { c.Dispose(); throw; }
    }
    public Task InitializeAsync() => WriteAsync(c =>
    {
        using var version = c.CreateCommand(); version.CommandText = "PRAGMA user_version"; int value = Convert.ToInt32(version.ExecuteScalar());
        if (value > 3) throw new InvalidOperationException("数据库来自较新版本，请使用新版程序，原数据已保留。");
        using var command = c.CreateCommand(); command.CommandText = """
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, project TEXT NOT NULL, created TEXT NOT NULL, revision INTEGER NOT NULL, payload BLOB NOT NULL);
CREATE INDEX IF NOT EXISTS sessions_project_date ON sessions(project,created);
CREATE TABLE IF NOT EXISTS segments(id TEXT PRIMARY KEY, session TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE, task TEXT NOT NULL, task_order INTEGER NOT NULL, sentence INTEGER NOT NULL, revision INTEGER NOT NULL, payload BLOB NOT NULL, UNIQUE(task,sentence));
CREATE INDEX IF NOT EXISTS segments_session ON segments(session,task_order,sentence);
CREATE TABLE IF NOT EXISTS terms(id TEXT PRIMARY KEY, scope TEXT NOT NULL, revision INTEGER NOT NULL, payload BLOB NOT NULL);
CREATE INDEX IF NOT EXISTS terms_scope ON terms(scope);
CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY, revision INTEGER NOT NULL, payload BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS tombstones(kind TEXT NOT NULL,id TEXT NOT NULL, PRIMARY KEY(kind,id));
CREATE TABLE IF NOT EXISTS suppression(id TEXT PRIMARY KEY, scope TEXT NOT NULL,payload BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS usage(id INTEGER PRIMARY KEY AUTOINCREMENT,day TEXT NOT NULL,purpose TEXT NOT NULL,input INTEGER NOT NULL,output INTEGER NOT NULL,unknown INTEGER NOT NULL,audio REAL NOT NULL);
CREATE TABLE IF NOT EXISTS corrections(id TEXT PRIMARY KEY,project TEXT NOT NULL,revision INTEGER NOT NULL,payload BLOB NOT NULL);
CREATE INDEX IF NOT EXISTS corrections_project ON corrections(project);
CREATE TABLE IF NOT EXISTS correction_sources(candidate TEXT NOT NULL REFERENCES corrections(id) ON DELETE CASCADE,segment TEXT NOT NULL REFERENCES segments(id) ON DELETE CASCADE,session TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,created TEXT NOT NULL,payload BLOB NOT NULL,PRIMARY KEY(candidate,segment));
CREATE INDEX IF NOT EXISTS correction_sources_segment ON correction_sources(segment);
CREATE INDEX IF NOT EXISTS correction_sources_session ON correction_sources(session);
CREATE TABLE IF NOT EXISTS correction_decisions(id TEXT PRIMARY KEY REFERENCES corrections(id) ON DELETE CASCADE);
CREATE TABLE IF NOT EXISTS term_observations(term TEXT NOT NULL REFERENCES terms(id) ON DELETE CASCADE,segment TEXT NOT NULL REFERENCES segments(id) ON DELETE CASCADE,session TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,project TEXT NOT NULL,corrected INTEGER NOT NULL CHECK(corrected IN (0,1)),created TEXT NOT NULL,corrected_at TEXT,PRIMARY KEY(term,segment));
CREATE INDEX IF NOT EXISTS term_observations_project ON term_observations(project,term,session);
CREATE INDEX IF NOT EXISTS term_observations_segment ON term_observations(segment);
CREATE INDEX IF NOT EXISTS term_observations_session ON term_observations(session);
PRAGMA user_version=3;
"""; command.ExecuteNonQuery();
        MergeDuplicateTerms(c);
        Volatile.Write(ref initialized, 1);
    }, initialize: true);
    private byte[] Pack<T>(T value) => protector.Protect(JsonSerializer.SerializeToUtf8Bytes(value, JsonCodec.Options));
    private T Unpack<T>(byte[] value) => JsonSerializer.Deserialize<T>(protector.Unprotect(value), JsonCodec.Options)!;
    private static SqliteCommand Command(SqliteConnection c, string sql, params (string Key, object? Value)[] args)
    {
        var command = c.CreateCommand(); command.CommandText = sql;
        foreach (var (key,value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private async Task WriteLoop()
    {
        await foreach (var work in writes.Reader.ReadAllAsync())
        {
            try { using var connection = Open(work.Initialize); work.Run(connection); work.Done.TrySetResult(); }
            catch (Exception e) { if (work.Initialize) Volatile.Write(ref initialized, -1); work.Done.TrySetException(e); }
            finally { Interlocked.Decrement(ref queued); }
        }
    }
    private async Task WriteAsync(Action<SqliteConnection> work, bool initialize = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Interlocked.Increment(ref queued);
        try { await writes.Writer.WriteAsync(new(work, done, initialize)); } catch { Interlocked.Decrement(ref queued); throw; }
        await done.Task;
    }
    public Task BarrierAsync() => WriteAsync(_ => { });
    private static bool Dead(SqliteConnection c, string kind, string id) { using var cmd = Command(c, "SELECT 1 FROM tombstones WHERE kind=$k AND id=$id", ("$k", kind), ("$id", id)); return cmd.ExecuteScalar() != null; }
    public Task SaveSessionAsync(SessionData data) => WriteAsync(c => { using var tx=c.BeginTransaction();SaveSession(c,data);tx.Commit(); });
    private void SaveSession(SqliteConnection c, SessionData data)
    {
        if (Dead(c, "session", data.Id) || Dead(c, "project", data.ProjectId)) throw new InvalidOperationException("已删除会话的旧保存已拒绝。");
        using (var existing = Command(c, "SELECT payload FROM sessions WHERE id=$id", ("$id", data.Id)))
            if (existing.ExecuteScalar() is byte[] bytes)
            {
                var saved = Unpack<SessionData>(bytes);
                data = data with { LearnedVersions = saved.LearnedVersions.Concat(data.LearnedVersions).Distinct().ToList() };
                if (saved.LearningRevision > data.LearningRevision)
                    data = data with { AllowLearning = saved.AllowLearning, LearningRevision = saved.LearningRevision };
            }
        using var cmd = Command(c, "INSERT INTO sessions(id,project,created,revision,payload) VALUES($id,$p,$at,$v,$b) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,payload=excluded.payload WHERE excluded.revision>=sessions.revision", ("$id",data.Id),("$p",data.ProjectId),("$at",data.CreatedAt.ToString("O")),("$v",data.Revision),("$b",Pack(data))); if(cmd.ExecuteNonQuery()>0&&!data.AllowLearning)
        {
            RemoveCorrectionSession(c,data.Id);
            RemoveTermObservationSession(c,data.Id);
        }
    }
    public Task SaveSegmentAsync(SessionData session, SegmentData data, bool learnCorrections=false, bool learnUsage=false) => WriteAsync(c =>
    {
        if(data.SessionId!=session.Id)throw new ArgumentException("片段与会话不匹配。");
        using var tx = c.BeginTransaction(); SaveSession(c, session);
        if (Dead(c,"segment",data.Id)) throw new InvalidOperationException("已清除片段的旧保存已拒绝。");
        using var cmd = Command(c, "INSERT INTO segments(id,session,task,task_order,sentence,revision,payload) VALUES($id,$s,$t,$o,$n,$v,$b) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,payload=excluded.payload WHERE excluded.revision>=segments.revision AND segments.session=excluded.session", ("$id",data.Id),("$s",data.SessionId),("$t",data.TaskId),("$o",data.TaskOrder),("$n",data.SentenceId),("$v",data.Revision),("$b",Pack(data)));
        if (cmd.ExecuteNonQuery() > 0)
        {
            if(data.EditRevision>0)
            {
                RemoveEvidence(c, e => e.SegmentId == data.Id && (data.OutputState == OutputState.Deleted || e.SourceRevision != data.SourceRevision || e.EditRevision != data.EditRevision));
                SyncCorrections(c,session,data,learnCorrections);
            }
            SyncTermObservations(c,session,data,learnUsage);
        }
        tx.Commit();
    });
    public Task SaveTermAsync(TermData term) => WriteAsync(c => {using var tx=c.BeginTransaction();SaveTerm(c,term);tx.Commit();});
    private void SaveTerm(SqliteConnection c,TermData term)
    {
        term.Validate(); if (Dead(c,"term",term.Id) || Dead(c,"project",term.Scope)) throw new InvalidOperationException("已删除词条的旧保存已拒绝。");
        TermData? previous=null;
        using(var query=Command(c,"SELECT payload FROM terms WHERE id=$id",("$id",term.Id)))
            if(query.ExecuteScalar() is byte[] bytes)previous=Unpack<TermData>(bytes);
        using var cmd = Command(c, "INSERT INTO terms(id,scope,revision,payload) VALUES($id,$s,$v,$b) ON CONFLICT(id) DO UPDATE SET scope=excluded.scope,revision=excluded.revision,payload=excluded.payload WHERE excluded.revision>=terms.revision", ("$id",term.Id),("$s",term.Scope),("$v",term.Revision),("$b",Pack(term)));
        if(cmd.ExecuteNonQuery()>0&&previous!=null&&(previous.Text!=term.Text||previous.Scope!=term.Scope))
        {
            using var clear=Command(c,"DELETE FROM term_observations WHERE term=$id",("$id",term.Id));clear.ExecuteNonQuery();
        }
    }
    public Task ImportTermsAsync(IReadOnlyList<TermData> batch) => WriteAsync(c=>{using var tx=c.BeginTransaction();foreach(var term in batch)SaveTerm(c,term);tx.Commit();});
    public Task SaveProjectAsync(Project project) => WriteAsync(c => { if (Dead(c,"project",project.Id)) throw new InvalidOperationException("项目已删除。"); using var cmd=Command(c,"INSERT INTO projects(id,revision,payload) VALUES($id,$v,$b) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,payload=excluded.payload WHERE excluded.revision>=projects.revision",("$id",project.Id),("$v",project.Revision),("$b",Pack(project))); cmd.ExecuteNonQuery(); });
    public Task<List<Project>> ProjectsAsync() => Task.Run(() => { using var c=Open(); using var cmd=Command(c,"SELECT payload FROM projects ORDER BY rowid"); using var r=cmd.ExecuteReader(); var list=new List<Project>(); while(r.Read())list.Add(Unpack<Project>((byte[])r[0])); return list; });
    public Task<List<TermData>> TermsAsync(string scope) => Task.Run(() => { using var c=Open();return ReadTermsWithUsage(c,scope); });
    public Task<HashSet<string>> SuppressedAsync(string scope) => Task.Run(() => { using var c=Open(); using var cmd=Command(c,"SELECT payload FROM suppression WHERE scope=$s OR scope='*'",("$s",scope)); using var r=cmd.ExecuteReader(); var list=new HashSet<string>(StringComparer.Ordinal); while(r.Read())list.Add(Unpack<string>((byte[])r[0])); return list; });
    public Task<List<SegmentData>> SegmentsAsync(string session) => Task.Run(() => { using var c=Open(); using var cmd=Command(c,"SELECT payload FROM segments WHERE session=$s ORDER BY task_order,sentence",("$s",session)); using var r=cmd.ExecuteReader(); var list=new List<SegmentData>(); while(r.Read())list.Add(Unpack<SegmentData>((byte[])r[0]) with { SaveState=SaveState.Saved }); return list; });
    public Task<List<MemoryHit>> SearchAsync(string? project, string query, DateTimeOffset? since, CancellationToken token) => Task.Run(() =>
    {
        var hits=new List<MemoryHit>(); long after=long.MaxValue;
        using var c=Open();
        while(hits.Count<500)
        {
            token.ThrowIfCancellationRequested();
            using var cmd=Command(c,"SELECT rowid,payload FROM sessions WHERE rowid<$after AND ($p IS NULL OR project=$p) AND ($d IS NULL OR created>=$d) ORDER BY rowid DESC LIMIT 100",("$after",after),("$p",project),("$d",since?.ToString("O")));
            var batch=new List<SessionData>(); using(var reader=cmd.ExecuteReader()){while(reader.Read()){after=reader.GetInt64(0);batch.Add(Unpack<SessionData>((byte[])reader[1]));}}
            if(batch.Count==0)break;
            foreach(var session in batch)
            {
                token.ThrowIfCancellationRequested(); string snippet=session.WholePolishState=="Completed"?JsonCodec.Take(session.WholePolishText,160):""; int count=0; bool found=query.Length==0||session.Title.Contains(query,StringComparison.OrdinalIgnoreCase)||(session.WholePolishState=="Completed"&&session.WholePolishText.Contains(query,StringComparison.OrdinalIgnoreCase));
                using var parts=Command(c,"SELECT payload FROM segments WHERE session=$id ORDER BY task_order,sentence",("$id",session.Id));
                using var reader=parts.ExecuteReader();
                while(reader.Read())
                {
                    token.ThrowIfCancellationRequested();var s=Unpack<SegmentData>((byte[])reader[0]);if(s.OutputState==OutputState.Deleted)continue;count++;
                    string current=s.FinalText.Length>0?s.FinalText:s.RawText.Length>0?s.RawText:s.PartialText;
                    bool match=current.Contains(query,StringComparison.OrdinalIgnoreCase)||s.RawText.Contains(query,StringComparison.OrdinalIgnoreCase);
                    if(snippet.Length==0&&(match||query.Length==0))snippet=JsonCodec.Take(current,160);
                    found|=match;
                }
                if(found)hits.Add(new(session,snippet,count));if(hits.Count>=500)break;
            }
        }
        return hits.OrderByDescending(h=>h.Session.CreatedAt).ToList();
    },token);
    public Task SaveUsageAsync(UsageData data) => WriteAsync(c => { using var cmd=Command(c,"INSERT INTO usage(day,purpose,input,output,unknown,audio) VALUES($d,$p,$i,$o,$u,$a)",("$d",UtcDay(data.At)),("$p",data.Purpose),("$i",data.InputTokens),("$o",data.OutputTokens),("$u",data.Unknown?1:0),("$a",data.AudioSeconds));cmd.ExecuteNonQuery(); });
    public Task<List<UsageData>> UsageAsync() => Task.Run(() => { using var c=Open();using var cmd=Command(c,"SELECT day,purpose,SUM(input),SUM(output),SUM(unknown),SUM(audio) FROM usage GROUP BY day,purpose ORDER BY day DESC LIMIT 200");using var r=cmd.ExecuteReader();var list=new List<UsageData>();while(r.Read())list.Add(new(r.GetString(1),r.GetInt64(2),r.GetInt64(3),r.GetInt64(4)>0,DateTimeOffset.ParseExact(r.GetString(0),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal),r.GetDouble(5)));return list; });
    private static void Tombstone(SqliteConnection c,string kind,string id){using var cmd=Command(c,"INSERT OR IGNORE INTO tombstones(kind,id) VALUES($k,$id)",("$k",kind),("$id",id));cmd.ExecuteNonQuery();}
    public Task DeleteTermAsync(TermData term) => WriteAsync(c => { using var tx=c.BeginTransaction();Tombstone(c,"term",term.Id);UnlinkCorrectionTerm(c,term.Id);using(var cmd=Command(c,"DELETE FROM terms WHERE id=$id",("$id",term.Id)))cmd.ExecuteNonQuery();using(var cmd=Command(c,"INSERT OR REPLACE INTO suppression(id,scope,payload) VALUES($id,$s,$b)",("$id",term.Id),("$s",term.Scope),("$b",Pack(term.Key))))cmd.ExecuteNonQuery();tx.Commit(); });
    public Task DeleteSessionAsync(string id) => WriteAsync(c => DeleteSession(c,id));
    private void DeleteSession(SqliteConnection c,string id)
    {
        using var tx=c.BeginTransaction(); Tombstone(c,"session",id);
        using(var cmd=Command(c,"DELETE FROM sessions WHERE id=$id",("$id",id)))cmd.ExecuteNonQuery();
        RemoveEvidence(c,e=>e.SessionId==id);RemoveCorrectionSession(c,id);tx.Commit();
    }
    public Task RemoveSegmentEvidenceAsync(string id) => WriteAsync(c => { using var tx=c.BeginTransaction();RemoveEvidence(c,e=>e.SegmentId==id);tx.Commit(); });
    private void RemoveEvidence(SqliteConnection c,Func<TermEvidence,bool> remove)
    {
        var all=new List<TermData>();using(var cmd=Command(c,"SELECT payload FROM terms"))using(var r=cmd.ExecuteReader())while(r.Read())all.Add(Unpack<TermData>((byte[])r[0]));
        foreach(var term in all.Where(t=>t.Evidence.Any(remove)))
        {
            var keep=term.Evidence.Where(e=>!remove(e)).ToList();
            if(keep.Count==0&&term.Origin=="Extracted"){Tombstone(c,"term",term.Id);using var cmd=Command(c,"DELETE FROM terms WHERE id=$id",("$id",term.Id));cmd.ExecuteNonQuery();}
            else{var changed=term with{Evidence=keep,Revision=term.Revision+1};using var cmd=Command(c,"UPDATE terms SET payload=$b,revision=$v WHERE id=$id",("$b",Pack(changed)),("$v",changed.Revision),("$id",term.Id));cmd.ExecuteNonQuery();}
        }
    }
    public async Task DeleteProjectAsync(string project)
    {
        var sessions=await SearchAsync(project,"",null,CancellationToken.None);
        // Mark the project first, so delayed saves cannot recreate any of its sessions.
        await WriteAsync(c=>Tombstone(c,"project",project));
        while(sessions.Count>0){foreach(var s in sessions)await DeleteSessionAsync(s.Session.Id);sessions=await SearchAsync(project,"",null,CancellationToken.None);}
        await WriteAsync(c=>{using var tx=c.BeginTransaction();using(var cmd=Command(c,"DELETE FROM corrections WHERE project=$p",("$p",project)))cmd.ExecuteNonQuery();using(var cmd=Command(c,"DELETE FROM terms WHERE scope=$p",("$p",project)))cmd.ExecuteNonQuery();using(var cmd=Command(c,"DELETE FROM projects WHERE id=$p",("$p",project)))cmd.ExecuteNonQuery();using(var cmd=Command(c,"DELETE FROM suppression WHERE scope=$p",("$p",project)))cmd.ExecuteNonQuery();tx.Commit();});
    }
    public async Task RetainAsync(int? days,CancellationToken token=default)
    {
        if(days==null)return; var cutoff=DateTimeOffset.UtcNow.AddDays(-days.Value);
        while(true){token.ThrowIfCancellationRequested();var batch=await Task.Run(()=>{using var c=Open();using var cmd=Command(c,"SELECT id FROM sessions WHERE created<$at LIMIT 100",("$at",cutoff.ToString("O")));using var r=cmd.ExecuteReader();var ids=new List<string>();while(r.Read())ids.Add(r.GetString(0));return ids;});if(batch.Count==0)break;foreach(var id in batch){token.ThrowIfCancellationRequested();await DeleteSessionAsync(id);}}
    }
    public Task CheckpointAsync() => WriteAsync(c=>{using var cmd=Command(c,"PRAGMA wal_checkpoint(TRUNCATE)");cmd.ExecuteNonQuery();});
    public async ValueTask DisposeAsync() { writes.Writer.TryComplete(); await writer.WaitAsync(TimeSpan.FromSeconds(3)); SqliteConnection.ClearAllPools(); }
}
