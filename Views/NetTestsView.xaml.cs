using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Views;

public partial class NetTestsView : UserControl
{
    private readonly ObservableCollection<NetTestTab> _tabs;
    private readonly DispatcherTimer _saveTimer;
    private CancellationTokenSource? _sendCancellation;
    private bool _loading = true;
    private bool _sending;
    private Point _tabDragStart;
    private NetTestTab? _draggedTab;
    private bool _tabDragMoved;
    private bool _updatingTabsScrollBar;
    private AdornerLayer? _tabDragAdornerLayer;
    private TabDragAdorner? _tabDragAdorner;

    public int TabCount => _tabs.Count;

    public NetTestsView()
    {
        InitializeComponent();
        _tabs = new ObservableCollection<NetTestTab>(Services.NetTests.LoadTabs());
        foreach (var tab in _tabs.Where(tab => tab.Name == "Nouvelle requête"))
            tab.Name = T("Nouvelle requête");
        if (_tabs.Count == 0) _tabs.Add(new NetTestTab());
        RequestTabs.ItemsSource = _tabs;
        RequestTabs.SelectedIndex = 0;
        LoadTab(_tabs[0]);
        if (Services.NetTests.LoadWarning is { } warning) ShowWarning(warning);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => SaveTabs();
        Loaded += (_, _) => RefreshLanguage();
    }

    private static string T(string text)
        => LocalizationService.Translate(text, Services.Settings.Language);

    private static string TranslateError(string message)
    {
        const string unresolvedPrefix = "Unresolved variables in URL: ";
        if (message.StartsWith(unresolvedPrefix, StringComparison.Ordinal))
            return T(unresolvedPrefix) + message[unresolvedPrefix.Length..];
        const string redirectPrefix = "Maximum of ";
        const string redirectSuffix = " redirects reached.";
        if (message.StartsWith(redirectPrefix, StringComparison.Ordinal)
            && message.EndsWith(redirectSuffix, StringComparison.Ordinal))
        {
            var count = message[redirectPrefix.Length..^redirectSuffix.Length];
            return string.Format(T("Maximum of {0} redirects reached."), count);
        }
        const string timeoutPrefix = "Request timed out after ";
        const string timeoutSuffix = " seconds.";
        if (message.StartsWith(timeoutPrefix, StringComparison.Ordinal)
            && message.EndsWith(timeoutSuffix, StringComparison.Ordinal))
        {
            var seconds = message[timeoutPrefix.Length..^timeoutSuffix.Length];
            return string.Format(T("Request timed out after {0} seconds."), seconds);
        }
        const string socketPrefix = "Connection failed (";
        const string socketSeparator = "): ";
        if (message.StartsWith(socketPrefix, StringComparison.Ordinal))
        {
            var separator = message.IndexOf(socketSeparator, socketPrefix.Length, StringComparison.Ordinal);
            if (separator >= 0)
                return string.Format(T("Connection failed ({0}): {1}"),
                    message[socketPrefix.Length..separator], message[(separator + socketSeparator.Length)..]);
        }
        const string charsetPrefix = "Charset “";
        const string charsetSuffix = "” is not supported.";
        if (message.StartsWith(charsetPrefix, StringComparison.Ordinal)
            && message.EndsWith(charsetSuffix, StringComparison.Ordinal))
            return string.Format(T("Charset “{0}” is not supported."),
                message[charsetPrefix.Length..^charsetSuffix.Length]);
        const string invalidHeaderPrefix = "Invalid value for header “";
        if (message.StartsWith(invalidHeaderPrefix, StringComparison.Ordinal)
            && message.EndsWith("”.", StringComparison.Ordinal))
            return string.Format(T("Invalid value for header “{0}”."),
                message[invalidHeaderPrefix.Length..^2]);
        const string unsupportedHeaderPrefix = "Invalid or unsupported header: “";
        if (message.StartsWith(unsupportedHeaderPrefix, StringComparison.Ordinal)
            && message.EndsWith("”.", StringComparison.Ordinal))
            return string.Format(T("Invalid or unsupported header: “{0}”."),
                message[unsupportedHeaderPrefix.Length..^2]);
        return T(message);
    }

    public void RefreshLanguage()
    {
        LocalizationService.Apply(this, Services.Settings.Language);
        foreach (var tab in _tabs.Where(tab => tab.IsPlaceholder))
            tab.Name = T("New request");
        ResponseBodyPreview.Language = Services.Settings.Language;
        ResponseBodyPreview.RefreshLanguage();
        ResponseHeadersPreview.Language = Services.Settings.Language;
    }

    public void RefreshTheme()
    {
        BodyBox.RefreshTheme();
        ResponseBodyPreview.RefreshTheme();
    }

