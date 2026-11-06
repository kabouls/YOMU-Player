using System.Text.Json;

namespace YomuPlayer.Desktop;

// Temporary local "not interested" marks for Home recommendations. This intentionally does not
// change the YouTube account or act like a dislike; it expires after a week.
public static class NotInterested
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppPaths.DataRoot, "not-interested.json");
    private static readonly Dictionary<string, DateTimeOffset> Marks = new(StringComparer.Ordinal);
    private static bool _loaded;

    public static event Action? Changed;

    public static bool IsBlocked(string? videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return false;
        EnsureLoaded();
        lock (Gate)
        {
            if (!Marks.TryGetValue(videoId, out var at)) return false;
            if (DateTimeOffset.UtcNow - at <= Lifetime) return true;
            Marks.Remove(videoId);
            SaveLocked();
            return false;
        }
    }

    public static void Mark(string? videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return;
        EnsureLoaded();
        lock (Gate)
        {
            Marks[videoId] = DateTimeOffset.UtcNow;
            PruneLocked();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                var items = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(FilePath));
                if (items is null) return;
                foreach (var item in items) Marks[item.Key] = item.Value;
                PruneLocked();
            }
            catch
            {
                // Optional local data; ignore corruption.
            }
        }
    }

    private static void PruneLocked()
    {
        var cutoff = DateTimeOffset.UtcNow - Lifetime;
        foreach (var id in Marks.Where(item => item.Value < cutoff).Select(item => item.Key).ToList())
            Marks.Remove(id);
    }

    private static void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Marks));
        }
        catch
        {
            // Optional local data.
        }
    }
}
