using System.Text.Json;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Views;

public partial class RulesView : UserControl
{
    private Point _dragStart;
    private Rule? _draggedRule;
    private RuleDragAdorner? _dragAdorner;
    private AdornerLayer? _dragAdornerLayer;
    private bool _dragMoved;

    public RulesView()
    {
        InitializeComponent();
        RulesGrid.ItemsSource = Services.Rules.Rules;
        Services.Rules.Rules.CollectionChanged += Rules_CollectionChanged;
        UpdateEmptyState();
    }

    private Rule? Selected => RulesGrid.SelectedItem as Rule;
    private Window? Owner => Window.GetWindow(this);
    private static System.Collections.ObjectModel.ObservableCollection<Rule> Rules => Services.Rules.Rules;

    private void Rules_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState()
    {
        EmptyText.Text = LocalizationService.Translate("Aucune règle configurée", Services.Settings.Language);
        EmptyText.Visibility = RulesGrid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void RefreshLanguage()
    {
        UpdateEmptyState();
        LocalizationService.Apply(this, Services.Settings.Language);
        RulesGrid.Items.Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var r = RuleEditorWindow.Show(Owner, new Rule());
        if (r == null) return;
        Rules.Add(r);
        Services.Rules.Commit();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRule(sender) is not { } sel) return;
        int idx = Rules.IndexOf(sel);
        var edited = RuleEditorWindow.Show(Owner, sel);
        if (edited == null || idx < 0) return;
        Rules[idx] = edited;
        RulesGrid.SelectedItem = edited;
        Services.Rules.Commit();
    }

    private void Dup_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRule(sender) is not { } sel) return;
        var copy = JsonUtil.Clone(sel);
        copy.Name += " (copie)";
        Rules.Insert(Rules.IndexOf(sel) + 1, copy);
        Services.Rules.Commit();
    }

    private void Del_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRule(sender) is not { } sel) return;
        if (HttpFreeKit.AppDialog.Show(Owner, LocalizationService.Format(
                "Delete rule “{0}”?", Services.Settings.Language, sel.Name),
                LocalizationService.Translate("Delete", Services.Settings.Language),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Rules.Remove(sel);
        Services.Rules.Commit();
    }

    private Rule? GetContextRule(object sender)
    {
        if (sender is MenuItem { DataContext: Rule rule }) return rule;
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow row } }
            && row.DataContext is Rule rowRule)
            return rowRule;
        return Selected;
    }

    private void RuleRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow row || row.DataContext is not Rule rule) return;
        RulesGrid.SelectedItem = rule;
        row.IsSelected = true;
    }

    private void RuleRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggedRule = null;
        if (sender is not DataGridRow row || row.DataContext is not Rule rule) return;

        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source != null && source is not CheckBox && source is not ButtonBase)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        if (source is CheckBox or ButtonBase) return;

        _dragStart = e.GetPosition(RulesGrid);
        _draggedRule = rule;
    }

    private void RuleRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedRule == null || e.LeftButton != MouseButtonState.Pressed) return;

        Point position = e.GetPosition(RulesGrid);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var rule = _draggedRule;
        var layer = AdornerLayer.GetAdornerLayer(RulesGrid);
        if (layer != null)
        {
            _dragAdornerLayer = layer;
            _dragAdorner = new RuleDragAdorner(RulesGrid, rule.Name);
            layer.Add(_dragAdorner);
            _dragAdorner.MoveTo(position);
        }

        try
        {
            var result = DragDrop.DoDragDrop((DependencyObject)sender, rule, DragDropEffects.Move);
            if (_dragMoved && result == DragDropEffects.Move)
            {
                RulesGrid.SelectedItem = rule;
                Services.Rules.Commit();
            }
        }
        finally
        {
            if (_dragAdorner != null && _dragAdornerLayer != null)
                _dragAdornerLayer.Remove(_dragAdorner);
            _dragAdorner = null;
            _dragAdornerLayer = null;
            _draggedRule = null;
            _dragMoved = false;
        }
    }

    private void RuleRow_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(Rule)) || _draggedRule == null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        var position = e.GetPosition(RulesGrid);
        _dragAdorner?.MoveTo(position);

        if (sender is not DataGridRow targetRow || targetRow.DataContext is not Rule target)
            return;

        int sourceIndex = Rules.IndexOf(_draggedRule);
        int targetIndex = Rules.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0) return;

        bool insertAfter = e.GetPosition(targetRow).Y >= targetRow.ActualHeight / 2;
        int insertionIndex = targetIndex + (insertAfter ? 1 : 0);
        if (sourceIndex < insertionIndex) insertionIndex--;
        insertionIndex = Math.Clamp(insertionIndex, 0, Rules.Count - 1);

        if (sourceIndex == insertionIndex) return;
        Rules.Move(sourceIndex, insertionIndex);
        RulesGrid.SelectedItem = _draggedRule;
        _dragMoved = true;
        e.Handled = true;
    }

    private void RuleRow_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
    }

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: Rule rule, IsChecked: { } isChecked })
            rule.Enabled = isChecked;
        Services.Rules.Commit();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = LocalizationService.Translate("Rules (*.json)|*.json", Services.Settings.Language),
            FileName = "rules.json"
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(Rules.ToList(), JsonUtil.Options));
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = LocalizationService.Translate("Rules (*.json)|*.json", Services.Settings.Language)
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<Rule>>(File.ReadAllText(dlg.FileName), JsonUtil.Options) ?? new();
            foreach (var r in list) Rules.Add(r);
            Services.Rules.Commit();
        }
        catch (Exception ex)
        {
            HttpFreeKit.AppDialog.Show(Owner,
                LocalizationService.Format("Invalid file: {0}", Services.Settings.Language, ex.Message),
                LocalizationService.Translate("Import", Services.Settings.Language),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed class RuleDragAdorner : Adorner
    {
        private readonly string _ruleName;
        private Point _position;
        private const double AdornerWidth = 340;
        private const double AdornerHeight = 34;

        public RuleDragAdorner(UIElement adornedElement, string ruleName) : base(adornedElement)
        {
            _ruleName = ruleName;
            IsHitTestVisible = false;
            Opacity = 0.92;
        }

        public void MoveTo(Point position)
        {
            _position = position;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var rect = new Rect(
                Math.Clamp(_position.X + 12, 0, Math.Max(0, AdornedElement.RenderSize.Width - AdornerWidth)),
                Math.Clamp(_position.Y + 10, 0, Math.Max(0, AdornedElement.RenderSize.Height - AdornerHeight)),
                Math.Min(AdornerWidth, AdornedElement.RenderSize.Width),
                AdornerHeight);
            drawingContext.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x36)),
                new Pen(new SolidColorBrush(Color.FromRgb(0xE1, 0x42, 0x1F)), 1.5),
                rect, 6, 6);

            var formatted = new FormattedText(
                _ruleName,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF0)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = rect.Width - 24,
                Trimming = TextTrimming.CharacterEllipsis
            };
            drawingContext.DrawText(formatted, new Point(rect.X + 12, rect.Y + 9));
        }
    }
}
