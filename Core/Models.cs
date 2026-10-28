using System.Text.Json.Serialization;
using System.Windows.Media;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Core;


public sealed class TrafficEntry : ObservableObject
{
    private static SolidColorBrush Mk(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }
    private static readonly SolidColorBrush Green = Mk(0x6C, 0xCB, 0x5F), Yellow = Mk(0xFC, 0xE1, 0x00),
        Orange = Mk(0xFF, 0xA5, 0x3F), Red = Mk(0xFF, 0x6B, 0x6B), Violet = Mk(0xB4, 0x8B, 0xFF), Gray = Mk(0x9A, 0x9A, 0x9A);

    public long Id { get; set; }
    [JsonIgnore]
    public bool IsHarImported { get; set; }
    [JsonIgnore]
    public string? HarStatusText { get; set; }
    [JsonIgnore]
    public string? HarRequestVersion { get; set; }
    [JsonIgnore]
    public string? HarResponseVersion { get; set; }
    [JsonIgnore]
    public string? HarRedirectUrl { get; set; }
    [JsonIgnore]
    public bool ResponseBodyIncomplete { get; set; }
    public DateTime Time { get; init; }
    public int Pid { get; init; }
    public string Process { get; init; } = "";
    public string? ProcessPath { get; init; }
    public string Method { get; init; } = "";
    public string Scheme { get; init; } = "";
    public string Host { get; init; } = "";
    public string HostName { get; init; } = "";
    public string Path { get; init; } = "";
    public string Url { get; init; } = "";
    public long StartTicks { get; init; }
    private bool _completed;
    public bool Completed
    {
        get => _completed;
        set
        {
            if (Set(ref _completed, value) && value)
            {
                ResponseHeadersLoading = false;
                ResponseLoading = false;
            }
        }
    }

    private string _requestHeaders = "";
    public string RequestHeaders { get => _requestHeaders; set => Set(ref _requestHeaders, value); }
    private bool _responseHeadersLoading = true;
    public bool ResponseHeadersLoading { get => _responseHeadersLoading; set => Set(ref _responseHeadersLoading, value); }
    private bool _requestBodyLoading = true;
    public bool RequestBodyLoading { get => _requestBodyLoading; set => Set(ref _requestBodyLoading, value); }
    private bool _responseLoading = true;
    public bool ResponseLoading { get => _responseLoading; set => Set(ref _responseLoading, value); }
    public string RequestContentType { get; set; } = "";
    private bool _requestIsBinary;
    public bool RequestIsBinary { get => _requestIsBinary; set => Set(ref _requestIsBinary, value); }
    private bool _requestPreviewUnavailable;
    public bool RequestPreviewUnavailable { get => _requestPreviewUnavailable; set => Set(ref _requestPreviewUnavailable, value); }
    private byte[]? _requestBodyBytes;
    public byte[]? RequestBodyBytes { get => _requestBodyBytes; set => Set(ref _requestBodyBytes, value); }
    private long? _requestBinaryLength;
    public long? RequestBinaryLength { get => _requestBinaryLength; set => Set(ref _requestBinaryLength, value); }
    public byte[]? RequestPayloadPreview { get; set; }
    public long? RequestPayloadLength { get; set; }
    private byte[]? _requestReplayBody;
    public byte[]? RequestReplayBody { get => _requestReplayBody; set => Set(ref _requestReplayBody, value); }
    private bool _requestBodyIncomplete;
    public bool RequestBodyIncomplete { get => _requestBodyIncomplete; set => Set(ref _requestBodyIncomplete, value); }

    private string _reqBody = "";
    public string RequestBody { get => _reqBody; set { if (Set(ref _reqBody, value)) Raise(nameof(RequestBodyView)); } }
    public string RequestBodyView => Fmt.Pretty(RequestContentType, RequestBody);

