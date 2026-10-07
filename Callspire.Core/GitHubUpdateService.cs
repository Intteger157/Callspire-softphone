using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Softphone
{
    /// <summary>Checks public GitHub Releases for desktop builds.</summary>
    public static class GitHubUpdateService
    {
        public const string DefaultRepositorySlug = "Intteger157/Callspire-softphone";

        private static readonly HttpClient HttpClient;

        static GitHubUpdateService()
        {
            HttpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(45),
            };
            HttpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("Callspire-Softphone", "1.0"));
            HttpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        public static async Task<UpdateInfo?> FetchLatestReleaseAsync()
        {
            var (owner, repo) = ResolveRepositorySlug();
            string url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            AppLog.Log($"[GitHubUpdate] GET {url}");

            using var response = await HttpClient.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Log($"[GitHubUpdate] HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            GitHubRelease? release;
            try
            {
                release = JsonConvert.DeserializeObject<GitHubRelease>(json);
            }
            catch (JsonException ex)
            {
                AppLog.Log($"[GitHubUpdate] JSON error: {ex.Message}");
                return null;
            }

            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                AppLog.Log("[GitHubUpdate] Release payload missing tag_name");
                return null;
            }

            string version = release.TagName.Trim().TrimStart('v', 'V');
            string? downloadUrl = PickAssetDownloadUrl(release);
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                AppLog.Log("[GitHubUpdate] No matching release asset for this OS/architecture");
                return null;
            }

            AppLog.Log($"[GitHubUpdate] Latest release {version} asset: {downloadUrl}");

            var info = new UpdateInfo
            {
                Version = version,
                Url = downloadUrl,
                Notes = string.IsNullOrWhiteSpace(release.Body) ? null : release.Body.Trim(),
                DownloadSource = "github",
            };

            if (OperatingSystem.IsMacOS())
            {
                info.MacUrl = downloadUrl;
            }

            return info;
        }

        public static async Task<UpdateInfo?> CheckForUpdateAsync(bool forceCheck = false)
        {
            if (!forceCheck && !UpdateService.ShouldCheckForUpdates())
                return null;

            var latest = await FetchLatestReleaseAsync().ConfigureAwait(false);
            if (latest == null)
                return null;

            string current = UpdateService.GetCurrentVersion();
            if (UpdateService.CompareVersions(current, latest.Version) >= 0)
            {
                AppLog.Log($"[GitHubUpdate] Up to date ({current})");
                return null;
            }

            AppLog.Log($"[GitHubUpdate] Update available: {current} -> {latest.Version}");
            return latest;
        }

        private static (string Owner, string Repo) ResolveRepositorySlug()
        {
            try
            {
                var link = AppDataHelper.LoadSettingsOrNew().GitHubRepositoryLink;
                if (!string.IsNullOrWhiteSpace(link)
                    && TryParseRepositorySlug(link, out string? owner, out string? repo))
                {
                    return (owner!, repo!);
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[GitHubUpdate] Settings read failed: {ex.Message}");
            }

            var parts = DefaultRepositorySlug.Split('/', 2);
            return (parts[0], parts.Length > 1 ? parts[1] : "Callspire-softphone");
        }

        internal static bool TryParseRepositorySlug(string link, out string? owner, out string? repo)
        {
            owner = null;
            repo = null;
            if (string.IsNullOrWhiteSpace(link))
                return false;

            link = link.Trim();
            if (link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
                    return false;
                var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length < 2)
                    return false;
                owner = segments[0];
                repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? segments[1][..^4]
                    : segments[1];
                return true;
            }

            var slash = link.IndexOf('/');
            if (slash <= 0 || slash >= link.Length - 1)
                return false;
            owner = link[..slash];
            repo = link[(slash + 1)..];
            return true;
        }

        private static string? PickAssetDownloadUrl(GitHubRelease release)
        {
            var assets = release.Assets ?? Array.Empty<GitHubReleaseAsset>();
            if (assets.Length == 0)
                return null;

            if (OperatingSystem.IsWindows())
            {
                bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
                string prefer = arm ? "win-arm64-setup.exe" : "win-x64-setup.exe";
                string alt = arm ? "win-x64-setup.exe" : "win-arm64-setup.exe";
                return FindAsset(assets, prefer)?.BrowserDownloadUrl
                    ?? FindAsset(assets, alt)?.BrowserDownloadUrl
                    ?? assets.FirstOrDefault(a =>
                        (a.Name ?? "").EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                        ?.BrowserDownloadUrl;
            }

            if (OperatingSystem.IsMacOS())
            {
                bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
                string archTag = arm ? "macos-arm64" : "macos-x64";
                var dmg = assets.FirstOrDefault(a =>
                    ContainsIgnoreCase(a.Name, archTag)
                    && (a.Name ?? "").EndsWith(".dmg", StringComparison.OrdinalIgnoreCase));
                if (dmg != null)
                    return dmg.BrowserDownloadUrl;

                var zip = assets.FirstOrDefault(a =>
                    ContainsIgnoreCase(a.Name, archTag)
                    && (a.Name ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (zip != null)
                    return zip.BrowserDownloadUrl;

                return assets.FirstOrDefault(a =>
                    (a.Name ?? "").Contains("macos", StringComparison.OrdinalIgnoreCase))
                    ?.BrowserDownloadUrl;
            }

            return null;
        }

        private static GitHubReleaseAsset? FindAsset(GitHubReleaseAsset[] assets, string fragment)
        {
            return assets.FirstOrDefault(a =>
                ContainsIgnoreCase(a.Name, fragment));
        }

        private static bool ContainsIgnoreCase(string? haystack, string needle)
        {
            return (haystack ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class GitHubRelease
        {
            [JsonProperty("tag_name")]
            public string TagName { get; set; } = "";

            [JsonProperty("body")]
            public string? Body { get; set; }

            [JsonProperty("assets")]
            public GitHubReleaseAsset[]? Assets { get; set; }
        }

        private sealed class GitHubReleaseAsset
        {
            [JsonProperty("name")]
            public string? Name { get; set; }

            [JsonProperty("browser_download_url")]
            public string? BrowserDownloadUrl { get; set; }
        }
    }
}
