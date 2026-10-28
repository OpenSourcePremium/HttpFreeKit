using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Titanium.Web.Proxy.Http;

namespace HttpFreeKit.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}


public static class AppPaths
{
    public static readonly string ExeDir = AppContext.BaseDirectory;
    public static readonly string DefaultDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".httpfreekit");
    public static readonly string LegacyDataDir = Path.Combine(ExeDir, "PortableProxy-data");
    private static readonly string DataLocationFile = Path.Combine(DefaultDataDir, "data-location.txt");
    private static string _dataDir = LoadDataDir();
    public static string DataDir => _dataDir;
    private static string MigrationMarker => Path.Combine(DataDir, ".legacy-data-migrated");

    public static string Rules => Path.Combine(DataDir, "rules.json");
    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string ProxyBackup => Path.Combine(DataDir, "system-proxy-backup.json");
    public static string Firewall => Path.Combine(DataDir, "firewall.json");
    public static string CaPfx => Path.Combine(DataDir, "ca", "rootCert.pfx");
    public static string CaPem => Path.Combine(DataDir, "ca", "rootCert.pem");
    public static string CaBundle => Path.Combine(DataDir, "ca", "ca-bundle.pem");
    public static string EnvBackup => Path.Combine(DataDir, "env-backup.json");
    public static string NetTestTabs => Path.Combine(DataDir, "request-tabs.json");

    public static void Ensure()
    {
        Directory.CreateDirectory(DefaultDataDir);
        Directory.CreateDirectory(DataDir);
        MigrateLegacyData(LegacyDataDir, DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "ca"));
    }

    public static bool ChangeDataDir(string destination)
    {
        string target = Path.GetFullPath(destination);
        if (string.Equals(target, DataDir, StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsSameOrNestedPath(DataDir, target) || IsSameOrNestedPath(target, DataDir))
            throw new InvalidOperationException("The selected folder cannot be inside or contain the current data folder.");

        Directory.CreateDirectory(target);
        bool restoringDefault = string.Equals(target, DefaultDataDir, StringComparison.OrdinalIgnoreCase);
        CopyDataDirectory(DataDir, target, restoringDefault);

        Directory.CreateDirectory(DefaultDataDir);
        if (restoringDefault)
        {
            if (File.Exists(DataLocationFile))
                File.Delete(DataLocationFile);
        }
        else
        {
            string temporaryFile = DataLocationFile + ".tmp";
            File.WriteAllText(temporaryFile, target);
            File.Move(temporaryFile, DataLocationFile, true);
        }

        _dataDir = target;
        return true;
    }

    public static void ResetDataDirConfiguration()
    {
        if (File.Exists(DataLocationFile))
            File.Delete(DataLocationFile);
        _dataDir = DefaultDataDir;
    }

    private static string LoadDataDir()
    {
        try
        {
            string? configuredPath = ReadDataLocation(DataLocationFile);
            if (configuredPath is not null)
                return configuredPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to read HTTP FreeKit data folder configuration: {ex}");
        }

        return DefaultDataDir;
    }

    private static string? ReadDataLocation(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        string path = File.ReadAllText(filePath).Trim();
        return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
    }

    private static void CopyDataDirectory(string source, string destination, bool overwriteConflicts)
    {
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(file => !string.Equals(Path.GetFullPath(file), DataLocationFile, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var conflicts = overwriteConflicts ? Array.Empty<string>() : files
            .Select(file => Path.Combine(destination, Path.GetRelativePath(source, file)))
            .Where(File.Exists)
            .ToArray();
        if (conflicts.Length > 0)
            throw new InvalidOperationException("The selected folder already contains HTTP FreeKit data files. Choose an empty folder.");

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in files)
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwriteConflicts);
        }
    }

    private static bool IsSameOrNestedPath(string parent, string candidate)
    {
        string relative = Path.GetRelativePath(parent, candidate);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static void MigrateLegacyData(string source, string destination)
    {
        if (File.Exists(MigrationMarker)
            || !Directory.Exists(source)
            || string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
                File.Copy(file, target);
        }

        File.WriteAllText(MigrationMarker, DateTimeOffset.UtcNow.ToString("O"));
    }
}

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T LoadOrDefault<T>(string path, Func<T> fallback)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback();
        }
        catch { }
        return fallback();
    }

    public static void Save<T>(string path, T value)
    {
        AppPaths.Ensure();
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }

    public static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

