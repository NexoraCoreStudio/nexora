using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexora.Gui.Updates;

/// <summary>
/// Verifies installed application binaries against a SHA-256 file manifest published as a
/// GitHub Release asset. The manifest itself MUST have a SHA-256 digest supplied by the
/// GitHub Release API. User data (Vault, PIN, credentials, logs) is never uploaded or read.
/// This is a comparison with the GitHub publication, not a code-signature or malware audit.
/// </summary>
internal sealed class InstallationIntegrityService : IDisposable
{
    private const string Api = "https://api.github.com/repos/NexoraCoreStudio/nexora/releases?per_page=50";
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private const int MaximumFiles = 10_000;
    private static readonly Regex VersionPattern = new(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([a-z0-9][a-z0-9.-]*))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DigestPattern = new(@"^sha256:([0-9a-fA-F]{64})$", RegexOptions.Compiled);
    private static readonly Regex ManifestLinePattern = new(@"^([0-9a-fA-F]{64})  (Nexora/.+)$", RegexOptions.Compiled);
    private readonly HttpClient _client;

    internal sealed record FileProblem(string RelativePath, string Reason);

    internal sealed class Report
    {
        public string InstalledVersion { get; init; } = "";
        public string LatestVersion { get; set; } = "indisponible";
        public string ComparedRelease { get; set; } = "aucune";
        public bool LatestMatchesInstalled { get; set; }
        public int CheckedFiles { get; set; }
        public int ExpectedFiles { get; set; }
        public bool ManifestVerified { get; set; }
        public bool ManagedInstall { get; init; }
        public List<FileProblem> Problems { get; } = [];
        public List<FileProblem> UnexpectedBinaries { get; } = [];
        public string? ReasonNotVerified { get; set; }

        public string Status => !ManifestVerified ? "VÉRIFICATION IMPOSSIBLE" :
            Problems.Count > 0 ? "FICHIERS MODIFIÉS OU MANQUANTS" :
            UnexpectedBinaries.Count > 0 ? "FICHIERS EXÉCUTABLES INATTENDUS" :
            !LatestMatchesInstalled ? "FICHIERS CONFORMES · VERSION DIFFÉRENTE DE LA DERNIÈRE" :
            "FICHIERS CONFORMES À LA RELEASE GITHUB";

