using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexora.Gui.Updates;

/// <summary>
/// Read-only GitHub Releases client. Release metadata and asset digest are obtained
/// from GitHub over TLS. Never execute an unverified or partially downloaded file.
/// </summary>
internal sealed class UpdateReleaseClient : IDisposable
{
#if NEXORA_DEV
    private const string LatestReleaseApi = "https://api.github.com/repos/NexoraCoreStudio/nexora-dev/releases?per_page=20";
#else
    private const string LatestReleaseApi = "https://api.github.com/repos/NexoraCoreStudio/nexora/releases/latest";
#endif
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private static readonly Regex Sha256Pattern = new("^sha256:([0-9a-fA-F]{64})$", RegexOptions.Compiled);
    private static readonly Regex TagPattern = new("^v?(\\d+)\\.(\\d+)\\.(\\d+)(?:-dev\\.(\\d+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly HttpClient _http;

    internal sealed record Release(string Tag, Version Version, string Notes, Uri DownloadUri,
        string Sha256, long Size);

    public UpdateReleaseClient()
    {
        // Validate each redirect ourselves. Never silently follow a redirect to an unknown host.
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Nexora", "1.3.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<Release?> GetLatestAsync(Version installedVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long metadataSize && metadataSize > 2 * 1024 * 1024)
            throw new InvalidDataException("Métadonnées de Release anormalement volumineuses.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 16 }, cancellationToken);
#if NEXORA_DEV
        // The development channel includes prereleases, which /releases/latest would hide.
        Release? newest = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var current = ReadRelease(item, installedVersion, requireDev: true);
            if (current is not null && (newest is null || current.Version > newest.Version)) newest = current;
        }
        return newest;
#else
        return ReadRelease(document.RootElement, installedVersion, requireDev: false);
#endif
    }

    private static Release? ReadRelease(JsonElement root, Version installedVersion, bool requireDev)
    {
        if (root.GetProperty("draft").GetBoolean()) return null;
        bool pre = root.GetProperty("prerelease").GetBoolean();
        if (pre != requireDev) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var match = TagPattern.Match(tag);
        if (!match.Success || match.Groups[4].Success != requireDev) return null;
        var version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        if (version <= installedVersion) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != "Nexora-Setup.exe") continue;
            var digest = asset.TryGetProperty("digest", out var digestProperty)
                ? digestProperty.GetString() : null;
            var digestMatch = Sha256Pattern.Match(digest ?? "");
            if (!digestMatch.Success)
                throw new InvalidDataException("Le Setup officiel ne possède pas d'empreinte SHA-256 GitHub vérifiable.");
            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumInstallerBytes)
                throw new InvalidDataException("Taille de l'installateur invalide.");
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var download) || !IsAllowedReleaseUri(download))
                throw new InvalidDataException("URL de téléchargement non autorisée.");
            string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new Release(tag, version, notes, download, digestMatch.Groups[1].Value, size);
        }
        throw new InvalidDataException("La Release officielle ne contient pas Nexora-Setup.exe.");
    }

    public async Task<string> DownloadVerifiedAsync(Release release, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nexora", "Updates");
        Directory.CreateDirectory(folder);
        // Random temporary name protects against collisions between multiple launches.
        string temporary = Path.Combine(folder, ".download-" + Guid.NewGuid().ToString("N") + ".tmp");
        string final = Path.Combine(folder, "Nexora-Setup-" + release.Version + ".exe");
        try
        {
            var uri = release.DownloadUri;
            for (int redirects = 0; ; redirects++)
            {
                if (redirects > 4 || !IsAllowedReleaseUri(uri))
                    throw new InvalidDataException("Chaîne de redirection GitHub non autorisée.");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("Redirection sans destination.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength && contentLength != release.Size)
                    throw new InvalidDataException("La taille reçue ne correspond pas à la Release.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 128];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    received += read;
                    if (received > release.Size || received > MaximumInstallerBytes)
                        throw new InvalidDataException("Le téléchargement dépasse la taille annoncée.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report((int)(100L * received / release.Size));
                }
                await output.FlushAsync(cancellationToken);
                if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset())
                        .Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 du Setup différent de celui annoncé par GitHub : installation annulée.");
                break;
            }
            File.Move(temporary, final, true);
            progress?.Report(100);
            return final;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsAllowedReleaseUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _http.Dispose();
}