    public void OpenFromTraffic(TrafficEntry entry, bool sendImmediately = false)
    {
        var tab = new NetTestTab
        {
            Name = $"{entry.Method} {entry.HostName}",
            Method = entry.Method,
            Url = entry.Url,
            Headers = ParseHeaders(entry.RequestHeaders),
            ContentType = entry.RequestContentType,
            BodyText = entry.RequestBody.StartsWith("(lecture impossible :", StringComparison.Ordinal)
                ? "" : entry.RequestBody,
            BodyBase64 = entry.RequestReplayBody is { } bytes
                ? Convert.ToBase64String(bytes)
                : entry.RequestBodyIncomplete && entry.RequestPayloadPreview is { Length: > 0 } preview
                    ? Convert.ToBase64String(preview) : null,
            BodyIsBinary = entry.RequestIsBinary,
            OriginalTrafficId = entry.Id,
            OriginalBodyIncomplete = entry.RequestBodyIncomplete
        };
        if (entry.RequestReplayBody is { } raw && Fmt.IsText(entry.RequestContentType)
            && Fmt.LooksText(raw))
        {
            try { tab.BodyText = new UTF8Encoding(false, true).GetString(raw); }
            catch (DecoderFallbackException) { tab.BodyText = ""; }
            tab.BodyIsBinary = false;
        }
        AddTabAndSelect(tab);
        ScheduleSave();
        if (sendImmediately) _ = SendAsync();
    }

