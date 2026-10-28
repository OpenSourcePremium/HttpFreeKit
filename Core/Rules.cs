using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Core;

public enum RulePhase { Request, Response }

public enum MatchField
{
    Url, Host, Path, Method, Process,
    RequestHeader, RequestBody,
    ResponseStatus, ResponseHeader, ResponseBody
}

public enum MatchOp { Contains, Is, StartsWith, EndsWith, Glob, RegexMatch }

public enum RuleActionType
{
    Block, Mock, Redirect, RewriteUrl, SetHeader, RemoveHeader, ReplaceBody, Delay, Passthrough, Breakpoint
}

public sealed class MatchContext
{
    public string Process = "", Host = "", Url = "", Path = "", Method = "";
    public string RequestHeaders = "", RequestBody = "";
    public string ResponseStatus = "", ResponseHeaders = "", ResponseBody = "";

    public string Get(MatchField f) => f switch
    {
        MatchField.Url => Url,
        MatchField.Host => Host,
        MatchField.Path => Path,
        MatchField.Method => Method,
        MatchField.Process => Process,
        MatchField.RequestHeader => RequestHeaders,
        MatchField.RequestBody => RequestBody,
        MatchField.ResponseStatus => ResponseStatus,
        MatchField.ResponseHeader => ResponseHeaders,
        MatchField.ResponseBody => ResponseBody,
        _ => ""
    };
}

public sealed class Matcher
{
    private static readonly ConcurrentDictionary<string, Regex?> Rx = new();

    public MatchField Field { get; set; } = MatchField.Host;
    public MatchOp Op { get; set; } = MatchOp.Glob;
    public string Value { get; set; } = "";
    public bool Negate { get; set; }

    public bool IsMatch(MatchContext c)
    {
        var v = c.Get(Field);
        bool r = Op switch
        {
            MatchOp.Contains => v.Contains(Value, StringComparison.OrdinalIgnoreCase),
            MatchOp.Is => string.Equals(v, Value, StringComparison.OrdinalIgnoreCase),
            MatchOp.StartsWith => v.StartsWith(Value, StringComparison.OrdinalIgnoreCase),
            MatchOp.EndsWith => v.EndsWith(Value, StringComparison.OrdinalIgnoreCase),
            MatchOp.Glob => Glob.IsMatch(Value, v),
            MatchOp.RegexMatch => SafeRegex(Value, v),
            _ => false
        };
        return r != Negate;
    }

    private static bool SafeRegex(string pattern, string input)
    {
        var rx = Rx.GetOrAdd(pattern, p =>
        {
            try { return new Regex(p, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)); }
            catch { return null; }
        });
        if (rx is null) return false;
        try { return rx.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}

public sealed class Rule : ObservableObject
{
    private string _name = "New rule";
    private volatile bool _enabled = true;
    private int _hits;
    private RulePhase _phase = RulePhase.Request;

    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Raise();
        }
    }
    public RulePhase Phase { get => _phase; set => Set(ref _phase, value); }
    public List<Matcher> Matchers { get; set; } = new();

    public RuleActionType ActionType { get; set; } = RuleActionType.Block;
    public int StatusCode { get; set; } = 403;
    public string ContentType { get; set; } = "application/json; charset=utf-8";
    public string Body { get; set; } = "";
    public string BodyFile { get; set; } = "";
    public string TargetUrl { get; set; } = "";
    public string HeaderName { get; set; } = "";
    public string HeaderValue { get; set; } = "";
    public string FindPattern { get; set; } = "";
    public bool FindIsRegex { get; set; }
    public string ReplaceWith { get; set; } = "";
    public int DelayMs { get; set; } = 1000;

    [JsonIgnore] public int Hits => _hits;
    public void Hit() { Interlocked.Increment(ref _hits); Raise(nameof(Hits)); }

    public bool IsMatch(MatchContext c) => Matchers.All(m => m.IsMatch(c));

    [JsonIgnore] public string PhaseLabel => T(Labels.Of(Labels.Phases, Phase));

    [JsonIgnore]
    public string ConditionSummary => Matchers.Count == 0 ? T("(always)") :
        string.Join($"  {T("AND")}  ", Matchers.Select(m =>
            $"{T(Labels.Of(Labels.Fields, m.Field))}{(m.Negate ? T(" NOT") : "")} " +
            $"{T(Labels.Of(Labels.Ops, m.Op)).ToLowerInvariant()} “{m.Value}”"));

    [JsonIgnore]
    public string ActionSummary => ActionType switch
    {
        RuleActionType.Block => F("Block ({0})", StatusCode),
        RuleActionType.Mock => F("Mock response {0}", StatusCode),
        RuleActionType.Redirect => F("Redirect → {0}", TargetUrl),
        RuleActionType.RewriteUrl => F("Rewrite URL → {0}", TargetUrl),
        RuleActionType.SetHeader => F("Header {0}: {1}", HeaderName, HeaderValue),
        RuleActionType.RemoveHeader => F("Remove header {0}", HeaderName),
        RuleActionType.ReplaceBody => F("Replace “{0}” with “{1}”", FindPattern, ReplaceWith),
        RuleActionType.Delay => F("Delay {0} ms", DelayMs),
        RuleActionType.Passthrough => T("Do not decrypt (tunnel)"),
        RuleActionType.Breakpoint => T("Breakpoint (éditer en direct)"),
        _ => ""
    };

    private static string T(string value) => LocalizationService.Translate(value, Services.Settings.Language);
    private static string F(string format, params object[] values)
        => LocalizationService.Format(format, Services.Settings.Language, values);
}

