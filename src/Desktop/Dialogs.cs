using System.Windows;
using System.Windows.Controls;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;
internal static class Dialogs
{
    public static string? Ask(Window owner,string title,string label,string initial="")
    {
        var win=new Window{Owner=owner,Title=title,Width=520,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize};
        var panel=new StackPanel{Margin=new Thickness(22)};panel.Children.Add(new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
        var input=new TextBox{Text=initial,Margin=new Thickness(0,0,0,18)};panel.Children.Add(input);
        var yes=new Button{Content="确定",IsDefault=true,HorizontalAlignment=HorizontalAlignment.Right};yes.Click+=(_,_)=>win.DialogResult=true;panel.Children.Add(yes);win.Content=panel;win.Loaded+=(_,_)=>{input.Focus();input.SelectAll();};
        return win.ShowDialog()==true?input.Text:null;
    }
    public static void Text(Window owner,string title,string text)
    {
        var win=new Window{Owner=owner,Title=title,Width=720,Height=520,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        win.Content=new TextBox{Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(18)};win.ShowDialog();
    }
    public static TermData? Term(Window owner,TermData term)
    {
        var win=new Window{Owner=owner,Title="词条设置",Width=550,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner};var p=new StackPanel{Margin=new Thickness(22)};
        TextBox Field(string label,string initial){p.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,10,0,5)});var box=new TextBox{Text=initial};p.Children.Add(box);return box;}
        var word=Field("词条（1—64 字）",term.Text);var category=Field("类别",term.Category);var alias=Field("旧写法（仅供追溯，不自动替换）",term.Alias);var weight=Field("识别权重（1—5）",term.Weight.ToString());
        var global=new CheckBox{Content="全局词条",IsChecked=term.Scope=="*",Margin=new Thickness(0,14,0,4)};p.Children.Add(global);
        var protect=new CheckBox{Content="润色时保护写法",IsChecked=term.Protect};p.Children.Add(protect);var pinned=new CheckBox{Content="优先加入识别热词",IsChecked=term.Pinned};p.Children.Add(pinned);
        var state=new ComboBox{ItemsSource=new[]{"已启用","待确认","已禁用"},SelectedIndex=term.State switch{TermState.Enabled=>0,TermState.Candidate=>1,_=>2},Margin=new Thickness(0,5,0,14)};p.Children.Add(state);
        TermData? result=null;var save=new Button{Content="保存",IsDefault=true,HorizontalAlignment=HorizontalAlignment.Right};
        save.Click+=(_,_)=>{try{result=term with{Text=word.Text.Trim(),Category=category.Text.Trim(),Alias=alias.Text.Trim(),Weight=int.Parse(weight.Text),Scope=global.IsChecked==true?"*":term.Scope=="*"?((MainWindow)owner).CurrentProject:term.Scope,Protect=protect.IsChecked==true,Pinned=pinned.IsChecked==true,State=state.SelectedIndex switch{0=>TermState.Enabled,1=>TermState.Candidate,_=>TermState.Disabled}};result.Validate();win.DialogResult=true;}catch(Exception){MessageBox.Show(win,"请检查词条、范围和权重。","词条",MessageBoxButton.OK,MessageBoxImage.Information);}};
        p.Children.Add(save);win.Content=p;return win.ShowDialog()==true?result:null;
    }
    public static async Task Review(Window owner,AppController controller)
    {
        var snapshot=await controller.SnapshotAsync();
        var win=new Window{Owner=owner,Title="原文对照与编辑（只修改本地记录）",Width=1060,Height=720,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var grid=new Grid{Margin=new Thickness(18)};grid.ColumnDefinitions.Add(new(){Width=new GridLength(290)});grid.ColumnDefinitions.Add(new());
        var list=new ListBox{ItemsSource=snapshot.Segments,DisplayMemberPath="ViewLabel",Margin=new Thickness(0,0,16,0)};grid.Children.Add(list);
        var panel=new DockPanel();Grid.SetColumn(panel,1);grid.Children.Add(panel);
        var actions=new WrapPanel();DockPanel.SetDock(actions,Dock.Bottom);panel.Children.Add(actions);
        var body=new Grid();body.RowDefinitions.Add(new(){Height=new GridLength(28)});body.RowDefinitions.Add(new());body.RowDefinitions.Add(new(){Height=new GridLength(28)});body.RowDefinitions.Add(new());panel.Children.Add(body);
        var rawTitle=new TextBlock{Text="服务端原文 / 未确认草稿"};body.Children.Add(rawTitle);
        var raw=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,0,0,14)};Grid.SetRow(raw,1);body.Children.Add(raw);
        var finalTitle=new TextBlock{Text="片段修订（保存后，本轮正文按修订片段重新拼接）"};Grid.SetRow(finalTitle,2);body.Children.Add(finalTitle);
        var final=new TextBox{TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,0,0,14)};Grid.SetRow(final,3);body.Children.Add(final);
        async Task Refresh(string? id){var snap=await controller.SnapshotAsync();list.ItemsSource=snap.Segments;list.SelectedItem=snap.Segments.FirstOrDefault(s=>s.Id==id);}
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is SegmentData s){raw.Text=s.RawText.Length>0?s.RawText:s.PartialText;final.Text=s.FinalText;final.IsReadOnly=s.OutputState is not (OutputState.Published or OutputState.Deleted);}};
        void Button(string label,Func<SegmentData,Task> act){var b=new Button{Content=label};b.Click+=async(_,_)=>{if(list.SelectedItem is not SegmentData s)return;try{await act(s);await Refresh(s.Id);}catch(Exception e){MessageBox.Show(win,AppController.SafeError(e));}};actions.Children.Add(b);}
        Button("保存编辑",s=>controller.EditAsync(s.Id,final.Text));Button("恢复原文",s=>controller.EditAsync(s.Id,s.RawText,"恢复原文"));Button("删除片段",s=>controller.EditAsync(s.Id,"","删除"));
        Button("撤销上次编辑",s=>controller.UndoAsync(s.Id));
        Button("修改历史",s=>{Text(win,"修改历史",string.Join("\n\n",s.Edits.Select(e=>$"{e.At.LocalDateTime:g} · {e.Action}\n{e.Text}")));return Task.CompletedTask;});
        Button("记住此写法",async s=>{string? word=Ask(win,"记住此写法","填写要记住的标准词条（不是整段正文）",final.SelectedText);if(string.IsNullOrWhiteSpace(word))return;string? old=Ask(win,"原写法","填写原写法，可留空",raw.SelectedText);if(old!=null)await controller.RememberAsync(word,old);});
        var learned=new Button{Content="查看纠错候选"};learned.Click+=(_,_)=>{win.Close();((MainWindow)owner).OpenCorrections();};actions.Children.Add(learned);
        var tabs=new TabControl();var whole=new Grid{Margin=new Thickness(18)};
        whole.RowDefinitions.Add(new(){Height=new GridLength(30)});whole.RowDefinitions.Add(new());whole.RowDefinitions.Add(new(){Height=new GridLength(30)});whole.RowDefinitions.Add(new());
        whole.Children.Add(new TextBlock{Text="本轮完整识别原文"});
        var rawWhole=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Text=TranscriptText.Render(snapshot.Segments.Select(s=>s with{FinalText=s.RawText}))};Grid.SetRow(rawWhole,1);whole.Children.Add(rawWhole);
        var wholeLabel=new TextBlock{Text="本轮最终正文 · "+snapshot.Session?.WholePolishReason,Margin=new Thickness(0,8,0,0)};Grid.SetRow(wholeLabel,2);whole.Children.Add(wholeLabel);
        var finalWhole=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Text=TranscriptText.Render(snapshot)};Grid.SetRow(finalWhole,3);whole.Children.Add(finalWhole);
        tabs.Items.Add(new TabItem{Header="全文对照",Content=whole});tabs.Items.Add(new TabItem{Header="原始片段修订",Content=grid});
        tabs.SelectionChanged+=async(_,e)=>{if(e.Source!=tabs||tabs.SelectedIndex!=0)return;var latest=await controller.SnapshotAsync();finalWhole.Text=TranscriptText.Render(latest);wholeLabel.Text="本轮最终正文 · "+latest.Session?.WholePolishReason;};
        win.Content=tabs;if(snapshot.Segments.Count>0)list.SelectedIndex=0;win.ShowDialog();
    }
}
