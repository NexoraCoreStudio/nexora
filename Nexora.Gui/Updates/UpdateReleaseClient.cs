using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexora.Gui.DevTools;

namespace Nexora.Gui.Updates;

/// <summary>
/// Updates come ONLY from the public nexora repository (including DEV diagnostics builds).
/// Downloads the release ZIP, validates its GitHub SHA-256, then validates the embedded setup
/// against the in-archive checksum. Does not log credentials or HTTP response bodies.
/// </summary>
internal sealed class UpdateReleaseClient : IDisposable
{
    private const string ReleasesApi = "https://api.github.com/repos/NexoraCoreStudio/nexora/releases?per_page=30";
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const long MaximumSetupBytes = 512L * 1024 * 1024;
    private static readonly Regex DigestPattern = new("^sha256:([0-9a-fA-F]{64})$", RegexOptions.Compiled);
    private static readonly Regex TagPattern = new("^v?(\\d+)\\.(\\d+)\\.(\\d+)(?:-([a-z0-9][a-z0-9.-]*))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly HttpClient _http;

    internal sealed record Release(string Tag, Version Version, string Notes, Uri DownloadUri,
        string Sha256, long Size, bool IsPrerelease);

    public UpdateReleaseClient()
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Nexora", typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.4.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string operation, CancellationToken token)
    {
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            DevConsole.Write(response.IsSuccessStatusCode ? "UPDATE" : "ERROR", operation,
                $"HTTP={(int)response.StatusCode} ({response.StatusCode})");
            return response;
        }
        catch (HttpRequestException ex)
        {
            DevConsole.Write("ERROR", operation,
                ex.StatusCode is { } httpCode
                    ? $"HTTP={(int)httpCode} ({httpCode}); HttpRequestError={ex.HttpRequestError}; HRESULT=0x{ex.HResult:X8}"
                    : $"HTTP=aucune réponse; HttpRequestError={ex.HttpRequestError}; HRESULT=0x{ex.HResult:X8}");
            throw;
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            DevConsole.Write("ERROR", operation, "HTTP=aucune réponse; TIMEOUT");
            throw;
        }
    }

    public async Task<Release?> GetLatestAsync(Version installedVersion, bool includePrereleases, CancellationToken cancellationToken)
    {
        // GitHub /releases/latest returns HTTP 404 when a repository has only pre-releases.
        // The list endpoint returns 200 and lets us explicitly select stable releases.
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        using var response = await SendAsync(request, "github-release-metadata", cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long metadataSize && metadataSize > 2 * 1024 * 1024)
            throw new InvalidDataException("Métadonnées de Release anormalement volumineuses.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream,
            new JsonDocumentOptions { MaxDepth = 16 }, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Liste des Releases GitHub invalide.");

        Release? newest = null;
        int validChannelCount = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False) continue;
            if (!item.TryGetProperty("prerelease", out var preFlag) ||
                preFlag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
            bool isPre = preFlag.GetBoolean();
            if (isPre && !includePrereleases) continue;
            validChannelCount++;
            var candidate = ReadRelease(item, installedVersion, isPre);
            if (candidate is not null &&
                (newest is null || candidate.Version > newest.Version ||
                 (candidate.Version == newest.Version && !candidate.IsPrerelease && newest.IsPrerelease)))
                newest = candidate;
        }
        if (validChannelCount == 0)
            DevConsole.Write("UPDATE", includePrereleases ? "github-release-none" : "github-release-no-stable");
        else if (newest is null)
            DevConsole.Write("UPDATE", "github-release-no-newer", includePrereleases
                ? "aucune version stable/préversion plus récente disponible" : "aucune version stable plus récente disponible");
        return newest;
    }

