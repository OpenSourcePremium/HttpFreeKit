using System.Text.Json.Serialization;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Core;

public sealed class NetTestHeader
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class NetTestTab : ObservableObject
{
    private string _name = "New request";
    public string Name { get => _name; set => Set(ref _name, value); }
    [JsonIgnore] public bool IsPlaceholder { get; set; }
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public List<NetTestHeader> Headers { get; set; } = new();
    public string BodyText { get; set; } = "";
    public string? BodyBase64 { get; set; }
    public string ContentType { get; set; } = "application/json";
    public bool BodyIsBinary { get; set; }
    public bool FollowRedirects { get; set; } = true;
    public int MaxRedirects { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 60;
    public bool IgnoreCertificateErrors { get; set; }
    [JsonPropertyName("UseHTTPFreeKitProxy")]
    public bool UseHttpFreeKitProxy { get; set; }
    [JsonPropertyName("UseWireScopeProxy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacyUseWireScopeProxy
    {
        get => false;
        set
        {
            if (value)
                UseHttpFreeKitProxy = true;
        }
    }
    public int HttpVersion { get; set; } = 2;
    public long? OriginalTrafficId { get; set; }
    public bool OriginalBodyIncomplete { get; set; }
    public bool AllowIncompleteBody { get; set; }
    public string? ResponseSummary { get; set; }
    public string? ResponseHeaders { get; set; }
    public string? ResponseBody { get; set; }
    public string? ResponseRedirects { get; set; }
    public string ResponseContentType { get; set; } = "";
    [JsonIgnore] public byte[]? ResponseBodyBytes { get; set; }
    [JsonIgnore] public bool ResponseIsBinary { get; set; }
    public string? ResponseError { get; set; }
    [JsonIgnore]
    public string? ResponseFilePath { get; set; }

    public byte[] GetBodyBytes(string? contentType = null)
    {
        if (BodyBase64 is not null)
            return Convert.FromBase64String(BodyBase64);
        var type = contentType ?? ContentType;
        var marker = type.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return System.Text.Encoding.UTF8.GetBytes(BodyText);
        var charset = type[(marker + "charset=".Length)..].Split(';')[0].Trim().Trim('"', '\'');
        try { return System.Text.Encoding.GetEncoding(charset).GetBytes(BodyText); }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(LocalizationService.Format(
                "Charset “{0}” is not supported.", Services.Settings.Language, charset), ex);
        }
    }
}

public sealed class NetTestResult
{
    public int StatusCode { get; init; }
    public string ReasonPhrase { get; init; } = "";
    public string Version { get; init; } = "";
    public Uri FinalUri { get; init; } = new("about:blank");
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<string> Redirects { get; init; } = Array.Empty<string>();
    public IReadOnlyList<KeyValuePair<string, string[]>> Headers { get; init; } =
        Array.Empty<KeyValuePair<string, string[]>>();
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public bool BodyTruncated { get; init; }
    public string ContentType { get; init; } = "";
    public string? BodyFilePath { get; init; }
    public string? Error { get; init; }
}

public sealed class NetTestSendOptions
{
    public bool FollowRedirects { get; init; } = true;
    public int MaxRedirects { get; init; } = 10;
    public int TimeoutSeconds { get; init; } = 60;
    public bool IgnoreCertificateErrors { get; init; }
    public bool UseHttpFreeKitProxy { get; init; }
    public int HttpVersion { get; init; } = 2;
}
