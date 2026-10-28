using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HttpFreeKit.Core;

public static class ProcessInfo
{
    private static readonly ConcurrentDictionary<int, (string Name, string? Path, long Ticks)> Cache = new();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    public static (string Name, string? Path) Get(int pid)
    {
        if (pid <= 0) return ("?", null);
        if (pid == 4) return ("System", null);

        long now = Environment.TickCount64;
        if (Cache.TryGetValue(pid, out var c) && now - c.Ticks < 30_000) return (c.Name, c.Path);

        string? path = null;
        var h = OpenProcess(0x1000 , false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
            }
            finally { CloseHandle(h); }
        }

        string name;
        if (path != null) name = System.IO.Path.GetFileName(path);
        else
        {
            try { name = Process.GetProcessById(pid).ProcessName + ".exe"; }
            catch { name = $"PID {pid}"; }
        }

        Cache[pid] = (name, path, now);
        return (name, path);
    }
}
