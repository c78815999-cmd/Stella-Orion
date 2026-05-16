using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StellaOrion.Models;
using StellaOrion.Services;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using L10n = StellaOrion.Services.Localization;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;

namespace StellaOrion;

public partial class MainWindow : Window
{
    private const string BackgroundMediaHost = "stella-background.local";
    private const string BackgroundAudioHost = "stella-audio.local";
    private static readonly Regex ChromeExtensionIdRegex = new(@"\b[a-p]{32}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HttpClient ExtensionHttpClient = CreateExtensionHttpClient();

    private readonly BrowserSettings _settings;
    private readonly List<BrowserTab> _tabs = [];
    private readonly List<DownloadRecord> _downloads = [];
    private readonly List<HistoryEntry> _history;
    private readonly Stack<ClosedTabRecord> _recentlyClosed = new();
    private CoreWebView2Environment? _defaultEnvironment;
    private BrowserTab? _activeTab;
    private bool _bindingSettings;
    private bool _isFullscreen;
    private DispatcherTimer? _historySaveTimer;
    private DispatcherTimer? _sessionSaveTimer;
    private string _currentBookmarkSearch = string.Empty;
    private string _currentHistorySearch = string.Empty;
    private int _blockedRequestCount;
    private string _currentPane = "Privacy";
    private readonly Dictionary<string, string> _extensionRuntimeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _extensionPopupGate = new(1, 1);
    private readonly AppUpdateService _appUpdateService = new();
    private DispatcherTimer? _layoutRefreshTimer;
    private WebView2? _extensionPopupWebView;
    private bool _extensionPopupHostReady;
    private bool _extensionPopupScriptRegistered;
    private bool _extensionPopupDragging;
    private bool _secondaryPanelsRendered;
    private bool _browserCoreReady;
    private Point _extensionPopupDragStart;
    private Point _extensionPopupDragOrigin;
    private readonly HashSet<string> _extensionProfilesLoaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly TranslateTransform _extensionPopupTransform = new();
    private string? _openExtensionPopupKey;
    private string? _pendingExtensionPopupUrl;

    private const string ChromeStoreInstallHookScript = """
(() => {
  if (window.__stellaOrionInstallHook) return;
  window.__stellaOrionInstallHook = true;

  const installText = /(chrome['’]?a ekle|chrome'a ekle|chrome’a ekle|add to chrome|install|yükle|indir)/i;
  const isStoreDetail = () =>
    location.hostname === 'chromewebstore.google.com' &&
    location.pathname.includes('/detail/');

  const elementText = (el) =>
    ((el && (el.innerText || el.textContent || el.getAttribute?.('aria-label'))) || '').trim();

  const findInstallElement = (event) => {
    const path = event.composedPath ? event.composedPath() : [];
    for (const el of path) {
      if (!el || el === window || el === document) continue;
      const text = elementText(el);
      if (text && installText.test(text)) return el;
    }
    const closest = event.target?.closest?.('button,a,[role="button"]');
    return closest && installText.test(elementText(closest)) ? closest : null;
  };

  const decorate = () => {
    if (!isStoreDetail()) return;
    document.querySelectorAll('button,a,[role="button"]').forEach((el) => {
      if (!installText.test(elementText(el))) return;
      el.dataset.stellaInstall = '1';
      el.title = 'Install with Stella Orion';
      el.style.boxShadow = '0 0 0 1px rgba(56,189,248,.45), 0 12px 30px rgba(56,189,248,.18)';
    });
  };

  const requestInstall = () => {
    window.chrome?.webview?.postMessage({
      type: 'installChromeExtension',
      url: location.href,
      title: document.title
    });
  };

  const patchStoreApi = () => {
    if (!isStoreDetail()) return;
    const chromeObject = window.chrome || (window.chrome = {});
    const storeApi = chromeObject.webstorePrivate || (chromeObject.webstorePrivate = {});
    ['beginInstallWithManifest3', 'completeInstall', 'install'].forEach((name) => {
      try {
        storeApi[name] = (...args) => {
          requestInstall();
          const callback = args.find((arg) => typeof arg === 'function');
          if (callback) setTimeout(() => callback(), 0);
        };
      } catch {}
    });
  };

  document.addEventListener('click', (event) => {
    if (!isStoreDetail()) return;
    const candidate = findInstallElement(event);
    if (!candidate) return;
    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation();
    requestInstall();
  }, true);

  new MutationObserver(() => { decorate(); patchStoreApi(); }).observe(document.documentElement, { subtree: true, childList: true });
  patchStoreApi();
  decorate();
})();
""";

    private static HttpClient CreateExtensionHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        return client;
    }

