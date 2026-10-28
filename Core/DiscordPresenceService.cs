using System.Diagnostics;
using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Core;

public sealed class DiscordPresenceService : IDisposable
{
    private const string ApplicationId = "1564923989787414578";
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingCommands = new();
    private readonly Task _worker;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly object _stateLock = new();
    private bool _enabled = Services.Settings.DiscordPresence;
    private string _details = "Exploring HTTP FreeKit";
    private string _activityState = "Ready to inspect network traffic";
    private string _defaultDetails = "Exploring HTTP FreeKit";
    private string _defaultActivityState = "Ready to inspect network traffic";
    private readonly Dictionary<Guid, (string Details, string State, long Sequence)> _inspectionActivities = new();
    private long _inspectionSequence;
    private string _status = Services.Settings.DiscordPresence ? "Discord: searching" : "Discord: off";

    public DiscordPresenceService() => _worker = Task.Run(RunAsync);

    public event Action<string>? StatusChanged;
    public string Status => _status;

    public void SetEnabled(bool enabled)
    {
        lock (_stateLock) _enabled = enabled;
        if (!enabled) SetStatus("Discord: off");
        else SetStatus("Discord: searching");
        SignalChange();
    }

    private void SetStatus(string status)
    {
        if (_status == status) return;
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public void SetActivity(string details, string activityState)
    {
        bool changed;
        lock (_stateLock)
        {
            _defaultDetails = details;
            _defaultActivityState = activityState;
            changed = ApplyCurrentActivityLocked();
        }
        if (changed) SignalChange();
    }

    public void SetInspectionActivity(Guid inspectionId, string details, string activityState)
    {
        bool changed;
        lock (_stateLock)
        {
            long sequence = _inspectionActivities.TryGetValue(inspectionId, out var current)
                ? current.Sequence
                : ++_inspectionSequence;
            _inspectionActivities[inspectionId] = (details, activityState, sequence);
            changed = ApplyCurrentActivityLocked();
        }
        if (changed) SignalChange();
    }

    public void ClearInspectionActivity(Guid inspectionId)
    {
        bool changed;
        lock (_stateLock)
        {
            _inspectionActivities.Remove(inspectionId);
            changed = ApplyCurrentActivityLocked();
        }
        if (changed) SignalChange();
    }

    private bool ApplyCurrentActivityLocked()
    {
        var current = _inspectionActivities.Count > 0
            ? _inspectionActivities.Values.MaxBy(activity => activity.Sequence)
            : default;
        string details = current.Details ?? _defaultDetails;
        string state = current.State ?? _defaultActivityState;
        if (_details == details && _activityState == state) return false;
        _details = details;
        _activityState = state;
        return true;
    }

    private void SignalChange()
    {
        if (_changed.CurrentCount == 0)
        {
            try { _changed.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            if (!IsEnabled())
            {
                await _changed.WaitAsync(token);
                continue;
            }

            try
            {
                var pipe = await ConnectAsync(token);
                if (pipe is null)
                {
                    if (IsEnabled()) SetStatus("Discord: not found");
                    await WaitForChangeOrTimeout(TimeSpan.FromSeconds(5), token);
                    continue;
                }

                using (pipe)
                    await RunConnectedAsync(pipe, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (IOException ex)
            {
                Debug.WriteLine($"Discord Rich Presence connection failed: {ex.Message}");
                if (IsEnabled()) SetStatus("Discord: connection error");
                await WaitForChangeOrTimeout(TimeSpan.FromSeconds(5), token);
            }
            catch (TimeoutException ex)
            {
                Debug.WriteLine($"Discord Rich Presence timed out: {ex.Message}");
                if (IsEnabled()) SetStatus("Discord: timeout");
                await WaitForChangeOrTimeout(TimeSpan.FromSeconds(5), token);
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"Discord Rich Presence returned invalid data: {ex.Message}");
                if (IsEnabled()) SetStatus("Discord: invalid response");
                await WaitForChangeOrTimeout(TimeSpan.FromSeconds(5), token);
            }
        }
    }

    private bool IsEnabled()
    {
        lock (_stateLock) return _enabled;
    }

    private async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken token)
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}",
                PipeDirection.InOut, PipeOptions.Asynchronous);
            bool keepPipe = false;
            try
            {
                SetStatus("Discord: connecting");
                await pipe.ConnectAsync(300, token);
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                await WriteFrameAsync(pipe, 0,
                    JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = ApplicationId }), handshakeTimeout.Token);
                if (await WaitForReadyAsync(pipe, handshakeTimeout.Token))
                {
                    keepPipe = true;
                    return pipe;
                }
            }
            catch (IOException) { }
            catch (TimeoutException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            finally
            {
                if (!keepPipe) pipe.Dispose();
            }
        }

        return null;
    }

    private async Task<bool> WaitForReadyAsync(Stream pipe, CancellationToken token)
    {
        while (true)
        {
            var (opcode, payload) = await ReadFrameAsync(pipe, token);
            if (opcode == 3)
            {
                await WriteFrameAsync(pipe, 4, payload, token);
                continue;
            }
            if (opcode == 2)
                throw new IOException("Discord rejected the Rich Presence handshake.");
            if (opcode != 1) continue;

            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("evt", out var evt) &&
                evt.GetString() == "READY")
                return true;
        }
    }

    private async Task RunConnectedAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var readTask = ReadLoopAsync(pipe, connectionStop.Token);
        try
        {
            await SendActivityAsync(pipe, token);
            while (!token.IsCancellationRequested && IsEnabled())
            {
                using var waitStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                var changed = _changed.WaitAsync(waitStop.Token);
                if (await Task.WhenAny(readTask, changed) == readTask)
                {
                    waitStop.Cancel();
                    await readTask;
                    throw new IOException("Discord IPC closed the connection.");
                }
                await changed;
                if (IsEnabled()) await SendActivityAsync(pipe, token);
                else await SendCommandAsync(pipe, new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity = (object?)null }, nonce = Guid.NewGuid().ToString() }, token);
                if (!IsEnabled()) return;
            }
        }
        finally
        {
            connectionStop.Cancel();
            await pipe.DisposeAsync();
            try { await readTask; }
            catch (OperationCanceledException) when (connectionStop.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    private async Task ReadLoopAsync(Stream pipe, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var (opcode, payload) = await ReadFrameAsync(pipe, token);
            if (opcode == 3)
                await WriteFrameAsync(pipe, 4, payload, token);
            else if (opcode == 2)
                throw new IOException("Discord closed the Rich Presence connection.");
            else if (opcode == 1)
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.TryGetProperty("nonce", out var nonceElement) &&
                    nonceElement.ValueKind == JsonValueKind.String &&
                    _pendingCommands.TryRemove(nonceElement.GetString()!, out var completion))
                {
                    if (root.TryGetProperty("evt", out var eventElement) &&
                        eventElement.ValueKind == JsonValueKind.String &&
                        eventElement.GetString() == "ERROR")
                        completion.TrySetException(new IOException("Discord rejected the Rich Presence activity."));
                    else
                        completion.TrySetResult(true);
                }
            }
        }
    }

    private async Task SendActivityAsync(Stream pipe, CancellationToken token)
    {
        string details;
        string activityState;
        lock (_stateLock)
        {
            details = _details;
            activityState = _activityState;
        }

        var activity = new
        {
            details,
            state = activityState,
            timestamps = new { start = _startedAt.ToUnixTimeSeconds() },
            instance = false
        };
        await SendCommandAsync(pipe, new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce = Guid.NewGuid().ToString()
        }, token);
        SetStatus("Discord: active");
    }

    private async Task SendCommandAsync(Stream pipe, object command, CancellationToken token)
    {
        string payloadText = JsonSerializer.Serialize(command);
        using var document = JsonDocument.Parse(payloadText);
        string nonce = document.RootElement.GetProperty("nonce").GetString()!;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingCommands.TryAdd(nonce, completion))
            throw new InvalidOperationException("Discord RPC command nonce collision.");

        try
        {
            await WriteFrameAsync(pipe, 1, JsonSerializer.SerializeToUtf8Bytes(command), token);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        finally
        {
            _pendingCommands.TryRemove(nonce, out _);
        }
    }

    private async Task WaitForChangeOrTimeout(TimeSpan timeout, CancellationToken token)
    {
        using var timeoutStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var changed = _changed.WaitAsync(timeoutStop.Token);
        var delay = Task.Delay(timeout, token);
        await Task.WhenAny(changed, delay);
        timeoutStop.Cancel();
        if (changed.IsCompletedSuccessfully) await changed;
        token.ThrowIfCancellationRequested();
    }

    private async Task WriteFrameAsync(Stream pipe, int opcode, byte[] payload, CancellationToken token)
    {
        var header = new byte[8];
        BitConverter.GetBytes(opcode).CopyTo(header, 0);
        BitConverter.GetBytes(payload.Length).CopyTo(header, 4);
        await _writeLock.WaitAsync(token);
        try
        {
            await pipe.WriteAsync(header, token);
            await pipe.WriteAsync(payload, token);
            await pipe.FlushAsync(token);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(Stream pipe, CancellationToken token)
    {
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, token);
        int opcode = BitConverter.ToInt32(header, 0);
        int length = BitConverter.ToInt32(header, 4);
        if (length is < 0 or > 1_048_576)
            throw new InvalidDataException("Discord IPC frame length is invalid.");

        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, token);
        return (opcode, payload);
    }

    public void Dispose()
    {
        _stop.Cancel();
        SignalChange();
        try { _worker.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _writeLock.Dispose();
        _changed.Dispose();
        _stop.Dispose();
    }
}
