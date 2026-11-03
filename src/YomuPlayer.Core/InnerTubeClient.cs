using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace YomuPlayer.Core;

public sealed class InnerTubeClient : IDisposable
{
    private const string Origin = "https://music.youtube.com";
    private const string Endpoint = Origin + "/youtubei/v1/";
    private const string ClientVersion = "1.20250101.01.00";
    private const string SongsFilter = "EgWKAQIIAWoMEA4QChADEAQQCRAF";

    private readonly HttpClient _http;
    private readonly string _language;
    private readonly string _country;

    // Small side cache: a "next" response is ~500 KB, and the player page only needs two browse ids from it.
    // Capturing them while the radio queue is fetched means opening the player page usually costs no request.
    private const int MaxCachedExtras = 16;
    private readonly ConcurrentDictionary<string, PlayerExtras> _extras = new();
    private readonly ConcurrentQueue<string> _extrasOrder = new();

    public InnerTubeClient(string language = "en", string country = "ID", HttpClient? http = null)
    {
        _language = language;
        _country = country;
        _http = http ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0");
        _http.DefaultRequestHeaders.Add("Origin", Origin);
        _http.DefaultRequestHeaders.Referrer = new Uri(Origin + "/");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
    }

    public async Task<IReadOnlyList<Track>> SearchSongsAsync(string query, CancellationToken ct = default)
    {
        var body = NewBody();
        body["query"] = query;
        body["params"] = SongsFilter;

        var root = await PostAsync("search", body, ct);
        return ParseSongs(root);
    }

    // ---------- high-level pages ----------

    public async Task<BrowsePage> BrowseAsync(string browseId, string? browseParams = null, CancellationToken ct = default) =>
        YtmParser.ParseBrowse(await BrowseRawAsync(browseId, browseParams, ct));

    public async Task<BrowsePage> ArtistAsync(string channelId, string? name = null, CancellationToken ct = default) =>
        YtmParser.ParseBrowse(await BrowseRawAsync(channelId, null, ct), new Artist(name ?? "", channelId));

    // @handle, /c/name and /user/name links carry no channel id; ask YouTube which channel they point to.
    public async Task<string?> ResolveChannelAsync(string url, CancellationToken ct = default)
    {
        var body = NewBody();
        body["url"] = url;
        var root = await PostAsync("navigation/resolve_url", body, ct);
        return FindAll(root, "browseId").Select(n => n.GetValue<string>()).FirstOrDefault(id => id.StartsWith("UC"));
    }

    public Task<BrowsePage> HomeAsync(CancellationToken ct = default) => BrowseAsync("FEmusic_home", null, ct);

    public Task<BrowsePage> ExploreAsync(CancellationToken ct = default) => BrowseAsync("FEmusic_explore", null, ct);

    public async Task<IReadOnlyList<MediaItem>> SearchAllAsync(string query, CancellationToken ct = default) =>
        YtmParser.ParseSearchAll(await SearchRawAsync(query, null, ct));

    // Queue for a song radio (videoId), an album/playlist (playlistId), or both.
    public async Task<IReadOnlyList<Track>> GetQueueAsync(
        string? videoId, string? playlistId, string? queueParams = null, CancellationToken ct = default)
    {
        var body = NewBody();
        if (videoId is not null) body["videoId"] = videoId;
        if (playlistId is not null) body["playlistId"] = playlistId;
        else if (videoId is not null) body["playlistId"] = "RDAMVM" + videoId;
        if (queueParams is not null) body["params"] = queueParams;
        body["isAudioOnly"] = true;
        var root = await PostAsync("next", body, ct);

        // Same response, second use: remember where this song's lyrics and related pages live.
        if (videoId is not null) CacheExtras(videoId, YtmParser.ParsePlayerExtras(root));

        return YtmParser.ParseQueue(root);
    }

    public Task<IReadOnlyList<Track>> GetRadioAsync(string videoId, CancellationToken ct = default) =>
        GetQueueAsync(videoId, null, null, ct);

    public PlayerExtras? TryGetCachedExtras(string videoId) =>
        _extras.TryGetValue(videoId, out var extras) ? extras : null;

    // When signed in, every InnerTube call carries the account's access token. The provider refreshes it on demand
    // and returns null when signed out; a refresh failure must not break anonymous browsing, so it is swallowed.
    public Func<CancellationToken, Task<string?>>? AccessTokenProvider { get; set; }

    public async Task<PlayerExtras> GetPlayerExtrasAsync(string videoId, CancellationToken ct = default)
    {
        if (TryGetCachedExtras(videoId) is { } hit) return hit;

        var body = NewBody();
        body["videoId"] = videoId;
        body["playlistId"] = "RDAMVM" + videoId;
        body["isAudioOnly"] = true;

        var extras = YtmParser.ParsePlayerExtras(await PostAsync("next", body, ct));
        CacheExtras(videoId, extras);
        return extras;
    }

