using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HttpFreeKit.Core;

public sealed class TransparentRedirector : IDisposable
{
    private const int PacketBufferLength = 0xFFFF;
    private const int FirstDataTimeoutMs = 1000;
    private const long NatIdleLifetimeMs = 30 * 60 * 1000;
    private const long ClosingLifetimeMs = 10 * 1000;
    private const int SystemProcessId = 4;
    private const string PidHeader = "X-HttpFreeKit-Pid";
    private const string TokenHeader = "X-HttpFreeKit-Token";

    private readonly object _sync = new();
    private readonly ConcurrentDictionary<(byte Protocol, int Port), ProcessMapping> _processByPort = new();
    private readonly ConcurrentDictionary<ConnectionKey, NatMapping> _nat = new();
    private readonly ConcurrentDictionary<(byte Protocol, ConnectionKey Key), FallbackMapping> _fallbackLookups = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _activeClients = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _relayCancellation;
    private Task? _acceptTask;
    private Thread? _socketThread;
    private Thread? _networkThread;
    private IntPtr _socketHandle = InvalidHandle;
    private IntPtr _networkHandle = InvalidHandle;
    private HashSet<int> _redirectPorts = [];
    private string? _sessionToken;
    private int _running;
    private string _status = "Stopped";

    private static readonly IntPtr InvalidHandle = new(-1);

    private sealed record ProcessMapping(int Pid, long ExpiresAt);
    private sealed record FallbackMapping(int Pid, long CheckedAt);

    private sealed class NatMapping(IPAddress originAddress, int originPort,
        IPAddress clientAddress, int clientPort, int pid)
    {
        internal IPAddress OriginAddress { get; } = originAddress;
        internal int OriginPort { get; } = originPort;
        internal IPAddress ClientAddress { get; } = clientAddress;
        internal int ClientPort { get; } = clientPort;
        internal int Pid { get; } = pid;
        internal long LastActivity { get; set; } = Environment.TickCount64;
        internal long ClosingAt { get; set; }
    }