        public string ToDisplayText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("NEXORA · DIAGNOSTIC D'INTÉGRITÉ");
            sb.AppendLine("Version installée : " + InstalledVersion);
            sb.AppendLine("Dernière version GitHub sélectionnée : " + LatestVersion);
            sb.AppendLine("Release de référence : " + ComparedRelease);
            sb.AppendLine("Installation : " + (ManagedInstall ? "gérée par Nexora Installer" : "portable ou développement (non enregistrée)"));
            sb.AppendLine("État : " + Status);
            if (!ManifestVerified)
            {
                sb.AppendLine("Détails : " + (ReasonNotVerified ?? "Manifeste absent ou non vérifiable."));
                sb.AppendLine("Aucune conformité cryptographique n'est affirmée.");
            }
            else
            {
                sb.AppendLine($"Fichiers conformes : {CheckedFiles}/{ExpectedFiles}");
                sb.AppendLine($"Anomalies : {Problems.Count} ; exécutables supplémentaires : {UnexpectedBinaries.Count}");
                foreach (var problem in Problems.Take(100))
                    sb.AppendLine("[ANOMALIE] " + problem.RelativePath + " — " + problem.Reason);
                foreach (var extra in UnexpectedBinaries.Take(100))
                    sb.AppendLine("[INATTENDU] " + extra.RelativePath + " — " + extra.Reason);
                if (Problems.Count + UnexpectedBinaries.Count > 200)
                    sb.AppendLine("La liste est limitée aux 200 premières anomalies.");
            }
            sb.AppendLine();
            sb.AppendLine("Le coffre Nexora.vault, les identifiants, PIN, journaux et autres données utilisateur ne sont ni lus ni envoyés.");
            sb.AppendLine("Les SHA-256 démontrent la concordance avec les fichiers GitHub, pas l'absence de malware ni une signature du développeur.");
            return sb.ToString();
        }
    }

    private sealed record ReleaseInfo(Version Version, string Tag, bool Prerelease, JsonElement Assets);

    public InstallationIntegrityService()
    {
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Nexora", "1.5"));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<Report> VerifyAsync(bool includePrereleases, IProgress<string>? progress, CancellationToken token)
    {
        string installed = GetInstalledVersion();
        string root = Path.GetFullPath(AppContext.BaseDirectory);
        var report = new Report
        {
            InstalledVersion = installed,
            ManagedInstall = File.Exists(Path.Combine(root, ".nexora-install-state.json"))
        };
        try
        {
            progress?.Report("Recherche des Releases officielles sur GitHub…");
            using var response = await _client.GetAsync(Api, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            byte[] json = await ReadLimitedAsync(response, MaximumManifestBytes, token);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 20 });
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Réponse GitHub invalide.");
            var releases = new List<ReleaseInfo>();
            foreach (JsonElement release in doc.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object ||
                    !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                    !release.TryGetProperty("prerelease", out var pre) ||
                    pre.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                bool isPre = pre.GetBoolean();
                if (isPre && !includePrereleases) continue;
                if (!release.TryGetProperty("tag_name", out var tagProp) || tagProp.ValueKind != JsonValueKind.String)
                    continue;
                string tag = tagProp.GetString() ?? "";
                var match = VersionPattern.Match(tag);
                if (!match.Success || (match.Groups[4].Success && !isPre) ||
                    !release.TryGetProperty("assets", out var assets) ||
                    assets.ValueKind != JsonValueKind.Array) continue;
                releases.Add(new ReleaseInfo(new Version(int.Parse(match.Groups[1].Value),
                    int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)), tag, isPre, assets));
            }
            var latest = releases.OrderByDescending(x => x.Version).ThenBy(x => x.Prerelease).FirstOrDefault();
            if (latest is null)
            {
                report.ReasonNotVerified = "Aucune Release GitHub utilisable n'est disponible dans les 50 dernières Releases.";
                return report;
            }
            report.LatestVersion = latest.Tag;
            report.LatestMatchesInstalled = latest.Version.ToString(3) == installed && !latest.Prerelease;
            // Never compare a 1.4 installation against files from 1.5, or vice versa.
            var sameVersion = releases.Where(x => x.Version.ToString(3) == installed)
                .OrderBy(x => x.Prerelease).FirstOrDefault();
            if (sameVersion is null)
            {
                report.ReasonNotVerified = $"Aucune Release publiée correspondant exactement à {installed}. " +
                    "Il est impossible d'utiliser les empreintes d'une autre version.";
                return report;
            }
            report.ComparedRelease = sameVersion.Tag;
            string manifestName = $"Nexora-v{installed}-filehashes.txt";
            JsonElement? manifestAsset = null;
            foreach (JsonElement asset in sameVersion.Assets.EnumerateArray())
                if (asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("name", out var name) &&
                    name.ValueKind == JsonValueKind.String && name.GetString() == manifestName)
                    manifestAsset = asset;
            if (manifestAsset is null)
            {
                report.ReasonNotVerified = "Le manifeste de fichiers " + manifestName +
                    " n'est pas publié. Republiez les deux ZIP et le manifeste avec Nexora Admin mis à jour.";
                return report;
            }
            JsonElement assetInfo = manifestAsset.Value;
            string digest = assetInfo.TryGetProperty("digest", out var digestProp) &&
                digestProp.ValueKind == JsonValueKind.String ? digestProp.GetString() ?? "" : "";
            var expectedDigest = DigestPattern.Match(digest);
            if (!expectedDigest.Success)
            {
                report.ReasonNotVerified = "Le manifeste GitHub ne possède pas de digest SHA-256 authentifiable.";
                return report;
            }
            long length = assetInfo.GetProperty("size").GetInt64();
            if (length is <= 0 or > MaximumManifestBytes)
                throw new InvalidDataException("Taille du manifeste GitHub invalide.");
            string source = assetInfo.GetProperty("browser_download_url").GetString() ?? "";
            if (!Uri.TryCreate(source, UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps ||
                url.Host != "github.com" ||
                !url.AbsolutePath.StartsWith("/NexoraCoreStudio/nexora/releases/download/", StringComparison.Ordinal) ||
                !url.AbsolutePath.EndsWith("/" + manifestName, StringComparison.Ordinal))
                throw new InvalidDataException("URL du manifeste GitHub non autorisée.");

            progress?.Report("Téléchargement du manifeste et vérification SHA-256…");
            using var manifestResponse = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            manifestResponse.EnsureSuccessStatusCode();
            byte[] content = await ReadLimitedAsync(manifestResponse, MaximumManifestBytes, token);
            string actualDigest = Convert.ToHexString(SHA256.HashData(content));
            if (!actualDigest.Equals(expectedDigest.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Le SHA-256 du manifeste GitHub ne correspond pas au fichier reçu.");
            var expectedFiles = ParseManifest(content);
            report.ManifestVerified = true;
            report.ExpectedFiles = expectedFiles.Count;

            int processed = 0;
            var allowed = new HashSet<string>(expectedFiles.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var (relative, hash) in expectedFiles)
            {
                token.ThrowIfCancellationRequested();
                processed++;
                if (processed == 1 || processed % 15 == 0 || processed == expectedFiles.Count)
                    progress?.Report($"Vérification des fichiers : {processed}/{expectedFiles.Count}…");
                string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                try
                {
                    if (!IsWithinRoot(root, path)) throw new InvalidDataException("Chemin interdit.");
                    RejectLinkedComponents(root, path);
                    if (!File.Exists(path))
                    {
                        report.Problems.Add(new FileProblem(relative, "Fichier manquant"));
                        continue;
                    }
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
                    byte[] actual = await SHA256.HashDataAsync(file, token);
                    if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(hash)))
                        report.Problems.Add(new FileProblem(relative, "Contenu différent du package GitHub"));
                    else report.CheckedFiles++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
                {
                    report.Problems.Add(new FileProblem(relative, "Lecture impossible : " + ex.Message));
                }
            }
            progress?.Report("Recherche d'exécutables et de DLL inattendus…");
            FindUnexpectedBinaries(root, allowed, report, token);
            return report;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
            JsonException or CryptographicException or IOException or InvalidDataException)
        {
            // A network or manifest failure must never be presented as a successful check.
            report.ManifestVerified = false;
            report.ReasonNotVerified = "Échec de contrôle : " + ex.Message;
            return report;
        }
    }

    private static string GetInstalledVersion()
    {
        Version? version = typeof(App).Assembly.GetName().Version;
        if (version is null || version.Major < 0 || version.Minor < 0 || version.Build < 0)
            throw new InvalidDataException("Version binaire de Nexora indisponible.");
        return version.ToString(3);
    }

    private static Dictionary<string, string> ParseManifest(byte[] bytes)
    {
        string body = new UTF8Encoding(false, true).GetString(bytes);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var match = ManifestLinePattern.Match(line);
            if (!match.Success) throw new InvalidDataException("Ligne de manifeste invalide.");
            string prefixed = match.Groups[2].Value;
            string relative = prefixed["Nexora/".Length..];
            if (!IsSafeRelativePath(relative) || !result.TryAdd(relative, match.Groups[1].Value))
                throw new InvalidDataException("Chemin invalide ou dupliqué dans le manifeste.");
            if (result.Count > MaximumFiles) throw new InvalidDataException("Trop de fichiers dans le manifeste.");
        }
        if (result.Count == 0 || !result.ContainsKey("Nexora.exe"))
            throw new InvalidDataException("Le manifeste ne contient pas l'exécutable Nexora.exe.");
        return result;
    }

    private static bool IsSafeRelativePath(string relative) => relative.Length is > 0 and < 512 &&
        !relative.Contains('\\') && !relative.Contains(':') && !relative.Any(char.IsControl) &&
        relative.Split('/').All(s => s is not ("" or "." or ".."));

    private static bool IsWithinRoot(string root, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void RejectLinkedComponents(string root, string path)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Le répertoire de l'application est redirigé.");
        string? current = Path.GetDirectoryName(path);
        while (current is not null && IsWithinRoot(root, current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Sous-dossier redirigé par un lien symbolique.");
            current = Path.GetDirectoryName(current);
        }
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Fichier redirigé par un lien symbolique.");
    }

    private static void FindUnexpectedBinaries(string root, HashSet<string> allowed, Report result,
        CancellationToken token)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        int visited = 0;
        while (directories.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var dir = directories.Pop();
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                {
                    result.Problems.Add(new FileProblem(Path.GetRelativePath(root, dir), "Répertoire redirigé."));
                    continue;
                }
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > 25_000)
                    {
                        result.Problems.Add(new FileProblem("(dossier)", "Trop de fichiers pour un examen exhaustif."));
                        return;
                    }
                    FileAttributes flags = File.GetAttributes(entry);
                    string rel = Path.GetRelativePath(root, entry).Replace('\\', '/');
                    if ((flags & FileAttributes.ReparsePoint) != 0)
                    {
                        result.Problems.Add(new FileProblem(rel, "Lien symbolique ou jonction non contrôlé."));
                        continue;
                    }
                    if ((flags & FileAttributes.Directory) != 0)
                    {
                        directories.Push(entry);
                        continue;
                    }
                    string ext = Path.GetExtension(entry);
                    if ((ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) && !allowed.Contains(rel))
                        result.UnexpectedBinaries.Add(new FileProblem(rel, "Absent du package GitHub de cette version"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Problems.Add(new FileProblem(Path.GetRelativePath(root, dir), "Exploration impossible : " + ex.Message));
            }
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit,
        CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > limit)
            throw new InvalidDataException("Fichier distant trop volumineux.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var target = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int n = await source.ReadAsync(buffer.AsMemory(), token);
            if (n == 0) break;
            if (target.Length + n > limit) throw new InvalidDataException("Fichier distant trop volumineux.");
            target.Write(buffer, 0, n);
        }
        return target.ToArray();
    }

    public void Dispose() => _client.Dispose();
}
