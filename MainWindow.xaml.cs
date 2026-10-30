using System.Windows;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;
using HttpFreeKit.Views;

namespace HttpFreeKit;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private readonly bool _recovered;
    private bool _suppress;
    private string _language = "en";
    private readonly DispatcherTimer _presenceRefreshTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly UpdateService _updateService = new();
    private bool _updateCheckInProgress;
    private bool _startupUpdateCheckStarted;
    private string _updateStatusKey = "Ready to check for updates.";
    private object[] _updateStatusValues = [];
    private UpdateWindow? _releaseWindow;

    public MainWindow(bool recovered)
    {
        _recovered = recovered;
        InitializeComponent();
        _language = LocalizationService.SupportedLanguages.Contains(Services.Settings.Language)
            ? Services.Settings.Language
            : "en";
        SidebarVersionText.Text = LocalizationService.Format(
            "HTTP FreeKit version {0}", _language, UpdateService.CurrentVersion);
        _presenceRefreshTimer.Tick += (_, _) => UpdateDiscordPresence();
        Closed += (_, _) => _presenceRefreshTimer.Stop();
        ContentRendered += (_, _) =>
        {
            if (_startupUpdateCheckStarted)
                return;

            _startupUpdateCheckStarted = true;
            _ = CheckForUpdatesAsync(isManual: false);
        };
        Loaded += (_, _) =>
        {
            LocalizationService.Apply(this, _language);
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => LocalizationService.Apply(this, _language)));
            if (_recovered)
                AppDialog.Show(this, LocalizationService.Translate(
                    "The previous session did not shut down cleanly; the original Windows proxy settings have been restored.",
                    _language), "HTTP FreeKit", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateDiscordPresence();
            _presenceRefreshTimer.Start();
            if (Services.Settings.AutoCapture) CaptureToggle.IsOn = true;
            RefreshUpdateLabels();
        };
    }

    public void ChangeLanguage(string language)
    {
        if (!LocalizationService.SupportedLanguages.Contains(language, StringComparer.Ordinal))
            return;

        _language = language;
        Services.Settings.Language = language;
        Services.Settings.Save();
        LocalizationService.Apply(this, language);
        TrafficPage.RefreshLanguage();
        ConnectionsPage.RefreshLanguage();
        FirewallPage.RefreshLanguage();
        RulesPage.RefreshLanguage();
        NetTestsPage.RefreshLanguage();
        SettingsPage.RefreshLanguage();
        SettingsPage.RefreshDiscordStatus();
        RefreshUpdateLabels();
        UpdateDiscordPresence();
    }

    public async Task CheckForUpdatesAsync(bool isManual)
    {
        if (_updateCheckInProgress)
            return;

        _updateCheckInProgress = true;
        SettingsPage.SetUpdateCheckInProgress(true);
        SetUpdateStatus("Checking for updates…");

        try
        {
            UpdateCheckResult result = await _updateService.CheckForUpdatesAsync();
            switch (result.Availability)
            {
                case UpdateAvailability.Current:
                    SetUpdateStatus("You're up to date (v{0}).", result.CurrentVersion);
                    break;
                case UpdateAvailability.InstallerMissing:
                    SetUpdateStatus(
                        "Version {0} is available, but its installer is not attached yet.",
                        result.LatestVersion);
                    break;
                case UpdateAvailability.Available:
                    SetUpdateStatus("New version {0} is available.", result.LatestVersion);
                    break;
            }

            if (result.Release is not null
                && (isManual || !string.Equals(
                    Services.Settings.DoNotRemindUpdateVersion, result.Release.Version, StringComparison.Ordinal)))
                ShowUpdateWindow(result.Release);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or TaskCanceledException
                                   or System.Text.Json.JsonException
                                   or InvalidDataException
                                   or IOException)
        {
            SetUpdateStatus("Unable to check for updates: {0}", ex.Message);
            if (isManual)
                AppDialog.Show(this, LocalizationService.Format(
                    "Unable to check for updates: {0}", _language, ex.Message),
                    "HTTP FreeKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateCheckInProgress = false;
            SettingsPage.SetUpdateCheckInProgress(false);
        }
    }

    public async Task ShowCurrentReleaseInfoAsync()
    {
        SettingsPage.SetUpdateCheckInProgress(true);
        SetUpdateStatus("Loading release notes…");
        try
        {
            UpdateRelease? release = await _updateService.GetCurrentReleaseAsync();
            if (release is null)
            {
                SetUpdateStatus("No release notes are available for this version.");
                AppDialog.Show(this, LocalizationService.Translate(
                    "No release notes are available for this version.", _language),
                    LocalizationService.Translate("Current release", _language),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ShowReleaseInfoWindow(release);
            SetUpdateStatus("Ready to check for updates.");
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or TaskCanceledException
                                   or System.Text.Json.JsonException
                                   or InvalidDataException
                                   or IOException)
        {
            SetUpdateStatus("Unable to check for updates: {0}", ex.Message);
            AppDialog.Show(this, LocalizationService.Format(
                "Unable to check for updates: {0}", _language, ex.Message),
                LocalizationService.Translate("Current release", _language),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SettingsPage.SetUpdateCheckInProgress(false);
        }
    }

    private void ShowUpdateWindow(UpdateRelease release)
    {
        if (_releaseWindow is { IsVisible: true })
        {
            _releaseWindow.Activate();
            return;
        }

        var window = new UpdateWindow(
            this,
            release,
            _language,
            isUpdate: true,
            installAction: async (progress, cancellationToken) =>
            {
                await _updateService.DownloadAndLaunchInstallerAsync(release, progress, cancellationToken);
                Application.Current.Shutdown();
            },
            ignoreAction: doNotRemind =>
            {
                if (doNotRemind)
                    Services.Settings.DoNotRemindUpdateVersion = release.Version;
                else if (string.Equals(
                    Services.Settings.DoNotRemindUpdateVersion, release.Version, StringComparison.Ordinal))
                    Services.Settings.DoNotRemindUpdateVersion = null;
                Services.Settings.Save();
                SetUpdateStatus(doNotRemind
                    ? "Version {0} was ignored."
                    : "Update dismissed for now.", release.Version);
            },
            doNotRemindInitially: string.Equals(
                Services.Settings.DoNotRemindUpdateVersion, release.Version, StringComparison.Ordinal));
        _releaseWindow = window;
        window.Closed += (_, _) => _releaseWindow = null;
        window.ShowDialog();
    }

    private void ShowReleaseInfoWindow(UpdateRelease release)
    {
        var window = new UpdateWindow(
            this,
            release,
            _language,
            isUpdate: false);
        _releaseWindow = window;
        window.Closed += (_, _) => _releaseWindow = null;
        window.ShowDialog();
    }

    private void SetUpdateStatus(string key, params object[] values)
    {
        _updateStatusKey = key;
        _updateStatusValues = values;
        string status = LocalizationService.Format(key, _language, values);
        SettingsPage.SetUpdateStatus(status);
    }

    private void RefreshUpdateLabels()
    {
        SidebarVersionText.Text = LocalizationService.Format(
            "HTTP FreeKit version {0}", _language, UpdateService.CurrentVersion);
        SettingsPage.SetCurrentVersion(UpdateService.CurrentVersion);
        SettingsPage.SetUpdateStatus(LocalizationService.Format(
            _updateStatusKey, _language, _updateStatusValues));
        _releaseWindow?.RefreshLanguage(_language);
    }

    public void RefreshTheme()
    {
        TrafficPage.RefreshTheme();
        NetTestsPage.RefreshTheme();
    }

    public void OpenNetTest(TrafficEntry entry, bool sendImmediately)
    {
        NetTestsPage.OpenFromTraffic(entry, sendImmediately);
        Nav.SelectedItem = Nav.MenuItems.OfType<ModernWpf.Controls.NavigationViewItem>()
            .FirstOrDefault(item => Equals(item.Tag, "nettests"));
    }

    private void Nav_SelectionChanged(ModernWpf.Controls.NavigationView sender,
                                      ModernWpf.Controls.NavigationViewSelectionChangedEventArgs args)
    {
        if (SettingsPage == null) return;
        var tag = (args.SelectedItemContainer as ModernWpf.Controls.NavigationViewItem)?.Tag as string;
        TrafficPage.Visibility = tag == "traffic" ? Visibility.Visible : Visibility.Collapsed;
        NetTestsPage.Visibility = tag == "nettests" ? Visibility.Visible : Visibility.Collapsed;
        RulesPage.Visibility = tag == "rules" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPage.Visibility = tag == "connections" ? Visibility.Visible : Visibility.Collapsed;
        FirewallPage.Visibility = tag == "firewall" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        UpdateDiscordPresence(tag);
    }

    private void UpdateDiscordPresence(string? page = null)
    {
        var tag = page ?? (Nav.SelectedItem as ModernWpf.Controls.NavigationViewItem)?.Tag as string ?? "traffic";
        string language = Services.Settings.Language;
        int trafficCount = Services.Traffic.Items.Count;
        int pendingBreakpoints = Services.Traffic.Items.Count(item => item.IsBreakpointPending);
        int activeRules = Services.Rules.Rules.Count(rule => rule.Enabled);
        int ruleMatches = Services.Rules.Rules.Where(rule => rule.Enabled).Sum(rule => rule.Hits);
        bool captureActive = CaptureToggle?.IsOn == true;

        string details;
        string stats;
        switch (tag)
        {
            case "traffic":
                details = T(captureActive ? "Intercepting the web" : "Exploring network traffic");
                stats = F("{0} exchanges logged · {1} waiting for review", trafficCount, pendingBreakpoints);
                break;
            case "nettests":
                details = T("Crafting and testing HTTP requests");
                stats = F("{0} request tabs ready", NetTestsPage.TabCount);
                break;
            case "rules":
                details = T("Shaping HTTP traffic");
                stats = F("{0} active rules · {1} matches", activeRules, ruleMatches);
                break;
            case "connections":
                details = T("Mapping live connections");
                stats = F("{0} connections in view", Services.Connections.Rows.Count);
                break;
            case "firewall":
                details = T("Managing app network access");
                stats = F("{0} apps blocked", Services.Firewall.Entries.Count);
                break;
            case "settings":
                details = T("Tuning your HTTP FreeKit setup");
                stats = T(captureActive ? "Capture is running" : "Capture is standing by");
                break;
            default:
                details = T("Exploring HTTP FreeKit");
                stats = T(captureActive ? "Capture is running" : "Capture is standing by");
                break;
        }

        string state = string.Format(T(captureActive ? "Capture on · {0}" : "Capture off · {0}"), stats);
        Services.DiscordPresence.SetActivity(details, state);

        string T(string text) => LocalizationService.Translate(text, language);
        string F(string format, params object[] values) => LocalizationService.Format(format, language, values);
    }



    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
            source.AddHook(WindowMessageHook);
        ApplyWindowChromeTheme();
    }

    private static IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;

        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxPosition = new NativePoint
        {
            X = monitorInfo.Work.Left - monitorInfo.Monitor.Left,
            Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top
        };
        info.MaxSize = new NativePoint
        {
            X = monitorInfo.Work.Right - monitorInfo.Work.Left,
            Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top
        };
        Marshal.StructureToPtr(info, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    public void ApplyWindowChromeTheme()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = App.IsLightTheme ? 0 : 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            int round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            int border = dark == 1 ? 0x003A312E : 0x00E2DCD9;
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        }
        catch { }
    }



    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        bool maximize = WindowState != WindowState.Maximized;
        UpdateMaximizedLayout(maximize);
        WindowState = maximize ? WindowState.Maximized : WindowState.Normal;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizedLayout(WindowState == WindowState.Maximized);
    }

    private void UpdateMaximizedLayout(bool maximized)
    {

        RootGrid.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        GlyphMax.Data = Geometry.Parse(maximized
            ? "M 5,3 H 13 V 11 H 5 Z M 3,5 V 13 H 11"
            : "M 3,3 H 13 V 13 H 3 Z");
    }



    private async void CaptureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (CaptureToggle.IsOn) await StartCaptureAsync();
        else StopCapture();
    }


    public bool IsCaptureOn => CaptureToggle.IsOn;
    public void SetCapture(bool on) => CaptureToggle.IsOn = on;

    public void RestartCapture()
    {
        if (!CaptureToggle.IsOn) return;
        _suppress = true; CaptureToggle.IsOn = false; _suppress = false;
        StopCapture();
        CaptureToggle.IsOn = true;
    }

    private async Task StartCaptureAsync()
    {
        await Task.Yield();
        try
        {
            if (!File.Exists(AppPaths.CaPfx) || !CertificateService.IsInstalled())
            {
                var r = AppDialog.Show(this, LocalizationService.Format(
                    "To inspect HTTPS, HTTP FreeKit must install its root certificate “{0}” for your Windows session only.\n\nWindows will ask you to confirm. The certificate is removed by uninstalling (Settings) or with --cleanup.\n\nContinue?",
                    Services.Settings.Language, ProxyService.CaName),
                    LocalizationService.Translate("Root certificate", Services.Settings.Language),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) throw new OperationCanceledException();
            }

            if (!Services.Proxy.EnsureCaTrusted())
                throw new InvalidOperationException("The root certificate was not installed; HTTPS cannot be decrypted.");

            int port = Services.Settings.Port;
            var upstream = SystemProxyManager.GetOriginalUpstream();
            Services.Proxy.Start(port, upstream);

            if (Services.Settings.CaptureAllProcesses && !Services.Redirector.Start())
            {
                AppDialog.Show(this, LocalizationService.Format(
                        "Driver capture could not start; system proxy capture remains active.\n\n{0}",
                        Services.Settings.Language, Services.Redirector.LastError ?? ""),
                    LocalizationService.Translate("All-process capture unavailable", Services.Settings.Language),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            SystemProxyManager.Enable($"127.0.0.1:{port}");


            if (Services.Settings.SetEnvVars)
            {
                try
                {
                    CertificateService.WriteBundles();
                    EnvironmentManager.Enable(port, AppPaths.CaPem, AppPaths.CaBundle);
                }
                catch { }
            }

            UpdateDiscordPresence();
        }
        catch (OperationCanceledException)
        {
            Revert();
        }
        catch (Exception ex)
        {
            Revert();
            AppDialog.Show(this, ex.Message,
                LocalizationService.Translate("Unable to start capture", Services.Settings.Language),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Revert()
    {
        try { Services.Redirector.Stop(); } catch { }
        try { SystemProxyManager.Restore(); } catch { }
        try { EnvironmentManager.Restore(); } catch { }
        try { Services.Proxy.Stop(); } catch { }
        _suppress = true; CaptureToggle.IsOn = false; _suppress = false;
        UpdateDiscordPresence();
    }

    private void StopCapture()
    {

        try { Services.Redirector.Stop(); } catch { }
        try { SystemProxyManager.Restore(); } catch { }
        try { EnvironmentManager.Restore(); } catch { }
        try { Services.Proxy.Stop(); } catch { }
        UpdateDiscordPresence();
    }
}
