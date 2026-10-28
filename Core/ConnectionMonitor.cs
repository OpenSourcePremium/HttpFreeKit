using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace HttpFreeKit.Core;


public sealed class ConnectionMonitor
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    private sealed record Snap(string Key, string Proto, string Local, string Remote, string State, int Pid);

    private static readonly string[] States =
        { "", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED", "FIN_WAIT1", "FIN_WAIT2",
          "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB" };

    public ObservableCollection<ConnectionRow> Rows { get; } = new();
    private readonly Dictionary<string, ConnectionRow> _map = new();
    private readonly DispatcherTimer _timer;
    private bool _busy;

    public ConnectionMonitor()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start() { if (!_timer.IsEnabled) { _timer.Start(); _ = TickAsync(); } }
    public void Stop() => _timer.Stop();

    private async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var snap = await Task.Run(Snapshot);
            Apply(snap);
        }
        catch { }
        finally { _busy = false; }
    }

    private void Apply(List<Snap> snap)
    {
        var seen = new HashSet<string>();
        foreach (var s in snap)
        {
            if (!seen.Add(s.Key)) continue;
            if (_map.TryGetValue(s.Key, out var row)) { row.State = s.State; continue; }

            var pi = ProcessInfo.Get(s.Pid);
            row = new ConnectionRow
            {
                Key = s.Key, Protocol = s.Proto, Local = s.Local, Remote = s.Remote, State = s.State,
                Pid = s.Pid, Process = pi.Name, ProcessPath = pi.Path,
                ProcessIcon = ProcessIconService.PlaceholderIcon
            };
            _map[s.Key] = row;
            Rows.Add(row);
            _ = LoadProcessIconAsync(row);
        }
        foreach (var key in _map.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Rows.Remove(_map[key]);
            _map.Remove(key);
        }
    }

    private async Task LoadProcessIconAsync(ConnectionRow row)
    {
        try
        {
            var icon = await Task.Run(() => ProcessIconService.GetIcon(row.Process, row.ProcessPath));
            if (icon != null && _map.TryGetValue(row.Key, out var current) && ReferenceEquals(current, row))
                row.ProcessIcon = icon;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to set process icon for {row.Process}: {ex.Message}");
        }
    }



    private static List<Snap> Snapshot()
    {
        var list = new List<Snap>();
        foreach (var af in new[] { 2, 23 })
        {
            ReadTcp(list, af);
            ReadUdp(list, af);
        }
        return list;
    }

    private static byte[]? GetTable(bool tcp, int af)
    {
        int size = 0;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                if (size > 0) buf = Marshal.AllocHGlobal(size);
                uint r = tcp
                    ? GetExtendedTcpTable(buf, ref size, false, af, 5 , 0)
                    : GetExtendedUdpTable(buf, ref size, false, af, 1 , 0);
                if (r == 0 && buf != IntPtr.Zero)
                {
                    var data = new byte[size];
                    Marshal.Copy(buf, data, 0, size);
                    return data;
                }
                if (r != 122) return null;
                size += 4096;
            }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }
        return null;
    }

    private static int Port(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    private static int I32(byte[] d, int o) => BitConverter.ToInt32(d, o);

    private static string Ep(byte[] d, int addrOff, int addrLen, int portOff)
    {
        var ip = new IPAddress(d.AsSpan(addrOff, addrLen));
        int port = Port(d, portOff);
        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
    }

    private static void ReadTcp(List<Snap> list, int af)
    {
        var d = GetTable(true, af);
        if (d == null) return;
        int n = I32(d, 0);
        bool v6 = af == 23;
        int rowSize = v6 ? 56 : 24;
        for (int i = 0; i < n; i++)
        {
            int o = 4 + i * rowSize;
            if (o + rowSize > d.Length) break;
            string local, remote; int state, pid;
            if (!v6)
            {
                state = I32(d, o);
                local = Ep(d, o + 4, 4, o + 8);
                remote = Ep(d, o + 12, 4, o + 16);
                pid = I32(d, o + 20);
            }
            else
            {
                local = Ep(d, o, 16, o + 20);
                remote = Ep(d, o + 24, 16, o + 44);
                state = I32(d, o + 48);
                pid = I32(d, o + 52);
            }
            string st = state >= 0 && state < States.Length ? States[state] : state.ToString();
            if (st == "LISTEN") remote = "—";
            list.Add(new Snap($"TCP|{local}|{remote}|{pid}", "TCP", local, remote, st, pid));
        }
    }

    private static void ReadUdp(List<Snap> list, int af)
    {
        var d = GetTable(false, af);
        if (d == null) return;
        int n = I32(d, 0);
        bool v6 = af == 23;
        int rowSize = v6 ? 28 : 12;
        for (int i = 0; i < n; i++)
        {
            int o = 4 + i * rowSize;
            if (o + rowSize > d.Length) break;
            string local; int pid;
            if (!v6) { local = Ep(d, o, 4, o + 4); pid = I32(d, o + 8); }
            else { local = Ep(d, o, 16, o + 20); pid = I32(d, o + 24); }
            list.Add(new Snap($"UDP|{local}|{pid}", "UDP", local, "—", "—", pid));
        }
    }
}