public static class Glob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public static bool IsMatch(string pattern, string text)
    {
        var rx = Cache.GetOrAdd(pattern, p => new Regex(
            "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline));
        return rx.IsMatch(text);
    }
}

public static class Fmt
{
    public enum BodyFormat { PlainText, Json, Xml, Html }

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly Regex HtmlTokens = new(
        @"<!--[\s\S]*?-->|<![^>]*>|</?[A-Za-z][^>]*>|[^<]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlDocumentStart = new(
        @"^<(?:html|head|body|div|span|p|a|form|input|button|table|section|article|main|nav|ul|ol|li|h[1-6]|script|style|meta|link|title)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> HtmlBlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "body", "caption", "dd", "details", "dialog",
        "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3",
        "h4", "h5", "h6", "head", "header", "html", "li", "main", "nav", "ol", "optgroup",
        "option", "p", "section", "summary", "table", "tbody", "td", "tfoot", "th", "thead",
        "tr", "ul", "pre", "script", "style"
    };
    private static readonly HashSet<string> HtmlVoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param",
        "source", "track", "wbr"
    };

    public static string Size(long bytes) =>
        bytes < 0 ? "" :
        bytes < 1024 ? $"{bytes} o" :
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} Ko" :
        $"{bytes / 1048576.0:0.#} Mo";

    public static bool IsText(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var ct = contentType.ToLowerInvariant();
        return ct.StartsWith("text/") || ct.Contains("json") || ct.Contains("xml") || ct.Contains("javascript")
            || ct.Contains("x-www-form-urlencoded") || ct.Contains("graphql") || ct.Contains("yaml");
    }


    public static bool LooksText(byte[] b)
    {
        if (b.Length == 0) return true;
        int n = Math.Min(b.Length, 8192), bad = 0;
        for (int i = 0; i < n; i++)
        {
            byte c = b[i];
            if (c == 0) return false;
            if (c < 32 && c != 9 && c != 10 && c != 13) bad++;
        }
        if (bad * 50 > n) return false;
        try { _ = new UTF8Encoding(false, true).GetString(b); return true; }
        catch (ArgumentException) { return false; }
    }

    public static string HexDump(byte[] data, int max = 4096)
    {
        var sb = new StringBuilder();
        int n = Math.Min(data.Length, max);
        for (int i = 0; i < n; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            for (int j = 0; j < 16; j++) sb.Append(i + j < n ? data[i + j].ToString("X2") + " " : "   ");
            sb.Append(' ');
            for (int j = 0; j < 16 && i + j < n; j++)
            {
                byte c = data[i + j];
                sb.Append(c >= 32 && c < 127 ? (char)c : '.');
            }
            sb.AppendLine();
        }
        if (data.Length > n) sb.Append($"… ({data.Length:N0} bytes total)");
        return sb.ToString();
    }

    public static string Truncate(string? s, int max = 200_000)
        => s == null ? "" : s.Length <= max ? s : s[..max] + $"\n… (truncated, {s.Length:N0} characters total)";

    public static string Pretty(string? contentType, string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return body ?? "";
        if (contentType != null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && body.Length < 500_000)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return JsonSerializer.Serialize(doc.RootElement, PrettyOptions);
            }
            catch { }
        }
        return body;
    }

    public static BodyFormat DetectBodyFormat(string? contentType, string? body)
    {
        string mediaType = (contentType ?? "").Split(';', 2)[0].Trim();
        if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            return BodyFormat.Json;
        if (mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
            return BodyFormat.Xml;
        if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            return BodyFormat.Html;

        ReadOnlySpan<char> text = body is null ? ReadOnlySpan<char>.Empty : body.AsSpan().TrimStart();
        if (text.Length == 0) return BodyFormat.PlainText;
        if (text[0] is '{' or '[')
        {
            try
            {
                using var _ = JsonDocument.Parse(body!);
                return BodyFormat.Json;
            }
            catch (JsonException) { }
        }
        if (text.StartsWith("<?xml".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return BodyFormat.Xml;
        if (text.StartsWith("<!doctype html".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || HtmlDocumentStart.IsMatch(text[..Math.Min(text.Length, 512)].ToString()))
            return BodyFormat.Html;
        if (text[0] == '<')
            return BodyFormat.Xml;
        return BodyFormat.PlainText;
    }

    public static string FormatBody(string? contentType, string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 1_000_000) return body ?? "";
        try
        {
            return DetectBodyFormat(contentType, body) switch
            {
                BodyFormat.Json => FormatJson(body),
                BodyFormat.Xml => FormatXml(body),
                BodyFormat.Html => FormatHtml(body),
                _ => body
            };
        }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidOperationException
                                   or ArgumentException)
        {
            return body;
        }
    }

    private static string FormatJson(string body)
    {
        using var document = JsonDocument.Parse(body);
        return JsonSerializer.Serialize(document.RootElement, PrettyOptions);
    }

    private static string FormatXml(string body)
    {
        var document = XDocument.Parse(body, LoadOptions.None);
        return document.ToString(SaveOptions.None);
    }

    private static string FormatHtml(string html)
    {
        var output = new StringBuilder(html.Length + 64);
        int depth = 0;
        string? rawTextTag = null;
        bool AtLineStart() => output.Length == 0 || output[^1] is '\r' or '\n';
        void NewLine()
        {
            while (output.Length > 0 && output[^1] is ' ' or '\t') output.Length--;
            if (!AtLineStart()) output.AppendLine();
        }
        void Indent()
        {
            if (AtLineStart()) output.Append(' ', Math.Max(0, depth) * 2);
        }

        foreach (Match match in HtmlTokens.Matches(html))
        {
            string token = match.Value;
            if (rawTextTag is not null)
            {
                output.Append(token);
                if (token.StartsWith("</" + rawTextTag, StringComparison.OrdinalIgnoreCase))
                    rawTextTag = null;
                continue;
            }

            if (token.StartsWith("<!--", StringComparison.Ordinal) || token.StartsWith("<!", StringComparison.Ordinal))
            {
                NewLine();
                Indent();
                output.Append(token);
                NewLine();
                continue;
            }
            if (!token.StartsWith('<') || token.Length < 3)
            {
                output.Append(token);
                continue;
            }

            bool closing = token[1] == '/';
            int nameStart = closing ? 2 : 1;
            int nameEnd = nameStart;
            while (nameEnd < token.Length && (char.IsLetterOrDigit(token[nameEnd])
                || token[nameEnd] is ':' or '-' or '_')) nameEnd++;
            if (nameEnd == nameStart)
            {
                output.Append(token);
                continue;
            }

            string tag = token[nameStart..nameEnd];
            bool block = HtmlBlockTags.Contains(tag);
            if (block)
            {
                if (closing) depth = Math.Max(0, depth - 1);
                NewLine();
                Indent();
            }
            output.Append(token);
            bool selfClosing = token.EndsWith("/>", StringComparison.Ordinal)
                || HtmlVoidTags.Contains(tag);
            if (block)
            {
                if (!closing && !selfClosing) depth++;
                NewLine();
            }
            if (!closing && tag.Equals("pre", StringComparison.OrdinalIgnoreCase)
                || !closing && tag.Equals("script", StringComparison.OrdinalIgnoreCase)
                || !closing && tag.Equals("style", StringComparison.OrdinalIgnoreCase))
                rawTextTag = tag;
        }
        return output.ToString().Trim();
    }

    public static string Curl(TrafficEntry e)
    {
        string Q(string s) => s.Replace("'", "'\\''");
        var sb = new StringBuilder($"curl -X {e.Method} '{Q(e.Url)}'");
        var skip = new[] { "host", "content-length", "connection", "proxy-connection", "accept-encoding" };
        foreach (var line in e.RequestHeaders.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = line.Split(':', 2)[0].Trim().ToLowerInvariant();
            if (skip.Contains(name)) continue;
            sb.Append(" -H '").Append(Q(line)).Append('\'');
        }
        if (!string.IsNullOrEmpty(e.RequestBody) && IsText(e.RequestContentType))
            sb.Append(" --data-raw '").Append(Q(e.RequestBody)).Append('\'');
        return sb.ToString();
    }
}

public static class HeaderExtensions
{

    public static void SetOrAddHeaderValue(this HeaderCollection headers, string name, string value)
    {
        headers.RemoveHeader(name);
        headers.AddHeader(name, value);
    }
}