    public async Task<LyricsText?> GetLyricsAsync(string browseId, CancellationToken ct = default)
    {
        var body = NewBody();
        body["browseId"] = browseId;
        return YtmParser.ParseLyrics(await PostAsync("browse", body, ct));
    }

    // The account's playlists, used by the sidebar and the save-queue picker. Liked Music itself is the special
    // playlist "LM" (browse "VLLM"), not part of this list.
    public async Task<IReadOnlyList<MediaItem>> GetLibraryPlaylistsAsync(CancellationToken ct = default)
    {
        var page = await BrowseAsync("FEmusic_liked_playlists", null, ct);
        var seen = new HashSet<string>();
        var playlists = new List<MediaItem>();

        foreach (var item in page.Shelves.SelectMany(s => s.Items))
        {
            if (item.Kind != MediaKind.Playlist) continue;
            var id = item.PlaylistId
                     ?? (item.BrowseId is { } browse && browse.StartsWith("VL", StringComparison.Ordinal)
                         ? browse[2..]
                         : null);
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;

            playlists.Add(item with
            {
                PlaylistId = id,
                BrowseId = "VL" + id,
                Subtitle = string.IsNullOrWhiteSpace(item.Subtitle) ? "Playlist" : item.Subtitle
            });
        }
        return playlists;
    }

    // playlist/create accepts the initial video ids, so saving a queue into a new playlist is one request.
    public async Task<string> CreatePlaylistAsync(
        string title, IEnumerable<string>? videoIds = null, CancellationToken ct = default)
    {
        title = title.Trim();
        if (title.Length == 0) throw new InvalidOperationException("Playlist name cannot be empty.");
        if (title.Contains('<') || title.Contains('>'))
            throw new InvalidOperationException("Playlist names cannot contain < or >.");

        var body = NewBody();
        body["title"] = title;
        body["description"] = "";
        body["privacyStatus"] = "PRIVATE";

        var ids = videoIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids is { Count: > 0 })
        {
            var array = new JsonArray();
            foreach (var id in ids) array.Add(id);
            body["videoIds"] = array;
        }

        var root = await PostAsync("playlist/create", body, ct);
        var playlistId = root["playlistId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new InvalidOperationException("YouTube did not return a playlist id. " + root.ToJsonString());
        return playlistId;
    }

    public async Task AddToPlaylistAsync(
        string playlistId, IEnumerable<string> videoIds, CancellationToken ct = default)
    {
        var ids = videoIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count == 0) return;

        var body = NewBody();
        body["playlistId"] = NormalizePlaylistId(playlistId);
        var actions = new JsonArray();
        foreach (var id in ids)
        {
            actions.Add(new JsonObject
            {
                ["action"] = "ACTION_ADD_VIDEO",
                ["addedVideoId"] = id
            });
        }
        body["actions"] = actions;

