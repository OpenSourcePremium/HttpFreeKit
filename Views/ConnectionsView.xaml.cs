using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Views;

public partial class ConnectionsView : UserControl
{
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _refreshTimer;
    private FilterQuery _filterQuery = FilterQuery.Parse("", traffic: false);

    public ConnectionsView()
    {
        InitializeComponent();
        ConnGrid.ItemsSource = Services.Connections.Rows;
        _view = CollectionViewSource.GetDefaultView(Services.Connections.Rows);
        _view.Filter = Filter;
        Services.Connections.Rows.CollectionChanged += (_, _) => UpdateCount();


        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _refreshTimer.Tick += (_, _) => { _view.Refresh(); UpdateCount(); };
    }

    private ConnectionRow? Selected => ConnGrid.SelectedItem as ConnectionRow;
    private Window? Owner => Window.GetWindow(this);

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Services.Connections.Start();
            _refreshTimer.Start();
            UpdateCount();
        }
        else
        {
            Services.Connections.Stop();
            _refreshTimer.Stop();
        }
    }

    private bool Filter(object o)
    {
        if (o is not ConnectionRow r) return false;
        if (EstablishedCheck.IsChecked == true && r.State != "ESTABLISHED") return false;

        bool Contains(string? value, string query) =>
            value != null && value.Contains(query, StringComparison.OrdinalIgnoreCase);
        bool HasPort(string endpoint, string query) =>
            endpoint.EndsWith($":{query}", StringComparison.OrdinalIgnoreCase);

        return _filterQuery.Matches(
            query => Contains(r.Process, query) || Contains(r.ProcessPath, query)
                || Contains(r.Local, query) || Contains(r.Remote, query) || Contains(r.Protocol, query)
                || Contains(r.State, query) || Contains(r.Pid.ToString(), query),
            (field, query) => field switch
            {
                "process" => Contains(r.Process, query) || Contains(r.ProcessPath, query),
                "pid" => Contains(r.Pid.ToString(), query),
                "local" => Contains(r.Local, query),
                "remote" => Contains(r.Remote, query),
                "address" => Contains(r.Local, query) || Contains(r.Remote, query),
                "protocol" => Contains(r.Protocol, query),
                "state" => Contains(r.State, query),
                "port" => HasPort(r.Local, query) || HasPort(r.Remote, query),
                "localport" => HasPort(r.Local, query),
                "remoteport" => HasPort(r.Remote, query),
                _ => false
            });
    }

    private void UpdateCount()
    {
        int n = ConnGrid.Items.Count;
        CountText.Text = LocalizationService.Format(
            n == 1 ? "{0} connexion" : "{0} connexions", Services.Settings.Language, n);
        EmptyText.Text = FilterBox.Text.Trim().Length > 0 || EstablishedCheck.IsChecked == true && Services.Connections.Rows.Count > 0
            ? LocalizationService.Translate("Aucun résultat", Services.Settings.Language)
            : LocalizationService.Translate("Aucune connexion trouvée", Services.Settings.Language);
        EmptyText.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void RefreshLanguage() => UpdateCount();

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_view == null) return;
        _filterQuery = FilterQuery.Parse(FilterBox.Text, traffic: false);
        _view.Refresh();
        UpdateCount();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        _view.Refresh();
        UpdateCount();
    }



    private void ConnectionsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu contextMenu)
            LocalizationService.Apply(contextMenu, Services.Settings.Language);
    }

    private void Ctx_CopyRemote(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Clipboard.SetText(s.Remote);
    }

    private void Ctx_CopyLocal(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Clipboard.SetText(s.Local);
    }

    private void Ctx_ViewHex(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        if (s.Protocol != "TCP" || s.State != "ESTABLISHED"
            || !string.Equals(s.Remote, $"127.0.0.1:{Services.Settings.Port}", StringComparison.OrdinalIgnoreCase))
        {
            HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Format(
                "Hex inspection is available for established TCP connections to the HTTP FreeKit proxy (127.0.0.1:{0}). Direct connections, UDP, and undecrypted HTTPS tunnels are not available in this view.",
                Services.Settings.Language, Services.Settings.Port),
                LocalizationService.Translate("Échanges hexadécimaux", Services.Settings.Language),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new HexExchangeWindow(s) { Owner = Owner }.Show();
    }

    private void Ctx_OpenFolder(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        if (string.IsNullOrEmpty(s.ProcessPath) || !File.Exists(s.ProcessPath))
        {
            HttpFreeKit.AppDialog.Show(Owner,
                LocalizationService.Translate("Application path is unknown (the process is protected or has already exited).",
                    Services.Settings.Language), "HTTP FreeKit");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{s.ProcessPath}\"") { UseShellExecute = true });
    }

    private void Ctx_BlockApp(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        if (string.IsNullOrEmpty(s.ProcessPath))
        {
            HttpFreeKit.AppDialog.Show(Owner,
                LocalizationService.Translate("Application path is unknown (the process is protected or has already exited).",
                    Services.Settings.Language), "HTTP FreeKit");
            return;
        }
        var r = HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Format(
            "Block ALL inbound and outbound traffic for:\n{0}\n\nusing Windows Firewall?",
            Services.Settings.Language, s.ProcessPath),
            LocalizationService.Translate("Block application", Services.Settings.Language),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        try { Services.Firewall.Block(s.ProcessPath); }
        catch (Exception ex) { HttpFreeKit.AppDialog.Show(Owner, ex.Message,
            LocalizationService.Translate("Windows Firewall", Services.Settings.Language),
            MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
