using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HttpFreeKit.Core;





public static class SystemProxyManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);

    public static bool IsActive => File.Exists(AppPaths.ProxyBackup);

    public static void Enable(string hostPort)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                        ?? throw new InvalidOperationException("Clé Internet Settings introuvable.");

        if (!File.Exists(AppPaths.ProxyBackup))
        {
            var b = new ProxyBackup
            {
                ProxyEnable = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0),
                ProxyServer = key.GetValue("ProxyServer") as string,
                ProxyOverride = key.GetValue("ProxyOverride") as string,
                AutoConfigUrl = key.GetValue("AutoConfigURL") as string,
            };
            JsonUtil.Save(AppPaths.ProxyBackup, b);
        }

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", hostPort, RegistryValueKind.String);
        key.DeleteValue("AutoConfigURL", false);
        Notify();
    }

    public static void Restore()
    {
        if (!File.Exists(AppPaths.ProxyBackup)) return;
        var b = JsonUtil.LoadOrDefault(AppPaths.ProxyBackup, () => new ProxyBackup());

        using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true))
        {
            if (key != null)
            {
                key.SetValue("ProxyEnable", b.ProxyEnable, RegistryValueKind.DWord);
                SetOrDelete(key, "ProxyServer", b.ProxyServer);
                SetOrDelete(key, "ProxyOverride", b.ProxyOverride);
                SetOrDelete(key, "AutoConfigURL", b.AutoConfigUrl);
            }
        }
        try { File.Delete(AppPaths.ProxyBackup); } catch { }
        Notify();
    }


    public static (string Host, int Port)? GetOriginalUpstream()
    {
        try
        {
            int enable;
            string? server;
            if (File.Exists(AppPaths.ProxyBackup))
            {
                var b = JsonUtil.LoadOrDefault(AppPaths.ProxyBackup, () => new ProxyBackup());
                enable = b.ProxyEnable;
                server = b.ProxyServer;
            }
            else
            {
                using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
                enable = Convert.ToInt32(k?.GetValue("ProxyEnable") ?? 0);
                server = k?.GetValue("ProxyServer") as string;
            }

            if (enable != 1 || string.IsNullOrWhiteSpace(server)) return null;

            string entry = server;
            if (server.Contains('='))
            {
                entry = server.Split(';').Select(p => p.Trim())
                    .FirstOrDefault(p => p.StartsWith("https=", StringComparison.OrdinalIgnoreCase)
                                      || p.StartsWith("http=", StringComparison.OrdinalIgnoreCase))
                    ?.Split('=', 2)[1] ?? "";
            }
            entry = entry.Replace("http://", "").Replace("https://", "").TrimEnd('/');

            int i = entry.LastIndexOf(':');
            if (i <= 0 || !int.TryParse(entry[(i + 1)..], out int port)) return null;
            string host = entry[..i];
            if ((host == "127.0.0.1" || host == "localhost") && port == Services.Settings.Port) return null;
            return (host, port);
        }
        catch { return null; }
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, 39 , IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37 , IntPtr.Zero, 0);
    }
}
