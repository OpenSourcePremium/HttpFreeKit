using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Views;

public partial class SettingsView : UserControl
{
    private bool _loading;
    private ModernWpf.Controls.ToggleSwitch? EnvSwitch => FindName("EnvToggle") as ModernWpf.Controls.ToggleSwitch;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadValues();
        Services.DiscordPresence.StatusChanged += DiscordPresence_StatusChanged;
    }

    private Window? Owner => Window.GetWindow(this);
    private MainWindow? Main => Owner as MainWindow;

    private void LoadValues()
    {
        _loading = true;
        var s = Services.Settings;
        ThemeCombo.SelectedValue = s.Theme;
        LanguageCombo.SelectedValue = LocalizationService.SupportedLanguages.Contains(s.Language, StringComparer.Ordinal)
            ? s.Language
            : "en";
        PortBox.Text = s.Port.ToString();
        AutoCaptureToggle.IsOn = s.AutoCapture;
        DiscordPresenceToggle.IsOn = s.DiscordPresence;
        if (EnvSwitch is { } env) env.IsOn = s.SetEnvVars;
        BypassBox.Text = s.BypassHosts;
        TrafficBodyBudgetBox.Text = s.TrafficBodyBudgetMb.ToString();
        DataPathText.Tag = AppPaths.DataDir;
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"HTTP FreeKit {v?.ToString(3) ?? "0.1.0"}";
        _loading = false;
        UpdateDiscordStatus(Services.DiscordPresence.Status);
        RefreshCert();
    }

    private void DiscordPresence_StatusChanged(string status)
    {
        Dispatcher.BeginInvoke(new Action(() => UpdateDiscordStatus(status)));
    }

    private void UpdateDiscordStatus(string status)
    {
        DiscordStatus.Text = LocalizationService.Translate(status, Services.Settings.Language);
    }

    public void RefreshDiscordStatus()
    {
        UpdateDiscordStatus(Services.DiscordPresence.Status);
    }

    public void RefreshLanguage()
    {
        _loading = true;
        ThemeCombo.SelectedIndex = -1;
        ThemeCombo.SelectedValue = Services.Settings.Theme;
        LanguageCombo.SelectedValue = Services.Settings.Language;
        _loading = false;
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedValue is not string theme) return;
        App.ApplyTheme(theme);
        Services.Settings.Save();
        Main?.ApplyWindowChromeTheme();
        Main?.RefreshTheme();

        _loading = true;
        ThemeCombo.SelectedIndex = -1;
        ThemeCombo.SelectedValue = theme;
        _loading = false;
    }

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedValue is not string language)
            return;

        Main?.ChangeLanguage(language);
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) RefreshCert();
    }

    private void RefreshCert()
    {
        bool ok = CertificateService.IsInstalled();
        CertStatus.Text = LocalizationService.Translate(ok
            ? "✔ Installé dans le magasin « Autorités de certification racines de confiance » (utilisateur courant)"
            : "✘ Non installé — le HTTPS ne pourra pas être déchiffré tant qu'il ne l'est pas",
            Services.Settings.Language);
    }



    private void AutoCapture_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Settings.AutoCapture = AutoCaptureToggle.IsOn;
        Services.Settings.Save();
    }

    private void DiscordPresence_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Settings.DiscordPresence = DiscordPresenceToggle.IsOn;
        Services.Settings.Save();
        Services.DiscordPresence.SetEnabled(DiscordPresenceToggle.IsOn);
    }

    private void Env_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (EnvSwitch is not { } env) return;
        Services.Settings.SetEnvVars = env.IsOn;
        Services.Settings.Save();
        Main?.RestartCapture();
    }

    private void ApplyPort_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port < 1024 || port > 65535)
        {
            HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Translate(
                "Enter a port between 1024 and 65535.", Services.Settings.Language),
                LocalizationService.Translate("Invalid port", Services.Settings.Language),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (port == Services.Settings.Port) return;

        Services.Settings.Port = port;
        Services.Settings.Save();
        Main?.RestartCapture();
    }

    private void SaveBypass_Click(object sender, RoutedEventArgs e)
    {
        Services.Settings.BypassHosts = BypassBox.Text.Trim();
        Services.Settings.Save();
    }

    private void ApplyTrafficBodyBudget_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TrafficBodyBudgetBox.Text.Trim(), out int budget) || budget is < 1 or > 2048)
        {
            HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Translate(
                "Saisissez une valeur entre 1 et 2048 Mo.", Services.Settings.Language),
                "HTTP FreeKit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Services.Settings.TrafficBodyBudgetMb = budget;
        Services.Settings.Save();
        Services.Traffic.RequestBodyBudgetBytes = budget * 1024L * 1024L;
    }



    private void InstallCert_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Services.Proxy.EnsureCaTrusted())
                HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Translate(
                    "The certificate was not installed (was the confirmation declined?).",
                    Services.Settings.Language), LocalizationService.Translate("Certificate", Services.Settings.Language),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            HttpFreeKit.AppDialog.Show(Owner, ex.Message,
                LocalizationService.Translate("Certificate", Services.Settings.Language),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshCert();
    }

    private void RemoveCert_Click(object sender, RoutedEventArgs e)
    {
        if (Main?.IsCaptureOn == true)
        {
            var r = HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Translate(
                "Capture is active. It will be stopped before removing the certificate. Continue?",
                Services.Settings.Language), LocalizationService.Translate("Certificate", Services.Settings.Language),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            Main.SetCapture(false);
        }

        try { CertificateService.RemoveFromStore(); }
        catch (Exception ex) { HttpFreeKit.AppDialog.Show(Owner, ex.Message,
            LocalizationService.Translate("Certificate", Services.Settings.Language),
            MessageBoxButton.OK, MessageBoxImage.Error); }
        RefreshCert();
    }



    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.Ensure();
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });
    }

    private void ChangeDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationService.Translate("Choose the HTTP FreeKit data folder", Services.Settings.Language),
            InitialDirectory = AppPaths.DataDir,
            Multiselect = false
        };
        if (dialog.ShowDialog(Owner) != true)
            return;

        try
        {
            if (!AppPaths.ChangeDataDir(dialog.FolderName))
                return;

            Services.Settings.Save();
            DataPathText.Tag = AppPaths.DataDir;
            HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Translate(
                "Existing HTTP FreeKit data was copied to the selected folder. This folder is now active.",
                Services.Settings.Language), "HTTP FreeKit", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Translate(ex.Message, Services.Settings.Language),
                LocalizationService.Translate("Unable to change the data folder", Services.Settings.Language),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreProxy_Click(object sender, RoutedEventArgs e)
    {
        if (Main?.IsCaptureOn == true) Main.SetCapture(false);
        try
        {
            SystemProxyManager.Restore();
            HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Translate(
                "Original Windows proxy settings have been restored.", Services.Settings.Language), "HTTP FreeKit",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            HttpFreeKit.AppDialog.Show(Owner, ex.Message, "HTTP FreeKit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var r = HttpFreeKit.AppDialog.Show(Owner,
            LocalizationService.Translate(
                "Restore Windows to its original state (system proxy, firewall rules, certificate)?\n\n• Yes : remove EVERYTHING, including the data folder and executable\n• No: clean Windows only, keeping your rules and executable\n• Cancel : do nothing",
                Services.Settings.Language),
            LocalizationService.Translate("Uninstall everything", Services.Settings.Language),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (r == MessageBoxResult.Cancel) return;
        bool everything = r == MessageBoxResult.Yes;

        if (Main?.IsCaptureOn == true) Main.SetCapture(false);

        string log = Uninstaller.Run(deleteData: everything, selfDelete: everything,
            language: Services.Settings.Language);
        HttpFreeKit.AppDialog.Show(Owner, log,
            LocalizationService.Translate("HTTP FreeKit - uninstall", Services.Settings.Language),
            MessageBoxButton.OK, MessageBoxImage.Information);
        Application.Current.Shutdown();
    }
}
