namespace StellaOrion.Models;

public sealed class BrowserSettings
{
    public bool BlockTrackers { get; set; } = true;
    public bool DoNotTrack { get; set; } = true;
    public bool DenySensitivePermissions { get; set; } = true;
    public bool HttpsFirst { get; set; } = true;
    public bool ForceDarkPages { get; set; } = true;
    public bool RestoreSessionOnStart { get; set; } = true;
    public bool ShowBookmarksBar { get; set; } = true;
    public string AccentColor { get; set; } = "#38BDF8";
    public string ThemeName { get; set; } = "Midnight";
    public string UiFontKey { get; set; } = "SegoeUiVariable";
    public string Language { get; set; } = "system";
    public string BackgroundMediaPath { get; set; } = string.Empty;
    public double BackgroundMediaOpacity { get; set; } = 0.55;
    public double BackgroundMediaBlur { get; set; }
    public bool SoftUiEffects { get; set; } = true;
    public bool PlayBackgroundMusic { get; set; }
    public string BackgroundMusicPath { get; set; } = string.Empty;
    public double BackgroundMusicVolume { get; set; } = 0.28;
    public string SearchEngine { get; set; } = "DuckDuckGo";
    public string HomePage { get; set; } = "stella://orion";
    public string DownloadDirectory { get; set; } = string.Empty;
    public bool AskWhereToSaveDownloads { get; set; }
    public double DefaultZoom { get; set; } = 1.0;

    public ProxySettings Proxy { get; set; } = new();
    public List<string> ExtensionPaths { get; set; } = [];
    public List<QuickLink> QuickLinks { get; set; } = QuickLinkDefaults.Create();
    public List<Bookmark> Bookmarks { get; set; } = [];
    public List<SessionTabRecord> LastSession { get; set; } = [];
}

public sealed class ProxySettings
{
    public bool Enabled { get; set; }
    public string Scheme { get; set; } = "socks5";
    public string Host { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "9050";
    public bool BypassLocal { get; set; } = true;

    public string ToProxyArgument()
    {
        var scheme = string.IsNullOrWhiteSpace(Scheme) ? "socks5" : Scheme.Trim().ToLowerInvariant();
        var host = string.IsNullOrWhiteSpace(Host) ? "127.0.0.1" : Host.Trim();
        var port = string.IsNullOrWhiteSpace(Port) ? "9050" : Port.Trim();
        return $"{scheme}://{host}:{port}";
    }
}

public sealed class QuickLink
{
    public string Name { get; set; } = "Site";
    public string Url { get; set; } = "https://example.com";
}

public sealed class Bookmark
{
    public string Name { get; set; } = "Bookmark";
    public string Url { get; set; } = "https://example.com";
    public long AddedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

public sealed class SessionTabRecord
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public bool IsActive { get; set; }
}

public sealed class HistoryEntry
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long VisitedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

public static class QuickLinkDefaults
{
    public static List<QuickLink> Create()
    {
        return
        [
            new() { Name = "ChatGPT", Url = "https://chatgpt.com" },
            new() { Name = "YouTube", Url = "https://youtube.com" },
            new() { Name = "Twitch", Url = "https://twitch.tv" },
            new() { Name = "Discord", Url = "https://discord.com/app" },
            new() { Name = "WhatsApp", Url = "https://web.whatsapp.com" },
            new() { Name = "X", Url = "https://x.com" },
            new() { Name = "Spotify", Url = "https://open.spotify.com" },
            new() { Name = "GitHub", Url = "https://github.com" }
        ];
    }
}

public static class SearchEngines
{
    public static readonly Dictionary<string, string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DuckDuckGo"] = "https://duckduckgo.com/?q={q}",
        ["Google"] = "https://www.google.com/search?q={q}",
        ["Bing"] = "https://www.bing.com/search?q={q}",
        ["Brave"] = "https://search.brave.com/search?q={q}",
        ["Startpage"] = "https://www.startpage.com/do/search?query={q}",
        ["Yandex"] = "https://yandex.com/search/?text={q}"
    };

    public static string ResolveQuery(string engine, string query)
    {
        if (!All.TryGetValue(engine, out var template))
        {
            template = All["DuckDuckGo"];
        }

        return template.Replace("{q}", Uri.EscapeDataString(query), StringComparison.Ordinal);
    }
}

public static class ThemeCatalog
{
    public sealed record Theme(string Name, string ChromeBg, string SidebarBg, string PanelBg, string Surface, string SurfaceSoft, string Border, string TextMain, string TextMuted);

    public static readonly List<Theme> All =
    [
        new("Midnight", "#08111C", "#050A10", "#0A121D", "#0D1623", "#111D2B", "#263447", "#F8FAFC", "#94A3B8"),
        new("Aurora", "#0D1A2D", "#08132A", "#0F1F36", "#142943", "#1A314D", "#2C4566", "#F1F5FF", "#9DB1D2"),
        new("Graphite", "#161616", "#101010", "#1B1B1B", "#222222", "#2A2A2A", "#3A3A3A", "#FAFAFA", "#A0A0A0"),
        new("Nord", "#2E3440", "#272B35", "#3B4252", "#434C5E", "#4C566A", "#566275", "#ECEFF4", "#A7B2C2"),
        new("Daylight", "#F5F7FA", "#EEF1F6", "#FFFFFF", "#FFFFFF", "#F2F4F8", "#D2D9E2", "#0F172A", "#475569")
    ];

    public static Theme FromName(string? name)
    {
        return All.FirstOrDefault(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }
}
