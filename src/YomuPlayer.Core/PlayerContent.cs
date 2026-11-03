namespace YomuPlayer.Core;

// The "next" response carries more than the radio queue: the browse ids for the song's Lyrics and Related pages.
// They cannot be derived from the video id (YouTube builds them from the track/album pair), so they are captured
// while the queue is fetched and cached.
public sealed record PlayerExtras(string? LyricsBrowseId, string? RelatedBrowseId, string? QueueTitle = null)
{
    public bool IsEmpty => LyricsBrowseId is null && RelatedBrowseId is null && QueueTitle is null;
}

public sealed record LyricsText(string Text, string? Footer)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}
