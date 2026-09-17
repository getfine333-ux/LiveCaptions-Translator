using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator;

public partial class GlossaryWindow : Window
{
    private readonly GlossaryFile file;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<GlossaryTerm>>>? generate;
    private readonly Action saved;
    private CancellationTokenSource? generation;
    private string original = "";
    private bool loaded, closed;
    private string Say(string zh, string en) => Loc.IsZh ? zh : en;

    public GlossaryWindow(string path, string providerDescription,
        Func<string, CancellationToken, Task<IReadOnlyList<GlossaryTerm>>>? generate, Action saved)
    {
        this.file = new(path); this.generate = generate; this.saved = saved;
        InitializeComponent();
        ProviderText.Text = providerDescription;
        if (!Loc.IsZh)
        {
            Title = "Glossary"; Heading.Text = "Prepare terms for this session";
            TopicLabel.Text = "What will you be working on?";
            ExampleText.Text = "For example: semiconductor lithography, focusing on photomasks, wafers, diffraction and process nodes.";
            GenerateButton.Content = "Generate draft"; CancelButton.Content = "Cancel generation";
            MergeBox.Content = "Merge with existing terms; keep existing translations";
            EditorLabel.Text = "Editable draft · One source = target entry per line";
            CloseButton.Content = "Close"; SaveButton.Content = "Save and apply";
            SaveHint.Text = "Saving applies to subsequent captions. Generating a draft leaves the file unchanged.";
        }
        try
        {
            original = file.Load(); EditorBox.Text = original; loaded = true;
            StatusText.Text = Say("描述领域后生成，或直接编辑现有术语。AI 草稿请核对后保存。", "Generate from your topic, or edit existing terms. Review AI suggestions before saving.");
        }
        catch (Exception ex)
        {
            StatusText.Text = Say("无法读取术语表：", "Unable to read glossary: ") + ex.Message;
            SaveButton.IsEnabled = false; EditorBox.IsReadOnly = true;
        }
        GenerateButton.IsEnabled = loaded && generate != null;
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; generation?.Cancel(); };
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (generate == null || generation != null || !loaded) return;
        if (TopicBox.Text.Trim().Length < 2)
        { StatusText.Text = Say("请先简单描述本次工作涉及的领域或主题。", "Describe the topic for this session first."); TopicBox.Focus(); return; }
        try { if (MergeBox.IsChecked == true) GlossaryDocument.Parse(EditorBox.Text); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        using var cancellation = new CancellationTokenSource();
        generation = cancellation;
        SetBusy(true);
        StatusText.Text = Say("正在生成术语草稿…", "Generating glossary draft…");
        try
        {
            var terms = await generate(TopicBox.Text, cancellation.Token);
            if (closed || cancellation.IsCancellationRequested) return;
            var merged = GlossaryDocument.Merge(EditorBox.Text, terms, MergeBox.IsChecked == true);
            EditorBox.Text = merged.Text;
            StatusText.Text = Say($"已加入 {merged.Added} 条术语，{merged.Kept} 条同名项保留原译法。可继续编辑，保存后生效。",
                $"Added {merged.Added} terms; kept {merged.Kept} existing entries. Edit as needed, then save to apply.");
        }
        catch (OperationCanceledException) { if (!closed) StatusText.Text = Say("已取消生成，草稿未修改。", "Generation cancelled. Draft unchanged."); }
        catch (Exception ex) { if (!closed) StatusText.Text = Say("生成未完成：", "Generation failed: ") + ex.Message; }
        finally { generation = null; if (!closed) SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        GenerateButton.IsEnabled = !busy && generate != null;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = !busy && loaded;
        EditorBox.IsReadOnly = busy; TopicBox.IsReadOnly = busy; MergeBox.IsEnabled = !busy;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        generation?.Cancel();
        StatusText.Text = Say("已取消生成，草稿未修改。", "Generation cancelled. Draft unchanged.");
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!loaded || generation != null) return;
        bool written = false;
        try
        {
            int count = GlossaryDocument.Parse(EditorBox.Text).Count;
            file.Save(EditorBox.Text); written = true; original = EditorBox.Text; saved();
            StatusText.Text = Say($"已保存并应用 {count} 条术语，后续字幕将使用这份术语表。", $"Saved and applied {count} terms for subsequent captions.");
        }
        catch (Exception ex) { StatusText.Text = (written ? Say("术语文件已保存，但应用设置失败：", "File saved, but applying settings failed: ") :
            Say("保存失败，草稿仍保留：", "Save failed; draft retained: ")) + ex.Message; }
    }
    private void Editor_Changed(object sender, TextChangedEventArgs e)
    {
        if (loaded && generation == null) StatusText.Text = Say("草稿已修改，保存后生效。", "Draft edited. Save to apply.");
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (loaded && EditorBox.Text != original && MessageBox.Show(this,
            Say("草稿尚未保存，是否放弃这些修改？", "Discard unsaved glossary changes?"), Say("未保存的术语", "Unsaved glossary"),
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
    }
}
