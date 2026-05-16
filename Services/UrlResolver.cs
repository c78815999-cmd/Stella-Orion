using StellaOrion.Models;

namespace StellaOrion.Services;

public static class UrlResolver
{
    public const string HomeUrl = "stella://orion";

    public static string Resolve(string? input, BrowserSettings settings)
    {
        var value = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return HomeUrl;
        }

        if (string.Equals(value, HomeUrl, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "stella://home", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "about:home", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("stella:", StringComparison.OrdinalIgnoreCase))
        {
            return HomeUrl;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            !string.IsNullOrWhiteSpace(absolute.Scheme) &&
            absolute.Scheme != Uri.UriSchemeFile)
        {
            return absolute.ToString();
        }

        var looksLikeHost = LooksLikeHost(value);
        if (looksLikeHost)
        {
            var isLocal = value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                          value.StartsWith("127.", StringComparison.OrdinalIgnoreCase) ||
                          value.StartsWith("10.", StringComparison.OrdinalIgnoreCase) ||
                          value.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase);

            var scheme = settings.HttpsFirst && !isLocal ? "https" : "http";
            return $"{scheme}://{value}";
        }

        return SearchEngines.ResolveQuery(settings.SearchEngine, value);
    }

    public static string Display(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || IsHome(url))
        {
            return HomeUrl;
        }

        return url;
    }

    public static bool IsHome(string? url)
    {
        return string.IsNullOrWhiteSpace(url) ||
               string.Equals(url, HomeUrl, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(url, "stella://home", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase) ||
               (url?.StartsWith("data:text/html;", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool LooksLikeHost(string value)
    {
        if (value.Contains(' '))
        {
            return false;
        }

        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("localhost/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.CheckHostName(value.Split('/')[0].Split(':')[0]) != UriHostNameType.Unknown)
        {
            return value.Contains('.') || value.Contains(':');
        }

        return false;
    }
}
