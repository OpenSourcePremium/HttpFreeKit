using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Views;

public partial class RuleEditorWindow : Window
{
    private readonly ObservableCollection<Matcher> _matchers;

    public List<KeyValuePair<RulePhase, string>> PhaseOptions => Localize(Labels.Phases);
    public List<KeyValuePair<MatchField, string>> FieldOptions => Localize(Labels.Fields);
    public List<KeyValuePair<MatchOp, string>> OpOptions => Localize(Labels.Ops);
    public List<KeyValuePair<RuleActionType, string>> ActionOptions => Localize(Labels.Actions);

    private static List<KeyValuePair<T, string>> Localize<T>(List<KeyValuePair<T, string>> labels)
        => labels.Select(pair => new KeyValuePair<T, string>(
            pair.Key, LocalizationService.Translate(pair.Value, Services.Settings.Language))).ToList();

    private RuleEditorWindow(Rule rule)
    {
        InitializeComponent();
        if (rule.Name == "New rule")
            rule.Name = LocalizationService.Translate("New rule", Services.Settings.Language);
        LocalizationService.Apply(this, Services.Settings.Language);
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => LocalizationService.Apply(this, Services.Settings.Language)));
        DataContext = rule;
        _matchers = new ObservableCollection<Matcher>(rule.Matchers);
        if (_matchers.Count == 0) _matchers.Add(new Matcher());
        MatcherList.ItemsSource = _matchers;
        UpdateJsonEditorMode();
        UpdatePanels();
    }


    public static Rule? Show(Window? owner, Rule source)
    {
        var w = new RuleEditorWindow(JsonUtil.Clone(source)) { Owner = owner };
        return w.ShowDialog() == true ? (Rule)w.DataContext : null;
    }

    private void AddMatcher_Click(object sender, RoutedEventArgs e) => _matchers.Add(new Matcher());

    private void RemoveMatcher_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Matcher m) _matchers.Remove(m);
    }

    private void ActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePanels();

    private void ContentTypeBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateJsonEditorMode();

    private void UpdateJsonEditorMode()
    {
        BodyJsonEditor.IsJson = Fmt.DetectBodyFormat(
            ContentTypeBox?.Text ?? (DataContext as Rule)?.ContentType, BodyJsonEditor.Text)
            == Fmt.BodyFormat.Json;
    }

    private void PhaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is Rule rule && PhaseCombo.SelectedValue is RulePhase phase)
            rule.Phase = phase;
    }

    private void UpdatePanels()
    {
        if (PnlStatus == null || PnlDelay == null || ActionCombo.SelectedValue is not RuleActionType t) return;

        PnlStatus.Visibility = t is RuleActionType.Block or RuleActionType.Mock ? Visibility.Visible : Visibility.Collapsed;
        PnlMockOnly.Visibility = t == RuleActionType.Mock ? Visibility.Visible : Visibility.Collapsed;
        PnlUrl.Visibility = t is RuleActionType.Redirect or RuleActionType.RewriteUrl ? Visibility.Visible : Visibility.Collapsed;
        PnlHeader.Visibility = t is RuleActionType.SetHeader or RuleActionType.RemoveHeader ? Visibility.Visible : Visibility.Collapsed;
        HeaderValueBox.Visibility = t == RuleActionType.SetHeader ? Visibility.Visible : Visibility.Collapsed;
        PnlReplace.Visibility = t == RuleActionType.ReplaceBody ? Visibility.Visible : Visibility.Collapsed;
        PnlDelay.Visibility = t == RuleActionType.Delay ? Visibility.Visible : Visibility.Collapsed;

        HintText.Text = LocalizationService.Translate(t switch
        {
            RuleActionType.Passthrough => "Le HTTPS de ces connexions n'est pas déchiffré (utile si une application refuse le certificat de HTTP FreeKit). Utilisez uniquement des conditions sur l'hôte ou l'application.",
            RuleActionType.Block => "La requête n'est jamais envoyée au serveur : l'application reçoit une page/erreur avec le code choisi.",
            RuleActionType.Mock => "La requête n'est jamais envoyée au serveur : l'application reçoit la réponse définie ici.",
            RuleActionType.RewriteUrl => "La requête est envoyée à la nouvelle URL sans que l'application le sache.",
            RuleActionType.ReplaceBody => "Fonctionne sur les corps texte (HTML, JSON, JS, XML…).",
            RuleActionType.Breakpoint => "Suspend le trafic correspondant à cette règle pour modifier ses en-têtes et son contenu avant de le transmettre ou de l'abandonner.",
            _ => ""
        }, Services.Settings.Language);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var r = (Rule)DataContext;
        if (PhaseCombo.SelectedValue is RulePhase selectedPhase)
            r.Phase = selectedPhase;
        r.Matchers = _matchers.Where(m => !string.IsNullOrWhiteSpace(m.Value)).ToList();

        string? error = Validate(r);
        if (error != null)
        {
            HttpFreeKit.AppDialog.Show(this, LocalizationService.Translate(error, Services.Settings.Language),
                LocalizationService.Translate("Invalid rule", Services.Settings.Language),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private static string? Validate(Rule r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Give the rule a name.";

        if (r.Matchers.Count == 0 && r.ActionType is RuleActionType.Block or RuleActionType.Mock
                                         or RuleActionType.Redirect or RuleActionType.Passthrough
                                         or RuleActionType.Breakpoint)
            return "Add at least one condition; without one, this action would apply to ALL traffic.";

        if (r.Phase == RulePhase.Request &&
            r.Matchers.Any(m => m.Field is MatchField.ResponseStatus or MatchField.ResponseHeader or MatchField.ResponseBody))
            return "Response conditions require the Response phase.";

        if (r.Phase == RulePhase.Response &&
            r.ActionType is RuleActionType.Redirect or RuleActionType.RewriteUrl or RuleActionType.Passthrough)
            return "This action is only available in the Request phase.";

        foreach (var m in r.Matchers.Where(m => m.Op == MatchOp.RegexMatch))
        {
            try { _ = new Regex(m.Value); }
            catch (ArgumentException ex)
            {
                return LocalizationService.Format("Invalid regular expression “{0}”: {1}",
                    Services.Settings.Language, m.Value, ex.Message);
            }
        }

        if (r.ActionType is RuleActionType.Block or RuleActionType.Mock && (r.StatusCode < 100 || r.StatusCode > 599))
            return "The HTTP status code must be between 100 and 599.";

        if (r.ActionType is RuleActionType.Redirect or RuleActionType.RewriteUrl &&
            !Uri.TryCreate(r.TargetUrl, UriKind.Absolute, out _))
            return "Enter a valid absolute URL (https://…).";

        if (r.ActionType is RuleActionType.SetHeader or RuleActionType.RemoveHeader && string.IsNullOrWhiteSpace(r.HeaderName))
            return "Enter a header name.";

        if (r.ActionType == RuleActionType.ReplaceBody)
        {
            if (string.IsNullOrEmpty(r.FindPattern)) return "Enter the text to find.";
            if (r.FindIsRegex)
            {
                try { _ = new Regex(r.FindPattern); }
                catch (ArgumentException ex)
                {
                    return LocalizationService.Format("Invalid regular expression: {0}",
                        Services.Settings.Language, ex.Message);
                }
            }
        }
        return null;
    }
}
