using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using HttpFreeKit.Views;
using HttpFreeKit.Localization;
using System.Windows;

namespace HttpFreeKit.Core;





public sealed class ProxyService : IDisposable
{
    public const string CaName = "HTTP FreeKit Root CA";
    public const string LegacyCaName = "PortableProxy Root CA";
    public const string CaPassword = "portableproxy";
    private const long MaxCapture = 1_000_000;
    private const string RedirectPidHeader = "X-HttpFreeKit-Pid";
    private const string RedirectTokenHeader = "X-HttpFreeKit-Token";

    private ProxyServer? _server;
    private ExplicitProxyEndPoint? _endpoint;
    private long _seq;
    private readonly object _exchangeSync = new();
    private readonly ConcurrentDictionary<long, PendingBreakpoint> _pendingBreakpoints = new();

    private sealed class PendingBreakpoint(
        string url,
        bool response,
        string headers,
        string body,
        int? statusCode,
        bool bodyEditable)
    {
        public string Url { get; } = url;
        public bool Response { get; } = response;
        public string Headers { get; } = headers;
        public string Body { get; } = body;
        public int? StatusCode { get; } = statusCode;
        public bool BodyEditable { get; } = bodyEditable;
        public TaskCompletionSource<BreakpointEdit?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _editing;

        public bool TryBeginEditing() => Interlocked.CompareExchange(ref _editing, 1, 0) == 0;
        public void EndEditing() => Volatile.Write(ref _editing, 0);
    }

    private sealed record RedirectedProcessContext(int Pid);

    public bool IsRunning => _server?.ProxyRunning == true;
    public event Action<TrafficExchangeUpdate>? ExchangeUpdated;

    public long SubscribeExchangeUpdates(Action<TrafficExchangeUpdate> handler)
    {
        lock (_exchangeSync)
        {
            ExchangeUpdated += handler;
            return _seq;
        }
    }

    public void UnsubscribeExchangeUpdates(Action<TrafficExchangeUpdate> handler)
        => ExchangeUpdated -= handler;

    private long NextExchangeId()
    {
        lock (_exchangeSync)
            return ++_seq;
    }

    public long ReserveExchangeIds(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        lock (_exchangeSync)
        {
            long firstId = _seq + 1;
            _seq += count;
            return firstId;
        }
    }

    private void NotifyExchangeUpdated(TrafficEntry entry)
    {
        var update = new TrafficExchangeUpdate
        {
            Id = entry.Id,
            Pid = entry.Pid,
            Time = entry.Time,
            Method = entry.Method,
            Host = entry.Host,
            Path = entry.Path,
            Url = entry.Url,
            RequestBody = entry.RequestBody,
            RequestIsBinary = entry.RequestIsBinary,
            RequestPreviewUnavailable = entry.RequestPreviewUnavailable,
            RequestPayloadPreview = entry.RequestPayloadPreview?.ToArray(),
            RequestPayloadLength = entry.RequestPayloadLength,
            ResponseBody = entry.ResponseBody,
            ResponseIsBinary = entry.ResponseIsBinary,
            ResponsePreviewUnavailable = entry.ResponsePreviewUnavailable,
            ResponsePayloadPreview = entry.ResponsePayloadPreview?.ToArray(),
            ResponsePayloadLength = entry.ResponsePayloadLength,
            StatusCode = entry.StatusCode,
            Outcome = entry.Outcome,
            Completed = entry.Completed
        };
        try { ExchangeUpdated?.Invoke(update); }
        catch (Exception ex) { Debug.WriteLine($"Failed to notify the hex inspector: {ex}"); }
    }



    private static ProxyServer CreateServer()
    {
        var s = new ProxyServer(false, false, false);
        var cm = s.CertificateManager;
        cm.CertificateEngine = CertificateEngine.BouncyCastle;
        cm.PfxFilePath = AppPaths.CaPfx;
        cm.PfxPassword = CaPassword;
        cm.RootCertificateName = CaName;
        cm.RootCertificateIssuerName = "HttpFreeKit";
        cm.SaveFakeCertificates = false;
        return s;
    }


