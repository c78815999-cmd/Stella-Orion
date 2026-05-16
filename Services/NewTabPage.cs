using System.IO;
using System.Globalization;
using System.Net;
using System.Text.Json;
using StellaOrion.Models;

namespace StellaOrion.Services;

public static class NewTabPage
{
    public static string Build(BrowserSettings settings)
    {
        var accent = SanitizeColor(settings.AccentColor);
        var theme = ThemeCatalog.FromName(settings.ThemeName);
        var linksJson = JsonSerializer.Serialize(settings.QuickLinks);
        var bookmarksJson = JsonSerializer.Serialize(settings.Bookmarks.Take(10));
        var engineLabel = settings.SearchEngine ?? "DuckDuckGo";
        var effectiveLanguage = Localization.EffectiveLanguage;
        var backgroundOpacity = Math.Clamp(settings.BackgroundMediaOpacity <= 0 ? 0.55 : settings.BackgroundMediaOpacity, 0.15, 1.0)
            .ToString("0.##", CultureInfo.InvariantCulture);
        var backgroundBlur = Math.Clamp(settings.BackgroundMediaBlur, 0.0, 24.0)
            .ToString("0.#", CultureInfo.InvariantCulture);
        var lang = effectiveLanguage switch
        {
            "tr" => "tr-TR",
            "de" => "de-DE",
            "es" => "es-ES",
            "fr" => "fr-FR",
            "it" => "it-IT",
            "pt" => "pt-BR",
            "ru" => "ru-RU",
            "ar" => "ar-SA",
            "ja" => "ja-JP",
            "ko" => "ko-KR",
            "zh" => "zh-CN",
            _ => "en-US"
        };
        var backgroundMedia = BuildBackgroundMedia(settings.BackgroundMediaPath);
        var backgroundMusic = BuildBackgroundMusic(settings.BackgroundMusicPath, settings.PlayBackgroundMusic, settings.BackgroundMusicVolume);

        var l = effectiveLanguage == "tr"
            ? new Dictionary<string, string>
            {
                ["search_placeholder"] = "Adres yaz veya ara",
                ["speed_dial"] = "H&#305;zl&#305; Eri&#351;im",
                ["bookmarks"] = "Yer &#304;mleri",
                ["sites_count"] = "site",
                ["saved_count"] = "kay&#305;tl&#305;",
                ["tip"] = "<strong>Ctrl+L</strong> adres &ccedil;ubu&#287;u &middot; <strong>Ctrl+T</strong> yeni sekme &middot; <strong>Ctrl+Shift+T</strong> kapat&#305;lan&#305; geri a&ccedil;"
            }
            : new Dictionary<string, string>
            {
                ["search_placeholder"] = "Search or enter address",
                ["speed_dial"] = "Speed Dial",
                ["bookmarks"] = "Bookmarks",
                ["sites_count"] = "sites",
                ["saved_count"] = "saved",
                ["tip"] = "<strong>Ctrl+L</strong> address bar &middot; <strong>Ctrl+T</strong> new tab &middot; <strong>Ctrl+Shift+T</strong> reopen closed"
            };

        return Template
            .Replace("__ACCENT__", accent, StringComparison.Ordinal)
            .Replace("__BG__", theme.ChromeBg, StringComparison.Ordinal)
            .Replace("__PANEL__", theme.PanelBg, StringComparison.Ordinal)
            .Replace("__SURFACE__", theme.Surface, StringComparison.Ordinal)
            .Replace("__BORDER__", theme.Border, StringComparison.Ordinal)
            .Replace("__TEXT__", theme.TextMain, StringComparison.Ordinal)
            .Replace("__MUTED__", theme.TextMuted, StringComparison.Ordinal)
            .Replace("__ENGINE__", engineLabel, StringComparison.Ordinal)
            .Replace("__LOCALE__", lang, StringComparison.Ordinal)
            .Replace("__L_PLACEHOLDER__", l["search_placeholder"], StringComparison.Ordinal)
            .Replace("__L_SPEED__", l["speed_dial"], StringComparison.Ordinal)
            .Replace("__L_BOOKMARKS__", l["bookmarks"], StringComparison.Ordinal)
            .Replace("__L_SITES__", l["sites_count"], StringComparison.Ordinal)
            .Replace("__L_SAVED__", l["saved_count"], StringComparison.Ordinal)
            .Replace("__L_TIP__", l["tip"], StringComparison.Ordinal)
            .Replace("__BACKGROUND_MEDIA__", backgroundMedia, StringComparison.Ordinal)
            .Replace("__BACKGROUND_MUSIC__", backgroundMusic, StringComparison.Ordinal)
            .Replace("__BG_OPACITY__", backgroundOpacity, StringComparison.Ordinal)
            .Replace("__BG_BLUR__", backgroundBlur, StringComparison.Ordinal)
            .Replace("__QUICK_LINKS__", linksJson, StringComparison.Ordinal)
            .Replace("__BOOKMARKS__", bookmarksJson, StringComparison.Ordinal);
    }

