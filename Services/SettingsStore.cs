using System.IO;
using System.Text.Json;
using StellaOrion.Models;

namespace StellaOrion.Services;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string AppDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Stella Orion");

    public static string SettingsFile { get; } = Path.Combine(AppDataRoot, "settings.json");
    public static string DefaultProfilePath { get; } = Path.Combine(AppDataRoot, "DefaultProfile");
    public static string PrivateProfileRoot { get; } = Path.Combine(AppDataRoot, "PrivateProfiles");

    public static BrowserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return Normalize(new BrowserSettings());
            }

            var json = File.ReadAllText(SettingsFile);
            return Normalize(JsonSerializer.Deserialize<BrowserSettings>(json, JsonOptions) ?? new BrowserSettings());
        }
        catch
        {
            return Normalize(new BrowserSettings());
        }
    }

    public static void Save(BrowserSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(AppDataRoot);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static BrowserSettings Normalize(BrowserSettings settings)
    {
        settings.Proxy ??= new ProxySettings();
        settings.ExtensionPaths ??= [];
        settings.QuickLinks ??= QuickLinkDefaults.Create();
        settings.Bookmarks ??= [];
        settings.LastSession ??= [];

        settings.AccentColor = string.IsNullOrWhiteSpace(settings.AccentColor)
            ? "#38BDF8"
            : settings.AccentColor;

        if (string.IsNullOrWhiteSpace(settings.SearchEngine) || !SearchEngines.All.ContainsKey(settings.SearchEngine))
        {
            settings.SearchEngine = "DuckDuckGo";
        }

        if (string.IsNullOrWhiteSpace(settings.HomePage))
        {
            settings.HomePage = "stella://orion";
        }

        if (string.IsNullOrWhiteSpace(settings.ThemeName))
        {
            settings.ThemeName = "Midnight";
        }

        if (string.IsNullOrWhiteSpace(settings.Language) ||
            !Localization.Languages.ContainsKey(settings.Language))
        {
            settings.Language = "system";
        }

        if (!string.IsNullOrWhiteSpace(settings.BackgroundMediaPath) &&
            !File.Exists(settings.BackgroundMediaPath))
        {
            settings.BackgroundMediaPath = string.Empty;
        }

        settings.BackgroundMediaOpacity = Math.Clamp(settings.BackgroundMediaOpacity <= 0 ? 0.55 : settings.BackgroundMediaOpacity, 0.15, 1.0);
        settings.BackgroundMediaBlur = Math.Clamp(settings.BackgroundMediaBlur, 0.0, 24.0);

        if (!string.IsNullOrWhiteSpace(settings.BackgroundMusicPath) &&
            !File.Exists(settings.BackgroundMusicPath))
        {
            settings.BackgroundMusicPath = string.Empty;
            settings.PlayBackgroundMusic = false;
        }

        settings.BackgroundMusicVolume = Math.Clamp(settings.BackgroundMusicVolume <= 0 ? 0.28 : settings.BackgroundMusicVolume, 0.02, 1.0);
        settings.DefaultZoom = Math.Clamp(settings.DefaultZoom <= 0 ? 1.0 : settings.DefaultZoom, 0.25, 5.0);

        settings.QuickLinks = settings.QuickLinks
            .Where(link => !string.IsNullOrWhiteSpace(link.Url))
            .Select(link => new QuickLink
            {
                Name = string.IsNullOrWhiteSpace(link.Name) ? HostName(link.Url) : link.Name.Trim(),
                Url = link.Url.Trim()
            })
            .Take(18)
            .ToList();

        settings.Bookmarks = settings.Bookmarks
            .Where(bookmark => !string.IsNullOrWhiteSpace(bookmark.Url))
            .Select(bookmark => new Bookmark
            {
                Name = string.IsNullOrWhiteSpace(bookmark.Name) ? HostName(bookmark.Url) : bookmark.Name.Trim(),
                Url = bookmark.Url.Trim(),
                AddedAt = bookmark.AddedAt <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : bookmark.AddedAt
            })
            .ToList();

        return settings;
    }

    private static string HostName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "Site";
        }

        return uri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public static string CreatePrivateProfilePath()
    {
        Directory.CreateDirectory(PrivateProfileRoot);
        return Path.Combine(PrivateProfileRoot, $"Private-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}");
    }

    public static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // WebView2 can release files slightly after a tab closes; cleanup is best-effort.
        }
    }
}
