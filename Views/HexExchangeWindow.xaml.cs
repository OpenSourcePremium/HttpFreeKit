using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using HttpFreeKit.Core;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Views;

public partial class HexExchangeWindow : Window
{
    private const int MaxDecodedCharacters = 8 * 1024;
    private const int MaxExchanges = 150;
    private const int MaxUpdatesPerTick = 100;
    private const string TruncationMarker = "\n… (truncated,";

    private readonly int _pid;
    private readonly string _processName;
    private readonly Guid _inspectionId = Guid.NewGuid();
    private readonly string _clientEndpoint;
    private readonly Dictionary<long, LiveExchange> _byId = new();
    private readonly ConcurrentDictionary<long, TrafficExchangeUpdate> _pendingUpdates = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private volatile bool _closed;
    private int _discardedExchanges;
    private long _lastStatusRefresh;

    public ObservableCollection<LiveExchange> Exchanges { get; } = new();
    public string AddressLabelText { get; }
    public string SourceLabelText { get; }
    public string DestinationLabelText { get; }
    public string ContentLabelText { get; }
    public string DecodedLabelText { get; }

    public HexExchangeWindow(ConnectionRow connection)
    {
        InitializeComponent();
        WindowFrame.SizeChanged += (_, _) =>
        {
            WindowFrame.Clip = new System.Windows.Media.RectangleGeometry(
                new Rect(0, 0, WindowFrame.ActualWidth, WindowFrame.ActualHeight), 12, 12);
        };
        DataContext = this;
        LocalizationService.Apply(this, Services.Settings.Language);
        _pid = connection.Pid;
        _processName = connection.Process;
        _clientEndpoint = connection.Local;
        AddressLabelText = LocalizationService.Translate("ADRESSES", Services.Settings.Language);
        SourceLabelText = LocalizationService.Translate("Source", Services.Settings.Language);
        DestinationLabelText = LocalizationService.Translate("Destination", Services.Settings.Language);
        ContentLabelText = LocalizationService.Translate("HEX · ASCII", Services.Settings.Language);
        DecodedLabelText = LocalizationService.Translate("DÉCODÉ", Services.Settings.Language);
        ConnectionText.Text = $"{connection.Process} · PID {_pid}";
        WindowTitleText.Text = LocalizationService.Translate(
            "Live connection inspector", Services.Settings.Language);
        InfoText.Text = LocalizationService.Translate(
            "Les nouveaux échanges apparaissent dans la timeline. Sélectionnez-en un pour l’inspecter ; les arrivées suivantes ne changent pas votre sélection.",
            Services.Settings.Language);
        TimelineHint.Text = LocalizationService.Translate("Sélectionnez un échange", Services.Settings.Language);
        EmptyDescription.Text = LocalizationService.Translate(
            "Les nouveaux échanges HTTP(S) de ce processus apparaîtront ici lorsqu’ils passeront par HTTP FreeKit.",
            Services.Settings.Language);
        FooterText.Text = LocalizationService.Translate(
            "Le dump montre les octets capturés du corps, avec offsets et aperçu ASCII. Sélectionnez le texte pour le copier ; les données non capturées ou au-delà de la limite ne sont pas incluses.",
            Services.Settings.Language);
        RequestPaneTitle.Text = LocalizationService.Translate("REQUÊTE", Services.Settings.Language);
        ResponsePaneTitle.Text = LocalizationService.Translate("RÉPONSE", Services.Settings.Language);
        HexLabel.Text = ContentLabelText;
        ResponseHexLabel.Text = ContentLabelText;
        DecodedLabel.Text = DecodedLabelText;
        ResponseDecodedLabel.Text = DecodedLabelText;

        long startAfterId = Services.Proxy.SubscribeExchangeUpdates(OnExchangeUpdated);
        _startAfterId = startAfterId;
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            _uiTimer.Stop();
            Services.Proxy.UnsubscribeExchangeUpdates(OnExchangeUpdated);
            _pendingUpdates.Clear();
            Services.DiscordPresence.ClearInspectionActivity(_inspectionId);
        };
        UpdateEmptyState();
        RefreshDiscordPresence();
    }

    private readonly long _startAfterId;

    private void OnExchangeUpdated(TrafficExchangeUpdate update)
    {
        if (update.Pid == _pid && update.Id > _startAfterId && !_closed)
            _pendingUpdates[update.Id] = update;
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (_closed) return;

        bool selectedUpdated = false;
        foreach (long id in _pendingUpdates.Keys.Take(MaxUpdatesPerTick))
        {
            if (!_pendingUpdates.TryRemove(id, out var update)) continue;
            if (update.Id <= _startAfterId) continue;

            if (!_byId.TryGetValue(id, out var exchange))
            {
                exchange = new LiveExchange(id);
                _byId.Add(id, exchange);
                int insertIndex = Exchanges.TakeWhile(item => item.Id < id).Count();
                Exchanges.Insert(insertIndex, exchange);
                if (ExchangeList.SelectedItem is null)
                {
                    ExchangeList.SelectedItem = exchange;
                    selectedUpdated = true;
                }
                if (Exchanges.Count > MaxExchanges)
                {
                    var selected = ExchangeList.SelectedItem as LiveExchange;
                    var oldestUnselected = Exchanges.FirstOrDefault(item => !ReferenceEquals(item, selected));
                    if (oldestUnselected is { } oldest)
                    {
                        Exchanges.Remove(oldest);
                        _byId.Remove(oldest.Id);
                    }
                    _discardedExchanges++;
                }
            }

            exchange.UpdateSummary(update, Services.Settings.Language);
            if (ReferenceEquals(ExchangeList.SelectedItem, exchange))
                selectedUpdated = true;
        }

        EmptyState.Visibility = Exchanges.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();

        if (selectedUpdated)
            RefreshSelectedDetails();

        long now = Environment.TickCount64;
        if (now - _lastStatusRefresh >= 500)
        {
            _lastStatusRefresh = now;
            UpdateEmptyState();
        }
    }

    private void UpdateEmptyState()
    {
        bool running = Services.Proxy.IsRunning;
        EmptyTitle.Text = LocalizationService.Translate(
            running ? "En attente de nouvelles données" : "Le proxy est arrêté",
            Services.Settings.Language);
        EmptyState.Visibility = Exchanges.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    private void UpdateCount()
    {
        ExchangeCount.Text = LocalizationService.Format(
            "{0:N0} échange(s) en direct", Services.Settings.Language, Exchanges.Count);
        if (_discardedExchanges > 0)
            ExchangeCount.Text += LocalizationService.Format(
                " · {0:N0} ancien(s) retiré(s) pour limiter la mémoire",
                Services.Settings.Language, _discardedExchanges);
        TimelineHint.Text = ExchangeList.SelectedItem is null
            ? LocalizationService.Translate("Sélectionnez un échange", Services.Settings.Language)
            : LocalizationService.Translate("Inspection fixe pendant le direct", Services.Settings.Language);
        RefreshDiscordPresence();
    }

    private void RefreshDiscordPresence()
    {
        int completed = Exchanges.Count(exchange => exchange.Snapshot?.Completed == true);
        var selected = ExchangeList.SelectedItem as LiveExchange;
        string details = LocalizationService.Format(
            "Inspecting live connection · {0}", Services.Settings.Language, _processName);
        string state = LocalizationService.Format(
            "{0} exchanges · {1} completed", Services.Settings.Language, Exchanges.Count, completed);
        if (selected is not null)
            state += LocalizationService.Format(
                " · Inspecting {0}", Services.Settings.Language,
                string.IsNullOrWhiteSpace(selected.Method) ? "HTTP" : selected.Method);
        Services.DiscordPresence.SetInspectionActivity(_inspectionId, details, state);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        GlyphMax.Data = System.Windows.Media.Geometry.Parse(maximized
            ? "M 5,3 H 13 V 11 H 5 Z M 3,5 V 13 H 11"
            : "M 3,3 H 13 V 13 H 3 Z");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ExchangeList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RefreshSelectedDetails();
        UpdateCount();
    }

    private void RefreshSelectedDetails()
    {
        if (ExchangeList.SelectedItem is not LiveExchange exchange || exchange.Snapshot is not { } update)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        exchange.UpdateDetails(update, _clientEndpoint, Services.Settings.Language);
        SelectedTitle.Text = $"{exchange.Method}  {exchange.Target}  ·  {exchange.Result}";
        SelectedRequestLine.Text = exchange.RequestLine;
        RequestAddress.Text = $"{AddressLabelText}  ·  {SourceLabelText}: {exchange.RequestSource}  →  {DestinationLabelText}: {exchange.RequestDestination}";
        ResponseAddress.Text = $"{AddressLabelText}  ·  {SourceLabelText}: {exchange.ResponseSource}  →  {DestinationLabelText}: {exchange.ResponseDestination}";
        RequestHexPreview.SetBinaryContent(update.RequestPayloadPreview, update.RequestPayloadLength,
            exchange.RequestPending || update.RequestPreviewUnavailable, Services.Settings.Language);
        RequestDecoded.Text = exchange.RequestDecoded;
        ResponseHexPreview.SetBinaryContent(update.ResponsePayloadPreview, update.ResponsePayloadLength,
            !update.Completed && !update.ResponsePayloadLength.HasValue || update.ResponsePreviewUnavailable,
            Services.Settings.Language);
        ResponseDecoded.Text = exchange.ResponseDecoded;
        DetailPanel.Visibility = Visibility.Visible;
    }

    private static string FormatDecodedPayload(
        string decoded, bool binary, bool unavailable, long? totalLength, string language)
    {
        if (unavailable)
            return LocalizationService.Translate("Valeur décodée indisponible", language);
        if (binary)
            return LocalizationService.Translate("Contenu binaire · aucune valeur texte", language);
        if (decoded.Length == 0 && (!totalLength.HasValue || totalLength == 0))
            return LocalizationService.Translate("Aucun contenu", language);
        if (string.IsNullOrEmpty(decoded))
            return LocalizationService.Translate("Aucune valeur décodée", language);

        int marker = decoded.IndexOf(TruncationMarker, StringComparison.Ordinal);
        string decodedValue = marker >= 0 ? decoded[..marker] : decoded;
        return decodedValue.Length > MaxDecodedCharacters
            ? decodedValue[..MaxDecodedCharacters] + "\n…"
            : decodedValue;
    }

    public sealed class LiveExchange : ObservableObject
    {
        public long Id { get; }
        private string _target = "";
        private string _method = "";
        private string _requestLine = "";
        private string _timeText = "";
        private string _result = "";
        private string _requestSource = "";
        private string _requestDestination = "";
        private string _responseSource = "";
        private string _responseDestination = "";
        private string _requestDecoded = "";
        private string _responseDecoded = "";

        public string Target { get => _target; private set => Set(ref _target, value); }
        public string Method { get => _method; private set => Set(ref _method, value); }
        public string RequestLine { get => _requestLine; private set => Set(ref _requestLine, value); }
        public string TimeText { get => _timeText; private set => Set(ref _timeText, value); }
        public string Result { get => _result; private set => Set(ref _result, value); }
        public string RequestSource { get => _requestSource; private set => Set(ref _requestSource, value); }
        public string RequestDestination { get => _requestDestination; private set => Set(ref _requestDestination, value); }
        public string ResponseSource { get => _responseSource; private set => Set(ref _responseSource, value); }
        public string ResponseDestination { get => _responseDestination; private set => Set(ref _responseDestination, value); }
        public string RequestDecoded { get => _requestDecoded; private set => Set(ref _requestDecoded, value); }
        public string ResponseDecoded { get => _responseDecoded; private set => Set(ref _responseDecoded, value); }
        public bool RequestPending { get; private set; }

        public LiveExchange(long id) => Id = id;

        public TrafficExchangeUpdate? Snapshot { get; private set; }

        public void UpdateSummary(TrafficExchangeUpdate update, string language)
        {
            Snapshot = update;
            Method = update.Method;
            Target = $"{update.Host}{update.Path}";
            RequestLine = $"{update.Method}  {update.Url}";
            TimeText = update.Time.ToString("HH:mm:ss.fff");
            Result = update.Completed
                ? update.StatusCode?.ToString() ?? LocalizationService.Translate(update.Outcome, language)
                : LocalizationService.Translate("EN COURS", language);
        }

        public void UpdateDetails(TrafficExchangeUpdate update, string clientEndpoint, string language)
        {
            RequestSource = clientEndpoint;
            RequestDestination = $"{update.Host}{update.Path}";
            ResponseSource = RequestDestination;
            ResponseDestination = clientEndpoint;

            bool requestPending = !update.Completed
                                  && !update.RequestPayloadLength.HasValue
                                  && update.RequestBody.Length == 0
                                  && !update.RequestPreviewUnavailable;
            RequestPending = requestPending;
            if (requestPending)
            {
                RequestDecoded = LocalizationService.Translate("Capture de la requête…", language);
            }
            else
            {
                RequestDecoded = FormatDecodedPayload(update.RequestBody,
                    update.RequestIsBinary, update.RequestPreviewUnavailable,
                    update.RequestPayloadLength, language);
            }

            ResponseDecoded = update.Completed || update.ResponseBody.Length > 0
                ? FormatDecodedPayload(update.ResponseBody, update.ResponseIsBinary,
                    update.ResponsePreviewUnavailable, update.ResponsePayloadLength, language)
                : LocalizationService.Translate("En attente de la réponse…", language);
        }
    }
}
