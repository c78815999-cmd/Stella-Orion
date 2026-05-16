namespace StellaOrion.Services;

/// <summary>
/// GitHub auto-update ayarları.
/// Kendi deposunu buraya yaz; boş veya YOUR_* bırakırsan güncelleme kontrolü atlanır.
/// </summary>
public static class UpdateConfig
{
    /// <summary>GitHub kullanıcı adın veya organizasyon adın.</summary>
    public const string GitHubOwner = "c78815999-cmd";

    /// <summary>Repo adı (ör. Stella-Orion).</summary>
    public const string GitHubRepo = "Stella-Orion";

    /// <summary>İsteğe bağlı: doğrudan Releases API URL (doluysa Owner/Repo yerine bu kullanılır).</summary>
    public const string ReleasesApiUrl = "https://api.github.com/repos/c78815999-cmd/Stella-Orion/releases/latest";

    /// <summary>Yükleyici .exe veya .zip asset adında aranacak parça (boş = ilk asset).</summary>
    public const string ReleaseAssetContains = "Stella";

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ReleasesApiUrl) ||
        (!string.IsNullOrWhiteSpace(GitHubOwner) &&
         !GitHubOwner.Contains("YOUR_", StringComparison.OrdinalIgnoreCase) &&
         !string.IsNullOrWhiteSpace(GitHubRepo) &&
         !GitHubRepo.Contains("YOUR_", StringComparison.OrdinalIgnoreCase));

    public static string ResolveReleasesApiUrl()
    {
        if (!string.IsNullOrWhiteSpace(ReleasesApiUrl))
        {
            return ReleasesApiUrl.Trim();
        }

        return $"https://api.github.com/repos/{GitHubOwner.Trim()}/{GitHubRepo.Trim()}/releases/latest";
    }
}