    private readonly record struct ConnectionKey(AddressFamily Family, IPAddress Address, int Port)
    {
        internal static ConnectionKey Create(IPAddress address, int port)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            return new ConnectionKey(address.AddressFamily, address, port);
        }
    }

    private readonly record struct PacketInfo(
        bool IsIpv6,
        int Protocol,
        int TransportOffset,
        IPAddress SourceAddress,
        IPAddress DestinationAddress,
        int SourcePort,
        int DestinationPort,
        byte TcpFlags);

    private enum DataKind
    {
        NeedMore,
        Raw,
        Http,
        TlsWithSni,
        TlsWithoutSni
    }

    private readonly record struct FirstData(DataKind Kind, string? Host);

    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public string DriverStatus => _status;
    public string? LastError { get; private set; }
    public event Action? StatusChanged;

    public bool Start()
    {
        lock (_sync)
        {
            if (IsRunning)
                return true;

            LastError = null;
            SetStatus("Starting");
            try
            {
                if (!Environment.Is64BitOperatingSystem || !Environment.Is64BitProcess)
                    throw new InvalidOperationException("WinDivert capture requires the x64 application.");

                WinDivertNative.ExtractRuntimeFiles();

                int redirectPort = Services.Settings.RedirectPort;
                if (redirectPort is < 1024 or > 65535)
                    throw new InvalidOperationException("The relay port must be between 1024 and 65535.");
                if (redirectPort == Services.Settings.Port)
                    throw new InvalidOperationException("The relay port must differ from the local proxy port.");
                if (!TryParsePorts(Services.Settings.RedirectPorts, out var ports, out string error))
                    throw new InvalidOperationException(error);
                if (ports.Contains(redirectPort))
                    throw new InvalidOperationException("The relay port cannot also be a redirected destination port.");

                var listener = new TcpListener(IPAddress.IPv6Any, redirectPort);
                listener.Server.DualMode = true;
                listener.Start(128);
                _listener = listener;
                _relayCancellation = new CancellationTokenSource();
                _redirectPorts = ports.ToHashSet();
                _sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

                _socketHandle = WinDivertNative.Open("true", WinDivertNative.SocketLayer, 0,
                    WinDivertNative.SniffFlag | WinDivertNative.ReceiveOnlyFlag);
                ThrowIfInvalidHandle(_socketHandle, "SOCKET");

                string filter = BuildNetworkFilter(ports, redirectPort, Services.Settings.BlockQuic);
                _networkHandle = WinDivertNative.Open(filter, WinDivertNative.NetworkLayer, 0, 0);
                ThrowIfInvalidHandle(_networkHandle, "NETWORK");

                Volatile.Write(ref _running, 1);
                _socketThread = new Thread(SocketReceiveLoop)
                {
                    IsBackground = true,
                    Name = "HTTP FreeKit WinDivert SOCKET"
                };
                _networkThread = new Thread(NetworkReceiveLoop)
                {
                    IsBackground = true,
                    Name = "HTTP FreeKit WinDivert NETWORK"
                };
                _socketThread.Start();
                _networkThread.Start();
                _acceptTask = Task.Run(() => AcceptLoopAsync(listener, _relayCancellation.Token));
                SetStatus("Running");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.WriteLine($"Unable to start WinDivert capture: {ex}");
                StopCore();
                SetStatus("Unavailable");
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopCore();
            SetStatus("Stopped");
        }
    }

    public bool IsValidToken(string? candidate)
    {
        string? token = Volatile.Read(ref _sessionToken);
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(token))
            return false;

        byte[] supplied = Encoding.ASCII.GetBytes(candidate);
        byte[] expected = Encoding.ASCII.GetBytes(token);
        return supplied.Length == expected.Length &&
               CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    public static bool TryParsePorts(string? text, out int[] ports, out string error)
    {
        var parsed = new HashSet<int>();
        foreach (string part in (text ?? "").Split(new[] { ',', ';', '\r', '\n', ' ' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
                port is < 1 or > 65535)
            {
                ports = [];
                error = "Invalid TCP ports: use values from 1 to 65535.";
                return false;
            }

            parsed.Add(port);
        }

        if (parsed.Count == 0)
        {
            ports = [];
            error = "Enter at least one TCP destination port.";
            return false;
        }

        ports = parsed.Order().ToArray();
        error = "";
        return true;
    }

    private static string BuildNetworkFilter(IEnumerable<int> ports, int relayPort, bool blockQuic)
    {
        string destinations = string.Join(" or ", ports.Select(port => $"tcp.DstPort == {port}"));
        string tcpFilter = $"tcp and (tcp.SrcPort == {relayPort} or {destinations})";
        return blockQuic
            ? $"outbound and (({tcpFilter}) or (udp and udp.DstPort == 443))"
            : $"outbound and ({tcpFilter})";
    }

    private static void ThrowIfInvalidHandle(IntPtr handle, string layer)
    {
        if (handle != InvalidHandle && handle != IntPtr.Zero)
            return;

        int error = Marshal.GetLastWin32Error();
        throw new InvalidOperationException(
            $"WinDivert could not open the {layer} handle (Windows error {error}). " +
            "Check that the embedded WinDivert files were extracted beside the application and that the driver is allowed by Windows.");
    }

    private void StopCore()
    {
        Volatile.Write(ref _running, 0);

        var network = _networkHandle;
        _networkHandle = InvalidHandle;
        CloseHandle(network);
        JoinThread(_networkThread);
        _networkThread = null;

        var socket = _socketHandle;
        _socketHandle = InvalidHandle;
        CloseHandle(socket);
        JoinThread(_socketThread);
        _socketThread = null;

        try { _relayCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Stop(); } catch (SocketException) { }
        foreach (var client in _activeClients.Keys)
        {
            try { client.Dispose(); } catch (ObjectDisposedException) { }
        }

        try { _acceptTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException ex) { Debug.WriteLine($"WinDivert relay stop: {ex.GetBaseException().Message}"); }
        _acceptTask = null;
        _listener = null;
        _relayCancellation?.Dispose();
        _relayCancellation = null;

        _nat.Clear();
        _processByPort.Clear();
        _fallbackLookups.Clear();
        _activeClients.Clear();
        _redirectPorts.Clear();
        Volatile.Write(ref _sessionToken, null);
    }

    private static void CloseHandle(IntPtr handle)
    {
        if (handle == InvalidHandle || handle == IntPtr.Zero)
            return;

        if (!WinDivertNative.Close(handle))
            Debug.WriteLine($"WinDivertClose failed with Windows error {Marshal.GetLastWin32Error()}.");
    }

    private static void JoinThread(Thread? thread)
    {
        if (thread is null || thread == Thread.CurrentThread)
            return;
        if (!thread.Join(TimeSpan.FromSeconds(2)))
            Debug.WriteLine($"WinDivert receive thread did not stop promptly: {thread.Name}");
    }

    private void SocketReceiveLoop()
    {
        var buffer = new byte[PacketBufferLength];
        while (IsRunning)
        {
            IntPtr handle = _socketHandle;
            if (handle == InvalidHandle)
                break;

            if (!WinDivertNative.Recv(handle, buffer, (uint)buffer.Length, out _, out var address))
            {
                int error = Marshal.GetLastWin32Error();
                if (IsRunning)
                    FailRuntime($"WinDivert SOCKET receive failed with Windows error {error}.");
                break;
            }

            if (address.Layer != WinDivertNative.SocketLayer)
                continue;

            int port = address.Socket.LocalPort;
            int pid = unchecked((int)address.Socket.ProcessId);
            switch (address.Event)
            {
                case WinDivertNative.SocketEventBind:
                case WinDivertNative.SocketEventConnect:
                    if (port > 0 && pid > 0)
                    {
                        long expiresAt = Environment.TickCount64 + 10 * 60 * 1000;
                        _processByPort.AddOrUpdate((address.Socket.Protocol, port),
                            new ProcessMapping(pid, expiresAt),
                            (_, existing) => pid == SystemProcessId && existing.Pid != SystemProcessId
                                ? existing
                                : new ProcessMapping(pid, expiresAt));
                    }
                    break;
                case WinDivertNative.SocketEventClose:
                    if (port > 0 &&
                        _processByPort.TryGetValue((address.Socket.Protocol, port), out var mapping) &&
                        (mapping.Pid == pid || mapping.Pid <= 0))
                        _processByPort.TryRemove((address.Socket.Protocol, port), out _);
                    break;
            }
        }
    }

    private void NetworkReceiveLoop()
    {
        var buffer = new byte[PacketBufferLength];
        long lastCleanup = Environment.TickCount64;

        while (IsRunning)
        {
            IntPtr handle = _networkHandle;
            if (handle == InvalidHandle)
                break;

            if (!WinDivertNative.Recv(handle, buffer, (uint)buffer.Length, out uint receivedLength,
                    out var address))
            {
                int error = Marshal.GetLastWin32Error();
                if (IsRunning)
                {
                    FailRuntime($"WinDivert NETWORK receive failed with Windows error {error}.");
                }
                break;
            }

            if (receivedLength == 0 || receivedLength > buffer.Length)
                continue;

            bool reinject = true;
            try
            {
                reinject = ProcessPacket(buffer, (int)receivedLength, ref address);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unable to process a WinDivert packet: {ex}");
            }

            if (reinject)
                SendPacket(handle, buffer, (int)receivedLength, ref address);

            long now = Environment.TickCount64;
            if (now - lastCleanup >= 30_000)
            {
                CleanupMappings(now);
                lastCleanup = now;
            }
        }
    }

    private bool ProcessPacket(byte[] packet, int length, ref WinDivertNative.Address address)
    {
        if (!address.IsOutbound || !TryParsePacket(packet, length, out var info))
            return true;
        if (IsLoopback(info.DestinationAddress))
            return true;

        if (info.Protocol == 17 && Services.Settings.BlockQuic && info.DestinationPort == 443)
        {
            int udpPid = ResolvePid(info, tcp: false);
            if (udpPid != Environment.ProcessId && (udpPid <= 0 || !IsExcluded(udpPid)))
            {
                Debug.WriteLine($"Dropped UDP/443 to force TCP fallback (PID {udpPid}).");
                return false;
            }
            return true;
        }

        if (info.Protocol != 6)
            return true;

        int relayPort = Services.Settings.RedirectPort;
        if (info.SourcePort == relayPort)
            return RewriteRelayResponse(packet, length, info, ref address);

        if (!_redirectPorts.Contains(info.DestinationPort))
            return true;

        int pid = ResolvePid(info, tcp: true);
        if (pid <= 0 || IsExcluded(pid))
            return true;

        var key = ConnectionKey.Create(info.DestinationAddress, info.SourcePort);
        if (!_nat.TryGetValue(key, out var mapping) ||
            !mapping.OriginAddress.Equals(info.DestinationAddress) ||
            mapping.OriginPort != info.DestinationPort ||
            (info.TcpFlags & 0x02) != 0)
        {
            mapping = new NatMapping(info.DestinationAddress, info.DestinationPort,
                info.SourceAddress, info.SourcePort, pid);
            _nat[key] = mapping;
        }
        else
        {
            mapping.LastActivity = Environment.TickCount64;
        }

        uint originalFlags = address.Flags;
        WriteAddress(packet, info, source: true, info.DestinationAddress.GetAddressBytes());
        WriteAddress(packet, info, source: false, info.SourceAddress.GetAddressBytes());
        WritePort(packet, info.TransportOffset + 2, relayPort);
        address.IsOutbound = false;
        if (!WinDivertNative.CalcChecksums(packet, (uint)length, ref address, 0))
        {
            Debug.WriteLine($"WinDivert could not recalculate packet checksums (Windows error {Marshal.GetLastWin32Error()}).");
            WriteAddress(packet, info, source: true, info.SourceAddress.GetAddressBytes());
            WriteAddress(packet, info, source: false, info.DestinationAddress.GetAddressBytes());
            WritePort(packet, info.TransportOffset + 2, info.DestinationPort);
            address.Flags = originalFlags;
            return true;
        }

        if ((info.TcpFlags & 0x04) != 0)
            _nat.TryRemove(key, out _);
        else
            MarkConnectionClosing(mapping, info.TcpFlags);
        return true;
    }

    private bool RewriteRelayResponse(byte[] packet, int length, PacketInfo info,
        ref WinDivertNative.Address address)
    {
        var key = ConnectionKey.Create(info.DestinationAddress, info.DestinationPort);
        if (!_nat.TryGetValue(key, out var mapping))
        {
            return true;
        }

        mapping.LastActivity = Environment.TickCount64;
        uint originalFlags = address.Flags;
        WriteAddress(packet, info, source: true, mapping.OriginAddress.GetAddressBytes());
        WritePort(packet, info.TransportOffset, mapping.OriginPort);
        WriteAddress(packet, info, source: false, mapping.ClientAddress.GetAddressBytes());
        WritePort(packet, info.TransportOffset + 2, mapping.ClientPort);
        address.IsOutbound = false;
        if (!WinDivertNative.CalcChecksums(packet, (uint)length, ref address, 0))
        {
            Debug.WriteLine($"WinDivert could not recalculate packet checksums (Windows error {Marshal.GetLastWin32Error()}).");
            WriteAddress(packet, info, source: true, info.SourceAddress.GetAddressBytes());
            WritePort(packet, info.TransportOffset, info.SourcePort);
            WriteAddress(packet, info, source: false, info.DestinationAddress.GetAddressBytes());
            WritePort(packet, info.TransportOffset + 2, info.DestinationPort);
            address.Flags = originalFlags;
            return true;
        }

        if ((info.TcpFlags & 0x04) != 0)
            _nat.TryRemove(key, out _);
        else
            MarkConnectionClosing(mapping, info.TcpFlags);
        return true;
    }

    private static bool IsLoopback(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    private void SendPacket(IntPtr handle, byte[] packet, int length, ref WinDivertNative.Address address)
    {
        if (!WinDivertNative.Send(handle, packet, (uint)length, out _, ref address))
        {
            Debug.WriteLine($"WinDivertSend failed with Windows error {Marshal.GetLastWin32Error()}.");
        }
    }

    private int ResolvePid(PacketInfo info, bool tcp)
    {
        long now = Environment.TickCount64;
        byte protocol = tcp ? (byte)6 : (byte)17;
        var portKey = (protocol, info.SourcePort);
        bool hasPortMapping = _processByPort.TryGetValue(portKey, out var mapping) &&
                              mapping.ExpiresAt > now;
        if (hasPortMapping && mapping!.Pid != SystemProcessId)
            return mapping.Pid;

        var key = ConnectionKey.Create(info.SourceAddress, info.SourcePort);
        var fallbackKey = (protocol, key);
        if (_fallbackLookups.TryGetValue(fallbackKey, out var fallback) &&
            now - fallback.CheckedAt < 30_000)
            return fallback.Pid;

        int pid = tcp
            ? ConnectionMonitor.FindTcpPid(info.SourceAddress, info.SourcePort,
                info.DestinationAddress, info.DestinationPort)
            : ConnectionMonitor.FindUdpPid(info.SourceAddress, info.SourcePort);
        _fallbackLookups[fallbackKey] = new FallbackMapping(pid, now);
        if (pid > 0)
            _processByPort[portKey] = new ProcessMapping(pid, now + 10 * 60 * 1000);
        else if (hasPortMapping && mapping!.Pid == SystemProcessId)
            _processByPort[portKey] = new ProcessMapping(-1, now + 10 * 60 * 1000);
        return pid;
    }

    private static bool IsExcluded(int pid)
    {
        if (pid == Environment.ProcessId)
            return true;

        var process = ProcessInfo.Get(pid).Name;
        string processName = Path.GetFileNameWithoutExtension(process);
        return Services.Settings.ExcludedProcesses?.Any(name =>
        {
            string excluded = Path.GetFileNameWithoutExtension(name.Trim());
            return excluded.Length > 0 && string.Equals(excluded, processName, StringComparison.OrdinalIgnoreCase);
        }) == true;
    }

    private static void WriteAddress(byte[] packet, PacketInfo info, bool source, byte[] address)
    {
        int offset = info.IsIpv6
            ? (source ? 8 : 24)
            : (source ? 12 : 16);
        Buffer.BlockCopy(address, 0, packet, offset, info.IsIpv6 ? 16 : 4);
    }

    private static void WritePort(byte[] packet, int offset, int port)
    {
        packet[offset] = (byte)(port >> 8);
        packet[offset + 1] = (byte)port;
    }

    private static void MarkConnectionClosing(NatMapping mapping, byte tcpFlags)
    {
        if ((tcpFlags & 0x04) != 0)
            mapping.ClosingAt = Environment.TickCount64;
        else if ((tcpFlags & 0x01) != 0 && mapping.ClosingAt == 0)
            mapping.ClosingAt = Environment.TickCount64 + ClosingLifetimeMs;
    }

    private void CleanupMappings(long now)
    {
        foreach (var pair in _nat)
        {
            long expiresAt = pair.Value.ClosingAt == 0
                ? pair.Value.LastActivity + NatIdleLifetimeMs
                : pair.Value.ClosingAt;
            if (expiresAt <= now)
                _nat.TryRemove(pair.Key, out _);
        }

        foreach (var pair in _processByPort)
            if (pair.Value.ExpiresAt <= now)
                _processByPort.TryRemove(pair.Key, out _);

        foreach (var pair in _fallbackLookups)
            if (now - pair.Value.CheckedAt >= 30_000)
                _fallbackLookups.TryRemove(pair.Key, out _);
    }

    private static bool TryParsePacket(byte[] packet, int length, out PacketInfo info)
    {
        info = default;
        if (length < 20)
            return false;

        bool ipv6 = (packet[0] >> 4) == 6;
        bool ipv4 = (packet[0] >> 4) == 4;
        int transportOffset;
        int protocol;
        byte[] source;
        byte[] destination;

        if (ipv4)
        {
            int headerLength = (packet[0] & 0x0F) * 4;
            if (headerLength < 20 || length < headerLength ||
                (ReadUInt16(packet, 6) & 0x3FFF) != 0)
                return false;

            protocol = packet[9];
            transportOffset = headerLength;
            source = packet.AsSpan(12, 4).ToArray();
            destination = packet.AsSpan(16, 4).ToArray();
        }
        else if (ipv6 && length >= 40)
        {
            protocol = packet[6];
            transportOffset = 40;
            source = packet.AsSpan(8, 16).ToArray();
            destination = packet.AsSpan(24, 16).ToArray();

            for (int extensions = 0; extensions < 8; extensions++)
            {
                if (protocol == 6 || protocol == 17)
                    break;
                if (protocol == 44)
                {
                    if (length < transportOffset + 8 || (ReadUInt16(packet, transportOffset + 2) & 0xFFF9) != 0)
                        return false;
                    protocol = packet[transportOffset];
                    transportOffset += 8;
                }
                else if (protocol is 0 or 43 or 60 or 135 or 139 or 140)
                {
                    if (length < transportOffset + 2)
                        return false;
                    int extensionLength = (packet[transportOffset + 1] + 1) * 8;
                    if (length < transportOffset + extensionLength)
                        return false;
                    protocol = packet[transportOffset];
                    transportOffset += extensionLength;
                }
                else if (protocol == 51)
                {
                    if (length < transportOffset + 2)
                        return false;
                    int extensionLength = (packet[transportOffset + 1] + 2) * 4;
                    if (length < transportOffset + extensionLength)
                        return false;
                    protocol = packet[transportOffset];
                    transportOffset += extensionLength;
                }
                else
                {
                    return false;
                }
            }
        }
        else
        {
            return false;
        }

        if (protocol == 6)
        {
            if (length < transportOffset + 20)
                return false;
            int tcpHeaderLength = (packet[transportOffset + 12] >> 4) * 4;
            if (tcpHeaderLength < 20 || length < transportOffset + tcpHeaderLength)
                return false;

            info = new PacketInfo(ipv6, protocol, transportOffset,
                new IPAddress(source), new IPAddress(destination),
                ReadUInt16(packet, transportOffset), ReadUInt16(packet, transportOffset + 2),
                packet[transportOffset + 13]);
            return true;
        }

        if (protocol == 17 && length >= transportOffset + 8)
        {
            info = new PacketInfo(ipv6, protocol, transportOffset,
                new IPAddress(source), new IPAddress(destination),
                ReadUInt16(packet, transportOffset), ReadUInt16(packet, transportOffset + 2), 0);
            return true;
        }

        return false;
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
        => (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            client.NoDelay = true;
            if (client.Client.RemoteEndPoint is not IPEndPoint remote ||
                !_nat.TryGetValue(ConnectionKey.Create(remote.Address, remote.Port), out var mapping))
            {
                client.Dispose();
                continue;
            }

            _activeClients.TryAdd(client, 0);
            _ = HandleClientAsync(client, mapping, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, NatMapping mapping, CancellationToken cancellationToken)
    {
        try
        {
            var clientStream = client.GetStream();
            byte[] initialData = new byte[PacketBufferLength];
            int initialLength = await ReadFirstDataAsync(clientStream, initialData, cancellationToken)
                .ConfigureAwait(false);
            var firstData = ClassifyFirstData(initialData.AsSpan(0, initialLength), mapping);

            if (firstData.Kind == DataKind.TlsWithSni &&
                firstData.Host is { Length: > 0 } sni &&
                !IsBypassed(sni))
            {
                await BridgeThroughProxyAsync(client, clientStream, mapping, sni,
                    initialData, initialLength, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (firstData.Kind == DataKind.Http &&
                firstData.Host is { Length: > 0 } host)
            {
                await BridgeThroughProxyAsync(client, clientStream, mapping, host,
                    initialData, initialLength, cancellationToken).ConfigureAwait(false);
                return;
            }

            await BridgeRawAsync(client, clientStream, mapping, initialData, initialLength,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException ex) { Debug.WriteLine($"Transparent relay closed: {ex.Message}"); }
        catch (SocketException ex) { Debug.WriteLine($"Transparent relay socket error: {ex.Message}"); }
        catch (Exception ex)
        {
            Debug.WriteLine($"Transparent relay failed: {ex}");
        }
        finally
        {
            _activeClients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    private static async Task<int> ReadFirstDataAsync(NetworkStream stream, byte[] buffer,
        CancellationToken cancellationToken)
    {
        int count = 0;
        long deadline = Environment.TickCount64 + FirstDataTimeoutMs;
        while (count < buffer.Length)
        {
            var result = ClassifyFirstData(buffer.AsSpan(0, count), null);
            if (result.Kind != DataKind.NeedMore)
                return count;

            int remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0)
                return count;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(remaining);
            try
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    return count;
                count += read;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return count;
            }
        }

        return count;
    }

    private static FirstData ClassifyFirstData(ReadOnlySpan<byte> data, NatMapping? mapping)
    {
        if (data.IsEmpty)
            return new FirstData(DataKind.NeedMore, null);

        if (data[0] == 0x16)
        {
            if (data.Length < 5)
                return new FirstData(DataKind.NeedMore, null);
            if (data[1] != 3)
                return new FirstData(DataKind.Raw, null);

            var result = TryReadSni(data);
            if (result.NeedMore)
                return new FirstData(DataKind.NeedMore, null);
            return result.Host is { Length: > 0 } host
                ? new FirstData(DataKind.TlsWithSni, host)
                : new FirstData(DataKind.TlsWithoutSni, null);
        }

        int lineEnd = data.IndexOf("\r\n"u8);
        if (lineEnd < 0)
            return data.Length < 8192
                ? new FirstData(DataKind.NeedMore, null)
                : new FirstData(DataKind.Raw, null);

        string line = Encoding.ASCII.GetString(data[..lineEnd]);
        int firstSpace = line.IndexOf(' ');
        if (firstSpace <= 0 || !KnownHttpMethods.Contains(line[..firstSpace].ToUpperInvariant()))
            return new FirstData(DataKind.Raw, null);

        int headersEnd = data.IndexOf("\r\n\r\n"u8);
        if (headersEnd < 0)
            return data.Length < PacketBufferLength
                ? new FirstData(DataKind.NeedMore, null)
                : new FirstData(DataKind.Raw, null);

        string target = line[(firstSpace + 1)..].Split(' ', 2)[0];
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == "http" || absolute.Scheme == "https"))
            return new FirstData(DataKind.Http, absolute.Host);

        string headers = Encoding.ASCII.GetString(data[(lineEnd + 2)..headersEnd]);
        string? hostHeader = headers.Split("\r\n", StringSplitOptions.None)
            .Select(header => header.Split(':', 2))
            .FirstOrDefault(parts => parts.Length == 2 &&
                                     parts[0].Equals("Host", StringComparison.OrdinalIgnoreCase))?[1].Trim();
        string hostName = ExtractHost(hostHeader) ?? mapping?.OriginAddress.ToString() ?? "";
        return hostName.Length > 0
            ? new FirstData(DataKind.Http, hostName)
            : new FirstData(DataKind.Raw, null);
    }

    private static readonly HashSet<string> KnownHttpMethods = new(StringComparer.Ordinal)
    {
        "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "TRACE", "PATCH",
        "PROPFIND", "PROPPATCH", "MKCOL", "COPY", "MOVE", "LOCK", "UNLOCK",
        "REPORT", "SEARCH", "NOTIFY", "SUBSCRIBE", "UNSUBSCRIBE"
    };

    private readonly record struct SniResult(bool NeedMore, string? Host);

    private static SniResult TryReadSni(ReadOnlySpan<byte> data)
    {
        var handshake = new List<byte>();
        int offset = 0;
        int handshakeLength = -1;
        while (offset + 5 <= data.Length)
        {
            if (data[offset] != 0x16 || data[offset + 1] != 3)
                return new SniResult(false, null);

            int recordLength = ReadUInt16(data, offset + 3);
            if (recordLength == 0)
                return new SniResult(false, null);
            if (offset + 5 + recordLength > data.Length)
                return new SniResult(true, null);

            handshake.AddRange(data.Slice(offset + 5, recordLength).ToArray());
            offset += 5 + recordLength;

            if (handshake.Count >= 4 && handshakeLength < 0)
            {
                if (handshake[0] != 1)
                    return new SniResult(false, null);
                handshakeLength = (handshake[1] << 16) | (handshake[2] << 8) | handshake[3];
            }

            if (handshakeLength >= 0 && handshake.Count >= handshakeLength + 4)
                return new SniResult(false, ParseClientHelloSni(CollectionsMarshal.AsSpan(handshake)[..(handshakeLength + 4)]));
        }

        return new SniResult(true, null);
    }

    private static string? ParseClientHelloSni(ReadOnlySpan<byte> handshake)
    {
        int end = handshake.Length;
        int offset = 4;
        if (offset + 34 > end)
            return null;
        offset += 34;

        if (offset >= end)
            return null;
        int sessionLength = handshake[offset++];
        if (offset + sessionLength + 2 > end)
            return null;
        offset += sessionLength;

        int cipherLength = ReadUInt16(handshake, offset);
        offset += 2;
        if (offset + cipherLength + 1 > end)
            return null;
        offset += cipherLength;

        int compressionLength = handshake[offset++];
        if (offset + compressionLength + 2 > end)
            return null;
        offset += compressionLength;

        int extensionsLength = ReadUInt16(handshake, offset);
        offset += 2;
        int extensionsEnd = Math.Min(end, offset + extensionsLength);
        while (offset + 4 <= extensionsEnd)
        {
            int type = ReadUInt16(handshake, offset);
            int length = ReadUInt16(handshake, offset + 2);
            offset += 4;
            if (offset + length > extensionsEnd)
                return null;

            if (type == 0 && length >= 5)
            {
                int listLength = ReadUInt16(handshake, offset);
                int listEnd = Math.Min(offset + length, offset + 2 + listLength);
                int nameOffset = offset + 2;
                while (nameOffset + 3 <= listEnd)
                {
                    byte nameType = handshake[nameOffset];
                    int nameLength = ReadUInt16(handshake, nameOffset + 1);
                    nameOffset += 3;
                    if (nameOffset + nameLength > listEnd)
                        return null;
                    if (nameType == 0)
                    {
                        string name = Encoding.ASCII.GetString(handshake.Slice(nameOffset, nameLength));
                        return name.Length == 0 ? null : name;
                    }
                    nameOffset += nameLength;
                }
                return null;
            }

            offset += length;
        }

        return null;
    }

    private static int ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
        => (bytes[offset] << 8) | bytes[offset + 1];

    private static string? ExtractHost(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
            return null;
        string value = hostHeader.Trim();
        if (Uri.TryCreate("http://" + value, UriKind.Absolute, out var uri))
            return uri.Host;
        return null;
    }

    private bool IsBypassed(string host)
        => Services.Settings.BypassList.Any(pattern => Glob.IsMatch(pattern, host));

    private async Task BridgeThroughProxyAsync(TcpClient client, NetworkStream clientStream,
        NatMapping mapping, string host, byte[] initialData, int initialLength,
        CancellationToken cancellationToken)
    {
        using var proxy = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await proxy.ConnectAsync(IPAddress.Loopback, Services.Settings.Port, cancellationToken)
            .ConfigureAwait(false);
        var proxyStream = proxy.GetStream();

        string authority = FormatAuthority(host, mapping.OriginPort);
        string connect = $"CONNECT {authority} HTTP/1.1\r\n" +
                         $"Host: {authority}\r\n" +
                         "Proxy-Connection: Keep-Alive\r\n" +
                         $"{PidHeader}: {mapping.Pid}\r\n" +
                         $"{TokenHeader}: {_sessionToken}\r\n\r\n";
        byte[] connectBytes = Encoding.ASCII.GetBytes(connect);
        await proxyStream.WriteAsync(connectBytes, cancellationToken).ConfigureAwait(false);

        byte[] response = await ReadHttpHeaderAsync(proxyStream, cancellationToken).ConfigureAwait(false);
        string responseLine = Encoding.ASCII.GetString(response).Split("\r\n", 2)[0];
        if (!responseLine.StartsWith("HTTP/1.", StringComparison.Ordinal) ||
            responseLine.Length < 12 ||
            !int.TryParse(responseLine.AsSpan(9, 3), out int statusCode) ||
            statusCode != 200)
        {
            await clientStream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (initialLength > 0)
            await proxyStream.WriteAsync(initialData.AsMemory(0, initialLength), cancellationToken)
                .ConfigureAwait(false);
        await RelayStreamsAsync(client, clientStream, proxy, proxyStream, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string FormatAuthority(string host, int port)
    {
        string normalized = host.Trim().Trim('[', ']');
        if (IPAddress.TryParse(normalized, out var address) &&
            address.AddressFamily == AddressFamily.InterNetworkV6)
            normalized = $"[{normalized}]";
        else if (!IPAddress.TryParse(normalized, out _))
            normalized = new IdnMapping().GetAscii(normalized);
        return $"{normalized}:{port}";
    }

    private static async Task<byte[]> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        using var header = new MemoryStream();
        byte[] oneByte = new byte[1];
        while (header.Length < 32 * 1024)
        {
            int read = await stream.ReadAsync(oneByte.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("The local proxy closed before returning a CONNECT response.");
            header.WriteByte(oneByte[0]);
            byte[] bytes = header.GetBuffer();
            int length = (int)header.Length;
            if (length >= 4 && bytes[length - 4] == '\r' && bytes[length - 3] == '\n' &&
                bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                return header.ToArray();
        }

        throw new IOException("The local proxy CONNECT response headers exceeded 32 KB.");
    }

    private async Task BridgeRawAsync(TcpClient client, NetworkStream clientStream, NatMapping mapping,
        byte[] initialData, int initialLength, CancellationToken cancellationToken)
    {
        using var upstream = new TcpClient(mapping.OriginAddress.AddressFamily) { NoDelay = true };
        await upstream.ConnectAsync(mapping.OriginAddress, mapping.OriginPort, cancellationToken)
            .ConfigureAwait(false);
        Services.Proxy.RecordRawTunnel(mapping.Pid, mapping.OriginAddress, mapping.OriginPort);

        var upstreamStream = upstream.GetStream();
        if (initialLength > 0)
            await upstreamStream.WriteAsync(initialData.AsMemory(0, initialLength), cancellationToken)
                .ConfigureAwait(false);

        await RelayStreamsAsync(client, clientStream, upstream, upstreamStream, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task RelayStreamsAsync(TcpClient firstClient, NetworkStream first,
        TcpClient secondClient, NetworkStream second, CancellationToken cancellationToken)
    {
        Task firstToSecond = CopyAndShutdownAsync(first, second, secondClient, cancellationToken);
        Task secondToFirst = CopyAndShutdownAsync(second, first, firstClient, cancellationToken);
        await Task.WhenAll(firstToSecond, secondToFirst).ConfigureAwait(false);
    }

    private static async Task CopyAndShutdownAsync(NetworkStream source, NetworkStream destination,
        TcpClient destinationClient, CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, PacketBufferLength, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally
        {
            try { destinationClient.Client.Shutdown(SocketShutdown.Send); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private void SetStatus(string status)
    {
        _status = status;
        try { StatusChanged?.Invoke(); }
        catch (Exception ex) { Debug.WriteLine($"Unable to notify redirector status change: {ex.Message}"); }
    }

    private void FailRuntime(string error)
    {
        lock (_sync)
        {
            if (!IsRunning)
                return;
            LastError = error;
            Debug.WriteLine(error);
            StopCore();
            SetStatus("Unavailable");
        }
    }

    public void Dispose() => Stop();
}
