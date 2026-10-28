namespace HttpFreeKit.Core;

public sealed class FilterQuery
{
    private static readonly HashSet<string> TrafficFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "statuscode", "status", "host", "path", "process", "pid", "method", "rule", "outcome", "scheme", "url"
    };

    private static readonly HashSet<string> ConnectionFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "process", "pid", "local", "remote", "address", "protocol", "state", "port", "localport", "remoteport"
    };

    private readonly Clause[] _clauses;

    private FilterQuery(Clause[] clauses) => _clauses = clauses;

    public static FilterQuery Parse(string query, bool traffic)
    {
        var knownFields = traffic ? TrafficFields : ConnectionFields;
        var clauses = new List<Clause>();
        var token = new System.Text.StringBuilder();
        bool quoted = false;

        void AddToken()
        {
            if (token.Length == 0)
                return;

            string value = token.ToString();
            token.Clear();
            int separator = value.IndexOf(':');
            if (separator > 0 && knownFields.Contains(value[..separator]))
                clauses.Add(new Clause(value[..separator].ToLowerInvariant(), value[(separator + 1)..]));
            else
                clauses.Add(new Clause(null, value));
        }

        foreach (char character in query)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                AddToken();
                continue;
            }

            token.Append(character);
        }

        AddToken();
        return new FilterQuery(clauses.ToArray());
    }

    public bool Matches(Func<string, bool> freeTextMatch, Func<string, string, bool> fieldMatch)
    {
        foreach (var clause in _clauses)
        {
            if (clause.Field is { } field
                ? !fieldMatch(field, clause.Value)
                : !freeTextMatch(clause.Value))
                return false;
        }

        return true;
    }

    private sealed record Clause(string? Field, string Value);
}
