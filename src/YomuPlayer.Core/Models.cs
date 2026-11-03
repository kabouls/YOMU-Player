namespace YomuPlayer.Core;

public sealed record Artist(string Name, string? BrowseId);

public sealed record Track(
    string VideoId,
    string Title,
    IReadOnlyList<Artist> Artists,
    string? Album,
    TimeSpan? Duration,
    string? ThumbnailUrl,
    string? AlbumId = null,
    bool Explicit = false,
    string? PlaylistItemId = null,
    string? PlaylistId = null)
{
    public string ArtistLine => string.Join(", ", Artists.Select(a => a.Name));
}

public enum AudioQuality
{
    High,
    Balanced,
    DataSaver
}

public enum TrackRating { None, Like, Dislike }

public sealed record ResolvedStream(
    string VideoId,
    string Url,
    string? Codec,
    double? BitrateKbps,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string> Headers,
    int ExtractMs = 0,
    int WarmupMs = 0);