    private static string BuildBackgroundMedia(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var fileName = Uri.EscapeDataString(Path.GetFileName(path));
        var version = File.GetLastWriteTimeUtc(path).Ticks;
        var uri = WebUtility.HtmlEncode($"https://stella-background.local/{fileName}?v={version}");
        return ext is ".mp4" or ".webm" or ".mov"
            ? $"""<div class="wallpaper-layer"><video src="{uri}" autoplay muted loop playsinline></video></div>"""
            : $"""<div class="wallpaper-layer"><img src="{uri}" alt=""></div>""";
    }

    private static string BuildBackgroundMusic(string? path, bool enabled, double volume)
    {
        if (!enabled || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".mp3" and not ".wav" and not ".ogg" and not ".m4a" and not ".flac")
        {
            return string.Empty;
        }

        var fileName = Uri.EscapeDataString(Path.GetFileName(path));
        var version = File.GetLastWriteTimeUtc(path).Ticks;
        var safeVolume = Math.Clamp(volume <= 0 ? 0.28 : volume, 0.02, 1.0).ToString("0.##", CultureInfo.InvariantCulture);
        var uri = WebUtility.HtmlEncode($"https://stella-audio.local/{fileName}?v={version}");

        return $$"""
<audio id="ambientMusic" src="{{uri}}" autoplay loop preload="auto"></audio>
<script>
  (() => {
    const audio = document.getElementById('ambientMusic');
    if (!audio) return;
    audio.volume = {{safeVolume}};
    const play = () => audio.play().catch(() => {});
    play();
    document.addEventListener('pointerdown', play, { once: true, passive: true });
    document.addEventListener('keydown', play, { once: true });
  })();
</script>
""";
    }