        var root = await PostAsync("browse/edit_playlist", body, ct);
        var status = root["status"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(status) && !status.Contains("SUCCEEDED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("YouTube rejected the playlist edit: " + status);
    }

    private static string NormalizePlaylistId(string id) =>
        id.StartsWith("VL", StringComparison.Ordinal) ? id[2..] : id;

    private void CacheExtras(string videoId, PlayerExtras extras)
    {
        if (extras.IsEmpty) return;

        if (!_extras.TryAdd(videoId, extras)) _extras[videoId] = extras;
        else _extrasOrder.Enqueue(videoId);

        while (_extras.Count > MaxCachedExtras && _extrasOrder.TryDequeue(out var oldest))
            _extras.TryRemove(oldest, out _);
    }

    public async Task<IReadOnlyList<string>> GetSearchSuggestionsAsync(string query, CancellationToken ct = default)
    {
        var body = NewBody();
        body["input"] = query;
        return YtmParser.ParseSuggestions(await PostAsync("music/get_search_suggestions", body, ct));
    }

    public Task<JsonNode> SuggestionsRawAsync(string query, CancellationToken ct = default)
    {
        var body = NewBody();
        body["input"] = query;
        return PostAsync("music/get_search_suggestions", body, ct);
    }

    // Raw endpoints, used to capture real response shapes for the Home/Explore/queue parsers.
    public Task<JsonNode> BrowseRawAsync(string browseId, string? browseParams = null, CancellationToken ct = default)
    {
        var body = NewBody();
        body["browseId"] = browseId;
        if (browseParams is not null) body["params"] = browseParams;
        return PostAsync("browse", body, ct);
    }

    public Task<JsonNode> SearchRawAsync(string query, string? filterParams = null, CancellationToken ct = default)
    {
        var body = NewBody();
        body["query"] = query;
        if (filterParams is not null) body["params"] = filterParams;
        return PostAsync("search", body, ct);
    }

    public Task<JsonNode> NextRawAsync(string videoId, CancellationToken ct = default)
    {
        var body = NewBody();
        body["videoId"] = videoId;
        body["playlistId"] = "RDAMVM" + videoId;
        body["isAudioOnly"] = true;
        return PostAsync("next", body, ct);
    }

    private JsonObject NewBody() => new()
    {
        ["context"] = new JsonObject
        {
            ["client"] = new JsonObject
            {
                ["clientName"] = "WEB_REMIX",
                ["clientVersion"] = ClientVersion,
                ["hl"] = _language,
                ["gl"] = _country
            }
        }
    };

    private async Task<JsonNode> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        string? token = null;
        if (AccessTokenProvider is not null)
        {
            try { token = await AccessTokenProvider(ct); }
            catch (OperationCanceledException) { throw; }
            catch { token = null; }
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}{path}?prettyPrint=false")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
            req.Headers.TryAddWithoutValidation("X-Origin", Origin);
        }
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var error = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"InnerTube {path} failed: {(int)res.StatusCode} {error[..Math.Min(300, error.Length)]}");
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct)
               ?? throw new InvalidOperationException("Empty InnerTube response");
    }

    // Walks the whole tree for list item renderers so minor layout changes don't break parsing.
    public static IReadOnlyList<Track> ParseSongs(JsonNode root)
    {
        var tracks = new List<Track>();
        foreach (var item in FindAll(root, "musicResponsiveListItemRenderer"))
        {
            var videoId = item["playlistItemData"]?["videoId"]?.GetValue<string>()
                          ?? FindFirst(item["overlay"], "videoId")?.GetValue<string>();
            if (videoId is null) continue;

            var cols = item["flexColumns"]?.AsArray();
            if (cols is null || cols.Count < 2) continue;

            var title = Runs(cols[0]).FirstOrDefault()?["text"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(title)) continue;

            var artists = new List<Artist>();
            string? album = null;
            TimeSpan? duration = null;

            var segments = new List<List<JsonNode>> { new() };
            foreach (var run in Runs(cols[1]))
            {
                if (run is null) continue;
                var t = run["text"]?.GetValue<string>();
                if (t is null) continue;
                if (t.Trim() == "\u2022") segments.Add(new List<JsonNode>());
                else segments[^1].Add(run);
            }

            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var segText = string.Concat(seg.Select(r => r["text"]!.GetValue<string>())).Trim();
                if (segText.Length == 0) continue;

                if (i > 0 && TryParseDuration(segText, out var d))
                {
                    duration = d;
                }
                else if (i == 0)
                {
                    foreach (var r in seg)
                    {
                        var name = r["text"]!.GetValue<string>();
                        var trimmed = name.Trim();
                        if (trimmed.Length == 0 || trimmed is "," or "&") continue;
                        var browseId = r["navigationEndpoint"]?["browseEndpoint"]?["browseId"]?.GetValue<string>();
                        artists.Add(new Artist(trimmed, browseId));
                    }
                }
                else if (album is null)
                {
                    album = segText;
                }
            }

            var thumbs = item["thumbnail"]?["musicThumbnailRenderer"]?["thumbnail"]?["thumbnails"]?.AsArray();
            var thumb = thumbs?.LastOrDefault()?["url"]?.GetValue<string>();

            tracks.Add(new Track(videoId, title!, artists, album, duration, thumb));
        }
        return tracks;
    }

    private static IEnumerable<JsonNode?> Runs(JsonNode? flexColumn) =>
        flexColumn?["musicResponsiveListItemFlexColumnRenderer"]?["text"]?["runs"]?.AsArray()
        ?? (IEnumerable<JsonNode?>)Array.Empty<JsonNode?>();

    internal static bool TryParseDuration(string s, out TimeSpan d)
    {
        d = default;
        var parts = s.Split(':');
        if (parts.Length is < 2 or > 3) return false;
        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out nums[i])) return false;
        d = parts.Length == 2
            ? new TimeSpan(0, nums[0], nums[1])
            : new TimeSpan(nums[0], nums[1], nums[2]);
        return true;
    }

    private static IEnumerable<JsonNode> FindAll(JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (k, v) in obj)
                {
                    if (k == key && v is not null) yield return v;
                    foreach (var f in FindAll(v, key)) yield return f;
                }
                break;
            case JsonArray arr:
                foreach (var v in arr)
                    foreach (var f in FindAll(v, key)) yield return f;
                break;
        }
    }

    private static JsonNode? FindFirst(JsonNode? node, string key) => FindAll(node, key).FirstOrDefault();

    public void Dispose() => _http.Dispose();
}
