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
        Step("WinDivert driver service removed", RemoveWinDivertService);
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

    private static void RemoveWinDivertService()
    {
        int query = RunSc("query", "WinDivert");
        if (query == 1060)
            return;
        if (query != 0)
            throw new InvalidOperationException($"sc.exe query failed with Windows error {query}.");

        int stop = RunSc("stop", "WinDivert");
        if (stop is not 0 and not 1060 and not 1062)
            throw new InvalidOperationException($"sc.exe stop failed with Windows error {stop}.");

        int delete = RunSc("delete", "WinDivert");
        if (delete is not 0 and not 1060 and not 1072)
            throw new InvalidOperationException($"sc.exe delete failed with Windows error {delete}.");
    }

    private static int RunSc(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start sc.exe.");
        process.WaitForExit();
        return process.ExitCode;
    }
}
