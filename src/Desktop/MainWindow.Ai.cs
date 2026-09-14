using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

public partial class MainWindow
{
    private readonly ObservableCollection<GeneratedTermRow> generatedRows=[];
    private bool generatingTerms,testingPrompt,importingGenerated,extractingHistory;
    private int generationSerial;
    private CancellationTokenSource? generationCancel,promptPreviewCancel;
    private string GenerationScope=>GenerationScopeBox.SelectedIndex==1?"*":CurrentProject;
    private string GenerationScopeName=>GenerationScope=="*"?"全局词库":controller.Projects.FirstOrDefault(p=>p.Id==CurrentProject)?.Name??"当前项目";
    private bool GeneratedExists(string word)=>controller.Terms.Any(t=>t.Scope==GenerationScope&&Lexicon.SameWord(t.Text,word));

    private async void PromptSave_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        EnsureIdle();await controller.SavePolishPromptAsync(PolishPromptBox.Text);PolishPromptBox.Text=controller.Settings.EffectivePolishPrompt;
        PromptTestStatus.Text="提示词已保存，将用于后续润色。";
    });
    private void PromptReset_Click(object sender,RoutedEventArgs e)
    {
        PolishPromptBox.Text=PolishRules.SystemPrompt;PromptTestStatus.Text="已恢复默认内容，点击“保存提示词”后生效。";
    }
    private async void PromptTest_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        EnsureIdle();testingPrompt=true;PromptEditorPanel.IsEnabled=false;promptPreviewCancel=new();
        try
        {
            PromptTestStatus.Text="正在试用当前提示词…";
            var result=await controller.PreviewPolishAsync(PolishPromptBox.Text,PolishSampleBox.Text,DeepSeekKeyBox.Password.Trim(),promptPreviewCancel.Token);
            PromptResultBox.Text=result.Text;
            PromptTestStatus.Text=result.Accepted?"试用完成："+result.Reason+"。":"试用完成，已保留原文："+result.Reason+"。";
        }
        catch{PromptTestStatus.Text="试用未完成，请查看错误提示。";throw;}
        finally{promptPreviewCancel.Dispose();promptPreviewCancel=null;testingPrompt=false;PromptEditorPanel.IsEnabled=true;}
    });

    private void SetGeneratedRows(IReadOnlyList<TermData> terms)
    {
        generatedRows.Clear();
        foreach(var term in terms)
        {
            var row=new GeneratedTermRow(term,GeneratedExists);
            row.PropertyChanged+=(_,_)=>RefreshGeneratedSelection();generatedRows.Add(row);
        }
        GeneratedTermsGrid.ItemsSource=generatedRows;RefreshGeneratedSelection();
    }
    private void RefreshGeneratedSelection()
    {
        if(GeneratedSelectionStatus==null)return;
        int existing=generatedRows.Count(r=>r.AlreadyExists);
        int selected=generatedRows.Count(r=>r.Selected&&!r.AlreadyExists);
        GeneratedSelectionStatus.Text=$"共 {generatedRows.Count} 个 · 已有 {existing} 个 · 已选可导入 {selected} 个 · 目标：{GenerationScopeName}";
        ImportGeneratedButton.IsEnabled=!generatingTerms&&!importingGenerated&&selected>0;
    }
    private void RefreshGeneratedMatches()
    {
        foreach(var row in generatedRows)row.RefreshStatus();RefreshGeneratedSelection();
    }
    private void GenerationScope_Changed(object sender,SelectionChangedEventArgs e){if(ready)RefreshGeneratedMatches();}
    private void GeneratedSelectAll_Click(object sender,RoutedEventArgs e){foreach(var row in generatedRows)row.Selected=!row.AlreadyExists;}
    private void GeneratedSelectNone_Click(object sender,RoutedEventArgs e){foreach(var row in generatedRows)row.Selected=false;}

    private async void GenerateTerms_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        EnsureIdle();
        if(!int.TryParse(GenerationCountBox.Text.Trim(),out int count))throw new ArgumentException("目标词数应为 1—500 的整数。");
        var options=new TermGenerationOptions(GenerationRequirementBox.Text.Trim(),count,GenerationScope,GenerationMaxThinkingBox.IsChecked==true);options.Validate();
        string key=DeepSeekKeyBox.Password.Trim();if(key.Length==0)throw new ArgumentException("请先在设置中填写 DeepSeek API Key。");
        generatingTerms=true;int serial=++generationSerial;generationCancel=new();
        GenerateTermsButton.IsEnabled=false;CancelGenerationButton.IsEnabled=true;GenerationRequestPanel.IsEnabled=false;GeneratedTermsGrid.IsEnabled=false;
        GeneratedSelectAllButton.IsEnabled=GeneratedSelectNoneButton.IsEnabled=false;
        SetGeneratedRows([]);GenerationProgressBar.Maximum=count;GenerationProgressBar.Value=0;GenerationProgressBar.IsIndeterminate=true;
        GenerationStatus.Text=$"正在生成 {count} 个词条…"+(options.MaxThinking?" 已开启 Max 思考，请稍候。":"");
        try
        {
            var progress=new Progress<TermGenerationProgress>(p=>
            {
                if(serial!=generationSerial||!generatingTerms||shuttingDown)return;
                SetGeneratedRows(p.Terms);GenerationProgressBar.IsIndeterminate=false;GenerationProgressBar.Value=p.Terms.Count;
                GenerationStatus.Text=p.Terms.Count>=p.Requested?"已生成目标数量，正在整理预览…":$"已生成 {p.Terms.Count}/{p.Requested} 个，正在处理第 {p.Requests+1} 批…";
            });
            var result=await controller.GenerateLexiconAsync(options,key,progress,generationCancel.Token);
            SetGeneratedRows(result.Terms);GenerationProgressBar.Value=result.Terms.Count;
            GenerationStatus.Text=result.Note+(result.Rejected>0?$" 已过滤 {result.Rejected} 个重复或无效结果。":"");
        }
        catch{GenerationStatus.Text="生成未完成，已显示的词条仍可检查和导入。";throw;}
        finally
        {
            generationCancel.Dispose();generationCancel=null;generatingTerms=false;
            GenerateTermsButton.IsEnabled=true;CancelGenerationButton.IsEnabled=false;GenerationRequestPanel.IsEnabled=true;GeneratedTermsGrid.IsEnabled=true;
            GeneratedSelectAllButton.IsEnabled=GeneratedSelectNoneButton.IsEnabled=true;GenerationProgressBar.IsIndeterminate=false;RefreshGeneratedSelection();
        }
    });
    private void CancelGeneration_Click(object sender,RoutedEventArgs e)
    {generationCancel?.Cancel();controller.CancelGeneration();CancelGenerationButton.IsEnabled=false;GenerationStatus.Text="正在取消，已完成的批次会保留。";}
    private async void ImportGenerated_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        EnsureIdle();
        string scope=GenerationScope;bool enable=GeneratedEnableBox.IsChecked==true;
        var rows=generatedRows.Where(r=>r.Selected).ToArray();
        var batch=rows.Select(r=>r.ToTerm(scope,enable)).ToArray();
        if(batch.Length==0)throw new InvalidOperationException("请先勾选要导入的词条。");
        importingGenerated=true;ImportGeneratedButton.IsEnabled=false;
        try
        {
            int added=await controller.ImportTermsAsync(batch,ignoreCase:true);
            foreach(var row in rows)row.Selected=false;RefreshGeneratedMatches();
            GenerationStatus.Text=$"已导入 {added} 个词条到{GenerationScopeName}，跳过 {batch.Length-added} 个重复项。"+(enable?"下一轮识别即可使用。":"可在“词库”页确认后启用。");
        }
        finally{importingGenerated=false;RefreshGeneratedSelection();}
    });
    private async void ExtractAllHistory_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        EnsureIdle();await SaveSettings();extractingHistory=true;
        AllHistoryButton.IsEnabled=HistoryExtractAllButton.IsEnabled=false;
        CancelHistoryButton.IsEnabled=HistoryCancelButton.IsEnabled=true;HistoryExtractionProgressBar.IsIndeterminate=true;
        HistoryExtractionStatus.Text="正在读取全部历史…";
        try
        {
            var progress=new Progress<HistoryExtractionProgress>(value=>
            {
                if(shuttingDown||!extractingHistory)return;
                HistoryExtractionProgressBar.IsIndeterminate=false;HistoryExtractionProgressBar.Maximum=Math.Max(1,value.Total);HistoryExtractionProgressBar.Value=value.Scanned;
                HistoryExtractionStatus.Text=$"已检查 {value.Scanned}/{value.Total} 条 · 完成 {value.Batches} 批 · 新增 {value.Added} · 更新 {value.Updated} · 跳过未允许整理 {value.SkippedSessions} 条。{value.Note}";
                StatusText.Text=HistoryExtractionStatus.Text;
            });
            var result=await controller.ExtractAllHistoryAsync(progress,CancellationToken.None);
            HistoryExtractionProgressBar.IsIndeterminate=false;HistoryExtractionProgressBar.Maximum=Math.Max(1,result.Total);HistoryExtractionProgressBar.Value=result.Scanned;
            HistoryExtractionStatus.Text=$"检查 {result.Scanned}/{result.Total} 条，完成 {result.Batches} 批，新增 {result.Added}、更新 {result.Updated} 个词条；跳过未允许整理 {result.SkippedSessions} 条。{result.Note}";
            StatusText.Text=HistoryExtractionStatus.Text;
        }
        finally
        {
            extractingHistory=false;AllHistoryButton.IsEnabled=HistoryExtractAllButton.IsEnabled=true;
            CancelHistoryButton.IsEnabled=HistoryCancelButton.IsEnabled=false;HistoryExtractionProgressBar.IsIndeterminate=false;
        }
    });
    private void CancelHistory_Click(object sender,RoutedEventArgs e)
    {controller.CancelExtraction();CancelHistoryButton.IsEnabled=HistoryCancelButton.IsEnabled=false;HistoryExtractionStatus.Text="正在取消，已完成的批次保留。";}
}

