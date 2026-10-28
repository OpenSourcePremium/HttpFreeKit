using System.Diagnostics;
using System.Text;
using HttpFreeKit.Localization;

namespace HttpFreeKit.Core;


public static class Uninstaller
{
    public static string Run(bool deleteData, bool selfDelete, string language = "en")
    {
        AppPaths.Ensure();
        var log = new StringBuilder();
        void Step(string label, Action action)
        {
            try { action(); log.AppendLine("✔ " + LocalizationService.Translate(label, language)); }
            catch (Exception ex) { log.AppendLine($"✘ {LocalizationService.Translate(label, language)}: {ex.Message}"); }
        }

        Step("Proxy system restored", SystemProxyManager.Restore);
        Step("Environment variables restored", EnvironmentManager.Restore);
        Step("Windows Firewall rules removed", () => new FirewallStore().UnblockAll(includeUnknown: true));
        Step("Root certificate removed", CertificateService.RemoveFromStore);

        if (deleteData)
            Step("Data folder removed", () =>
            {
                bool usesCustomFolder = !string.Equals(
                    AppPaths.DataDir, AppPaths.DefaultDataDir, StringComparison.OrdinalIgnoreCase);
                if (Directory.Exists(AppPaths.DataDir))
                    Directory.Delete(AppPaths.DataDir, true);
                AppPaths.ResetDataDirConfiguration();
                if (usesCustomFolder && Directory.Exists(AppPaths.DefaultDataDir))
                    Directory.Delete(AppPaths.DefaultDataDir, true);
            });

        if (selfDelete)
            Step("Executable deletion scheduled", () =>
            {
                var exe = Environment.ProcessPath!;

                if (!string.Equals(Path.GetFileName(exe), "HTTP FreeKit.exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(LocalizationService.Translate(
                        "Unrecognized executable; deletion skipped", language));
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 >nul & del /f /q \"{exe}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            });

        return log.ToString();
    }
}
