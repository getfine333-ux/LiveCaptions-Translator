// Modified derivative; see CHANGES.md. Original upstream attribution is retained in NOTICE.
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows.Input;
using System.Windows.Data;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator;

public partial class CaptionPage : Page
{
    private static CaptionPage? instance;
    private readonly bool preview;
    private long lastPresentationVersion = -1;
    private bool historyFollowsTop = true;
    private readonly CollectionViewSource recentHistory;
    public static CaptionPage? Instance => instance;

    public CaptionPage() : this(Translator.Caption!, Translator.Setting!.MainWindow.CaptionLogEnabled, false) { }

    // A supplied view model allows an offline layout preview without capture,
    // provider credentials, global settings or a running application session.
    public CaptionPage(object viewModel, bool historyEnabled, bool preview)
    {
        this.preview = preview;
        InitializeComponent();
        DataContext = viewModel;
        recentHistory = (CollectionViewSource)CaptionLayout.Resources["RecentHistory"];
        recentHistory.Filter += RecentHistory_Filter;
        if (!preview) instance = this;
        Loaded += (_, _) =>
        {
            AutoHeight();
            if (DataContext is INotifyPropertyChanged changes) changes.PropertyChanged += CaptionChanged;
            if (DataContext is models.Caption caption) caption.DisplayLogCards.CollectionChanged += HistoryChanged;
            if (!preview && App.Current.MainWindow is MainWindow window) window.CaptionLogButton.Visibility = Visibility.Visible;
        };
        Unloaded += (_, _) =>
        {
            if (DataContext is INotifyPropertyChanged changes) changes.PropertyChanged -= CaptionChanged;
            if (DataContext is models.Caption caption) caption.DisplayLogCards.CollectionChanged -= HistoryChanged;
            if (!preview && App.Current.MainWindow is MainWindow window) window.CaptionLogButton.Visibility = Visibility.Collapsed;
        };
        CollapseTranslatedCaption(historyEnabled);
    }

    private void Glossary_Click(object sender, RoutedEventArgs e)
    { if (!preview) GlossaryEditor.Show(Window.GetWindow(this)); }

    private void CaptionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not models.Caption caption || args.PropertyName != nameof(models.Caption.PresentationVersion)) return;
        if (caption.PresentationVersion == lastPresentationVersion) return;
        lastPresentationVersion = caption.PresentationVersion;
        recentHistory.View?.Refresh();
        // Start each deliberately released card at its beginning. Arrival of the
        // next result or an unreleased revision never changes this viewport.
        Dispatcher.BeginInvoke(new Action(() => ReadingScroll.ScrollToTop()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RecentHistory_Filter(object sender, FilterEventArgs e)
    {
        // The retained previous page is already readable above. Keep every
        // segment in history storage, but avoid displaying it twice on this page.
        if (e.Item is models.ReadingCard card && DataContext is models.Caption caption)
            e.Accepted = caption.PreviousReadingPage?.Results.Any(r => r.Segment.Id == card.Id) != true;
    }

    private void HistoryChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (historyFollowsTop && LogCardItems.Items.Count > 0)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (historyFollowsTop && LogCardItems.Items.Count > 0)
                    LogCardItems.ScrollIntoView(LogCardItems.Items[0]);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void PauseReading_Click(object sender, RoutedEventArgs e) => (DataContext as models.Caption)?.ToggleReadingPause();
    private void NextReading_Click(object sender, RoutedEventArgs e) => (DataContext as models.Caption)?.ReadNext();
    private void LatestReading_Click(object sender, RoutedEventArgs e) => (DataContext as models.Caption)?.JumpToLatest();
    private void Reading_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => (DataContext as models.Caption)?.HoldReading();
    private void Reading_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            (DataContext as models.Caption)?.HoldReading();
    }
    private void Reading_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer scroll) return;
        if (scroll.IsMouseCaptureWithin && e.VerticalChange != 0) (DataContext as models.Caption)?.HoldReading();
        if (sender == LogCardItems && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            historyFollowsTop = scroll.VerticalOffset <= 0.5;
    }

    private async void TextBlock_MouseLeftButtonDown(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock block) return;
        try
        {
            Clipboard.SetText(block.Text);
            SnackbarHost.Show("Copied.", block.Text, SnackbarType.Info, 100);
        }
        catch { SnackbarHost.Show("Copy Failed.", string.Empty, SnackbarType.Error, 100); }
        await Task.Delay(500);
    }

    public void CollapseTranslatedCaption(bool enabled)
    {
        LogCards.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        CaptionLogCard_Row.MinHeight = enabled ? 115 : 0;
        CaptionLogCard_Row.Height = enabled ? new GridLength(0.30, GridUnitType.Star) : new GridLength(0);
        AutoHeight();
    }

    // Called only on navigation or an explicit history toggle. New captions never
    // resize the window or shrink the font beneath the reader.
    public void AutoHeight()
    {
        if (!preview && App.Current.MainWindow is MainWindow window)
            window.AutoHeightAdjust(minHeight: Translator.Setting!.MainWindow.CaptionLogEnabled ? 460 : 300);
    }
}
