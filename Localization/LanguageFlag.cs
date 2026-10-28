using System.Windows;
using System.Windows.Media;

namespace HttpFreeKit.Localization;

public sealed class LanguageFlag : FrameworkElement
{
    public static readonly DependencyProperty LanguageProperty =
        DependencyProperty.Register(nameof(Language), typeof(string), typeof(LanguageFlag),
            new FrameworkPropertyMetadata("en", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Language
    {
        get => (string)GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(24, 16);

    protected override void OnRender(DrawingContext drawingContext)
    {
        const double width = 24;
        const double height = 16;
        var bounds = new Rect(0.5, 0.5, width - 1, height - 1);
        var clip = new RectangleGeometry(bounds, 1, 1);
        drawingContext.PushClip(clip);

        switch (Language)
        {
            case "en":
                DrawUnitedKingdom(drawingContext, width, height);
                break;
            case "fr":
                DrawVerticalTricolour(drawingContext, width, height, "#002395", "#FFFFFF", "#ED2939");
                break;
            case "es":
                DrawSpain(drawingContext, width, height);
                break;
            case "it":
                DrawVerticalTricolour(drawingContext, width, height, "#009246", "#FFFFFF", "#CE2B37");
                break;
            case "pt":
                DrawPortugal(drawingContext, width, height);
                break;
            case "zh":
                DrawChina(drawingContext, width, height);
                break;
            case "ru":
                DrawHorizontalTricolour(drawingContext, width, height, "#FFFFFF", "#0039A6", "#D52B1E");
                break;
            default:
                DrawUnitedKingdom(drawingContext, width, height);
                break;
        }

        drawingContext.Pop();
        drawingContext.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 220, 225, 235)), 0.7),
            bounds, 1, 1);
    }

    private static void DrawVerticalTricolour(DrawingContext dc, double width, double height,
        string left, string middle, string right)
    {
        dc.DrawRectangle(Brush(left), null, new Rect(0, 0, width / 3, height));
        dc.DrawRectangle(Brush(middle), null, new Rect(width / 3, 0, width / 3, height));
        dc.DrawRectangle(Brush(right), null, new Rect(width * 2 / 3, 0, width / 3, height));
    }

    private static void DrawHorizontalTricolour(DrawingContext dc, double width, double height,
        string top, string middle, string bottom)
    {
        dc.DrawRectangle(Brush(top), null, new Rect(0, 0, width, height / 3));
        dc.DrawRectangle(Brush(middle), null, new Rect(0, height / 3, width, height / 3));
        dc.DrawRectangle(Brush(bottom), null, new Rect(0, height * 2 / 3, width, height / 3));
    }

    private static void DrawSpain(DrawingContext dc, double width, double height)
    {
        dc.DrawRectangle(Brush("#AA151B"), null, new Rect(0, 0, width, height * 0.25));
        dc.DrawRectangle(Brush("#F1BF00"), null, new Rect(0, height * 0.25, width, height * 0.5));
        dc.DrawRectangle(Brush("#AA151B"), null, new Rect(0, height * 0.75, width, height * 0.25));

        var crest = new Rect(width * 0.24, height * 0.34, 2.2, 4.8);
        dc.DrawRectangle(Brush("#AA151B"), null, crest);
        dc.DrawRectangle(Brush("#F1BF00"), null, new Rect(crest.X + 0.6, crest.Y + 1, 1, 2.8));
        dc.DrawRectangle(Brush("#AA151B"), null, new Rect(crest.X + 0.8, crest.Y + 1.4, 0.6, 0.8));
    }

    private static void DrawPortugal(DrawingContext dc, double width, double height)
    {
        dc.DrawRectangle(Brush("#006600"), null, new Rect(0, 0, width * 0.4, height));
        dc.DrawRectangle(Brush("#FF0000"), null, new Rect(width * 0.4, 0, width * 0.6, height));
        dc.DrawEllipse(Brush("#FFCC00"), null, new Point(width * 0.4, height * 0.5), 3.4, 3.4);
        dc.DrawEllipse(Brush("#FFFFFF"), null, new Point(width * 0.4, height * 0.5), 1.7, 2.3);
    }

    private static void DrawChina(DrawingContext dc, double width, double height)
    {
        dc.DrawRectangle(Brush("#DE2910"), null, new Rect(0, 0, width, height));
        var gold = Brush("#FFDE00");
        DrawStar(dc, gold, new Point(5.4, 4.6), 2.5, 1.05, -90);
        DrawStar(dc, gold, new Point(10.2, 2.2), 1, 0.4, -55);
        DrawStar(dc, gold, new Point(12.1, 4.1), 1, 0.4, -20);
        DrawStar(dc, gold, new Point(12.1, 6.8), 1, 0.4, 20);
        DrawStar(dc, gold, new Point(10.1, 8.8), 1, 0.4, 55);
    }

    private static void DrawStar(DrawingContext dc, Brush brush, Point center, double outerRadius,
        double innerRadius, double rotation)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (int i = 0; i < 10; i++)
            {
                double angle = (rotation + i * 36) * Math.PI / 180;
                double radius = i % 2 == 0 ? outerRadius : innerRadius;
                var point = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
                if (i == 0) context.BeginFigure(point, true, true);
                else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    private static void DrawUnitedKingdom(DrawingContext dc, double width, double height)
    {
        dc.DrawRectangle(Brush("#012169"), null, new Rect(0, 0, width, height));

        DrawPolygon(dc, Brush("#FFFFFF"), new Point(0, 0), new Point(3.2, 0),
            new Point(width, height - 3.2), new Point(width, height), new Point(width - 3.2, height),
            new Point(0, 3.2));
        DrawPolygon(dc, Brush("#FFFFFF"), new Point(width, 0), new Point(width, 3.2),
            new Point(3.2, height), new Point(0, height), new Point(0, height - 3.2),
            new Point(width - 3.2, 0));

        DrawPolygon(dc, Brush("#C8102E"), new Point(0, 0), new Point(1.6, 0),
            new Point(width, height - 1.6), new Point(width, height), new Point(width - 1.6, height),
            new Point(0, 1.6));
        DrawPolygon(dc, Brush("#C8102E"), new Point(width, 0), new Point(width, 1.6),
            new Point(1.6, height), new Point(0, height), new Point(0, height - 1.6),
            new Point(width - 1.6, 0));

        dc.DrawRectangle(Brush("#FFFFFF"), null, new Rect(width * 0.4, 0, width * 0.2, height));
        dc.DrawRectangle(Brush("#FFFFFF"), null, new Rect(0, height * 0.32, width, height * 0.36));
        dc.DrawRectangle(Brush("#C8102E"), null, new Rect(width * 0.45, 0, width * 0.1, height));
        dc.DrawRectangle(Brush("#C8102E"), null, new Rect(0, height * 0.39, width, height * 0.22));
    }

    private static void DrawPolygon(DrawingContext dc, Brush fill, params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            for (int i = 1; i < points.Length; i++)
                context.LineTo(points[i], true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(fill, null, geometry);
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
