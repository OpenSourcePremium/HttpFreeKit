using System.Security.Cryptography.X509Certificates;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Core;


public static class CertificateService
{
    private static bool IsApplicationCertificate(X509Certificate2 certificate)
        => certificate.Subject.Contains(ProxyService.CaName, StringComparison.OrdinalIgnoreCase)
            || certificate.Subject.Contains(ProxyService.LegacyCaName, StringComparison.OrdinalIgnoreCase);

    private static bool IsOurs(X509Certificate2 c)
    {
        if (IsApplicationCertificate(c)) return true;
        var thumb = AppSettings.Load().CaThumbprint;
        return !string.IsNullOrEmpty(thumb) && string.Equals(c.Thumbprint, thumb, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInstalled()
    {
        try
        {
            var thumb = AppSettings.Load().CaThumbprint;
            if (string.IsNullOrEmpty(thumb)) return false;
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Cast<X509Certificate2>()
                .Any(c => string.Equals(c.Thumbprint, thumb, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }


    public static void RemoveStale(string currentThumbprint)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var c in store.Certificates.Cast<X509Certificate2>()
                     .Where(c => IsApplicationCertificate(c)
                                 && !string.Equals(c.Thumbprint, currentThumbprint, StringComparison.OrdinalIgnoreCase))
                     .ToList())
            store.Remove(c);
    }

    private static string Pem(byte[] der) =>
        "-----BEGIN CERTIFICATE-----\n" +
        Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n") +
        "\n-----END CERTIFICATE-----\n";





    public static void WriteBundles()
    {
        AppPaths.Ensure();


        byte[] ours; string thumb;
        try
        {
            using var pfx = new X509Certificate2(AppPaths.CaPfx, ProxyService.CaPassword, X509KeyStorageFlags.EphemeralKeySet);
            ours = pfx.RawData;
            thumb = pfx.Thumbprint;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(LocalizationService.Format(
                "Unable to read root certificate: {0}", Services.Settings.Language, ex.Message), ex);
        }

        var others = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.Root, loc);
                store.Open(OpenFlags.ReadOnly);
                foreach (var c in store.Certificates.Cast<X509Certificate2>())
                {
                    if (IsApplicationCertificate(c)) continue;
                    if (string.Equals(c.Thumbprint, thumb, StringComparison.OrdinalIgnoreCase)) continue;
                    others.TryAdd(c.Thumbprint, c.RawData);
                }
            }
            catch { }
        }

        File.WriteAllText(AppPaths.CaPem, Pem(ours), System.Text.Encoding.ASCII);
        File.WriteAllText(AppPaths.CaBundle,
            Pem(ours) + string.Concat(others.Values.Select(Pem)), System.Text.Encoding.ASCII);
    }

    public static void RemoveFromStore()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var c in store.Certificates.Cast<X509Certificate2>().Where(IsOurs).ToList())
            store.Remove(c);
    }
}
