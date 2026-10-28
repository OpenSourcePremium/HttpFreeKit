using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HttpFreeKit.Core;






public static class EnvironmentManager
{
    private static readonly string[] Names =
    {
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY",
        "REQUESTS_CA_BUNDLE", "SSL_CERT_FILE", "CURL_CA_BUNDLE", "NODE_EXTRA_CA_CERTS", "NODE_USE_ENV_PROXY"
    };

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);

    public static bool IsActive => File.Exists(AppPaths.EnvBackup);

    public static void Enable(int port, string caPemPath, string bundlePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true)
                        ?? throw new InvalidOperationException("Clé Environment introuvable.");

        if (!File.Exists(AppPaths.EnvBackup))
        {
            var backup = new Dictionary<string, string?>();
            foreach (var n in Names) backup[n] = key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            JsonUtil.Save(AppPaths.EnvBackup, backup);
        }

        string url = $"http://127.0.0.1:{port}";
        var values = new Dictionary<string, string>
        {
            ["HTTP_PROXY"] = url,
            ["HTTPS_PROXY"] = url,
            ["NO_PROXY"] = "localhost,127.0.0.1,::1",
            ["REQUESTS_CA_BUNDLE"] = bundlePath,
            ["SSL_CERT_FILE"] = bundlePath,
            ["CURL_CA_BUNDLE"] = bundlePath,
            ["NODE_EXTRA_CA_CERTS"] = caPemPath,
            ["NODE_USE_ENV_PROXY"] = "1"
        };
        foreach (var (k, v) in values) key.SetValue(k, v, RegistryValueKind.String);
        Broadcast();
    }

    public static void Restore()
    {
        if (!File.Exists(AppPaths.EnvBackup)) return;
        var backup = JsonUtil.LoadOrDefault(AppPaths.EnvBackup, () => new Dictionary<string, string?>());

        using (var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true))
        {
            if (key != null)
            {
                foreach (var n in Names)
                {
                    if (backup.TryGetValue(n, out var old) && old != null) key.SetValue(n, old, RegistryValueKind.String);
                    else key.DeleteValue(n, false);
                }
            }
        }
        try { File.Delete(AppPaths.EnvBackup); } catch { }
        Broadcast();
    }

    private static void Broadcast()
    {

        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, UIntPtr.Zero, "Environment", 0x2, 3000, out _);
    }
}
