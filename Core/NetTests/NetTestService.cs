using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Core;

public sealed class NetTestService : IDisposable
{
    private const int MaxResponseBytes = 10 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();
    private readonly ConcurrentBag<string> _responseFiles = new();
    public string? LoadWarning { get; private set; }

    public List<NetTestTab> LoadTabs()
    {
        AppPaths.Ensure();
        var path = AppPaths.NetTestTabs;
        if (!File.Exists(path)) return new List<NetTestTab> { CreatePlaceholderTab() };
        try
        {
            try
            {
                var tabs = System.Text.Json.JsonSerializer.Deserialize<List<NetTestTab>>(
                    File.ReadAllText(path), JsonUtil.Options);
                return tabs ?? new List<NetTestTab> { CreatePlaceholderTab() };
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
            {
                var backup = path + ".bak";
                try { File.Move(path, backup, true); }
                catch (Exception moveError)
                {
                    LoadWarning = LocalizationService.Format(
                        "Request tabs file is corrupted and could not be backed up: {0}",
                        Services.Settings.Language, moveError.Message);
                    return new List<NetTestTab> { CreatePlaceholderTab() };
                }
                LoadWarning = LocalizationService.Format(
                    "Request tabs file was corrupted; a copy was saved to {0}.",
                    Services.Settings.Language, backup);
                return new List<NetTestTab> { CreatePlaceholderTab() };
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(LocalizationService.Translate(
                "Unable to read Network tests tabs.", Services.Settings.Language), ex);
        }
    }

    private static NetTestTab CreatePlaceholderTab() => new()
    {
        Name = LocalizationService.Translate("New request", Services.Settings.Language),
        IsPlaceholder = true
    };

    public void SaveTabs(IReadOnlyCollection<NetTestTab> tabs)
    {
        AppPaths.Ensure();
        var path = AppPaths.NetTestTabs;
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";
        File.WriteAllText(tempPath, System.Text.Json.JsonSerializer.Serialize(tabs, JsonUtil.Options));
        if (File.Exists(path))
        {
            try { File.Replace(tempPath, path, backupPath, true); }
            catch (PlatformNotSupportedException) { File.Copy(path, backupPath, true); File.Move(tempPath, path, true); }
        }
        else File.Move(tempPath, path);
    }

    public async Task<NetTestResult> SendAsync(
        NetTestTab tab, NetTestSendOptions options, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(tab.Url, UriKind.Absolute, out var initialUri)
            || initialUri.Scheme is not ("http" or "https"))
            throw new ArgumentException(LocalizationService.Translate(
                "Enter a valid HTTP or HTTPS URL.", Services.Settings.Language));
        if (options.TimeoutSeconds is < 1 or > 600)
            throw new ArgumentOutOfRangeException(nameof(options), LocalizationService.Translate(
                "Timeout must be between 1 and 600 seconds.", Services.Settings.Language));
        if (options.MaxRedirects is < 0 or > 50)
            throw new ArgumentOutOfRangeException(nameof(options), LocalizationService.Translate(
                "Redirect limit must be between 0 and 50.", Services.Settings.Language));
        if (options.UseHttpFreeKitProxy && !Services.Proxy.IsRunning)
            throw new InvalidOperationException(LocalizationService.Translate(
                "Proxy is stopped. Start capture before using it.", Services.Settings.Language));

        var unresolved = Regex.Matches(tab.Url, @"\{\{([^{}]+)\}\}");
        if (unresolved.Count != 0)
            throw new InvalidOperationException(LocalizationService.Translate(
                "Unresolved variables in URL: ", Services.Settings.Language)
                + string.Join(", ", unresolved.Select(m => m.Groups[1].Value).Distinct()));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var started = Stopwatch.GetTimestamp();
        var redirects = new List<string>();
        var method = new HttpMethod(tab.Method.Trim().ToUpperInvariant());
        var explicitContentType = tab.Headers.FirstOrDefault(header => header.Enabled
            && header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        var body = tab.GetBodyBytes(explicitContentType ?? tab.ContentType);
        bool sendBody = body.Length > 0 || tab.BodyText.Length > 0 || tab.BodyBase64 is not null;
        var uri = initialUri;
        bool stripSensitiveHeaders = false;
        bool stripContentHeaders = false;
        HttpResponseMessage? response = null;
        try
        {
            for (int hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(method, uri)
                {
                    Version = options.HttpVersion switch
                    {
                        1 => HttpVersion.Version11,
                        2 => HttpVersion.Version20,
                        _ => HttpVersion.Version20
                    },
                    VersionPolicy = options.HttpVersion == 0
                        ? HttpVersionPolicy.RequestVersionOrLower : HttpVersionPolicy.RequestVersionExact
                };
                if (sendBody)
                    request.Content = new ByteArrayContent(body);
                foreach (var header in tab.Headers.Where(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Name)
                             && !(stripSensitiveHeaders && IsSensitiveHeader(h.Name))
                             && !(stripContentHeaders && IsContentHeader(h.Name))
                             && !(hop > 0 && h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase))))
                    AddHeader(request, header.Name.Trim(), header.Value);
                if (request.Content is not null && !string.IsNullOrWhiteSpace(tab.ContentType)
                    && !request.Content.Headers.Contains("Content-Type"))
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", tab.ContentType);

                var key = $"{options.UseHttpFreeKitProxy}:{options.IgnoreCertificateErrors}:{Services.Settings.Port}";
                var client = Clients.GetOrAdd(key, _ => CreateClient(options));
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);

                if (!options.FollowRedirects || !IsRedirect(response.StatusCode)
                    || response.Headers.Location is null)
                    break;
                if (hop >= options.MaxRedirects)
                    throw new HttpRequestException(LocalizationService.Format(
                        "Maximum of {0} redirects reached.", Services.Settings.Language, options.MaxRedirects));

                var nextUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                redirects.Add($"{(int)response.StatusCode} {uri} → {nextUri}");
                bool switchToGet = response.StatusCode == HttpStatusCode.SeeOther && method != HttpMethod.Head
                    || (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                        && method == HttpMethod.Post);
                if (!string.Equals(uri.Authority, nextUri.Authority, StringComparison.OrdinalIgnoreCase))
                    stripSensitiveHeaders = true;
                response.Dispose();
                response = null;
                if (switchToGet)
                {
                    method = HttpMethod.Get;
                    body = Array.Empty<byte>();
                    sendBody = false;
                    stripContentHeaders = true;
                }
                uri = nextUri;
            }

            if (response is null) throw new HttpRequestException(LocalizationService.Translate(
                "No HTTP response was received.", Services.Settings.Language));
            var responseBody = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            _responseFiles.Add(responseBody.FilePath);
            return new NetTestResult
            {
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase ?? "",
                Version = response.Version.ToString(),
                FinalUri = uri,
                Duration = Stopwatch.GetElapsedTime(started),
                Redirects = redirects,
                Headers = response.Headers.Concat(response.Content.Headers)
                    .Select(h => new KeyValuePair<string, string[]>(h.Key, h.Value.ToArray())).ToArray(),
                Body = responseBody.Bytes,
                BodyTruncated = responseBody.Truncated,
                ContentType = response.Content.Headers.ContentType?.ToString() ?? "",
                BodyFilePath = responseBody.FilePath
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(LocalizationService.Format(
                "Request timed out after {0} seconds.", Services.Settings.Language, options.TimeoutSeconds));
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new HttpRequestException(LocalizationService.Translate(
                "TLS certificate validation failed. Check the server certificate or explicitly enable the ignore option for this request.",
                Services.Settings.Language), ex);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException socket)
        {
            throw new HttpRequestException(LocalizationService.Format(
                "Connection failed ({0}): {1}", Services.Settings.Language,
                socket.SocketErrorCode, socket.Message), ex);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static HttpClient CreateClient(NetTestSendOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = options.UseHttpFreeKitProxy
        };
        if (options.UseHttpFreeKitProxy)
        {
            if (!Services.Proxy.IsRunning)
                throw new InvalidOperationException(LocalizationService.Translate(
                    "Proxy is stopped. Start capture before using it.", Services.Settings.Language));
            handler.Proxy = new WebProxy(IPAddress.Loopback.ToString(), Services.Settings.Port);
        }
        if (options.IgnoreCertificateErrors)
            handler.SslOptions.RemoteCertificateValidationCallback =
                (_, _, _, _) => true;
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static void AddHeader(HttpRequestMessage request, string name, string value)
    {
        if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Host = value;
            return;
        }
        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(LocalizationService.Translate(
                "Content-Length is calculated automatically; remove this request header.", Services.Settings.Language));
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Language", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Location", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Range", StringComparison.OrdinalIgnoreCase))
        {
            request.Content ??= new ByteArrayContent(Array.Empty<byte>());
            if (!request.Content.Headers.TryAddWithoutValidation(name, value))
                throw new FormatException(LocalizationService.Format(
                    "Invalid value for header “{0}”.", Services.Settings.Language, name));
            return;
        }
        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            request.Content ??= new ByteArrayContent(Array.Empty<byte>());
            if (!request.Content.Headers.TryAddWithoutValidation(name, value))
                throw new FormatException(LocalizationService.Format(
                    "Invalid or unsupported header: “{0}”.", Services.Settings.Language, name));
        }
    }

    private static bool IsRedirect(HttpStatusCode code)
        => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsSensitiveHeader(string name)
        => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);

    private static bool IsContentHeader(string name)
        => name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase);

    private async Task<(byte[] Bytes, bool Truncated, string FilePath)> ReadLimitedAsync(
        HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var directory = Path.Combine(Path.GetTempPath(), "HttpFreeKit", "nettests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.body");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        bool truncated = false;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                int keep = (int)Math.Min(read, MaxResponseBytes - output.Length);
                if (keep > 0) output.Write(buffer, 0, keep);
                if (keep < read) truncated = true;
            }
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            return (output.ToArray(), truncated, path);
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var client in Clients.Values) client.Dispose();
        Clients.Clear();
        while (_responseFiles.TryTake(out var path))
        {
            try { File.Delete(path); }
            catch (Exception ex) { Debug.WriteLine($"Unable to delete temporary network-test body: {ex.Message}"); }
        }
    }
}
