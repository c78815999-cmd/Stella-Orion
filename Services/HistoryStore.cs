using System.IO;
using System.Text.Json;
using StellaOrion.Models;

namespace StellaOrion.Services;

public static class HistoryStore
{
    private const int MaxEntries = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string HistoryFile { get; } = Path.Combine(SettingsStore.AppDataRoot, "history.json");

    public static List<HistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(HistoryFile))
            {
                return [];
            }

            var json = File.ReadAllText(HistoryFile);
            var list = JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonOptions) ?? [];
            return [.. list.OrderByDescending(entry => entry.VisitedAt)];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IEnumerable<HistoryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.AppDataRoot);
            var trimmed = entries
                .OrderByDescending(entry => entry.VisitedAt)
                .Take(MaxEntries)
                .ToList();
            File.WriteAllText(HistoryFile, JsonSerializer.Serialize(trimmed, JsonOptions));
        }
        catch
        {
            // History persistence is best-effort; never crash the browser over a write failure.
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(HistoryFile))
            {
                File.Delete(HistoryFile);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
