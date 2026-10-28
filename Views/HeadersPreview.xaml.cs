using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Views;

public partial class HeadersPreview : UserControl
{
    public static readonly DependencyProperty HeadersTextProperty =
        DependencyProperty.Register(nameof(HeadersText), typeof(string), typeof(HeadersPreview),
            new PropertyMetadata("", OnHeadersChanged));

    public static readonly DependencyProperty LanguageProperty =
        DependencyProperty.Register(nameof(Language), typeof(string), typeof(HeadersPreview),
            new PropertyMetadata("en", OnLanguageChanged));

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(HeadersPreview),
            new PropertyMetadata(false, OnIsLoadingChanged));

    public ObservableCollection<HeaderLine> HeaderLines { get; } = new();

    public string HeadersText
    {
        get => (string)GetValue(HeadersTextProperty);
        set => SetValue(HeadersTextProperty, value);
    }

    public string Language
    {
        get => (string)GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public HeadersPreview()
    {
        InitializeComponent();
        DataContext = this;
        UpdateLanguage();
    }

    private static void OnHeadersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HeadersPreview preview)
            preview.ParseHeaders(e.NewValue as string ?? "");
    }

    private static void OnLanguageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HeadersPreview preview)
            preview.UpdateLanguage();
    }

    private void UpdateLanguage()
    {
        if (!IsInitialized)
            return;

        HeaderNameText.Text = LocalizationService.Translate("Header name", Language);
        HeaderValueText.Text = LocalizationService.Translate("Value", Language);
        EmptyText.Text = LocalizationService.Translate("No headers captured", Language);
        LoadingText.Text = LocalizationService.Translate("Loading headers…", Language);
    }

    private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HeadersPreview preview && preview.IsInitialized)
        {
            bool isLoading = (bool)e.NewValue;
            preview.StatusOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            preview.SetLoadingAnimation(isLoading);
            preview.UpdateEmptyState();
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

    private void ParseHeaders(string text)
    {
        HeaderLines.Clear();
        StartLineText.Text = "";
        StartLineBorder.Visibility = Visibility.Collapsed;

        foreach (string rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n').Split('\n'))
        {
            if (rawLine.Length == 0)
                continue;

            if (char.IsWhiteSpace(rawLine[0]) && HeaderLines.Count > 0)
            {
                var previous = HeaderLines[^1];
                previous.Value += "\n" + rawLine.Trim();
                continue;
            }

            int separator = rawLine[0] == ':'
                ? rawLine.IndexOf(':', 1)
                : rawLine.IndexOf(':');

            if (separator > 0)
            {
                HeaderLines.Add(new HeaderLine(
                    rawLine[..separator].Trim(),
                    rawLine[(separator + 1)..].Trim()));
                continue;
            }

            if (HeaderLines.Count == 0 && string.IsNullOrEmpty(StartLineText.Text))
            {
                StartLineText.Text = rawLine;
                StartLineBorder.Visibility = Visibility.Visible;
            }
            else
            {
                HeaderLines.Add(new HeaderLine(rawLine, ""));
            }
        }

        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = !IsLoading && HeaderLines.Count == 0 && StartLineText.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public sealed class HeaderLine(string name, string value)
    {
        public string Name { get; } = name;
        public string Value { get; set; } = value;
    }
}
