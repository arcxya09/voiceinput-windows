using System.ComponentModel;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;

public sealed class GeneratedTermRow : INotifyPropertyChanged
{
    private readonly TermData source;
    private readonly Func<string,bool> exists;
    private string text,category;
    private bool selected;
    public GeneratedTermRow(TermData source,Func<string,bool> exists)
    {this.source=source;this.exists=exists;text=source.Text;category=source.Category;selected=!AlreadyExists;}
    public bool Selected {get=>selected;set{selected=value;Changed(nameof(Selected));}}
    public string Text {get=>text;set{text=value;Changed(nameof(Text));RefreshStatus();}}
    public string Category {get=>category;set{category=value;Changed(nameof(Category));}}
    public bool AlreadyExists=>exists(Text);
    public string Status=>AlreadyExists?"已存在，导入时跳过":"可导入";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name)=>PropertyChanged?.Invoke(this,new(name));
    public void RefreshStatus(){Changed(nameof(Status));Changed(nameof(AlreadyExists));}
    public TermData ToTerm(string scope,bool enabled)
    {
        var term=source with{Id=JsonCodec.Id(),Scope=scope,Text=TermGenerationRules.Normalize(Text),Category=Category.Trim(),State=enabled?TermState.Enabled:TermState.Candidate};
        term.Validate();
        if(JsonCodec.Count(term.Category)>32||term.Category.Any(char.IsControl))throw new ArgumentException("词条类别最多 32 字，不能含有控制字符。");
        return term;
    }
}