    private static List<NetTestHeader> ParseHeaders(string text)
    {
        var result = new List<NetTestHeader>();
        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Expect", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(new NetTestHeader { Name = name, Value = line[(colon + 1)..].Trim() });
        }
        return result;
    }

    private NetTestTab? Selected => RequestTabs.SelectedItem as NetTestTab;

    private void RequestTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || e.Source != RequestTabs || Selected is not { } tab) return;
        LoadTab(tab);
    }

    private void LoadTab(NetTestTab tab)
    {
        _loading = true;
        MethodBox.SelectedItem = GetMethodItem(tab.Method);
        UrlBox.Text = tab.Url;
        HeadersGrid.ItemsSource = null;
        HeadersGrid.ItemsSource = tab.Headers;
        ContentTypeBox.Text = tab.ContentType;
        tab.BodyText = Fmt.FormatBody(tab.ContentType, tab.BodyText);
        BodyBox.Text = tab.BodyIsBinary ? "" : tab.BodyText;
        BodyBox.IsJson = Fmt.DetectBodyFormat(tab.ContentType, tab.BodyText) == Fmt.BodyFormat.Json;
        FollowRedirectsCheck.IsChecked = tab.FollowRedirects;
        IgnoreTlsCheck.IsChecked = tab.IgnoreCertificateErrors;
        HttpFreeKitProxyCheck.IsChecked = tab.UseHttpFreeKitProxy;
        HttpVersionBox.SelectedIndex = tab.HttpVersion switch { 1 => 0, 2 => 1, _ => 2 };
        TimeoutBox.Text = tab.TimeoutSeconds.ToString();
        AllowIncompleteCheck.IsChecked = tab.AllowIncompleteBody;
        AllowIncompleteCheck.Visibility = tab.OriginalBodyIncomplete ? Visibility.Visible : Visibility.Collapsed;
        WarningText.Text = tab.OriginalBodyIncomplete
            ? LocalizationService.Translate("Le corps original n’a pas été capturé intégralement. Le rejeu est désactivé sans confirmation explicite.", Services.Settings.Language)
            : tab.IgnoreCertificateErrors
                ? LocalizationService.Translate("La validation du certificat TLS est désactivée pour cette requête.", Services.Settings.Language)
                : "";
        WarningText.Visibility = string.IsNullOrEmpty(WarningText.Text) ? Visibility.Collapsed : Visibility.Visible;
        ResponseSummary.Text = tab.ResponseSummary ?? T("La réponse apparaîtra ici.");
        ResponseHeadersPreview.HeadersText = tab.ResponseHeaders ?? "";
        ResponseHeadersPreview.Language = Services.Settings.Language;
        ResponseBodyPreview.ContentText = tab.ResponseIsBinary ? "" : tab.ResponseBody ?? "";
        ResponseBodyPreview.ContentType = tab.ResponseContentType;
        ResponseBodyPreview.IsBinary = tab.ResponseIsBinary;
        ResponseBodyPreview.BinaryBytes = tab.ResponseBodyBytes;
        ResponseBodyPreview.BinaryLength = tab.ResponseBodyBytes?.LongLength;
        ResponseRedirectsBox.Text = tab.ResponseRedirects ?? "";
        _loading = false;
    }

    private void UpdateSelectedFromEditor()
    {
        if (_loading || Selected is not { } tab) return;
        tab.Method = (MethodBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "GET";
        tab.Url = UrlBox.Text;
        tab.Headers = HeadersGrid.ItemsSource is IEnumerable<NetTestHeader> rows
            ? rows.ToList() : new List<NetTestHeader>();
        tab.ContentType = ContentTypeBox.Text;
        if (!tab.BodyIsBinary && BodyBox.Text != tab.BodyText)
        {
            tab.BodyText = BodyBox.Text;
            tab.BodyBase64 = null;
        }
        tab.FollowRedirects = FollowRedirectsCheck.IsChecked == true;
        tab.IgnoreCertificateErrors = IgnoreTlsCheck.IsChecked == true;
        tab.UseHttpFreeKitProxy = HttpFreeKitProxyCheck.IsChecked == true;
        tab.HttpVersion = HttpVersionBox.SelectedIndex switch { 0 => 1, 1 => 2, _ => 0 };
        if (int.TryParse(TimeoutBox.Text, out int timeout)) tab.TimeoutSeconds = timeout;
        tab.AllowIncompleteBody = AllowIncompleteCheck.IsChecked == true;
        ScheduleSave();
    }

    private void RequestChanged(object sender, RoutedEventArgs e) => UpdateSelectedFromEditor();
    private void RequestChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectedFromEditor();
    private void RequestChanged(object sender, TextChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ContentTypeBox))
            BodyBox.IsJson = Fmt.DetectBodyFormat(ContentTypeBox.Text, null) == Fmt.BodyFormat.Json;
        UpdateSelectedFromEditor();
    }

    private void BodyBox_TextChanged(object? sender, EventArgs e) => UpdateSelectedFromEditor();

    private void BodyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loading || Selected is not { } tab || tab.BodyIsBinary) return;
        var formatted = Fmt.FormatBody(ContentTypeBox.Text, BodyBox.Text);
        if (formatted == BodyBox.Text) return;
        BodyBox.Text = formatted;
        tab.BodyText = formatted;
        tab.BodyBase64 = null;
        ScheduleSave();
    }

    private void Method_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MethodBox is null) return;
        UpdateSelectedFromEditor();
    }

    private ComboBoxItem GetMethodItem(string method)
    {
        var existing = MethodBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), method, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var legacyCustomMethod = new ComboBoxItem { Content = method };
        MethodBox.Items.Add(legacyCustomMethod);
        return legacyCustomMethod;
    }
    private void HeadersGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(new Action(UpdateSelectedFromEditor), DispatcherPriority.Background);

    private void AddHeader_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } tab) return;
        tab.Headers.Add(new NetTestHeader());
        LoadTab(tab);
        HeadersGrid.SelectedIndex = HeadersGrid.Items.Count - 1;
        HeadersGrid.ScrollIntoView(HeadersGrid.SelectedItem);
        HeadersGrid.BeginEdit();
        ScheduleSave();
    }

    private void RemoveHeader_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } tab || HeadersGrid.SelectedItem is not NetTestHeader header) return;
        tab.Headers.Remove(header);
        LoadTab(tab);
        ScheduleSave();
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        UpdateSelectedFromEditor();
        if (Selected is not { } tab || _sending) return;
        if (tab.OriginalBodyIncomplete && !tab.AllowIncompleteBody)
        {
            ShowWarning(T("Le corps original est incomplet. Confirmez explicitement l’envoi depuis l’onglet Corps."));
            return;
        }
        var options = new NetTestSendOptions
        {
            FollowRedirects = tab.FollowRedirects,
            MaxRedirects = tab.MaxRedirects,
            TimeoutSeconds = tab.TimeoutSeconds,
            IgnoreCertificateErrors = tab.IgnoreCertificateErrors,
            UseHttpFreeKitProxy = tab.UseHttpFreeKitProxy,
            HttpVersion = tab.HttpVersion
        };
        _sending = true;
        _sendCancellation = new CancellationTokenSource();
        SendButton.Content = T("Annuler");
        SendButton.Click -= Send_Click;
        SendButton.Click += Cancel_Click;
        CancelButton.Visibility = Visibility.Visible;
        WarningText.Visibility = Visibility.Collapsed;
        try
        {
            var result = await Services.NetTests.SendAsync(tab, options, _sendCancellation.Token);
            tab.ResponseSummary = $"{result.StatusCode} {result.ReasonPhrase} · {result.Duration.TotalMilliseconds:0} ms · HTTP/{result.Version} · {result.FinalUri}";
            tab.ResponseHeaders = string.Join("\r\n", result.Headers.SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));
            tab.ResponseRedirects = string.Join(Environment.NewLine, result.Redirects);
            var charset = result.ContentType.Contains("charset=", StringComparison.OrdinalIgnoreCase)
                ? result.ContentType.Split("charset=", StringSplitOptions.None)[1].Split(';')[0].Trim()
                : "utf-8";
            tab.ResponseContentType = result.ContentType;
            tab.ResponseIsBinary = !Fmt.IsText(result.ContentType) && !Fmt.LooksText(result.Body);
            tab.ResponseBodyBytes = tab.ResponseIsBinary ? result.Body : null;
            try
            {
                tab.ResponseBody = tab.ResponseIsBinary
                    ? ""
                    : Encoding.GetEncoding(charset).GetString(result.Body);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                tab.ResponseBody = tab.ResponseIsBinary ? "" : Encoding.UTF8.GetString(result.Body);
            }
            if (result.BodyTruncated)
                tab.ResponseSummary += " · " + T("corps complet disponible à l’enregistrement");
            tab.ResponseError = null;
            tab.ResponseFilePath = result.BodyFilePath;
        }
        catch (OperationCanceledException)
        {
            tab.ResponseError = T("Requête annulée.");
            tab.ResponseSummary = tab.ResponseError;
            tab.ResponseBody = "";
            tab.ResponseContentType = "";
            tab.ResponseBodyBytes = null;
            tab.ResponseIsBinary = false;
            tab.ResponseHeaders = "";
            tab.ResponseRedirects = "";
        }
        catch (Exception ex)
        {
            tab.ResponseError = TranslateError(ex.Message);
            tab.ResponseSummary = T("Échec de la requête");
            tab.ResponseBody = tab.ResponseError;
            tab.ResponseHeaders = "";
            tab.ResponseContentType = "text/plain";
            tab.ResponseBodyBytes = null;
            tab.ResponseIsBinary = false;
            tab.ResponseRedirects = "";
        }
        finally
        {
            _sendCancellation?.Dispose();
            _sendCancellation = null;
            _sending = false;
            SendButton.Content = T("Envoyer");
            SendButton.Click -= Cancel_Click;
            SendButton.Click += Send_Click;
            CancelButton.Visibility = Visibility.Collapsed;
            if (Selected is { } selected)
                LoadTab(selected);
            ScheduleSave();
        }
    }

    private void ShowWarning(string message)
    {
        WarningText.Text = message;
        WarningText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _sendCancellation?.Cancel();

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var tab = new NetTestTab { Name = T("New request"), IsPlaceholder = true };
        AddTabAndSelect(tab);
        ScheduleSave();
    }

    private void DuplicateTab(NetTestTab current)
    {
        var copy = JsonUtil.Clone(current);
        copy.IsPlaceholder = false;
        copy.Name += " (copie)";
        copy.ResponseSummary = null;
        copy.ResponseHeaders = null;
        copy.ResponseBody = null;
        copy.ResponseRedirects = null;
        copy.ResponseContentType = "";
        copy.ResponseBodyBytes = null;
        copy.ResponseIsBinary = false;
        copy.ResponseError = null;
        AddTabAndSelect(copy);
        ScheduleSave();
    }

    private void AddTabAndSelect(NetTestTab tab)
    {
        _tabs.Add(tab);
        RequestTabs.SelectedItem = tab;
        LoadTab(tab);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (RequestTabs.Template.FindName("RequestTabScrollViewer", RequestTabs) is ScrollViewer scrollViewer)
            {
                scrollViewer.UpdateLayout();
                scrollViewer.ScrollToRightEnd();
                UpdateTabFade(scrollViewer);
            }
        }), DispatcherPriority.Loaded);
    }

    private void RequestTabScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
            Dispatcher.BeginInvoke(new Action(() => UpdateTabFade(scrollViewer)), DispatcherPriority.Loaded);
    }

    private void RequestTabScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            UpdateTabsScrollBar(scrollViewer);
            UpdateTabFade(scrollViewer);
        }
    }

    private void RequestTabsScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTabsScrollBar
            || RequestTabs.Template.FindName("RequestTabScrollViewer", RequestTabs) is not ScrollViewer scrollViewer)
            return;
        scrollViewer.ScrollToHorizontalOffset(e.NewValue);
    }

    private void UpdateTabsScrollBar(ScrollViewer scrollViewer)
    {
        if (RequestTabs.Template.FindName("RequestTabsScrollBar", RequestTabs) is not ScrollBar scrollBar)
            return;

        _updatingTabsScrollBar = true;
        scrollBar.Maximum = scrollViewer.ScrollableWidth;
        scrollBar.ViewportSize = scrollViewer.ViewportWidth;
        scrollBar.SmallChange = 32;
        scrollBar.LargeChange = Math.Max(32, scrollViewer.ViewportWidth * 0.8);
        scrollBar.Value = scrollViewer.HorizontalOffset;
        scrollBar.Visibility = scrollViewer.ScrollableWidth > 1
            ? Visibility.Visible : Visibility.Collapsed;
        _updatingTabsScrollBar = false;
    }

    private void UpdateTabFade(ScrollViewer scrollViewer)
    {
        if (RequestTabs.Template.FindName("LeftTabFade", RequestTabs) is not Border leftFade
            || RequestTabs.Template.FindName("RightTabFade", RequestTabs) is not Border rightFade)
            return;

        leftFade.Visibility = scrollViewer.HorizontalOffset > 1
            ? Visibility.Visible : Visibility.Collapsed;
        rightFade.Visibility = scrollViewer.ScrollableWidth - scrollViewer.HorizontalOffset > 1
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RequestTab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggedTab = null;
        if (sender is not TabItem tabItem || tabItem.DataContext is not NetTestTab tab)
            return;

        if (FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _tabDragStart = e.GetPosition(RequestTabs);
        _draggedTab = tab;
    }

    private void RequestTab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedTab is not { } tab || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point current = e.GetPosition(RequestTabs);
        if (Math.Abs(current.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _tabDragMoved = false;
        var layer = AdornerLayer.GetAdornerLayer(RequestTabs);
        if (layer is not null)
        {
            _tabDragAdornerLayer = layer;
            _tabDragAdorner = new TabDragAdorner(RequestTabs, tab.Name);
            layer.Add(_tabDragAdorner);
            _tabDragAdorner.MoveTo(current);
        }

        try
        {
            var result = DragDrop.DoDragDrop((DependencyObject)sender, tab, DragDropEffects.Move);
            if (_tabDragMoved && result == DragDropEffects.Move)
            {
                RequestTabs.SelectedItem = tab;
                ScheduleSave();
            }
        }
        finally
        {
            if (_tabDragAdorner is not null && _tabDragAdornerLayer is not null)
                _tabDragAdornerLayer.Remove(_tabDragAdorner);
            _tabDragAdorner = null;
            _tabDragAdornerLayer = null;
            _draggedTab = null;
            _tabDragMoved = false;
        }
    }

    private void RequestTab_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(NetTestTab)) || _draggedTab is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        _tabDragAdorner?.MoveTo(e.GetPosition(RequestTabs));
        if (sender is not TabItem targetItem || targetItem.DataContext is not NetTestTab target)
            return;

        int sourceIndex = _tabs.IndexOf(_draggedTab);
        int targetIndex = _tabs.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0) return;

        bool insertAfter = e.GetPosition(targetItem).X >= targetItem.ActualWidth / 2;
        int insertionIndex = targetIndex + (insertAfter ? 1 : 0);
        if (sourceIndex < insertionIndex) insertionIndex--;
        insertionIndex = Math.Clamp(insertionIndex, 0, _tabs.Count - 1);
        if (sourceIndex == insertionIndex) return;

        _tabs.Move(sourceIndex, insertionIndex);
        RequestTabs.SelectedItem = _draggedTab;
        _tabDragMoved = true;
        e.Handled = true;
    }

    private void RequestTab_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void RequestTabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu
            || menu.PlacementTarget is not TabItem tabItem
            || tabItem.DataContext is not NetTestTab tab)
            return;

        RequestTabs.SelectedItem = tab;
        menu.Items.Clear();
        var copyAs = new MenuItem
        {
            Header = LocalizationService.Translate("Copy as…", Services.Settings.Language)
        };
        copyAs.Items.Add(CreateCopyAsItem("cURL", tab, HttpFreeKit.Core.RequestCodeFormat.Curl));
        copyAs.Items.Add(CreateCopyAsItem("Batch (Windows)", tab, HttpFreeKit.Core.RequestCodeFormat.Batch));
        copyAs.Items.Add(CreateCopyAsItem("Node.js (fetch)", tab, HttpFreeKit.Core.RequestCodeFormat.NodeJs));
        copyAs.Items.Add(CreateCopyAsItem("Python (requests)", tab, HttpFreeKit.Core.RequestCodeFormat.Python));
        copyAs.Items.Add(CreateCopyAsItem("Lua (LuaSocket)", tab, HttpFreeKit.Core.RequestCodeFormat.Lua));
        copyAs.Items.Add(CreateCopyAsItem("Go (net/http)", tab, HttpFreeKit.Core.RequestCodeFormat.Go));
        copyAs.Items.Add(CreateCopyAsItem("C# (HttpClient)", tab, HttpFreeKit.Core.RequestCodeFormat.CSharp));
        copyAs.Items.Add(CreateCopyAsItem("C++ (libcurl)", tab, HttpFreeKit.Core.RequestCodeFormat.Cpp));
        menu.Items.Add(copyAs);
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateTabMenuItem("Dupliquer", () => DuplicateTab(tab)));
        menu.Items.Add(CreateTabMenuItem("Renommer", () => RenameTab(tab)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateTabMenuItem("Supprimer", () => RemoveTab(tab)));
    }

    private MenuItem CreateCopyAsItem(
        string label, NetTestTab tab, HttpFreeKit.Core.RequestCodeFormat format)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(GenerateTestRequestCode(tab, format));
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                ShowWarning(ex.Message);
            }
        };
        return item;
    }

    private static string GenerateTestRequestCode(
        NetTestTab tab, HttpFreeKit.Core.RequestCodeFormat format)
    {
        string method = tab.Method.Trim().ToUpperInvariant();
        var headers = tab.Headers
            .Where(header => header.Enabled && !string.IsNullOrWhiteSpace(header.Name))
            .Select(header => $"{header.Name.Trim()}: {header.Value}");
        string headersText = string.Join("\r\n", headers);
        string contentType = tab.Headers.FirstOrDefault(header => header.Enabled
            && header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value
            ?? tab.ContentType;
        bool hasBody = tab.BodyText.Length > 0 || tab.BodyBase64 is not null;
        byte[] body = tab.GetBodyBytes(contentType);

        var entry = new TrafficEntry
        {
            Method = method,
            Url = tab.Url,
            RequestHeaders = headersText,
            RequestContentType = hasBody ? contentType : "",
            RequestReplayBody = body,
            RequestBodyIncomplete = tab.OriginalBodyIncomplete
        };
        return RequestCodeGenerator.Generate(entry, format);
    }

    private static string GenerateRequestCode(NetTestTab tab, RequestCodeFormat format)
    {
        string method = tab.Method.Trim().ToUpperInvariant();
        string url = tab.Url;
        var headers = tab.Headers
            .Where(header => header.Enabled && !string.IsNullOrWhiteSpace(header.Name))
            .Where(header => !header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                             && !header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                             && !header.Name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                             && !header.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                             && !header.Name.Equals("Expect", StringComparison.OrdinalIgnoreCase)
                             && !header.Name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
            .Select(header => (Name: header.Name.Trim(), Value: header.Value))
            .ToList();
        string contentType = headers.FirstOrDefault(header =>
            header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value ?? tab.ContentType;
        bool sendBody = tab.BodyText.Length > 0 || tab.BodyBase64 is not null;
        byte[] body = tab.GetBodyBytes(contentType);

        if (sendBody && !string.IsNullOrWhiteSpace(tab.ContentType)
            && headers.All(header => !header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            headers.Add(("Content-Type", tab.ContentType));

        string bodyBase64 = Convert.ToBase64String(body);
        return format switch
        {
            RequestCodeFormat.Curl => GenerateCurl(method, url, headers, bodyBase64, sendBody),
            RequestCodeFormat.JavaScript => GenerateJavaScript(method, url, headers, body, sendBody),
            RequestCodeFormat.Python => GeneratePython(method, url, headers, bodyBase64, sendBody),
            RequestCodeFormat.CSharp => GenerateCSharp(method, url, headers, bodyBase64, sendBody),
            RequestCodeFormat.Cpp => GenerateCpp(method, url, headers, body, sendBody),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private static string GenerateCurl(string method, string url,
        IReadOnlyList<(string Name, string Value)> headers, string bodyBase64, bool sendBody)
    {
        static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        var command = new StringBuilder();
        if (sendBody)
            command.Append("printf '%s' ").Append(Quote(bodyBase64)).Append(" | base64 --decode | \\\n");
        command.Append("curl --request ").Append(Quote(method))
            .Append(" --url ").Append(Quote(url));
        foreach (var header in headers)
            command.Append(" \\\n  --header ").Append(Quote($"{header.Name}: {header.Value}"));
        if (sendBody)
            command.Append(" \\\n  --data-binary @-");
        return command.ToString();
    }

    private static string GenerateJavaScript(string method, string url,
        IReadOnlyList<(string Name, string Value)> headers, byte[] body, bool sendBody)
    {
        var headerRows = string.Join(",\n  ", headers.Select(header =>
            $"[{System.Text.Json.JsonSerializer.Serialize(header.Name)}, {System.Text.Json.JsonSerializer.Serialize(header.Value)}]"));
        bool bodyAllowed = method is not ("GET" or "HEAD");
        var code = new StringBuilder("const response = await fetch(")
            .Append(System.Text.Json.JsonSerializer.Serialize(url))
            .Append(", {\n  method: ").Append(System.Text.Json.JsonSerializer.Serialize(method));
        if (headers.Count > 0)
            code.Append(",\n  headers: [\n  ").Append(headerRows).Append("\n  ]");
        if (sendBody && bodyAllowed)
            code.Append(",\n  body: Uint8Array.from([")
                .Append(string.Join(", ", body.Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                .Append("])");
        code.Append("\n});\n\nconsole.log(response.status, await response.text());");
        if (sendBody && !bodyAllowed)
            code.Append("\n// Fetch does not permit a request body with ").Append(method).Append('.');
        return code.ToString();
    }

    private static string GeneratePython(string method, string url,
        IReadOnlyList<(string Name, string Value)> headers, string bodyBase64, bool sendBody)
    {
        static string Literal(string value) => "'" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + "'";

        var code = new StringBuilder("import base64\nimport requests\n\n")
            .Append("url = ").Append(Literal(url)).Append("\n")
            .Append("headers = [\n");
        foreach (var header in headers)
            code.Append("    (").Append(Literal(header.Name)).Append(", ")
                .Append(Literal(header.Value)).Append("),\n");
        code.Append("]\n");
        if (sendBody)
            code.Append("body = base64.b64decode(").Append(Literal(bodyBase64)).Append(")\n");
        code.Append("\nresponse = requests.request(\n")
            .Append("    ").Append(Literal(method)).Append(", url,\n")
            .Append("    headers=headers");
        if (sendBody)
            code.Append(",\n    data=body");
        code.Append("\n)\nprint(response.status_code, response.text)");
        return code.ToString();
    }

    private static string GenerateCSharp(string method, string url,
        IReadOnlyList<(string Name, string Value)> headers, string bodyBase64, bool sendBody)
    {
        var code = new StringBuilder("using System;\nusing System.Net.Http;\n\n")
            .Append("using var client = new HttpClient();\n")
            .Append("using var request = new HttpRequestMessage(new HttpMethod(")
            .Append(System.Text.Json.JsonSerializer.Serialize(method)).Append("), ")
            .Append(System.Text.Json.JsonSerializer.Serialize(url)).Append(");\n");
        bool needsContent = sendBody || headers.Any(header =>
            header.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase));
        if (needsContent)
            code.Append("request.Content = new ByteArrayContent(Convert.FromBase64String(")
                .Append(System.Text.Json.JsonSerializer.Serialize(sendBody ? bodyBase64 : "")).Append("));\n");
        foreach (var header in headers)
        {
            string collection = header.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
                ? "request.Content.Headers"
                : "request.Headers";
            code.Append(collection).Append(".TryAddWithoutValidation(")
                .Append(System.Text.Json.JsonSerializer.Serialize(header.Name)).Append(", ")
                .Append(System.Text.Json.JsonSerializer.Serialize(header.Value)).Append(");\n");
        }
        code.Append("\nusing var response = await client.SendAsync(request);\n")
            .Append("Console.WriteLine($\"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}\");");
        return code.ToString();
    }

    private static string GenerateCpp(string method, string url,
        IReadOnlyList<(string Name, string Value)> headers, byte[] body, bool sendBody)
    {
        var code = new StringBuilder("#include <curl/curl.h>\n#include <cstdint>\n#include <string>\n#include <vector>\n\n")
            .Append("int main() {\n")
            .Append("    CURL* curl = curl_easy_init();\n")
            .Append("    if (!curl) return 1;\n")
            .Append("    curl_easy_setopt(curl, CURLOPT_URL, ")
            .Append(CppString(url)).Append(");\n")
            .Append("    curl_easy_setopt(curl, CURLOPT_CUSTOMREQUEST, ")
            .Append(CppString(method)).Append(");\n")
            .Append("    curl_slist* headers = nullptr;\n");
        if (headers.Count > 0)
        {
            foreach (var header in headers)
                code.Append("    headers = curl_slist_append(headers, ")
                    .Append(CppString($"{header.Name}: {header.Value}")).Append(");\n");
            code.Append("    curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);\n");
        }
        if (sendBody)
        {
            code.Append("    const std::vector<std::uint8_t> body = {")
                .Append(string.Join(", ", body.Select(value => "0x" + value.ToString("X2"))))
                .Append("};\n")
                .Append("    curl_easy_setopt(curl, CURLOPT_POSTFIELDS, body.data());\n")
                .Append("    curl_easy_setopt(curl, CURLOPT_POSTFIELDSIZE_LARGE, static_cast<curl_off_t>(body.size()));\n");
        }
        code.Append("    CURLcode result = curl_easy_perform(curl);\n")
            .Append("    if (headers) curl_slist_free_all(headers);\n")
            .Append("    curl_easy_cleanup(curl);\n")
            .Append("    return result == CURLE_OK ? 0 : 1;\n}");
        return code.ToString();
    }

    private static string CppString(string value)
    {
        var literal = new StringBuilder("\"");
        foreach (char character in value)
        {
            literal.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(character) => "\\x" + ((int)character).ToString("X2") + "\"\"",
                _ => character.ToString()
            });
        }
        return literal.Append('"').ToString();
    }

    private enum RequestCodeFormat
    {
        Curl,
        JavaScript,
        Python,
        CSharp,
        Cpp
    }

    private MenuItem CreateTabMenuItem(string label, Action action)
    {
        var item = new MenuItem { Header = T(label) };
        item.Click += (_, _) => action();
        return item;
    }

    private void RenameTab(NetTestTab tab)
    {
        var dialog = new Window
        {
            Title = T("Renommer"),
            Owner = Window.GetWindow(this),
            Width = 380,
            Height = 150,
            MinWidth = 340,
            MinHeight = 140,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("SurfaceBrush"),
            Foreground = (Brush)FindResource("AppForegroundBrush")
        };
        var nameBox = new TextBox
        {
            Text = tab.Name,
            Margin = new Thickness(0, 0, 0, 14),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var confirmButton = new Button
        {
            Content = T("Renommer"),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true,
            Style = TryFindResource("AccentButtonStyle") as Style
        };
        var cancelButton = new Button { Content = T("Annuler"), MinWidth = 90, IsCancel = true };
        confirmButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) return;
            dialog.DialogResult = true;
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(confirmButton);
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(nameBox);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) =>
        {
            nameBox.SelectAll();
            nameBox.Focus();
        };

        if (dialog.ShowDialog() != true) return;
        tab.Name = nameBox.Text.Trim();
        tab.IsPlaceholder = false;
        ScheduleSave();
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NetTestTab tab) return;
        RemoveTab(tab);
    }

    private void RemoveTab(NetTestTab tab)
    {
        bool wasSelected = ReferenceEquals(Selected, tab);
        int index = _tabs.IndexOf(tab);
        if (index < 0) return;
        if (wasSelected && _sending)
            _sendCancellation?.Cancel();
        _tabs.Remove(tab);
        if (_tabs.Count == 0)
            _tabs.Add(new NetTestTab { Name = T("New request"), IsPlaceholder = true });
        if (wasSelected)
            RequestTabs.SelectedIndex = Math.Clamp(index - 1, 0, _tabs.Count - 1);
        ScheduleSave();
    }

    private void Url_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SendAsync();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            e.Handled = true;
            SaveTabs();
        }
        else if (e.Key == Key.Escape && _sending)
        {
            e.Handled = true;
            _sendCancellation?.Cancel();
        }
    }

    private void SaveResponse_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { ResponseBody: { } body } tab) return;
        var dialog = new SaveFileDialog { FileName = "response.txt", Filter = T("Tous les fichiers (*.*)|*.*") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            if (tab.ResponseFilePath is { } responsePath && File.Exists(responsePath))
                File.Copy(responsePath, dialog.FileName, true);
            else
                File.WriteAllBytes(dialog.FileName, Encoding.UTF8.GetBytes(body));
        }
        catch (Exception ex) { HttpFreeKit.AppDialog.Show(Window.GetWindow(this), ex.Message, "HTTP FreeKit", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTabs()
    {
        _saveTimer.Stop();
        try { Services.NetTests.SaveTabs(_tabs); }
        catch (Exception ex) { HttpFreeKit.AppDialog.Show(Window.GetWindow(this), ex.Message, "HTTP FreeKit", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}

internal sealed class TabDragAdorner : Adorner
{
    private readonly string _tabName;
    private Point _position;
    private const double AdornerWidth = 240;
    private const double AdornerHeight = 34;

    public TabDragAdorner(UIElement adornedElement, string name) : base(adornedElement)
    {
        _tabName = name;
        IsHitTestVisible = false;
        Opacity = 0.92;
    }

    public void MoveTo(Point position)
    {
        _position = position;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var rect = new Rect(
            Math.Clamp(_position.X + 12, 0, Math.Max(0, AdornedElement.RenderSize.Width - AdornerWidth)),
            Math.Clamp(_position.Y + 10, 0, Math.Max(0, AdornedElement.RenderSize.Height - AdornerHeight)),
            Math.Min(AdornerWidth, AdornedElement.RenderSize.Width),
            AdornerHeight);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x36)),
            new Pen(new SolidColorBrush(Color.FromRgb(0xE1, 0x42, 0x1F)), 1.5),
            rect, 6, 6);

        var formatted = new FormattedText(
            _tabName,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF0)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = rect.Width - 24,
            Trimming = TextTrimming.CharacterEllipsis
        };
        drawingContext.DrawText(formatted, new Point(rect.X + 12, rect.Y + 9));
    }
}
