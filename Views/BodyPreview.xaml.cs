using System.Text.RegularExpressions;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Views;

public partial class BodyPreview : UserControl
{
    private const int MaxTextPreviewCharacters = 250_000;
    private const int MaxPreviewLines = 10_000;
    private const int MaxBinaryPreviewBytes = 8_192;
    private const double MaxDocumentPageWidth = 16_384;
    public static readonly DependencyProperty ContentTextProperty =
        DependencyProperty.Register(nameof(ContentText), typeof(string), typeof(BodyPreview),
            new PropertyMetadata("", OnContentChanged));

    public static readonly DependencyProperty ContentTypeProperty =
        DependencyProperty.Register(nameof(ContentType), typeof(string), typeof(BodyPreview),
            new PropertyMetadata("", OnContentChanged));

    public static readonly DependencyProperty BinaryBytesProperty =
        DependencyProperty.Register(nameof(BinaryBytes), typeof(byte[]), typeof(BodyPreview),
            new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty IsBinaryProperty =
        DependencyProperty.Register(nameof(IsBinary), typeof(bool), typeof(BodyPreview),
            new PropertyMetadata(false, OnContentChanged));

    public static readonly DependencyProperty PreviewUnavailableProperty =
        DependencyProperty.Register(nameof(PreviewUnavailable), typeof(bool), typeof(BodyPreview),
            new PropertyMetadata(false, OnContentChanged));

    public static readonly DependencyProperty BinaryLengthProperty =
        DependencyProperty.Register(nameof(BinaryLength), typeof(long?), typeof(BodyPreview),
            new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty ShowBodyLabelProperty =
        DependencyProperty.Register(nameof(ShowBodyLabel), typeof(bool), typeof(BodyPreview),
            new PropertyMetadata(true, OnContentChanged));

    public static readonly DependencyProperty LanguageProperty =
        DependencyProperty.Register(nameof(Language), typeof(string), typeof(BodyPreview),
            new PropertyMetadata("en", OnContentChanged));

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(BodyPreview),
            new PropertyMetadata(false, OnIsLoadingChanged));

    private static readonly Regex JsonToken = new(
        "\"(?:\\\\.|[^\"\\\\])*\"|\\b(?:true|false|null)\\b|-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?|[{}\\[\\],:]",
        RegexOptions.Compiled);

    private static readonly Regex MarkupToken = new(
        "<!--[\\s\\S]*?-->|<![^>]*>|</?[A-Za-z][\\w:.-]*|[A-Za-z_:][\\w:.-]*(?=\\s*=)|\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|/?>|=",
        RegexOptions.Compiled);
    private static readonly Regex MarkupTag = new(
        "<(?:!--[\\s\\S]*?--|![^>]*|/?[A-Za-z][^\"'<>]*(?:\"[^\"]*\"[^\"'<>]*|'[^']*'[^\"'<>]*)*)>",
        RegexOptions.Compiled);
    private bool _deferRender;
    private bool _insideRawMarkup;
    private int _longestLineLength;

    public string ContentText { get => (string)GetValue(ContentTextProperty); set => SetValue(ContentTextProperty, value); }
    public string ContentType { get => (string)GetValue(ContentTypeProperty); set => SetValue(ContentTypeProperty, value); }
    public byte[]? BinaryBytes { get => (byte[]?)GetValue(BinaryBytesProperty); set => SetValue(BinaryBytesProperty, value); }
    public bool IsBinary { get => (bool)GetValue(IsBinaryProperty); set => SetValue(IsBinaryProperty, value); }
    public bool PreviewUnavailable { get => (bool)GetValue(PreviewUnavailableProperty); set => SetValue(PreviewUnavailableProperty, value); }
    public long? BinaryLength { get => (long?)GetValue(BinaryLengthProperty); set => SetValue(BinaryLengthProperty, value); }
    public bool ShowBodyLabel { get => (bool)GetValue(ShowBodyLabelProperty); set => SetValue(ShowBodyLabelProperty, value); }
    public string Language { get => (string)GetValue(LanguageProperty); set => SetValue(LanguageProperty, value); }
    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }

