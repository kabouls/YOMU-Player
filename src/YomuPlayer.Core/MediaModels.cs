namespace YomuPlayer.Core;

public enum MediaKind { Song, Video, Album, Playlist, Artist, Nav }

public enum ShelfStyle { Cards, List, Chips }

public sealed record MediaItem(
    MediaKind Kind,
    string Title,
    string Subtitle,
    string? ThumbnailUrl,
    string? VideoId = null,
    string? PlaylistId = null,
    string? BrowseId = null,
    string? BrowseParams = null,
    Track? Track = null,
    uint? Color = null,
    bool CanEdit = false)
{
    public bool IsTrack => Kind is MediaKind.Song or MediaKind.Video;
    public bool HasQueue => PlaylistId is not null;
}

public sealed record Shelf(string Title, ShelfStyle Style, IReadOnlyList<MediaItem> Items);

public sealed record BrowsePage(string? Title, IReadOnlyList<Shelf> Shelves, string? ThumbnailUrl = null, string? Description = null);
