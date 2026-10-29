using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HttpFreeKit.Core;

public enum UpdateAvailability
{
    Current,
    Available,
    InstallerMissing
}

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string LatestVersion,
    UpdateAvailability Availability,
    UpdateRelease? Release);

public sealed record UpdateRelease(
    string Version,
    string TagName,
    string Title,
    string Notes,
    string Url,
    UpdateInstaller? Installer);

public sealed record UpdateInstaller(
    string Name,
    Uri DownloadUri,
    long Size,
    string? Sha256);

public sealed class UpdateService
{
    private const string Repository = "OpenSourcePremium/HttpFreeKit";
    private const long MaximumInstallerSize = 512L * 1024 * 1024;
    private const int MaximumMetadataSize = 8 * 1024 * 1024;
    private static readonly HttpClient Client = CreateHttpClient();

    public static string CurrentVersion
    {
        get
        {
            Version? version = typeof(UpdateService).Assembly.GetName().Version;
            return version is null ? "0.0.0" : FormatVersion(version);
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ParsedReleaseVersion currentVersion = ParseReleaseVersion(CurrentVersion);
        UpdateRelease? latestUpdate = null;
        ParsedReleaseVersion latestVersion = currentVersion;

        foreach (GitHubRelease release in await GetAllReleasesAsync(cancellationToken))
        {
            if (release.Draft || !TryParseReleaseVersion(release.TagName, out ParsedReleaseVersion releaseVersion)
                || releaseVersion.CompareTo(currentVersion) <= 0)
                continue;

            if (releaseVersion.CompareTo(latestVersion) < 0)
                continue;

            UpdateRelease candidate = CreateRelease(release, releaseVersion);
            if (releaseVersion.CompareTo(latestVersion) > 0
                || latestUpdate is null
                || (latestUpdate.Installer is null && candidate.Installer is not null))
            {
                latestVersion = releaseVersion;
                latestUpdate = candidate;
            }
        }

        if (latestUpdate is null)
            return new(CurrentVersion, CurrentVersion, UpdateAvailability.Current, null);

        return new(CurrentVersion, latestUpdate.Version,
            latestUpdate.Installer is null ? UpdateAvailability.InstallerMissing : UpdateAvailability.Available,
            latestUpdate);
    }

    public async Task<UpdateRelease?> GetCurrentReleaseAsync(CancellationToken cancellationToken = default)
    {
        ParsedReleaseVersion currentVersion = ParseReleaseVersion(CurrentVersion);
        string version = currentVersion.Normalized;
        string[] tags = { $"V{version}", $"v{version}", version };

        foreach (string tag in tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            GitHubRelease? release = await GetReleaseByTagAsync(tag, cancellationToken);
            if (release is null)
                continue;

            if (!TryParseReleaseVersion(release.TagName, out ParsedReleaseVersion releaseVersion)
                || releaseVersion.CompareTo(currentVersion) != 0)
                throw new InvalidDataException(
                    $"The release tag {release.TagName} does not match the installed version {version}.");

            return CreateRelease(release, releaseVersion);
        }

        return null;
    }

    public async Task DownloadAndLaunchInstallerAsync(
        UpdateRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        UpdateInstaller installer = release.Installer
            ?? throw new InvalidOperationException("The release does not contain a compatible Windows x64 installer.");

        string expectedName = GetInstallerName(release.Version);
        if (!string.Equals(installer.Name, expectedName, StringComparison.Ordinal))
            throw new InvalidDataException("The release installer filename does not match the expected versioned filename.");
        if (!IsTrustedDownloadUri(installer.DownloadUri))
            throw new InvalidDataException("The release installer URL is not a trusted HTTPS GitHub release URL.");
        if (installer.Size is <= 0 or > MaximumInstallerSize)
            throw new InvalidDataException("The release installer has an invalid or excessive file size.");

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"HTTPFreeKit-Update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string installerPath = Path.Combine(temporaryDirectory, installer.Name);
        bool launchStarted = false;

        try
        {
            // Le téléchargement est isolé dans sa propre méthode : à son retour, le FileStream est
            // entièrement fermé et le fichier n'est plus verrouillé (FileShare.None).
            string actualHash = await DownloadInstallerFileAsync(installer, installerPath, progress, cancellationToken);

            if (installer.Sha256 is { Length: > 0 } expectedHash
                && !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualHash),
                    Encoding.ASCII.GetBytes(expectedHash)))
                throw new InvalidDataException("The downloaded installer failed its SHA-256 integrity check.");

            cancellationToken.ThrowIfCancellationRequested();
            string? applicationPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(applicationPath))
                throw new InvalidOperationException("The current application path could not be determined.");
            string applicationDirectory = Path.GetDirectoryName(applicationPath)
                ?? throw new InvalidOperationException("The current application directory could not be determined.");