    public BodyPreview()
    {
        InitializeComponent();
        PreviewBox.SizeChanged += (_, _) => UpdateDocumentPageWidth();
        RenderContent();
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BodyPreview preview && preview.IsInitialized && !preview._deferRender)
        {
            preview.UpdateLoadingText();
            preview.BodyTitle.Visibility = preview.ShowBodyLabel ? Visibility.Visible : Visibility.Collapsed;
            preview.RenderContent();
        }
    }

    private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BodyPreview preview && preview.IsInitialized)
        {
            bool isLoading = (bool)e.NewValue;
            preview.LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            preview.SetLoadingAnimation(isLoading);
            preview.UpdateLoadingText();
        }
    }

    private void SetLoadingAnimation(bool isLoading)
    {
        if (LoadingIndicatorTransform is null)
            return;

        if (isLoading)
        {
            var animation = new DoubleAnimation(-45, 150, TimeSpan.FromMilliseconds(1100))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            LoadingIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, animation);
        }
        else
        {
            LoadingIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }

    public void RefreshLanguage() => RenderContent();
    public void RefreshTheme() => RenderContent();

    private void UpdateLoadingText()
    {
        if (LoadingText is not null)
            LoadingText.Text = LocalizationService.Translate("Loading body…", Language);
    }

    public void SetBinaryContent(byte[]? bytes, long? length, bool unavailable, string language)
    {
        _deferRender = true;
        try
        {
            SetValue(ShowBodyLabelProperty, false);
            SetValue(LanguageProperty, language);
            SetValue(IsBinaryProperty, true);
            SetValue(PreviewUnavailableProperty, unavailable);
            SetValue(BinaryLengthProperty, length);
            SetValue(BinaryBytesProperty, bytes);
        }
        finally
        {
            _deferRender = false;
        }
        BodyTitle.Visibility = Visibility.Collapsed;
        RenderContent();
    }

    public void SetContent(string? text, string? contentType, bool isBinary,
        bool previewUnavailable, byte[]? bytes, long? binaryLength)
    {
        _deferRender = true;
        try
        {
            SetValue(ContentTextProperty, text ?? "");
            SetValue(ContentTypeProperty, contentType ?? "");
            SetValue(IsBinaryProperty, isBinary);
            SetValue(PreviewUnavailableProperty, previewUnavailable);
            SetValue(BinaryBytesProperty, bytes);
            SetValue(BinaryLengthProperty, binaryLength);
        }
        finally
        {
            _deferRender = false;
        }

        BodyTitle.Visibility = ShowBodyLabel ? Visibility.Visible : Visibility.Collapsed;
        RenderContent();
    }

    private bool IsLightTheme => App.IsLightTheme;

    private void RenderContent()
    {
        if (PreviewBox == null) return;

        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };
        _longestLineLength = 0;
        _insideRawMarkup = false;
        bool binary = IsBinary || BinaryBytes is { Length: > 0 };
        BinaryBanner.Visibility = ShowBodyLabel && (binary || PreviewUnavailable)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (binary)
        {
            long? length = BinaryLength ?? BinaryBytes?.Length;
            BinaryLabel.Text = length.HasValue
                ? BinaryBytes is { Length: > 0 }
                    ? LocalizationService.Format("BINARY CONTENT · {0} bytes", Language, length.Value)
                    : LocalizationService.Format("BINARY CONTENT · {0} bytes · preview unavailable", Language, length.Value)
                : LocalizationService.Translate("BINARY CONTENT · size unknown · preview unavailable", Language);
            if (BinaryBytes is { Length: > 0 } bytes)
            {
                var preview = bytes.Length > MaxBinaryPreviewBytes
                    ? bytes.AsSpan(0, MaxBinaryPreviewBytes).ToArray()
                    : bytes;
                AddHexDump(document, preview, length ?? bytes.Length);
            }
        }
        else if (PreviewUnavailable)
        {
            BinaryLabel.Text = BinaryLength.HasValue
                ? LocalizationService.Format("TEXT CONTENT · {0} bytes · preview unavailable", Language, BinaryLength.Value)
                : LocalizationService.Translate("TEXT CONTENT · size unknown · preview unavailable", Language);
        }
        else
        {
            var sourceText = ContentText ?? "";
            bool truncated = sourceText.Length > MaxTextPreviewCharacters;
            var text = Fmt.FormatBody(ContentType, sourceText);
            if (text.Length > MaxTextPreviewCharacters)
                text = text[..MaxTextPreviewCharacters];
            BinaryBanner.Visibility = truncated ? Visibility.Visible : Visibility.Collapsed;
            BinaryLabel.Text = truncated
                ? LocalizationService.Format("Aperçu limité aux {0:N0} premiers caractères", Language,
                    MaxTextPreviewCharacters)
                : "";
            if (text.Length != 0)
            {
                var type = DetectType(ContentType, text);
                AddHighlightedText(document, text, type);
            }
        }

        if (document.Blocks.Count == 0)
            document.Blocks.Add(new Paragraph());
        PreviewBox.Document = document;
        UpdateDocumentPageWidth();
    }

    private void UpdateDocumentPageWidth()
    {
        if (PreviewBox?.Document is not { } document)
            return;

        double pixelsPerDip = VisualTreeHelper.GetDpi(PreviewBox).PixelsPerDip;
        var typeface = new Typeface(
            PreviewBox.FontFamily, PreviewBox.FontStyle, PreviewBox.FontWeight, PreviewBox.FontStretch);
        double characterWidth = Math.Ceiling(new FormattedText(
            "M", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, PreviewBox.FontSize, Brushes.Transparent, new NumberSubstitution(),
            TextFormattingMode.Display, pixelsPerDip)
            .WidthIncludingTrailingWhitespace);


        double viewportWidth = Math.Max(1, PreviewBox.ActualWidth - PreviewBox.Padding.Left - PreviewBox.Padding.Right - 14);
        double contentWidth = (_longestLineLength + 2) * characterWidth;
        document.PageWidth = Math.Min(MaxDocumentPageWidth, Math.Max(viewportWidth, contentWidth));
    }

    private static BodyType DetectType(string? contentType, string text)
    {
        return Fmt.DetectBodyFormat(contentType, text) switch
        {
            Fmt.BodyFormat.Json => BodyType.Json,
            Fmt.BodyFormat.Xml or Fmt.BodyFormat.Html => BodyType.Markup,
            _ => BodyType.PlainText
        };
    }

    private void AddHighlightedText(FlowDocument document, string text, BodyType type)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        int displayedLines = Math.Min(lines.Length, MaxPreviewLines);
        for (int index = 0; index < displayedLines; index++)
        {
            var line = lines[index];
            _longestLineLength = Math.Max(_longestLineLength, line.Length);
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 16 };
            if (type == BodyType.Json)
                AddTokens(paragraph, line, JsonToken, JsonColor);
            else if (type == BodyType.Markup)
                AddMarkupText(paragraph, line);
            else
                paragraph.Inlines.Add(new Run(line) { Foreground = TextBrush() });
            document.Blocks.Add(paragraph);
        }
        if (lines.Length > displayedLines)
        {
            _longestLineLength = Math.Max(_longestLineLength, 60);
            document.Blocks.Add(new Paragraph(new Run(LocalizationService.Format(
                "Aperçu limité aux {0:N0} premières lignes", Language, displayedLines))
            { Foreground = Brush(IsLightTheme ? "#6A737D" : "#858B98") })
            { Margin = new Thickness(0, 4, 0, 0), LineHeight = 16 });
        }
    }

    private void AddMarkupText(Paragraph paragraph, string line)
    {
        int position = 0;
        while (position < line.Length)
        {
            if (_insideRawMarkup)
            {
                int closingTag = line.IndexOf("</script", position, StringComparison.OrdinalIgnoreCase);
                int closingStyleTag = line.IndexOf("</style", position, StringComparison.OrdinalIgnoreCase);
                int closing = closingTag < 0 ? closingStyleTag
                    : closingStyleTag < 0 ? closingTag
                    : Math.Min(closingTag, closingStyleTag);
                if (closing < 0)
                {
                    paragraph.Inlines.Add(new Run(line[position..]) { Foreground = TextBrush() });
                    return;
                }

                if (closing > position)
                    paragraph.Inlines.Add(new Run(line[position..closing]) { Foreground = TextBrush() });
                position = closing;
                _insideRawMarkup = false;
            }

            var match = MarkupTag.Match(line, position);
            if (!match.Success)
            {
                paragraph.Inlines.Add(new Run(line[position..]) { Foreground = TextBrush() });
                return;
            }

            if (match.Index > position)
                paragraph.Inlines.Add(new Run(line[position..match.Index]) { Foreground = TextBrush() });
            AddTokens(paragraph, match.Value, MarkupToken, MarkupColor);
            position = match.Index + match.Length;

            string tag = match.Value;
            if (tag.StartsWith('<') && !tag.StartsWith("</", StringComparison.Ordinal)
                && !tag.EndsWith("/>", StringComparison.Ordinal))
            {
                int nameEnd = 1;
                while (nameEnd < tag.Length && (char.IsLetterOrDigit(tag[nameEnd])
                    || tag[nameEnd] is ':' or '-' or '_'))
                    nameEnd++;

                string tagName = tag[1..nameEnd];
                _insideRawMarkup = tagName.Equals("script", StringComparison.OrdinalIgnoreCase)
                    || tagName.Equals("style", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private void AddTokens(Paragraph paragraph, string line, Regex tokenizer, Func<string, Brush> color)
    {
        int position = 0;
        foreach (Match match in tokenizer.Matches(line))
        {
            if (match.Index > position)
                paragraph.Inlines.Add(new Run(line[position..match.Index]) { Foreground = TextBrush() });
            paragraph.Inlines.Add(new Run(match.Value) { Foreground = color(match.Value) });
            position = match.Index + match.Length;
        }
        if (position < line.Length)
            paragraph.Inlines.Add(new Run(line[position..]) { Foreground = TextBrush() });
    }

    private Brush JsonColor(string token)
    {
        if (token.StartsWith('"'))
            return Brush(Regex.IsMatch(token + ":", "^\"(?:\\\\.|[^\"\\\\])*\"\\s*:$")
                ? (IsLightTheme ? "#0451A5" : "#9CDCFE")
                : (IsLightTheme ? "#A31515" : "#CE9178"));
        if (token is "true" or "false" or "null") return Brush(IsLightTheme ? "#AF00DB" : "#C586C0");
        if (char.IsDigit(token[0]) || token[0] == '-') return Brush(IsLightTheme ? "#098658" : "#B5CEA8");
        return TextBrush();
    }

    private Brush MarkupColor(string token)
    {
        if (token.StartsWith("<!--", StringComparison.Ordinal)) return Brush(IsLightTheme ? "#008000" : "#6A9955");
        if (token.StartsWith("<!", StringComparison.Ordinal)) return Brush(IsLightTheme ? "#800000" : "#569CD6");
        if (token.StartsWith("</", StringComparison.Ordinal) || token.StartsWith('<'))
            return Brush(IsLightTheme ? "#800000" : "#569CD6");
        if (token.StartsWith('"') || token.StartsWith('\'')) return Brush(IsLightTheme ? "#A31515" : "#CE9178");
        if (token == "=") return TextBrush();
        if (token is ">" or "/>") return Brush(IsLightTheme ? "#800000" : "#569CD6");
        return Brush(IsLightTheme ? "#FF0000" : "#9CDCFE");
    }

    private void AddHexDump(FlowDocument document, byte[] bytes, long totalLength)
    {
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 16 };
            paragraph.Inlines.Add(new Run(offset.ToString("X8") + "  ") { Foreground = Brush(IsLightTheme ? "#6A737D" : "#858B98") });

            int count = Math.Min(16, bytes.Length - offset);

            _longestLineLength = Math.Max(_longestLineLength, 8 + 2 + 16 * 3 + 1 + 2 + count);
            for (int index = 0; index < 16; index++)
            {
                if (index < count)
                    paragraph.Inlines.Add(new Run(bytes[offset + index].ToString("X2") + " ")
                    { Foreground = Brush(IsLightTheme ? "#0451A5" : "#9CDCFE") });
                else
                    paragraph.Inlines.Add(new Run("   "));
                if (index == 7) paragraph.Inlines.Add(new Run(" "));
            }

            paragraph.Inlines.Add(new Run("  ") { Foreground = Brush(IsLightTheme ? "#A0A6B0" : "#505663") });
            for (int index = 0; index < count; index++)
            {
                byte value = bytes[offset + index];
                string glyph = value is >= 32 and < 127 ? ((char)value).ToString() : ".";
                paragraph.Inlines.Add(new Run(glyph)
                { Foreground = value is >= 32 and < 127 ? TextBrush() : Brush(IsLightTheme ? "#6A737D" : "#777E8C") });
            }
            document.Blocks.Add(paragraph);
        }

        if (totalLength > bytes.Length)
        {
            _longestLineLength = Math.Max(_longestLineLength, 60);
            var summary = new Paragraph { Margin = new Thickness(0, 4, 0, 0), LineHeight = 16 };
            summary.Inlines.Add(new Run(LocalizationService.Format(
                "Preview limited to {0} of {1} bytes", Language, bytes.Length, totalLength))
            { Foreground = Brush(IsLightTheme ? "#6A737D" : "#858B98") });
            document.Blocks.Add(summary);
        }
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private Brush TextBrush() => Brush(IsLightTheme ? "#20232A" : "#D7DAE0");

    private enum BodyType { PlainText, Json, Markup }
}