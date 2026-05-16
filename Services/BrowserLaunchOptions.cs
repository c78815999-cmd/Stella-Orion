namespace StellaOrion.Services;

public static class BrowserLaunchOptions
{
    public static IReadOnlyList<string> BuildChromiumArgs(bool forceDarkPages, bool proxyEnabled, string? proxyArgument, bool proxyBypassLocal)
    {
        var args = new List<string>
        {
            "--disable-background-networking",
            "--disable-sync",
            "--disable-default-apps",
            "--disable-domain-reliability",
            "--disable-breakpad",
            "--disable-component-update",
            "--disable-client-side-phishing-detection",
            "--disable-features=msEdgeShoppingAssistant,DownloadBubble,DownloadBubbleV2,DownloadShelf,msPersonalCheckout,TranslateUI,MediaRouter",
            "--disable-ipc-flooding-protection",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding",
            "--process-per-site",
            "--renderer-process-limit=4",
            "--disk-cache-size=33554432",
            "--autoplay-policy=no-user-gesture-required"
        };

        if (forceDarkPages)
        {
            args.Add("--enable-features=WebContentsForceDark");
        }

        if (proxyEnabled && !string.IsNullOrWhiteSpace(proxyArgument))
        {
            args.Add($"--proxy-server={proxyArgument}");
            if (proxyBypassLocal)
            {
                args.Add("--proxy-bypass-list=<local>");
            }
        }

        return args;
    }
}