public static class Labels
{
    public static readonly List<KeyValuePair<RulePhase, string>> Phases = new()
    {
        new(RulePhase.Request, "Requête (avant l'envoi au serveur)"),
        new(RulePhase.Response, "Réponse (avant retour à l'application)"),
    };

    public static readonly List<KeyValuePair<MatchField, string>> Fields = new()
    {
        new(MatchField.Url, "URL complète"),
        new(MatchField.Host, "Hôte"),
        new(MatchField.Path, "Chemin + paramètres"),
        new(MatchField.Method, "Méthode HTTP"),
        new(MatchField.Process, "Application"),
        new(MatchField.RequestHeader, "En-têtes de requête"),
        new(MatchField.RequestBody, "Corps de requête"),
        new(MatchField.ResponseStatus, "Code de statut (réponse)"),
        new(MatchField.ResponseHeader, "En-têtes de réponse"),
        new(MatchField.ResponseBody, "Corps de réponse"),
    };

    public static readonly List<KeyValuePair<MatchOp, string>> Ops = new()
    {
        new(MatchOp.Glob, "Motif (* ?)"),
        new(MatchOp.Contains, "Contient"),
        new(MatchOp.Is, "Égal à"),
        new(MatchOp.StartsWith, "Commence par"),
        new(MatchOp.EndsWith, "Finit par"),
        new(MatchOp.RegexMatch, "Regex"),
    };

    public static readonly List<KeyValuePair<RuleActionType, string>> Actions = new()
    {
        new(RuleActionType.Block, "Bloquer"),
        new(RuleActionType.Mock, "Répondre automatiquement (mock)"),
        new(RuleActionType.Redirect, "Rediriger (302)"),
        new(RuleActionType.RewriteUrl, "Réécrire l'URL (transparent)"),
        new(RuleActionType.SetHeader, "Ajouter / modifier un en-tête"),
        new(RuleActionType.RemoveHeader, "Supprimer un en-tête"),
        new(RuleActionType.ReplaceBody, "Remplacer du texte dans le corps"),
        new(RuleActionType.Delay, "Retarder"),
        new(RuleActionType.Passthrough, "Ne pas déchiffrer le HTTPS (tunnel)"),
        new(RuleActionType.Breakpoint, "Breakpoint (éditer en direct)"),
    };

    public static string Of<T>(List<KeyValuePair<T, string>> list, T value) where T : notnull
        => list.FirstOrDefault(k => EqualityComparer<T>.Default.Equals(k.Key, value)).Value ?? value.ToString()!;
}

public sealed class RuleEngine
{
    public ObservableCollection<Rule> Rules { get; } = new();
    private volatile Rule[] _snapshot = Array.Empty<Rule>();

    public RuleEngine()
    {
        foreach (var r in JsonUtil.LoadOrDefault(AppPaths.Rules, Samples)) Rules.Add(r);
        Commit();
    }


    public void Commit()
    {
        _snapshot = Rules.ToArray();
        try { JsonUtil.Save(AppPaths.Rules, Rules.ToList()); } catch { }
    }

    public IEnumerable<Rule> Match(RulePhase phase, MatchContext ctx)
    {
        foreach (var r in _snapshot)
            if (r.Enabled && r.Phase == phase && r.IsMatch(ctx))
                yield return r;
    }

    private static List<Rule> Samples() => new()
    {
        new Rule
        {
            Name = "Example: block DoubleClick ads",
            Enabled = false,
            ActionType = RuleActionType.Block,
            Matchers = { new Matcher { Field = MatchField.Host, Op = MatchOp.Glob, Value = "*.doubleclick.net" } }
        },
        new Rule
        {
            Name = "Example: mock API",
            Enabled = false,
            ActionType = RuleActionType.Mock,
            StatusCode = 200,
            Body = "{ \"hello\": \"world\" }",
            Matchers = { new Matcher { Field = MatchField.Url, Op = MatchOp.Glob, Value = "https://api.example.com/hello*" } }
        },
    };
}
