namespace YomuPlayer.Core;

public sealed class PlayerService : IDisposable
{
    private const int MaxFailStreak = 3;

    private readonly IStreamResolver _resolver;
    private readonly IAudioPlayer _player;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private string? _retriedVideoId;
    private int _failStreak;

    public PlayerService(IStreamResolver resolver, IAudioPlayer player, PlaybackQueue? queue = null)
    {
        _resolver = resolver;
        _player = player;
        Queue = queue ?? new PlaybackQueue();
        _player.Ended += OnEnded;
        _player.Error += OnPlayerError;
        _player.PositionChanged += OnPosition;
    }

    public PlaybackQueue Queue { get; }
    public AudioQuality Quality { get; set; } = AudioQuality.Balanced;

    private readonly List<Track> _sessionHistory = new();
    private Track? _lastStartedTrack;
    private const int MaxSessionHistory = 20;

    // Songs already played in this session, oldest to newest. This survives radio/queue replacement,
    // so the player page can show what led up to the current song.
    public IReadOnlyList<Track> SessionHistory
    {
        get
        {
            lock (_sessionHistory) return _sessionHistory.ToList();
        }
    }

    private void RememberStart(Track track)
    {
        lock (_sessionHistory)
        {
            if (_lastStartedTrack?.VideoId == track.VideoId) return;
            if (_lastStartedTrack is { } previous)
            {
                _sessionHistory.Add(previous);
                if (_sessionHistory.Count > MaxSessionHistory)
                    _sessionHistory.RemoveAt(0);
            }
            _lastStartedTrack = track;
        }
    }

    // Supplies "more like this" tracks. Used for song radio on click and for endless playback when the queue runs out.
    public Func<Track, Task<IReadOnlyList<Track>>>? RadioProvider { get; set; }

    // Final gate for every radio-generated queue insertion. A dislike can arrive while the radio request
    // is in flight, so filtering only the provider response is not enough.
    public Func<Track, bool>? TrackFilter { get; set; }

    // Supplies the queue's display title ("Playing from ...") after a radio response has been parsed.
    public Func<Track, string?>? QueueTitleProvider { get; set; }
    public bool Autoplay { get; set; } = true;

    public event Action<Track?>? TrackChanged;
    public event Action? StateChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<int>? VolumeChanged;

    public bool IsPlaying => _player.IsPlaying;

    // True from the moment a track is requested until audio is actually playing (or the request fails).
    // Lets the UI show a loading state instead of leaving the previous song's audio looking current.
    private volatile bool _loading;

    public bool IsLoading
    {
        get
        {
            if (_loading && _player.IsPlaying) _loading = false;
            return _loading;
        }
    }
    public TimeSpan Position => _player.Position;
    public TimeSpan? Duration => _player.Duration;

    public int Volume
    {
        get => _player.Volume;
        set
        {
            var volume = Math.Clamp(value, 0, 100);
            if (volume == _player.Volume) return;
            _player.Volume = volume;
            VolumeChanged?.Invoke(volume);
        }
    }

    public Task PlayQueueAsync(IReadOnlyList<Track> tracks, int startIndex = 0)
    {
        Queue.Set(tracks, startIndex);
        _failStreak = 0;
        return PlayCurrentAsync();
    }

    public async Task PlayTrackWithRadioAsync(Track track)
    {
        Queue.SourceTitle = track.Title + " Radio";
        var radio = RadioProvider?.Invoke(track);
        var play = PlayQueueAsync(new[] { track });
        if (radio is not null)
        {
            try
            {
                var list = await radio;
                if (Queue.Current?.VideoId == track.VideoId)
                {
                    Queue.ReplaceAfterCurrent(FilterGenerated(list));
                    Queue.SourceTitle = QueueTitleProvider?.Invoke(track) ?? Queue.SourceTitle;
                    _ = PrefetchNextAsync();
                }
            }
            catch
            {
                // radio is best effort
            }
        }
        await play;
    }

