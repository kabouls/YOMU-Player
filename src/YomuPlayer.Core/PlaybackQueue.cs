namespace YomuPlayer.Core;

public enum RepeatMode { Off, All, One }

// One row of the player page's queue: the position in play order (which differs from the track index when shuffle
// is on), the track, and whether it is the one playing now.
public sealed record QueueEntry(int OrderIndex, Track Track, bool IsCurrent);

public sealed class PlaybackQueue
{
    private readonly List<Track> _tracks = new();
    private readonly Random _rng = new();
    private List<int> _order = new();
    private int _pos = -1;
    private bool _shuffle;

    public event Action? Changed;

    public IReadOnlyList<Track> Tracks => _tracks;
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    // Displayed as "Playing from ..." on the player page. Set when a radio or collection starts.
    public string? SourceTitle { get; set; }

    public Track? Current => _pos >= 0 && _pos < _order.Count ? _tracks[_order[_pos]] : null;

    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            Rebuild(_pos >= 0 && _pos < _order.Count ? _order[_pos] : -1);
            Changed?.Invoke();
        }
    }

    public void Set(IReadOnlyList<Track> tracks, int startIndex = 0)
    {
        _tracks.Clear();
        _tracks.AddRange(tracks);
        Rebuild(_tracks.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _tracks.Count - 1));
        Changed?.Invoke();
    }

    public void Add(Track track)
    {
        _tracks.Add(track);
        _order.Add(_tracks.Count - 1);
        if (_pos < 0) _pos = 0;
        Changed?.Invoke();
    }

    public void PlayNext(Track track)
    {
        _tracks.Add(track);
        _order.Insert(Math.Min(_pos + 1, _order.Count), _tracks.Count - 1);
        if (_pos < 0) _pos = 0;
        Changed?.Invoke();
    }

    public int Position => _pos;

    // Tracks in play order (shuffled order when shuffle is on).
    public IReadOnlyList<Track> OrderedTracks => _order.Select(i => _tracks[i]).ToList();

    // The current track followed by everything still to come, in play order. This is what "Up next" shows.
    public IReadOnlyList<QueueEntry> Upcoming()
    {
        if (_pos < 0 || _pos >= _order.Count) return Array.Empty<QueueEntry>();
        var list = new List<QueueEntry>(_order.Count - _pos);
        for (var i = _pos; i < _order.Count; i++)
            list.Add(new QueueEntry(i, _tracks[_order[i]], i == _pos));
        return list;
    }

    public int UpcomingCount => _pos < 0 ? 0 : _order.Count - _pos;

    // Jumps to a position in play order (what the queue list shows), not a track index.
    public bool MoveToOrder(int orderIndex)
    {
        if (orderIndex < 0 || orderIndex >= _order.Count) return false;
        _pos = orderIndex;
        Changed?.Invoke();
        return true;
    }

    // Removes an upcoming row. The current track cannot be removed: it is playing, and dropping it would leave
    // playback pointing at nothing while the audio continued.
    public bool RemoveAt(int orderIndex)
    {
        if (orderIndex < 0 || orderIndex >= _order.Count || orderIndex == _pos) return false;

        var trackIndex = _order[orderIndex];
        _order.RemoveAt(orderIndex);
        _tracks.RemoveAt(trackIndex);
        for (var i = 0; i < _order.Count; i++)
            if (_order[i] > trackIndex) _order[i]--;
        if (orderIndex < _pos) _pos--;
        if (_order.Count == 0) _pos = -1;

        Changed?.Invoke();
        return true;
    }

    // A disliked track must not come back later in this queue. The current row is kept until playback moves on.
    public void RemoveUpcoming(string videoId) =>
        RemoveUpcomingWhere(t => t.VideoId == videoId);

    public void RemoveUpcomingWhere(Func<Track, bool> predicate)
    {
        if (_pos < 0) return;
        for (var i = _order.Count - 1; i > _pos; i--)
            if (predicate(_tracks[_order[i]]))
                RemoveAt(i);
    }

    // Called after skipping a disliked current track; drop its other occurrences without removing the new current.
    public void RemoveVideoExceptCurrent(string videoId)
    {
        for (var i = _order.Count - 1; i >= 0; i--)
            if (i != _pos && _tracks[_order[i]].VideoId == videoId)
                RemoveAt(i);
    }

    public void AppendRange(IEnumerable<Track> tracks)
    {
        var added = false;
        foreach (var t in tracks)
        {
            _tracks.Add(t);
            _order.Add(_tracks.Count - 1);
            added = true;
        }
        if (added && _pos < 0) _pos = 0;
        if (added) Changed?.Invoke();
    }

    // Keeps the current track, replaces everything after it (used when a song's radio arrives).
    public void ReplaceAfterCurrent(IReadOnlyList<Track> upcoming)
    {
        var cur = Current;
        if (cur is null)
        {
            Set(upcoming);
            return;
        }
        _tracks.Clear();
        _tracks.Add(cur);
        _tracks.AddRange(upcoming.Where(t => t.VideoId != cur.VideoId));
        Rebuild(0);
        Changed?.Invoke();
    }

    public void InsertAfterCurrent(IEnumerable<Track> tracks)
    {
        var at = Math.Min(_pos + 1, _order.Count);
        var added = false;
        foreach (var t in tracks)
        {
            _tracks.Add(t);
            _order.Insert(at++, _tracks.Count - 1);
            added = true;
        }
        if (added && _pos < 0) _pos = 0;
        if (added) Changed?.Invoke();
    }

    public bool MoveTo(int trackIndex)
    {
        var p = _order.IndexOf(trackIndex);
        if (p < 0) return false;
        _pos = p;
        Changed?.Invoke();
        return true;
    }

    public Track? PeekNext()
    {
        var p = NextPos();
        return p is null ? null : _tracks[_order[p.Value]];
    }

    public bool MoveNext()
    {
        var p = NextPos();
        if (p is null) return false;
        _pos = p.Value;
        Changed?.Invoke();
        return true;
    }

    public bool MovePrevious()
    {
        if (_order.Count == 0) return false;
        if (_pos > 0) _pos--;
        else if (Repeat == RepeatMode.All) _pos = _order.Count - 1;
        else return false;
        Changed?.Invoke();
        return true;
    }

    private int? NextPos()
    {
        if (_order.Count == 0) return null;
        if (_pos + 1 < _order.Count) return _pos + 1;
        return Repeat == RepeatMode.All ? 0 : null;
    }

    private void Rebuild(int currentTrackIndex)
    {
        _order = Enumerable.Range(0, _tracks.Count).ToList();
        if (_shuffle && _order.Count > 1)
        {
            for (var i = _order.Count - 1; i > 0; i--)
            {
                var j = _rng.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
            if (currentTrackIndex >= 0)
            {
                _order.Remove(currentTrackIndex);
                _order.Insert(0, currentTrackIndex);
            }
            _pos = 0;
        }
        else
        {
            _pos = currentTrackIndex;
        }
        if (_order.Count == 0) _pos = -1;
    }
}
