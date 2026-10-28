using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Views;

public sealed class BreakpointHeader
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed record BreakpointEdit(string Headers, string Body, int? StatusCode, bool BodyChanged);

public partial class BreakpointEditorWindow : Window
{
    private readonly bool _response;
    private readonly bool _bodyEditable;
    private readonly string _displayBody;
    private readonly ObservableCollection<BreakpointHeader> _headers;

    private BreakpointEditorWindow(Window? owner, string language, string url, bool response,
        string headers, string body, int? statusCode, bool bodyEditable)
    {
        InitializeComponent();
        Owner = owner;
        _response = response;
        _bodyEditable = bodyEditable;
        TitleText.Text = LocalizationService.Format("BREAKPOINT · {0}", language,
            LocalizationService.Translate(response ? "Response" : "Request", language));
        Title = LocalizationService.Translate("Breakpoint", language);
        LocalizationService.Apply(this, language);
        Loaded += (_, _) => LocalizationService.Apply(this, language);
        UrlText.Text = url;

        _headers = new ObservableCollection<BreakpointHeader>(ParseHeaders(headers));
        HeadersGrid.ItemsSource = _headers;
        string? contentType = _headers.FirstOrDefault(header =>
            header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        _displayBody = bodyEditable ? Fmt.FormatBody(contentType, body) : body;
        BodyBox.Text = _displayBody;
        if (response)
            BodyBox.Background = System.Windows.Media.Brushes.Black;
        BodyBox.IsEnabled = bodyEditable;
        UpdateJsonEditorMode();
        BodyNote.Visibility = bodyEditable ? Visibility.Collapsed : Visibility.Visible;
        ResponseStatusPanel.Visibility = response ? Visibility.Visible : Visibility.Collapsed;
        StatusBox.Text = (statusCode ?? 200).ToString();
    }

    public static BreakpointEdit? Show(Window? owner, string language, string url, bool response,
        string headers, string body, int? statusCode, bool bodyEditable)
    {
        var window = new BreakpointEditorWindow(owner, language, url, response, headers, body, statusCode, bodyEditable);
        if (window.ShowDialog() != true) return null;

        window.HeadersGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        window.HeadersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        return new BreakpointEdit(
            SerializeHeaders(window._headers),
            window.BodyBox.Text,
            window._response ? int.Parse(window.StatusBox.Text) : null,
            window._bodyEditable && !string.Equals(window.BodyBox.Text, window._displayBody, StringComparison.Ordinal));
    }

    private void UpdateJsonEditorMode()
    {
        string? contentType = _headers.FirstOrDefault(header =>
            header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        BodyBox.IsJson = HttpFreeKit.Core.Fmt.DetectBodyFormat(contentType, BodyBox.Text)
            == HttpFreeKit.Core.Fmt.BodyFormat.Json;
        BodyBox.IsMarkup = HttpFreeKit.Core.Fmt.DetectBodyFormat(contentType, BodyBox.Text)
            is HttpFreeKit.Core.Fmt.BodyFormat.Xml or HttpFreeKit.Core.Fmt.BodyFormat.Html;
    }

    private static IEnumerable<BreakpointHeader> ParseHeaders(string text)
    {
        foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            yield return new BreakpointHeader
            {
                Name = line[..colon].Trim(),
                Value = line[(colon + 1)..].Trim()
            };
        }
    }

    private static string SerializeHeaders(IEnumerable<BreakpointHeader> headers)
        => string.Join("\r\n", headers.Select(header => $"{header.Name}: {header.Value}"));

    private void AddHeader_Click(object sender, RoutedEventArgs e)
    {
        var header = new BreakpointHeader();
        _headers.Add(header);
        HeadersGrid.SelectedItem = header;
        HeadersGrid.ScrollIntoView(header);
        HeadersGrid.BeginEdit();
    }

    private void RemoveHeader_Click(object sender, RoutedEventArgs e)
    {
        if (HeadersGrid.SelectedItem is BreakpointHeader header)
            _headers.Remove(header);
    }

    private void HeadersGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(UpdateJsonEditorMode, DispatcherPriority.Background);
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        HeadersGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        HeadersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        UpdateJsonEditorMode();
        if (_response && (!int.TryParse(StatusBox.Text, out int status) || status is < 100 or > 599))
        {
            HttpFreeKit.AppDialog.Show(this,
                LocalizationService.Translate("Enter a valid HTTP status code (100–599).",
                    HttpFreeKit.Core.Services.Settings.Language),
                LocalizationService.Translate("Invalid status", HttpFreeKit.Core.Services.Settings.Language),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
