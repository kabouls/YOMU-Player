using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Web;

namespace YomuPlayer.Core;

public sealed class StreamResolver : IStreamResolver
{
    private readonly string _ytDlpPath;
    private static readonly HttpClient Http = new();
    private const int MaxCachedStreams = 64;
    private readonly ConcurrentDictionary<string, ResolvedStream> _cache = new();
    private readonly ConcurrentDictionary<string, Task<ResolvedStream>> _inflight = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();

    public StreamResolver(string? ytDlpPath = null)
    {
        _ytDlpPath = ytDlpPath ?? LocateYtDlp();
    }

    public string? CookiesFile { get; set; }
    public string? PlayerClient { get; set; }
    public string? LastStderr { get; private set; }

    public async Task<ResolvedStream> ResolveAsync(
        string videoId, AudioQuality quality = AudioQuality.High, CancellationToken ct = default)
    {
        var key = $"{videoId}:{quality}:{PlayerClient}";
        if (_cache.TryGetValue(key, out var hit) && hit.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10))
            return hit;

        // Share one yt-dlp run between prefetch and a real play request; a cancelled caller doesn't kill it.
        var task = _inflight.GetOrAdd(key, _ => ResolveCoreAsync(key, videoId, quality));
        return await task.WaitAsync(ct);
    }

    public void Invalidate(string videoId)
    {
        foreach (var key in _cache.Keys.Where(k => k.StartsWith(videoId + ":")))
            _cache.TryRemove(key, out _);
    }

    private async Task<ResolvedStream> ResolveCoreAsync(string key, string videoId, AudioQuality quality)
    {
        await Task.Yield();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var args = new List<string>
            {
                "--ignore-config", "--dump-single-json", "--no-playlist", "--skip-download",
                "-f", FormatSelector(quality),
                $"https://music.youtube.com/watch?v={videoId}"
            };
            if (PlayerClient is not null)
            {
                args.Insert(0, $"youtube:player_client={PlayerClient}");
                args.Insert(0, "--extractor-args");
            }
            if (CookiesFile is not null)
            {
                args.Insert(0, CookiesFile);
                args.Insert(0, "--cookies");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var json = await RunAsync(args, cts.Token);
            var extractMs = (int)sw.ElapsedMilliseconds;
            var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("yt-dlp returned no JSON");

            var url = node["url"]?.GetValue<string>()
                      ?? throw new InvalidOperationException("yt-dlp JSON has no url");
            var headers = new Dictionary<string, string>();
            if (node["http_headers"] is JsonObject h)
                foreach (var (k, v) in h)
                    if (v is not null) headers[k] = v.GetValue<string>();

            var stream = new ResolvedStream(
                videoId, url,
                node["acodec"]?.GetValue<string>(),
                node["abr"]?.GetValue<double>(),
                ParseExpiry(url), headers, extractMs);

            // Fresh googlevideo URLs answer 403 for a few seconds after yt-dlp mints them (yt-dlp itself sleeps ~4 s).
            // Poll until the URL is accepted so callers only ever receive playable streams; prefetch hides this wait.
            sw.Restart();
            await WaitUntilAcceptedAsync(stream, cts.Token);
            stream = stream with { WarmupMs = (int)sw.ElapsedMilliseconds };

            _cache[key] = stream;
            _cacheOrder.Enqueue(key);
            while (_cache.Count > MaxCachedStreams && _cacheOrder.TryDequeue(out var old))
                _cache.TryRemove(old, out _);
            return stream;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public Task PrefetchAsync(string videoId, AudioQuality quality = AudioQuality.High) =>
        ResolveAsync(videoId, quality);

    private static async Task WaitUntilAcceptedAsync(ResolvedStream s, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, s.Url);
            foreach (var (k, v) in s.Headers) req.Headers.TryAddWithoutValidation(k, v);
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
            try
            {
                using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (res.IsSuccessStatusCode) return;
                if ((int)res.StatusCode != 403) return; // anything else: let the player report it
            }
            catch (HttpRequestException)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException("YouTube kept rejecting the stream URL (HTTP 403)");
            await Task.Delay(250, ct);
        }
    }

    private static string FormatSelector(AudioQuality q) => q switch
    {
        AudioQuality.High => "bestaudio/best",
        AudioQuality.Balanced => "bestaudio[abr<=100]/bestaudio[abr<=130]/bestaudio/best",
        AudioQuality.DataSaver => "bestaudio[abr<=60]/worstaudio/worst",
        _ => "bestaudio/best"
    };

    private static DateTimeOffset ParseExpiry(string url)
    {
        var qs = HttpUtility.ParseQueryString(new Uri(url).Query);
        return long.TryParse(qs["expire"], out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : DateTimeOffset.UtcNow.AddHours(5);
    }

    private async Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ytDlpPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start yt-dlp");
        try { p.PriorityClass = ProcessPriorityClass.Idle; } catch { /* process may have exited */ }
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        var err = await stderr;
        LastStderr = err;
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp exited {p.ExitCode}: {err}");
        return await stdout;
    }

    // Preference: the faster-starting "onedir" build installed by tools\install-fast-ytdlp.ps1, then a copy next to
    // the app, then whatever is on PATH.
    private static string LocateYtDlp()
    {
        var fast = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YomuPlayer", "yt-dlp", "yt-dlp.exe");
        if (File.Exists(fast)) return fast;
        var local = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        return File.Exists(local) ? local : "yt-dlp";
    }

    public string YtDlpPath => _ytDlpPath;
}
