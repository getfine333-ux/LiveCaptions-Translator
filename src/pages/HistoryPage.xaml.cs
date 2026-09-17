// Modified derivative; see CHANGES.md. Original upstream attribution is retained in NOTICE.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using TextBlock = System.Windows.Controls.TextBlock;

namespace LiveCaptionsTranslator
{
    public partial class HistoryPage : Page
    {
        public const int MIN_HEIGHT = 300;

        private int currentPage = 1;
        private int searchPage = 1;
        private int maxPage = 1;
        private int maxRowPerPage = 30;

        public string SearchText { get; set; } = string.Empty;

        public HistoryPage()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();

            Loaded += async (s, e) =>
            {
                await LoadHistory();
                (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(minHeight: MIN_HEIGHT, maxHeight: MIN_HEIGHT);
                Translator.TranslationLogged += OnTranslationLogged;
            };
            Unloaded += (s, e) =>
            {
                HistoryDataGrid.ItemsSource = null;
                Translator.TranslationLogged -= OnTranslationLogged;
            };

            HistoryMaxRow.SelectionChanged += maxRow_SelectionChanged;
        }

        private async void OnTranslationLogged()
        {
            await LoadHistory();
        }

        private async void PageDown_click(object sender, RoutedEventArgs e)
        {
            if (currentPage - 1 >= 1)
                currentPage--;
            await LoadHistory();
        }

        private async void PageUp_click(object sender, RoutedEventArgs e)
        {
            if (currentPage < maxPage)
                currentPage++;
            await LoadHistory();
        }

        private async void Delete_click(object sender, RoutedEventArgs e)
        {
            var dialogHostContainer = (Application.Current.MainWindow as MainWindow)?.DialogHostContainer;

            var dialog = new ContentDialog
            {
                Title = new TextBlock
                {
                    Text = "Do you want to delete all history?",
                    FontSize = 18,
                    FontWeight = FontWeights.Regular
                },
                Content = "This operation cannot be undone!",
                PrimaryButtonText = "Yes",
                CloseButtonText = "No",
                DefaultButton = ContentDialogButton.Close,
                DialogHost = dialogHostContainer,
                Padding = new Thickness(8, 4, 8, 8),
            };

            dialogHostContainer.Visibility = Visibility.Visible;
            var result = await dialog.ShowAsync();
            dialogHostContainer.Visibility = Visibility.Collapsed;

            if (result == ContentDialogResult.Primary)
            {
                currentPage = 1;
                await SQLiteHistoryLogger.ClearHistory();
                await LoadHistory();
            }
        }

        private async void maxRow_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string tag = (e.AddedItems[0] as ComboBoxItem).Tag as string;
            maxRowPerPage = Convert.ToInt32(tag);

            await LoadHistory();

            if (currentPage > maxPage)
            {
                currentPage = maxPage;
                await LoadHistory();
            }
        }

        private async void Refresh_click(object sender, RoutedEventArgs e)
        {
            await LoadHistory();
        }

        private void Export_click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();

            menu.Items.Add(MakeExportItem("CSV (表格)", "csv",
                path => SQLiteHistoryLogger.ExportToCSV(path)));
            menu.Items.Add(MakeExportItem("TXT (纯文本)", "txt",
                path => SQLiteHistoryLogger.ExportToTxt(path)));
            menu.Items.Add(MakeExportItem("Markdown (会议笔记)", "md",
                path => SQLiteHistoryLogger.ExportToMarkdown(path)));
            menu.Items.Add(MakeExportItem("复盘模板 (复制给 AI)", "txt",
                path => SQLiteHistoryLogger.ExportReviewTemplate(path)));

            menu.PlacementTarget = Export;
            menu.IsOpen = true;
        }

        private System.Windows.Controls.MenuItem MakeExportItem(string header, string ext, Func<string, Task> export)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += async (s, e) =>
            {
                var dialog = new SaveFileDialog
                {
                    Filter = $"{ext.ToUpperInvariant()} (*.{ext})|*.{ext}|All files (*.*)|*.*",
                    DefaultExt = "." + ext,
                    FileName = $"meeting_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };

                if (dialog.ShowDialog() != true)
                    return;

                try
                {
                    await export(dialog.FileName);
                    SnackbarHost.Show("Saved Success.", $"File saved to: {dialog.FileName}", SnackbarType.Success);
                }
                catch (Exception ex)
                {
                    SnackbarHost.Show("Save Failed.", ex.Message, SnackbarType.Error);
                }
            };
            return item;
        }

        private async void HistorySearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            string searchText = (sender as AutoSuggestBox)?.Text ?? "";

            // Clear search by Ctrl+A and Delete and Enter
            if (string.IsNullOrEmpty(searchText))
            {
                SearchText = string.Empty;
                currentPage = searchPage;
            }
            else // Submit search
            {
                if (string.IsNullOrEmpty(SearchText))
                {
                    searchPage = currentPage;
                }
                SearchText = (sender as AutoSuggestBox)?.Text;
                currentPage = 1;
            }
            await LoadHistory();
        }

        private async void HistorySearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            // Press X to clear search box
            if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
            {
                if (!string.IsNullOrEmpty(SearchText))
                {
                    SearchText = string.Empty;
                    currentPage = searchPage;
                    await LoadHistory();
                }
            }
        }

        public async Task LoadHistory()
        {
            var data = await SQLiteHistoryLogger.LoadHistoryAsync(currentPage, maxRowPerPage, SearchText);
            List<TranslationHistoryEntry> history = data.Item1;

            maxPage = (data.Item2 > 0) ? data.Item2 : 1;

            await Dispatcher.InvokeAsync(() =>
            {
                HistoryDataGrid.ItemsSource = history;
                PageNumber.Text = currentPage.ToString() + "/" + maxPage.ToString();
            });
        }
    }
}