using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;

namespace YomuPlayer.Core;

// Official YouTube Data API v3. YouTube Music's InnerTube endpoints now reject OAuth tokens (400 INVALID_ARGUMENT),
// but the Data API still works with the same token, so account library features live here while browse/playback
// stay on anonymous InnerTube.
public sealed class YouTubeDataApi
{
    private const string Base = "https://www.googleapis.com/youtube/v3/";
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    public sealed record MyChannel(string Id, string? Title, string? ThumbnailUrl, string? LikesPlaylistId);

    public sealed record TrackPage(IReadOnlyList<Track> Tracks, string? NextPageToken);

    private sealed record VideoMeta(TimeSpan? Duration, string? Category, string? ChannelId);

    // Cached channel classification: true means the channel has topics but none of them is Music.
    private static readonly ConcurrentDictionary<string, bool> NonMusicChannels = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (VideoMeta Meta, DateTime At)> VideoMetaCache = new();
    private static readonly ConcurrentDictionary<string, (bool Music, DateTime At)> PlaylistMusicCache = new();
    private static readonly TimeSpan MetaTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan PlaylistMusicTtl = TimeSpan.FromHours(1);

    private MyChannel? _channelCache;
    private DateTime _channelAt;

    public void ClearAccountCache()
    {
        _channelCache = null;
        _channelAt = default;
        VideoMetaCache.Clear();
        PlaylistMusicCache.Clear();
        NonMusicChannels.Clear();
    }