    // Warms a track's stream (e.g. while the user hovers it) so the click that follows starts almost instantly.
    // Bounded: if two warm-ups are already running, extra requests are skipped instead of queued.
    private readonly SemaphoreSlim _prefetchGate = new(2);

    public void Prefetch(string videoId)
    {
        _ = Task.Run(async () =>
        {
            if (!await _prefetchGate.WaitAsync(0)) return;
            try { await _resolver.PrefetchAsync(videoId, Quality); }
            catch { /* best effort */ }
            finally { _prefetchGate.Release(); }
        });
    }

    public async Task PlayIndexAsync(int trackIndex)
    {
        if (Queue.MoveTo(trackIndex)) await PlayCurrentAsync();
    }

    // The queue list works in play order (which shuffle changes), so it uses this instead of PlayIndexAsync.
    public async Task PlayOrderIndexAsync(int orderIndex)
    {
        if (Queue.MoveToOrder(orderIndex)) await PlayCurrentAsync();
    }

    public async Task NextAsync()
    {
        if (!Queue.MoveNext()) return;
        await PlayCurrentAsync();
    }

    // YouTube Music's thumbs-down skips immediately and the track does not return to this queue.
    public async Task SkipDislikedAsync(string videoId)
    {
        if (Queue.Current?.VideoId != videoId) return;

        Queue.RemoveUpcoming(videoId);
        if (!Queue.MoveNext())
        {
            await ExtendFromRadioAsync();
            if (!Queue.MoveNext())
            {
                _loading = false;
                _player.Stop();
                TrackChanged?.Invoke(null);
                StateChanged?.Invoke();
                return;
            }
        }

        Queue.RemoveVideoExceptCurrent(videoId);
        await PlayCurrentAsync();
    }

    public async Task PreviousAsync()
    {
        if (_player.Position > TimeSpan.FromSeconds(3) || !Queue.MovePrevious())
        {
            _player.Seek(TimeSpan.Zero);
            return;
        }
        await PlayCurrentAsync();
    }

    public void TogglePause()
    {
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
        StateChanged?.Invoke();
    }

    public void Seek(TimeSpan position) => _player.Seek(position);

    public void SeekRelative(TimeSpan delta)
    {
        var target = _player.Position + delta;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        var dur = _player.Duration ?? Queue.Current?.Duration;
        if (dur is { } d && target > d - TimeSpan.FromSeconds(1)) target = d - TimeSpan.FromSeconds(1);
        _player.Seek(target);
    }

    public void SeekFraction(double fraction)
    {
        var dur = _player.Duration ?? Queue.Current?.Duration;
        if (dur is { } d) _player.Seek(TimeSpan.FromSeconds(d.TotalSeconds * Math.Clamp(fraction, 0, 0.99)));
    }

    private int _premuteVolume = 70;

    public void ToggleMute()
    {
        if (Volume > 0)
        {
            _premuteVolume = Volume;
            Volume = 0;
        }
        else
        {
            Volume = _premuteVolume > 0 ? _premuteVolume : 70;
        }
    }

    public async Task AddNextAsync(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        if (Queue.Current is null) { await PlayQueueAsync(tracks); return; }
        Queue.InsertAfterCurrent(tracks);
        _ = PrefetchNextAsync();
    }

    public async Task AddToQueueAsync(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        if (Queue.Current is null) { await PlayQueueAsync(tracks); return; }
        Queue.AppendRange(tracks);
    }

    private IReadOnlyList<Track> FilterGenerated(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        return TrackFilter is null ? list : list.Where(t => !TrackFilter(t)).ToList();
    }

    private async Task PlayCurrentAsync(TimeSpan? resumeAt = null, bool isRetry = false)
    {
        var track = Queue.Current;
        if (track is null)
        {
            _loading = false;
            _player.Stop();
            TrackChanged?.Invoke(null);
            StateChanged?.Invoke();
            return;
        }

        RememberStart(track);
        if (!isRetry) _retriedVideoId = null;

        CancellationTokenSource cts;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = cts = new CancellationTokenSource();
        }

