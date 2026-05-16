using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace StellaOrion.Services;

public sealed class AppUpdateService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    static AppUpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("StellaOrion-Updater/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!UpdateConfig.IsConfigured)
        {
            return null;
        }

        try
        {
            using var response = await Http.GetAsync(UpdateConfig.ResolveReleasesApiUrl(), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            var remoteVersion = ParseVersionFromTag(tag);
            if (remoteVersion == null)
            {
                return null;
            }

            var current = CurrentVersion;
            if (remoteVersion <= new Version(current.Major, current.Minor, current.Build))
            {
                return new AppUpdateInfo(false, tag, null, null);
            }

            var pageUrl = root.TryGetProperty("html_url", out var page) ? page.GetString() : null;
            var downloadUrl = ResolveDownloadUrl(root);
            return new AppUpdateInfo(true, tag, pageUrl, downloadUrl);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveDownloadUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(UpdateConfig.ReleaseAssetContains))
            {
                return url;
            }

            fallback ??= url;
            if (name != null &&
                name.Contains(UpdateConfig.ReleaseAssetContains, StringComparison.OrdinalIgnoreCase) &&
                (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                return url;
            }
        }

        return fallback;
    }

    private static Version? ParseVersionFromTag(string tag)
    {
        var cleaned = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(cleaned, out var version) ? version : null;
    }
}

public sealed record AppUpdateInfo(bool HasUpdate, string TagName, string? ReleasePageUrl, string? DownloadUrl);