    private static Release? ReadRelease(JsonElement root, Version installedVersion, bool isPrerelease)
    {
        string tag = root.TryGetProperty("tag_name", out var tagJson) ? tagJson.GetString() ?? "" : "";
        var match = TagPattern.Match(tag);
        if (!match.Success) return null;
        // A tag with a prerelease suffix must also be marked as prerelease on GitHub.
        if (match.Groups[4].Success && !isPrerelease) return null;
        var version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        if (version <= installedVersion) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        string expectedName = $"Nexora-v{version}-win-x64.zip";
        string taggedName = $"Nexora-{tag}-win-x64.zip";
        foreach (var asset in assets.EnumerateArray())
        {
            string fileName = asset.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
            if (fileName != expectedName && fileName != taggedName) continue;
            var digest = asset.TryGetProperty("digest", out var digestProperty)
                ? digestProperty.GetString() : null;
            var digestMatch = DigestPattern.Match(digest ?? "");
            if (!digestMatch.Success)
                throw new InvalidDataException("Le ZIP officiel n'a pas d'empreinte SHA-256 vérifiable sur GitHub.");
            long size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumPackageBytes)
                throw new InvalidDataException("Taille du ZIP officiel invalide.");
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var download) || !IsAllowedReleaseUri(download))
                throw new InvalidDataException("URL de téléchargement non autorisée.");
            string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new Release(tag, version, notes, download, digestMatch.Groups[1].Value, size, isPrerelease);
        }
        // Ignore incomplete Releases without a ZIP, rather than blocking other valid Releases.
        DevConsole.Write("UPDATE", "release-no-compatible-zip", isPrerelease ? "prerelease" : "stable");
        return null;
    }

    public async Task<string> DownloadVerifiedAsync(Release release, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nexora", "Updates");
        Directory.CreateDirectory(folder);
        string packageTemp = Path.Combine(folder, ".download-" + Guid.NewGuid().ToString("N") + ".zip.tmp");
        string setupTemp = Path.Combine(folder, ".extract-" + Guid.NewGuid().ToString("N") + ".exe.tmp");
        string finalSetup = Path.Combine(folder, "Nexora-Setup-" + release.Version + ".exe");
        try
        {
            Uri uri = release.DownloadUri;
            for (int redirects = 0; ; redirects++)
            {
                if (redirects > 4 || !IsAllowedReleaseUri(uri))
                    throw new InvalidDataException("Chaîne de redirection GitHub non autorisée.");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await SendAsync(request, "github-release-download", cancellationToken);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("Redirection GitHub sans destination.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long announced && announced != release.Size)
                    throw new InvalidDataException("Taille téléchargée différente de celle publiée.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(packageTemp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[1024 * 128];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    received += read;
                    if (received > release.Size || received > MaximumPackageBytes)
                        throw new InvalidDataException("Dépassement de la taille attendue du ZIP.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report((int)(90L * received / release.Size));
                }
                await output.FlushAsync(cancellationToken);
                if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset())
                    .Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 GitHub du ZIP incorrect : mise à jour interrompue.");
                break;
            }

            using (var archive = ZipFile.OpenRead(packageTemp))
            {
                if (archive.Entries.Count != 2)
                    throw new InvalidDataException("Le ZIP officiel contient des fichiers inattendus.");
                var setup = archive.GetEntry("Nexora-Setup.exe")
                    ?? throw new InvalidDataException("Nexora-Setup.exe absent du ZIP.");
                var checksumEntry = archive.GetEntry("SHA256SUMS.txt")
                    ?? throw new InvalidDataException("SHA256SUMS.txt absent du ZIP.");
                if (setup.Length <= 0 || setup.Length > MaximumSetupBytes || checksumEntry.Length > 4096)
                    throw new InvalidDataException("Taille de l'installateur ou du manifeste invalide.");
                string checksum;
                using (var reader = new StreamReader(checksumEntry.Open()))
                    checksum = (await reader.ReadToEndAsync(cancellationToken)).Trim();
                var checksumMatch = Regex.Match(checksum, @"^([0-9a-fA-F]{64})  Nexora-Setup\.exe$");
                if (!checksumMatch.Success)
                    throw new InvalidDataException("SHA256SUMS.txt du ZIP invalide.");
                await using var input = setup.Open();
                await using var output = new FileStream(setupTemp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 1024 * 128, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[1024 * 128];
                long size = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    size += read;
                    if (size > setup.Length) throw new InvalidDataException("Extraction ZIP trop volumineuse.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(90 + (int)(10L * size / setup.Length));
                }
                await output.FlushAsync(cancellationToken);
                if (size != setup.Length || !Convert.ToHexString(hash.GetHashAndReset())
                    .Equals(checksumMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 de Nexora-Setup.exe incorrect.");
            }
            File.Move(setupTemp, finalSetup, true);
            progress?.Report(100);
            return finalSetup;
        }
        finally
        {
            try { if (File.Exists(packageTemp)) File.Delete(packageTemp); } catch { }
            try { if (File.Exists(setupTemp)) File.Delete(setupTemp); } catch { }
        }
    }

    private static bool IsAllowedReleaseUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _http.Dispose();
}
