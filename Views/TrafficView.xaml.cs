using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Views;

public partial class TrafficView : UserControl
{
    public static readonly DependencyProperty LanguageProperty =
        DependencyProperty.Register(nameof(Language), typeof(string), typeof(TrafficView),
            new PropertyMetadata("en"));

    private readonly ICollectionView _view;
    private TrafficEntry? _previewEntry;
    private bool _previewRefreshQueued;
    private bool _bulkImport;
    private FilterQuery _filterQuery = FilterQuery.Parse("", traffic: true);
    public string Language { get => (string)GetValue(LanguageProperty); set => SetValue(LanguageProperty, value); }

    public TrafficView()
    {
        InitializeComponent();
        Language = Services.Settings.Language;
        TrafficGrid.ItemsSource = Services.Traffic.Items;
        _view = CollectionViewSource.GetDefaultView(Services.Traffic.Items);
        _view.SortDescriptions.Add(new SortDescription(nameof(TrafficEntry.Id), ListSortDirection.Ascending));
        _view.Filter = Filter;
        Services.Traffic.Items.CollectionChanged += Items_CollectionChanged;
        UpdatePreviewLanguage();
        UpdateEmptyState();
    }

    private TrafficEntry? Selected => TrafficGrid.SelectedItem as TrafficEntry;
    private Window? Owner => Window.GetWindow(this);

    public void RefreshLanguage()
    {
        Language = Services.Settings.Language;
        UpdatePreviewLanguage();
        RequestBodyPreview.RefreshLanguage();
        ResponseBodyPreview.RefreshLanguage();
        foreach (var entry in Services.Traffic.Items)
            entry.RefreshLocalizedValues();
        UpdateCount();
        UpdateEmptyState();
    }

    private void UpdatePreviewLanguage()
    {
        RequestHeadersPreview.Language = Language;
        ResponseHeadersPreview.Language = Language;
    }