    private static string SanitizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return "#38BDF8";
        }

        var value = color.Trim();
        if (value.Length == 7 &&
            value[0] == '#' &&
            value.Skip(1).All(Uri.IsHexDigit))
        {
            return value;
        }

        return "#38BDF8";
    }

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>New Tab</title>
  <style>
    [hidden] { display: none !important; }

    :root {
      color-scheme: dark;
      --accent: __ACCENT__;
      --bg: __BG__;
      --panel: __PANEL__;
      --surface: __SURFACE__;
      --border: __BORDER__;
      --text: __TEXT__;
      --muted: __MUTED__;
      font-family: "Segoe UI Variable Text", "Segoe UI", Arial, sans-serif;
      background: var(--bg);
      color: var(--text);
    }

    * { box-sizing: border-box; }

    html, body {
      height: auto;
      min-height: var(--app-height, 100vh);
    }

    body {
      margin: 0;
      overflow-x: hidden;
      overflow-y: auto;
      background:
        radial-gradient(60vw 40vh at 20% 18%, color-mix(in srgb, var(--accent) 14%, transparent), transparent 70%),
        radial-gradient(60vw 40vh at 82% 78%, color-mix(in srgb, var(--accent) 9%, transparent), transparent 70%),
        linear-gradient(180deg, color-mix(in srgb, var(--bg) 92%, black 8%), var(--bg) 65%);
    }

    body::before {
      content: "";
      position: fixed;
      inset: 0;
      z-index: 1;
      pointer-events: none;
      opacity: 0.22;
      background-image:
        linear-gradient(rgba(148, 163, 184, 0.06) 1px, transparent 1px),
        linear-gradient(90deg, rgba(148, 163, 184, 0.05) 1px, transparent 1px);
      background-size: 56px 56px;
      mask-image: linear-gradient(to bottom, transparent, black 22%, black 78%, transparent);
      animation: drift 26s linear infinite;
    }

    .wallpaper-layer {
      position: fixed;
      inset: 0;
      z-index: 0;
      overflow: hidden;
      pointer-events: none;
      background: var(--bg);
    }

    .wallpaper-layer img,
    .wallpaper-layer video {
      width: 100%;
      height: 100%;
      object-fit: cover;
      opacity: __BG_OPACITY__;
      filter: brightness(0.58) saturate(1.08) blur(__BG_BLUR__px);
      transform: scale(calc(1 + (__BG_BLUR__ * 0.004)));
    }

    .wallpaper-layer::after {
      content: "";
      position: absolute;
      inset: 0;
      background:
        radial-gradient(60vw 40vh at 20% 18%, color-mix(in srgb, var(--accent) 18%, transparent), transparent 72%),
        linear-gradient(180deg, color-mix(in srgb, var(--bg) 40%, transparent), color-mix(in srgb, var(--bg) 82%, black 8%));
    }

    @keyframes drift {
      from { transform: translate3d(0, 0, 0); }
      to { transform: translate3d(56px, 56px, 0); }
    }

    main {
      position: relative;
      z-index: 2;
      min-height: var(--app-height, 100vh);
      width: 100%;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: flex-start;
      padding: clamp(18px, 4vh, 42px) 28px 80px;
      gap: 22px;
      box-sizing: border-box;
    }

    .stage {
      width: min(760px, 100%);
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 18px;
      animation: enter 420ms cubic-bezier(.2, .8, .2, 1) both;
    }

    @keyframes enter {
      from { opacity: 0; transform: translateY(10px); }
      to { opacity: 1; transform: translateY(0); }
    }

    .header {
      display: flex;
      align-items: center;
      gap: 14px;
    }

    .mark {
      width: 38px;
      height: 38px;
      display: grid;
      place-items: center;
      color: var(--accent);
      filter: drop-shadow(0 0 16px color-mix(in srgb, var(--accent) 36%, transparent));
    }

    .mark svg { width: 38px; height: 38px; }

    h1 {
      margin: 0;
      font-size: 30px;
      line-height: 1;
      font-weight: 720;
      letter-spacing: -0.02em;
      background: linear-gradient(95deg, var(--text), color-mix(in srgb, var(--accent) 65%, var(--text)));
      -webkit-background-clip: text;
      background-clip: text;
      color: transparent;
    }

    .clock {
      font-size: 11px;
      letter-spacing: 0.22em;
      text-transform: uppercase;
      color: var(--muted);
      margin-top: -4px;
    }

    form {
      width: 100%;
      height: 54px;
      display: grid;
      grid-template-columns: 48px 1fr auto 44px;
      align-items: center;
      gap: 4px;
      border: 1px solid var(--border);
      border-radius: 14px;
      background: color-mix(in srgb, var(--panel) 86%, transparent);
      box-shadow: 0 16px 60px rgba(0, 0, 0, 0.28);
      backdrop-filter: blur(20px);
      transition: border-color 180ms ease, box-shadow 180ms ease, transform 180ms ease;
    }

    form:focus-within {
      border-color: color-mix(in srgb, var(--accent) 72%, transparent);
      box-shadow: 0 16px 60px rgba(0, 0, 0, 0.32), 0 0 0 4px color-mix(in srgb, var(--accent) 20%, transparent);
      transform: translateY(-1px);
    }

    .search-icon { display: grid; place-items: center; color: var(--muted); }
    .search-icon svg { width: 18px; height: 18px; }

    input {
      width: 100%;
      min-width: 0;
      border: 0;
      outline: 0;
      background: transparent;
      color: var(--text);
      font-size: 15px;
      font-family: inherit;
    }

    input::placeholder { color: color-mix(in srgb, var(--muted) 80%, transparent); }

    .engine-pill {
      padding: 5px 11px;
      border-radius: 7px;
      font-size: 11px;
      letter-spacing: 0.06em;
      color: var(--muted);
      background: color-mix(in srgb, var(--surface) 80%, transparent);
      border: 1px solid var(--border);
      margin-right: 4px;
      white-space: nowrap;
    }

    button {
      border: 0;
      color: var(--text);
      cursor: pointer;
      background: transparent;
      font-family: inherit;
    }

    .go {
      width: 36px;
      height: 36px;
      border-radius: 10px;
      background: color-mix(in srgb, var(--accent) 88%, white 6%);
      color: #04111c;
      display: grid;
      place-items: center;
      justify-self: center;
      transition: transform 140ms ease;
    }

    .go:hover { transform: scale(1.06); }
    .go svg { width: 17px; height: 17px; }

    .section {
      width: 100%;
      display: flex;
      flex-direction: column;
      gap: 10px;
    }

    .section-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 0 4px;
    }

    .section-head h2 {
      font-size: 10px;
      letter-spacing: 0.24em;
      text-transform: uppercase;
      color: var(--muted);
      margin: 0;
      font-weight: 700;
    }

    .section-head .count {
      font-size: 10px;
      color: color-mix(in srgb, var(--muted) 70%, transparent);
      letter-spacing: 0.1em;
    }

    .speed-dial {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(62px, 62px));
      justify-content: center;
      gap: 10px;
      width: 100%;
    }

    .dial, .add-dial {
      position: relative;
      width: 62px;
      height: 62px;
      border: 1px solid var(--border);
      border-radius: 15px;
      background: color-mix(in srgb, var(--surface) 92%, transparent);
      box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.04), 0 8px 24px rgba(0, 0, 0, 0.18);
      transition: transform 160ms ease, border-color 160ms ease, background 160ms ease;
      backdrop-filter: blur(18px);
      display: grid;
      place-items: center;
    }

    .dial:hover, .add-dial:hover {
      transform: translateY(-3px);
      border-color: color-mix(in srgb, var(--accent) 78%, transparent);
      background: color-mix(in srgb, var(--surface) 98%, var(--accent) 4%);
    }

    .dial img {
      width: 28px;
      height: 28px;
      border-radius: 8px;
      pointer-events: none;
    }

    .add-dial svg { width: 22px; height: 22px; color: var(--accent); }

    .bookmarks {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
      gap: 6px;
      width: 100%;
    }

    .bookmark {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 8px 10px;
      border: 1px solid var(--border);
      border-radius: 10px;
      background: color-mix(in srgb, var(--panel) 70%, transparent);
      color: var(--text);
      cursor: pointer;
      transition: background 160ms ease, transform 140ms ease, border-color 160ms ease;
      text-align: left;
      font-size: 12px;
    }

    .bookmark:hover {
      background: color-mix(in srgb, var(--surface) 96%, transparent);
      border-color: color-mix(in srgb, var(--accent) 50%, var(--border));
      transform: translateY(-1px);
    }

    .bookmark img { width: 16px; height: 16px; border-radius: 4px; }
    .bookmark span { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }

    .tools {
      position: absolute;
      z-index: 3;
      top: 16px;
      right: 16px;
      display: flex;
      gap: 8px;
    }

    .tool {
      width: 32px;
      height: 32px;
      border: 1px solid var(--border);
      border-radius: 10px;
      background: color-mix(in srgb, var(--panel) 75%, transparent);
      backdrop-filter: blur(18px);
      display: grid;
      place-items: center;
      transition: border-color 160ms ease, background 160ms ease;
    }

    .tool:hover { border-color: color-mix(in srgb, var(--accent) 70%, transparent); background: color-mix(in srgb, var(--surface) 92%, transparent); }
    .tool svg { width: 15px; height: 15px; color: var(--muted); }
    .tool:hover svg { color: var(--accent); }

    .remove {
      position: absolute;
      top: -6px;
      right: -6px;
      width: 20px;
      height: 20px;
      border: 1px solid rgba(248, 113, 113, 0.55);
      border-radius: 999px;
      background: #2a1116;
      color: #fca5a5;
      opacity: 0;
      transform: scale(0.82);
      transition: opacity 160ms ease, transform 160ms ease;
      display: grid;
      place-items: center;
    }

    .remove svg { width: 10px; height: 10px; }
    body.editing .remove { opacity: 1; transform: scale(1); }

    .tip {
      position: absolute;
      z-index: 2;
      bottom: 18px;
      left: 0;
      right: 0;
      text-align: center;
      color: var(--muted);
      font-size: 11px;
      letter-spacing: 0.03em;
      opacity: 0.55;
    }

    .tip strong { color: var(--text); font-weight: 600; }

    .sr-only {
      position: absolute;
      width: 1px; height: 1px; padding: 0; margin: -1px;
      overflow: hidden; clip: rect(0, 0, 0, 0); white-space: nowrap; border: 0;
    }

    @media (max-height: 620px) {
      .clock { display: none; }
      .tip { display: none; }
      main { gap: 14px; padding: 18px; }
    }

    @media (max-width: 600px) {
      main { padding: 20px 14px; }
      .stage { gap: 14px; }
      form { grid-template-columns: 44px 1fr auto 40px; height: 50px; }
      .engine-pill { display: none; }
      h1 { font-size: 24px; }
      .speed-dial { grid-template-columns: repeat(auto-fit, minmax(56px, 56px)); gap: 8px; }
      .dial, .add-dial { width: 56px; height: 56px; border-radius: 13px; }
    }
  </style>
