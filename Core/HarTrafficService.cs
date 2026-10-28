using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HttpFreeKit.Core;

public sealed class HarTrafficService
{
    private const long MaximumHarFileBytes = 128L * 1024 * 1024;
    private const int MaximumEntries = 50_000;
    private const int MaximumBodyBytesPerEntry = 32 * 1024 * 1024;
    private const long MaximumDecodedBodyBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<TrafficEntry> Import(string filePath)
    {
        var file = new FileInfo(filePath);
        if (file.Length > MaximumHarFileBytes)
            throw new InvalidDataException("The HAR file exceeds the 128 MB import limit.");

        using var stream = file.OpenRead();
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("log", out var log)
            || log.ValueKind != JsonValueKind.Object
            || !log.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The file is not a valid HAR archive with a log.entries array.");

        if (entries.GetArrayLength() > MaximumEntries)
            throw new InvalidDataException($"The HAR archive exceeds the {MaximumEntries:N0} entry limit.");

        var imported = new List<TrafficEntry>(entries.GetArrayLength());
        long decodedBodyBytes = 0;
        int index = 0;
        foreach (var item in entries.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
                throw EntryError(index, "entry must be an object");

            try
            {
                var request = RequiredObject(item, "request");
                var response = OptionalObject(item, "response");
                string url = RequiredString(request, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https"))
                    throw new InvalidDataException("request.url must be an absolute HTTP or HTTPS URL");

                string method = RequiredString(request, "method");
                if (method.Length == 0)
                    throw new InvalidDataException("request.method cannot be empty");

                string requestHeaders = ReadHeaders(request, "headers");
                string responseHeaders = ReadHeaders(response, "headers");
                var metadata = OptionalObject(item, "_httpFreeKit");

                var postData = OptionalObject(request, "postData");
                string requestMimeType = OptionalString(postData, "mimeType");
                if (requestMimeType.Length == 0)
                    requestMimeType = HeaderValue(requestHeaders, "Content-Type");
                bool requestBodyIsBase64 = string.Equals(
                    OptionalString(postData, "_httpFreeKitEncoding"), "base64", StringComparison.OrdinalIgnoreCase);
                byte[]? requestBody = ReadBody(
                    postData, "text",
                    OptionalString(postData, "_httpFreeKitEncoding"),
                    requestMimeType,
                    MaximumBodyBytesPerEntry, ref decodedBodyBytes);
                var content = OptionalObject(response, "content");
                string responseMimeType = OptionalString(content, "mimeType");
                if (responseMimeType.Length == 0)
                    responseMimeType = HeaderValue(responseHeaders, "Content-Type");
                byte[]? responseBody = ReadBody(
                    content, "text", OptionalString(content, "encoding"),
                    responseMimeType,
                    MaximumBodyBytesPerEntry, ref decodedBodyBytes);
                bool responseBodyIsBase64 = string.Equals(
                    OptionalString(content, "encoding"), "base64", StringComparison.OrdinalIgnoreCase);
                bool requestIsBinary = requestBody is not null
                    && (requestBodyIsBase64 || IsBinary(requestMimeType, requestBody));
                bool responseIsBinary = responseBody is not null
                    && (responseBodyIsBase64 || IsBinary(responseMimeType, responseBody));
                long responseSize = OptionalInt64(
                    content, "size", OptionalInt64(response, "bodySize", responseBody?.LongLength ?? -1));
                if (responseSize < 0 && responseBody is not null)
                    responseSize = responseBody.LongLength;
                bool requestBodyMissing = requestBody is null && OptionalInt64(request, "bodySize", 0) > 0;
                bool responseBodyMissing = responseBody is null && responseSize > 0;

                string host = uri.IsDefaultPort ? uri.Host : uri.Authority;
                string process = OptionalString(metadata, "process");
                int pid = OptionalInt32(metadata, "pid");
                DateTime time = ReadDate(item);
                int? status = OptionalInt32(response, "status") is int code && code > 0 ? code : null;
                int duration = Math.Max(0, (int)Math.Clamp(
                    OptionalDouble(item, "time"), 0, int.MaxValue));

                imported.Add(new TrafficEntry
                {
                    Time = time,
                    Pid = pid,
                    Process = process,
                    ProcessPath = OptionalNullableString(metadata, "processPath"),
                    Method = method,
                    Scheme = uri.Scheme,
                    Host = host,
                    HostName = uri.Host,
                    Path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery,
                    Url = url,
                    RequestHeaders = requestHeaders,
                    RequestContentType = requestMimeType,
                    RequestBody = requestBody is null || requestIsBinary
                        ? ""
                        : DecodeBody(requestBody, requestMimeType),
                    RequestBodyBytes = requestBody is { } requestBytes && requestIsBinary
                        ? requestBytes.Take(8192).ToArray()
                        : null,
                    RequestReplayBody = requestBody,
                    RequestPayloadPreview = requestBody?.Take(8192).ToArray(),
                    RequestPayloadLength = requestBody?.LongLength ?? OptionalNullableInt64(request, "bodySize"),
                    RequestBinaryLength = requestBody?.LongLength ?? OptionalNullableInt64(request, "bodySize"),
                    RequestIsBinary = requestIsBinary,
                    RequestBodyIncomplete = requestBodyMissing || OptionalBoolean(metadata, "requestBodyIncomplete"),
                    RequestPreviewUnavailable = requestBodyMissing || OptionalBoolean(metadata, "requestPreviewUnavailable"),
                    ResponseHeaders = responseHeaders,
                    ResponseContentType = responseMimeType,
                    ResponseBody = responseBody is null || responseIsBinary
                        ? ""
                        : Fmt.Truncate(DecodeBody(responseBody, responseMimeType)),
                    ResponseBodyBytes = responseBody is { } responseBytes && responseIsBinary
                        ? responseBytes.Take(8192).ToArray()
                        : null,
                    ResponseReplayBody = responseBody,
                    ResponsePayloadPreview = responseBody?.Take(8192).ToArray(),
                    ResponsePayloadLength = responseBody?.LongLength
                        ?? OptionalNullableInt64(content, "size"),
                    ResponseBinaryLength = responseBody?.LongLength
                        ?? OptionalNullableInt64(content, "size"),
                    ResponseIsBinary = responseIsBinary,
                    ResponsePreviewUnavailable = responseBodyMissing || OptionalBoolean(metadata, "responsePreviewUnavailable"),
                    ResponseBodyIncomplete = responseBodyMissing || OptionalBoolean(metadata, "responseTruncated"),
                    HarStatusText = OptionalString(response, "statusText"),
                    HarRequestVersion = OptionalString(request, "httpVersion"),
                    HarResponseVersion = OptionalString(response, "httpVersion"),
                    HarRedirectUrl = OptionalString(response, "redirectURL"),
                    StatusCode = status,
                    DurationMs = duration,
                    Size = responseSize,
                    Outcome = OptionalString(metadata, "outcome"),
                    RuleName = OptionalString(metadata, "ruleName"),
                    StartTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
                    Completed = OptionalBoolean(metadata, "completed", fallback: true),
                    IsHarImported = true,
                    RequestBodyLoading = false,
                    ResponseHeadersLoading = false,
                    ResponseLoading = false
                });
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException
                                       or ArgumentException or JsonException)
            {
                throw EntryError(index, ex.Message);
            }
        }

        return imported;
    }

    public Task ExportAsync(
        string filePath, IReadOnlyCollection<TrafficEntry> entries, bool redactSensitiveHeaders)
    {
        var harEntries = entries.Select(entry => ToHarEntry(entry, redactSensitiveHeaders)).ToArray();
        var archive = new HarArchive
        {
            Log = new HarLog
            {
                Creator = new HarCreator { Name = "HTTP FreeKit", Version = "1.0" },
                Version = "1.2",
                Entries = harEntries
            }
        };

        return Task.Run(() => WriteArchive(filePath, archive));
    }

    private static void WriteArchive(string filePath, HarArchive archive)
    {
        string temporaryPath = filePath + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, archive, JsonOptions);

            if (File.Exists(filePath))
            {
                try { File.Replace(temporaryPath, filePath, null, true); }
                catch (PlatformNotSupportedException) { File.Move(temporaryPath, filePath, true); }
            }
            else
                File.Move(temporaryPath, filePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static HarEntry ToHarEntry(TrafficEntry entry, bool redactSensitiveHeaders)
    {
        byte[]? requestBody = entry.RequestReplayBody
            ?? entry.RequestBodyBytes
            ?? (entry.RequestBody.Length > 0 ? Encoding.UTF8.GetBytes(entry.RequestBody) : null);
        byte[]? responseBody = entry.ResponseReplayBody
            ?? (entry.ResponseIsBinary
                ? entry.ResponsePayloadPreview ?? entry.ResponseBodyBytes
                : entry.ResponseBody.Length > 0
                    ? Encoding.UTF8.GetBytes(RemoveTruncationNotice(entry.ResponseBody))
                    : entry.ResponsePayloadPreview ?? entry.ResponseBodyBytes);
        bool requestBinary = entry.RequestIsBinary || requestBody is not null && IsBinary(entry.RequestContentType, requestBody);
        bool responseBinary = entry.ResponseIsBinary || responseBody is not null && IsBinary(entry.ResponseContentType, responseBody);

        return new HarEntry
        {
            StartedDateTime = (entry.Time == default ? DateTime.Now : entry.Time).ToUniversalTime().ToString("O"),
            Time = Math.Max(0, entry.DurationMs),
            Request = new HarRequest
            {
                Method = string.IsNullOrWhiteSpace(entry.Method) ? "GET" : entry.Method,
                Url = entry.Url,
                HttpVersion = string.IsNullOrWhiteSpace(entry.HarRequestVersion) ? "HTTP/1.1" : entry.HarRequestVersion,
                Headers = ParseHeaders(entry.RequestHeaders, redactSensitiveHeaders),
                HeadersSize = -1,
                BodySize = requestBody?.LongLength ?? -1,
                PostData = requestBody is null ? null : new HarPostData
                {
                    MimeType = entry.RequestContentType,
                    Text = requestBinary ? Convert.ToBase64String(requestBody) : DecodeBody(requestBody, entry.RequestContentType),
                    HttpFreeKitEncoding = requestBinary ? "base64" : null
                }
            },
            Response = new HarResponse
            {
                Status = entry.StatusCode ?? 0,
                StatusText = string.IsNullOrEmpty(entry.HarStatusText)
                    ? entry.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? ""
                    : entry.HarStatusText,
                HttpVersion = string.IsNullOrWhiteSpace(entry.HarResponseVersion) ? "HTTP/1.1" : entry.HarResponseVersion,
                Headers = ParseHeaders(entry.ResponseHeaders, redactSensitiveHeaders),
                HeadersSize = -1,
                BodySize = responseBody?.LongLength ?? entry.Size,
                RedirectUrl = entry.HarRedirectUrl
                    ?? (entry.StatusCode is >= 300 and < 400 ? HeaderValue(entry.ResponseHeaders, "Location") : ""),
                Content = new HarContent
                {
                    Size = entry.Size >= 0 ? entry.Size : responseBody?.LongLength ?? -1,
                    MimeType = entry.ResponseContentType,
                    Text = responseBody is null
                        ? null
                        : responseBinary ? Convert.ToBase64String(responseBody) : DecodeBody(responseBody, entry.ResponseContentType),
                    Encoding = responseBody is not null && responseBinary ? "base64" : null
                }
            },
            Cache = new Dictionary<string, object>(),
            Timings = new HarTimings { Send = -1, Wait = Math.Max(0, entry.DurationMs), Receive = -1 },
            HttpFreeKit = new HarMetadata
            {
                Process = entry.Process,
                ProcessPath = entry.ProcessPath,
                Pid = entry.Pid,
                Outcome = entry.Outcome,
                RuleName = entry.RuleName,
                RequestBodyIncomplete = entry.RequestBodyIncomplete,
                RequestPreviewUnavailable = entry.RequestPreviewUnavailable,
                ResponsePreviewUnavailable = entry.ResponsePreviewUnavailable,
                ResponseTruncated = entry.ResponseBodyIncomplete || IsTruncated(entry.ResponseBody)
                    || entry.ResponsePayloadLength is long length && responseBody is not null && length > responseBody.LongLength,
                Completed = entry.Completed
            }
        };
    }

    private static byte[]? ReadBody(
        JsonElement element, string propertyName, string encoding, string mimeType,
        int bodyLimit, ref long totalBytes)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var text))
            return null;
        if (text.ValueKind == JsonValueKind.Null)
            return null;
        if (text.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{propertyName} must be a string");

        string? value = text.GetString();
        if (value is null)
            return null;
        if (Encoding.UTF8.GetByteCount(value) > bodyLimit * 2L)
            throw new InvalidDataException("A request or response body exceeds the 32 MB per-body limit");

        byte[] bytes;
        try
        {
            bytes = string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(value)
                : GetBodyEncoding(mimeType).GetBytes(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("A body marked as base64 contains invalid data", ex);
        }

        if (bytes.Length > bodyLimit)
            throw new InvalidDataException("A request or response body exceeds the 32 MB per-body limit");
        totalBytes += bytes.LongLength;
        if (totalBytes > MaximumDecodedBodyBytes)
            throw new InvalidDataException("The decoded HAR bodies exceed the 256 MB import limit");
        return bytes;
    }

    private static string ReadHeaders(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(propertyName, out var headers))
            return "";
        if (headers.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{propertyName} must be an array");

        var lines = new List<string>();
        foreach (var header in headers.EnumerateArray())
        {
            if (header.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{propertyName} contains a non-object header");
            string name = RequiredString(header, "name");
            string value = RequiredString(header, "value");
            if (name.Length == 0 || name.Contains(':') || name.Contains('\r') || name.Contains('\n')
                || value.Contains('\r') || value.Contains('\n'))
                throw new InvalidDataException($"{propertyName} contains an invalid header name or value");
            lines.Add($"{name}: {value}");
        }
        return string.Join("\r\n", lines);
    }

    private static List<HarHeader> ParseHeaders(string? headers, bool redactSensitiveHeaders)
    {
        var result = new List<HarHeader>();
        if (string.IsNullOrWhiteSpace(headers))
            return result;

        foreach (string line in headers.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            result.Add(new HarHeader
            {
                Name = line[..separator].Trim(),
                Value = redactSensitiveHeaders && IsSensitiveHeader(line[..separator].Trim())
                    ? "[REDACTED]"
                    : line[(separator + 1)..].Trim()
            });
        }
        return result;
    }

    private static bool IsSensitiveHeader(string name)
    {
        string normalized = name.Trim().ToLowerInvariant().Replace('_', '-');
        return normalized is "authorization" or "proxy-authorization" or "www-authenticate"
            or "cookie" or "cookie2" or "set-cookie" or "set-cookie2"
            or "api-key" or "apikey" or "x-api-key" or "api-token" or "x-api-token"
            or "x-goog-api-key" or "ocp-apim-subscription-key" or "subscription-key"
            or "x-auth-token" or "x-auth-key" or "x-authentication-token"
            or "x-access-token" or "access-token" or "auth-token"
            or "authentication" or "x-csrf-token" or "x-xsrf-token" or "x-amz-security-token"
            or "x-amz-credential" or "client-secret" or "x-client-secret" or "x-client-token"
            or "api-secret" or "x-api-secret" or "secret-key" or "x-secret-key"
            || normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("api-key", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("api-token", StringComparison.Ordinal)
            || normalized.Contains("access-token", StringComparison.Ordinal)
            || normalized.Contains("auth-token", StringComparison.Ordinal)
            || normalized.Contains("client-secret", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal)
            || normalized.Contains("subscription-key", StringComparison.Ordinal);
    }

    private static string HeaderValue(string headers, string name)
        => headers.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (Line: line, Separator: line.IndexOf(':')))
            .Where(item => item.Separator > 0
                && item.Line[..item.Separator].Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Line[(item.Separator + 1)..].Trim())
            .FirstOrDefault() ?? "";

    private static bool IsBinary(string mimeType, byte[] body)
        => string.IsNullOrWhiteSpace(mimeType) ? !Fmt.LooksText(body) : !Fmt.IsText(mimeType);

    private static string DecodeBody(byte[] body, string mimeType)
        => GetBodyEncoding(mimeType).GetString(body);

    private static Encoding GetBodyEncoding(string mimeType)
    {
        string charset = "utf-8";
        int marker = mimeType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
            charset = mimeType[(marker + "charset=".Length)..].Split(';')[0].Trim().Trim('"', '\'');
        try { return Encoding.GetEncoding(charset); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    private static string RemoveTruncationNotice(string body)
    {
        int marker = body.LastIndexOf("\n… (truncated,", StringComparison.Ordinal);
        return marker >= 0 ? body[..marker] : body;
    }

    private static bool IsTruncated(string body)
        => body.Contains("\n… (truncated,", StringComparison.Ordinal);

    private static DateTime ReadDate(JsonElement entry)
    {
        string value = OptionalString(entry, "startedDateTime");
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToLocalTime()
            : DateTime.Now;
    }

    private static JsonElement RequiredObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{propertyName} must be an object");
        return value;
    }

    private static JsonElement OptionalObject(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return default;
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{propertyName} must be an object");
        return value;
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        string value = OptionalString(element, propertyName);
        if (value.Length == 0 && (!element.TryGetProperty(propertyName, out var property)
                                 || property.ValueKind != JsonValueKind.String))
            throw new InvalidDataException($"{propertyName} must be a string");
        return value;
    }

    private static string OptionalString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string? OptionalNullableString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int OptionalInt32(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.TryGetInt32(out int result)
            ? result
            : 0;

    private static long OptionalInt64(JsonElement element, string propertyName, long fallback)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.TryGetInt64(out long result)
            ? result
            : fallback;

    private static long? OptionalNullableInt64(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.TryGetInt64(out long result)
           && result >= 0
            ? result
            : null;

    private static double OptionalDouble(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.TryGetDouble(out double result)
           && double.IsFinite(result)
            ? result
            : 0;

    private static bool OptionalBoolean(JsonElement element, string propertyName, bool fallback = false)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
            ? value.ValueKind == JsonValueKind.True
            : fallback;

    private static InvalidDataException EntryError(int index, string message)
        => new($"Invalid HAR entry {index}: {message}");

    private sealed class HarArchive
    {
        [JsonPropertyName("log")] public HarLog Log { get; set; } = new();
    }

    private sealed class HarLog
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "1.2";
        [JsonPropertyName("creator")] public HarCreator Creator { get; set; } = new();
        [JsonPropertyName("entries")] public HarEntry[] Entries { get; set; } = Array.Empty<HarEntry>();
    }

    private sealed class HarCreator
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
    }

    private sealed class HarEntry
    {
        [JsonPropertyName("startedDateTime")] public string StartedDateTime { get; set; } = "";
        [JsonPropertyName("time")] public int Time { get; set; }
        [JsonPropertyName("request")] public HarRequest Request { get; set; } = new();
        [JsonPropertyName("response")] public HarResponse Response { get; set; } = new();
        [JsonPropertyName("cache")] public Dictionary<string, object> Cache { get; set; } = new();
        [JsonPropertyName("timings")] public HarTimings Timings { get; set; } = new();
        [JsonPropertyName("_httpFreeKit")] public HarMetadata HttpFreeKit { get; set; } = new();
    }

    private sealed class HarRequest
    {
        [JsonPropertyName("method")] public string Method { get; set; } = "GET";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("httpVersion")] public string HttpVersion { get; set; } = "HTTP/1.1";
        [JsonPropertyName("headers")] public List<HarHeader> Headers { get; set; } = new();
        [JsonPropertyName("queryString")] public List<HarHeader> QueryString { get; set; } = new();
        [JsonPropertyName("cookies")] public List<HarHeader> Cookies { get; set; } = new();
        [JsonPropertyName("headersSize")] public int HeadersSize { get; set; } = -1;
        [JsonPropertyName("bodySize")] public long BodySize { get; set; } = -1;
        [JsonPropertyName("postData")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HarPostData? PostData { get; set; }
    }

    private sealed class HarPostData
    {
        [JsonPropertyName("mimeType")] public string MimeType { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("_httpFreeKitEncoding")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HttpFreeKitEncoding { get; set; }
    }

    private sealed class HarResponse
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("statusText")] public string StatusText { get; set; } = "";
        [JsonPropertyName("httpVersion")] public string HttpVersion { get; set; } = "HTTP/1.1";
        [JsonPropertyName("headers")] public List<HarHeader> Headers { get; set; } = new();
        [JsonPropertyName("cookies")] public List<HarHeader> Cookies { get; set; } = new();
        [JsonPropertyName("content")] public HarContent Content { get; set; } = new();
        [JsonPropertyName("redirectURL")] public string RedirectUrl { get; set; } = "";
        [JsonPropertyName("headersSize")] public int HeadersSize { get; set; } = -1;
        [JsonPropertyName("bodySize")] public long BodySize { get; set; } = -1;
    }

    private sealed class HarContent
    {
        [JsonPropertyName("size")] public long Size { get; set; } = -1;
        [JsonPropertyName("mimeType")] public string MimeType { get; set; } = "";
        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Text { get; set; }
        [JsonPropertyName("encoding")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Encoding { get; set; }
    }

    private sealed class HarHeader
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("value")] public string Value { get; set; } = "";
    }

    private sealed class HarTimings
    {
        [JsonPropertyName("blocked")] public int Blocked { get; set; } = -1;
        [JsonPropertyName("dns")] public int Dns { get; set; } = -1;
        [JsonPropertyName("connect")] public int Connect { get; set; } = -1;
        [JsonPropertyName("send")] public int Send { get; set; } = -1;
        [JsonPropertyName("wait")] public int Wait { get; set; } = -1;
        [JsonPropertyName("receive")] public int Receive { get; set; } = -1;
        [JsonPropertyName("ssl")] public int Ssl { get; set; } = -1;
    }

    private sealed class HarMetadata
    {
        [JsonPropertyName("process")] public string Process { get; set; } = "";
        [JsonPropertyName("processPath")] public string? ProcessPath { get; set; }
        [JsonPropertyName("pid")] public int Pid { get; set; }
        [JsonPropertyName("outcome")] public string Outcome { get; set; } = "";
        [JsonPropertyName("ruleName")] public string RuleName { get; set; } = "";
        [JsonPropertyName("requestBodyIncomplete")] public bool RequestBodyIncomplete { get; set; }
        [JsonPropertyName("requestPreviewUnavailable")] public bool RequestPreviewUnavailable { get; set; }
        [JsonPropertyName("responsePreviewUnavailable")] public bool ResponsePreviewUnavailable { get; set; }
        [JsonPropertyName("responseTruncated")] public bool ResponseTruncated { get; set; }
        [JsonPropertyName("completed")] public bool Completed { get; set; } = true;
    }
}