    public bool EnsureCaTrusted()
    {
        AppPaths.Ensure();
        using var s = CreateServer();
        var cm = s.CertificateManager;
        cm.EnsureRootCertificate();

        var thumb = cm.RootCertificate?.Thumbprint;
        if (thumb != null && Services.Settings.CaThumbprint != thumb)
        {
            Services.Settings.CaThumbprint = thumb;
            Services.Settings.Save();
        }

        if (!cm.IsRootCertificateUserTrusted()) cm.TrustRootCertificate(false);
        bool trusted = cm.IsRootCertificateUserTrusted();


        if (trusted && thumb != null) { try { CertificateService.RemoveStale(thumb); } catch { } }
        return trusted;
    }



    public void Start(int port, (string Host, int Port)? upstream)
    {
        if (IsRunning) return;
        AppPaths.Ensure();

        var s = CreateServer();
        s.CertificateManager.EnsureRootCertificate();

        if (upstream is { } u)
        {
            var ext = new ExternalProxy(u.Host, u.Port);
            s.UpStreamHttpProxy = ext;
            s.UpStreamHttpsProxy = ext;
        }

        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, port, true);
        ep.BeforeTunnelConnectRequest += OnTunnel;
        s.BeforeRequest += OnRequest;
        s.BeforeResponse += OnResponse;
        s.ServerCertificateValidationCallback += OnServerCert;
        s.AddEndPoint(ep);

        try { s.Start(); }
        catch { try { s.Dispose(); } catch { } throw; }