    public MainWindow()
    {
        _settings = SettingsStore.Load();
        _history = HistoryStore.Load();
        _bindingSettings = true;
        InitializeComponent();
        _bindingSettings = false;
        ExtensionActionCard.RenderTransform = _extensionPopupTransform;
        ExtensionActionCard.RenderTransformOrigin = new Point(1, 0);
        TryApplyWindowIcon();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        SizeChanged += MainWindow_SizeChanged;
        SourceInitialized += MainWindow_SourceInitialized;
        RegisterCommandBindings();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProcHook);
        }
    }

    private const int WmGetMinMaxInfo = 0x0024;

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source?.CompositionTarget == null)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return IntPtr.Zero;
        }

        var work = info.rcWork;
        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(new Point(work.Left, work.Top));
        var bottomRight = transform.Transform(new Point(work.Right, work.Bottom));

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.ptMaxPosition = new NativePoint
        {
            X = (int)Math.Round(topLeft.X),
            Y = (int)Math.Round(topLeft.Y)
        };
        mmi.ptMaxSize = new NativePoint
        {
            X = (int)Math.Round(bottomRight.X - topLeft.X),
            Y = (int)Math.Round(bottomRight.Y - topLeft.Y)
        };
        Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            L10n.CurrentLanguage = _settings.Language;
            BindSettingsToControls();
            ApplyUiFont(_settings.UiFontKey);
            ApplyTheme(_settings.ThemeName);
            ApplyAccent(_settings.AccentColor);
            ApplySoftEffects();
            ApplyWindowChromeLayout();
            ApplyLanguage();
            RenderSidebar();
            UpdatePrivacyStatus();
            UpdateBookmarksBarVisibility();
            UpdateMaximizeIcon();
            UpdateVersionLabel();
            _ = InitializeBrowserCoreAsync();
            _ = CheckForAppUpdatesAsync(silent: true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Stella Orion", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializeBrowserCoreAsync()
    {
        if (_browserCoreReady) return;

        try
        {
            _defaultEnvironment = await CreateEnvironmentAsync(SettingsStore.DefaultProfilePath);

            var restored = false;
            BrowserTab? activeRestored = null;

            if (_settings.RestoreSessionOnStart && _settings.LastSession.Count > 0)
            {
                var records = _settings.LastSession.ToList();
                for (var i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    var url = string.IsNullOrWhiteSpace(record.Url) ? UrlResolver.HomeUrl : record.Url;
                    var tab = await CreateTabAsync(url, activate: false, privateMode: false, isPinned: record.IsPinned, animateInsert: false);
                    if (record.IsActive)
                    {
                        activeRestored = tab;
                    }

                    restored = true;
                }

                if (activeRestored != null)
                {
                    ActivateTab(activeRestored);
                }
                else if (_tabs.Count > 0)
                {
                    ActivateTab(_tabs[0]);
                }
            }

            if (!restored)
            {
                await CreateTabAsync(UrlResolver.HomeUrl, activate: true, privateMode: false, animateInsert: false);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                RenderBookmarksBar();
                EnsureSecondaryPanelsRendered();
                _browserCoreReady = true;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
                System.Windows.MessageBox.Show(this, ex.Message, "Stella Orion", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private void EnsureSecondaryPanelsRendered()
    {
        if (_secondaryPanelsRendered) return;
        _secondaryPanelsRendered = true;
        RenderBookmarkPanel();
        RenderHistoryPanel();
        RenderDownloads();
    }

    private void TryApplyWindowIcon()
    {
        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.png", UriKind.Absolute));
        }
        catch
        {
            try
            {
                var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(icoPath))
                {
                    Icon = BitmapFrame.Create(new Uri(icoPath, UriKind.Absolute));
                }
            }
            catch { }
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSession();
        HistoryStore.Save(_history);

        foreach (var tab in _tabs.ToList())
        {
            DisposeTab(tab);
        }

        DisposeExtensionPopupHost();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeIcon();
        ApplyWindowChromeLayout();
        ScheduleLayoutRefresh();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleLayoutRefresh();
    }

    private void ScheduleLayoutRefresh()
    {
        _layoutRefreshTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _layoutRefreshTimer.Stop();
        _layoutRefreshTimer.Tick -= LayoutRefreshTimer_Tick;
        _layoutRefreshTimer.Tick += LayoutRefreshTimer_Tick;
        _layoutRefreshTimer.Start();
    }

    private void LayoutRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _layoutRefreshTimer?.Stop();
        InvalidateWebHostLayout();
        NotifyActiveWebViewViewportChanged();
    }

    private void InvalidateWebHostLayout()
    {
        MainContentGrid.UpdateLayout();
        WebHost.UpdateLayout();
        WebHost.InvalidateMeasure();
        WebHost.InvalidateArrange();

        var size = WebHost.RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            size = new Size(WebHost.ActualWidth, WebHost.ActualHeight);
        }

        foreach (var tab in _tabs)
        {
            tab.WebView.InvalidateMeasure();
            tab.WebView.InvalidateArrange();
            if (tab.WebView.Visibility == Visibility.Visible && size.Width > 0 && size.Height > 0)
            {
                tab.WebView.Measure(size);
                tab.WebView.Arrange(new Rect(0, 0, size.Width, size.Height));
            }
        }
    }

    private void NotifyActiveWebViewViewportChanged()
    {
        var core = _activeTab?.WebView.CoreWebView2;
        if (core == null)
        {
            return;
        }

        _ = core.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
    }

    private void UpdateVersionLabel()
    {
        var version = AppUpdateService.CurrentVersion;
        AppVersionLabel.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
        CheckUpdatesButton.Content = L10n.T("Check for updates");
    }

    private async Task CheckForAppUpdatesAsync(bool silent)
    {
        if (!UpdateConfig.IsConfigured)
        {
            if (!silent)
            {
                ShowToast(L10n.T("Update source is not configured"));
            }

            return;
        }

        try
        {
            var info = await _appUpdateService.CheckForUpdateAsync();
            if (info == null)
            {
                if (!silent)
                {
                    ShowToast(L10n.T("Could not check for updates"));
                }

                return;
            }

            if (!info.HasUpdate)
            {
                if (!silent)
                {
                    ShowToast(L10n.T("You are on the latest version"));
                }

                return;
            }

            ShowToast($"{L10n.T("Update available")}: {info.TagName}");
            if (string.IsNullOrWhiteSpace(info.ReleasePageUrl))
            {
                return;
            }

            var open = System.Windows.MessageBox.Show(
                this,
                $"{L10n.T("Update available")}: {info.TagName}\n{L10n.T("Open release page?")}",
                "Stella Orion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (open == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(info.ReleasePageUrl) { UseShellExecute = true });
            }
        }
        catch
        {
            if (!silent)
            {
                ShowToast(L10n.T("Could not check for updates"));
            }
        }
    }

    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => _ = CheckForAppUpdatesAsync(silent: false);

    private void ApplyWindowChromeLayout()
    {
        var edgeToEdge = WindowState == WindowState.Maximized || _isFullscreen;

        RootGrid.Margin = new Thickness(0);
        OuterBorder.CornerRadius = edgeToEdge ? new CornerRadius(0) : new CornerRadius(14);
        OuterBorder.BorderThickness = edgeToEdge ? new Thickness(0) : new Thickness(1);

        if (edgeToEdge)
        {
            OuterBorder.Effect = null;
        }
        else
        {
            ApplySoftEffects();
        }

        ScheduleLayoutRefresh();
    }

    private void UpdateMaximizeIcon()
    {
        SetIcon(MaximizeIcon, WindowState == WindowState.Maximized ? "IconRestore" : "IconMaximize");
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private Geometry IconGeometry(string key) => (Geometry)FindResource(key);

    private System.Windows.Shapes.Path CreateSvgIcon(string key, double size, Brush? fill = null, Thickness? margin = null)
    {
        return new System.Windows.Shapes.Path
        {
            Data = IconGeometry(key),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Fill = fill ?? (Brush)FindResource("TextMain"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.9, 0.9),
            Margin = margin ?? new Thickness(0)
        };
    }

    private void SetIcon(System.Windows.Shapes.Path icon, string key, Brush? fill = null)
    {
        icon.Data = IconGeometry(key);
        if (fill != null)
        {
            icon.Fill = fill;
        }
    }

    private void RegisterCommandBindings()
    {
        AddShortcut(Key.T, ModifierKeys.Control, () => NewTab(false));
        AddShortcut(Key.N, ModifierKeys.Control | ModifierKeys.Shift, () => NewTab(true));
        AddShortcut(Key.W, ModifierKeys.Control, CloseActiveTab);
        AddShortcut(Key.F4, ModifierKeys.Control, CloseActiveTab);
        AddShortcut(Key.T, ModifierKeys.Control | ModifierKeys.Shift, ReopenLastClosedTab);
        AddShortcut(Key.L, ModifierKeys.Control, FocusAddressBar);
        AddShortcut(Key.E, ModifierKeys.Control, FocusAddressBar);
        AddShortcut(Key.D, ModifierKeys.Control, ToggleBookmarkForActiveTab);
        AddShortcut(Key.F, ModifierKeys.Control, ShowFindBar);
        AddShortcut(Key.R, ModifierKeys.Control, ReloadActive);
        AddShortcut(Key.F5, ModifierKeys.None, ReloadActive);
        AddShortcut(Key.F11, ModifierKeys.None, ToggleFullscreen);
        AddShortcut(Key.F12, ModifierKeys.None, () => _activeTab?.WebView.CoreWebView2?.OpenDevToolsWindow());
        AddShortcut(Key.Tab, ModifierKeys.Control, () => CycleTab(1));
        AddShortcut(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, () => CycleTab(-1));
        AddShortcut(Key.PageUp, ModifierKeys.Control, () => CycleTab(-1));
        AddShortcut(Key.PageDown, ModifierKeys.Control, () => CycleTab(1));
        AddShortcut(Key.Left, ModifierKeys.Alt, GoBack);
        AddShortcut(Key.Right, ModifierKeys.Alt, GoForward);
        AddShortcut(Key.Add, ModifierKeys.Control, () => AdjustZoom(0.1));
        AddShortcut(Key.OemPlus, ModifierKeys.Control, () => AdjustZoom(0.1));
        AddShortcut(Key.Subtract, ModifierKeys.Control, () => AdjustZoom(-0.1));
        AddShortcut(Key.OemMinus, ModifierKeys.Control, () => AdjustZoom(-0.1));
        AddShortcut(Key.D0, ModifierKeys.Control, ResetZoom);
        AddShortcut(Key.NumPad0, ModifierKeys.Control, ResetZoom);
        AddShortcut(Key.P, ModifierKeys.Control, PrintActive);
        AddShortcut(Key.J, ModifierKeys.Control | ModifierKeys.Shift, () => OpenPane("Downloads"));
        AddShortcut(Key.H, ModifierKeys.Control | ModifierKeys.Shift, () => OpenPane("History"));
        AddShortcut(Key.O, ModifierKeys.Control | ModifierKeys.Shift, () => OpenPane("Bookmarks"));
        AddShortcut(Key.B, ModifierKeys.Control | ModifierKeys.Shift, ToggleBookmarksBar);

        for (var i = 1; i <= 9; i++)
        {
            var index = i - 1;
            var key = Key.D0 + i;
            AddShortcut(key, ModifierKeys.Control, () => SelectTabByIndex(index));
            AddShortcut(Key.NumPad0 + i, ModifierKeys.Control, () => SelectTabByIndex(index));
        }
    }

    private void AddShortcut(Key key, ModifierKeys mods, Action action)
    {
        var binding = new InputBinding(new RelayCommand(_ => action()), new KeyGesture(key, mods));
        InputBindings.Add(binding);
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync(string userDataFolder)
    {
        Directory.CreateDirectory(userDataFolder);
        return await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder,
            options: BuildEnvironmentOptions());
    }

    private CoreWebView2EnvironmentOptions BuildEnvironmentOptions()
    {
        var args = BrowserLaunchOptions.BuildChromiumArgs(
            _settings.ForceDarkPages,
            _settings.Proxy.Enabled,
            _settings.Proxy.Enabled ? _settings.Proxy.ToProxyArgument() : null,
            _settings.Proxy.BypassLocal);

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Join(" ", args)
        };

        SetOption(options, "AllowSingleSignOnUsingOSPrimaryAccount", false);
        SetOption(options, "AreBrowserExtensionsEnabled", true);
        return options;
    }

    private static void SetOption(object target, string propertyName, object value)
    {
        target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.SetValue(target, value);
    }

    private async Task<BrowserTab> CreateTabAsync(string? address, bool activate, bool privateMode, bool isPinned = false, bool animateInsert = true)
    {
        var profilePath = privateMode ? SettingsStore.CreatePrivateProfilePath() : SettingsStore.DefaultProfilePath;
        var environment = privateMode
            ? await CreateEnvironmentAsync(profilePath)
            : _defaultEnvironment ??= await CreateEnvironmentAsync(SettingsStore.DefaultProfilePath);

        var webView = new WebView2
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var tab = new BrowserTab(webView, privateMode, privateMode ? profilePath : null)
        {
            Title = privateMode ? L10n.T("Private Tab") : L10n.T("New Tab"),
            IsPinned = isPinned,
            IsNew = animateInsert
        };

        // Insert position: right after the active tab (Opera-style)
        var insertAt = _tabs.Count;
        if (_activeTab != null)
        {
            var activeIndex = _tabs.IndexOf(_activeTab);
            if (activeIndex >= 0)
            {
                insertAt = activeIndex + 1;
                // Pinned tabs always live at the start
                if (isPinned)
                {
                    var pinnedCount = _tabs.Count(t => t.IsPinned);
                    insertAt = Math.Min(pinnedCount, insertAt);
                }
                else
                {
                    var pinnedCount = _tabs.Count(t => t.IsPinned);
                    insertAt = Math.Max(pinnedCount, insertAt);
                }
            }
        }
        _tabs.Insert(insertAt, tab);
        WebHost.Children.Add(webView);

        if (activate)
        {
            ActivateTab(tab);
        }

        RenderTabs();
        await webView.EnsureCoreWebView2Async(environment);
        ConfigureWebView(tab);
        await LoadSavedExtensionsAsync(tab);
        webView.ZoomFactor = _settings.DefaultZoom;
        NavigateTab(tab, address ?? UrlResolver.HomeUrl);
        ScheduleSessionSave();
        return tab;
    }

    private void ConfigureWebView(BrowserTab tab)
    {
        var core = tab.WebView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsScriptEnabled = true;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreHostObjectsAllowed = false;

        TrySetCoreSetting(core.Settings, "IsStatusBarEnabled", false);
        TrySetCoreSetting(core.Settings, "IsZoomControlEnabled", true);
        TrySetCoreSetting(core.Settings, "HiddenPdfToolbarItems", 0);
        TrySetCoreSetting(core, "IsDefaultDownloadDialogOpen", false);
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(ChromeStoreInstallHookScript);

        core.NavigationStarting += (_, args) =>
        {
            tab.IsLoading = true;
            if (UrlResolver.IsHome(args.Uri))
            {
                // do not overwrite stella://orion
            }
            else if (!string.Equals(args.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                tab.IsInternalNewTab = false;
                tab.Url = args.Uri;
            }
            if (_activeTab == tab)
            {
                if (!AddressBox.IsKeyboardFocusWithin)
                {
                    AddressBox.Text = UrlResolver.Display(tab.Url);
                }
                UpdateSecurityIndicator(tab.Url);
                UpdateNavigationButtons();
                UpdateStarIcon();
            }
            RenderTabs();
        };

        core.SourceChanged += (_, _) =>
        {
            var src = core.Source ?? string.Empty;
            var isInternalSource =
                string.IsNullOrEmpty(src) ||
                src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(src, "about:blank", StringComparison.OrdinalIgnoreCase);

            if (tab.IsInternalNewTab || isInternalSource)
            {
                tab.IsInternalNewTab = true;
                tab.Url = UrlResolver.HomeUrl;
                tab.Title = tab.IsPrivate ? L10n.T("Private Tab") : L10n.T("New Tab");
                if (_activeTab == tab && !AddressBox.IsKeyboardFocusWithin)
                {
                    AddressBox.Text = UrlResolver.Display(tab.Url);
                    UpdateSecurityIndicator(tab.Url);
                    UpdateStarIcon();
                }
                RenderTabs();
                UpdateWindowTitle();
                return;
            }

            tab.Url = src;
            if (_activeTab == tab && !AddressBox.IsKeyboardFocusWithin)
            {
                AddressBox.Text = UrlResolver.Display(tab.Url);
                UpdateSecurityIndicator(tab.Url);
                UpdateStarIcon();
            }
            RenderTabs();
            ScheduleSessionSave();
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            if (tab.IsInternalNewTab)
            {
                tab.Title = tab.IsPrivate ? L10n.T("Private Tab") : L10n.T("New Tab");
            }
            else
            {
                tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle)
                    ? TitleFromUrl(tab.Url, tab.IsPrivate)
                    : core.DocumentTitle;
            }
            UpdateWindowTitle();
            RenderTabs();
        };

        core.HistoryChanged += (_, _) => UpdateNavigationButtons();

        core.NavigationCompleted += (_, args) =>
        {
            tab.IsLoading = false;
            if (_activeTab == tab)
            {
                UpdateNavigationButtons();
            }

            if (args.IsSuccess && !tab.IsPrivate && !UrlResolver.IsHome(tab.Url))
            {
                RecordHistory(tab.Url, tab.Title);
            }

            if (IsChromeWebStoreDetail(tab.Url))
            {
                _ = core.ExecuteScriptAsync(ChromeStoreInstallHookScript);
            }

            RenderTabs();
        };

        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            _ = Dispatcher.InvokeAsync(() => CreateTabAsync(args.Uri, activate: true, privateMode: tab.IsPrivate));
        };

        core.PermissionRequested += (_, args) =>
        {
            if (_settings.DenySensitivePermissions && IsSensitivePermission(args.PermissionKind.ToString()))
            {
                args.State = CoreWebView2PermissionState.Deny;
                args.Handled = true;
                ShowToast($"{args.PermissionKind} blocked");
            }
        };

        core.DownloadStarting += (_, args) =>
        {
            HandleDownloadStarting(args);
        };

        core.WebMessageReceived += (_, args) =>
        {
            HandleNewTabMessage(tab, args.WebMessageAsJson);
        };

        core.WebResourceRequested += (_, args) =>
        {
            if (_settings.DoNotTrack)
            {
                args.Request.Headers.SetHeader("DNT", "1");
                args.Request.Headers.SetHeader("Sec-GPC", "1");
            }

            if (_settings.BlockTrackers && PrivacyRules.IsTracker(args.Request.Uri))
            {
                args.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(Array.Empty<byte>()),
                    403,
                    "Blocked",
                    "Content-Type: text/plain");
                System.Threading.Interlocked.Increment(ref _blockedRequestCount);
                Dispatcher.BeginInvoke(new Action(UpdateBlockCount), DispatcherPriority.Background);
            }
        };

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (core.ContainsFullScreenElement)
                {
                    EnterContentFullscreen();
                }
                else
                {
                    ExitContentFullscreen();
                }
            });
        };

        tab.WebView.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            e.Handled = true;
            AdjustZoom(e.Delta > 0 ? 0.1 : -0.1);
        };
    }

    private void UpdateBlockCount()
    {
        BlockCountLabel.Text = $"Blocked {_blockedRequestCount:n0} tracker/ad requests this session.";
    }

    private static void TrySetCoreSetting(object settings, string propertyName, object value)
    {
        try { settings.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.SetValue(settings, value); }
        catch { }
    }

    private static bool IsSensitivePermission(string permissionKind)
    {
        return permissionKind.Contains("Microphone", StringComparison.OrdinalIgnoreCase) ||
               permissionKind.Contains("Camera", StringComparison.OrdinalIgnoreCase) ||
               permissionKind.Contains("Geolocation", StringComparison.OrdinalIgnoreCase) ||
               permissionKind.Contains("Notifications", StringComparison.OrdinalIgnoreCase) ||
               permissionKind.Contains("Midi", StringComparison.OrdinalIgnoreCase) ||
               permissionKind.Contains("Sensor", StringComparison.OrdinalIgnoreCase) ||
               permissionKind.Contains("Clipboard", StringComparison.OrdinalIgnoreCase) ||
               permissionKind.Contains("WindowManagement", StringComparison.OrdinalIgnoreCase);
    }

    private void NavigateTab(BrowserTab tab, string address)
    {
        var core = tab.WebView.CoreWebView2;
        var resolved = UrlResolver.Resolve(address, _settings);

        if (UrlResolver.IsHome(resolved))
        {
            tab.Url = UrlResolver.HomeUrl;
            tab.Title = tab.IsPrivate ? L10n.T("Private Tab") : L10n.T("New Tab");
            tab.IsInternalNewTab = true;
            ApplyBackgroundMediaMapping(core);
            core.NavigateToString(NewTabPage.Build(_settings));
        }
        else
        {
            tab.IsInternalNewTab = false;
            tab.Url = resolved;
            core.Navigate(resolved);
        }

        if (_activeTab == tab)
        {
            AddressBox.Text = UrlResolver.Display(tab.Url);
            UpdateSecurityIndicator(tab.Url);
            UpdateNavigationButtons();
            UpdateStarIcon();
        }

        RenderTabs();
        ScheduleSessionSave();
    }

    private void ActivateTab(BrowserTab tab)
    {
        if (_activeTab != null)
        {
            _activeTab.WebView.Visibility = Visibility.Collapsed;
        }

        _activeTab = tab;
        tab.WebView.Visibility = Visibility.Visible;
        AddressBox.Text = UrlResolver.Display(tab.Url);
        UpdateSecurityIndicator(tab.Url);
        UpdateNavigationButtons();
        UpdateStarIcon();
        UpdateZoomIndicator();
        UpdateWindowTitle();
        RenderTabs();
        tab.WebView.Focus();
    }

    private void CloseTab(BrowserTab tab)
    {
        if (!tab.IsPrivate && !UrlResolver.IsHome(tab.Url))
        {
            _recentlyClosed.Push(new ClosedTabRecord(tab.Url, tab.Title, tab.IsPinned));
            while (_recentlyClosed.Count > 24)
            {
                var temp = _recentlyClosed.ToArray().Take(24).ToArray();
                _recentlyClosed.Clear();
                for (var i = temp.Length - 1; i >= 0; i--) _recentlyClosed.Push(temp[i]);
                break;
            }
        }

        var wasActive = _activeTab == tab;
        var index = _tabs.IndexOf(tab);
        DisposeTab(tab);
        _tabs.Remove(tab);

        if (_tabs.Count == 0)
        {
            _ = CreateTabAsync(UrlResolver.HomeUrl, activate: true, privateMode: false);
            return;
        }

        if (wasActive)
        {
            var nextIndex = Math.Clamp(index, 0, _tabs.Count - 1);
            ActivateTab(_tabs[nextIndex]);
        }

        RenderTabs();
        ScheduleSessionSave();
    }

    private void DisposeTab(BrowserTab tab)
    {
        try
        {
            WebHost.Children.Remove(tab.WebView);
            tab.WebView.Dispose();
        }
        catch { }

        if (tab.IsPrivate)
        {
            var path = tab.PrivateProfilePath;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1200);
                SettingsStore.TryDeleteDirectory(path);
            });
        }
    }

    private void RenderTabs()
    {
        TabStrip.Children.Clear();

        foreach (var tab in _tabs)
        {
            var isActive = tab == _activeTab;
            var border = new Border
            {
                MinWidth = tab.IsPinned ? 40 : 168,
                MaxWidth = tab.IsPinned ? 40 : 240,
                Height = 28,
                Margin = new Thickness(0, 0, 5, 0),
                Padding = tab.IsPinned ? new Thickness(0) : new Thickness(9, 0, 4, 0),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                BorderBrush = BrushFrom(isActive ? "#46627F" : "#263446"),
                Background = BrushFrom(isActive ? "#18283B" : "#101A27"),
                Cursor = Cursors.Hand,
                ToolTip = string.IsNullOrEmpty(tab.Title) ? tab.Url : $"{tab.Title}\n{tab.Url}",
                Tag = tab,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            if (tab.IsPinned)
            {
                var pinnedContent = CreateFaviconImage(tab.Url, 16);
                pinnedContent.HorizontalAlignment = HorizontalAlignment.Center;
                pinnedContent.VerticalAlignment = VerticalAlignment.Center;
                border.Child = pinnedContent;
            }
            else
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                UIElement iconElement;
                if (tab.IsLoading)
                {
                    iconElement = CreateSvgIcon("IconReload", 13, (Brush)FindResource("Accent"), new Thickness(0, 0, 7, 0));
                }
                else if (tab.IsPrivate)
                {
                    iconElement = CreateSvgIcon("IconPrivate", 13, BrushFrom("#C4B5FD"), new Thickness(0, 0, 7, 0));
                }
                else
                {
                    var fav = CreateFaviconImage(tab.Url, 15);
                    fav.Margin = new Thickness(0, 0, 7, 0);
                    fav.VerticalAlignment = VerticalAlignment.Center;
                    iconElement = fav;
                }
                Grid.SetColumn(iconElement, 0);

                var title = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(tab.Title) ? TitleFromUrl(tab.Url, tab.IsPrivate) : tab.Title,
                    Foreground = BrushFrom(isActive ? "#F8FAFC" : "#B6C3D1"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12
                };
                Grid.SetColumn(title, 1);

                var close = new Button
                {
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = BrushFrom("#B6C3D1"),
                    ToolTip = "Close tab",
                    Tag = "close",
                    Content = CreateSvgIcon("IconClose", 11, BrushFrom("#B6C3D1"))
                };
                close.Click += (_, e) =>
                {
                    e.Handled = true;
                    CloseTab(tab);
                };

                Grid.SetColumn(close, 2);

                grid.Children.Add(iconElement);
                grid.Children.Add(title);
                grid.Children.Add(close);

                border.Child = grid;
            }

            border.MouseUp += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle)
                {
                    e.Handled = true;
                    CloseTab(tab);
                    return;
                }

                if (e.ChangedButton != MouseButton.Left) return;
                if (IsButtonInTree(e.OriginalSource as DependencyObject)) return;
                ActivateTab(tab);
            };

            border.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowTabContextMenu(border, tab);
            };

            if (tab.IsNew)
            {
                tab.IsNew = false;
                var transform = new TranslateTransform(-14, 0);
                border.RenderTransform = transform;
                border.Opacity = 0;

                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
                var slide = new DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
                border.BeginAnimation(OpacityProperty, fade);
                transform.BeginAnimation(TranslateTransform.XProperty, slide);
            }

            TabStrip.Children.Add(border);
        }
    }

    private void ShowTabContextMenu(Border anchor, BrowserTab tab)
    {
        var menu = new ContextMenu
        {
            Background = (Brush)FindResource("PanelBg"),
            BorderBrush = (Brush)FindResource("BorderBrushSoft"),
            BorderThickness = new Thickness(1)
        };

        menu.Items.Add(MakeMenuItem("New tab to the right", "", (_, _) =>
        {
            var index = _tabs.IndexOf(tab);
            var prevActive = _activeTab;
            _activeTab = tab; // so the new one inserts after this tab
            _ = CreateTabAsync(UrlResolver.HomeUrl, activate: true, privateMode: tab.IsPrivate);
            _ = prevActive;
            _ = index;
        }));

        menu.Items.Add(MakeMenuItem("Reload", "", (_, _) => tab.WebView.CoreWebView2?.Reload()));

        menu.Items.Add(MakeMenuItem("Duplicate", "", (_, _) =>
        {
            var prevActive = _activeTab;
            _activeTab = tab;
            _ = CreateTabAsync(tab.Url, activate: true, privateMode: tab.IsPrivate);
            _ = prevActive;
        }));

        menu.Items.Add(MakeMenuItem(tab.IsPinned ? "Unpin tab" : "Pin tab", "", (_, _) =>
        {
            tab.IsPinned = !tab.IsPinned;
            ReorderPinnedTabs();
            RenderTabs();
            ScheduleSessionSave();
        }));

        menu.Items.Add(new Separator());

        menu.Items.Add(MakeMenuItem("Copy URL", "", (_, _) =>
        {
            try { Clipboard.SetText(tab.Url ?? string.Empty); ShowToast("URL copied"); }
            catch { }
        }));

        menu.Items.Add(new Separator());

        menu.Items.Add(MakeMenuItem("Close tab", "", (_, _) => CloseTab(tab)));
        menu.Items.Add(MakeMenuItem("Close other tabs", "", (_, _) =>
        {
            foreach (var other in _tabs.Where(t => t != tab && !t.IsPinned).ToList())
            {
                CloseTab(other);
            }
        }));
        menu.Items.Add(MakeMenuItem("Close tabs to the right", "", (_, _) =>
        {
            var index = _tabs.IndexOf(tab);
            foreach (var other in _tabs.Skip(index + 1).Where(t => !t.IsPinned).ToList())
            {
                CloseTab(other);
            }
        }));

        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem MakeMenuItem(string header, string icon, RoutedEventHandler action)
    {
        var item = new MenuItem
        {
            Header = header,
            Foreground = (Brush)FindResource("TextMain"),
            Background = Brushes.Transparent
        };
        if (!string.IsNullOrWhiteSpace(icon))
        {
            item.Icon = CreateSvgIcon(icon, 13, (Brush)FindResource("TextMuted"), new Thickness(0, 0, 6, 0));
        }
        item.Click += action;
        return item;
    }

    private void ReorderPinnedTabs()
    {
        var pinned = _tabs.Where(t => t.IsPinned).ToList();
        var others = _tabs.Where(t => !t.IsPinned).ToList();
        _tabs.Clear();
        _tabs.AddRange(pinned);
        _tabs.AddRange(others);
    }

    private static bool IsButtonInTree(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is Button button && button.Tag is string tag && tag == "close")
            {
                return true;
            }
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private static Brush BrushFrom(string hex)
    {
        return (Brush)new BrushConverter().ConvertFromString(hex)!;
    }

    private static string TitleFromUrl(string? url, bool privateMode)
    {
        if (UrlResolver.IsHome(url))
        {
            return privateMode ? L10n.T("Private Tab") : L10n.T("New Tab");
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase)
            : url ?? "Tab";
    }

    private void UpdateWindowTitle()
    {
        if (_activeTab == null || _activeTab.IsInternalNewTab || UrlResolver.IsHome(_activeTab.Url))
        {
            WindowTitleText.Text = "Stella Orion";
            Title = "Stella Orion";
            return;
        }

        var title = string.IsNullOrWhiteSpace(_activeTab.Title) ? "Stella Orion" : _activeTab.Title;
        WindowTitleText.Text = title;
        Title = $"{title} — Stella Orion";
    }

    private void UpdateNavigationButtons()
    {
        var core = _activeTab?.WebView.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        ReloadButton.IsEnabled = core != null;
        SetIcon(ReloadIcon, _activeTab?.IsLoading == true ? "IconClose" : "IconReload");
    }

    private void UpdateStarIcon()
    {
        if (_activeTab == null || UrlResolver.IsHome(_activeTab.Url))
        {
            SetIcon(StarIcon, "IconStarOutline", (Brush)FindResource("TextMuted"));
            return;
        }

        var isBookmarked = _settings.Bookmarks.Any(b =>
            string.Equals(b.Url, _activeTab.Url, StringComparison.OrdinalIgnoreCase));
        SetIcon(StarIcon, isBookmarked ? "IconStarFilled" : "IconStarOutline",
            isBookmarked ? (Brush)FindResource("Accent") : (Brush)FindResource("TextMuted"));
    }

    private void UpdateZoomIndicator()
    {
        if (_activeTab == null)
        {
            ZoomIndicator.Visibility = Visibility.Collapsed;
            MenuZoomLabel.Text = "100%";
            return;
        }

        var zoom = _activeTab.WebView.ZoomFactor;
        var percent = Math.Round(zoom * 100);
        MenuZoomLabel.Text = $"{percent:0}%";

        if (Math.Abs(zoom - 1.0) < 0.001)
        {
            ZoomIndicator.Visibility = Visibility.Collapsed;
        }
        else
        {
            ZoomIndicator.Visibility = Visibility.Visible;
            ZoomIndicator.Text = $"{percent:0}%";
        }
    }

    private void BindSettingsToControls()
    {
        _bindingSettings = true;
        BlockTrackersCheck.IsChecked = _settings.BlockTrackers;
        DoNotTrackCheck.IsChecked = _settings.DoNotTrack;
        HardenPermissionsCheck.IsChecked = _settings.DenySensitivePermissions;
        HttpsFirstCheck.IsChecked = _settings.HttpsFirst;
        ForceDarkPagesCheck.IsChecked = _settings.ForceDarkPages;
        BookmarksBarCheck.IsChecked = _settings.ShowBookmarksBar;
        RestoreSessionCheck.IsChecked = _settings.RestoreSessionOnStart;
        SoftEffectsCheck.IsChecked = _settings.SoftUiEffects;
        BackgroundMusicEnabledCheck.IsChecked = _settings.PlayBackgroundMusic;
        AskWhereDownloadCheck.IsChecked = _settings.AskWhereToSaveDownloads;
        HomePageBox.Text = _settings.HomePage;
        DownloadFolderBox.Text = string.IsNullOrWhiteSpace(_settings.DownloadDirectory)
            ? DefaultDownloadDirectory()
            : _settings.DownloadDirectory;
        BackgroundMediaBox.Text = string.IsNullOrWhiteSpace(_settings.BackgroundMediaPath)
            ? "No custom background"
            : _settings.BackgroundMediaPath;
        BackgroundMusicBox.Text = string.IsNullOrWhiteSpace(_settings.BackgroundMusicPath)
            ? "No background music"
            : _settings.BackgroundMusicPath;
        BackgroundOpacitySlider.Value = Math.Clamp(_settings.BackgroundMediaOpacity, 0.15, 1.0) * 100;
        BackgroundBlurSlider.Value = Math.Clamp(_settings.BackgroundMediaBlur, 0.0, 24.0);
        MusicVolumeSlider.Value = Math.Clamp(_settings.BackgroundMusicVolume, 0.02, 1.0) * 100;
        UpdatePersonalizationLabels();

        SelectComboValue(ThemeBox, _settings.ThemeName);
        SelectComboValue(SearchEngineBox, _settings.SearchEngine);
        PopulateUiFontBox();
        PopulateLanguageBox();

        foreach (var item in LanguageBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), _settings.Language, StringComparison.OrdinalIgnoreCase))
            {
                LanguageBox.SelectedItem = item;
                break;
            }
        }
        if (LanguageBox.SelectedItem == null) LanguageBox.SelectedIndex = 0;

        _bindingSettings = false;

        RenderExtensions();
        RenderExtensionToolbar();
        UpdateBlockCount();
        UpdateAccentPreview();
    }

    private void PopulateLanguageBox()
    {
        LanguageBox.Items.Clear();
        foreach (var language in L10n.Languages)
        {
            LanguageBox.Items.Add(new ComboBoxItem
            {
                Content = language.Key == "system" ? L10n.T("System default") : language.Value,
                Tag = language.Key
            });
        }
    }

    private void UpdateAccentPreview()
    {
        AccentPreview.Background = new SolidColorBrush(ParseColor(_settings.AccentColor, "#38BDF8"));
    }

    private static void SelectComboValue(ComboBox box, string? value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static void SelectComboByTag(ComboBox box, string? tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private void PopulateUiFontBox()
    {
        UiFontBox.Items.Clear();
        foreach (var font in UiFontCatalog.All)
        {
            UiFontBox.Items.Add(new ComboBoxItem
            {
                Content = font.DisplayName,
                Tag = font.Key,
                FontFamily = new FontFamily(font.FontFamily),
                FontWeight = UiFontCatalog.ResolveWeight(font)
            });
        }

        SelectComboByTag(UiFontBox, _settings.UiFontKey);
    }

    private void ApplyUiFont(string? key)
    {
        var preset = UiFontCatalog.FromKey(key);
        _settings.UiFontKey = preset.Key;
        var family = new FontFamily(preset.FontFamily);
        Application.Current.Resources["AppFont"] = family;
        Resources["AppFont"] = family;
        FontFamily = family;
        FontWeight = UiFontCatalog.ResolveWeight(preset);
    }

    private static string DefaultDownloadDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    private void SavePrivacySettingsFromControls()
    {
        _settings.BlockTrackers = BlockTrackersCheck.IsChecked == true;
        _settings.DoNotTrack = DoNotTrackCheck.IsChecked == true;
        _settings.DenySensitivePermissions = HardenPermissionsCheck.IsChecked == true;
        _settings.HttpsFirst = HttpsFirstCheck.IsChecked == true;
        SettingsStore.Save(_settings);
        UpdatePrivacyStatus();
    }

    private void UpdatePrivacyStatus()
    {
        ShieldStatus.Text = _settings.BlockTrackers ? L10n.T("Shield On") : L10n.T("Shield Off");
        ShieldBadge.ToolTip = ExtensionActionCard.Visibility == Visibility.Visible
            ? L10n.T("Close extension panel")
            : (_settings.BlockTrackers ? L10n.T("Shield On") : L10n.T("Shield Off"));
    }

    private void ShieldBadge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ExtensionActionCard.Visibility == Visibility.Visible)
        {
            CloseExtensionAction();
            return;
        }

        OpenPane("Privacy");
    }

    private void ApplyAccent(string color)
    {
        var accent = ParseColor(color, "#38BDF8");
        var accentAlt = ParseColor(color, "#14B8A6");

        SetBrushColor("Accent", accent);
        SetBrushColor("AccentAlt", accentAlt);
        UpdateAccentPreview();
    }

    private void ApplyTheme(string themeName)
    {
        var theme = ThemeCatalog.FromName(themeName);
        SetBrushColor("ChromeBg", ParseColor(theme.ChromeBg, "#08111C"));
        SetBrushColor("SidebarBg", ParseColor(theme.SidebarBg, "#050A10"));
        SetBrushColor("PanelBg", ParseColor(theme.PanelBg, "#0A121D"));
        SetBrushColor("Surface", ParseColor(theme.Surface, "#0D1623"));
        SetBrushColor("SurfaceSoft", ParseColor(theme.SurfaceSoft, "#111D2B"));
        SetBrushColor("BorderBrushSoft", ParseColor(theme.Border, "#263447"));
        SetBrushColor("TextMain", ParseColor(theme.TextMain, "#F8FAFC"));
        SetBrushColor("TextMuted", ParseColor(theme.TextMuted, "#94A3B8"));
    }

    private void ApplySoftEffects()
    {
        if (_settings.SoftUiEffects)
        {
            OuterBorder.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.18
            };
            SidePanel.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.16
            };
        }
        else
        {
            OuterBorder.Effect = null;
            SidePanel.Effect = null;
        }
    }

    private void SetBrushColor(string key, Color color)
    {
        if (Resources[key] is SolidColorBrush existing && !existing.IsFrozen)
        {
            existing.Color = color;
            return;
        }
        Resources[key] = new SolidColorBrush(color);
    }

    private static Color ParseColor(string? color, string fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(color) ? fallback : color)!;
        }
        catch
        {
            return (Color)ColorConverter.ConvertFromString(fallback)!;
        }
    }

    private void UpdateSecurityIndicator(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                SetIcon(SecurityIcon, "IconLock", BrushFrom("#22C55E"));
                SecurityBadge.Background = BrushFrom("#092515");
                SecurityBadge.ToolTip = "Secure HTTPS connection";
                return;
            }

            if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                SetIcon(SecurityIcon, "IconWarning", BrushFrom("#F87171"));
                SecurityBadge.Background = BrushFrom("#2A1116");
                SecurityBadge.ToolTip = "Not secure HTTP connection";
                return;
            }
        }

        SetIcon(SecurityIcon, "IconGlobe", BrushFrom("#94A3B8"));
        SecurityBadge.Background = BrushFrom("#10161E");
        SecurityBadge.ToolTip = "Search or internal page";
    }

    private void SecurityBadge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeTab == null) return;

        var url = _activeTab.Url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var detail = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? $"{uri.Host} — connection is encrypted"
                : $"{uri.Host} — connection is NOT encrypted";
            ShowToast(detail);
        }
        else
        {
            ShowToast("Stella Orion home page");
        }
    }

    private void RenderSidebar()
    {
        SidebarShortcuts.Children.Clear();

        var homeButton = new Button
        {
            Style = (Style)FindResource("SidebarSiteButton"),
            ToolTip = L10n.T("Home"),
            Content = CreateSidebarTile(CreateSvgIcon("IconHome", 18, (Brush)FindResource("TextMain")))
        };
        homeButton.Click += (_, _) => NavigateActiveOrCreate(_settings.HomePage);
        SidebarShortcuts.Children.Add(homeButton);
        SidebarShortcuts.Children.Add(CreateSidebarDivider());

        var links = _settings.QuickLinks.Take(16).ToList();
        for (var i = 0; i < links.Count; i++)
        {
            if (i > 0)
            {
                SidebarShortcuts.Children.Add(CreateSidebarDivider());
            }

            var quickLink = links[i];
            var button = new Button
            {
                Style = (Style)FindResource("SidebarSiteButton"),
                ToolTip = quickLink.Name,
                Content = CreateSidebarTile(CreateFaviconImage(quickLink.Url, 20))
            };

            button.Click += (_, _) => NavigateActiveOrCreate(quickLink.Url);
            SidebarShortcuts.Children.Add(button);
        }
    }

    private Border CreateSidebarDivider()
    {
        return new Border
        {
            Height = 1,
            Margin = new Thickness(8, 3, 8, 3),
            Background = (Brush)FindResource("BorderBrushSoft"),
            Opacity = 0.45
        };
    }

    private static Border CreateSidebarTile(UIElement icon)
    {
        return new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(11),
            Background = BrushFrom("#141B24"),
            BorderBrush = BrushFrom("#2A3440"),
            BorderThickness = new Thickness(1),
            Child = icon
        };
    }

    private void RenderBookmarksBar()
    {
        BookmarksBarStrip.Children.Clear();
        foreach (var bookmark in _settings.Bookmarks)
        {
            var button = new Button
            {
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 3, 0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Foreground = (Brush)FindResource("TextMain"),
                ToolTip = bookmark.Url
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var fav = CreateFaviconImage(bookmark.Url, 14);
            fav.Margin = new Thickness(0, 0, 6, 0);
            fav.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(fav);
            content.Children.Add(new TextBlock
            {
                Text = bookmark.Name,
                Foreground = (Brush)FindResource("TextMain"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            button.Content = content;
            button.Click += (_, _) => NavigateActiveOrCreate(bookmark.Url);
            button.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowBookmarkContextMenu(button, bookmark);
            };
            BookmarksBarStrip.Children.Add(button);
        }
    }

    private void ShowBookmarkContextMenu(FrameworkElement anchor, Bookmark bookmark)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("Open in new tab", "", (_, _) =>
        {
            _ = CreateTabAsync(bookmark.Url, activate: true, privateMode: false);
        }));
        menu.Items.Add(MakeMenuItem("Open in new private tab", "", (_, _) =>
        {
            _ = CreateTabAsync(bookmark.Url, activate: true, privateMode: true);
        }));
        menu.Items.Add(MakeMenuItem("Copy URL", "", (_, _) =>
        {
            try { Clipboard.SetText(bookmark.Url); ShowToast("URL copied"); }
            catch { }
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("Edit", "", (_, _) =>
        {
            var dialog = new QuickLinkDialog(this, bookmark.Name, bookmark.Url, "Edit Bookmark");
            if (dialog.ShowDialog() == true)
            {
                bookmark.Name = string.IsNullOrWhiteSpace(dialog.SiteName) ? bookmark.Name : dialog.SiteName.Trim();
                bookmark.Url = string.IsNullOrWhiteSpace(dialog.SiteUrl) ? bookmark.Url : dialog.SiteUrl.Trim();
                SettingsStore.Save(_settings);
                RenderBookmarksBar();
                RenderBookmarkPanel();
                UpdateStarIcon();
            }
        }));
        menu.Items.Add(MakeMenuItem("Remove", "", (_, _) =>
        {
            _settings.Bookmarks.RemoveAll(b => string.Equals(b.Url, bookmark.Url, StringComparison.OrdinalIgnoreCase));
            SettingsStore.Save(_settings);
            RenderBookmarksBar();
            RenderBookmarkPanel();
            UpdateBookmarksBarVisibility();
            UpdateStarIcon();
            ShowToast("Bookmark removed");
        }));
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void UpdateBookmarksBarVisibility()
    {
        BookmarksBar.Visibility = _settings.ShowBookmarksBar && _settings.Bookmarks.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private Image CreateFaviconImage(string url, double size)
    {
        var image = new Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true
        };

        try
        {
            image.Source = new BitmapImage(new Uri(FaviconUrl(url), UriKind.Absolute));
        }
        catch
        {
            image.Source = null;
        }

        return image;
    }

    private static string FaviconUrl(string url)
    {
        return $"https://www.google.com/s2/favicons?sz=64&domain_url={Uri.EscapeDataString(url)}";
    }

    private void NavigateActiveOrCreate(string url)
    {
        if (_activeTab == null)
        {
            _ = CreateTabAsync(url, activate: true, privateMode: false);
            return;
        }

        NavigateTab(_activeTab, url);
    }

    private void RefreshNewTabPages()
    {
        foreach (var tab in _tabs.Where(tab => tab.IsInternalNewTab || UrlResolver.IsHome(tab.Url)))
        {
            tab.IsInternalNewTab = true;
            tab.Title = tab.IsPrivate ? L10n.T("Private Tab") : L10n.T("New Tab");
            if (tab.WebView.CoreWebView2 != null)
            {
                ApplyBackgroundMediaMapping(tab.WebView.CoreWebView2);
            }
            tab.WebView.CoreWebView2?.NavigateToString(NewTabPage.Build(_settings));
        }
    }

    private void ApplyBackgroundMediaMapping(CoreWebView2 core)
    {
        try
        {
            core.ClearVirtualHostNameToFolderMapping(BackgroundMediaHost);
            core.ClearVirtualHostNameToFolderMapping(BackgroundAudioHost);
        }
        catch { }

        MapLocalMediaFolder(core, BackgroundMediaHost, _settings.BackgroundMediaPath);
        MapLocalMediaFolder(core, BackgroundAudioHost, _settings.BackgroundMusicPath);
    }

    private static void MapLocalMediaFolder(CoreWebView2 core, string host, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            core.SetVirtualHostNameToFolderMapping(
                host,
                folder,
                CoreWebView2HostResourceAccessKind.Allow);
        }
        catch { }
    }

    private void HandleNewTabMessage(BrowserTab tab, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var type = typeElement.GetString();
            switch (type)
            {
                case "addQuickLink":
                    AddQuickLink(
                        root.TryGetProperty("name", out var name) ? name.GetString() : null,
                        root.TryGetProperty("url", out var url) ? url.GetString() : null);
                    break;
                case "removeQuickLink":
                    if (root.TryGetProperty("url", out var removeUrl))
                    {
                        RemoveQuickLink(removeUrl.GetString());
                    }
                    break;
                case "requestAddQuickLink":
                    Dispatcher.Invoke(AddQuickLinkFromDialog);
                    break;
                case "navigate":
                    if (root.TryGetProperty("url", out var navigateUrl))
                    {
                        NavigateTab(tab, navigateUrl.GetString() ?? string.Empty);
                    }
                    break;
                case "search":
                    if (root.TryGetProperty("query", out var queryEl))
                    {
                        NavigateTab(tab, queryEl.GetString() ?? string.Empty);
                    }
                    break;
                case "installChromeExtension":
                    var source = root.TryGetProperty("url", out var installUrl)
                        ? installUrl.GetString()
                        : tab.Url;
                    var extensionId = ExtractChromeExtensionId(source);
                    if (extensionId != null)
                    {
                        _ = InstallChromeWebStoreExtensionAsync(extensionId);
                    }
                    else
                    {
                        ShowToast("Extension ID could not be detected");
                    }
                    break;
            }
        }
        catch { }
    }

    private void AddQuickLink(string? name, string? url)
    {
        var resolved = UrlResolver.Resolve(url, _settings);
        if (UrlResolver.IsHome(resolved) || !Uri.TryCreate(resolved, UriKind.Absolute, out var uri))
        {
            ShowToast("Enter a valid site address");
            return;
        }

        if (_settings.QuickLinks.Any(link => string.Equals(link.Url, resolved, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("Site already exists");
            return;
        }

        _settings.QuickLinks.Add(new QuickLink
        {
            Name = string.IsNullOrWhiteSpace(name) ? uri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase) : name.Trim(),
            Url = resolved
        });

        SettingsStore.Save(_settings);
        RenderSidebar();
        RefreshNewTabPages();
        ShowToast("Speed dial site added");
    }

    private void RemoveQuickLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        _settings.QuickLinks.RemoveAll(link => string.Equals(link.Url, url, StringComparison.OrdinalIgnoreCase));
        SettingsStore.Save(_settings);
        RenderSidebar();
        RefreshNewTabPages();
        ShowToast("Speed dial site removed");
    }

    private void AddQuickLinkFromDialog()
    {
        var dialog = new QuickLinkDialog(this);
        if (dialog.ShowDialog() == true)
        {
            AddQuickLink(dialog.SiteName, dialog.SiteUrl);
        }
    }

    private void OpenPane(string name)
    {
        EnsureSecondaryPanelsRendered();
        SidePanel.Visibility = Visibility.Visible;
        SidePanel.Opacity = 0;
        PanelColumn.Width = new GridLength(380);
        ShowPane(name);
        SidePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void ShowPane(string name)
    {
        _currentPane = name;
        PanelTitle.Text = PrettyName(name);

        PanePrivacy.Visibility = name == "Privacy" ? Visibility.Visible : Visibility.Collapsed;
        PaneAppearance.Visibility = name == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        PaneSearch.Visibility = name == "Search" ? Visibility.Visible : Visibility.Collapsed;
        PaneExtensions.Visibility = name == "Extensions" ? Visibility.Visible : Visibility.Collapsed;
        PaneBookmarks.Visibility = name == "Bookmarks" ? Visibility.Visible : Visibility.Collapsed;
        PaneHistory.Visibility = name == "History" ? Visibility.Visible : Visibility.Collapsed;
        PaneDownloads.Visibility = name == "Downloads" ? Visibility.Visible : Visibility.Collapsed;

        NavPrivacy.IsChecked = name == "Privacy";
        NavAppearance.IsChecked = name == "Appearance";
        NavSearch.IsChecked = name == "Search";
        NavExtensions.IsChecked = name == "Extensions";
        NavBookmarks.IsChecked = name == "Bookmarks";
        NavHistory.IsChecked = name == "History";
        NavDownloads.IsChecked = name == "Downloads";
    }

    private static string PrettyName(string name) => name switch
    {
        "Search" => L10n.T("Search & Home"),
        _ => L10n.T(name)
    };

    private void ClosePanel()
    {
        SidePanel.Visibility = Visibility.Collapsed;
        PanelColumn.Width = new GridLength(0);
    }

    private void PanelNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string name)
        {
            ShowPane(name);
        }
    }

    private async Task LoadSavedExtensionsAsync(BrowserTab tab)
    {
        var profileKey = tab.PrivateProfilePath ?? SettingsStore.DefaultProfilePath;
        if (!_extensionProfilesLoaded.Add(profileKey)) return;

        foreach (var extensionPath in _settings.ExtensionPaths.Where(Directory.Exists))
        {
            await TryAddExtensionAsync(tab, extensionPath, silent: true);
        }
    }

    private async Task<string> TryAddExtensionAsync(BrowserTab tab, string extensionPath, bool silent)
    {
        if (!File.Exists(Path.Combine(extensionPath, "manifest.json")))
        {
            return "manifest.json was not found.";
        }

        var profile = tab.WebView.CoreWebView2.Profile;
        var method = profile.GetType().GetMethod("AddBrowserExtensionAsync", [typeof(string)]);
        if (method == null)
        {
            return "This WebView2 runtime does not expose extension APIs yet.";
        }

        try
        {
            var extension = await AwaitTask(method.Invoke(profile, [extensionPath]));
            var runtimeId = extension?.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(extension)?.ToString();
            if (!string.IsNullOrWhiteSpace(runtimeId))
            {
                _extensionRuntimeIds[NormalizeExtensionPath(extensionPath)] = runtimeId;
            }
            if (!silent) ShowToast("Extension loaded");
            RenderExtensionToolbar();
            return "Loaded";
        }
        catch (TargetInvocationException ex) { return ex.InnerException?.Message ?? ex.Message; }
        catch (Exception ex) { return ex.Message; }
    }

    private async Task InstallChromeWebStoreExtensionFromActiveTabAsync()
    {
        var url = _activeTab?.Url ?? string.Empty;
        var extensionId = ExtractChromeExtensionId(url);
        if (extensionId == null)
        {
            ShowToast("Open a Chrome Web Store extension page first");
            return;
        }

        await InstallChromeWebStoreExtensionAsync(extensionId);
    }

    private async Task InstallChromeWebStoreExtensionAsync(string extensionId)
    {
        if (_activeTab == null) return;

        try
        {
            ShowToast("Installing extension...");
            var extensionPath = await DownloadAndUnpackChromeExtensionAsync(extensionId);
            var result = await TryAddExtensionAsync(_activeTab, extensionPath, silent: false);
            if (string.Equals(result, "Loaded", StringComparison.OrdinalIgnoreCase))
            {
                if (!_settings.ExtensionPaths.Contains(extensionPath, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.ExtensionPaths.Add(extensionPath);
                    SettingsStore.Save(_settings);
                }
            }
            else
            {
                ShowToast(result);
            }

            RenderExtensions();
            RenderExtensionToolbar();
        }
        catch (Exception ex)
        {
            ShowToast($"Extension install failed: {ex.Message}");
        }
    }

    private static string? ExtractChromeExtensionId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = ChromeExtensionIdRegex.Match(text);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static bool IsChromeWebStoreDetail(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               uri.Host.Equals("chromewebstore.google.com", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.Contains("/detail/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> DownloadAndUnpackChromeExtensionAsync(string extensionId)
    {
        var crxBytes = await DownloadChromeExtensionPackageAsync(extensionId);
        return await UnpackChromeExtensionAsync(crxBytes, extensionId);
    }

    private static async Task<byte[]> DownloadChromeExtensionPackageAsync(string extensionId)
    {
        var escaped = Uri.EscapeDataString(extensionId);
        var urls = new[]
        {
            "https://clients2.google.com/service/update2/crx" +
            "?response=redirect&prod=chromecrx&prodchannel=stable&prodversion=124.0.6367.60" +
            "&acceptformat=crx2,crx3&os=win&arch=x64&nacl_arch=x86-64" +
            $"&x=id%3D{escaped}%26installsource%3Dondemand%26uc",
            "https://clients2.google.com/service/update2/crx" +
            "?response=redirect&prodversion=124.0.0.0&acceptformat=crx2,crx3" +
            $"&x=id%3D{escaped}%26installsource%3Dondemand%26uc",
            "https://clients2.google.com/service/update2/crx" +
            "?response=redirect&prod=chromecrx&prodversion=124.0.0.0&acceptformat=crx2,crx3" +
            $"&x=id%3D{escaped}%26uc"
        };

        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Referrer = new Uri($"https://chromewebstore.google.com/detail/{extensionId}");
                request.Headers.Accept.ParseAdd("application/x-chrome-extension, application/octet-stream, */*");
                using var response = await ExtensionHttpClient.SendAsync(request);
                var payload = await response.Content.ReadAsByteArrayAsync();
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new InvalidOperationException($"Chrome Web Store returned {(int)response.StatusCode}");
                    continue;
                }

                if (payload.Length < 4)
                {
                    lastError = new InvalidOperationException("Chrome Web Store returned an empty package");
                    continue;
                }

                if ((payload[0] == (byte)'C' && payload[1] == (byte)'r' && payload[2] == (byte)'2' && payload[3] == (byte)'4') ||
                    (payload[0] == (byte)'P' && payload[1] == (byte)'K'))
                {
                    return payload;
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
                lastError = new InvalidOperationException($"Chrome Web Store returned {contentType}, not a CRX package");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(lastError?.Message ?? "Could not download the extension package");
    }

    private async Task InstallDownloadedCrxAsync(string crxPath, string extensionId)
    {
        if (_activeTab == null || !File.Exists(crxPath)) return;

        try
        {
            var extensionPath = await UnpackChromeExtensionAsync(await File.ReadAllBytesAsync(crxPath), extensionId);
            var result = await TryAddExtensionAsync(_activeTab, extensionPath, silent: false);
            if (string.Equals(result, "Loaded", StringComparison.OrdinalIgnoreCase))
            {
                if (!_settings.ExtensionPaths.Contains(extensionPath, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.ExtensionPaths.Add(extensionPath);
                    SettingsStore.Save(_settings);
                }
            }
            else
            {
                ShowToast(result);
            }

            RenderExtensions();
            RenderExtensionToolbar();
        }
        catch (Exception ex)
        {
            ShowToast($"Extension install failed: {ex.Message}");
        }
    }

    private static Task<string> UnpackChromeExtensionAsync(byte[] package, string extensionId)
    {
        var zipBytes = ExtractZipPayload(package);
        return UnpackZipExtensionAsync(zipBytes, extensionId);
    }

    private static async Task<string> UnpackZipExtensionAsync(byte[] zipBytes, string extensionId)
    {
        var root = Path.Combine(SettingsStore.AppDataRoot, "Extensions");
        Directory.CreateDirectory(root);

        var tempRoot = Path.Combine(root, $"{extensionId}.tmp-{Guid.NewGuid():N}");
        var finalRoot = Path.Combine(root, extensionId);
        Directory.CreateDirectory(tempRoot);

        try
        {
            var zipPath = Path.Combine(tempRoot, "extension.zip");
            await File.WriteAllBytesAsync(zipPath, zipBytes);
            ZipFile.ExtractToDirectory(zipPath, tempRoot, overwriteFiles: true);
            File.Delete(zipPath);

            var manifestPath = Directory
                .EnumerateFiles(tempRoot, "manifest.json", SearchOption.AllDirectories)
                .OrderBy(path => path.Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar))
                .FirstOrDefault();

            if (manifestPath == null)
            {
                throw new InvalidOperationException("manifest.json was not found in the package");
            }

            var manifestRoot = Path.GetDirectoryName(manifestPath)!;
            SettingsStore.TryDeleteDirectory(finalRoot);
            CopyDirectory(manifestRoot, finalRoot);
            return finalRoot;
        }
        finally
        {
            SettingsStore.TryDeleteDirectory(tempRoot);
        }
    }

    private static byte[] ExtractZipPayload(byte[] package)
    {
        if (package.Length >= 4 &&
            package[0] == (byte)'P' &&
            package[1] == (byte)'K')
        {
            return package;
        }

        if (package.Length < 16 ||
            package[0] != (byte)'C' ||
            package[1] != (byte)'r' ||
            package[2] != (byte)'2' ||
            package[3] != (byte)'4')
        {
            throw new InvalidOperationException("Downloaded package is not a CRX file");
        }

        var version = BitConverter.ToUInt32(package, 4);
        var offset = version switch
        {
            2 => 16 + BitConverter.ToUInt32(package, 8) + BitConverter.ToUInt32(package, 12),
            3 => 12 + BitConverter.ToUInt32(package, 8),
            _ => throw new InvalidOperationException($"Unsupported CRX version {version}")
        };

        if (offset >= package.Length)
        {
            throw new InvalidOperationException("CRX payload is empty");
        }

        var zip = new byte[package.Length - offset];
        Buffer.BlockCopy(package, (int)offset, zip, 0, zip.Length);
        return zip;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            File.Copy(file, Path.Combine(destination, relative), overwrite: true);
        }
    }

    private static async Task<object?> AwaitTask(object? value)
    {
        if (value is not Task task) return null;
        await task.ConfigureAwait(true);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private void RenderExtensions()
    {
        ExtensionList.Children.Clear();

        if (_settings.ExtensionPaths.Count == 0)
        {
            ExtensionList.Children.Add(new TextBlock
            {
                Text = "No extensions added yet.",
                Foreground = BrushFrom("#9BA9BC"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var extensionPath in _settings.ExtensionPaths.ToList())
        {
            var row = new Border
            {
                Background = BrushFrom("#101A27"),
                BorderBrush = BrushFrom("#263446"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(extensionPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            text.Children.Add(new TextBlock
            {
                Text = extensionPath,
                Foreground = BrushFrom("#9BA9BC"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(text, 0);

            var remove = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 30,
                Height = 28,
                ToolTip = "Remove saved path",
                Content = CreateSvgIcon("IconClose", 12)
            };
            remove.Click += (_, _) =>
            {
                _settings.ExtensionPaths.Remove(extensionPath);
                _extensionRuntimeIds.Remove(NormalizeExtensionPath(extensionPath));
                SettingsStore.Save(_settings);
                RenderExtensions();
                RenderExtensionToolbar();
                ShowToast("Extension path removed");
            };
            Grid.SetColumn(remove, 1);

            grid.Children.Add(text);
            grid.Children.Add(remove);
            row.Child = grid;
            ExtensionList.Children.Add(row);
        }
    }

    private void RenderExtensionToolbar()
    {
        ExtensionActionStrip.Children.Clear();

        foreach (var extensionPath in _settings.ExtensionPaths.Where(Directory.Exists).Take(8))
        {
            var info = ReadExtensionInfo(extensionPath);
            var button = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Width = 30,
                Height = 28,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = info.Name,
                Content = CreateExtensionIcon(info, 16)
            };
            button.Click += async (_, _) => await ShowExtensionActionAsync(info);
            ExtensionActionStrip.Children.Add(button);
        }
    }

    private async Task ShowExtensionActionAsync(ExtensionManifestInfo info)
    {
        var popupKey = $"{info.Id}|{info.PopupPath}";
        if (ExtensionActionCard.Visibility == Visibility.Visible &&
            string.Equals(_openExtensionPopupKey, popupKey, StringComparison.Ordinal))
        {
            CloseExtensionAction();
            return;
        }

        await _extensionPopupGate.WaitAsync();
        try
        {
            ExtensionPopupTitle.Text = info.Name;
            ExtensionPopupSubtitle.Text = string.IsNullOrWhiteSpace(info.Id) ? "Unpacked extension" : info.Id;
            ExtensionPopupIcon.Child = CreateExtensionIcon(info, 18);
            ExtensionFallbackPanel.Children.Clear();
            ExtensionFallbackPanel.Visibility = Visibility.Collapsed;
            ExtensionActionCard.Visibility = Visibility.Visible;
            _openExtensionPopupKey = popupKey;
            UpdatePrivacyStatus();

            if (!string.IsNullOrWhiteSpace(info.Id) && !string.IsNullOrWhiteSpace(info.PopupPath))
            {
                try
                {
                    SetExtensionPopupChromeMode(compact: true);
                    await EnsureExtensionPopupHostAsync();
                    ResetExtensionPopupSizeForLoading();
                    ExtensionPopupLoading.Visibility = Visibility.Visible;
                    if (_extensionPopupWebView != null)
                    {
                        _extensionPopupWebView.Visibility = Visibility.Collapsed;
                    }

                    _pendingExtensionPopupUrl = BuildExtensionUrl(info.Id, info.PopupPath);
                    _extensionPopupWebView!.CoreWebView2!.Navigate(_pendingExtensionPopupUrl);
                    return;
                }
                catch (Exception ex)
                {
                    ExtensionPopupLoading.Visibility = Visibility.Collapsed;
                    ShowToast($"Extension popup could not open: {ex.Message}");
                }
            }

            ShowExtensionFallback(info);
        }
        finally
        {
            _extensionPopupGate.Release();
        }
    }

    private async Task WarmExtensionPopupHostAsync()
    {
        if (_settings.ExtensionPaths.All(path => !Directory.Exists(path))) return;

        try
        {
            await EnsureExtensionPopupHostAsync();
        }
        catch
        {
            // Popup host warms on first click if startup warm-up fails.
        }
    }

    private async Task EnsureExtensionPopupHostAsync()
    {
        if (_extensionPopupHostReady && _extensionPopupWebView?.CoreWebView2 != null) return;

        if (_extensionPopupWebView == null)
        {
            _extensionPopupWebView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 16, 22, 30),
                Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(_extensionPopupWebView, 0);
            ExtensionActionHost.Children.Insert(0, _extensionPopupWebView);
        }

        _defaultEnvironment ??= await CreateEnvironmentAsync(SettingsStore.DefaultProfilePath);
        if (_extensionPopupWebView.CoreWebView2 is null)
        {
            await _extensionPopupWebView.EnsureCoreWebView2Async(_defaultEnvironment);
            if (_extensionPopupWebView.CoreWebView2 is { } core)
            {
                var settings = core.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsZoomControlEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                settings.IsWebMessageEnabled = true;
                core.NavigationCompleted += ExtensionPopup_NavigationCompleted;

                if (!_extensionPopupScriptRegistered)
                {
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(ExtensionPopupScripts.DocumentCreated);
                    core.WebMessageReceived += ExtensionPopup_WebMessageReceived;
                    _extensionPopupScriptRegistered = true;
                }
            }
        }

        _extensionPopupHostReady = true;
    }

    private void ExtensionPopup_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!string.Equals(root.GetProperty("type").GetString(), "stella-popup-size", StringComparison.Ordinal))
            {
                return;
            }

            var width = root.GetProperty("width").GetInt32();
            var height = root.GetProperty("height").GetInt32();
            if (width < 40 || height < 40) return;

            Dispatcher.Invoke(() =>
            {
                if (ExtensionActionCard.Visibility != Visibility.Visible) return;
                ApplyExtensionPopupSize(width, height);
            });
        }
        catch
        {
            // Ignore malformed extension messages.
        }
    }

    private void ResetExtensionPopupSizeForLoading()
    {
        ApplyExtensionPopupSize(316, 76);
        ExtensionPopupLoading.Width = 316;
        ExtensionPopupLoading.Height = 76;
    }

    private void ApplyExtensionPopupSize(double contentWidth, double contentHeight)
    {
        var width = Math.Clamp(contentWidth, 260, 440);
        var height = Math.Clamp(contentHeight, 72, 600);

        if (_extensionPopupWebView != null)
        {
            _extensionPopupWebView.Width = width;
            _extensionPopupWebView.Height = height;
        }

        ExtensionActionHost.Width = width;
        ExtensionActionHost.Height = height;
        ExtensionPopupLoading.Width = width;
        ExtensionPopupLoading.Height = height;
        ExtensionActionCard.Width = width + 2;
        ExtensionActionCard.Height = height + 2;
    }

    private void ExtensionPopup_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _ = HandleExtensionPopupNavigationCompletedAsync(e);
    }

    private async Task HandleExtensionPopupNavigationCompletedAsync(CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_extensionPopupWebView == null || ExtensionActionCard.Visibility != Visibility.Visible) return;

        var source = _extensionPopupWebView.CoreWebView2?.Source;
        var pending = _pendingExtensionPopupUrl?.Split('?')[0];
        if (!string.IsNullOrWhiteSpace(pending) &&
            (source == null || !source.StartsWith(pending, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (!e.IsSuccess)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                ExtensionPopupLoading.Visibility = Visibility.Collapsed;
                _extensionPopupWebView!.Visibility = Visibility.Collapsed;
                ShowToast("Extension popup failed to load");
            });
            return;
        }

        await ResizeExtensionPopupToContentAsync();

        await Dispatcher.InvokeAsync(() =>
        {
            ExtensionPopupLoading.Visibility = Visibility.Collapsed;
            _pendingExtensionPopupUrl = null;
            if (_extensionPopupWebView != null && ExtensionActionCard.Visibility == Visibility.Visible)
            {
                _extensionPopupWebView.Visibility = Visibility.Visible;
            }
        });
    }

    private async Task ResizeExtensionPopupToContentAsync()
    {
        if (_extensionPopupWebView?.CoreWebView2 == null) return;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(attempt switch
                {
                    1 => 50,
                    2 => 120,
                    _ => 240
                });
            }

            try
            {
                var raw = await _extensionPopupWebView.CoreWebView2.ExecuteScriptAsync(ExtensionPopupScripts.MeasureNow);
                using var doc = JsonDocument.Parse(raw);
                var width = doc.RootElement.GetProperty("width").GetInt32();
                var height = doc.RootElement.GetProperty("height").GetInt32();
                if (width < 40 || height < 40) continue;

                await Dispatcher.InvokeAsync(() => ApplyExtensionPopupSize(width, height));
                return;
            }
            catch
            {
                // Extension may still be laying out; retry.
            }
        }
    }

    private void SetExtensionPopupChromeMode(bool compact)
    {
        ExtensionPopupHeader.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ExtensionPopupDragBar.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        ExtensionPopupCloseOverlay.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        ExtensionActionHost.Margin = new Thickness(0);

        if (!compact)
        {
            ApplyExtensionPopupSize(340, 360);
        }
    }

    private void ExtensionPopupDragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ExtensionActionCard.Visibility != Visibility.Visible) return;
        _extensionPopupDragging = true;
        _extensionPopupDragStart = e.GetPosition(this);
        _extensionPopupDragOrigin = new Point(_extensionPopupTransform.X, _extensionPopupTransform.Y);
        ExtensionPopupDragBar.CaptureMouse();
        e.Handled = true;
    }

    private void ExtensionPopupDragBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_extensionPopupDragging) return;
        var pos = e.GetPosition(this);
        _extensionPopupTransform.X = _extensionPopupDragOrigin.X + (pos.X - _extensionPopupDragStart.X);
        _extensionPopupTransform.Y = _extensionPopupDragOrigin.Y + (pos.Y - _extensionPopupDragStart.Y);
    }

    private void ExtensionPopupDragBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_extensionPopupDragging) return;
        _extensionPopupDragging = false;
        ExtensionPopupDragBar.ReleaseMouseCapture();
    }

    private void ShowExtensionFallback(ExtensionManifestInfo info)
    {
        SetExtensionPopupChromeMode(compact: false);
        ExtensionPopupLoading.Visibility = Visibility.Collapsed;
        if (_extensionPopupWebView != null)
        {
            _extensionPopupWebView.Visibility = Visibility.Collapsed;
        }

        ExtensionFallbackPanel.Children.Clear();
        ExtensionFallbackPanel.Children.Add(BuildExtensionFallbackPanel(info));
        ExtensionFallbackPanel.Visibility = Visibility.Visible;
    }

    private void DisposeExtensionPopupHost()
    {
        if (_extensionPopupWebView == null) return;

        try
        {
            if (_extensionPopupWebView.CoreWebView2 != null)
            {
                _extensionPopupWebView.CoreWebView2.NavigationCompleted -= ExtensionPopup_NavigationCompleted;
                _extensionPopupWebView.CoreWebView2.WebMessageReceived -= ExtensionPopup_WebMessageReceived;
            }
            _extensionPopupWebView.Dispose();
        }
        catch { }

        _extensionPopupWebView = null;
        _extensionPopupHostReady = false;
        _extensionPopupScriptRegistered = false;
        _openExtensionPopupKey = null;
        _pendingExtensionPopupUrl = null;
    }

    private FrameworkElement BuildExtensionFallbackPanel(ExtensionManifestInfo info)
    {
        var stack = new StackPanel { Margin = new Thickness(4) };
        stack.Children.Add(new TextBlock
        {
            Text = "This extension does not expose a popup, so Stella can open its internal pages instead.",
            Foreground = (Brush)FindResource("TextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        if (!string.IsNullOrWhiteSpace(info.OptionsPath))
        {
            var options = new Button
            {
                Style = (Style)FindResource("WideButton"),
                Content = "Open options",
                Margin = new Thickness(0, 0, 0, 8)
            };
            options.Click += (_, _) => OpenExtensionPage(info, info.OptionsPath);
            stack.Children.Add(options);
        }

        if (!string.IsNullOrWhiteSpace(info.PopupPath))
        {
            var popup = new Button
            {
                Style = (Style)FindResource("WideButton"),
                Content = "Open popup in tab",
                Margin = new Thickness(0, 0, 0, 8)
            };
            popup.Click += (_, _) => OpenExtensionPage(info, info.PopupPath);
            stack.Children.Add(popup);
        }

        var folder = new TextBlock
        {
            Text = info.Path,
            Foreground = (Brush)FindResource("TextMuted"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        stack.Children.Add(folder);
        return stack;
    }

    private void OpenExtensionPage(ExtensionManifestInfo info, string? page)
    {
        if (string.IsNullOrWhiteSpace(info.Id) || string.IsNullOrWhiteSpace(page))
        {
            ShowToast("Extension page is not available");
            return;
        }

        CloseExtensionAction();
        NavigateActiveOrCreate(BuildExtensionUrl(info.Id, page));
    }

    private static string BuildExtensionUrl(string id, string page)
    {
        return $"chrome-extension://{id}/{page.TrimStart('/', '\\')}";
    }

    private FrameworkElement CreateExtensionIcon(ExtensionManifestInfo info, double size)
    {
        if (!string.IsNullOrWhiteSpace(info.IconPath) && File.Exists(info.IconPath))
        {
            try
            {
                return new Image
                {
                    Source = new BitmapImage(new Uri(info.IconPath, UriKind.Absolute)),
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true
                };
            }
            catch { }
        }

        return CreateSvgIcon("IconExtensions", size, (Brush)FindResource("TextMain"));
    }

    private ExtensionManifestInfo ReadExtensionInfo(string extensionPath)
    {
        var normalized = NormalizeExtensionPath(extensionPath);
        var folderId = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var id = _extensionRuntimeIds.TryGetValue(normalized, out var runtimeId)
            ? runtimeId
            : ExtractChromeExtensionId(folderId);
        var manifestPath = Path.Combine(extensionPath, "manifest.json");

        if (!File.Exists(manifestPath))
        {
            return new ExtensionManifestInfo(extensionPath, id, folderId, null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            var defaultLocale = GetManifestString(root, "default_locale");
            var name = ResolveManifestText(
                GetManifestString(root, "name") ?? folderId,
                extensionPath,
                defaultLocale);
            var popup = GetNestedManifestString(root, "action", "default_popup") ??
                        GetNestedManifestString(root, "browser_action", "default_popup") ??
                        GetNestedManifestString(root, "page_action", "default_popup");
            var options = GetManifestString(root, "options_page") ??
                          GetNestedManifestString(root, "options_ui", "page");
            var iconPath = GetBestIconPath(root, extensionPath);
            return new ExtensionManifestInfo(extensionPath, id, name, iconPath, popup, options);
        }
        catch
        {
            return new ExtensionManifestInfo(extensionPath, id, folderId, null, null, null);
        }
    }

    private static string NormalizeExtensionPath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }

    private static string? GetManifestString(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? GetNestedManifestString(JsonElement root, string parent, string property)
    {
        return root.TryGetProperty(parent, out var parentElement) &&
               parentElement.ValueKind == JsonValueKind.Object &&
               parentElement.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string ResolveManifestText(string value, string extensionPath, string? defaultLocale)
    {
        var match = Regex.Match(value, @"^__MSG_(.+)__$", RegexOptions.IgnoreCase);
        if (!match.Success) return value;

        var key = match.Groups[1].Value;
        foreach (var locale in new[] { defaultLocale, "en", "en_US", "tr" }.Where(locale => !string.IsNullOrWhiteSpace(locale)))
        {
            var message = TryReadLocaleMessage(extensionPath, locale!, key);
            if (!string.IsNullOrWhiteSpace(message)) return message;
        }

        return key;
    }

    private static string? TryReadLocaleMessage(string extensionPath, string locale, string key)
    {
        try
        {
            var file = Path.Combine(extensionPath, "_locales", locale, "messages.json");
            if (!File.Exists(file)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.TryGetProperty(key, out var entry) &&
                   entry.TryGetProperty("message", out var message) &&
                   message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch { return null; }
    }

    private static string? GetBestIconPath(JsonElement root, string extensionPath)
    {
        if (root.TryGetProperty("action", out var action) &&
            action.TryGetProperty("default_icon", out var actionIcon) &&
            TryResolveIconElement(actionIcon, extensionPath, out var actionPath))
        {
            return actionPath;
        }

        if (root.TryGetProperty("icons", out var icons) &&
            TryResolveIconElement(icons, extensionPath, out var iconPath))
        {
            return iconPath;
        }

        return null;
    }

    private static bool TryResolveIconElement(JsonElement iconElement, string extensionPath, out string? iconPath)
    {
        iconPath = null;
        if (iconElement.ValueKind == JsonValueKind.String)
        {
            iconPath = ResolveExtensionRelativePath(extensionPath, iconElement.GetString());
            return File.Exists(iconPath);
        }

        if (iconElement.ValueKind != JsonValueKind.Object) return false;

        var candidates = iconElement.EnumerateObject()
            .Select(prop => new
            {
                Size = int.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
                Path = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .OrderByDescending(item => item.Size);

        foreach (var candidate in candidates)
        {
            var resolved = ResolveExtensionRelativePath(extensionPath, candidate.Path);
            if (File.Exists(resolved))
            {
                iconPath = resolved;
                return true;
            }
        }

        return false;
    }

    private static string ResolveExtensionRelativePath(string extensionPath, string? relativePath)
    {
        var safe = (relativePath ?? string.Empty).TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(extensionPath, safe);
    }

    private async Task ClearBrowsingDataAsync()
    {
        foreach (var tab in _tabs)
        {
            var core = tab.WebView.CoreWebView2;
            core.CookieManager.DeleteAllCookies();
            await TryClearProfileDataAsync(core.Profile);
        }
    }

    private static async Task TryClearProfileDataAsync(object profile)
    {
        var methods = profile.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == "ClearBrowsingDataAsync")
            .OrderBy(method => method.GetParameters().Length)
            .ToList();

        foreach (var method in methods)
        {
            try
            {
                var parameters = method.GetParameters();
                object? result;
                if (parameters.Length == 0)
                {
                    result = method.Invoke(profile, []);
                }
                else if (parameters.Length == 1 && parameters[0].ParameterType.IsEnum)
                {
                    var allProfile = Enum.Parse(parameters[0].ParameterType, "AllProfile");
                    result = method.Invoke(profile, [allProfile]);
                }
                else
                {
                    continue;
                }

                await AwaitTask(result);
                return;
            }
            catch { }
        }
    }

    private void HandleDownloadStarting(CoreWebView2DownloadStartingEventArgs args)
    {
        var fileName = Path.GetFileName(args.ResultFilePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "download";
        }

        var downloads = string.IsNullOrWhiteSpace(_settings.DownloadDirectory)
            ? DefaultDownloadDirectory()
            : _settings.DownloadDirectory;

        try { Directory.CreateDirectory(downloads); }
        catch { downloads = DefaultDownloadDirectory(); Directory.CreateDirectory(downloads); }

        args.ResultFilePath = Path.Combine(downloads, fileName);
        args.Handled = true;

        var record = new DownloadRecord(fileName, args.ResultFilePath, args.DownloadOperation.Uri)
        {
            Operation = args.DownloadOperation
        };
        _downloads.Insert(0, record);
        RenderDownloads();
        ShowToast($"Downloading {fileName}");

        args.DownloadOperation.BytesReceivedChanged += (_, _) =>
        {
            record.BytesReceived = args.DownloadOperation.BytesReceived;
            record.TotalBytes = args.DownloadOperation.TotalBytesToReceive.HasValue
                ? Math.Min((long)args.DownloadOperation.TotalBytesToReceive.Value, long.MaxValue)
                : 0;
            Dispatcher.Invoke(RenderDownloads);
        };

        args.DownloadOperation.StateChanged += (_, _) =>
        {
            record.State = args.DownloadOperation.State.ToString();
            Dispatcher.Invoke(RenderDownloads);
            if (args.DownloadOperation.State == CoreWebView2DownloadState.Completed)
            {
                Dispatcher.Invoke(() =>
                {
                    ShowToast($"Downloaded {fileName}");
                    var extensionId = ExtractChromeExtensionId(args.DownloadOperation.Uri) ?? ExtractChromeExtensionId(_activeTab?.Url);
                    if (extensionId != null && string.Equals(Path.GetExtension(fileName), ".crx", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = InstallDownloadedCrxAsync(args.ResultFilePath, extensionId);
                    }
                });
            }
            else if (args.DownloadOperation.State == CoreWebView2DownloadState.Interrupted)
            {
                var reason = args.DownloadOperation.InterruptReason.ToString();
                Dispatcher.Invoke(() =>
                {
                    ShowToast($"{fileName}: {reason}");
                    var extensionId = ExtractChromeExtensionId(args.DownloadOperation.Uri) ?? ExtractChromeExtensionId(_activeTab?.Url);
                    if (extensionId != null)
                    {
                        _ = InstallChromeWebStoreExtensionAsync(extensionId);
                    }
                });
            }
        };
    }

    private void RenderDownloads()
    {
        DownloadsList.Children.Clear();

        if (_downloads.Count == 0)
        {
            DownloadsList.Children.Add(new TextBlock
            {
                Text = "No downloads yet.",
                Foreground = BrushFrom("#9BA9BC")
            });
            return;
        }

        foreach (var download in _downloads)
        {
            var row = new Border
            {
                Background = BrushFrom("#101A27"),
                BorderBrush = BrushFrom("#263446"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = download.FileName,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{download.State}  ·  {download.ProgressText}",
                Foreground = BrushFrom("#9BA9BC"),
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 6)
            });

            if (download.TotalBytes > 0)
            {
                var bar = new ProgressBar
                {
                    Height = 4,
                    Minimum = 0,
                    Maximum = download.TotalBytes,
                    Value = download.BytesReceived,
                    Background = BrushFrom("#1A2A40"),
                    Foreground = (Brush)FindResource("Accent"),
                    BorderThickness = new Thickness(0)
                };
                stack.Children.Add(bar);
            }

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var open = new Button
            {
                Style = (Style)FindResource("WideButton"),
                Height = 28,
                Margin = new Thickness(0, 0, 6, 0),
                Content = new TextBlock { Text = "Open" }
            };
            open.Click += (_, _) => TryOpenPath(download.Path);

            var folder = new Button
            {
                Style = (Style)FindResource("WideButton"),
                Height = 28,
                Margin = new Thickness(0, 0, 6, 0),
                Content = new TextBlock { Text = "Show in folder" }
            };
            folder.Click += (_, _) => TryShowInFolder(download.Path);

            var cancel = new Button
            {
                Style = (Style)FindResource("WideButton"),
                Height = 28,
                Content = new TextBlock { Text = "Cancel" }
            };
            cancel.Click += (_, _) =>
            {
                try { download.Operation?.Cancel(); } catch { }
                RenderDownloads();
            };

            var completed = string.Equals(download.State, "Completed", StringComparison.OrdinalIgnoreCase);
            open.IsEnabled = completed;
            folder.IsEnabled = File.Exists(download.Path) || Directory.Exists(Path.GetDirectoryName(download.Path) ?? "");
            cancel.IsEnabled = !completed && !string.Equals(download.State, "Interrupted", StringComparison.OrdinalIgnoreCase);

            actions.Children.Add(open);
            actions.Children.Add(folder);
            actions.Children.Add(cancel);
            stack.Children.Add(actions);

            row.Child = stack;
            DownloadsList.Children.Add(row);
        }
    }

    private static void TryOpenPath(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    private static void TryShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
                return;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start("explorer.exe", $"\"{dir}\"");
            }
        }
        catch { }
    }

    private void ShowToast(string message)
    {
        var toast = new Border
        {
            Background = BrushFrom("#132238"),
            BorderBrush = BrushFrom("#36516E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = 0,
            Child = new TextBlock
            {
                Text = message,
                Foreground = BrushFrom("#F8FAFC"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360
            }
        };

        ToastHost.Children.Insert(0, toast);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        _ = Task.Run(async () =>
        {
            await Task.Delay(3200);
            await Dispatcher.InvokeAsync(() =>
            {
                var fade = new DoubleAnimation(toast.Opacity, 0, TimeSpan.FromMilliseconds(180));
                fade.Completed += (_, _) => ToastHost.Children.Remove(toast);
                toast.BeginAnimation(OpacityProperty, fade);
            });
        });
    }

    private void RecordHistory(string? url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url) || UrlResolver.IsHome(url)) return;

        _history.RemoveAll(entry => string.Equals(entry.Url, url, StringComparison.OrdinalIgnoreCase));
        _history.Insert(0, new HistoryEntry
        {
            Url = url,
            Title = string.IsNullOrWhiteSpace(title) ? TitleFromUrl(url, false) : title,
            VisitedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });

        if (_history.Count > 5000)
        {
            _history.RemoveRange(5000, _history.Count - 5000);
        }

        ScheduleHistorySave();
        RenderHistoryPanel();
    }

    private void ScheduleHistorySave()
    {
        _historySaveTimer?.Stop();
        _historySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _historySaveTimer.Tick += (_, _) =>
        {
            _historySaveTimer?.Stop();
            HistoryStore.Save(_history);
        };
        _historySaveTimer.Start();
    }

    private void ScheduleSessionSave()
    {
        _sessionSaveTimer?.Stop();
        _sessionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _sessionSaveTimer.Tick += (_, _) =>
        {
            _sessionSaveTimer?.Stop();
            SaveSession();
        };
        _sessionSaveTimer.Start();
    }

    private void SaveSession()
    {
        try
        {
            _settings.LastSession = _tabs
                .Where(tab => !tab.IsPrivate)
                .Select(tab => new SessionTabRecord
                {
                    Url = UrlResolver.IsHome(tab.Url) ? UrlResolver.HomeUrl : tab.Url ?? string.Empty,
                    Title = tab.Title ?? string.Empty,
                    IsPinned = tab.IsPinned,
                    IsActive = tab == _activeTab
                })
                .ToList();
            SettingsStore.Save(_settings);
        }
        catch { }
    }

    private void RenderBookmarkPanel()
    {
        BookmarkPanelList.Children.Clear();
        var filtered = _settings.Bookmarks.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_currentBookmarkSearch))
        {
            var needle = _currentBookmarkSearch.Trim();
            filtered = filtered.Where(b =>
                (b.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (b.Url?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = filtered.ToList();
        if (list.Count == 0)
        {
            BookmarkPanelList.Children.Add(new TextBlock
            {
                Text = _settings.Bookmarks.Count == 0
                    ? "Nothing saved yet. Press Ctrl+D on any page to add it."
                    : "No bookmarks match your search.",
                Foreground = BrushFrom("#9BA9BC"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var bookmark in list)
        {
            BookmarkPanelList.Children.Add(CreatePanelEntry(
                bookmark.Name,
                bookmark.Url,
                "",
                () => NavigateActiveOrCreate(bookmark.Url),
                () =>
                {
                    _settings.Bookmarks.RemoveAll(b => string.Equals(b.Url, bookmark.Url, StringComparison.OrdinalIgnoreCase));
                    SettingsStore.Save(_settings);
                    RenderBookmarksBar();
                    RenderBookmarkPanel();
                    UpdateBookmarksBarVisibility();
                    UpdateStarIcon();
                },
                bookmark.Url));
        }
    }

    private void RenderHistoryPanel()
    {
        HistoryPanelList.Children.Clear();
        var filtered = _history.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_currentHistorySearch))
        {
            var needle = _currentHistorySearch.Trim();
            filtered = filtered.Where(h =>
                (h.Title?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.Url?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var grouped = filtered.GroupBy(entry => DateTimeOffset.FromUnixTimeSeconds(entry.VisitedAt).LocalDateTime.Date)
                              .OrderByDescending(group => group.Key)
                              .Take(20);

        var any = false;
        foreach (var group in grouped)
        {
            any = true;
            var label = FormatDateGroup(group.Key);
            HistoryPanelList.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = BrushFrom("#94A3B8"),
                FontSize = 11,
                Margin = new Thickness(2, 12, 0, 6),
                FontWeight = FontWeights.SemiBold
            });

            foreach (var entry in group.Take(40))
            {
                var time = DateTimeOffset.FromUnixTimeSeconds(entry.VisitedAt).LocalDateTime.ToString("HH:mm");
                HistoryPanelList.Children.Add(CreatePanelEntry(
                    string.IsNullOrWhiteSpace(entry.Title) ? entry.Url : entry.Title,
                    $"{time} · {entry.Url}",
                    "",
                    () => NavigateActiveOrCreate(entry.Url),
                    () =>
                    {
                        _history.RemoveAll(h => string.Equals(h.Url, entry.Url, StringComparison.OrdinalIgnoreCase) && h.VisitedAt == entry.VisitedAt);
                        ScheduleHistorySave();
                        RenderHistoryPanel();
                    },
                    entry.Url));
            }
        }

        if (!any)
        {
            HistoryPanelList.Children.Add(new TextBlock
            {
                Text = "No history yet.",
                Foreground = BrushFrom("#9BA9BC")
            });
        }
    }

    private static string FormatDateGroup(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today) return L10n.T("Today");
        if (date == today.AddDays(-1)) return L10n.T("Yesterday");
        var culture = L10n.CurrentLanguage == "tr"
            ? new System.Globalization.CultureInfo("tr-TR")
            : new System.Globalization.CultureInfo("en-US");
        if (date > today.AddDays(-7)) return date.ToString("dddd", culture);
        return date.ToString("d MMM yyyy", culture);
    }

    private Border CreatePanelEntry(string primary, string secondary, string iconGlyph, Action onClick, Action? onRemove = null, string? faviconUrl = null)
    {
        var row = new Border
        {
            Background = BrushFrom("#101A27"),
            BorderBrush = BrushFrom("#263446"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        UIElement icon;
        if (!string.IsNullOrWhiteSpace(faviconUrl))
        {
            icon = CreateFaviconImage(faviconUrl, 16);
            ((FrameworkElement)icon).Margin = new Thickness(0, 0, 10, 0);
        }
        else
        {
            icon = string.IsNullOrWhiteSpace(iconGlyph)
                ? new Border { Width = 16, Margin = new Thickness(0, 0, 10, 0) }
                : CreateSvgIcon(iconGlyph, 13, (Brush)FindResource("TextMuted"), new Thickness(0, 0, 10, 0));
        }
        Grid.SetColumn(icon, 0);

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(new TextBlock
        {
            Text = primary,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = secondary,
            Foreground = BrushFrom("#9BA9BC"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(text, 1);

        if (onRemove != null)
        {
            var remove = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 26,
                Height = 24,
                Tag = "remove",
                ToolTip = "Remove",
                Content = CreateSvgIcon("IconClose", 11)
            };
            remove.Click += (_, e) =>
            {
                e.Handled = true;
                onRemove();
            };
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);
        }

        grid.Children.Add(icon);
        grid.Children.Add(text);
        row.Child = grid;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src && IsInButtonNamedRemove(src)) return;
            onClick();
        };
        return row;
    }

    private static bool IsInButtonNamedRemove(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is Button btn && btn.Tag is string s && s == "remove") return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void NewTab(bool privateMode) => _ = CreateTabAsync(UrlResolver.HomeUrl, activate: true, privateMode: privateMode);
    private void CloseActiveTab() { if (_activeTab != null) CloseTab(_activeTab); }

    private void ReopenLastClosedTab()
    {
        if (_recentlyClosed.Count == 0) return;
        var record = _recentlyClosed.Pop();
        _ = CreateTabAsync(record.Url, activate: true, privateMode: false, isPinned: record.IsPinned);
    }

    private void FocusAddressBar()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void ToggleBookmarkForActiveTab()
    {
        if (_activeTab == null || UrlResolver.IsHome(_activeTab.Url)) return;
        var existing = _settings.Bookmarks.FirstOrDefault(b =>
            string.Equals(b.Url, _activeTab.Url, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            _settings.Bookmarks.Remove(existing);
            ShowToast("Bookmark removed");
        }
        else
        {
            _settings.Bookmarks.Insert(0, new Bookmark
            {
                Name = string.IsNullOrWhiteSpace(_activeTab.Title) ? TitleFromUrl(_activeTab.Url, false) : _activeTab.Title,
                Url = _activeTab.Url
            });
            ShowToast("Bookmark added");
        }

        SettingsStore.Save(_settings);
        RenderBookmarksBar();
        RenderBookmarkPanel();
        UpdateBookmarksBarVisibility();
        UpdateStarIcon();
        RefreshNewTabPages();
    }

    private void ShowFindBar()
    {
        if (_activeTab == null) return;
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void HideFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        ClearFindHighlights();
        _activeTab?.WebView.Focus();
    }

    private async void RunFind(bool forward)
    {
        if (_activeTab == null) return;
        var term = FindBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(term))
        {
            FindStatus.Text = string.Empty;
            return;
        }

        var script = "(function(q, fw){window.getSelection && window.getSelection().removeAllRanges && window.getSelection().removeAllRanges(); return window.find ? window.find(q, false, !fw, true, false, true, false) : false;})(" +
                     JsonSerializer.Serialize(term) + "," + (forward ? "true" : "false") + ");";
        try
        {
            var result = await _activeTab.WebView.CoreWebView2.ExecuteScriptAsync(script);
            FindStatus.Text = result == "true" ? "Match found" : "Not found";
        }
        catch
        {
            FindStatus.Text = "Search failed";
        }
    }

    private async void ClearFindHighlights()
    {
        if (_activeTab?.WebView.CoreWebView2 == null) return;
        try
        {
            await _activeTab.WebView.CoreWebView2.ExecuteScriptAsync("window.getSelection && window.getSelection().removeAllRanges && window.getSelection().removeAllRanges();");
        }
        catch { }
    }

    private void AdjustZoom(double delta)
    {
        if (_activeTab == null) return;
        var current = _activeTab.WebView.ZoomFactor;
        var next = Math.Clamp(current + delta, 0.25, 5.0);
        _activeTab.WebView.ZoomFactor = next;
        UpdateZoomIndicator();
    }

    private void ResetZoom()
    {
        if (_activeTab == null) return;
        _activeTab.WebView.ZoomFactor = 1.0;
        UpdateZoomIndicator();
    }

    private bool _wasMaximizedBeforeFullscreen;
    private bool _wasSidePanelOpen;
    private Rect _windowBoundsBeforeFullscreen;
    private WindowState _windowStateBeforeFullscreen;
    private ResizeMode _resizeModeBeforeFullscreen;
    private bool _topmostBeforeFullscreen;

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitContentFullscreen();
        else EnterContentFullscreen();
    }

    private const double SidebarWidth = 58;

    private void EnterContentFullscreen()
    {
        if (_isFullscreen) return;
        _isFullscreen = true;
        _wasMaximizedBeforeFullscreen = WindowState == WindowState.Maximized;
        _wasSidePanelOpen = SidePanel.Visibility == Visibility.Visible;
        _windowStateBeforeFullscreen = WindowState;
        _resizeModeBeforeFullscreen = ResizeMode;
        _topmostBeforeFullscreen = Topmost;
        _windowBoundsBeforeFullscreen = new Rect(Left, Top, Width, Height);

        TitleBarRow.Height = new GridLength(0);
        TabRow.Height = new GridLength(0);
        ToolbarRow.Height = new GridLength(0);
        BookmarksRow.Height = new GridLength(0);
        FindRow.Height = new GridLength(0);
        SidebarColumn.Width = new GridLength(0);

        if (_wasSidePanelOpen) ClosePanel();

        var bounds = GetCurrentMonitorBounds();
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        ApplyWindowChromeLayout();
        ScheduleLayoutRefresh();
        Activate();
    }

    private void ExitContentFullscreen()
    {
        if (!_isFullscreen) return;
        _isFullscreen = false;

        TitleBarRow.Height = GridLength.Auto;
        TabRow.Height = GridLength.Auto;
        ToolbarRow.Height = GridLength.Auto;
        BookmarksRow.Height = GridLength.Auto;
        FindRow.Height = GridLength.Auto;
        SidebarColumn.Width = new GridLength(SidebarWidth);

        if (_wasSidePanelOpen)
        {
            OpenPane(_currentPane);
        }

        Topmost = _topmostBeforeFullscreen;
        ResizeMode = _resizeModeBeforeFullscreen;

        if (_windowStateBeforeFullscreen == WindowState.Maximized || _wasMaximizedBeforeFullscreen)
        {
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            Left = _windowBoundsBeforeFullscreen.Left;
            Top = _windowBoundsBeforeFullscreen.Top;
            Width = _windowBoundsBeforeFullscreen.Width;
            Height = _windowBoundsBeforeFullscreen.Height;
        }

        ApplyWindowChromeLayout();
        ScheduleLayoutRefresh();
    }

    private Rect GetCurrentMonitorBounds()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var monitor = MonitorFromWindow(handle, 2);
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null)
                {
                    var transform = source.CompositionTarget.TransformFromDevice;
                    var topLeft = transform.Transform(new Point(work.Left, work.Top));
                    var bottomRight = transform.Transform(new Point(work.Right, work.Bottom));
                    return new Rect(topLeft, bottomRight);
                }

                return new Rect(
                    work.Left,
                    work.Top,
                    work.Right - work.Left,
                    work.Bottom - work.Top);
            }
        }
        catch { }

        return new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
    }

    private void CycleTab(int delta)
    {
        if (_tabs.Count == 0 || _activeTab == null) return;
        var index = _tabs.IndexOf(_activeTab);
        var next = (index + delta + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[next]);
    }

    private void SelectTabByIndex(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        ActivateTab(_tabs[index]);
    }

    private void GoBack() { if (_activeTab?.WebView.CoreWebView2?.CanGoBack == true) _activeTab.WebView.CoreWebView2.GoBack(); }
    private void GoForward() { if (_activeTab?.WebView.CoreWebView2?.CanGoForward == true) _activeTab.WebView.CoreWebView2.GoForward(); }

    private void ReloadActive()
    {
        if (_activeTab?.WebView.CoreWebView2 == null) return;
        if (_activeTab.IsLoading) _activeTab.WebView.CoreWebView2.Stop();
        else _activeTab.WebView.CoreWebView2.Reload();
    }

    private void PrintActive()
    {
        try
        {
            var method = _activeTab?.WebView.CoreWebView2.GetType().GetMethod("ShowPrintUI", []);
            method?.Invoke(_activeTab!.WebView.CoreWebView2, null);
        }
        catch { ShowToast("Print is not available on this runtime"); }
    }

    private void ToggleBookmarksBar()
    {
        _settings.ShowBookmarksBar = !_settings.ShowBookmarksBar;
        BookmarksBarCheck.IsChecked = _settings.ShowBookmarksBar;
        SettingsStore.Save(_settings);
        UpdateBookmarksBarVisibility();
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e) => NewTab(false);
    private void PrivateTabButton_Click(object sender, RoutedEventArgs e) => NewTab(true);
    private void BackButton_Click(object sender, RoutedEventArgs e) => GoBack();
    private void ForwardButton_Click(object sender, RoutedEventArgs e) => GoForward();
    private void ReloadButton_Click(object sender, RoutedEventArgs e) => ReloadActive();
    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab != null) NavigateTab(_activeTab, _settings.HomePage);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            AddressBox.Text = UrlResolver.Display(_activeTab?.Url);
            Keyboard.ClearFocus();
            CloseSuggestions();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || _activeTab == null) return;

        NavigateTab(_activeTab, AddressBox.Text);
        Keyboard.ClearFocus();
        CloseSuggestions();
        e.Handled = true;
    }

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressBox.SelectAll();
        UpdateSuggestions(AddressBox.Text);
    }

    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!SuggestionPopup.IsKeyboardFocusWithin) CloseSuggestions();
        }), DispatcherPriority.Background);

        // Restore display URL after focus loss
        if (_activeTab != null && UrlResolver.IsHome(_activeTab.Url))
        {
            AddressBox.Text = UrlResolver.Display(_activeTab.Url);
        }
    }

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (AddressBox.IsKeyboardFocusWithin)
        {
            UpdateSuggestions(AddressBox.Text);
        }
    }

    private void PasteAndGo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || _activeTab == null) return;
            NavigateTab(_activeTab, text);
        }
        catch { }
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        try { Clipboard.SetText(_activeTab.Url ?? string.Empty); ShowToast("URL copied"); }
        catch { }
    }

    private void UpdateSuggestions(string text)
    {
        SuggestionList.Children.Clear();
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2 || string.Equals(text, UrlResolver.HomeUrl, StringComparison.OrdinalIgnoreCase))
        {
            CloseSuggestions();
            return;
        }

        var needle = text.Trim();
        var matches = _history
            .Where(h => (h.Url?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (h.Title?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(5)
            .ToList();

        var bookmarkMatches = _settings.Bookmarks
            .Where(b => !matches.Any(m => string.Equals(m.Url, b.Url, StringComparison.OrdinalIgnoreCase)))
            .Where(b => (b.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (b.Url?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(3)
            .ToList();

        AppendSuggestion("", $"Search \"{needle}\" on {_settings.SearchEngine}", string.Empty, () =>
        {
            if (_activeTab != null) NavigateTab(_activeTab, needle);
        });

        foreach (var match in matches)
        {
            AppendSuggestion("", string.IsNullOrWhiteSpace(match.Title) ? match.Url : match.Title, match.Url, () =>
            {
                if (_activeTab != null) NavigateTab(_activeTab, match.Url);
            });
        }

        foreach (var bookmark in bookmarkMatches)
        {
            AppendSuggestion("", bookmark.Name, bookmark.Url, () =>
            {
                if (_activeTab != null) NavigateTab(_activeTab, bookmark.Url);
            });
        }

        if (SuggestionList.Children.Count == 0)
        {
            CloseSuggestions();
            return;
        }

        SuggestionPopup.Width = AddressChrome.ActualWidth;
        SuggestionPopup.IsOpen = true;
    }

    private void AppendSuggestion(string icon, string primary, string secondary, Action onClick)
    {
        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Padding = new Thickness(10, 7, 10, 7),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        UIElement iconElement = string.IsNullOrWhiteSpace(icon)
            ? new Border { Width = 0, Margin = new Thickness(0, 0, 10, 0) }
            : CreateSvgIcon(icon, 13, (Brush)FindResource("TextMuted"), new Thickness(0, 0, 10, 0));
        Grid.SetColumn(iconElement, 0);

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = primary,
            Foreground = (Brush)FindResource("TextMain"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrEmpty(secondary))
        {
            text.Children.Add(new TextBlock
            {
                Text = secondary,
                Foreground = (Brush)FindResource("TextMuted"),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        Grid.SetColumn(text, 1);

        grid.Children.Add(iconElement);
        grid.Children.Add(text);
        button.Content = grid;
        button.Click += (_, _) => { onClick(); CloseSuggestions(); };
        SuggestionList.Children.Add(button);
    }

    private void CloseSuggestions() => SuggestionPopup.IsOpen = false;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenPane("Search");
    private void ExtensionsButton_Click(object sender, RoutedEventArgs e) => OpenPane("Extensions");
    private void DevToolsButton_Click(object sender, RoutedEventArgs e) => _activeTab?.WebView.CoreWebView2?.OpenDevToolsWindow();
    private void ClosePanelButton_Click(object sender, RoutedEventArgs e) => ClosePanel();
    private void CloseExtensionAction_Click(object sender, RoutedEventArgs e) => CloseExtensionAction();

    private void CloseExtensionAction()
    {
        ExtensionActionCard.Visibility = Visibility.Collapsed;
        ExtensionPopupLoading.Visibility = Visibility.Collapsed;
        ExtensionFallbackPanel.Visibility = Visibility.Collapsed;
        ExtensionFallbackPanel.Children.Clear();
        SetExtensionPopupChromeMode(compact: false);
        if (_extensionPopupWebView != null)
        {
            _extensionPopupWebView.Visibility = Visibility.Collapsed;
        }

        _openExtensionPopupKey = null;
        _pendingExtensionPopupUrl = null;
        _extensionPopupTransform.X = 0;
        _extensionPopupTransform.Y = 0;
        UpdatePrivacyStatus();
    }
    private void DownloadsButton_Click(object sender, RoutedEventArgs e) => OpenPane("Downloads");
    private void BookmarksPanelButton_Click(object sender, RoutedEventArgs e) => OpenPane("Bookmarks");
    private void HistoryPanelButton_Click(object sender, RoutedEventArgs e) => OpenPane("History");
    private void StarButton_Click(object sender, RoutedEventArgs e) => ToggleBookmarkForActiveTab();
    private void ZoomIndicator_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ResetZoom();

    private void PrivacySettingChanged(object sender, RoutedEventArgs e)
    {
        if (_bindingSettings) return;
        SavePrivacySettingsFromControls();
    }

    private void AppearanceSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_bindingSettings) return;
        _settings.ForceDarkPages = ForceDarkPagesCheck.IsChecked == true;
        _settings.ShowBookmarksBar = BookmarksBarCheck.IsChecked == true;
        _settings.RestoreSessionOnStart = RestoreSessionCheck.IsChecked == true;
        _settings.SoftUiEffects = SoftEffectsCheck.IsChecked == true;
        _settings.PlayBackgroundMusic = BackgroundMusicEnabledCheck.IsChecked == true;
        _settings.AskWhereToSaveDownloads = AskWhereDownloadCheck.IsChecked == true;
        SettingsStore.Save(_settings);
        ApplySoftEffects();
        UpdateBookmarksBarVisibility();
        RefreshNewTabPages();
    }

    private void AccentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string color) return;
        _settings.AccentColor = color;
        SettingsStore.Save(_settings);
        ApplyAccent(color);
        RefreshNewTabPages();
        ShowToast("Accent updated");
    }

    private void CustomAccentButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AccentColorDialog(this, _settings.AccentColor);
        if (dialog.ShowDialog() != true) return;

        _settings.AccentColor = dialog.SelectedColor;
        SettingsStore.Save(_settings);
        ApplyAccent(_settings.AccentColor);
        RefreshNewTabPages();
        ShowToast("Accent updated");
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingSettings || ThemeBox.SelectedItem is not ComboBoxItem item) return;
        _settings.ThemeName = item.Content?.ToString() ?? "Midnight";
        SettingsStore.Save(_settings);
        ApplyTheme(_settings.ThemeName);
        ApplyAccent(_settings.AccentColor);
        RefreshNewTabPages();
    }

    private void UiFontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingSettings || UiFontBox.SelectedItem is not ComboBoxItem item) return;
        var key = item.Tag?.ToString() ?? UiFontCatalog.All[0].Key;
        _settings.UiFontKey = key;
        SettingsStore.Save(_settings);
        ApplyUiFont(key);
        RenderTabs();
        RenderSidebar();
        RenderBookmarksBar();
        RenderBookmarkPanel();
        RenderHistoryPanel();
        RenderDownloads();
        RenderExtensions();
        RefreshNewTabPages();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingSettings || LanguageBox.SelectedItem is not ComboBoxItem item) return;
        var lang = item.Tag?.ToString() ?? "en";
        _settings.Language = lang;
        L10n.CurrentLanguage = lang;
        SettingsStore.Save(_settings);
        ApplyLanguage();
        RefreshNewTabPages();
        UpdatePrivacyStatus();
        UpdateWindowTitle();
    }

    private void ApplyLanguage()
    {
        // Panel title and pane name
        PanelTitle.Text = PrettyName(_currentPane);

        // Side panel labels
        LblTheme.Text = L10n.T("Theme");
        LblUiFont.Text = L10n.T("Interface font");
        LblLanguage.Text = L10n.T("Language");
        LblAccent.Text = L10n.T("Accent");
        LblCustomAccent.Text = L10n.T("Custom color");
        LblBackgroundMedia.Text = L10n.T("New tab background");
        LblBackgroundMusic.Text = L10n.T("New tab music");
        LblUpdates.Text = L10n.T("Updates");
        CheckUpdatesButton.Content = L10n.T("Check for updates");
        UpdateVersionLabel();

        // Privacy
        BlockTrackersCheck.Content = L10n.T("Tracker & ad blocker");
        DoNotTrackCheck.Content = L10n.T("Do Not Track + Sec-GPC headers");
        HardenPermissionsCheck.Content = L10n.T("Auto-deny camera, mic, geolocation");
        HttpsFirstCheck.Content = L10n.T("HTTPS first");

        // Appearance
        ForceDarkPagesCheck.Content = L10n.T("Force dark websites");
        BookmarksBarCheck.Content = L10n.T("Show bookmarks bar");
        RestoreSessionCheck.Content = L10n.T("Restore tabs on startup");
        SoftEffectsCheck.Content = L10n.T("Soft glass effects");
        BackgroundMusicEnabledCheck.Content = L10n.T("Play music on new tab");

        // Search/Home
        AskWhereDownloadCheck.Content = L10n.T("Always ask where to save");

        // Tooltips - nav pills
        NavPrivacy.ToolTip = L10n.T("Privacy");
        NavAppearance.ToolTip = L10n.T("Appearance");
        NavSearch.ToolTip = L10n.T("Search & Home");
        NavExtensions.ToolTip = L10n.T("Extensions");
        NavBookmarks.ToolTip = L10n.T("Bookmarks");
        NavHistory.ToolTip = L10n.T("History");
        NavDownloads.ToolTip = L10n.T("Downloads");

        // Tooltips - top bar
        BackButton.ToolTip = $"{L10n.T("Back")} (Alt+Left)";
        ForwardButton.ToolTip = $"{L10n.T("Forward")} (Alt+Right)";
        ReloadButton.ToolTip = $"{L10n.T("Reload")} (F5)";
        HomeButton.ToolTip = L10n.T("Home");
        StarButton.ToolTip = $"{L10n.T("Bookmark this page")} (Ctrl+D)";
        PrivateTabButton.ToolTip = $"{L10n.T("New private tab")} (Ctrl+Shift+N)";
        ExtensionsButton.ToolTip = L10n.T("Extensions");
        DevToolsButton.ToolTip = $"{L10n.T("Developer tools")} (F12)";
        SettingsButton.ToolTip = L10n.T("Settings");
        NewTabButton.ToolTip = $"{L10n.T("New tab")} (Ctrl+T)";
        MenuButton.ToolTip = L10n.T("Menu");
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? L10n.T("Restore") : L10n.T("Maximize");
        AddressBox.ToolTip = L10n.T("Search or enter address");

        // Shield indicator
        UpdatePrivacyStatus();

        // Refresh dynamic panels
        RenderBookmarkPanel();
        RenderHistoryPanel();
        RenderDownloads();
        RenderExtensions();
    }

    private void SearchEngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingSettings || SearchEngineBox.SelectedItem is not ComboBoxItem item) return;
        _settings.SearchEngine = item.Content?.ToString() ?? "DuckDuckGo";
        SettingsStore.Save(_settings);
        RefreshNewTabPages();
    }

    private void HomePageBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_bindingSettings) return;
        var value = HomePageBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value)) value = UrlResolver.HomeUrl;
        _settings.HomePage = value;
        SettingsStore.Save(_settings);
    }

    private void ChooseDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose download folder",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _settings.DownloadDirectory = dialog.FolderName;
            DownloadFolderBox.Text = dialog.FolderName;
            SettingsStore.Save(_settings);
            ShowToast("Download folder updated");
        }
    }

    private void ChooseBackgroundMedia_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose new tab background",
            Filter = "Media files|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.mp4;*.webm;*.mov|Images|*.png;*.jpg;*.jpeg;*.webp;*.gif|Videos|*.mp4;*.webm;*.mov|All files|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;

        _settings.BackgroundMediaPath = dialog.FileName;
        BackgroundMediaBox.Text = dialog.FileName;
        SettingsStore.Save(_settings);
        RefreshNewTabPages();
        ShowToast("Background updated");
    }

    private void ClearBackgroundMedia_Click(object sender, RoutedEventArgs e)
    {
        _settings.BackgroundMediaPath = string.Empty;
        BackgroundMediaBox.Text = "No custom background";
        SettingsStore.Save(_settings);
        RefreshNewTabPages();
        ShowToast("Background cleared");
    }

    private void BackgroundOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_bindingSettings) return;
        _settings.BackgroundMediaOpacity = Math.Clamp(BackgroundOpacitySlider.Value / 100.0, 0.15, 1.0);
        SettingsStore.Save(_settings);
        UpdatePersonalizationLabels();
        RefreshNewTabPages();
    }

    private void BackgroundBlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_bindingSettings) return;
        _settings.BackgroundMediaBlur = Math.Clamp(BackgroundBlurSlider.Value, 0.0, 24.0);
        SettingsStore.Save(_settings);
        UpdatePersonalizationLabels();
        RefreshNewTabPages();
    }

    private void ChooseBackgroundMusic_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose new tab music",
            Filter = "Audio files|*.mp3;*.wav;*.ogg;*.m4a;*.flac|All files|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;

        _settings.BackgroundMusicPath = dialog.FileName;
        _settings.PlayBackgroundMusic = true;
        BackgroundMusicEnabledCheck.IsChecked = true;
        BackgroundMusicBox.Text = dialog.FileName;
        SettingsStore.Save(_settings);
        RefreshNewTabPages();
        ShowToast("Background music updated");
    }

    private void ClearBackgroundMusic_Click(object sender, RoutedEventArgs e)
    {
        _settings.BackgroundMusicPath = string.Empty;
        _settings.PlayBackgroundMusic = false;
        BackgroundMusicEnabledCheck.IsChecked = false;
        BackgroundMusicBox.Text = "No background music";
        SettingsStore.Save(_settings);
        RefreshNewTabPages();
        ShowToast("Background music cleared");
    }

    private void MusicVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_bindingSettings) return;
        _settings.BackgroundMusicVolume = Math.Clamp(MusicVolumeSlider.Value / 100.0, 0.02, 1.0);
        SettingsStore.Save(_settings);
        UpdatePersonalizationLabels();
        RefreshNewTabPages();
    }

    private void UpdatePersonalizationLabels()
    {
        if (BackgroundOpacityLabel != null)
        {
            BackgroundOpacityLabel.Text = $"{Math.Round(Math.Clamp(_settings.BackgroundMediaOpacity, 0.15, 1.0) * 100):0}%";
        }

        if (BackgroundBlurLabel != null)
        {
            BackgroundBlurLabel.Text = $"{Math.Round(Math.Clamp(_settings.BackgroundMediaBlur, 0.0, 24.0)):0}px";
        }

        if (MusicVolumeLabel != null)
        {
            MusicVolumeLabel.Text = $"{Math.Round(Math.Clamp(_settings.BackgroundMusicVolume, 0.02, 1.0) * 100):0}%";
        }
    }

    private void BookmarkSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _currentBookmarkSearch = BookmarkSearchBox.Text ?? string.Empty;
        RenderBookmarkPanel();
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _currentHistorySearch = HistorySearchBox.Text ?? string.Empty;
        RenderHistoryPanel();
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        HistoryStore.Clear();
        RenderHistoryPanel();
        ShowToast("History cleared");
    }

    private void TitleBarDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ChromeDrag_MouseLeftButtonDown(sender, e);
    }

    private void ChromeDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideInteractive(e.OriginalSource)) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }
        try { DragMove(); } catch { }
    }

    private static bool IsInsideInteractive(object? source)
    {
        var current = source as DependencyObject;
        while (current != null)
        {
            if (current is Button or TextBox or ComboBox or ToggleButton or MenuItem or RadioButton)
            {
                return true;
            }
            if (current is FrameworkElement { Tag: BrowserTab })
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximized();
    private void WindowCloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximized() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MenuButton_Click(object sender, RoutedEventArgs e) => MenuPopup.IsOpen = !MenuPopup.IsOpen;

    private void MenuNewTab_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; NewTab(false); }
    private void MenuPrivateTab_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; NewTab(true); }
    private void MenuFind_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; ShowFindBar(); }
    private void MenuPrint_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; PrintActive(); }
    private void MenuFullscreen_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; ToggleFullscreen(); }
    private void MenuZoomIn_Click(object sender, RoutedEventArgs e) => AdjustZoom(0.1);
    private void MenuZoomOut_Click(object sender, RoutedEventArgs e) => AdjustZoom(-0.1);
    private void MenuBookmarks_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; OpenPane("Bookmarks"); }
    private void MenuHistory_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; OpenPane("History"); }
    private void MenuDownloads_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; OpenPane("Downloads"); }
    private void MenuSettings_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; OpenPane("Search"); }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideFindBar(); e.Handled = true; return; }
        if (e.Key == Key.Enter)
        {
            RunFind(!Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift));
            e.Handled = true;
        }
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(FindBox.Text))
        {
            FindStatus.Text = string.Empty;
            ClearFindHighlights();
            return;
        }
        RunFind(true);
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => RunFind(true);
    private void FindPrev_Click(object sender, RoutedEventArgs e) => RunFind(false);
    private void FindClose_Click(object sender, RoutedEventArgs e) => HideFindBar();

    private async void ClearDataButton_Click(object sender, RoutedEventArgs e)
    {
        await ClearBrowsingDataAsync();
        ShowToast("Browsing data cleared");
    }

    private async void AddExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose an unpacked Chromium extension folder",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        if (!File.Exists(Path.Combine(dialog.FolderName, "manifest.json")))
        {
            ShowToast("manifest.json was not found");
            return;
        }

        var result = await TryAddExtensionAsync(_activeTab, dialog.FolderName, silent: false);
        if (string.Equals(result, "Loaded", StringComparison.OrdinalIgnoreCase))
        {
            if (!_settings.ExtensionPaths.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
            {
                _settings.ExtensionPaths.Add(dialog.FolderName);
                SettingsStore.Save(_settings);
            }
        }
        else
        {
            ShowToast(result);
        }

        RenderExtensions();
        RenderExtensionToolbar();
    }

    private async void InstallChromeStoreButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallChromeWebStoreExtensionFromActiveTabAsync();
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _handler;
        public RelayCommand(Action<object?> handler) { _handler = handler; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _handler(parameter);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    private sealed class AccentColorDialog : Window
    {
        private readonly Border _preview;
        private readonly TextBox _hexBox;
        private readonly Slider _red;
        private readonly Slider _green;
        private readonly Slider _blue;
        private bool _updating;

        public string SelectedColor { get; private set; }

        public AccentColorDialog(Window owner, string initialColor)
        {
            Owner = owner;
            Title = "Custom color";
            Width = 360;
            Height = 390;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            Background = BrushFrom("#0A121D");

            var initial = ParseColor(initialColor, "#38BDF8");
            SelectedColor = ToHex(initial);

            var shell = new Border
            {
                BorderBrush = BrushFrom("#263447"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18),
                Background = BrushFrom("#0A121D")
            };

            var stack = new StackPanel();
            shell.Child = stack;

            stack.Children.Add(new TextBlock
            {
                Text = "Custom color",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 14)
            });

            _preview = new Border
            {
                Height = 58,
                CornerRadius = new CornerRadius(10),
                BorderBrush = BrushFrom("#263447"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 14)
            };
            stack.Children.Add(_preview);

            _red = SliderRow(stack, "Red");
            _green = SliderRow(stack, "Green");
            _blue = SliderRow(stack, "Blue");

            _hexBox = new TextBox
            {
                Height = 34,
                Text = SelectedColor,
                Margin = new Thickness(0, 8, 0, 12),
                Background = BrushFrom("#0B1522"),
                Foreground = BrushFrom("#F8FAFC"),
                BorderBrush = BrushFrom("#263447")
            };
            _hexBox.LostFocus += (_, _) => TryApplyHex();
            _hexBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    TryApplyHex();
                    e.Handled = true;
                }
            };
            stack.Children.Add(_hexBox);

            var swatches = new UniformGrid { Columns = 6, Rows = 1, Margin = new Thickness(0, 0, 0, 16) };
            foreach (var color in new[] { "#38BDF8", "#22C55E", "#A78BFA", "#F97316", "#F43F5E", "#FBBF24" })
            {
                var button = new Button
                {
                    Width = 36,
                    Height = 32,
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = BrushFrom(color),
                    BorderBrush = BrushFrom("#263447"),
                    BorderThickness = new Thickness(1),
                    Tag = color
                };
                button.Click += (_, _) => SetColor(ParseColor(color, "#38BDF8"));
                swatches.Children.Add(button);
            }
            stack.Children.Add(swatches);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var cancel = DialogButton("Cancel", "#111D2B");
            cancel.Click += (_, _) => DialogResult = false;

            var apply = DialogButton("Apply", "#38BDF8");
            apply.Foreground = BrushFrom("#04111C");
            apply.Margin = new Thickness(8, 0, 0, 0);
            apply.Click += (_, _) =>
            {
                TryApplyHex();
                DialogResult = true;
            };

            buttons.Children.Add(cancel);
            buttons.Children.Add(apply);
            stack.Children.Add(buttons);

            Content = shell;
            SetColor(initial);
        }

        private Slider SliderRow(Panel parent, string label)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, Foreground = BrushFrom("#94A3B8") });

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 255,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (Owner is FrameworkElement ownerElement &&
                ownerElement.TryFindResource("DarkSlider") is Style sliderStyle)
            {
                slider.Style = sliderStyle;
            }
            slider.ValueChanged += (_, _) => UpdateFromSliders();
            Grid.SetColumn(slider, 1);
            row.Children.Add(slider);

            parent.Children.Add(row);
            return slider;
        }

        private void SetColor(Color color)
        {
            _updating = true;
            _red.Value = color.R;
            _green.Value = color.G;
            _blue.Value = color.B;
            SelectedColor = ToHex(color);
            _hexBox.Text = SelectedColor;
            _preview.Background = new SolidColorBrush(color);
            _updating = false;
        }

        private void UpdateFromSliders()
        {
            if (_updating) return;
            var color = Color.FromRgb((byte)_red.Value, (byte)_green.Value, (byte)_blue.Value);
            SelectedColor = ToHex(color);
            _hexBox.Text = SelectedColor;
            _preview.Background = new SolidColorBrush(color);
        }

        private void TryApplyHex()
        {
            try
            {
                SetColor(ParseColor(_hexBox.Text, SelectedColor));
            }
            catch
            {
                _hexBox.Text = SelectedColor;
            }
        }

        private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private static Button DialogButton(string text, string background)
        {
            return new Button
            {
                Content = text,
                MinWidth = 82,
                Height = 36,
                Padding = new Thickness(12, 0, 12, 0),
                Background = BrushFrom(background),
                Foreground = BrushFrom("#F8FAFC"),
                BorderBrush = BrushFrom("#263447"),
                BorderThickness = new Thickness(1)
            };
        }
    }

    private sealed class QuickLinkDialog : Window
    {
        private readonly TextBox _nameBox;
        private readonly TextBox _urlBox;

        public string SiteName => _nameBox.Text;
        public string SiteUrl => _urlBox.Text;

        public QuickLinkDialog(Window owner) : this(owner, string.Empty, string.Empty, "Add Speed Dial Site") { }

        public QuickLinkDialog(Window owner, string initialName, string initialUrl, string title)
        {
            Owner = owner;
            Title = title;
            Width = 420;
            Height = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            Background = BrushFrom("#0A121D");

            var shell = new Border
            {
                BorderBrush = BrushFrom("#263447"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18),
                Background = BrushFrom("#0A121D")
            };

            var stack = new StackPanel();
            shell.Child = stack;

            stack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = BrushFrom("#F8FAFC"),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 16)
            });

            stack.Children.Add(new TextBlock
            {
                Text = "Name",
                Foreground = BrushFrom("#94A3B8"),
                Margin = new Thickness(0, 0, 0, 5)
            });

            _nameBox = new TextBox
            {
                Height = 36,
                Text = initialName,
                Margin = new Thickness(0, 0, 0, 12),
                Background = BrushFrom("#0B1522"),
                Foreground = BrushFrom("#F8FAFC"),
                BorderBrush = BrushFrom("#263447")
            };
            stack.Children.Add(_nameBox);

            stack.Children.Add(new TextBlock
            {
                Text = "Address",
                Foreground = BrushFrom("#94A3B8"),
                Margin = new Thickness(0, 0, 0, 5)
            });

            _urlBox = new TextBox
            {
                Height = 36,
                Text = initialUrl,
                Margin = new Thickness(0, 0, 0, 18),
                Background = BrushFrom("#0B1522"),
                Foreground = BrushFrom("#F8FAFC"),
                BorderBrush = BrushFrom("#263447")
            };
            _urlBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) DialogResult = true; };
            stack.Children.Add(_urlBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var cancel = DialogButton("Cancel", "#111D2B");
            cancel.Click += (_, _) => DialogResult = false;

            var add = DialogButton("Save", "#38BDF8");
            add.Foreground = BrushFrom("#04111C");
            add.Margin = new Thickness(8, 0, 0, 0);
            add.Click += (_, _) => DialogResult = true;

            buttons.Children.Add(cancel);
            buttons.Children.Add(add);
            stack.Children.Add(buttons);

            Content = shell;
        }

        private static Button DialogButton(string text, string background)
        {
            return new Button
            {
                Content = text,
                MinWidth = 82,
                Height = 36,
                Padding = new Thickness(12, 0, 12, 0),
                Background = BrushFrom(background),
                Foreground = BrushFrom("#F8FAFC"),
                BorderBrush = BrushFrom("#263447"),
                BorderThickness = new Thickness(1)
            };
        }
    }

    private sealed record ExtensionManifestInfo(
        string Path,
        string? Id,
        string Name,
        string? IconPath,
        string? PopupPath,
        string? OptionsPath);

    private sealed class BrowserTab(WebView2 webView, bool isPrivate, string? privateProfilePath)
    {
        public WebView2 WebView { get; } = webView;
        public bool IsPrivate { get; } = isPrivate;
        public string? PrivateProfilePath { get; } = privateProfilePath;
        public string Title { get; set; } = "New Tab";
        public string Url { get; set; } = UrlResolver.HomeUrl;
        public bool IsLoading { get; set; }
        public bool IsInternalNewTab { get; set; } = true;
        public bool IsPinned { get; set; }
        public bool IsNew { get; set; }
    }

    private sealed class DownloadRecord(string fileName, string path, string uri)
    {
        public string FileName { get; } = fileName;
        public string Path { get; } = path;
        public string Uri { get; } = uri;
        public string State { get; set; } = "Starting";
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public CoreWebView2DownloadOperation? Operation { get; set; }

        public string ProgressText
        {
            get
            {
                if (TotalBytes <= 0) return FormatBytes(BytesReceived);
                var percent = Math.Clamp(BytesReceived / (double)TotalBytes, 0, 1) * 100;
                return $"{percent:0}% of {FormatBytes(TotalBytes)}";
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB"];
            var value = (double)Math.Max(0, bytes);
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }
    }

    private sealed record ClosedTabRecord(string Url, string Title, bool IsPinned);
}
