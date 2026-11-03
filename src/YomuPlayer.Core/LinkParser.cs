using System.Text.RegularExpressions;

namespace YomuPlayer.Core;

public enum LinkKind { Song, Playlist, Album, Artist, ChannelName }

public enum LinkStatus { NotALink, Supported, Unsupported }

// Id is the video/playlist/browse/channel id; for ChannelName it is the full URL (@handle, /c/name, /user/name)
// that has to be resolved to a channel id over the network.
public sealed record LinkTarget(LinkKind Kind, string Id, string? PlaylistId = null);

// Understands links people share from music.youtube.com, youtube.com, youtu.be, the phone apps, and our own yomu:// scheme.
public static class LinkParser
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "music.youtube.com", "www.youtube.com", "youtube.com", "m.youtube.com", "youtu.be", "www.youtu.be",
        "youtube-nocookie.com", "www.youtube-nocookie.com"
    };

    // Tells "daft punk" (search text) apart from a link, and a supported link from one we can't open.
    public static LinkStatus Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LinkStatus.NotALink;
        var t = text.Trim();
        if (!LooksLikeLink(t)) return LinkStatus.NotALink;
        return Parse(t) is not null ? LinkStatus.Supported : LinkStatus.Unsupported;
    }

    public static bool LooksLikeLink(string t)
    {
        if (t.Contains(' ')) return false;
        if (Regex.IsMatch(t, @"^(https?://|yomu:|www\.)", RegexOptions.IgnoreCase)) return true;
        // "music.youtube.com/watch?v=..." without a scheme
        return t.Contains('/') && t.Split('/')[0].Contains('.') && Uri.TryCreate("https://" + t, UriKind.Absolute, out _);
    }

    public static LinkTarget? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().Trim('"', '\'', '<', '>');

        // yomu://open?u=<encoded url>   or   yomu:https://music.youtube.com/...
        const string scheme = "yomu:";
        if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            var rest = text[scheme.Length..].TrimStart('/');
            var q = Query(rest);
            text = q.TryGetValue("u", out var inner) ? inner : rest;
            if (rest.StartsWith("open", StringComparison.OrdinalIgnoreCase) && !q.ContainsKey("u")) return null;
        }

        if (!text.Contains("://") && text.Contains('.') && !text.Contains(' ')) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !Hosts.Contains(uri.Host)) return null;

        var result = ParseUri(uri);
        return result is not null && Valid(result) ? result : null;
    }

    // Ids must be complete: pasting is instant, but typing a URL by hand must not trigger on a half-typed id.
    private static bool Valid(LinkTarget t) => t.Kind switch
    {
        LinkKind.Song => Regex.IsMatch(t.Id, "^[A-Za-z0-9_-]{11}$"),
        LinkKind.Playlist => t.Id.Length >= 12 && Regex.IsMatch(t.Id, "^[A-Za-z0-9_-]+$"),
        LinkKind.Album => t.Id.StartsWith("MPRE") && t.Id.Length >= 12,
        LinkKind.Artist => Regex.IsMatch(t.Id, "^UC[A-Za-z0-9_-]{22}$"),
        LinkKind.ChannelName => t.Id.Length > 20,
        _ => false
    };

    private static LinkTarget? ParseUri(Uri uri)
    {
        var query = Query(uri.Query);
        var path = uri.AbsolutePath.Trim('/');
        query.TryGetValue("v", out var v);
        query.TryGetValue("list", out var list);

        if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase) && path.Length > 0)
            return new LinkTarget(LinkKind.Song, path.Split('/')[0], NullIfEmpty(list));

        if (path.Equals("watch", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))
            return new LinkTarget(LinkKind.Song, v, NullIfEmpty(list));

        foreach (var prefix in new[] { "shorts/", "embed/", "live/", "v/" })
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return new LinkTarget(LinkKind.Song, path[prefix.Length..].Split('/')[0], NullIfEmpty(list));

        if (path.Equals("playlist", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(list))
            return new LinkTarget(LinkKind.Playlist, list);

        if (path.StartsWith("channel/", StringComparison.OrdinalIgnoreCase))
            return new LinkTarget(LinkKind.Artist, path["channel/".Length..].Split('/')[0]);

        if (path.StartsWith("browse/", StringComparison.OrdinalIgnoreCase))
        {
            var id = path["browse/".Length..].Split('/')[0];
            if (id.StartsWith("MPRE")) return new LinkTarget(LinkKind.Album, id);
            if (id.StartsWith("UC")) return new LinkTarget(LinkKind.Artist, id);
            if (id.StartsWith("VL")) return new LinkTarget(LinkKind.Playlist, id[2..]);
        }

        // @handle, /c/name, /user/name: resolved to a channel id later
        if (path.StartsWith("@") || path.StartsWith("c/", StringComparison.OrdinalIgnoreCase)
                                 || path.StartsWith("user/", StringComparison.OrdinalIgnoreCase))
            return new LinkTarget(LinkKind.ChannelName, "https://www.youtube.com/" + path.Split('/')[0] + (path.StartsWith("@") ? "" : "/" + path.Split('/').ElementAtOrDefault(1)));

        return null;
    }

    private static Dictionary<string, string> Query(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = s.IndexOf('?');
        if (i >= 0) s = s[(i + 1)..];
        foreach (var part in s.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            var val = eq < 0 ? "" : part[(eq + 1)..];
            d[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(val.Replace('+', ' '));
        }
        return d;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
