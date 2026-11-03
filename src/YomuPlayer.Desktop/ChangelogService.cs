using System.Text.Json;

namespace YomuPlayer.Desktop;

public sealed record ChangelogEntry(string Version, string Date, string Channel, IReadOnlyList<string> Notes);

// Changelog is bundled next to the exe, so it always matches the installed build and works offline.
public static class ChangelogService
{
    private static IReadOnlyList<ChangelogEntry>? _entries;

    public static IReadOnlyList<ChangelogEntry> Entries => _entries ??= Load();

    public static ChangelogEntry? ForVersion(string version)
    {
        var normalized = version.Split('+')[0].Trim();
        return Entries.FirstOrDefault(e => string.Equals(e.Version, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ChangelogEntry> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ChangelogPath)) return Array.Empty<ChangelogEntry>();
            var json = File.ReadAllText(AppPaths.ChangelogPath);
            var entries = JsonSerializer.Deserialize<List<ChangelogEntry>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return entries ?? (IReadOnlyList<ChangelogEntry>)Array.Empty<ChangelogEntry>();
        }
        catch (Exception e)
        {
            Log.Write("changelog load failed: " + e.Message);
            return Array.Empty<ChangelogEntry>();
        }
    }
}
