using System.Text;
using System.Text.Json;

namespace RealtimeTranscription.Core;
public static class TermExchange
{
    public static IReadOnlyList<TermData> Parse(string text,string extension,string project)
    {
        if(Encoding.UTF8.GetByteCount(text)>5*1024*1024)throw new ArgumentException("导入文件最多 5 MiB。");
        var result=new List<TermData>();
        if(extension.Equals(".json",StringComparison.OrdinalIgnoreCase))
        {
            using var doc=JsonDocument.Parse(text,new JsonDocumentOptions{MaxDepth=20});var root=doc.RootElement;
            var list=root.ValueKind==JsonValueKind.Array?root:root.GetProperty("terms");
            foreach(var item in list.EnumerateArray())
            {
                var term=JsonSerializer.Deserialize<TermData>(item.GetRawText(),JsonCodec.Options)??throw new ArgumentException("词条格式无效。");
                if(item.TryGetProperty("enabled",out var enabled)&&enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)term=term with{State=enabled.GetBoolean()?TermState.Enabled:TermState.Disabled};
                if(item.TryGetProperty("protect_in_polish",out var protect)&&protect.ValueKind is JsonValueKind.True or JsonValueKind.False)term=term with{Protect=protect.GetBoolean()};
                result.Add(term with{Id=JsonCodec.Id(),Scope=term.Scope=="*"?"*":project,Revision=1,Origin="Imported",Evidence=[],UpdatedAt=DateTimeOffset.UtcNow});
            }
        }
        else if(extension.Equals(".csv",StringComparison.OrdinalIgnoreCase))
        {
            var rows=Csv(text);if(rows.Count==0)return result;
            var header=rows[0].Select(s=>s.Trim().TrimStart('\uFEFF').ToLowerInvariant()).ToArray();
            int column=Array.FindIndex(header,x=>x is "text" or "词条");if(column<0)throw new ArgumentException("CSV 第一行需要 text 或 词条 列。");
            string Read(string[] row,string en,string zh,string fallback=""){int index=Array.FindIndex(header,x=>x==en||x==zh);return index>=0&&index<row.Length?row[index]:fallback;}
            foreach(var row in rows.Skip(1))
            {
                if(row.All(string.IsNullOrWhiteSpace))continue;
                if(column>=row.Length)throw new ArgumentException("CSV 词条列缺失。");
                int weight=int.TryParse(Read(row,"weight","权重","3"),out int w)?w:throw new ArgumentException("CSV 权重必须为 1—5。");
                string status=Read(row,"state","状态","Enabled");TermState state=status switch{"Candidate" or "待确认"=>TermState.Candidate,"Disabled" or "已禁用"=>TermState.Disabled,"Enabled" or "已启用"=>TermState.Enabled,_=>throw new ArgumentException("CSV 词条状态无效。")};
                result.Add(new(){Text=row[column].Trim(),Category=Read(row,"category","类别","专业术语"),Weight=weight,State=state,Scope=Read(row,"scope","范围") is "*" or "全局"?"*":project,Origin="Imported"});
            }
        }
        else if(extension.Equals(".txt",StringComparison.OrdinalIgnoreCase))
        {
            foreach(string line in text.Split('\n'))if(line.Trim().TrimStart('\uFEFF') is {Length:>0} word)result.Add(new(){Text=word,Scope=project,Origin="Imported"});
        }
        else throw new ArgumentException("请选择 TXT、CSV 或 JSON 文件。");
        if(result.Count>10000)throw new ArgumentException("一次导入最多 10,000 条，未写入任何词条。");
        foreach(var term in result){term.Validate();if(!Enum.IsDefined(term.State))throw new ArgumentException("词条状态无效。");}
        return result.DistinctBy(t=>t.Key,StringComparer.Ordinal).ToArray();
    }
    public static string Export(IEnumerable<TermData> terms)=>JsonSerializer.Serialize(new{schemaVersion=1,terms=terms.Select(t=>t with{Evidence=[]})},new JsonSerializerOptions(JsonCodec.Options){WriteIndented=true});
    private static List<string[]> Csv(string input)
    {
        var rows=new List<string[]>();var row=new List<string>();var field=new StringBuilder();bool quoted=false;
        for(int i=0;i<input.Length;i++)
        {
            char c=input[i];
            if(c=='"'){if(quoted&&i+1<input.Length&&input[i+1]=='"'){field.Append('"');i++;}else if(quoted||field.Length==0)quoted=!quoted;else throw new ArgumentException("CSV 引号位置不正确。");}
            else if(!quoted&&c==','){row.Add(field.ToString());field.Clear();}
            else if(!quoted&&(c=='\n'||c=='\r')){if(c=='\r'&&i+1<input.Length&&input[i+1]=='\n')i++;row.Add(field.ToString());field.Clear();rows.Add(row.ToArray());row.Clear();}
            else field.Append(c);
        }
        if(quoted)throw new ArgumentException("CSV 引号未闭合。");
        if(field.Length>0||row.Count>0){row.Add(field.ToString());rows.Add(row.ToArray());}return rows;
    }
}