    private void TrafficGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_previewEntry is not null)
            _previewEntry.PropertyChanged -= SelectedEntry_PropertyChanged;

        _previewEntry = Selected;
        if (_previewEntry is null)
        {
            RequestHeadersPreview.HeadersText = "";
            ResponseHeadersPreview.HeadersText = "";
            RequestBodyPreview.SetContent("", "", false, false, null, null);
            ResponseBodyPreview.SetContent("", "", false, false, null, null);
            SetPreviewsLoading(false, false, false, false);
            return;
        }

        _previewEntry.PropertyChanged += SelectedEntry_PropertyChanged;
        SetPreviewsLoading(true, true, true, true);
        QueuePreviewRefresh();
    }

    private void SelectedEntry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrafficEntry.RequestHeaders)
            or nameof(TrafficEntry.RequestBody)
            or nameof(TrafficEntry.RequestContentType)
            or nameof(TrafficEntry.RequestIsBinary)
            or nameof(TrafficEntry.RequestPreviewUnavailable)
            or nameof(TrafficEntry.RequestBodyBytes)
            or nameof(TrafficEntry.RequestBinaryLength)
            or nameof(TrafficEntry.RequestBodyLoading)
            or nameof(TrafficEntry.ResponseHeaders)
            or nameof(TrafficEntry.ResponseHeadersLoading)
            or nameof(TrafficEntry.ResponseBody)
            or nameof(TrafficEntry.ResponseContentType)
            or nameof(TrafficEntry.ResponseIsBinary)
            or nameof(TrafficEntry.ResponsePreviewUnavailable)
            or nameof(TrafficEntry.ResponseBodyBytes)
            or nameof(TrafficEntry.ResponseBinaryLength)
            or nameof(TrafficEntry.ResponseLoading))
        {
            if (sender is TrafficEntry entry)
                QueuePreviewRefresh();
        }
    }

    private void QueuePreviewRefresh()
    {
        if (_previewRefreshQueued)
            return;

        _previewRefreshQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            _previewRefreshQueued = false;
            if (Selected is not { } selectedEntry)
                return;

            RequestHeadersPreview.HeadersText = selectedEntry.RequestHeaders;
            ResponseHeadersPreview.HeadersText = selectedEntry.ResponseHeaders;
            RequestBodyPreview.SetContent(selectedEntry.RequestBody, selectedEntry.RequestContentType,
                selectedEntry.RequestIsBinary, selectedEntry.RequestPreviewUnavailable,
                selectedEntry.RequestBodyBytes, selectedEntry.RequestBinaryLength);
            ResponseBodyPreview.SetContent(selectedEntry.ResponseBody, selectedEntry.ResponseContentType,
                selectedEntry.ResponseIsBinary, selectedEntry.ResponsePreviewUnavailable,
                selectedEntry.ResponseBodyBytes, selectedEntry.ResponseBinaryLength);

            SetPreviewsLoading(false, selectedEntry.RequestBodyLoading,
                selectedEntry.ResponseHeadersLoading, selectedEntry.ResponseLoading);
        }));
    }

    private void SetPreviewsLoading(bool requestHeaders, bool requestBody, bool responseHeaders, bool responseBody)
    {
        RequestHeadersPreview.IsLoading = requestHeaders;
        RequestBodyPreview.IsLoading = requestBody;
        ResponseHeadersPreview.IsLoading = responseHeaders;
        ResponseBodyPreview.IsLoading = responseBody;
    }

    public void RefreshTheme()
    {
        RequestBodyPreview.RefreshTheme();
        ResponseBodyPreview.RefreshTheme();
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_bulkImport)
            return;

        UpdateCount();
        UpdateEmptyState();
        if (FollowCheck.IsChecked == true && e.Action == NotifyCollectionChangedAction.Add && TrafficGrid.Items.Count > 0)
            TrafficGrid.ScrollIntoView(TrafficGrid.Items[TrafficGrid.Items.Count - 1]);
    }

    private void UpdateCount()
    {
        int count = Services.Traffic.Items.Count;
        CountText.Text = LocalizationService.Format(
            count == 1 ? "{0} requête" : "{0} requêtes", Services.Settings.Language, count);
    }

    private void UpdateEmptyState()
    {
        bool empty = TrafficGrid.Items.Count == 0;
        EmptyText.Text = FilterBox.Text.Trim().Length > 0
            ? LocalizationService.Translate("Aucun résultat", Services.Settings.Language)
            : LocalizationService.Translate("Aucune requête pour le moment", Services.Settings.Language);
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool Filter(object o)
    {
        if (o is not TrafficEntry e) return false;
        bool Contains(string? value, string query) =>
            value != null && value.Contains(query, StringComparison.OrdinalIgnoreCase);

        return _filterQuery.Matches(
            query => Contains(e.Host, query) || Contains(e.Path, query) || Contains(e.Process, query)
                || Contains(e.Method, query) || Contains(e.StatusText, query) || Contains(e.RuleName, query)
                || Contains(e.Outcome, query) || Contains(e.Url, query) || Contains(e.Pid.ToString(), query),
            (field, query) => field switch
            {
                "statuscode" or "status" => Contains(e.StatusCode?.ToString(), query),
                "host" => Contains(e.Host, query) || Contains(e.HostName, query),
                "path" => Contains(e.Path, query),
                "process" => Contains(e.Process, query) || Contains(e.ProcessPath, query),
                "pid" => Contains(e.Pid.ToString(), query),
                "method" => Contains(e.Method, query),
                "rule" => Contains(e.RuleName, query),
                "outcome" => Contains(e.Outcome, query),
                "scheme" => Contains(e.Scheme, query),
                "url" => Contains(e.Url, query),
                _ => false
            });
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterQuery = FilterQuery.Parse(FilterBox.Text, traffic: true);
        _view.Refresh();
        UpdateEmptyState();
    }
    private void Clear_Click(object sender, RoutedEventArgs e) => Services.Traffic.Clear();
    private void Pause_Changed(object sender, RoutedEventArgs e) => Services.Traffic.Paused = PauseCheck.IsChecked == true;

    private async void ImportHar_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "HTTP Archive (*.har;*.har.json)|*.har;*.har.json|JSON (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Owner) != true) return;

        try
        {
            var imported = await Task.Run(() => new HarTrafficService().Import(dialog.FileName));
            long firstId = Services.Proxy.ReserveExchangeIds(imported.Count);
            for (int i = 0; i < imported.Count; i++)
                imported[i].Id = firstId + i;
            _bulkImport = true;
            try
            {
                foreach (var entry in imported)
                    Services.Traffic.Items.Add(entry);
            }
            finally
            {
                _bulkImport = false;
            }
            Services.Traffic.MaxItems = Math.Max(Services.Traffic.MaxItems, Services.Traffic.Items.Count);
            UpdateCount();
            UpdateEmptyState();
            HttpFreeKit.AppDialog.Show(Owner,
                LocalizationService.Format("Imported {0} HAR entries. They were added to the current traffic log.",
                    Services.Settings.Language, imported.Count),
                LocalizationService.Translate("Import HAR", Services.Settings.Language),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            HttpFreeKit.AppDialog.Show(Owner,
                LocalizationService.Format("HAR import failed: {0}", Services.Settings.Language, ex.Message),
                LocalizationService.Translate("Import HAR", Services.Settings.Language),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportHar_Click(object sender, RoutedEventArgs e)
    {
        var redactChoice = HttpFreeKit.AppDialog.Show(Owner,
            LocalizationService.Translate(
                "Redact known sensitive header values in the HAR export? URLs and request/response bodies will not be changed.",
                Services.Settings.Language),
            LocalizationService.Translate("Protect HAR export", Services.Settings.Language),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (redactChoice == MessageBoxResult.Cancel)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "HTTP Archive (*.har)|*.har",
            DefaultExt = ".har",
            AddExtension = true,
            FileName = "traffic.har",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Owner) != true) return;

        try
        {
            var entries = Services.Traffic.Items.ToArray();
            await new HarTrafficService().ExportAsync(
                dialog.FileName, entries, redactChoice == MessageBoxResult.Yes);
            HttpFreeKit.AppDialog.Show(Owner,
                LocalizationService.Format("Exported {0} HAR entries.", Services.Settings.Language, entries.Length),
                LocalizationService.Translate("Export HAR", Services.Settings.Language),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            HttpFreeKit.AppDialog.Show(Owner,
                LocalizationService.Format("HAR export failed: {0}", Services.Settings.Language, ex.Message),
                LocalizationService.Translate("Export HAR", Services.Settings.Language),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }



    private void TrafficContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu contextMenu)
            LocalizationService.Apply(contextMenu, Services.Settings.Language);
    }

    private void Ctx_CopyUrl(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Clipboard.SetText(s.Url);
    }

    private void Ctx_CopyAs(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry
            || sender is not MenuItem { Tag: string formatName }
            || !Enum.TryParse(formatName, out RequestCodeFormat format))
            return;

        Clipboard.SetText(RequestCodeGenerator.Generate(entry, format));
    }

    private void Ctx_OpenNetTest(object sender, RoutedEventArgs e)
    {
        if (Selected is { } entry) OpenInNetTests(entry, false);
    }

    private void Ctx_ReplayNetTest(object sender, RoutedEventArgs e)
    {
        if (Selected is { } entry) OpenInNetTests(entry, true);
    }

    private void OpenInNetTests(TrafficEntry entry, bool sendImmediately)
    {
        var window = Window.GetWindow(this) as MainWindow;
        if (window is null) return;
        window.OpenNetTest(entry, sendImmediately);
    }

    private void AddRuleViaEditor(Rule prefilled)
    {
        var edited = RuleEditorWindow.Show(Owner, prefilled);
        if (edited == null) return;
        Services.Rules.Rules.Add(edited);
        Services.Rules.Commit();
    }

    private void Ctx_BlockHost(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        AddRuleViaEditor(new Rule
        {
            Name = $"Bloquer {s.HostName}",
            ActionType = RuleActionType.Block,
            Matchers = { new Matcher { Field = MatchField.Host, Op = MatchOp.Is, Value = s.HostName } }
        });
    }

    private void Ctx_Mock(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        AddRuleViaEditor(new Rule
        {
            Name = $"Mock {s.Method} {s.HostName}{s.Path}",
            ActionType = RuleActionType.Mock,
            StatusCode = s.StatusCode ?? 200,
            ContentType = string.IsNullOrEmpty(s.ResponseContentType) ? "application/json; charset=utf-8" : s.ResponseContentType,
            Body = Fmt.IsText(s.ResponseContentType) ? s.ResponseBody : "",
            Matchers =
            {
                new Matcher { Field = MatchField.Method, Op = MatchOp.Is, Value = s.Method },
                new Matcher { Field = MatchField.Url, Op = MatchOp.Is, Value = s.Url }
            }
        });
    }

    private void Ctx_NoDecrypt(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var st = Services.Settings;
        st.BypassHosts = (st.BypassHosts.TrimEnd() + "\n" + s.HostName).Trim();
        st.Save();
        HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Format(
            "“{0}” will no longer be decrypted for future connections (useful for applications that pin their certificate).",
            Services.Settings.Language, s.HostName), "HTTP FreeKit",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Ctx_BlockApp(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        if (string.IsNullOrEmpty(s.ProcessPath))
        {
            HttpFreeKit.AppDialog.Show(Owner,
                LocalizationService.Translate("Application path is unknown (the process is protected or has already exited).",
                    Services.Settings.Language), "HTTP FreeKit");
            return;
        }
        var r = HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Format(
            "Block ALL inbound and outbound traffic for:\n{0}\n\nusing Windows Firewall?",
            Services.Settings.Language, s.ProcessPath),
            LocalizationService.Translate("Block application", Services.Settings.Language),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        try { Services.Firewall.Block(s.ProcessPath); }
        catch (Exception ex) { HttpFreeKit.AppDialog.Show(Owner, ex.Message,
            LocalizationService.Translate("Windows Firewall", Services.Settings.Language),
            MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void BreakpointEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrafficEntry entry
            && entry.IsBreakpointPending)
            Services.Proxy.EditPendingBreakpoint(entry.Id);
    }

    private void BreakpointEdit_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not FrameworkElement button
            || ItemsControl.ContainerFromElement(TrafficGrid, button) is not DataGridRow row)
            return;

        string brushKey = row.IsSelected ? "SelectionBrush" : "SurfaceBrush";
        row.SetCurrentValue(Control.BackgroundProperty, FindResource(brushKey) as Brush);
    }

    private void BreakpointEdit_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement button
            && ItemsControl.ContainerFromElement(TrafficGrid, button) is DataGridRow row)
            row.ClearValue(Control.BackgroundProperty);
    }
}