    private int? _status;
    public int? StatusCode
    {
        get => _status;
        set { if (Set(ref _status, value)) { Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); } }
    }
    private bool _isBreakpointPending;
    [JsonIgnore]
    public bool IsBreakpointPending
    {
        get => _isBreakpointPending;
        set
        {
            if (Set(ref _isBreakpointPending, value))
            {
                Raise(nameof(StatusText));
                Raise(nameof(StatusBrush));
            }
        }
    }
    public string StatusText => IsBreakpointPending
        ? LocalizationService.Translate("Breakpoint", Services.Settings.Language)
        : _status?.ToString() ?? (Outcome == "Tunnel" ? "—" : "…");

    [JsonIgnore]
    public string BreakpointEditText => LocalizationService.Translate("Modifier", Services.Settings.Language);

    private string _outcome = "";
    public string Outcome
    {
        get => _outcome;
        set { if (Set(ref _outcome, value)) { Raise(nameof(StatusBrush)); Raise(nameof(StatusText)); Raise(nameof(OutcomeText)); } }
    }

    public string OutcomeText => LocalizationService.Translate(_outcome, Services.Settings.Language);
    public void RefreshLocalizedValues()
    {
        Raise(nameof(OutcomeText));
        Raise(nameof(StatusText));
        Raise(nameof(BreakpointEditText));
    }

    private string _rule = "";
    public string RuleName { get => _rule; set => Set(ref _rule, value); }
    public void AddRule(string name) => RuleName = string.IsNullOrEmpty(_rule) ? name : _rule + ", " + name;

    private long _size = -1;
    public long Size { get => _size; set { if (Set(ref _size, value)) Raise(nameof(SizeText)); } }
    public string SizeText => Fmt.Size(_size);

    private int _dur = -1;
    public int DurationMs { get => _dur; set { if (Set(ref _dur, value)) Raise(nameof(DurationText)); } }
    public string DurationText => _dur < 0 ? "" : $"{_dur} ms";

    private string _resHeaders = "";
    public string ResponseHeaders { get => _resHeaders; set => Set(ref _resHeaders, value); }

    private string _resCt = "";
    public string ResponseContentType { get => _resCt; set { if (Set(ref _resCt, value)) Raise(nameof(ResponseBodyView)); } }
    private bool _responseIsBinary;
    public bool ResponseIsBinary { get => _responseIsBinary; set => Set(ref _responseIsBinary, value); }
    private bool _responsePreviewUnavailable;
    public bool ResponsePreviewUnavailable { get => _responsePreviewUnavailable; set => Set(ref _responsePreviewUnavailable, value); }
    private byte[]? _responseBodyBytes;
    public byte[]? ResponseBodyBytes { get => _responseBodyBytes; set => Set(ref _responseBodyBytes, value); }
    private long? _responseBinaryLength;
    public long? ResponseBinaryLength { get => _responseBinaryLength; set => Set(ref _responseBinaryLength, value); }
    public byte[]? ResponsePayloadPreview { get; set; }
    public long? ResponsePayloadLength { get; set; }
    [JsonIgnore]
    public byte[]? ResponseReplayBody { get; set; }

    private string _resBody = "";
    public string ResponseBody { get => _resBody; set { if (Set(ref _resBody, value)) Raise(nameof(ResponseBodyView)); } }
    public string ResponseBodyView => Fmt.Pretty(_resCt, _resBody);

    public Brush MethodBrush => Method switch
    {
        "GET" => Green,
        "POST" => Orange,
        "PUT" or "PATCH" => Yellow,
        "DELETE" => Red,
        "CONNECT" => Gray,
        _ => Violet
    };

    public Brush StatusBrush => IsBreakpointPending ? Violet : Outcome switch
    {
        "Bloqué" or "Erreur" => Red,
        "Mock" or "Redirigé" => Violet,
        _ => _status switch { null => Gray, >= 500 => Red, >= 400 => Orange, >= 300 => Yellow, _ => Green }
    };
}

public sealed record TrafficExchangeUpdate
{
    public long Id { get; init; }
    public int Pid { get; init; }
    public DateTime Time { get; init; }
    public string Method { get; init; } = "";
    public string Host { get; init; } = "";
    public string Path { get; init; } = "";
    public string Url { get; init; } = "";
    public string RequestBody { get; init; } = "";
    public bool RequestIsBinary { get; init; }
    public bool RequestPreviewUnavailable { get; init; }
    public byte[]? RequestPayloadPreview { get; init; }
    public long? RequestPayloadLength { get; init; }
    public string ResponseBody { get; init; } = "";
    public bool ResponseIsBinary { get; init; }
    public bool ResponsePreviewUnavailable { get; init; }
    public byte[]? ResponsePayloadPreview { get; init; }
    public long? ResponsePayloadLength { get; init; }
    public int? StatusCode { get; init; }
    public string Outcome { get; init; } = "";
    public bool Completed { get; init; }
}

public sealed class ConnectionRow : ObservableObject
{
    public string Key { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string Process { get; init; } = "";
    public string? ProcessPath { get; init; }
    public int Pid { get; init; }
    public string Local { get; init; } = "";
    public string Remote { get; init; } = "";
    public DateTime Since { get; init; } = DateTime.Now;
    public string SinceText => Since.ToString("HH:mm:ss");
    private ImageSource? _processIcon;
    public ImageSource? ProcessIcon { get => _processIcon; set => Set(ref _processIcon, value); }

    private string _state = "";
    public string State { get => _state; set => Set(ref _state, value); }
}

public sealed class FirewallEntry : ObservableObject
{
    public string Program { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Program);
    [JsonIgnore] public string CreatedText => CreatedAt.ToString("yyyy-MM-dd HH:mm");
}

public sealed class AppSettings
{
    public int Port { get; set; } = 8866;
    public bool AutoCapture { get; set; }
    public bool SetEnvVars { get; set; } = true;
    public bool DiscordPresence { get; set; } = true;
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "Dark";
    public string BypassHosts { get; set; } = "";
    public string? CaThumbprint { get; set; }
    public int TrafficBodyBudgetMb { get; set; } = 200;

    [JsonIgnore]
    public IEnumerable<string> BypassList =>
        BypassHosts.Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);

    public static AppSettings Load() => JsonUtil.LoadOrDefault(AppPaths.Settings, () => new AppSettings());
    public void Save() => JsonUtil.Save(AppPaths.Settings, this);
}

public sealed class ProxyBackup
{
    public int ProxyEnable { get; set; }
    public string? ProxyServer { get; set; }
    public string? ProxyOverride { get; set; }
    public string? AutoConfigUrl { get; set; }
}
