using System.Text.Json;
using System.Text.Json.Serialization;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

public sealed class HistoryEntry
{
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artists { get; set; } = "";
    public string? Album { get; set; }
    public string? ThumbnailUrl { get; set; }
    public double DurationSeconds { get; set; }
    public DateTimeOffset PlayedAt { get; set; }
}

// Local, per-Windows-install play history. This is intentionally not the YouTube account history:
// the OAuth app has no watch-history endpoint, and InnerTube history requires browser cookies.
public static class PlaybackHistory
{
    private const int MaxEntries = 300;
    private static readonly object Gate = new();
    private static readonly string PathFile = Path.Combine(AppPaths.DataRoot, "play-history.json");
    private static readonly List<HistoryEntry> Entries = new();
    private static bool _loaded;

    public static event Action? Changed;

    public static IReadOnlyList<HistoryEntry> Items
    {
        get
        {
            EnsureLoaded();
            lock (Gate) return Entries.ToList();
        }
    }

    public static void Record(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.VideoId)) return;
        EnsureLoaded();
        lock (Gate)
        {
            Entries.RemoveAll(e => string.Equals(e.VideoId, track.VideoId, StringComparison.Ordinal));
            Entries.Insert(0, new HistoryEntry
            {
                VideoId = track.VideoId,
                Title = track.Title,
                Artists = track.ArtistLine,
                Album = track.Album,
                ThumbnailUrl = track.ThumbnailUrl,
                DurationSeconds = track.Duration?.TotalSeconds ?? 0,
                PlayedAt = DateTimeOffset.Now
            });
            if (Entries.Count > MaxEntries) Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public static void Clear()
    {
        EnsureLoaded();
        lock (Gate)
        {
            Entries.Clear();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public static Track ToTrack(HistoryEntry entry) => new(
        entry.VideoId,
        entry.Title,
        new[] { new Artist(entry.Artists, null) },
        entry.Album,
        entry.DurationSeconds > 0 ? TimeSpan.FromSeconds(entry.DurationSeconds) : null,
        entry.ThumbnailUrl);

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(PathFile)) return;
                var items = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(PathFile));
                if (items is not null) Entries.AddRange(items.Take(MaxEntries));
            }
            catch
            {
                // A corrupt history file must not stop the app.
            }
        }
    }

    private static void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(Entries,
                new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
        }
        catch
        {
            // Optional data; ignore a full/locked disk.
        }
    }
}