    public async Task<MyChannel?> GetMyChannelAsync(string accessToken, CancellationToken ct = default)
    {
        if (_channelCache is { } cached && DateTime.UtcNow - _channelAt < TimeSpan.FromMinutes(10))
            return cached;

        var root = await GetAsync("channels?part=snippet,contentDetails&mine=true", accessToken, ct);
        var item = root["items"]?.AsArray().FirstOrDefault();
        if (item is null) return null;

        var id = item["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) return null;

        var snippet = item["snippet"];
        var channel = new MyChannel(
            id,
            snippet?["title"]?.GetValue<string>(),
            Thumbnail(snippet),
            item["contentDetails"]?["relatedPlaylists"]?["likes"]?.GetValue<string>());
        _channelCache = channel;
        _channelAt = DateTime.UtcNow;
        return channel;
    }

    public async Task<IReadOnlyList<Track>> GetLikedMusicAsync(string accessToken, CancellationToken ct = default)
    {
        var page = await GetLikedMusicPageAsync(accessToken, null, 500, ct);
        return page.Tracks;
    }

    public async Task<TrackPage> GetLikedMusicPageAsync(
        string accessToken, string? pageToken = null, int pageSize = 100, CancellationToken ct = default,
        bool filterChannels = true)
    {
        var channel = await GetMyChannelAsync(accessToken, ct);
        if (channel?.LikesPlaylistId is not { } likes)
            return new TrackPage(Array.Empty<Track>(), null);
        return await GetPlaylistTracksPageAsync(accessToken, likes, pageToken, pageSize, ct, filterChannels);
    }

    public async Task<IReadOnlyList<MediaItem>> GetMyPlaylistsAsync(string accessToken, CancellationToken ct = default)
    {
        var items = await GetPagesAsync("playlists?part=snippet,contentDetails&mine=true&maxResults=50",
            accessToken, 100, ct);

        var candidates = new List<(string Id, string Title, string? Thumbnail, int ItemCount)>();
        foreach (var item in items)
        {
            var id = item["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || IsSystemPlaylist(id)) continue;

            var snippet = item["snippet"];
            var title = snippet?["title"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(title)) continue;

            candidates.Add((id, title, Thumbnail(snippet),
                item["contentDetails"]?["itemCount"]?.GetValue<int>() ?? 0));
        }

        // The Data API cannot tell a YouTube Music playlist from a regular YouTube playlist. Check the first
        // video's category in one batch of playlist lookups + one videos lookup instead of two serial calls per
        // playlist. Empty playlists stay visible so a newly created playlist still appears.
        var uncached = candidates
            .Where(c => c.ItemCount > 0 && CachedMusic(c.Id) is null)
            .ToList();

        if (uncached.Count > 0)
        {
            var firstIds = await Task.WhenAll(uncached.Select(c =>
                GetFirstPlaylistVideoIdAsync(accessToken, c.Id, ct)));
            var meta = await GetVideoMetaAsync(accessToken,
                firstIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToList(), ct);

            for (var i = 0; i < uncached.Count; i++)
            {
                var id = uncached[i].Id;
                var videoId = firstIds[i];
                var category = videoId is not null && meta.TryGetValue(videoId, out var m)
                    ? m.Category
                    : null;
                // Unknown metadata is treated as music; known non-music categories are hidden.
                var music = category is null || category == "10";
                PlaylistMusicCache[id] = (music, DateTime.UtcNow);
            }
        }

        var playlists = new List<MediaItem>();
        foreach (var candidate in candidates)
        {
            if (candidate.ItemCount > 0 && CachedMusic(candidate.Id) == false) continue;
            playlists.Add(new MediaItem(MediaKind.Playlist, candidate.Title, "Playlist", candidate.Thumbnail,
                PlaylistId: candidate.Id, CanEdit: true));
        }
        return playlists;
    }

    private static bool? CachedMusic(string playlistId)
    {
        if (PlaylistMusicCache.TryGetValue(playlistId, out var hit)
            && DateTime.UtcNow - hit.At < PlaylistMusicTtl)
            return hit.Music;
        return null;
    }

    private async Task<string?> GetFirstPlaylistVideoIdAsync(
        string accessToken, string playlistId, CancellationToken ct)
    {
        try
        {
            var root = await GetAsync(
                "playlistItems?part=contentDetails&maxResults=1&playlistId=" +
                Uri.EscapeDataString(playlistId), accessToken, ct);
            var item = root["items"]?.AsArray().FirstOrDefault();
            return item is null ? null : VideoId(item);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(
        string accessToken, string playlistId, int maxTracks = 500, CancellationToken ct = default) =>
        (await GetPlaylistTracksPageAsync(accessToken, playlistId, null, maxTracks, ct)).Tracks;

    public async Task<TrackPage> GetPlaylistTracksPageAsync(
        string accessToken, string playlistId, string? pageToken, int pageSize, CancellationToken ct = default,
        bool filterChannels = true)
    {
        playlistId = NormalizePlaylistId(playlistId);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var raw = new List<JsonNode>();
        var next = pageToken;

        while (raw.Count < pageSize)
        {
            var take = Math.Min(50, pageSize - raw.Count);
            var path = "playlistItems?part=snippet,contentDetails&maxResults=" + take +
                       "&playlistId=" + Uri.EscapeDataString(playlistId);
            if (!string.IsNullOrEmpty(next))
                path += "&pageToken=" + Uri.EscapeDataString(next);

            var root = await GetAsync(path, accessToken, ct);
            var items = root["items"]?.AsArray();
            if (items is null) break;

            foreach (var item in items)
            {
                if (item is null) continue;
                raw.Add(item);
                if (raw.Count >= pageSize) break;
            }

            next = root["nextPageToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(next)) break;
        }

        var videoIds = raw
            .Select(item => VideoId(item))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct()
            .ToList();
        var meta = await GetVideoMetaAsync(accessToken, videoIds, ct);
        var nonMusic = filterChannels
            ? await GetNonMusicChannelsAsync(accessToken, meta.Values.Select(m => m.ChannelId), ct)
            : new HashSet<string>();

        var tracks = new List<Track>();
        foreach (var item in raw)
        {
            if (ToTrack(item, meta, nonMusic, playlistId) is { } track) tracks.Add(track);
        }
        return new TrackPage(tracks, next);
    }

    public async Task<string> CreatePlaylistAsync(
        string accessToken, string title, CancellationToken ct = default)
    {
        title = title.Trim();
        if (title.Length == 0) throw new InvalidOperationException("Playlist name cannot be empty.");
        if (title.Contains('<') || title.Contains('>'))
            throw new InvalidOperationException("Playlist names cannot contain < or >.");

        var body = new JsonObject
        {
            ["snippet"] = new JsonObject { ["title"] = title },
            ["status"] = new JsonObject { ["privacyStatus"] = "private" }
        };
        var root = await PostAsync("playlists?part=snippet,status", body, accessToken, ct);
        var id = root["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("YouTube did not return a playlist id. " + root.ToJsonString());
        return id;
    }

    public async Task AddToPlaylistAsync(
        string accessToken, string playlistId, IEnumerable<string> videoIds, CancellationToken ct = default)
    {
        playlistId = NormalizePlaylistId(playlistId);
        var ids = videoIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count == 0) return;

        foreach (var videoId in ids)
        {
            var body = new JsonObject
            {
                ["snippet"] = new JsonObject
                {
                    ["playlistId"] = playlistId,
                    ["resourceId"] = new JsonObject
                    {
                        ["kind"] = "youtube#video",
                        ["videoId"] = videoId
                    }
                }
            };
            await PostAsync("playlistItems?part=snippet", body, accessToken, ct);
        }
    }

    public async Task RateVideoAsync(
        string accessToken, string videoId, string rating = "like", CancellationToken ct = default)
    {
        var path = "videos/rate?id=" + Uri.EscapeDataString(videoId) +
                   "&rating=" + Uri.EscapeDataString(rating);
        await PostAsync(path, new JsonObject(), accessToken, ct);
    }

    public async Task<TrackRating> GetVideoRatingAsync(
        string accessToken, string videoId, CancellationToken ct = default)
    {
        var root = await GetAsync("videos/getRating?id=" + Uri.EscapeDataString(videoId), accessToken, ct);
        var rating = root["items"]?.AsArray().FirstOrDefault()?["rating"]?.GetValue<string>();
        return rating switch
        {
            "like" => TrackRating.Like,
            "dislike" => TrackRating.Dislike,
            _ => TrackRating.None
        };
    }

    public async Task DeletePlaylistItemAsync(
        string accessToken, string playlistItemId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Delete,
            Base + "playlistItems?id=" + Uri.EscapeDataString(playlistItemId));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await Http.SendAsync(req, ct);
        await ReadAsync(res, ct);
    }

    public async Task DeletePlaylistAsync(
        string accessToken, string playlistId, CancellationToken ct = default)
    {
        var id = NormalizePlaylistId(playlistId);
        using var req = new HttpRequestMessage(
            HttpMethod.Delete,
            Base + "playlists?id=" + Uri.EscapeDataString(id));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await Http.SendAsync(req, ct);
        await ReadAsync(res, ct);
    }

    private static Track? ToTrack(
        JsonNode item, IReadOnlyDictionary<string, VideoMeta> meta,
        IReadOnlySet<string> nonMusicChannels, string playlistId)
    {
        var videoId = VideoId(item);
        if (string.IsNullOrWhiteSpace(videoId)) return null;

        var snippet = item["snippet"];
        var title = snippet?["title"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(title) || title is "Deleted video" or "Private video") return null;

        meta.TryGetValue(videoId, out var videoMeta);
        // The liked/playlist feed includes non-music videos. Category 10 is Music; if the category is known, reject
        // anything else. As a second layer, reject channels whose topics exist and do not include Music.
        if (videoMeta?.Category is { Length: > 0 } category && category != "10") return null;
        if (videoMeta?.ChannelId is { } channelId && nonMusicChannels.Contains(channelId)) return null;

        var artist = snippet?["videoOwnerChannelTitle"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(artist)) artist = "Unknown artist";

        return new Track(
            videoId,
            title,
            new[] { new Artist(artist, null) },
            null,
            videoMeta?.Duration,
            Thumbnail(snippet),
            PlaylistItemId: item["id"]?.GetValue<string>(),
            PlaylistId: playlistId);
    }

    private static string? VideoId(JsonNode item) =>
        item["contentDetails"]?["videoId"]?.GetValue<string>()
        ?? item["snippet"]?["resourceId"]?["videoId"]?.GetValue<string>();

    private static string? Thumbnail(JsonNode? snippet)
    {
        var thumbs = snippet?["thumbnails"];
        if (thumbs is null) return null;

        foreach (var key in new[] { "maxres", "standard", "high", "medium", "default" })
        {
            if (thumbs[key]?["url"]?.GetValue<string>() is { } url) return url;
        }
        return null;
    }

    private async Task<Dictionary<string, VideoMeta>> GetVideoMetaAsync(
        string accessToken, IReadOnlyList<string> videoIds, CancellationToken ct)
    {
        var result = new Dictionary<string, VideoMeta>();
        var missing = new List<string>();

        foreach (var id in videoIds.Distinct(StringComparer.Ordinal))
        {
            if (VideoMetaCache.TryGetValue(id, out var cached)
                && DateTime.UtcNow - cached.At < MetaTtl)
                result[id] = cached.Meta;
            else
                missing.Add(id);
        }

        foreach (var chunk in missing.Chunk(50))
        {
            var path = "videos?part=snippet,contentDetails&maxResults=50&id=" +
                       string.Join(",", chunk.Select(Uri.EscapeDataString));
            try
            {
                var root = await GetAsync(path, accessToken, ct);
                foreach (var item in root["items"]?.AsArray() ?? new JsonArray())
                {
                    var id = item?["id"]?.GetValue<string>();
                    if (id is null) continue;

                    TimeSpan? duration = null;
                    var text = item?["contentDetails"]?["duration"]?.GetValue<string>();
                    if (text is not null)
                    {
                        try { duration = XmlConvert.ToTimeSpan(text); }
                        catch (FormatException) { /* ignore one bad duration */ }
                    }

                    var meta = new VideoMeta(
                        duration,
                        item?["snippet"]?["categoryId"]?.GetValue<string>(),
                        item?["snippet"]?["channelId"]?.GetValue<string>());
                    VideoMetaCache[id] = (meta, DateTime.UtcNow);
                    result[id] = meta;
                }
            }
            catch (Exception e)
            {
                // Durations/category are cosmetic; a quota or transient failure must not hide the playlist itself.
                System.Diagnostics.Debug.WriteLine("video metadata failed: " + e.Message);
            }
        }

        if (VideoMetaCache.Count > 2500)
        {
            foreach (var key in VideoMetaCache
                         .OrderBy(kv => kv.Value.At)
                         .Take(VideoMetaCache.Count - 2500)
                         .Select(kv => kv.Key)
                         .ToList())
                VideoMetaCache.TryRemove(key, out _);
        }
        return result;
    }

    private async Task<HashSet<string>> GetNonMusicChannelsAsync(
        string accessToken, IEnumerable<string?> channelIds, CancellationToken ct)
    {
        var known = channelIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var unknown = known.Where(id => !NonMusicChannels.ContainsKey(id)).ToList();
        foreach (var chunk in unknown.Chunk(50))
        {
            try
            {
                var path = "channels?part=topicDetails&maxResults=50&id=" +
                           string.Join(",", chunk.Select(Uri.EscapeDataString));
                var root = await GetAsync(path, accessToken, ct);
                foreach (var item in root["items"]?.AsArray() ?? new JsonArray())
                {
                    var id = item?["id"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var topics = item?["topicDetails"]?["topicCategories"]?.AsArray();
                    var hasMusic = topics?.Any(t =>
                        (t?.GetValue<string>() ?? "").EndsWith("/Music", StringComparison.OrdinalIgnoreCase)) == true;
                    NonMusicChannels[id] = topics is { Count: > 0 } && !hasMusic;
                }
            }
            catch (Exception e)
            {
                // Unknown channels stay allowed; a metadata failure must not empty the library.
                System.Diagnostics.Debug.WriteLine("channel topics failed: " + e.Message);
            }
        }

        return known
            .Where(id => NonMusicChannels.TryGetValue(id, out var blocked) && blocked)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<List<JsonNode>> GetPagesAsync(
        string path, string accessToken, int maxItems, CancellationToken ct)
    {
        var items = new List<JsonNode>();
        string? pageToken = null;

        while (items.Count < maxItems)
        {
            var url = path;
            if (!string.IsNullOrEmpty(pageToken))
                url += "&pageToken=" + Uri.EscapeDataString(pageToken);

            var root = await GetAsync(url, accessToken, ct);
            var page = root["items"]?.AsArray();
            if (page is null) break;

            foreach (var item in page)
            {
                if (item is null) continue;
                items.Add(item);
                if (items.Count >= maxItems) break;
            }

            pageToken = root["nextPageToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pageToken)) break;
        }
        return items;
    }

    private static async Task<JsonNode> GetAsync(string path, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await Http.SendAsync(req, ct);
        return await ReadAsync(res, ct);
    }

    private static async Task<JsonNode> PostAsync(
        string path, JsonObject body, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await Http.SendAsync(req, ct);
        return await ReadAsync(res, ct);
    }

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            JsonNode? errorNode = null;
            try { errorNode = JsonNode.Parse(text); }
            catch { /* non-JSON error */ }

            var message = errorNode?["error"]?["message"]?.GetValue<string>() ?? text;
            var reason = errorNode?["error"]?["errors"]?.AsArray()?.FirstOrDefault()?["reason"]?.GetValue<string>();
            var friendly = res.StatusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    "Google sign-in has expired. Sign out and sign in again.",
                HttpStatusCode.Forbidden when reason == "quotaExceeded" || message.Contains("quota", StringComparison.OrdinalIgnoreCase) =>
                    "YouTube API quota is exhausted for this project. Try again later.",
                HttpStatusCode.Forbidden when reason == "insufficientPermissions" ||
                                              message.Contains("insufficient", StringComparison.OrdinalIgnoreCase) =>
                    "Google did not grant YouTube access. Revoke YOMU Player at myaccount.google.com/permissions, then sign in again and tick the YouTube permission.",
                _ => message
            };
            throw new InvalidOperationException("YouTube library request failed: " + friendly);
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        try
        {
            return await JsonNode.ParseAsync(stream, cancellationToken: ct) ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static string NormalizePlaylistId(string id) =>
        id.StartsWith("VL", StringComparison.Ordinal) ? id[2..] : id;

    private static bool IsSystemPlaylist(string id) =>
        id.StartsWith("LL", StringComparison.Ordinal) ||
        id.StartsWith("UU", StringComparison.Ordinal) ||
        id.StartsWith("FL", StringComparison.Ordinal) ||
        id.StartsWith("WL", StringComparison.Ordinal) ||
        id.StartsWith("HL", StringComparison.Ordinal) ||
        id.StartsWith("RD", StringComparison.Ordinal);
}
