using System.Windows;

namespace StellaOrion.Services;

public static class UiFontCatalog
{
    public sealed record FontPreset(string Key, string DisplayName, string FontFamily, FontWeight? Weight = null);

    public static FontWeight ResolveWeight(FontPreset preset) => preset.Weight ?? FontWeights.Normal;

    public static readonly FontPreset[] All =
    [
        new("SegoeUiVariable", "Segoe UI Variable (Soft)", "Segoe UI Variable Text, Segoe UI Variable Display, Segoe UI"),
        new("SegoeUi", "Segoe UI", "Segoe UI, Segoe UI Variable Text"),
        new("SegoeUiSemibold", "Segoe UI Semibold", "Segoe UI, Segoe UI Variable Text", FontWeight.FromOpenTypeWeight(600)),
        new("SegoeUiBold", "Segoe UI Bold", "Segoe UI, Segoe UI Variable Text", FontWeight.FromOpenTypeWeight(700)),
        new("Bahnschrift", "Bahnschrift", "Bahnschrift, Segoe UI"),
        new("BahnschriftSemiBold", "Bahnschrift SemiBold", "Bahnschrift SemiBold, Bahnschrift, Segoe UI", FontWeight.FromOpenTypeWeight(600)),
        new("Corbel", "Corbel (Rounded Sans)", "Corbel, Segoe UI"),
        new("Candara", "Candara", "Candara, Segoe UI"),
        new("Calibri", "Calibri", "Calibri, Segoe UI"),
        new("Franklin", "Franklin Gothic", "Franklin Gothic Medium, Segoe UI"),
        new("Arial", "Arial", "Arial, Segoe UI"),
        new("ArialRounded", "Arial Rounded MT", "Arial Rounded MT Bold, Arial, Segoe UI", FontWeight.FromOpenTypeWeight(600)),
        new("Trebuchet", "Trebuchet MS", "Trebuchet MS, Segoe UI"),
        new("Verdana", "Verdana", "Verdana, Segoe UI"),
        new("Tahoma", "Tahoma", "Tahoma, Segoe UI"),
        new("YuGothic", "Yu Gothic UI", "Yu Gothic UI, Segoe UI")
    ];

    public static FontPreset FromKey(string? key)
    {
        return All.FirstOrDefault(font => string.Equals(font.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }
}