            var startInfo = new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            startInfo.ArgumentList.Add("/SILENT");
            startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");
            startInfo.ArgumentList.Add("/FORCECLOSEAPPLICATIONS");
            startInfo.ArgumentList.Add($"/DIR={applicationDirectory}");
            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("Windows did not start the update installer.");

            launchStarted = true;
            StartRelaunchHelper(installer.Name, installerPath, temporaryDirectory, applicationPath);
        }
        finally
        {
            if (!launchStarted)
            {
                try
                {
                    if (File.Exists(installerPath))
                        File.Delete(installerPath);
                    if (Directory.Exists(temporaryDirectory))
                        Directory.Delete(temporaryDirectory);
                }
                catch (IOException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Unable to remove temporary update files from {temporaryDirectory}.");
                }
                catch (UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Unable to remove temporary update files from {temporaryDirectory}.");
                }
            }
        }
    }

    // Télécharge l'installeur vers installerPath, vérifie sa taille, et renvoie son SHA-256 (hex majuscule).
    // Tous les flux (réseau + fichier) sont fermés quand la méthode se termine.
    private static async Task<string> DownloadInstallerFileAsync(
        UpdateInstaller installer,
        string installerPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, installer.DownloadUri);
        using HttpResponseMessage response = await Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null
            || finalUri.Scheme != Uri.UriSchemeHttps
            || !(finalUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                 || finalUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("GitHub redirected the installer download to an untrusted host.");

        if (response.Content.Headers.ContentLength is long contentLength
            && (contentLength != installer.Size || contentLength > MaximumInstallerSize))
            throw new InvalidDataException("The downloaded installer size does not match the release metadata.");

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long totalBytes = 0;

        while (true)
        {
            int bytesRead = await input.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
                break;

            totalBytes += bytesRead;
            if (totalBytes > MaximumInstallerSize || totalBytes > installer.Size)
                throw new InvalidDataException("The downloaded installer exceeds its declared size.");

            hash.AppendData(buffer, 0, bytesRead);
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            progress?.Report((double)totalBytes / installer.Size);
        }

        if (totalBytes != installer.Size)
            throw new InvalidDataException("The downloaded installer is incomplete.");

        await output.FlushAsync(cancellationToken);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // The installer runs silently and closes this app, so nothing restarts it afterwards.
    // A hidden PowerShell process waits for the installer to exit, then starts the updated app
    // unless the installer's own [Run] entry already did. This process inherits this app's
    // administrator elevation (requireAdministrator manifest), so no extra UAC prompt appears.
    private static void StartRelaunchHelper(
        string installerFileName,
        string installerPath,
        string temporaryDirectory,
        string? applicationPath)
    {
        if (string.IsNullOrWhiteSpace(applicationPath))
            return;

        try
        {
            static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

            string installerProcessName = Path.GetFileNameWithoutExtension(installerFileName);
            string applicationProcessName = Path.GetFileNameWithoutExtension(applicationPath);
            string workingDirectory = Path.GetDirectoryName(applicationPath) ?? AppContext.BaseDirectory;
            string script =
                $"$deadline = (Get-Date).AddMinutes(15); " +
                $"while ((Get-Process -Name {Quote(installerProcessName)} -ErrorAction SilentlyContinue) " +
                "-and ((Get-Date) -lt $deadline)) { Start-Sleep -Milliseconds 500 }; " +
                "Start-Sleep -Seconds 2; " +
                $"if (-not (Get-Process -Name {Quote(applicationProcessName)} -ErrorAction SilentlyContinue) " +
                $"-and (Test-Path -LiteralPath {Quote(applicationPath)})) {{ " +
                $"Start-Process -FilePath {Quote(applicationPath)} -WorkingDirectory {Quote(workingDirectory)} }}; " +
                $"Remove-Item -LiteralPath {Quote(installerPath)} -Force -ErrorAction SilentlyContinue; " +
                $"Remove-Item -LiteralPath {Quote(temporaryDirectory)} -Force -ErrorAction SilentlyContinue";

            var helper = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            helper.ArgumentList.Add("-NoProfile");
            helper.ArgumentList.Add("-NonInteractive");
            helper.ArgumentList.Add("-ExecutionPolicy");
            helper.ArgumentList.Add("Bypass");
            helper.ArgumentList.Add("-EncodedCommand");
            helper.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            Process.Start(helper)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to schedule the application relaunch: {ex.Message}");
        }
    }

    public static string FormatReleaseNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "No release notes were provided.";

        string text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^#{1,6}\s*", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\s*[-*]\s+", "• ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(\*\*|__)(.*?)\1", "$2");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<!`)`([^`]+)`(?!`)", "$1");
        return text.Trim();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"HTTP-FreeKit/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return client;
    }

    private static async Task<string> GetTextAsync(
        string address,
        int maximumBytes,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        using HttpResponseMessage response = await Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return "";

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) for {address}.");

        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
            throw new InvalidDataException("GitHub returned unexpectedly large update metadata.");

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(chunk, cancellationToken);
            if (count == 0)
                break;
            if (buffer.Length + count > maximumBytes)
                throw new InvalidDataException("GitHub returned unexpectedly large update metadata.");
            buffer.Write(chunk, 0, count);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
    }

    private static async Task<IReadOnlyList<GitHubRelease>> GetAllReleasesAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 20;
        var releases = new List<GitHubRelease>();

        for (int page = 1; ; page++)
        {
            string address = $"https://api.github.com/repos/{Repository}/releases?per_page={pageSize}&page={page}";
            string json = await GetTextAsync(address, MaximumMetadataSize, cancellationToken);
            GitHubRelease[] pageReleases = JsonSerializer.Deserialize<GitHubRelease[]>(json)
                ?? throw new InvalidDataException("GitHub returned an invalid releases list.");

            foreach (GitHubRelease? release in pageReleases)
            {
                if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                    throw new InvalidDataException("GitHub returned release metadata without a tag.");
                releases.Add(release);
            }

            if (pageReleases.Length < pageSize)
                return releases;
        }
    }

    private static async Task<GitHubRelease?> GetReleaseByTagAsync(
        string tag,
        CancellationToken cancellationToken)
    {
        string escapedTag = Uri.EscapeDataString(tag);
        string address = $"https://api.github.com/repos/{Repository}/releases/tags/{escapedTag}";
        string json = await GetTextAsync(address, MaximumMetadataSize, cancellationToken, allowNotFound: true);
        return json.Length == 0 ? null : DeserializeRelease(json);
    }

    private static GitHubRelease DeserializeRelease(string json)
    {
        GitHubRelease? release = JsonSerializer.Deserialize<GitHubRelease>(json);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            throw new InvalidDataException("GitHub returned release metadata without a tag.");
        return release;
    }

    private static UpdateRelease CreateRelease(GitHubRelease release, ParsedReleaseVersion version)
    {
        string normalizedVersion = version.Normalized;
        string expectedName = GetInstallerName(normalizedVersion);
        GitHubAsset? asset = (release.Assets ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, expectedName, StringComparison.Ordinal));
        UpdateInstaller? installer = null;

        if (asset is not null)
        {
            if (asset.Size is <= 0 or > MaximumInstallerSize)
                throw new InvalidDataException("GitHub returned an invalid size for the release installer.");
            if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri? downloadUri)
                || !IsTrustedDownloadUri(downloadUri))
                throw new InvalidDataException("GitHub returned an untrusted URL for the release installer.");

            string? digest = asset.Digest;
            if (digest is not null)
            {
                const string prefix = "sha256:";
                if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || digest.Length != prefix.Length + 64
                    || !digest.AsSpan(prefix.Length).ToArray().All(Uri.IsHexDigit))
                    throw new InvalidDataException("GitHub returned an invalid SHA-256 digest for the installer.");
                digest = digest[prefix.Length..].ToUpperInvariant();
            }

            installer = new UpdateInstaller(asset.Name, downloadUri, asset.Size, digest);
        }

        return new UpdateRelease(
            normalizedVersion,
            release.TagName,
            string.IsNullOrWhiteSpace(release.Name) ? $"HTTP FreeKit {normalizedVersion}" : release.Name,
            release.Body ?? "",
            release.HtmlUrl ?? $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(release.TagName)}",
            installer);
    }

    private static bool IsTrustedDownloadUri(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps
           && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    private static string GetInstallerName(string version) => $"HTTP-FreeKit-Setup-{version}-win-x64.exe";

    private static ParsedReleaseVersion ParseReleaseVersion(string value)
    {
        if (!TryParseReleaseVersion(value, out ParsedReleaseVersion version))
            throw new InvalidDataException($"Invalid HTTP FreeKit version value: {value.Trim()}");
        return version;
    }

    private static bool TryParseReleaseVersion(string value, out ParsedReleaseVersion parsed)
    {
        parsed = default;
        string normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        string? buildMetadata = null;
        int buildSeparator = normalized.IndexOf('+');
        if (buildSeparator >= 0)
        {
            buildMetadata = normalized[(buildSeparator + 1)..];
            normalized = normalized[..buildSeparator];
            if (!IsValidVersionIdentifiers(buildMetadata))
                return false;
        }

        string? prerelease = null;
        int prereleaseSeparator = normalized.IndexOf('-');
        if (prereleaseSeparator >= 0)
        {
            prerelease = normalized[(prereleaseSeparator + 1)..];
            normalized = normalized[..prereleaseSeparator];
            if (!IsValidVersionIdentifiers(prerelease))
                return false;
        }

        if (!Version.TryParse(normalized, out Version? numericVersion)
            || numericVersion.Major < 0
            || numericVersion.Minor < 0)
            return false;

        var numeric = new Version(numericVersion.Major, numericVersion.Minor, Math.Max(numericVersion.Build, 0));
        string formatted = FormatVersion(numeric);
        if (prerelease is not null)
            formatted += $"-{prerelease}";
        if (buildMetadata is not null)
            formatted += $"+{buildMetadata}";
        parsed = new ParsedReleaseVersion(numeric, prerelease, formatted);
        return true;
    }

    private static string FormatVersion(Version version)
        => $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    private static bool IsValidVersionIdentifiers(string identifiers)
        => identifiers.Length > 0
           && identifiers.Split('.').All(identifier =>
               identifier.Length > 0 && identifier.All(character =>
                   char.IsAsciiLetterOrDigit(character) || character == '-'));

    private readonly record struct ParsedReleaseVersion(
        Version Numeric,
        string? Prerelease,
        string Normalized) : IComparable<ParsedReleaseVersion>
    {
        public int CompareTo(ParsedReleaseVersion other)
        {
            int numericComparison = Numeric.CompareTo(other.Numeric);
            if (numericComparison != 0)
                return numericComparison;
            if (Prerelease is null)
                return other.Prerelease is null ? 0 : 1;
            if (other.Prerelease is null)
                return -1;

            string[] identifiers = Prerelease.Split('.');
            string[] otherIdentifiers = other.Prerelease.Split('.');
            for (int index = 0; index < Math.Min(identifiers.Length, otherIdentifiers.Length); index++)
            {
                string identifier = identifiers[index];
                string otherIdentifier = otherIdentifiers[index];
                bool isNumeric = identifier.All(char.IsAsciiDigit);
                bool otherIsNumeric = otherIdentifier.All(char.IsAsciiDigit);

                int comparison;
                if (isNumeric && otherIsNumeric)
                    comparison = CompareNumericIdentifiers(identifier, otherIdentifier);
                else if (isNumeric != otherIsNumeric)
                    comparison = isNumeric ? -1 : 1;
                else
                    comparison = string.CompareOrdinal(identifier, otherIdentifier);

                if (comparison != 0)
                    return comparison;
            }

            return identifiers.Length.CompareTo(otherIdentifiers.Length);
        }

        private static int CompareNumericIdentifiers(string left, string right)
        {
            int leftStart = left.TrimStart('0').Length;
            int rightStart = right.TrimStart('0').Length;
            int lengthComparison = leftStart.CompareTo(rightStart);
            if (lengthComparison != 0)
                return lengthComparison;

            string normalizedLeft = leftStart == 0 ? "0" : left[^leftStart..];
            string normalizedRight = rightStart == 0 ? "0" : right[^rightStart..];
            return string.CompareOrdinal(normalizedLeft, normalizedRight);
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}