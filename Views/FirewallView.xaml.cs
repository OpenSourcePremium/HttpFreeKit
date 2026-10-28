using System.Windows;
using System.Windows.Controls;
using System.Collections.Specialized;
using Microsoft.Win32;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Views;

public partial class FirewallView : UserControl
{
    public FirewallView()
    {
        InitializeComponent();
        FwGrid.ItemsSource = Services.Firewall.Entries;
        Services.Firewall.Entries.CollectionChanged += Entries_CollectionChanged;
        UpdateEmptyState();
    }

    private Window? Owner => Window.GetWindow(this);

    private void Entries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState()
    {
        EmptyText.Text = LocalizationService.Translate("Aucune application bloquée", Services.Settings.Language);
        EmptyText.Visibility = FwGrid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void RefreshLanguage() => UpdateEmptyState();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = LocalizationService.Translate("Choose an application to block", Services.Settings.Language),
            Filter = LocalizationService.Translate("Applications (*.exe)|*.exe", Services.Settings.Language),
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;

        var r = HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Format(
            "Block ALL inbound and outbound traffic for:\n{0}\n\nusing Windows Firewall?",
            Services.Settings.Language, dlg.FileName),
            LocalizationService.Translate("Block application", Services.Settings.Language),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        try { Services.Firewall.Block(dlg.FileName); }
        catch (Exception ex) { HttpFreeKit.AppDialog.Show(Owner, ex.Message,
            LocalizationService.Translate("Windows Firewall", Services.Settings.Language),
            MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Unblock_Click(object sender, RoutedEventArgs e)
    {
        var selected = FwGrid.SelectedItems.Cast<FirewallEntry>().ToList();
        if (selected.Count == 0) return;
        foreach (var entry in selected) Services.Firewall.Unblock(entry);
    }

    private void UnblockAll_Click(object sender, RoutedEventArgs e)
    {
        var r = HttpFreeKit.AppDialog.Show(Owner,
            LocalizationService.Translate("Remove all blocking rules created by HTTP FreeKit?", Services.Settings.Language),
            LocalizationService.Translate("Unblock all", Services.Settings.Language),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        try { Services.Firewall.UnblockAll(includeUnknown: true); }
        catch (Exception ex) { HttpFreeKit.AppDialog.Show(Owner, ex.Message,
            LocalizationService.Translate("Windows Firewall", Services.Settings.Language),
            MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
