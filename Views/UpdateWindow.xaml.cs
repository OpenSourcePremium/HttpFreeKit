using System.Windows;
using System.Net.Http;
using System.Windows.Media;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateRelease _release;
    private readonly bool _isUpdate;
    private readonly Func<IProgress<double>, CancellationToken, Task>? _installAction;
    private readonly Action<bool>? _ignoreAction;
    private CancellationTokenSource? _installationCancellation;
    private string? _statusKey;
    private object[] _statusValues = [];
    private string _language;

    public UpdateWindow(
        Window owner,
        UpdateRelease release,
        string language,
        bool isUpdate,
        Func<IProgress<double>, CancellationToken, Task>? installAction = null,
        Action<bool>? ignoreAction = null,
        bool doNotRemindInitially = false)
    {
        InitializeComponent();
        Owner = owner;
        _release = release;
        _language = language;
        _isUpdate = isUpdate;
        _installAction = installAction;
        _ignoreAction = ignoreAction;
        WindowShell.SizeChanged += (_, _) =>
            WindowShell.Clip = new RectangleGeometry(
                new Rect(0, 0, WindowShell.ActualWidth, WindowShell.ActualHeight), 11, 11);

        LocalizationService.Apply(this, language);
        Title = LocalizationService.Translate(isUpdate ? "New version available" : "Current release", language);
        WindowHeading.Text = Title;
        ReleaseTitleText.Text = release.Title;
        VersionText.Text = LocalizationService.Format("Version {0}", language, release.Version);
        ReleaseNotesText.Text = GetReleaseNotes(language);

        IgnoreButton.Visibility = isUpdate ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.Visibility = isUpdate ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        DismissButton.Visibility = isUpdate ? Visibility.Collapsed : Visibility.Visible;
        DoNotRemindCheckBox.Visibility = isUpdate ? Visibility.Visible : Visibility.Collapsed;
        DoNotRemindCheckBox.IsChecked = doNotRemindInitially;
        InstallButton.IsEnabled = isUpdate && release.Installer is not null;

        if (isUpdate && release.Installer is null)
        {
            SetStatus("The release is published, but its installer is not attached yet.");
        }
    }

    public void RefreshLanguage(string language)
    {
        _language = language;
        LocalizationService.Apply(this, language);
        Title = LocalizationService.Translate(_isUpdate ? "New version available" : "Current release", language);
        WindowHeading.Text = Title;
        ReleaseTitleText.Text = _release.Title;
        VersionText.Text = LocalizationService.Format("Version {0}", language, _release.Version);
        ReleaseNotesText.Text = GetReleaseNotes(language);
        if (_statusKey is not null)
            StatusText.Text = LocalizationService.Format(_statusKey, language, _statusValues);
    }

    private string GetReleaseNotes(string language)
        => string.IsNullOrWhiteSpace(_release.Notes)
            ? LocalizationService.Translate("No release notes are available for this version.", language)
            : UpdateService.FormatReleaseNotes(_release.Notes);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        _ignoreAction?.Invoke(DoNotRemindCheckBox.IsChecked == true);
        Close();
    }

    private void CancelInstall_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        SetStatus("Canceling update download…");
        _installationCancellation?.Cancel();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installAction is null || _release.Installer is null || _installationCancellation is not null)
            return;

        var cancellation = new CancellationTokenSource();
        _installationCancellation = cancellation;
        IgnoreButton.IsEnabled = false;
        InstallButton.IsEnabled = false;
        IgnoreButton.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
        DoNotRemindCheckBox.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        SetStatus("Downloading the update installer…");
        ProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        ProgressText.Text = "0%";
        var progress = new Progress<double>(value =>
        {
            if (!IsVisible)
                return;
            int percent = Math.Clamp((int)(value * 100), 0, 100);
            DownloadProgressBar.Value = percent;
            ProgressText.Text = $"{percent}%";
        });

        try
        {
            await _installAction(progress, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!IsVisible)
                return;
            ProgressPanel.Visibility = Visibility.Collapsed;
            SetStatus("Update download canceled.");
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or TaskCanceledException
                                   or IOException
                                   or InvalidDataException
                                   or InvalidOperationException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or System.Security.Cryptography.CryptographicException
                                   or System.ComponentModel.Win32Exception)
        {
            if (!IsVisible)
                return;
            ProgressPanel.Visibility = Visibility.Collapsed;
            SetStatus("Unable to install the update: {0}", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_installationCancellation, cancellation))
            {
                _installationCancellation.Dispose();
                _installationCancellation = null;
            }

            if (IsVisible)
            {
                CancelButton.Visibility = Visibility.Collapsed;
                IgnoreButton.Visibility = _isUpdate ? Visibility.Visible : Visibility.Collapsed;
                InstallButton.Visibility = _isUpdate ? Visibility.Visible : Visibility.Collapsed;
                DoNotRemindCheckBox.Visibility = _isUpdate ? Visibility.Visible : Visibility.Collapsed;
                IgnoreButton.IsEnabled = true;
                InstallButton.IsEnabled = _isUpdate && _release.Installer is not null;
            }
        }
    }

    private void SetStatus(string key, params object[] values)
    {
        _statusKey = key;
        _statusValues = values;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = LocalizationService.Format(key, _language, values);
    }

    protected override void OnClosed(EventArgs e)
    {
        _installationCancellation?.Cancel();
        base.OnClosed(e);
    }
}