        _server = s;
        _endpoint = ep;
    }

    public void Stop()
    {
        var s = _server; var ep = _endpoint;
        _server = null; _endpoint = null;
        foreach (var pending in _pendingBreakpoints.Values)
            pending.Completion.TrySetResult(null);
        if (s == null) return;
        try
        {
            if (ep != null) ep.BeforeTunnelConnectRequest -= OnTunnel;
            s.BeforeRequest -= OnRequest;
            s.BeforeResponse -= OnResponse;
            s.ServerCertificateValidationCallback -= OnServerCert;
        }
        catch { }
        try { if (s.ProxyRunning) s.Stop(); } catch { }
        try { s.Dispose(); } catch { }
    }

    public void Dispose() => Stop();



    private static int GetPid(SessionEventArgsBase e)
    {
        try
        {
            object p = e.HttpClient.ProcessId;
            if (p is Lazy<int> lazy) return lazy.Value;
            if (p is int i) return i;
        }
        catch { }
        return -1;
    }

    private static RedirectedProcessContext? ReadRedirectedProcessContext(
        Titanium.Web.Proxy.Http.Request? request)
    {
        if (request == null)
            return null;

        string? pidText = request.Headers.GetFirstHeader(RedirectPidHeader)?.Value;
        string? token = request.Headers.GetFirstHeader(RedirectTokenHeader)?.Value;
        request.Headers.RemoveHeader(RedirectPidHeader);
        request.Headers.RemoveHeader(RedirectTokenHeader);

        return int.TryParse(pidText, out int pid) && pid > 0 && Services.Redirector.IsValidToken(token)
            ? new RedirectedProcessContext(pid)
            : null;
    }

    public void RecordRawTunnel(int pid, IPAddress originAddress, int originPort)
    {
        string authority = originAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{originAddress}]:{originPort}"
            : $"{originAddress}:{originPort}";
        var process = ProcessInfo.Get(pid);
        var entry = new TrafficEntry
        {
            Id = NextExchangeId(),
            Time = DateTime.Now,
            Pid = pid,
            Process = process.Name,
            ProcessPath = process.Path,
            Method = "CONNECT",
            Scheme = "tcp",
            Host = authority,
            HostName = originAddress.ToString(),
            Path = "(undecrypted tunnel)",
            Url = $"tcp://{authority}",
            StartTicks = Stopwatch.GetTimestamp(),
            Outcome = "Tunnel",
            Completed = true
        };
        Services.Traffic.Enqueue(entry);
        NotifyExchangeUpdated(entry);
    }

    private static string HeadersText(IEnumerable<HttpHeader> headers)
        => string.Join("\r\n", headers.Select(h => $"{h.Name}: {h.Value}"));

    private static int ElapsedMs(TrafficEntry e)
        => (int)((Stopwatch.GetTimestamp() - e.StartTicks) * 1000 / Stopwatch.Frequency);

    private static void SetRequestPayloadPreview(TrafficEntry entry, byte[] payload)
    {
        entry.RequestPayloadLength = payload.LongLength;
        entry.RequestPayloadPreview = payload.Take(8192).ToArray();
    }

    private static void SetResponsePayloadPreview(TrafficEntry entry, byte[] payload)
    {
        entry.ResponsePayloadLength = payload.LongLength;
        entry.ResponsePayloadPreview = payload.Take(8192).ToArray();
    }

    private static string BlockPage(Rule rule, TrafficEntry e)
    {
        string language = Services.Settings.Language;
        string msg = string.IsNullOrWhiteSpace(rule.Body)
            ? LocalizationService.Translate("This request was blocked.", language)
            : rule.Body;
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>" +
               LocalizationService.Translate("Blocked", language) + "</title></head>" +
               "<body style=\"font-family:Segoe UI,sans-serif;background:#1b1b1f;color:#eee;display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
               "<div style=\"max-width:560px;padding:32px;border:1px solid #444;border-radius:12px;background:#25252b\">" +
               "<h2 style=\"margin-top:0;color:#ff6b6b\">⛔ " +
               LocalizationService.Translate("Blocked by HTTP FreeKit", language) + "</h2>" +
               $"<p>{WebUtility.HtmlEncode(msg)}</p>" +
               $"<p style=\"color:#999;font-size:13px\">{LocalizationService.Translate("Rule:", language)} {WebUtility.HtmlEncode(rule.Name)}<br>{WebUtility.HtmlEncode(e.Url)}</p>" +
               "</div></body></html>";
    }

    private static string ReplaceText(string input, Rule rule)
    {
        if (string.IsNullOrEmpty(rule.FindPattern)) return input;
        try
        {
            return rule.FindIsRegex
                ? Regex.Replace(input, rule.FindPattern, rule.ReplaceWith, RegexOptions.None, TimeSpan.FromSeconds(1))
                : input.Replace(rule.FindPattern, rule.ReplaceWith, StringComparison.Ordinal);
        }
        catch { return input; }
    }

    private static byte[] MockBytes(Rule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.BodyFile) && File.Exists(rule.BodyFile))
            return File.ReadAllBytes(rule.BodyFile);
        return Encoding.UTF8.GetBytes(rule.Body ?? "");
    }

    private async Task<BreakpointEdit?> WaitForBreakpointAsync(TrafficEntry entry, string url, bool response,
        string headers, string body, int? statusCode, bool bodyEditable)
    {
        var pending = new PendingBreakpoint(url, response, headers, body, statusCode, bodyEditable);
        if (!_pendingBreakpoints.TryAdd(entry.Id, pending))
            throw new InvalidOperationException(LocalizationService.Format(
                "A breakpoint is already pending for exchange {0}.", Services.Settings.Language, entry.Id));

        entry.IsBreakpointPending = true;
        entry.Outcome = "Breakpoint";
        if (Services.Traffic.Paused)
            Services.Traffic.EnqueueBreakpoint(entry);
        NotifyExchangeUpdated(entry);
        try
        {
            return await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingBreakpoints.TryRemove(entry.Id, out _);
            entry.IsBreakpointPending = false;
        }
    }

    public void EditPendingBreakpoint(long entryId)
    {
        if (!_pendingBreakpoints.TryGetValue(entryId, out var pending) || !pending.TryBeginEditing())
            return;

        try
        {
            var app = Application.Current
                ?? throw new InvalidOperationException(LocalizationService.Translate(
                    "The HTTP FreeKit interface is unavailable.", Services.Settings.Language));
            if (!app.Dispatcher.CheckAccess())
                throw new InvalidOperationException(LocalizationService.Translate(
                    "The breakpoint editor must be opened from the HTTP FreeKit interface.", Services.Settings.Language));

            var edit = BreakpointEditorWindow.Show(app.MainWindow, Services.Settings.Language,
                pending.Url, pending.Response, pending.Headers, pending.Body,
                pending.StatusCode, pending.BodyEditable);
            pending.Completion.TrySetResult(edit);
        }
        catch (Exception ex)
        {
            pending.EndEditing();
            HttpFreeKit.AppDialog.Show(Application.Current?.MainWindow, ex.Message, "HTTP FreeKit",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void ApplyEditedHeaders(IEnumerable<HttpHeader> originalHeaders,
        Action<string> removeHeader, Action<string, string> setHeader, string editedHeaders)
    {
        foreach (var name in originalHeaders.Select(header => header.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            removeHeader(name);

        foreach (var line in editedHeaders.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
                throw new FormatException(LocalizationService.Format(
                    "Invalid header: {0}", Services.Settings.Language, line));
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Length == 0)
                throw new FormatException(LocalizationService.Translate(
                    "A header name cannot be empty.", Services.Settings.Language));
            setHeader(name, value);
        }
    }


    private static void RespondGenerated(SessionEventArgs e, TrafficEntry entry, int code, byte[] body, string contentType, string outcome)
    {
        var headers = new Dictionary<string, HttpHeader>
        {
            ["Content-Type"] = new HttpHeader("Content-Type", contentType),
            ["Cache-Control"] = new HttpHeader("Cache-Control", "no-store"),
            ["X-Intercepted-By"] = new HttpHeader("X-Intercepted-By", "HttpFreeKit"),
        };
        var origin = e.HttpClient.Request.Headers.GetFirstHeader("Origin")?.Value;
        if (!string.IsNullOrEmpty(origin))
            headers["Access-Control-Allow-Origin"] = new HttpHeader("Access-Control-Allow-Origin", origin);

        e.GenericResponse(body, (HttpStatusCode)code, headers);

        entry.StatusCode = code;
        entry.ResponseContentType = contentType;
        entry.ResponseHeaders = LocalizationService.Translate(
            " (response generated by HTTP FreeKit)\r\nContent-Type: ", Services.Settings.Language) + contentType;
        if (Fmt.IsText(contentType))
        {
            entry.ResponseBody = Fmt.Truncate(Encoding.UTF8.GetString(body));
            entry.ResponseIsBinary = false;
        }
        else
        {
            entry.ResponseIsBinary = true;
            entry.ResponseBodyBytes = body.Take(8192).ToArray();
            entry.ResponseBinaryLength = body.Length;
            entry.ResponseBody = "";
        }
        SetResponsePayloadPreview(entry, body);
        entry.Size = body.Length;
        entry.DurationMs = ElapsedMs(entry);
        entry.Outcome = outcome;
        entry.Completed = true;
    }


    private static void ReplaceResponse(SessionEventArgs e, int code, byte[] body, string contentType)
    {
        var res = e.HttpClient.Response;
        res.StatusCode = code;
        res.StatusDescription = ((HttpStatusCode)code).ToString();
        res.Headers.RemoveHeader("Content-Encoding");
        res.Headers.SetOrAddHeaderValue("Content-Type", contentType);
        e.SetResponseBody(body);
    }



    private Task OnServerCert(object sender, CertificateValidationEventArgs e)
    {

        if (e.SslPolicyErrors == SslPolicyErrors.None) e.IsValid = true;
        return Task.CompletedTask;
    }

    private Task OnTunnel(object sender, TunnelConnectSessionEventArgs e)
    {
        try
        {
            var uri = e.HttpClient.Request.RequestUri;
            string host = uri.Host;
            var context = ReadRedirectedProcessContext(e.HttpClient.ConnectRequest);
            if (context != null)
                e.UserData = context;
            int pid = context?.Pid ?? GetPid(e);
            var pi = ProcessInfo.Get(pid);

            bool bypass = Services.Settings.BypassList.Any(p => Glob.IsMatch(p, host));
            string ruleName = bypass
                ? LocalizationService.Translate("Hosts excluded from HTTPS decryption (Settings)", Services.Settings.Language)
                : "";

            if (!bypass)
            {
                var ctx = new MatchContext
                {
                    Process = pi.Name, Host = host, Url = $"https://{host}/", Path = "/", Method = "CONNECT"
                };
                var rule = Services.Rules.Match(RulePhase.Request, ctx)
                    .FirstOrDefault(r => r.ActionType == RuleActionType.Passthrough);
                if (rule != null) { bypass = true; ruleName = rule.Name; rule.Hit(); }
            }

            if (bypass)
            {
                e.DecryptSsl = false;
                Services.Traffic.Enqueue(new TrafficEntry
                {
                    Id = NextExchangeId(),
                    Time = DateTime.Now,
                    Pid = pid, Process = pi.Name, ProcessPath = pi.Path,
                    Method = "CONNECT", Scheme = "tcp",
                    Host = uri.Authority, HostName = host, Path = "(undecrypted tunnel)", Url = $"https://{uri.Authority}",
                    StartTicks = Stopwatch.GetTimestamp(),
                    RuleName = ruleName,
                    Outcome = "Tunnel",
                    Completed = true
                });
            }
        }
        catch { }
        return Task.CompletedTask;
    }

    private async Task OnRequest(object sender, SessionEventArgs e)
    {
        TrafficEntry? entry = null;
        try
        {
            var req = e.HttpClient.Request;
            var uri = req.RequestUri;
            var context = e.UserData as RedirectedProcessContext ?? ReadRedirectedProcessContext(req);
            int pid = context?.Pid ?? GetPid(e);
            var pi = ProcessInfo.Get(pid);

            entry = new TrafficEntry
            {
                Id = NextExchangeId(),
                Time = DateTime.Now,
                Pid = pid, Process = pi.Name, ProcessPath = pi.Path,
                Method = req.Method,
                Scheme = uri.Scheme,
                Host = uri.IsDefaultPort ? uri.Host : uri.Authority,
                HostName = uri.Host,
                Path = uri.PathAndQuery,
                Url = req.Url,
                StartTicks = Stopwatch.GetTimestamp(),
                RequestHeaders = HeadersText(req.Headers.GetAllHeaders()),
                RequestContentType = req.ContentType ?? ""
            };
            e.UserData = entry;
            Services.Traffic.Enqueue(entry);
            NotifyExchangeUpdated(entry);

            try
            {
                await CaptureRequestBody(e, entry);
            }
            finally
            {
                entry.RequestBodyLoading = false;
            }
            NotifyExchangeUpdated(entry);

            var ctx = new MatchContext
            {
                Process = pi.Name, Host = uri.Host, Url = req.Url, Path = uri.PathAndQuery, Method = req.Method,
                RequestHeaders = entry.RequestHeaders, RequestBody = entry.RequestBody
            };

            foreach (var rule in Services.Rules.Match(RulePhase.Request, ctx))
            {
                switch (rule.ActionType)
                {
                    case RuleActionType.Delay:
                        rule.Hit(); entry.AddRule(rule.Name);
                        await Task.Delay(Math.Max(0, rule.DelayMs));
                        break;

                    case RuleActionType.SetHeader:
                        rule.Hit(); entry.AddRule(rule.Name);
                        if (!string.IsNullOrWhiteSpace(rule.HeaderName))
                            req.Headers.SetOrAddHeaderValue(rule.HeaderName, rule.HeaderValue);
                        entry.Outcome = "Modifié";
                        break;

                    case RuleActionType.RemoveHeader:
                        rule.Hit(); entry.AddRule(rule.Name);
                        if (!string.IsNullOrWhiteSpace(rule.HeaderName)) req.Headers.RemoveHeader(rule.HeaderName);
                        entry.Outcome = "Modifié";
                        break;

                    case RuleActionType.ReplaceBody:
                        if (req.HasBody && Fmt.IsText(req.ContentType))
                        {
                            rule.Hit(); entry.AddRule(rule.Name);
                            var txt = await e.GetRequestBodyAsString();
                            var nt = ReplaceText(txt, rule);
                            e.SetRequestBodyString(nt);
                            entry.RequestBody = Fmt.Truncate(nt);
                            SetRequestPayloadPreview(entry, Encoding.UTF8.GetBytes(nt));
                            entry.Outcome = "Modifié";
                        }
                        break;

                    case RuleActionType.Breakpoint:
                        rule.Hit(); entry.AddRule(rule.Name);
                        bool bodyEditable = !req.HasBody
                            || (Fmt.IsText(req.ContentType) && req.ContentLength is >= 0 and <= MaxCapture);
                        string body = req.HasBody && bodyEditable ? await e.GetRequestBodyAsString() : "";
                        var edit = await WaitForBreakpointAsync(entry, req.Url, false,
                            HeadersText(req.Headers.GetAllHeaders()), body, null, bodyEditable);
                        if (edit == null)
                        {
                            const string abandoned = "Request abandoned at breakpoint.";
                            RespondGenerated(e, entry, 502, Encoding.UTF8.GetBytes(abandoned),
                                "text/plain; charset=utf-8", "Abandonné");
                            return;
                        }

                        ApplyEditedHeaders(req.Headers.GetAllHeaders(),
                            name => { req.Headers.RemoveHeader(name); },
                            (name, value) => req.Headers.SetOrAddHeaderValue(name, value),
                            edit.Headers);
                        if (bodyEditable && edit.BodyChanged && (req.HasBody || edit.Body.Length > 0))
                        {
                            e.SetRequestBodyString(edit.Body);
                            entry.RequestBody = Fmt.Truncate(edit.Body);
                            SetRequestPayloadPreview(entry, Encoding.UTF8.GetBytes(edit.Body));
                        }
                        entry.RequestHeaders = HeadersText(req.Headers.GetAllHeaders());
                        entry.Outcome = "Modifié";
                        break;

                    case RuleActionType.RewriteUrl:
                        if (Uri.TryCreate(rule.TargetUrl, UriKind.Absolute, out var target))
                        {
                            rule.Hit(); entry.AddRule(rule.Name);
                            req.RequestUri = target;
                            req.Headers.SetOrAddHeaderValue("Host", target.Authority);
                            entry.Outcome = "Modifié";
                        }
                        break;

                    case RuleActionType.Redirect:
                        rule.Hit(); entry.AddRule(rule.Name);
                        e.Redirect(rule.TargetUrl);
                        entry.StatusCode = 302;
                        entry.ResponseHeaders = "Location: " + rule.TargetUrl;
                        entry.DurationMs = ElapsedMs(entry);
                        entry.Outcome = "Redirigé";
                        entry.Completed = true;
                        return;

                    case RuleActionType.Block:
                        rule.Hit(); entry.AddRule(rule.Name);
                        RespondGenerated(e, entry, rule.StatusCode,
                            Encoding.UTF8.GetBytes(BlockPage(rule, entry)), "text/html; charset=utf-8", "Bloqué");
                        return;

                    case RuleActionType.Mock:
                        rule.Hit(); entry.AddRule(rule.Name);
                        RespondGenerated(e, entry, rule.StatusCode, MockBytes(rule),
                            string.IsNullOrWhiteSpace(rule.ContentType) ? "text/plain; charset=utf-8" : rule.ContentType, "Mock");
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            if (entry != null)
            {
                entry.Outcome = "Erreur";
                entry.ResponseBody = ex.Message;
                entry.ResponseHeadersLoading = false;
                entry.ResponseLoading = false;
            }
        }
        finally
        {
            if (entry != null) NotifyExchangeUpdated(entry);
        }
    }

    private static async Task CaptureRequestBody(SessionEventArgs e, TrafficEntry entry)
    {
        var req = e.HttpClient.Request;
        if (!req.HasBody) return;
        long len = req.ContentLength;
        string ct = req.ContentType ?? "";
        try
        {

            bool readable = len >= 0 ? len <= MaxCapture : Fmt.IsText(ct);
            if (!readable)
            {
                entry.RequestIsBinary = !Fmt.IsText(ct);
                entry.RequestPreviewUnavailable = Fmt.IsText(ct);
                entry.RequestBinaryLength = len >= 0 ? len : null;
                entry.RequestPayloadLength = len >= 0 ? len : null;
                entry.RequestBodyIncomplete = true;
                return;
            }

            var bytes = await e.GetRequestBody();
            entry.RequestReplayBody = bytes.ToArray();
            SetRequestPayloadPreview(entry, bytes);
            if (Fmt.IsText(ct) && Fmt.LooksText(bytes))
                entry.RequestBody = Fmt.Truncate(Encoding.UTF8.GetString(bytes));
            else
            {
                entry.RequestIsBinary = true;
                entry.RequestBodyBytes = bytes.Take(8192).ToArray();
                entry.RequestBinaryLength = bytes.Length;
            }
        }
        catch (Exception ex)
        {
            entry.RequestBodyIncomplete = true;
            entry.RequestBody = "(lecture impossible : " + ex.Message + ")";
        }
    }

    private async Task OnResponse(object sender, SessionEventArgs e)
    {
        if (e.UserData is not TrafficEntry entry || entry.Completed) return;
        try
        {
            var req = e.HttpClient.Request;
            var res = e.HttpClient.Response;

            entry.StatusCode = res.StatusCode;
            entry.ResponseContentType = res.ContentType ?? "";
            entry.ResponseHeaders = HeadersText(res.Headers.GetAllHeaders());
            entry.ResponseHeadersLoading = false;
            entry.DurationMs = ElapsedMs(entry);
            NotifyExchangeUpdated(entry);

            long len = res.ContentLength;
            string ct = res.ContentType ?? "";
            bool hasBody = res.HasBody
                           && res.StatusCode is not (101 or 204 or 304)
                           && !req.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
            bool readable = hasBody && Fmt.IsText(ct)
                            && !ct.Contains("event-stream", StringComparison.OrdinalIgnoreCase)
                            && (len < 0 || len <= MaxCapture);

            if (readable)
            {
                string responseText = await e.GetResponseBodyAsString();
                byte[] responseBytes = Encoding.UTF8.GetBytes(responseText);
                SetResponsePayloadPreview(entry, responseBytes);
                entry.ResponseBody = Fmt.Truncate(responseText);
            }
            else if (hasBody)
            {
                entry.ResponseIsBinary = !Fmt.IsText(ct);
                entry.ResponsePreviewUnavailable = Fmt.IsText(ct);
                entry.ResponseBinaryLength = len >= 0 ? len : null;
                entry.ResponsePayloadLength = len >= 0 ? len : null;
                if (len >= 0 && len <= MaxCapture)
                {
                    byte[] responseBytes = await e.GetResponseBody();
                    SetResponsePayloadPreview(entry, responseBytes);
                    entry.ResponseBodyBytes = responseBytes.Take(8192).ToArray();
                }
            }

            entry.Size = len >= 0 ? len : (readable ? Encoding.UTF8.GetByteCount(entry.ResponseBody) : -1);

            var ctx = new MatchContext
            {
                Process = entry.Process, Host = entry.HostName, Url = entry.Url, Path = entry.Path, Method = entry.Method,
                RequestHeaders = entry.RequestHeaders, RequestBody = entry.RequestBody,
                ResponseStatus = res.StatusCode.ToString(), ResponseHeaders = entry.ResponseHeaders,
                ResponseBody = readable ? entry.ResponseBody : ""
            };

            foreach (var rule in Services.Rules.Match(RulePhase.Response, ctx))
            {
                bool stop = false;
                switch (rule.ActionType)
                {
                    case RuleActionType.Delay:
                        rule.Hit(); entry.AddRule(rule.Name);
                        await Task.Delay(Math.Max(0, rule.DelayMs));
                        break;

                    case RuleActionType.SetHeader:
                        rule.Hit(); entry.AddRule(rule.Name);
                        if (!string.IsNullOrWhiteSpace(rule.HeaderName))
                            res.Headers.SetOrAddHeaderValue(rule.HeaderName, rule.HeaderValue);
                        entry.Outcome = "Modifié";
                        break;

                    case RuleActionType.RemoveHeader:
                        rule.Hit(); entry.AddRule(rule.Name);
                        if (!string.IsNullOrWhiteSpace(rule.HeaderName)) res.Headers.RemoveHeader(rule.HeaderName);
                        entry.Outcome = "Modifié";
                        break;

                    case RuleActionType.ReplaceBody:
                        if (readable)
                        {
                            rule.Hit(); entry.AddRule(rule.Name);
                            var txt = await e.GetResponseBodyAsString();
                            var nt = ReplaceText(txt, rule);
                            e.SetResponseBodyString(nt);
                            entry.ResponseBody = Fmt.Truncate(nt);
                            entry.ResponseIsBinary = false;
                            SetResponsePayloadPreview(entry, Encoding.UTF8.GetBytes(nt));
                            entry.Outcome = "Modifié";
                        }
                        break;

                    case RuleActionType.Breakpoint:
                        if (!ct.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
                        {
                            rule.Hit(); entry.AddRule(rule.Name);
                            bool bodyEditable = readable;
                            string responseBody = bodyEditable ? await e.GetResponseBodyAsString() : "";
                            var edit = await WaitForBreakpointAsync(entry, req.Url, true,
                                HeadersText(res.Headers.GetAllHeaders()), responseBody, res.StatusCode, bodyEditable);
                            if (edit == null)
                            {
                                const string abandoned = "Response abandoned at breakpoint.";
                                ReplaceResponse(e, 502, Encoding.UTF8.GetBytes(abandoned), "text/plain; charset=utf-8");
                                entry.StatusCode = 502;
                                entry.ResponseContentType = "text/plain; charset=utf-8";
                                entry.ResponseBody = abandoned;
                                entry.ResponseIsBinary = false;
                                SetResponsePayloadPreview(entry, Encoding.UTF8.GetBytes(abandoned));
                                entry.Size = Encoding.UTF8.GetByteCount(abandoned);
                                entry.Outcome = "Abandonné";
                                stop = true;
                                break;
                            }

                            ApplyEditedHeaders(res.Headers.GetAllHeaders(),
                                name => { res.Headers.RemoveHeader(name); },
                                (name, value) => res.Headers.SetOrAddHeaderValue(name, value),
                                edit.Headers);
                            res.StatusCode = edit.StatusCode!.Value;
                            res.StatusDescription = ((HttpStatusCode)res.StatusCode).ToString();
                            if (bodyEditable && edit.BodyChanged)
                            {
                                e.SetResponseBodyString(edit.Body);
                                entry.ResponseBody = Fmt.Truncate(edit.Body);
                                entry.ResponseIsBinary = false;
                                SetResponsePayloadPreview(entry, Encoding.UTF8.GetBytes(edit.Body));
                                entry.Size = Encoding.UTF8.GetByteCount(edit.Body);
                            }
                            entry.StatusCode = res.StatusCode;
                            entry.ResponseHeaders = HeadersText(res.Headers.GetAllHeaders());
                            entry.Outcome = "Modifié";
                        }
                        break;

                    case RuleActionType.Block:
                        rule.Hit(); entry.AddRule(rule.Name);
                        var blockedBody = Encoding.UTF8.GetBytes(BlockPage(rule, entry));
                        ReplaceResponse(e, rule.StatusCode, blockedBody, "text/html; charset=utf-8");
                        entry.ResponseContentType = "text/html; charset=utf-8";
                        entry.ResponseBody = Fmt.Truncate(Encoding.UTF8.GetString(blockedBody));
                        entry.ResponseIsBinary = false;
                        SetResponsePayloadPreview(entry, blockedBody);
                        entry.StatusCode = rule.StatusCode;
                        entry.Outcome = "Bloqué";
                        stop = true;
                        break;

                    case RuleActionType.Mock:
                        rule.Hit(); entry.AddRule(rule.Name);
                        var body = MockBytes(rule);
                        var mockCt = string.IsNullOrWhiteSpace(rule.ContentType) ? "text/plain; charset=utf-8" : rule.ContentType;
                        ReplaceResponse(e, rule.StatusCode, body, mockCt);
                        entry.StatusCode = rule.StatusCode;
                        entry.ResponseContentType = mockCt;
                        if (Fmt.IsText(mockCt))
                        {
                            entry.ResponseBody = Fmt.Truncate(Encoding.UTF8.GetString(body));
                            entry.ResponseIsBinary = false;
                        }
                        else
                        {
                            entry.ResponseIsBinary = true;
                            entry.ResponseBodyBytes = body.Take(8192).ToArray();
                            entry.ResponseBinaryLength = body.Length;
                            entry.ResponseBody = "";
                        }
                        SetResponsePayloadPreview(entry, body);
                        entry.Size = body.Length;
                        entry.Outcome = "Mock";
                        stop = true;
                        break;
                }
                if (stop) break;
            }
        }
        catch (Exception ex)
        {
            entry.Outcome = "Erreur";
            entry.ResponseBody = ex.Message;
        }
        finally
        {
            entry.Completed = true;
            NotifyExchangeUpdated(entry);
        }
    }
}
