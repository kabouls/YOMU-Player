using YomuPlayer.Core;

if (args.Length < 1 || (args[0] is not ("dump" or "session") && args.Length < 2))
{
    Console.WriteLine("usage: search <query> | resolve <videoId> [high|balanced|saver]");
    Console.WriteLine("       next <videoId> | related <videoId> | session");
    return 1;
}

try
{
    switch (args[0])
    {
        case "next":
        {
            using var yt = new InnerTubeClient();
            var extras = await yt.GetPlayerExtrasAsync(args[1]);
            Console.WriteLine($"lyrics={extras.LyricsBrowseId ?? "-"} related={extras.RelatedBrowseId ?? "-"}");

            if (extras.LyricsBrowseId is { } lyricsId)
            {
                var lyrics = await yt.GetLyricsAsync(lyricsId);
                Console.WriteLine($"footer={lyrics?.Footer ?? "-"}");
                Console.WriteLine($"lines={(lyrics is null ? 0 : lyrics.Text.Split('\n').Length)}");
                Console.WriteLine(lyrics?.Text);
            }
            return 0;
        }
        case "related":
        {
            using var yt = new InnerTubeClient();
            var extras = await yt.GetPlayerExtrasAsync(args[1]);
            if (extras.RelatedBrowseId is null)
            {
                Console.WriteLine("no related page");
                return 2;
            }
            var page = await yt.BrowseAsync(extras.RelatedBrowseId);
            Console.WriteLine($"title={page.Title} shelves={page.Shelves.Count}");
            foreach (var shelf in page.Shelves)
            {
                Console.WriteLine($"[{shelf.Style}] {shelf.Title}: {shelf.Items.Count}");
                foreach (var item in shelf.Items.Take(4))
                    Console.WriteLine($"   {item.Kind,-8} {item.Title} | {item.Subtitle}");
            }
            return 0;
        }
        case "session":
        {
            // Round-trip the remote-control contract without a player: the wire format must survive serialization.
            PlaybackCommand command = new PlaybackCommand.SeekTo(91_000);
            var json = System.Text.Json.JsonSerializer.Serialize<PlaybackCommand>(command);
            var back = System.Text.Json.JsonSerializer.Deserialize<PlaybackCommand>(json);
            Console.WriteLine(json);
            Console.WriteLine(back is PlaybackCommand.SeekTo seek ? $"seekTo {seek.PositionMs}" : "round-trip failed");
            return back is PlaybackCommand.SeekTo ? 0 : 2;
        }
        case "search":
        {
            using var yt = new InnerTubeClient();
            var tracks = await yt.SearchSongsAsync(string.Join(' ', args.Skip(1)));
            Console.WriteLine($"{tracks.Count} results");
            foreach (var t in tracks)
                Console.WriteLine($"{t.VideoId}  {t.Title} - {t.ArtistLine}  [{t.Album}]  {t.Duration}");
            return tracks.Count > 0 ? 0 : 2;
        }
        case "parse":
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(args[1]))!;
            foreach (var t in InnerTubeClient.ParseSongs(node))
                Console.WriteLine($"{t.VideoId}  {t.Title} - {t.ArtistLine}  [{t.Album}]  {t.Duration}");
            return 0;
        }
        case "parse-dump":
        {
            var dir = args[1];
            Console.WriteLine("-- browse pages");
            foreach (var name in new[] { "home", "explore", "new_releases", "charts", "moods" })
            {
                var page = YtmParser.ParseBrowse(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, name + ".json")))!);
                Console.WriteLine($"{name}: title='{page.Title}' shelves={page.Shelves.Count}");
                foreach (var sh in page.Shelves)
                {
                    var f = sh.Items[0];
                    Console.WriteLine($"   [{sh.Style}] '{sh.Title}' x{sh.Items.Count}  first: {f.Kind} | {f.Title} | {f.Subtitle} | vid={f.VideoId} pl={f.PlaylistId} br={f.BrowseId}");
                }
            }
            Console.WriteLine("-- search_all");
            var res = YtmParser.ParseSearchAll(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "search_all.json")))!);
            foreach (var m in res.Take(14)) Console.WriteLine($"   {m.Kind,-8} {m.Title} | {m.Subtitle} | vid={m.VideoId} br={m.BrowseId}");
            Console.WriteLine($"   total {res.Count}");
            Console.WriteLine("-- queue (next_radio)");
            var q = YtmParser.ParseQueue(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "next_radio.json")))!);
            foreach (var t in q.Take(5)) Console.WriteLine($"   {t.VideoId} {t.Title} - {t.ArtistLine} [{t.Album}] {t.Duration}");
            Console.WriteLine($"   total {q.Count}");
            return 0;
        }
        case "dump":
        {
            var dir = args.Length > 1 ? args[1] : "dump";
            Directory.CreateDirectory(dir);
            using var yt = new InnerTubeClient();
            var jobs = new (string Name, Func<Task<System.Text.Json.Nodes.JsonNode>> Run)[]
            {
                ("home", () => yt.BrowseRawAsync("FEmusic_home")),
                ("explore", () => yt.BrowseRawAsync("FEmusic_explore")),
                ("new_releases", () => yt.BrowseRawAsync("FEmusic_new_releases_albums")),
                ("charts", () => yt.BrowseRawAsync("FEmusic_charts")),
                ("moods", () => yt.BrowseRawAsync("FEmusic_moods_and_genres")),
                ("search_all", () => yt.SearchRawAsync("daft punk")),
                ("next_radio", () => yt.NextRawAsync("fa5IWHDbftI")),
                ("suggest", () => yt.SuggestionsRawAsync("daft pu")),
                ("artist", () => yt.BrowseRawAsync("UCRr1xG_2WIDs18a6cIiCxeA")),
                ("playlist", () => yt.BrowseRawAsync("VLPL-3zGTN3zNztvhU2Xb1vm6_KmlSXPq42s")),
            };
            foreach (var (name, run) in jobs)
            {
                try
                {
                    var node = await run();
                    if (node is System.Text.Json.Nodes.JsonObject o) o.Remove("responseContext");
                    var path = Path.Combine(dir, name + ".json");
                    File.WriteAllText(path, node.ToJsonString());
                    Console.WriteLine($"ok   {name}  {new FileInfo(path).Length / 1024} KB");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"FAIL {name}: {e.Message.Split('\n')[0]}");
                }
            }
            Console.WriteLine("saved to " + Path.GetFullPath(dir));
            return 0;
        }
        case "bench":
        {
            Console.WriteLine($"yt-dlp: {new StreamResolver().YtDlpPath}");
            foreach (var client in new string?[] { null, "tv", "tv_simply", "tv_embedded", "web_embedded", "mweb", "android_vr" })
            {
                var label = (client ?? "default").PadRight(13);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var s = await new StreamResolver { PlayerClient = client }.ResolveAsync(args[1], AudioQuality.Balanced);
                    Console.WriteLine($"{label} total={sw.ElapsedMilliseconds,5} ms  extract={s.ExtractMs,5}  warmup={s.WarmupMs,5}  {s.Codec} {s.BitrateKbps:0}k");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{label} FAILED after {sw.ElapsedMilliseconds} ms: {e.Message.Split('\n')[0]}");
                }
            }
            return 0;
        }
        case "probe":
        {
            using var http = new HttpClient();
            foreach (var client in new string?[] { null, "tv", "web_safari", "android_vr", "ios", "mweb" })
            {
                var r = new StreamResolver { PlayerClient = client };
                var label = (client ?? "default").PadRight(11);
                try
                {
                    var s = await r.ResolveAsync(args[1], AudioQuality.High);
                    var itag = System.Web.HttpUtility.ParseQueryString(new Uri(s.Url).Query)["itag"];
                    using var req = new HttpRequestMessage(HttpMethod.Get, s.Url);
                    foreach (var (k, v) in s.Headers) req.Headers.TryAddWithoutValidation(k, v);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
                    using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    Console.WriteLine($"{label} itag={itag} host={new Uri(s.Url).Host} http={(int)res.StatusCode}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{label} failed: {e.Message.Split('\n')[0]}");
                }
                if (client is null)
                    foreach (var line in (r.LastStderr ?? "").Split('\n').Where(l => l.Contains("WARNING") || l.Contains("ERROR")))
                        Console.WriteLine("  yt-dlp: " + line.Trim());
            }
            Console.WriteLine();
            Console.WriteLine("request-shape test on a fresh default-client URL:");
            var fresh = await new StreamResolver().ResolveAsync(args[1], AudioQuality.High);
            Console.WriteLine($"  t+0s  range 0-1023             : {await Probe.GetAsync(http, fresh, "bytes=0-1023")}");
            await Task.Delay(5000);
            Console.WriteLine($"  t+5s  range 0-1023             : {await Probe.GetAsync(http, fresh, "bytes=0-1023")}");
            Console.WriteLine($"  t+5s  range 0-10485759 (ytdlp) : {await Probe.GetAsync(http, fresh, "bytes=0-10485759")}");
            Console.WriteLine($"  t+5s  no range header          : {await Probe.GetAsync(http, fresh, null)}");
            Console.WriteLine($"  t+5s  VLC user-agent           : {await Probe.GetAsync(http, fresh, "bytes=0-1023", "VLC/3.0.24 LibVLC/3.0.24")}");
            Console.WriteLine($"  t+5s  no yt-dlp headers        : {await Probe.GetAsync(http, fresh, "bytes=0-1023", null, false)}");
            return 0;
        }
        case "resolve":
        {
            var q = args.ElementAtOrDefault(2) switch
            {
                "balanced" => AudioQuality.Balanced,
                "saver" => AudioQuality.DataSaver,
                _ => AudioQuality.High
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = await new StreamResolver().ResolveAsync(args[1], q);
            Console.WriteLine($"resolved in {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"codec={s.Codec} abr={s.BitrateKbps} extract={s.ExtractMs}ms warmup={s.WarmupMs}ms expires={s.ExpiresAt:u}");
            Console.WriteLine(s.Url);
            return 0;
        }
        default:
            Console.WriteLine("unknown command");
            return 1;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    return 3;
}

static class Probe
{
    public static async Task<string> GetAsync(HttpClient http, ResolvedStream s, string? range, string? userAgent = null, bool ytDlpHeaders = true)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, s.Url);
            if (ytDlpHeaders)
                foreach (var (k, v) in s.Headers) req.Headers.TryAddWithoutValidation(k, v);
            if (userAgent is not null)
            {
                req.Headers.Remove("User-Agent");
                req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            }
            if (range is not null) req.Headers.TryAddWithoutValidation("Range", range);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            return ((int)res.StatusCode).ToString();
        }
        catch (Exception e)
        {
            return "error: " + e.Message;
        }
    }
}
