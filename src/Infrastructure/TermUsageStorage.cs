using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Infrastructure;

public sealed partial class MemoryRepository
{
    private List<TermData> ReadTermsWithUsage(SqliteConnection c,string project)
    {
        // Statistics are scoped to the current project even when the vocabulary entry is global.
        // Count sessions, not partial ASR frames, retries, or repeated mentions in one recording.
        using var query=Command(c,"""
SELECT t.payload,COALESCE(u.uses,0),COALESCE(u.corrections,0),u.last_used,u.last_corrected
FROM terms t LEFT JOIN (
    SELECT term,COUNT(DISTINCT session) uses,
        COUNT(DISTINCT CASE WHEN corrected=1 THEN session END) corrections,
        MAX(created) last_used,MAX(corrected_at) last_corrected
    FROM term_observations WHERE project=$p GROUP BY term
) u ON u.term=t.id WHERE t.scope=$p OR t.scope='*'
""",("$p",project));
        using var reader=query.ExecuteReader();var result=new List<TermData>();
        while(reader.Read())result.Add(Unpack<TermData>((byte[])reader[0]) with
        {
            UsageCount=reader.GetInt64(1),CorrectionCount=reader.GetInt64(2),
            LastUsedAt=reader.IsDBNull(3)?null:DateTimeOffset.Parse(reader.GetString(3),CultureInfo.InvariantCulture),
            LastCorrectedAt=reader.IsDBNull(4)?null:DateTimeOffset.Parse(reader.GetString(4),CultureInfo.InvariantCulture)
        });
        return result;
    }

    private static void RemoveTermObservationSession(SqliteConnection c,string session)
    {
        using var clear=Command(c,"DELETE FROM term_observations WHERE session=$id",("$id",session));clear.ExecuteNonQuery();
    }

    private void SyncTermObservations(SqliteConnection c,SessionData incoming,SegmentData segment,bool permitted)
    {
        // Replacing the segment's observations keeps edit, undo and deletion reversible. The
        // caller performs this only after accepting its revision, in the same transaction.
        using(var clear=Command(c,"DELETE FROM term_observations WHERE segment=$id",("$id",segment.Id)))clear.ExecuteNonQuery();
        using var permission=Command(c,"SELECT payload FROM sessions WHERE id=$id",("$id",incoming.Id));
        var session=permission.ExecuteScalar() is byte[] bytes?Unpack<SessionData>(bytes):null;
        if(!permitted||session?.AllowLearning!=true||segment.SessionId!=session.Id
            ||segment.AsrState!=AsrState.Confirmed||segment.OutputState!=OutputState.Published)return;

        // Machine-generated aliases/polish must not feed their own output back as observed use.
        string content=(segment.EditRevision>0?segment.FinalText:segment.RawText).Normalize(NormalizationForm.FormC);
        var applicable=ReadTerms(c,session.ProjectId).Concat(session.ProjectId=="*"?[]:ReadTerms(c,"*"))
            .Where(t=>t.State==TermState.Enabled)
            .GroupBy(t=>TermGenerationRules.Normalize(t.Text),StringComparer.Ordinal)
            .Select(g=>g.OrderByDescending(t=>t.Scope==session.ProjectId).ThenByDescending(t=>t.UpdatedAt).ThenBy(t=>t.Id,StringComparer.Ordinal).First());
        var corrections=CorrectionRules.Active(segment).Where(x=>CorrectionRules.ValidPair(x.Original,x.Corrected)).ToArray();
        var usedAt=segment.EditRevision>0&&segment.Edits.Count>0?segment.Edits.Max(e=>e.At):session.CreatedAt;
        foreach(var term in applicable)
        {
            if(!Lexicon.ContainsTerm(content,TermGenerationRules.Normalize(term.Text)))continue;
            var correctedAt=corrections.Where(x=>x.Corrected==term.Text).Select(x=>(DateTimeOffset?)x.At).Max();
            using var insert=Command(c,"""
INSERT INTO term_observations(term,segment,session,project,corrected,created,corrected_at)
VALUES($t,$s,$session,$p,$corrected,$at,$corrected_at)
""",("$t",term.Id),("$s",segment.Id),("$session",session.Id),("$p",session.ProjectId),
                ("$corrected",correctedAt.HasValue?1:0),("$at",usedAt.ToUniversalTime().ToString("O")),
                ("$corrected_at",correctedAt?.ToUniversalTime().ToString("O")));
            insert.ExecuteNonQuery();
        }
    }
}