</head>
<body>
  __BACKGROUND_MEDIA__
  __BACKGROUND_MUSIC__
  <div class="tools">
    <button class="tool" id="edit" type="button" title="Edit speed dial" aria-label="Edit speed dial">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"/></svg>
    </button>
  </div>
  <main>
    <section class="stage" aria-label="Stella Orion new tab">
      <div class="header">
        <div class="mark" aria-hidden="true">
          <svg viewBox="0 0 48 48" fill="none">
            <path d="M24 4l5.7 14.3L44 24l-14.3 5.7L24 44l-5.7-14.3L4 24l14.3-5.7L24 4z" stroke="currentColor" stroke-width="3" stroke-linejoin="round"/>
            <circle cx="24" cy="24" r="5" fill="#c4b5fd"/>
          </svg>
        </div>
        <div>
          <h1>Stella Orion</h1>
          <div class="clock" id="clock">—</div>
        </div>
      </div>

      <form id="search">
        <span class="search-icon" aria-hidden="true">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>
        </span>
        <input id="query" autocomplete="off" autofocus placeholder="__L_PLACEHOLDER__" spellcheck="false">
        <span class="engine-pill" id="engine">__ENGINE__</span>
        <button class="go" type="submit" aria-label="Go">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M5 12h14"/><path d="m13 6 6 6-6 6"/></svg>
        </button>
      </form>

      <div class="section">
        <div class="section-head">
          <h2>__L_SPEED__</h2>
          <span class="count" id="dialCount"></span>
        </div>
        <nav class="speed-dial" id="speedDial" aria-label="Speed dial"></nav>
      </div>

      <div class="section" id="bookmarkLane" hidden>
        <div class="section-head">
          <h2>__L_BOOKMARKS__</h2>
          <span class="count" id="bookmarkCount"></span>
        </div>
        <nav class="bookmarks" id="bookmarks" aria-label="Bookmarks"></nav>
      </div>
    </section>
  </main>
  <p class="tip">__L_TIP__</p>
  <script>
    function syncViewportHeight() {
      const h = Math.max(window.innerHeight, document.documentElement.clientHeight || 0);
      document.documentElement.style.setProperty('--app-height', `${h}px`);
    }
    syncViewportHeight();
    window.addEventListener('resize', syncViewportHeight);

    const links = __QUICK_LINKS__;
    const bookmarks = __BOOKMARKS__;
    const dial = document.getElementById('speedDial');
    const bookmarkNav = document.getElementById('bookmarks');
    const bookmarkLane = document.getElementById('bookmarkLane');
    const bookmarkCount = document.getElementById('bookmarkCount');
    const dialCount = document.getElementById('dialCount');
    const form = document.getElementById('search');
    const input = document.getElementById('query');
    const edit = document.getElementById('edit');
    const clockEl = document.getElementById('clock');

    const LOCALE = '__LOCALE__';
    const L_SITES = '__L_SITES__';
    const L_SAVED = '__L_SAVED__';

    function tick() {
      const now = new Date();
      clockEl.textContent = now.toLocaleString(LOCALE, {
        weekday: 'long', hour: '2-digit', minute: '2-digit'
      });
    }
    tick();
    setInterval(tick, 30000);

    function favicon(url) {
      try {
        const u = new URL(url);
        return 'https://www.google.com/s2/favicons?sz=64&domain_url=' + encodeURIComponent(u.origin);
      } catch {
        return 'https://www.google.com/s2/favicons?sz=64&domain_url=' + encodeURIComponent(url);
      }
    }

    function host(url) {
      try { return new URL(url).hostname.replace(/^www\./, ''); }
      catch { return 'Site'; }
    }

    function post(message) {
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(message);
      }
    }

    function render() {
      dial.textContent = '';
      for (const item of links) {
        const button = document.createElement('button');
        button.className = 'dial';
        button.type = 'button';
        button.title = item.Name || host(item.Url);
        button.innerHTML = `<img src="${favicon(item.Url)}" alt=""><span class="sr-only">${item.Name || host(item.Url)}</span>`;
        button.addEventListener('click', () => post({ type: 'navigate', url: item.Url }));

        const remove = document.createElement('button');
        remove.className = 'remove';
        remove.type = 'button';
        remove.title = 'Remove';
        remove.setAttribute('aria-label', 'Remove');
        remove.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><path d="M18 6 6 18"/><path d="m6 6 12 12"/></svg>';
        remove.addEventListener('click', (event) => {
          event.stopPropagation();
          post({ type: 'removeQuickLink', url: item.Url });
        });

        button.appendChild(remove);
        dial.appendChild(button);
      }

      const add = document.createElement('button');
      add.className = 'add-dial';
      add.type = 'button';
      add.title = 'Add site';
      add.setAttribute('aria-label', 'Add site');
      add.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><path d="M12 5v14"/><path d="M5 12h14"/></svg>';
      add.addEventListener('click', () => post({ type: 'requestAddQuickLink' }));
      dial.appendChild(add);
      dialCount.textContent = links.length ? `${links.length} ${L_SITES}` : '';

      const validBookmarks = (bookmarks || []).filter(b => b && b.Url);
      if (validBookmarks.length) {
        bookmarkLane.removeAttribute('hidden');
        bookmarkNav.textContent = '';
        for (const bm of validBookmarks) {
          const a = document.createElement('button');
          a.className = 'bookmark';
          a.type = 'button';
          a.title = bm.Url;
          a.innerHTML = `<img src="${favicon(bm.Url)}" alt=""><span>${bm.Name || host(bm.Url)}</span>`;
          a.addEventListener('click', () => post({ type: 'navigate', url: bm.Url }));
          bookmarkNav.appendChild(a);
        }
        bookmarkCount.textContent = `${validBookmarks.length} ${L_SAVED}`;
      } else {
        bookmarkLane.setAttribute('hidden', '');
      }
    }

    form.addEventListener('submit', (event) => {
      event.preventDefault();
      const value = input.value.trim();
      if (!value) return;
      post({ type: 'search', query: value });
    });

    edit.addEventListener('click', () => {
      document.body.classList.toggle('editing');
    });

    document.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') document.body.classList.remove('editing');
    });

    render();
  </script>
</body>
</html>
""";
}