        try
        {
            // Silence the previous song right away: the new one takes a few seconds to resolve,
            // and the UI already shows the new track.
            _player.Stop();
            _loading = true;
            TrackChanged?.Invoke(track);
            var stream = await _resolver.ResolveAsync(track.VideoId, Quality, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            _player.Load(stream);
            _player.Play();
            if (resumeAt is { } pos && pos > TimeSpan.Zero) _player.Seek(pos);

            StateChanged?.Invoke();
            _ = PrefetchNextAsync();
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer request
        }
        catch (Exception e)
        {
            if (!cts.IsCancellationRequested)
                await HandleFailureAsync($"{track.Title}: {e.Message}");
        }
    }

    private Task? _extendTask;

    private bool CanExtend => Autoplay && RadioProvider is not null && Queue.Repeat == RepeatMode.Off;

    // One radio fetch at a time; callers (prefetch, end-of-queue) share the in-flight task.
    private Task ExtendFromRadioAsync()
    {
        lock (_gate)
        {
            if (!CanExtend) return Task.CompletedTask;
            return _extendTask ??= ExtendCoreAsync();
        }
    }

    private async Task ExtendCoreAsync()
    {
        await Task.Yield();
        try
        {
            var seed = Queue.Current;
            if (seed is null) return;
            var more = await RadioProvider!(seed);
            var known = Queue.Tracks.Select(t => t.VideoId).ToHashSet();
            Queue.AppendRange(FilterGenerated(more).Where(t => known.Add(t.VideoId)));
        }
        catch
        {
            // best effort
        }
        finally
        {
            lock (_gate) { _extendTask = null; }
        }
    }

    private async Task PrefetchNextAsync()
    {
        if (Queue.Repeat == RepeatMode.One) return;
        var next = Queue.PeekNext();
        if (next is null && CanExtend)
        {
            await ExtendFromRadioAsync();
            next = Queue.PeekNext();
        }
        if (next is null) return;
        try { await _resolver.PrefetchAsync(next.VideoId, Quality); }
        catch { /* best effort */ }
    }

    private async Task HandleFailureAsync(string message)
    {
        ErrorOccurred?.Invoke(message);
        if (++_failStreak < MaxFailStreak && Queue.MoveNext())
        {
            await PlayCurrentAsync();
        }
        else
        {
            _loading = false;
            _player.Stop();
            StateChanged?.Invoke();
        }
    }

    private void OnEnded() => _ = Task.Run(async () =>
    {
        if (Queue.Repeat == RepeatMode.One)
        {
            await PlayCurrentAsync();
        }
        else if (Queue.MoveNext())
        {
            await PlayCurrentAsync();
        }
        else
        {
            await ExtendFromRadioAsync();
            if (Queue.MoveNext())
            {
                await PlayCurrentAsync();
            }
            else
            {
                _loading = false;
                _player.Stop();
                StateChanged?.Invoke();
            }
        }
    });

    private void OnPlayerError(string message) => _ = Task.Run(async () =>
    {
        var track = Queue.Current;
        if (track is null) return;

        // Most mid-playback failures are expired or revoked stream URLs: re-resolve once and resume.
        if (_retriedVideoId != track.VideoId)
        {
            _retriedVideoId = track.VideoId;
            var resumeAt = _player.Position;
            _resolver.Invalidate(track.VideoId);
            await PlayCurrentAsync(resumeAt, isRetry: true);
            return;
        }
        await HandleFailureAsync($"{track.Title}: {message}");
    });

    // Only real playback progress counts as success; Load/Play returning says nothing about a 403 arriving later.
    private void OnPosition(TimeSpan t)
    {
        if (t > TimeSpan.FromSeconds(2)) _failStreak = 0;
    }

    public void Dispose()
    {
        _player.PositionChanged -= OnPosition;
        _player.Ended -= OnEnded;
        _player.Error -= OnPlayerError;
        lock (_gate) { _cts?.Cancel(); }
    }
}
