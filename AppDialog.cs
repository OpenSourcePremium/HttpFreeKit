using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;

namespace HttpFreeKit;

public static class AppDialog
{
    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None)
        => Show(null, message, title, buttons, image);

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new DialogWindow(owner, message, title, buttons, image);
        dialog.ShowDialog();
        return dialog.Result;
    }

    private sealed class DialogWindow : Window
    {
        public MessageBoxResult Result { get; private set; }

        public DialogWindow(Window? owner, string message, string title,
            MessageBoxButton buttons, MessageBoxImage image)
        {
            string language = GetLanguage();
            Owner = owner;
            Title = LocalizationService.Translate(title, language);
            Width = 520;
            MinWidth = 420;
            MaxWidth = 720;
            MinHeight = 170;
            MaxHeight = 520;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = owner is null;
            Background = Brushes.Transparent;
            Foreground = Brushes.White;

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 42,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                UseAeroCaptionButtons = false
            });

            var shell = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0)
            };
            shell.SetResourceReference(Border.BackgroundProperty, "BgBrush");
            shell.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            Content = shell;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.SizeChanged += (_, _) =>
            {
                root.Clip = new RectangleGeometry(
                    new Rect(0, 0, root.ActualWidth, root.ActualHeight), 11, 11);
            };
            shell.Child = root;

            var titleBar = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(11, 11, 0, 0)
            };
            titleBar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            titleBar.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            var titleBarContent = new Grid();
            titleBar.Child = titleBarContent;
            titleBarContent.ColumnDefinitions.Add(new ColumnDefinition());
            titleBarContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text = Title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 10, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "AppForegroundBrush");
            titleBarContent.Children.Add(titleText);

            var close = new Button
            {
                Content = new Viewbox
                {
                    Width = 12,
                    Height = 12,
                    Child = new System.Windows.Shapes.Path
                    {
                        Data = Geometry.Parse("M 2,2 L 10,10 M 10,2 L 2,10"),
                        StrokeThickness = 1.6,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Stretch = Stretch.Fill
                    }
                },
                Width = 36,
                Height = 30,
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            close.Background = Brushes.Transparent;
            close.BorderBrush = Brushes.Transparent;
            close.BorderThickness = new Thickness(0);
            close.FocusVisualStyle = null;
            ((System.Windows.Shapes.Path)((Viewbox)close.Content).Child).SetResourceReference(
                System.Windows.Shapes.Shape.StrokeProperty, "AppForegroundBrush");
            WindowChrome.SetIsHitTestVisibleInChrome(close, true);
            close.Click += (_, _) => CloseWith(GetCloseResult(buttons));
            Grid.SetColumn(close, 1);
            titleBarContent.Children.Add(close);
            Grid.SetRow(titleBar, 0);
            root.Children.Add(titleBar);

            var content = new Grid { Margin = new Thickness(24, 22, 24, 12) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            var iconBadge = new Grid
            {
                Width = 44,
                Height = 44,
                VerticalAlignment = VerticalAlignment.Top
            };
            var badgeBackground = new Border
            {
                CornerRadius = new CornerRadius(13),
                Opacity = 0.14
            };
            badgeBackground.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            iconBadge.Children.Add(badgeBackground);
            var glyph = new TextBlock
            {
                Text = GetGlyph(image),
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            iconBadge.Children.Add(glyph);
            content.Children.Add(iconBadge);

            var body = new TextBox
            {
                Text = LocalizationService.Translate(message, language),
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 320,
                IsTabStop = false,
                Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            body.SetResourceReference(TextBox.ForegroundProperty, "AppForegroundBrush");
            var messageCard = new Border
            {
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 13, 16, 13),
                Child = body
            };
            messageCard.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            messageCard.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            Grid.SetColumn(messageCard, 2);
            content.Children.Add(messageCard);
            Grid.SetRow(content, 1);
            root.Children.Add(content);

            if (buttons != MessageBoxButton.OK)
            {
                var footer = new Border
                {
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Padding = new Thickness(20, 14, 20, 16),
                    CornerRadius = new CornerRadius(0, 0, 11, 11)
                };
                footer.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
                footer.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
                var footerButtons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                AddButtons(footerButtons, buttons);
                footer.Child = footerButtons;
                Grid.SetRow(footer, 2);
                root.Children.Add(footer);
            }

            KeyDown += (_, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Escape)
                {
                    CloseWith(GetCloseResult(buttons));
                    args.Handled = true;
                }
            };
        }

        private void AddButtons(Panel panel, MessageBoxButton buttons)
        {
            var labels = buttons switch
            {
                MessageBoxButton.OK => new[] { ("OK", MessageBoxResult.OK) },
                MessageBoxButton.OKCancel => new[] { ("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel) },
                MessageBoxButton.YesNo => new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No) },
                MessageBoxButton.YesNoCancel => new[]
                {
                    ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel)
                },
                _ => new[] { ("OK", MessageBoxResult.OK) }
            };

            for (int index = 0; index < labels.Length; index++)
            {
                var (key, result) = labels[index];
                var button = new Button
                {
                    Content = LocalizationService.Translate(key, GetLanguage()),
                    MinWidth = 86,
                    Height = 36,
                    Margin = new Thickness(10, 0, 0, 0),
                    Padding = new Thickness(16, 0, 16, 0),
                    IsDefault = result is MessageBoxResult.OK or MessageBoxResult.No,
                    IsCancel = result == GetCloseResult(buttons)
                };
                SetButtonBrushes(button, index == 0 ? "AccentBrush" : "Surface2Brush",
                    index == 0 ? Brushes.White : null);
                button.Click += (_, _) => CloseWith(result);
                panel.Children.Add(button);
            }
        }

        private void SetButtonBrushes(Button button, string backgroundResource, Brush? foreground = null)
        {
            button.SetResourceReference(Control.BackgroundProperty, backgroundResource);
            if (foreground is null)
                button.SetResourceReference(Control.ForegroundProperty, "AppForegroundBrush");
            else
                button.Foreground = foreground;
            button.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
            button.BorderThickness = new Thickness(1);
            button.FocusVisualStyle = null;
        }

        private void CloseWith(MessageBoxResult result)
        {
            Result = result;
            DialogResult = result != MessageBoxResult.Cancel;
        }

        private static MessageBoxResult GetCloseResult(MessageBoxButton buttons) => buttons switch
        {
            MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.OK
        };

        private static string GetLanguage() =>
            Services.Settings is { } settings
                ? settings.Language
                : "en";

        private static string GetGlyph(MessageBoxImage image) => image switch
        {
            MessageBoxImage.Error => "!",
            MessageBoxImage.Warning => "!",
            MessageBoxImage.Question => "?",
            MessageBoxImage.Information => "i",
            _ => "·"
        };
    }
}
