using System.Text.Json.Nodes;

namespace YomuPlayer.Core;

// Parses InnerTube (WEB_REMIX) responses. Everything is defensive: missing or reshaped nodes are skipped, never thrown on.
public static class YtmParser
{
    private static readonly string[] ShelfKeys =
    {
        "musicCarouselShelfRenderer", "musicShelfRenderer", "musicPlaylistShelfRenderer",
        "gridRenderer", "musicImmersiveCarouselShelfRenderer"
    };

    private static readonly HashSet<string> TypeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Song", "Video", "Album", "Single", "EP", "Playlist", "Artist", "Profile", "Episode", "Podcast"
    };

    // ---------- public entry points ----------

    public static BrowsePage ParseBrowse(JsonNode root, Artist? defaultArtist = null)
    {
        // Pages use different header renderers (plain, immersive for artists, responsive for albums/playlists):
        // read the title, art and description from whichever is present.
        var header = G(root, "header");
        var title = Text(G(G(header, "musicHeaderRenderer"), "title"));
        if (string.IsNullOrWhiteSpace(title)) title = Text(FindFirst(header, "title"));
        var description = Text(FindFirst(header, "description"));
        return new BrowsePage(
            string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            ParseShelves(root, defaultArtist),
            BestThumb(header),
            string.IsNullOrWhiteSpace(description) ? null : description.Trim());
    }

    public static IReadOnlyList<Shelf> ParseShelves(JsonNode root, Artist? defaultArtist = null)
    {
        var shelves = new List<Shelf>();
        foreach (var node in FindShelves(root))
        {
            var title = Text(FindFirst(G(node, "header"), "title"));
            if (string.IsNullOrWhiteSpace(title)) title = Text(G(node, "title"));

            var raw = G(node, "contents") as JsonArray ?? G(node, "items") as JsonArray;
            if (raw is null) continue;

            var items = new List<MediaItem>();
            foreach (var el in raw)
            {
                var item = ParseElement(el, defaultArtist);
                if (item is not null) items.Add(item);
            }
            if (items.Count == 0) continue;

            var nav = items.Count(i => i.Kind == MediaKind.Nav);
            var list = items.Count(i => i.Kind is MediaKind.Song or MediaKind.Artist && i.Track is not null || i.Kind == MediaKind.Artist);
            var style = nav * 2 > items.Count ? ShelfStyle.Chips
                : IsListShelf(raw) ? ShelfStyle.List
                : ShelfStyle.Cards;
            shelves.Add(new Shelf(title.Trim(), style, items));
        }
        return shelves;
    }

    public static IReadOnlyList<MediaItem> ParseSearchAll(JsonNode root)
    {
        var result = new List<MediaItem>();
        var seen = new HashSet<string>();

        var card = FindAll(root, "musicCardShelfRenderer").FirstOrDefault();
        var top = card is null ? null : ParseCard(card);
        if (top is not null && seen.Add(Key(top))) result.Add(top);

        // Songs listed under an artist card omit the artist column; borrow it from the card.
        var cardArtist = top is { Kind: MediaKind.Artist } ? new Artist(top.Title, top.BrowseId) : null;
        if (G(card, "contents") is JsonArray cardItems)
            foreach (var el in cardItems)
            {
                var m = ParseListItem(G(el, "musicResponsiveListItemRenderer"), cardArtist);
                if (m is not null && seen.Add(Key(m))) result.Add(m);
            }

        foreach (var li in FindAll(root, "musicResponsiveListItemRenderer"))
        {
            var m = ParseListItem(li);
            if (m is not null && seen.Add(Key(m))) result.Add(m);
        }

        // Songs and artists first, then albums, videos, playlists. OrderBy is stable, so relevance order holds inside each group.
        return result.OrderBy(i => i.Kind switch
        {
            MediaKind.Song or MediaKind.Artist => 0,
            MediaKind.Album => 1,
            MediaKind.Video => 2,
            MediaKind.Playlist => 3,
            _ => 4
        }).ToList();
    }

    public static IReadOnlyList<Track> ParseQueue(JsonNode root)
    {
        var tracks = new List<Track>();
        var panel = FindAll(root, "playlistPanelRenderer").FirstOrDefault();
        if (G(panel, "contents") is not JsonArray contents) return tracks;

        foreach (var c in contents)
        {
            var r = G(c, "playlistPanelVideoRenderer")
                    ?? G(G(G(c, "playlistPanelVideoWrapperRenderer"), "primaryRenderer"), "playlistPanelVideoRenderer");
            if (r is null) continue;

            var videoId = Str(G(r, "videoId"));
            var title = Text(G(r, "title"));
            if (videoId.Length == 0 || title.Length == 0) continue;

            var segs = Segments(G(r, "longBylineText"));
            var artists = segs.Count > 0 ? Artists(segs[0]) : new List<Artist>();
            string? album = null;
            string? albumId = null;
            foreach (var seg in segs.Skip(1))
            {
                var browse = seg.Select(x => Str(G(G(G(x, "navigationEndpoint"), "browseEndpoint"), "browseId"))).FirstOrDefault(b => b.StartsWith("MPRE"));
                if (browse is not null)
                {
                    album = string.Concat(seg.Select(RunText)).Trim();
                    albumId = browse;
                }
            }

            TimeSpan? dur = TryDuration(Text(G(r, "lengthText")), out var d) ? d : null;
            tracks.Add(new Track(videoId, title, artists, album, dur, BestThumb(G(r, "thumbnail")), albumId,
                IsExplicit(r)));
        }
        return tracks;
    }

    // The player page tabs ("Lyrics", "Related") point at browse pages by id. YouTube also exposes the same ids in
    // engagement panels, so the lookup is by page type rather than by walking a particular layout.
    public static PlayerExtras ParsePlayerExtras(JsonNode root)
    {
        var queueHeader = FindAll(root, "musicQueueHeaderRenderer").FirstOrDefault();
        var queueTitle = queueHeader is null ? null : NullIfEmpty(Text(G(queueHeader, "subtitle")));
        return new(FindBrowseId(root, "MUSIC_PAGE_TYPE_TRACK_LYRICS"),
            FindBrowseId(root, "MUSIC_PAGE_TYPE_TRACK_RELATED"),
            queueTitle);
    }

    // Lyrics pages are a section list with one description shelf: the full text in a single run, footer carries the
    // provider ("Lyrics powered by LyricFind"). Songs without lyrics answer with a message renderer instead.
    public static LyricsText? ParseLyrics(JsonNode root)
    {
        var shelf = FindAll(root, "musicDescriptionShelfRenderer").FirstOrDefault();
        if (shelf is null) return null;

        var text = NormalizeLines(Text(G(shelf, "description")));
        if (text.Length == 0) return null;

        var footer = NormalizeLines(Text(G(shelf, "footer")));
        return new LyricsText(text, footer.Length == 0 ? null : footer);
    }

    private static string? FindBrowseId(JsonNode? root, string pageType)
    {
        foreach (var endpoint in FindAll(root, "browseEndpoint"))
        {
            var configs = G(endpoint, "browseEndpointContextSupportedConfigs");
            var music = G(configs, "browseEndpointContextMusicConfig");
            if (Str(G(music, "pageType")) == pageType) return NullIfEmpty(Str(G(endpoint, "browseId")));
        }
        return null;
    }

    private static string NormalizeLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    public static IReadOnlyList<string> ParseSuggestions(JsonNode root)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in FindAll(root, "searchSuggestionRenderer"))
        {
            var text = Str(G(G(G(node, "navigationEndpoint"), "searchEndpoint"), "query"));
            if (text.Length == 0) text = Text(G(node, "suggestion"));
            text = text.Trim();
            if (text.Length > 0 && seen.Add(text)) result.Add(text);
        }
        return result;
    }

    // ---------- items ----------

    private static MediaItem? ParseElement(JsonNode? el, Artist? defaultArtist = null)
    {
        if (el is not JsonObject o) return null;
        var kv = o.FirstOrDefault();
        return kv.Key switch
        {
            "musicResponsiveListItemRenderer" => ParseListItem(kv.Value, defaultArtist),
            "musicTwoRowItemRenderer" => ParseTwoRow(kv.Value),
            "musicNavigationButtonRenderer" => ParseNav(kv.Value),
            _ => null
        };
    }

    private static bool IsListShelf(JsonArray raw)
    {
        var n = raw.Count(e => e is JsonObject o && o.ContainsKey("musicResponsiveListItemRenderer"));
        return n * 2 > raw.Count;
    }

    public static MediaItem? ParseListItem(JsonNode? r, Artist? defaultArtist = null)
    {
        if (G(r, "flexColumns") is not JsonArray cols || cols.Count == 0) return null;
        static JsonNode? ColText(JsonArray c, int i) =>
            i < c.Count ? G(G(c[i], "musicResponsiveListItemFlexColumnRenderer"), "text") : null;

        var title = Text(ColText(cols, 0));
        if (title.Length == 0) return null;

        var segs = Segments(ColText(cols, 1));
        string? label = null;
        if (segs.Count > 0 && TypeLabels.Contains(SegText(segs[0])))
        {
            label = SegText(segs[0]);
            segs.RemoveAt(0);
        }

        var overlay = G(r, "overlay");
        var videoId = NullIfEmpty(Str(G(G(r, "playlistItemData"), "videoId")))
                      ?? NullIfEmpty(Str(FindFirst(overlay, "videoId")))
                      ?? NullIfEmpty(Str(FindFirst(ColText(cols, 0), "videoId")));
        var nav = G(r, "navigationEndpoint");
        var pageType = PageType(nav);
        var thumb = BestThumb(G(r, "thumbnail"));

        string? album = null;
        string? albumId = null;
        TimeSpan? dur = null;
        var shown = new List<string>();
        for (var i = 0; i < segs.Count; i++)
        {
            var st = SegText(segs[i]);
            if (st.Length == 0) continue;
            if (TryDuration(st, out var d)) { dur = d; continue; }
            if (i > 0)
            {
                var mpre = segs[i].Select(x => Str(G(G(G(x, "navigationEndpoint"), "browseEndpoint"), "browseId")))
                    .FirstOrDefault(b => b.StartsWith("MPRE"));
                if (mpre is not null) { album = st; albumId = mpre; }
            }
            shown.Add(st);
        }
        if (album is null && cols.Count > 2)
        {
            var c2 = Text(ColText(cols, 2));
            if (c2.Length > 0 && !TryDuration(c2, out _)) { album = c2; if (!shown.Contains(c2)) shown.Add(c2); }
        }

        if (videoId is not null)
        {
            var type = Str(FindFirst(overlay, "musicVideoType"));
            var isVideo = string.Equals(label, "Video", StringComparison.OrdinalIgnoreCase)
                          || (type.Length > 0 && !type.Contains("ATV"));
            var artists = segs.Count > 0 && !TryDuration(SegText(segs[0]), out _) ? Artists(segs[0]) : new List<Artist>();
            if (artists.Count == 0 && defaultArtist is not null)
            {
                artists.Add(defaultArtist);
                shown.Insert(0, defaultArtist.Name);
            }
            var track = new Track(videoId, title, artists, album, dur, thumb, albumId, IsExplicit(r));
            var sub = string.Join(" \u2022 ", isVideo && label is null ? shown.Prepend("Video") : shown);
            return new MediaItem(isVideo ? MediaKind.Video : MediaKind.Song, title, sub, thumb, VideoId: videoId, Track: track);
        }

        var kind = pageType switch
        {
            "MUSIC_PAGE_TYPE_ALBUM" => MediaKind.Album,
            "MUSIC_PAGE_TYPE_PLAYLIST" => MediaKind.Playlist,
            "MUSIC_PAGE_TYPE_ARTIST" => MediaKind.Artist,
            _ => (MediaKind?)null
        };
        if (kind is null) return null;

        var be = G(nav, "browseEndpoint");
        var browseId = NullIfEmpty(Str(G(be, "browseId")));
        var playlistId = NullIfEmpty(Str(FindFirst(overlay, "playlistId"))) ?? PlaylistFromBrowse(browseId);
        var subtitle = string.Join(" \u2022 ", label is null ? shown : shown.Prepend(label));
        return new MediaItem(kind.Value, title, subtitle, thumb, PlaylistId: playlistId, BrowseId: browseId,
            BrowseParams: NullIfEmpty(Str(G(be, "params"))));
    }

    public static MediaItem? ParseTwoRow(JsonNode? r)
    {
        var title = Text(G(r, "title"));
        if (title.Length == 0) return null;

        var subtitle = Text(G(r, "subtitle"));
        var nav = G(r, "navigationEndpoint");
        var overlay = G(r, "thumbnailOverlay");
        var thumb = BestThumb(G(r, "thumbnailRenderer"));
        var pageType = PageType(nav);
        var videoId = NullIfEmpty(Str(G(G(nav, "watchEndpoint"), "videoId")));

        if (videoId is not null)
        {
            var type = Str(FindFirst(nav, "musicVideoType"));
            var isVideo = type.Length > 0 && !type.Contains("ATV");
            var segs = Segments(G(r, "subtitle"));
            var artists = segs.Count > 0 ? Artists(segs[0]) : new List<Artist>();
            var track = new Track(videoId, title, artists, null, null, thumb, Explicit: IsExplicit(r));
            return new MediaItem(isVideo ? MediaKind.Video : MediaKind.Song, title, subtitle, thumb, VideoId: videoId, Track: track);
        }

        var be = G(nav, "browseEndpoint");
        var browseId = NullIfEmpty(Str(G(be, "browseId")));
        var playlistId = NullIfEmpty(Str(FindFirst(overlay, "playlistId"))) ?? PlaylistFromBrowse(browseId);
        var kind = pageType switch
        {
            "MUSIC_PAGE_TYPE_ALBUM" => MediaKind.Album,
            "MUSIC_PAGE_TYPE_ARTIST" => MediaKind.Artist,
            "MUSIC_PAGE_TYPE_PLAYLIST" => MediaKind.Playlist,
            _ => playlistId is not null ? MediaKind.Playlist : (MediaKind?)null
        };
        if (kind is null) return null;

        return new MediaItem(kind.Value, title, subtitle, thumb, PlaylistId: playlistId, BrowseId: browseId,
            BrowseParams: NullIfEmpty(Str(G(be, "params"))));
    }

    public static MediaItem? ParseNav(JsonNode? r)
    {
        var title = Text(G(r, "buttonText"));
        var be = G(G(r, "clickCommand"), "browseEndpoint");
        var browseId = NullIfEmpty(Str(G(be, "browseId")));
        if (title.Length == 0 || browseId is null) return null;

        uint? color = null;
        if (G(G(r, "solid"), "leftStripeColor") is JsonValue v && v.TryGetValue<long>(out var l)) color = (uint)l;
        return new MediaItem(MediaKind.Nav, title, "", null, BrowseId: browseId,
            BrowseParams: NullIfEmpty(Str(G(be, "params"))), Color: color);
    }

    private static MediaItem? ParseCard(JsonNode card)
    {
        var run = Runs(G(card, "title")).FirstOrDefault();
        var title = run is null ? "" : RunText(run);
        if (title.Length == 0) return null;

        var nav = G(run, "navigationEndpoint");
        var be = G(nav, "browseEndpoint");
        var browseId = NullIfEmpty(Str(G(be, "browseId")));
        var videoId = NullIfEmpty(Str(G(G(nav, "watchEndpoint"), "videoId")));
        var thumb = BestThumb(G(card, "thumbnail"));
        var sub = Text(G(card, "subtitle")).Trim().TrimEnd('\u2022', ' ');
        var kind = PageType(nav) switch
        {
            "MUSIC_PAGE_TYPE_ARTIST" => MediaKind.Artist,
            "MUSIC_PAGE_TYPE_ALBUM" => MediaKind.Album,
            "MUSIC_PAGE_TYPE_PLAYLIST" => MediaKind.Playlist,
            _ => videoId is not null ? MediaKind.Song : (MediaKind?)null
        };
        if (kind is null) return null;

        var track = videoId is null ? null : new Track(videoId, title, new List<Artist>(), null, null, thumb,
            Explicit: IsExplicit(card));
        return new MediaItem(kind.Value, title, sub, thumb, VideoId: videoId, PlaylistId: PlaylistFromBrowse(browseId),
            BrowseId: browseId, BrowseParams: NullIfEmpty(Str(G(be, "params"))), Track: track);
    }

    // ---------- helpers ----------

    public static string Key(MediaItem m) => m.VideoId ?? m.BrowseId ?? m.PlaylistId ?? m.Title;

    private static string? PlaylistFromBrowse(string? browseId) =>
        browseId is not null && browseId.StartsWith("VL") ? browseId[2..] : null;

    private static string PageType(JsonNode? endpoint) =>
        Str(G(G(G(G(endpoint, "browseEndpoint"), "browseEndpointContextSupportedConfigs"), "browseEndpointContextMusicConfig"), "pageType"));

    private static readonly System.Text.RegularExpressions.Regex StatText = new(
        @"^\d[\d.,]*\s*[KMB]?\s+(plays|views|subscribers|listeners|likes|songs|monthly audience)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsStat(string s) => StatText.IsMatch(s.Trim());

    private static List<Artist> Artists(List<JsonNode> runs)
    {
        var list = new List<Artist>();
        foreach (var r in runs)
        {
            var t = RunText(r).Trim();
            if (t.Length == 0 || t is "," or "&" or "and" || IsStat(t)) continue;
            var id = NullIfEmpty(Str(G(G(G(r, "navigationEndpoint"), "browseEndpoint"), "browseId")));
            list.Add(new Artist(t, id));
        }
        return list;
    }

    private static List<List<JsonNode>> Segments(JsonNode? text)
    {
        var segs = new List<List<JsonNode>> { new() };
        foreach (var run in Runs(text))
        {
            if (RunText(run).Trim() == "\u2022") segs.Add(new List<JsonNode>());
            else segs[^1].Add(run);
        }
        return segs;
    }

    private static string SegText(List<JsonNode> seg) => string.Concat(seg.Select(RunText)).Trim();

    private static List<JsonNode> Runs(JsonNode? text) =>
        G(text, "runs") is JsonArray a ? a.Where(x => x is not null).Select(x => x!).ToList() : new List<JsonNode>();

    private static string RunText(JsonNode run) => Str(G(run, "text"));

    private static string Text(JsonNode? text) => string.Concat(Runs(text).Select(RunText));

    private static string? BestThumb(JsonNode? root) =>
        FindFirst(root, "thumbnails") is JsonArray a && a.Count > 0 ? NullIfEmpty(Str(G(a[a.Count - 1], "url"))) : null;

    public static bool TryDuration(string s, out TimeSpan d)
    {
        d = default;
        var parts = s.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return false;
        var n = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out n[i])) return false;
        d = parts.Length == 2 ? new TimeSpan(0, n[0], n[1]) : new TimeSpan(n[0], n[1], n[2]);
        return true;
    }

    private static JsonNode? G(JsonNode? n, string key) => n is JsonObject o ? o[key] : null;

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static IEnumerable<JsonNode> FindShelves(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (k, v) in obj)
                {
                    if (v is null) continue;
                    if (Array.IndexOf(ShelfKeys, k) >= 0) yield return v;
                    else foreach (var f in FindShelves(v)) yield return f;
                }
                break;
            case JsonArray arr:
                foreach (var v in arr)
                    foreach (var f in FindShelves(v)) yield return f;
                break;
        }
    }

    private static IEnumerable<JsonNode> FindAll(JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (k, v) in obj)
                {
                    if (v is null) continue;
                    if (k == key) yield return v;
                    foreach (var f in FindAll(v, key)) yield return f;
                }
                break;
            case JsonArray arr:
                foreach (var v in arr)
                    foreach (var f in FindAll(v, key)) yield return f;
                break;
        }
    }

    private static bool IsExplicit(JsonNode? node)
    {
        foreach (var iconType in FindAll(node, "iconType"))
            if (Str(iconType) == "MUSIC_EXPLICIT_BADGE") return true;
        return false;
    }

    private static JsonNode? FindFirst(JsonNode? node, string key) => FindAll(node, key).FirstOrDefault();
}
