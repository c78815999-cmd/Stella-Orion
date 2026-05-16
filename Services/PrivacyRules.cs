namespace StellaOrion.Services;

public static class PrivacyRules
{
    private static readonly HashSet<string> TrackerHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        // Google
        "doubleclick.net",
        "googlesyndication.com",
        "google-analytics.com",
        "googletagmanager.com",
        "googletagservices.com",
        "googleadservices.com",
        "adservice.google.com",
        "adservice.google.co.uk",
        "2mdn.net",
        "googleads.g.doubleclick.net",
        "pagead2.googlesyndication.com",
        "stats.g.doubleclick.net",

        // Facebook / Meta
        "facebook.net",
        "connect.facebook.net",
        "an.facebook.com",
        "graph.facebook.com",

        // Amazon ads
        "amazon-adsystem.com",
        "aax.amazon-adsystem.com",
        "amazon-clicks.com",

        // Microsoft
        "clarity.ms",
        "bat.bing.com",
        "ads.microsoft.com",
        "c.bing.com",

        // Twitter / X
        "analytics.twitter.com",
        "ads-twitter.com",
        "static.ads-twitter.com",
        "t.co",

        // Major ad networks
        "adnxs.com",
        "rubiconproject.com",
        "openx.net",
        "pubmatic.com",
        "criteo.com",
        "criteo.net",
        "adsrvr.org",
        "adform.net",
        "casalemedia.com",
        "contextweb.com",
        "moatads.com",
        "bidswitch.net",
        "media.net",
        "yieldmo.com",
        "spotxchange.com",
        "advertising.com",
        "adroll.com",
        "outbrain.com",
        "taboola.com",
        "revcontent.com",
        "smartadserver.com",
        "exoclick.com",
        "popads.net",
        "propellerads.com",
        "trafficjunky.net",

        // Analytics
        "scorecardresearch.com",
        "quantserve.com",
        "quantcast.com",
        "hotjar.com",
        "segment.io",
        "segment.com",
        "mixpanel.com",
        "amplitude.com",
        "newrelic.com",
        "fullstory.com",
        "loggly.com",
        "logrocket.com",
        "mouseflow.com",
        "smartlook.com",
        "datadog-rum.com",
        "browser.sentry-cdn.com",
        "demdex.net",
        "omtrdc.net",
        "everesttech.net",
        "exelator.com",
        "imrworldwide.com",
        "mathtag.com",
        "nr-data.net",
        "bluekai.com",
        "krxd.net",
        "agkn.com",
        "tapad.com",
        "rlcdn.com",

        // Trackers
        "branch.io",
        "appsflyer.com",
        "adjust.com",
        "kochava.com",
        "tealiumiq.com",
        "tiqcdn.com",
        "salesforceiq.com",
        "marketo.com",
        "hubspot.com",
        "intercom.io",
        "drift.com",
        "optimizely.com",
        "optimizelyedge.com",
        "vwo.com",

        // Misc creep
        "snigelweb.com",
        "snapads.com",
        "yandex.ru",
        "yandexmetrica.com",
        "mc.yandex.ru",
        "vk.com/rtrg",
        "histats.com",
        "statcounter.com",
        "go-mpulse.net"
    };

    public static bool IsTracker(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        foreach (var domain in TrackerHosts)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
