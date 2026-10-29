using System.Windows;
using System.Windows.Media;
using HttpFreeKit.Core;

namespace HttpFreeKit;

public partial class App : Application
{
    private sealed record ThemePalette(
        bool IsLight,
        string Background,
        string Surface,
        string Surface2,
        string Line,
        string Muted,
        string Selection,
        string Foreground,
        string ForegroundSecondary,
        string TitleHover,
        string TitleHoverBorder,
        string Accent,
        string AccentLight,
        string AccentHover,
        string RowSelection,
        string SelectedHover,
        string TabShadow);

    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--cleanup", StringComparison.OrdinalIgnoreCase)))
        {
            bool purge = e.Args.Any(a => a.Equals("--purge", StringComparison.OrdinalIgnoreCase));
            var log = Uninstaller.Run(deleteData: purge, selfDelete: false);
            AppDialog.Show(log, "HTTP FreeKit - cleanup", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, "HTTP FreeKit-SingleInstance", out bool created);
        if (!created)
        {
            AppDialog.Show("HTTP FreeKit is already running.", "HTTP FreeKit");
            Shutdown();
            return;
        }

        Services.Init();
        ApplyTheme(Services.Settings.Theme);


        bool recovered = false;
        if (SystemProxyManager.IsActive || EnvironmentManager.IsActive)
        {
            try { SystemProxyManager.Restore(); recovered = true; } catch { }
            try { EnvironmentManager.Restore(); } catch { }
        }

        DispatcherUnhandledException += (_, args) =>
        {
            RestoreSystem();
            AppDialog.Show(args.Exception.ToString(), "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreSystem();
        SessionEnding += (_, _) => RestoreSystem();

        new MainWindow(recovered).Show();
    }

    public static void ApplyTheme(string theme)
    {
        string selectedTheme = theme switch
        {
            "Light" => "Light",
            "Midnight" => "Midnight",
            "Slate" => "Slate",
            "Violet" => "Violet",
            _ => "Dark"
        };
        var palette = selectedTheme switch
        {
            "Light" => new ThemePalette(true, "#F4F5F7", "#FFFFFF", "#E9EBEF", "#D9DCE2",
                "#626976", "#E2E5EB", "#20232A", "#626976", "#D9DCE2", "#C5C9D3",
                "#E1421F", "#FF7A45", "#C73519", "#E2E5EB", "#D9DDE4", "#38000000"),
            "Midnight" => new ThemePalette(false, "#101722", "#172130", "#202E40", "#2A3A4E",
                "#91A0B4", "#1A2635", "#E6EDF7", "#AAB8CA", "#202D3E", "#34465B",
                "#6EA8FE", "#8DB9FF", "#4D8FEA", "#294B73", "#365D88", "#758A9FB8"),
            "Slate" => new ThemePalette(false, "#1B2026", "#242B33", "#2D3742", "#38434F",
                "#9BA8B7", "#202A34", "#E9EDF2", "#B8C2CE", "#2D3742", "#414E5C",
                "#74B7A2", "#91CDB9", "#5A9F89", "#36574F", "#41685D", "#758A9BA8"),
            "Violet" => new ThemePalette(false, "#191621", "#231E2D", "#30283D", "#40364F",
                "#A49AB6", "#292237", "#F0ECF7", "#C6BDD5", "#342B43", "#514361",
                "#9B78D0", "#B59AE5", "#845FB9", "#302642", "#403252", "#758D79A8"),
            _ => new ThemePalette(false, "#16181D", "#1F2128", "#2A2D36", "#2E313A",
                "#8A8F9C", "#191B21", "#E8EAF0", "#AEB7C8", "#22252D", "#3A3D47",
                "#E1421F", "#FF7A45", "#C73519", "#252932", "#30343E", "#758A8F9C")
        };
        Services.Settings.Theme = selectedTheme;
        if (Current.Resources.MergedDictionaries.OfType<ModernWpf.ThemeResources>().FirstOrDefault()
            is { } themeResources)
        {
            themeResources.AccentColor =
                (Color)ColorConverter.ConvertFromString(palette.Accent);
        }
        ModernWpf.ThemeManager.Current.ApplicationTheme =
            palette.IsLight ? ModernWpf.ApplicationTheme.Light : ModernWpf.ApplicationTheme.Dark;

        var colors = new Dictionary<string, string>
        {
            ["BgBrush"] = palette.Background,
            ["SurfaceBrush"] = palette.Surface,
            ["Surface2Brush"] = palette.Surface2,
            ["LineBrush"] = palette.Line,
            ["MutedBrush"] = palette.Muted,
            ["SelectionBrush"] = palette.Selection,
            ["AppForegroundBrush"] = palette.Foreground,
            ["AppForegroundSecondaryBrush"] = palette.ForegroundSecondary,
            ["TitleBarHoverBrush"] = palette.Surface2,
            ["TitleBarHoverBorderBrush"] = palette.TitleHoverBorder,
            ["NavigationViewDefaultPaneBackground"] = palette.Background,
            ["NavigationViewExpandedPaneBackground"] = palette.Background,
            ["NavigationViewTopPaneBackground"] = palette.Background,
            ["DataGridRowSelectedBackgroundBrush"] = palette.RowSelection,
            ["DataGridRowSelectedUnfocusedBackgroundBrush"] = palette.RowSelection,
            ["DataGridRowSelectedHoverBackgroundBrush"] = palette.SelectedHover,
            ["DataGridRowSelectedHoverUnfocusedBackgroundBrush"] = palette.SelectedHover,
            ["DataGridCellSelectedBackgroundBrush"] = palette.RowSelection,
            ["DataGridCellSelectedUnfocusedBackgroundBrush"] = palette.RowSelection
        };

        foreach (var (key, value) in colors)
            Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        Current.Resources["HttpFreeKitAccentColor"] = (Color)ColorConverter.ConvertFromString(palette.Accent);
        Current.Resources["HttpFreeKitAccentLightColor"] = (Color)ColorConverter.ConvertFromString(palette.AccentLight);
        Current.Resources["HttpFreeKitBackgroundColor"] = (Color)ColorConverter.ConvertFromString(palette.Background);
        Current.Resources["HttpFreeKitSurfaceColor"] = (Color)ColorConverter.ConvertFromString(palette.Surface);
        Current.Resources["HttpFreeKitSurface2Color"] = (Color)ColorConverter.ConvertFromString(palette.Surface2);
        Current.Resources["HttpFreeKitLineColor"] = (Color)ColorConverter.ConvertFromString(palette.Line);
        Current.Resources["HttpFreeKitMutedColor"] = (Color)ColorConverter.ConvertFromString(palette.Muted);
        Current.Resources["HttpFreeKitForegroundColor"] = (Color)ColorConverter.ConvertFromString(palette.Foreground);
        Current.Resources["HttpFreeKitForegroundSecondaryColor"] =
            (Color)ColorConverter.ConvertFromString(palette.ForegroundSecondary);
        Current.Resources["AccentBrush"] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(palette.Accent));
        Current.Resources["AccentHoverBrush"] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(palette.AccentHover));
        Current.Resources["DataGridSelectionBrush"] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(palette.RowSelection));
        Current.Resources["DataGridSelectionHoverBrush"] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(palette.SelectedHover));
        Current.Resources["TabShadowColor"] = (Color)ColorConverter.ConvertFromString(palette.TabShadow);
    }

    public static bool IsLightTheme =>
        string.Equals(Services.Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);

    protected override void OnExit(ExitEventArgs e)
    {
        RestoreSystem();
        Services.DiscordPresence?.Dispose();
        try { Services.NetTests?.Dispose(); } catch { }
        base.OnExit(e);
    }

    private static void RestoreSystem()
    {
        try { SystemProxyManager.Restore(); } catch { }
        try { EnvironmentManager.Restore(); } catch { }
        try { Services.Proxy?.Stop(); } catch { }
    }
}
