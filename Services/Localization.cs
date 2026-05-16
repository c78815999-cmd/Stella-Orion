using System.Globalization;

namespace StellaOrion.Services;

public static class Localization
{
    public static string CurrentLanguage { get; set; } = "system";

    public static string EffectiveLanguage
    {
        get
        {
            if (!string.Equals(CurrentLanguage, "system", StringComparison.OrdinalIgnoreCase))
            {
                return CurrentLanguage;
            }

            var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            return Languages.ContainsKey(lang) ? lang : "en";
        }
    }

    public static readonly Dictionary<string, string> Languages = new()
    {
        ["system"] = "System default",
        ["en"] = "English",
        ["tr"] = "Türkçe",
        ["de"] = "Deutsch",
        ["es"] = "Español",
        ["fr"] = "Français",
        ["it"] = "Italiano",
        ["pt"] = "Português",
        ["ru"] = "Русский",
        ["ar"] = "العربية",
        ["ja"] = "日本語",
        ["ko"] = "한국어",
        ["zh"] = "中文"
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Table = new()
    {
        ["tr"] = new()
        {
            // Panel categories
            ["Privacy"] = "Gizlilik",
            ["Appearance"] = "Görünüm",
            ["Search"] = "Arama",
            ["Search & Home"] = "Arama ve Ana Sayfa",
            ["Extensions"] = "Eklentiler",
            ["Bookmarks"] = "Yer İmleri",
            ["History"] = "Geçmiş",
            ["Downloads"] = "İndirilenler",
            ["Control Center"] = "Kontrol Merkezi",

            // Privacy
            ["Tracker & ad blocker"] = "Reklam ve takipçi engelleyici",
            ["Do Not Track + Sec-GPC headers"] = "Beni İzleme + Sec-GPC başlıkları",
            ["Auto-deny camera, mic, geolocation"] = "Kamera, mikrofon, konum reddet",
            ["HTTPS first"] = "Önce HTTPS",
            ["Clear Browsing Data"] = "Tarama verilerini temizle",
            ["Clear History"] = "Geçmişi temizle",

            // Appearance
            ["Theme"] = "Tema",
            ["Interface font"] = "Arayüz yazı tipi",
            ["Force dark websites"] = "Siteleri zorla koyu yap",
            ["Show bookmarks bar"] = "Yer imi çubuğunu göster",
            ["Restore tabs on startup"] = "Açılışta sekmeleri geri yükle",
            ["Accent"] = "Vurgu",
            ["Language"] = "Dil",
            ["System default"] = "Sistem varsayılanı",
            ["Custom color"] = "Özel renk",
            ["New tab background"] = "Yeni sekme arkaplanı",

            // Search
            ["Default search engine"] = "Varsayılan arama motoru",
            ["Home page"] = "Ana sayfa",
            ["Download folder"] = "İndirme klasörü",
            ["Always ask where to save"] = "Kayıt yerini her zaman sor",

            // Extensions
            ["Add Unpacked Extension"] = "Paketsiz eklenti ekle",
            ["No extensions added yet."] = "Henüz eklenti eklenmedi.",
            ["Remove saved path"] = "Kayıtlı yolu kaldır",

            // Bookmarks
            ["Nothing saved yet. Press Ctrl+D on any page to add it."] = "Henüz kayıtlı yok. Eklemek için Ctrl+D'ye basın.",
            ["No bookmarks match your search."] = "Aramayla eşleşen yer imi yok.",

            // History
            ["No history yet."] = "Henüz geçmiş yok.",
            ["Today"] = "Bugün",
            ["Yesterday"] = "Dün",

            // Downloads
            ["No downloads yet."] = "Henüz indirme yok.",
            ["Open"] = "Aç",
            ["Show in folder"] = "Klasörde göster",
            ["Cancel"] = "İptal",

            // Status / toasts
            ["Shield On"] = "Kalkan açık",
            ["Shield Off"] = "Kalkan kapalı",
            ["Close extension panel"] = "Eklenti panelini kapat",
            ["Updates"] = "Güncellemeler",
            ["Check for updates"] = "Güncellemeleri kontrol et",
            ["Update available"] = "Güncelleme var",
            ["Open release page?"] = "Sürüm sayfasını açmak ister misin?",
            ["You are on the latest version"] = "En güncel sürümü kullanıyorsun",
            ["Could not check for updates"] = "Güncelleme kontrol edilemedi",
            ["Update source is not configured"] = "Güncelleme kaynağı ayarlanmadı",
            ["URL copied"] = "Bağlantı kopyalandı",
            ["Bookmark added"] = "Yer imi eklendi",
            ["Bookmark removed"] = "Yer imi kaldırıldı",
            ["Speed dial site added"] = "Hızlı erişim sitesi eklendi",
            ["Speed dial site removed"] = "Hızlı erişim sitesi kaldırıldı",
            ["Site already exists"] = "Site zaten var",
            ["Enter a valid site address"] = "Geçerli bir site adresi gir",
            ["Browsing data cleared"] = "Tarama verileri temizlendi",
            ["History cleared"] = "Geçmiş temizlendi",
            ["Download folder updated"] = "İndirme klasörü güncellendi",
            ["Accent updated"] = "Vurgu rengi güncellendi",
            ["Extension loaded"] = "Eklenti yüklendi",
            ["Extension path removed"] = "Eklenti yolu kaldırıldı",
            ["manifest.json was not found"] = "manifest.json bulunamadı",
            ["Match found"] = "Eşleşme bulundu",
            ["Not found"] = "Bulunamadı",
            ["Search failed"] = "Arama başarısız",
            ["Print is not available on this runtime"] = "Bu sürümde yazdırma yok",
            ["Some changes require a restart"] = "Bazı değişiklikler yeniden başlatma gerektirir",

            // Tab titles
            ["New Tab"] = "Yeni Sekme",
            ["Private Tab"] = "Gizli Sekme",

            // Tooltips
            ["New tab"] = "Yeni sekme",
            ["New private tab"] = "Yeni gizli sekme",
            ["Back"] = "Geri",
            ["Forward"] = "İleri",
            ["Reload"] = "Yenile",
            ["Home"] = "Ana sayfa",
            ["Bookmarks"] = "Yer imleri",
            ["History"] = "Geçmiş",
            ["Downloads"] = "İndirilenler",
            ["Settings"] = "Ayarlar",
            ["Menu"] = "Menü",
            ["Minimize"] = "Küçült",
            ["Maximize"] = "Büyüt",
            ["Restore"] = "Geri yükle",
            ["Close"] = "Kapat",
            ["Find on page"] = "Sayfada bul",
            ["Print"] = "Yazdır",
            ["Toggle fullscreen"] = "Tam ekran",
            ["Developer tools"] = "Geliştirici araçları",
            ["Bookmark this page"] = "Bu sayfayı yer imine ekle",
            ["Choose folder"] = "Klasör seç",
            ["Previous"] = "Önceki",
            ["Next"] = "Sonraki",
            ["Search or enter address"] = "Adres yaz veya ara",

            // Address bar context
            ["Cut"] = "Kes",
            ["Copy"] = "Kopyala",
            ["Paste"] = "Yapıştır",
            ["Paste and go"] = "Yapıştır ve git",
            ["Copy URL"] = "Bağlantıyı kopyala",
            ["Select all"] = "Tümünü seç",

            // Tab context
            ["New tab to the right"] = "Sağa yeni sekme",
            ["Duplicate"] = "Çoğalt",
            ["Pin tab"] = "Sekmeyi sabitle",
            ["Unpin tab"] = "Sabitlemeyi kaldır",
            ["Close tab"] = "Sekmeyi kapat",
            ["Close other tabs"] = "Diğer sekmeleri kapat",
            ["Close tabs to the right"] = "Sağdakileri kapat",
            ["Open in new tab"] = "Yeni sekmede aç",
            ["Open in new private tab"] = "Yeni gizli sekmede aç",
            ["Edit"] = "Düzenle",
            ["Remove"] = "Kaldır"
        }
    };

    public static string T(string key)
    {
        if (!Table.TryGetValue(EffectiveLanguage, out var dict)) return key;
        return dict.TryGetValue(key, out var translated) ? translated : key;
    }

    public static string T(string key, string language)
    {
        if (string.Equals(language, "system", StringComparison.OrdinalIgnoreCase))
        {
            language = EffectiveLanguage;
        }

        if (!Table.TryGetValue(language, out var dict)) return key;
        return dict.TryGetValue(key, out var translated) ? translated : key;
    }
}
