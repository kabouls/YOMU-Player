using Microsoft.UI.Xaml.Controls;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YomuPlayer.Audio.Vlc;
using YomuPlayer.Core;
using YomuPlayer.Desktop.Auth;

namespace YomuPlayer.Desktop;

// Process-wide services shared by the window, pages and player bar.
public static class AppServices
{
    private static readonly Dictionary<string, (DateTime At, BrowsePage Page)> PageCache = new();
    private const int MaxPageCache = 12;
    private const int MaxCollectionCache = 12;
    private const int MaxSearchCache = 12;

    private static readonly Dictionary<string, (DateTime At, Task<IReadOnlyList<MediaItem>> Task)> SearchCache = new();
    private static readonly Dictionary<string, (DateTime At, CollectionData Data)> CollectionCache = new();
    private static readonly Dictionary<string, (DateTime At, CollectionPageData Data)> CollectionPageCache = new();
    private const int MaxCollectionPageCache = 12;
    private static readonly Dictionary<bool, (DateTime At, IReadOnlyList<Shelf> Shelves)> ForYouCache = new();
    private static readonly TimeSpan ForYouTtl = TimeSpan.FromMinutes(15);

    private sealed record ForYouDiskCache(DateTimeOffset At, List<Shelf> Shelves, int Cursor);

    private static string ForYouDiskPath(bool shuffle) => Path.Combine(
        AppPaths.DataRoot, shuffle ? "foryou-shuffle-v2.json" : "foryou-default-v2.json");
    private static readonly object LikedGate = new();
    private static Task<IReadOnlyList<Track>>? _likedTask;
    private static DateTime _likedAt;
    private static readonly TimeSpan LikedTtl = TimeSpan.FromMinutes(15);

    // Per-account thumbs-down feedback. The Data API rating is authoritative, while this local set also filters
    // the anonymous radio/InnerTube results so a disliked song cannot slip back into generated queues.
    private static readonly ConcurrentDictionary<string, (TrackRating Rating, DateTime At)> RatingCache = new();
    private static readonly HashSet<string> Disliked = new(StringComparer.Ordinal);
    private static readonly object DislikedGate = new();
    private static string? _dislikedAccount;
    private static readonly TimeSpan RatingTtl = TimeSpan.FromMinutes(5);
    public static event Action<string, TrackRating>? RatingChanged;
    public static event Action? LikedMusicChanged;

    // Player page content. These are small (a lyrics page is a few KB), so a handful per track is cheap.
    private static readonly Dictionary<string, (DateTime At, Task<PlayerExtras> Task)> ExtrasCache = new();
    private static readonly Dictionary<string, (DateTime At, Task<LyricsText?> Task)> LyricsCache = new();
    private static readonly Dictionary<string, (DateTime At, Task<BrowsePage?> Task)> RelatedCache = new();
    private static readonly TimeSpan ContentTtl = TimeSpan.FromMinutes(45);

    public static InnerTubeClient Yt { get; } = new();
    public static YouTubeDataApi Data { get; } = new();
    public static VlcAudioPlayer Audio { get; private set; } = null!;
    public static PlayerService Player { get; private set; } = null!;
    public static LocalPlaybackEndpoint LocalDevice { get; private set; } = null!;
    public static GoogleAuth Auth { get; private set; } = null!;

    private static readonly List<IPlaybackEndpoint> DeviceList = new();

    // Today this holds only this PC. A phone/TV endpoint added later shows up in the same list and flyout.
    public static IReadOnlyList<IPlaybackEndpoint> Devices => DeviceList;

    public static event Action<PageRequest>? NavigateRequested;
    public static event Action<string>? ErrorRaised;
    public static event Action<string>? InfoRaised;
    public static event Action<PlayerTab>? PlayerRequested;

    // Raised after a playlist is created or modified so the sidebar can refresh.
    public static event Action? LibraryChanged;

    // Raised after a playlist row is deleted; the open collection page removes it from its list.
    public static event Action<string>? TrackRemoved;

    private static readonly List<MediaItem> LibraryPlaylists = new();
    private static DateTime LibraryPlaylistsAt;
    private static bool _libraryPlaylistsLoaded;

    private static string LibraryPlaylistsDiskPath => Path.Combine(
        AppPaths.DataRoot, "library-playlists.json");

    private sealed record LibraryPlaylistsDiskCache(DateTimeOffset At, List<MediaItem> Playlists);

    public static MediaItem LikedMusic => new(MediaKind.Playlist, "Liked Music", "Auto playlist", null,
        PlaylistId: "LM", CanEdit: true);

    public static void Init()
    {
        Audio = new VlcAudioPlayer();
        var settings = AppSettings.Current;
        settings.ForYouSeedCursor = settings.ForYouSeedCursor == int.MaxValue ? 0 : settings.ForYouSeedCursor + 1;
        AppSettings.Save();
        Yt.Country = RegionCode(settings.Region);
        Player = new PlayerService(new StreamResolver(), Audio)
        {
            Volume = settings.Volume is > 0 and <= 100 ? settings.Volume : 70,
            Quality = (AudioQuality)Math.Clamp(settings.Quality, 0, 2),
            Autoplay = settings.Autoplay,
            RadioProvider = async seed => FilterRadio(MixRadio(await Yt.GetRadioAsync(seed.VideoId), seed)),
            QueueTitleProvider = track => Yt.TryGetCachedExtras(track.VideoId)?.QueueTitle
        };
        Player.TrackChanged += track => { if (track is not null) PlaybackHistory.Record(track); };

        LocalDevice = new LocalPlaybackEndpoint(Player, Environment.MachineName);
        DeviceList.Clear();
        DeviceList.Add(LocalDevice);

        Auth = new GoogleAuth();
        RefreshDislikedAccount();
        Auth.Changed += RefreshDislikedAccount;
        // Account library features use the official Data API. InnerTube now rejects OAuth access tokens with
        // 400 INVALID_ARGUMENT, so browsing and playback stay anonymous.
    }

    // Authenticated results (library playlists, liked songs, personal recommendations) must not survive sign-out
    // or an account switch.
    public static void ClearSearchCache()
    {
        lock (SearchCache) SearchCache.Clear();
    }

    public static void ClearCaches()
    {
        lock (PageCache) PageCache.Clear();
        lock (SearchCache) SearchCache.Clear();
        lock (CollectionCache) CollectionCache.Clear();
        lock (CollectionPageCache) CollectionPageCache.Clear();
        lock (ExtrasCache) ExtrasCache.Clear();
        lock (LyricsCache) LyricsCache.Clear();
        lock (RelatedCache) RelatedCache.Clear();
        ClearForYouCache();
        lock (LikedGate) { _likedTask = null; _likedAt = default; }
        RatingCache.Clear();
        Data.ClearAccountCache();
        LibraryPlaylists.Clear();
        LibraryPlaylistsAt = default;
        _libraryPlaylistsLoaded = false;
        try { File.Delete(LibraryPlaylistsDiskPath); } catch { /* optional cache */ }
    }

    public static void SetRegion(string region)
    {
        AppSettings.Current.Region = string.IsNullOrWhiteSpace(region) ? "auto" : region;
        AppSettings.Save();
        Yt.Country = RegionCode(AppSettings.Current.Region);
        lock (PageCache) PageCache.Clear();
        lock (SearchCache) SearchCache.Clear();
        lock (CollectionCache) CollectionCache.Clear();
        lock (CollectionPageCache) CollectionPageCache.Clear();
        ClearForYouCache();
    }

    public static string CurrentRegionLabel
    {
        get
        {
            var setting = AppSettings.Current.Region;
            if (string.Equals(setting, "worldwide", StringComparison.OrdinalIgnoreCase)) return "Worldwide";
            if (string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase))
                return "Auto (" + (RegionCode("auto") ?? "system") + ")";
            return setting.ToUpperInvariant();
        }
    }

    private static string? RegionCode(string? setting)
    {
        if (string.Equals(setting, "worldwide", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrWhiteSpace(setting) && !string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase))
            return setting.Trim().ToUpperInvariant();
        try
        {
            var code = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return code.Length == 2 ? code.ToUpperInvariant() : "US";
        }
        catch
        {
            return "US";
        }
    }

    public static async Task<IReadOnlyList<MediaItem>> GetGenresAsync()
    {
        // The moods page is two chip shelves: moods/moments plus genres.
        var page = await GetBrowseAsync("FEmusic_moods_and_genres", null);
        return page.Shelves
            .SelectMany(s => s.Items)
            .Where(i => i.Kind == MediaKind.Nav && !string.IsNullOrWhiteSpace(i.BrowseId))
            .GroupBy(i => i.Title)
            .Select(g => g.First())
            .OrderBy(i => i.Title)
            .ToList();
    }

    public static void SetGenre(MediaItem? genre)
    {
        AppSettings.Current.GenreTitle = genre?.Title;
        AppSettings.Current.GenreBrowseId = genre?.BrowseId;
        AppSettings.Current.GenreParams = genre?.BrowseParams;
        AppSettings.Save();
    }

    public static async Task<IReadOnlyList<Shelf>> GetGenreShelvesAsync()
    {
        var settings = AppSettings.Current;
        if (string.IsNullOrWhiteSpace(settings.GenreBrowseId)) return Array.Empty<Shelf>();
        var page = await GetBrowseAsync(settings.GenreBrowseId, settings.GenreParams);
        return page.Shelves.Take(2).ToList();
    }

    public static async Task<IReadOnlyList<MediaItem>> GetLibraryPlaylistsAsync(bool refresh = false)
    {
        if (!refresh && _libraryPlaylistsLoaded
            && DateTime.UtcNow - LibraryPlaylistsAt < TimeSpan.FromMinutes(2))
            return LibraryPlaylists;

        if (!Auth.IsSignedIn) return Array.Empty<MediaItem>();

        if (!refresh)
        {
            try
            {
                if (File.Exists(LibraryPlaylistsDiskPath))
                {
                    var disk = JsonSerializer.Deserialize<LibraryPlaylistsDiskCache>(
                        await File.ReadAllTextAsync(LibraryPlaylistsDiskPath));
                    if (disk is not null && DateTimeOffset.UtcNow - disk.At < TimeSpan.FromMinutes(15))
                    {
                        LibraryPlaylists.Clear();
                        LibraryPlaylists.AddRange(disk.Playlists);
                        LibraryPlaylistsAt = DateTime.UtcNow;
                        _libraryPlaylistsLoaded = true;
                        return LibraryPlaylists;
                    }
                }
            }
            catch
            {
                // fall back to the Data API
            }
        }

        var playlists = await Data.GetMyPlaylistsAsync(await LibraryTokenAsync());
        LibraryPlaylists.Clear();
        LibraryPlaylists.AddRange(playlists);
        LibraryPlaylistsAt = DateTime.UtcNow;
        _libraryPlaylistsLoaded = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LibraryPlaylistsDiskPath)!);
            await File.WriteAllTextAsync(LibraryPlaylistsDiskPath,
                JsonSerializer.Serialize(new LibraryPlaylistsDiskCache(DateTimeOffset.UtcNow, LibraryPlaylists)));
        }
        catch
        {
            // optional cache
        }
        return LibraryPlaylists;
    }

    public static async Task<MediaItem> CreatePlaylistAsync(string title, IReadOnlyList<Track>? tracks = null)
    {
        var token = await LibraryTokenAsync();
        var id = await Data.CreatePlaylistAsync(token, title);
        try
        {
            if (tracks is { Count: > 0 })
                await Data.AddToPlaylistAsync(token, id, tracks.Select(t => t.VideoId));
        }
        finally
        {
            _libraryPlaylistsLoaded = false;
            try { File.Delete(LibraryPlaylistsDiskPath); } catch { /* optional cache */ }
            lock (CollectionCache) CollectionCache.Clear();
        lock (CollectionPageCache) CollectionPageCache.Clear();
            LibraryChanged?.Invoke();
        }
        return new MediaItem(MediaKind.Playlist, title, "Playlist", tracks?.FirstOrDefault()?.ThumbnailUrl,
            PlaylistId: id, CanEdit: true);
    }

    public static async Task AddToPlaylistAsync(MediaItem playlist, IReadOnlyList<Track> tracks)
    {
        if (playlist.PlaylistId is not { } id)
            throw new InvalidOperationException("That playlist cannot be edited.");
        await Data.AddToPlaylistAsync(await LibraryTokenAsync(), id, tracks.Select(t => t.VideoId));
        _libraryPlaylistsLoaded = false;
        lock (CollectionCache) CollectionCache.Clear();
        lock (CollectionPageCache) CollectionPageCache.Clear();
        ClearForYouCache();
        LibraryChanged?.Invoke();
    }

    public static Task LikeTrackAsync(Track track) => SetTrackRatingAsync(track, TrackRating.Like);

    public static async Task<TrackRating> GetTrackRatingAsync(Track track, CancellationToken ct = default)
    {
        if (!Auth.IsSignedIn) return TrackRating.None;
        if (IsDisliked(track.VideoId)) return TrackRating.Dislike;

        if (RatingCache.TryGetValue(track.VideoId, out var hit)
            && DateTime.UtcNow - hit.At < RatingTtl)
            return hit.Rating;

        try
        {
            var rating = await Data.GetVideoRatingAsync(await LibraryTokenAsync(), track.VideoId, ct);
            RatingCache[track.VideoId] = (rating, DateTime.UtcNow);
            return rating;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return TrackRating.None;
        }
    }

    public static async Task SetTrackRatingAsync(
        Track track, TrackRating rating, CancellationToken ct = default)
    {
        if (!Auth.IsSignedIn)
            throw new InvalidOperationException("Sign in with Google to rate songs.");

        var apiRating = rating switch
        {
            TrackRating.Like => "like",
            TrackRating.Dislike => "dislike",
            _ => "none"
        };
        await Data.RateVideoAsync(await LibraryTokenAsync(), track.VideoId, apiRating, ct);
        RatingCache[track.VideoId] = (rating, DateTime.UtcNow);

        lock (DislikedGate)
        {
            if (rating == TrackRating.Dislike) Disliked.Add(track.VideoId);
            else Disliked.Remove(track.VideoId);
            SaveDislikedLocked();
        }

        ClearLikedCache();
        ClearForYouCache();
        lock (CollectionCache) CollectionCache.Clear();
        lock (CollectionPageCache) CollectionPageCache.Clear();
        lock (PageCache) PageCache.Clear();
        Player.Queue.RemoveUpcomingWhere(t => IsDisliked(t.VideoId));
        RatingChanged?.Invoke(track.VideoId, rating);
        LikedMusicChanged?.Invoke();

        if (rating == TrackRating.Dislike && Player.Queue.Current?.VideoId == track.VideoId)
            _ = SkipDislikedSafeAsync(track.VideoId);

        ReportInfo(rating switch
        {
            TrackRating.Like => "Added to Liked Music.",
            TrackRating.Dislike => "Disliked. This song will be skipped and kept out of recommendations.",
            _ => "Rating removed."
        });
    }

    private static async Task SkipDislikedSafeAsync(string videoId)
    {
        try { await Player.SkipDislikedAsync(videoId); }
        catch { /* playback failure is reported by PlayerService */ }
    }

    public static bool IsDisliked(string videoId)
    {
        lock (DislikedGate) return Disliked.Contains(videoId);
    }

    public static IReadOnlyList<Track> FilterDisliked(IEnumerable<Track> tracks)
    {
        HashSet<string> disliked;
        lock (DislikedGate) disliked = new HashSet<string>(Disliked, StringComparer.Ordinal);
        return tracks.Where(t => !disliked.Contains(t.VideoId)).ToList();
    }

    private static IReadOnlyList<Track> FilterRadio(IEnumerable<Track> tracks)
    {
        HashSet<string> disliked;
        lock (DislikedGate) disliked = new HashSet<string>(Disliked, StringComparer.Ordinal);
        return tracks
            .Where(t => !disliked.Contains(t.VideoId) && !NotInterested.IsBlocked(t.VideoId))
            .ToList();
    }

    public static void MarkNotInterested(string? videoId)
    {
        NotInterested.Mark(videoId);
        ClearForYouCache();
    }

    private static string? DislikedPathLocked()
    {
        if (_dislikedAccount is not { } email) return null;
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())))[..20];
        return Path.Combine(
            AppPaths.DataRoot, "disliked-" + hash + ".json");
    }

    private static void SaveDislikedLocked()
    {
        var path = DislikedPathLocked();
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(Disliked.OrderBy(id => id).ToList()));
        }
        catch
        {
            // local filter cache is optional
        }
    }

    private static void RefreshDislikedAccount()
    {
        var email = Auth.IsSignedIn ? Auth.Email : null;
        lock (DislikedGate)
        {
            if (string.Equals(email, _dislikedAccount, StringComparison.OrdinalIgnoreCase)) return;
            _dislikedAccount = email;
            Disliked.Clear();
            RatingCache.Clear();
            if (string.IsNullOrWhiteSpace(email)) return;

            try
            {
                var path = DislikedPathLocked();
                if (path is null || !File.Exists(path)) return;
                if (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) is { } saved)
                    foreach (var id in saved)
                        if (!string.IsNullOrWhiteSpace(id)) Disliked.Add(id);
            }
            catch
            {
                // begin with an empty local filter
            }
        }
    }

    public static async Task DeletePlaylistAsync(MediaItem playlist)
    {
        if (!playlist.CanEdit || playlist.PlaylistId is not { } id || id is "LM" or "LL")
            throw new InvalidOperationException("That playlist cannot be deleted.");

        await Data.DeletePlaylistAsync(await LibraryTokenAsync(), id);
        _libraryPlaylistsLoaded = false;
        try { File.Delete(LibraryPlaylistsDiskPath); } catch { /* optional cache */ }
        lock (CollectionCache) CollectionCache.Clear();
        lock (CollectionPageCache) CollectionPageCache.Clear();
        LibraryChanged?.Invoke();
        ReportInfo($"Deleted playlist \u201C{playlist.Title}\u201D.");
    }

    public static async Task RemoveFromPlaylistAsync(Track track)
    {
        var token = await LibraryTokenAsync();
        var liked = track.PlaylistId is "LM" or "LL";

        if (liked)
        {
            await Data.RateVideoAsync(token, track.VideoId, "none");
        }
        else if (track.PlaylistItemId is { } itemId)
        {
            await Data.DeletePlaylistItemAsync(token, itemId);
        }
        else
        {
            throw new InvalidOperationException("This track cannot be removed from here.");
        }

        _libraryPlaylistsLoaded = false;
        lock (CollectionCache) CollectionCache.Clear();
        lock (CollectionPageCache) CollectionPageCache.Clear();
        ClearLikedCache();
        ClearForYouCache();
        LibraryChanged?.Invoke();
        TrackRemoved?.Invoke(track.VideoId);
        ReportInfo(liked ? "Removed from Liked Music." : "Removed from playlist.");
    }

    public static void ClearForYouCache()
    {
        lock (ForYouCache) ForYouCache.Clear();
        foreach (var shuffle in new[] { false, true })
        {
            try { File.Delete(ForYouDiskPath(shuffle)); }
            catch { /* cache cleanup is best effort */ }
        }
    }

    private static async Task<IReadOnlyList<Shelf>?> TryLoadForYouDiskAsync(bool shuffle)
    {
        try
        {
            var path = ForYouDiskPath(shuffle);
            if (!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path);
            var disk = JsonSerializer.Deserialize<ForYouDiskCache>(json);
            if (disk is null || disk.Shelves.Count == 0
                || disk.Cursor != AppSettings.Current.ForYouSeedCursor
                || DateTimeOffset.UtcNow - disk.At > ForYouTtl)
                return null;

            var filtered = disk.Shelves
                .Select(s => s with
                {
                    Items = s.Items
                        .Where(i => !IsDisliked(i.Track?.VideoId ?? i.VideoId ?? "")
                                    && !NotInterested.IsBlocked(i.Track?.VideoId ?? i.VideoId))
                        .ToList()
                })
                .Where(s => s.Items.Count > 0)
                .ToList();
            return filtered.Count > 0 ? filtered : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task SaveForYouDiskAsync(bool shuffle, IReadOnlyList<Shelf> shelves, int cursor)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ForYouDiskPath(shuffle))!);
            var json = JsonSerializer.Serialize(new ForYouDiskCache(DateTimeOffset.UtcNow, shelves.ToList(), cursor));
            await File.WriteAllTextAsync(ForYouDiskPath(shuffle), json);
        }
        catch
        {
            // disk cache is optional
        }
    }

    public static async Task<IReadOnlyList<Track>> GetLikedPreviewAsync(int count = 20) =>
        (await GetLikedSampleAsync(count))
        .Where(t => !IsDisliked(t.VideoId))
        .Take(count).ToList();

    private static async Task<IReadOnlyList<Track>> GetLikedSampleAsync(int count)
    {
        Task<IReadOnlyList<Track>> task;
        lock (LikedGate)
        {
            if (_likedTask is null || DateTime.UtcNow - _likedAt > LikedTtl)
            {
                _likedAt = DateTime.UtcNow;
                _likedTask = FetchLikedSampleAsync();
            }
            task = _likedTask;
        }

        try
        {
            var all = await task;
            return all.Take(count).ToList();
        }
        catch
        {
            lock (LikedGate) { _likedTask = null; _likedAt = default; }
            throw;
        }
    }

    private static async Task<IReadOnlyList<Track>> FetchLikedSampleAsync()
    {
        var page = await Data.GetLikedMusicPageAsync(await LibraryTokenAsync(), null, 50, filterChannels: false);
        return page.Tracks;
    }

    public static void ClearLikedCache()
    {
        lock (LikedGate) { _likedTask = null; _likedAt = default; }
    }

    public static async Task<IReadOnlyList<Shelf>> GetForYouShelvesAsync(bool shuffle)
    {
        lock (ForYouCache)
        {
            if (ForYouCache.TryGetValue(shuffle, out var hit) && DateTime.UtcNow - hit.At < ForYouTtl)
                return hit.Shelves;
        }

        if (await TryLoadForYouDiskAsync(shuffle) is { } diskShelves)
        {
            lock (ForYouCache) ForYouCache[shuffle] = (DateTime.UtcNow, diskShelves);
            return diskShelves;
        }

        var liked = (await GetLikedSampleAsync(50))
            .Where(t => !IsDisliked(t.VideoId) && !NotInterested.IsBlocked(t.VideoId))
            .ToList();
        var candidates = liked
            .Select((track, index) => new
            {
                Track = track,
                Index = index,
                Artist = track.Artists.FirstOrDefault()?.Name
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Artist) && x.Artist != "Unknown artist")
            .GroupBy(x => x.Artist!)
            .Select(g => new
            {
                Artist = g.Key,
                Count = g.Count(),
                FirstIndex = g.Min(x => x.Index),
                All = g.OrderBy(x => x.Index).Select(x => x.Track).ToList()
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.FirstIndex)
            .Take(5)
            .ToList();

        if (shuffle) candidates = candidates.OrderBy(_ => Random.Shared.Next()).ToList();

        var cursor = candidates.Count == 0 ? 0 : Math.Abs(AppSettings.Current.ForYouSeedCursor % candidates.Count);
        var rotated = candidates.Skip(cursor).Concat(candidates.Take(cursor)).ToList();
        var chosen = rotated.Take(2).ToList();
        var nextCursor = candidates.Count == 0 ? 0 : (cursor + 2) % candidates.Count;

        var shelves = new List<Shelf>();
        var seeds = chosen
            .Select(candidate => (
                Candidate: candidate,
                Seed: shuffle
                    ? candidate.All[Random.Shared.Next(candidate.All.Count)]
                    : candidate.All[0]))
            .ToList();
        var radioTasks = seeds
            .Select(async item => (
                item.Candidate,
                item.Seed,
                Radio: await Safe(() => Yt.GetRadioAsync(item.Seed.VideoId))))
            .ToList();

        // Start the artist searches alongside the radios; both only depend on the liked sample.
        var searchArtists = chosen.Concat(rotated.Where(c => !chosen.Contains(c))).Take(3).ToList();
        var searchTasks = searchArtists
            .Select(candidate => Safe(() => Yt.SearchSongsAsync(candidate.Artist)))
            .ToList();

        var radios = await Task.WhenAll(radioTasks);
        var searchResults = await Task.WhenAll(searchTasks);

        foreach (var (candidate, seed, radio) in radios)
        {
            var isSecond = ReferenceEquals(candidate, seeds.Count > 1 ? seeds[1].Candidate : null);
            var picked = PickForYou(radio, seed.VideoId, 10, 2, shuffle, isSecond ? candidate.Artist : null);
            if (picked.Count == 0) continue;

            var displayArtist = DisplayArtist(candidate.Artist);
            var title = isSecond ? $"More like {displayArtist}" : $"Because you liked {displayArtist}";
            shelves.Add(new Shelf(title, ShelfStyle.Cards, picked.Select(ToMedia).ToList()));
        }

        var more = new List<Track>();
        var seen = new HashSet<string>();
        var perArtist = new Dictionary<string, int>();

        for (var i = 0; i < searchArtists.Count; i++)
        {
            var candidate = searchArtists[i];
            var songs = searchResults[i]
                .Where(t => !IsDisliked(t.VideoId) && !NotInterested.IsBlocked(t.VideoId))
                .ToList();
            var ordered = shuffle
                ? songs.OrderBy(_ => Random.Shared.Next()).ToList()
                : songs.ToList();

            foreach (var track in ordered)
            {
                if (!seen.Add(track.VideoId)) continue;
                var artist = track.Artists.FirstOrDefault()?.Name ?? candidate.Artist;
                perArtist.TryGetValue(artist, out var count);
                if (count >= 3) continue;
                perArtist[artist] = count + 1;
                more.Add(track);
                if (more.Count >= 12) break;
            }
            if (more.Count >= 12) break;
        }
        if (more.Count > 0)
        {
            shelves.Add(new Shelf("More from artists you like", ShelfStyle.Cards,
                more.Select(ToMedia).ToList()));
        }

        AppSettings.Current.ForYouSeedCursor = nextCursor;
        AppSettings.Save();
        lock (ForYouCache) ForYouCache[shuffle] = (DateTime.UtcNow, shelves);
        await SaveForYouDiskAsync(shuffle, shelves, nextCursor);
        return shelves;
    }

    private static IReadOnlyList<Track> PickForYou(
        IReadOnlyList<Track> source, string seedId, int max, int perArtist, bool shuffle,
        string? excludeArtist = null)
    {
        var ordered = shuffle
            ? source.Where(t => !IsDisliked(t.VideoId) && !NotInterested.IsBlocked(t.VideoId))
                .OrderBy(_ => Random.Shared.Next()).ToList()
            : source.Where(t => !IsDisliked(t.VideoId) && !NotInterested.IsBlocked(t.VideoId)).ToList();
        var seen = new HashSet<string> { seedId };
        var counts = new Dictionary<string, int>();
        var result = new List<Track>();
        var excluded = NormalizeArtist(excludeArtist);

        foreach (var track in ordered)
        {
            if (!seen.Add(track.VideoId)) continue;
            if (excluded.Length > 0 && track.Artists.Any(a => NormalizeArtist(a.Name) == excluded))
                continue;
            var artist = track.Artists.FirstOrDefault()?.Name ?? "";
            counts.TryGetValue(artist, out var count);
            if (count >= perArtist) continue;
            counts[artist] = count + 1;
            result.Add(track);
            if (result.Count >= max) break;
        }
        return result;
    }

    private static string NormalizeArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        return DisplayArtist(artist).ToLowerInvariant();
    }

    private static string DisplayArtist(string artist) =>
        artist.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase).Trim();

    private static MediaItem ToMedia(Track track) =>
        new(MediaKind.Song, track.Title, track.ArtistLine, track.ThumbnailUrl,
            VideoId: track.VideoId, Track: track);

    private static async Task<string> LibraryTokenAsync()
    {
        if (!Auth.IsSignedIn)
            throw new InvalidOperationException("Sign in with Google to use your library.");
        return await Auth.AccessTokenAsync()
               ?? throw new InvalidOperationException("Google sign-in has expired. Sign out and sign in again.");
    }

    public static void SetAutoplay(bool on)
    {
        Player.Autoplay = on;
        AppSettings.Current.Autoplay = on;
        AppSettings.Save();
    }

    public static void Shutdown()
    {
        var settings = AppSettings.Current;
        settings.Volume = Player.Volume > 0 ? Player.Volume : settings.Volume;
        settings.Quality = (int)Player.Quality;
        settings.Autoplay = Player.Autoplay;
        AppSettings.Save();

        Player.Dispose();
        Audio.Dispose();
        Yt.Dispose();
    }

    public static void Prefetch(Track? track)
    {
        if (track is not null) Player.Prefetch(track.VideoId);
    }

    public static void OpenArtist(Artist artist) =>
        Navigate(artist.BrowseId is { } id
            ? new PageRequest(artist.Name, PageKind.Artist, id)
            : new PageRequest(artist.Name, PageKind.Search, Query: artist.Name));

    public static void Navigate(PageRequest request) => NavigateRequested?.Invoke(request);

    public static void ReportError(string message) => ErrorRaised?.Invoke(message);

    public static void ReportInfo(string message) => InfoRaised?.Invoke(message);

    // Shared by the predictive dropdown and the results page: typing "daft punk" and pressing Enter costs one request.
    public static Task<IReadOnlyList<MediaItem>> GetSearchAsync(string query)
    {
        var q = query.Trim();
        var key = q.ToLowerInvariant();
        lock (SearchCache)
        {
            if (SearchCache.TryGetValue(key, out var hit)
                && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(2)
                && !hit.Task.IsFaulted && !hit.Task.IsCanceled)
                return hit.Task;

            foreach (var stale in SearchCache.Where(kv => DateTime.UtcNow - kv.Value.At > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToList())
                SearchCache.Remove(stale);
            if (SearchCache.Count >= MaxSearchCache)
            {
                foreach (var old in SearchCache.OrderBy(kv => kv.Value.At)
                             .Take(SearchCache.Count - MaxSearchCache + 1)
                             .Select(kv => kv.Key).ToList())
                    SearchCache.Remove(old);
            }

            var task = Yt.SearchAllAsync(q);
            SearchCache[key] = (DateTime.UtcNow, task);
            _ = task.ContinueWith(t =>
            {
                if (!t.IsFaulted) return;
                lock (SearchCache)
                {
                    if (SearchCache.TryGetValue(key, out var current) && ReferenceEquals(current.Task, t))
                        SearchCache.Remove(key);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    // Player page content, all cached per track for the life of a session. The radio fetch that starts a song
    // already carries the two browse ids, so opening "Lyrics" is usually a single small request (or none at all).
    public static Task<PlayerExtras> GetExtrasAsync(Track track, CancellationToken ct = default)
    {
        Task<PlayerExtras> task;
        lock (ExtrasCache)
        {
            if (ExtrasCache.TryGetValue(track.VideoId, out var hit)
                && DateTime.UtcNow - hit.At < ContentTtl
                && !hit.Task.IsFaulted)
            {
                task = hit.Task;
            }
            else
            {
                task = Yt.TryGetCachedExtras(track.VideoId) is { } cached
                    ? Task.FromResult(cached)
                    : Yt.GetPlayerExtrasAsync(track.VideoId);
                ExtrasCache[track.VideoId] = (DateTime.UtcNow, task);
                TrimCache(ExtrasCache, 24);
            }
        }
        return task.WaitAsync(ct);
    }

    public static Task<LyricsText?> GetLyricsAsync(Track track, CancellationToken ct = default)
    {
        Task<LyricsText?> task;
        lock (LyricsCache)
        {
            if (LyricsCache.TryGetValue(track.VideoId, out var hit)
                && DateTime.UtcNow - hit.At < ContentTtl
                && !hit.Task.IsFaulted)
            {
                task = hit.Task;
            }
            else
            {
                task = LoadLyricsCoreAsync(track);
                LyricsCache[track.VideoId] = (DateTime.UtcNow, task);
                TrimCache(LyricsCache, 16);
            }
        }
        return task.WaitAsync(ct);
    }

    public static Task<BrowsePage?> GetRelatedAsync(Track track, CancellationToken ct = default)
    {
        Task<BrowsePage?> task;
        lock (RelatedCache)
        {
            if (RelatedCache.TryGetValue(track.VideoId, out var hit)
                && DateTime.UtcNow - hit.At < ContentTtl
                && !hit.Task.IsFaulted)
            {
                task = hit.Task;
            }
            else
            {
                task = LoadRelatedCoreAsync(track);
                RelatedCache[track.VideoId] = (DateTime.UtcNow, task);
                TrimCache(RelatedCache, 12);
            }
        }
        return task.WaitAsync(ct);
    }

    private static async Task<LyricsText?> LoadLyricsCoreAsync(Track track)
    {
        var extras = await GetExtrasAsync(track);
        return extras.LyricsBrowseId is null ? null : await Yt.GetLyricsAsync(extras.LyricsBrowseId);
    }

    private static async Task<BrowsePage?> LoadRelatedCoreAsync(Track track)
    {
        var extras = await GetExtrasAsync(track);
        return extras.RelatedBrowseId is null ? null : await Yt.BrowseAsync(extras.RelatedBrowseId);
    }

    // Keeps the oldest-inserted entries out; sizes are deliberately small so the app stays light.
    private static void TrimCache<T>(Dictionary<string, (DateTime At, Task<T> Task)> cache, int max)
    {
        if (cache.Count <= max) return;
        foreach (var key in cache.OrderBy(kv => kv.Value.At).Take(cache.Count - max).Select(kv => kv.Key).ToList())
            cache.Remove(key);
    }

    public static async Task<BrowsePage> GetBrowseAsync(string browseId, string? browseParams)
    {
        var key = browseId + "|" + browseParams;
        if (PageCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5))
            return hit.Page;

        var page = await Yt.BrowseAsync(browseId, browseParams);
        page = FilterDislikedPage(page);
        PageCache[key] = (DateTime.UtcNow, page);
        TrimPageCache();
        return page;
    }

    public static async Task<BrowsePage> GetArtistAsync(string channelId, string? name)
    {
        var key = "artist|" + channelId;
        if (PageCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5))
            return hit.Page;
        var page = FilterDislikedPage(await Yt.ArtistAsync(channelId, name));
        PageCache[key] = (DateTime.UtcNow, page);
        TrimPageCache();
        return page;
    }

    private static BrowsePage FilterDislikedPage(BrowsePage page)
    {
        HashSet<string> disliked;
        lock (DislikedGate) disliked = new HashSet<string>(Disliked, StringComparer.Ordinal);

        return page with
        {
            Shelves = page.Shelves
                .Select(s => s with
                {
                    Items = s.Items.Where(i =>
                    {
                        var id = i.Track?.VideoId ?? i.VideoId;
                        return id is null || (!disliked.Contains(id) && !NotInterested.IsBlocked(id));
                    }).ToList()
                })
                .Where(s => s.Items.Count > 0)
                .ToList()
        };
    }

    private static void TrimPageCache()
    {
        if (PageCache.Count <= MaxPageCache) return;
        foreach (var key in PageCache.OrderBy(kv => kv.Value.At)
                     .Take(PageCache.Count - MaxPageCache)
                     .Select(kv => kv.Key).ToList())
            PageCache.Remove(key);
    }

    private static void TrimCollectionCache()
    {
        if (CollectionCache.Count <= MaxCollectionCache) return;
        foreach (var key in CollectionCache.OrderBy(kv => kv.Value.At)
                     .Take(CollectionCache.Count - MaxCollectionCache)
                     .Select(kv => kv.Key).ToList())
            CollectionCache.Remove(key);
    }

    public sealed record CollectionData(IReadOnlyList<Track> Tracks, string? Title, string? ThumbnailUrl);

    public sealed record CollectionPageData(
        IReadOnlyList<Track> Tracks, string? NextPageToken, string? Title, string? ThumbnailUrl);

    public static async Task<CollectionPageData> LoadCollectionPageAsync(MediaItem item, string? pageToken)
    {
        var key = (item.PlaylistId ?? item.BrowseId ?? item.Title) + "|" + pageToken;
        lock (CollectionPageCache)
        {
            if (CollectionPageCache.TryGetValue(key, out var hit)
                && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(3))
                return hit.Data;
        }

        CollectionPageData result;
        if (Auth.IsSignedIn && item.Kind == MediaKind.Playlist && item.PlaylistId is { } id)
        {
            if (id == "LM")
            {
                var liked = await Data.GetLikedMusicPageAsync(
                    await LibraryTokenAsync(), pageToken, 100);
                result = new CollectionPageData(liked.Tracks, liked.NextPageToken, item.Title,
                    item.ThumbnailUrl ?? liked.Tracks.FirstOrDefault()?.ThumbnailUrl);
            }
            else
            {
                try
                {
                    var page = await Data.GetPlaylistTracksPageAsync(
                        await LibraryTokenAsync(), id, pageToken, 100);
                    result = new CollectionPageData(page.Tracks, page.NextPageToken, item.Title,
                        item.ThumbnailUrl ?? page.Tracks.FirstOrDefault()?.ThumbnailUrl);
                }
                catch
                {
                    // Public/YTM-only playlist: fall through to the anonymous full load.
                    var data = await LoadCollectionAsync(item);
                    result = new CollectionPageData(data.Tracks, null, data.Title, data.ThumbnailUrl);
                }
            }
        }
        else
        {
            var data = await LoadCollectionAsync(item);
            result = new CollectionPageData(data.Tracks, null, data.Title, data.ThumbnailUrl);
        }

        lock (CollectionPageCache)
        {
            CollectionPageCache[key] = (DateTime.UtcNow, result);
            if (CollectionPageCache.Count > MaxCollectionPageCache)
            {
                foreach (var old in CollectionPageCache.OrderBy(kv => kv.Value.At)
                             .Take(CollectionPageCache.Count - MaxCollectionPageCache)
                             .Select(kv => kv.Key).ToList())
                    CollectionPageCache.Remove(old);
            }
        }
        return result;
    }

    public static async Task<IReadOnlyList<Track>> LoadCollectionTracksAsync(MediaItem item) =>
        (await LoadCollectionAsync(item)).Tracks;

    private static async Task<bool> ConfirmAsync(LinkPreview preview)
    {
        var result = await Dialogs.ShowAsync(new LinkConfirmDialog(preview));
        return result == ContentDialogResult.Primary;
    }

    private static int IndexOf(IReadOnlyList<Track> tracks, string videoId)
    {
        for (var i = 0; i < tracks.Count; i++)
            if (tracks[i].VideoId == videoId) return i;
        return -1;
    }

    // Opens a link someone shared (browser, phone app, chat). Pasted links ask first, showing what was detected.
    public static async Task<bool> OpenLinkAsync(string text, bool confirm = true)
    {
        switch (LinkParser.Classify(text))
        {
            case LinkStatus.NotALink:
                return false;
            case LinkStatus.Unsupported:
                ReportError("Link not supported. Paste a YouTube Music or YouTube link: a song, video, playlist, album, artist or channel.");
                return false;
        }

        var target = LinkParser.Parse(text)!;
        try
        {
            if (target.Kind == LinkKind.ChannelName)
            {
                var channelId = await Yt.ResolveChannelAsync(target.Id);
                if (channelId is null)
                {
                    ReportError("Couldn't find that channel.");
                    return false;
                }
                target = new LinkTarget(LinkKind.Artist, channelId);
            }

            switch (target.Kind)
            {
                case LinkKind.Song:
                {
                    var tracks = await Yt.GetQueueAsync(target.Id, target.PlaylistId);
                    if (tracks.Count == 0)
                    {
                        ReportError("Couldn't find that song.");
                        return false;
                    }
                    var start = Math.Max(0, IndexOf(tracks, target.Id));
                    var first = tracks[start];
                    var detail = first.ArtistLine;
                    if (!string.IsNullOrWhiteSpace(first.Album)) detail += " \u2022 " + first.Album;
                    if (target.PlaylistId is { } list && !list.StartsWith("RD"))
                        detail += $"\nFrom a playlist of {tracks.Count} songs; it will play from this song.";
                    if (confirm && !await ConfirmAsync(new LinkPreview(MediaKind.Song, first.Title, detail, first.ThumbnailUrl, "Play")))
                        return false;
                    await Player.PlayQueueAsync(tracks, start);
                    Player.Queue.SourceTitle = Yt.TryGetCachedExtras(target.Id)?.QueueTitle
                                               ?? first.Title + " Radio";
                    return true;
                }

                case LinkKind.Playlist or LinkKind.Album:
                {
                    var kind = target.Kind == LinkKind.Playlist ? MediaKind.Playlist : MediaKind.Album;
                    var item = kind == MediaKind.Playlist
                        ? new MediaItem(kind, "Playlist", "", null, PlaylistId: target.Id, BrowseId: "VL" + target.Id)
                        : new MediaItem(kind, "Album", "", null, BrowseId: target.Id);
                    var data = await LoadCollectionAsync(item);
                    if (data.Tracks.Count == 0)
                    {
                        ReportError("Couldn't load that " + kind.ToString().ToLowerInvariant() + ".");
                        return false;
                    }
                    var title = string.IsNullOrWhiteSpace(data.Title) ? item.Title : data.Title!;
                    var thumb = data.ThumbnailUrl ?? data.Tracks[0].ThumbnailUrl;
                    if (confirm && !await ConfirmAsync(new LinkPreview(kind, title, $"{data.Tracks.Count} songs", thumb, "Open")))
                        return false;
                    Navigate(new PageRequest(title, PageKind.Collection, Item: item with { Title = title, ThumbnailUrl = thumb }));
                    return true;
                }

                case LinkKind.Artist:
                {
                    var page = await GetArtistAsync(target.Id, null);
                    var name = string.IsNullOrWhiteSpace(page.Title) ? "Artist" : page.Title!;
                    if (confirm && !await ConfirmAsync(new LinkPreview(MediaKind.Artist, name, "Opens the artist page", page.ThumbnailUrl, "Open")))
                        return false;
                    Navigate(new PageRequest(name, PageKind.Artist, target.Id,
                        Item: new MediaItem(MediaKind.Artist, name, "", page.ThumbnailUrl, BrowseId: target.Id)));
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            ReportError(e.Message);
        }
        return false;
    }

    public static async Task<CollectionData> LoadCollectionAsync(MediaItem item)
    {
        var cacheKey = item.PlaylistId + "|" + item.BrowseId;
        if (CollectionCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(5))
            return cached.Data;

        // Signed-in playlists come from the official Data API. InnerTube rejects OAuth tokens, so only signed-out
        // or public playlists use the anonymous queue/browse fallback below.
        if (item.Kind == MediaKind.Playlist && Auth.IsSignedIn && item.PlaylistId is { } libraryId)
        {
            if (libraryId == "LM")
            {
                var liked = await Data.GetLikedMusicAsync(await LibraryTokenAsync());
                var likedData = new CollectionData(liked, item.Title,
                    item.ThumbnailUrl ?? liked.FirstOrDefault()?.ThumbnailUrl);
                CollectionCache[cacheKey] = (DateTime.UtcNow, likedData);
                TrimCollectionCache();
                return likedData;
            }

            try
            {
                var dataTracks = await Data.GetPlaylistTracksAsync(await LibraryTokenAsync(), libraryId);
                var data = new CollectionData(dataTracks, item.Title,
                    item.ThumbnailUrl ?? dataTracks.FirstOrDefault()?.ThumbnailUrl);
                CollectionCache[cacheKey] = (DateTime.UtcNow, data);
                TrimCollectionCache();
                return data;
            }
            catch
            {
                // Not visible to this account through the Data API (for example a public/YTM-only playlist);
                // fall through to anonymous InnerTube.
            }
        }

        // Two sources, whichever returns more wins (ties prefer the queue: it carries artists, durations and art).
        var queueTask = item.PlaylistId is null
            ? Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>())
            : Safe(() => Yt.GetQueueAsync(null, item.PlaylistId));

        BrowsePage? page = null;
        var browseTask = item.BrowseId is null
            ? Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>())
            : Safe(async () =>
            {
                page = await Yt.BrowseAsync(item.BrowseId, item.BrowseParams);
                return TracksOf(page);
            });

        var queue = await queueTask;
        var browse = await browseTask;
        var tracks = browse.Count > queue.Count ? browse : queue;
        var result = new CollectionData(tracks, page?.Title, page?.ThumbnailUrl);
        if (tracks.Count > 0)
        {
            CollectionCache[cacheKey] = (DateTime.UtcNow, result);
            TrimCollectionCache();
        }
        return result;
    }

    public static async Task PlayCollectionAsync(
        IReadOnlyList<Track> tracks, int startIndex, bool? shuffle, string? sourceTitle = null)
    {
        // Disliked tracks are excluded from generated/collection playback. Explicit search play
        // goes through PlayExplicitAsync instead and remains available.
        tracks = FilterDisliked(tracks);
        if (tracks.Count == 0) return;
        RequestPlayer(PlayerTab.Queue);
        if (shuffle is { } sh) Player.Queue.Shuffle = sh;
        if (!string.IsNullOrWhiteSpace(sourceTitle)) Player.Queue.SourceTitle = sourceTitle;
        await Player.PlayQueueAsync(tracks, Math.Clamp(startIndex, 0, tracks.Count - 1));
    }

    private static async Task<IReadOnlyList<Track>> Safe(Func<Task<IReadOnlyList<Track>>> load)
    {
        try { return await load(); }
        catch { return Array.Empty<Track>(); }
    }

    private static IReadOnlyList<Track> TracksOf(BrowsePage page)
    {
        var seen = new HashSet<string>();
        return page.Shelves.SelectMany(s => s.Items)
            .Where(i => i.IsTrack && i.Track is not null)
            .Select(i => i.Track!)
            .Where(t => seen.Add(t.VideoId))
            .ToList();
    }

    public static void RequestPlayer(PlayerTab tab = PlayerTab.Queue) => PlayerRequested?.Invoke(tab);

    // Explicit play: this is the only path that brings the full player page forward.
    // Automatic queue advances never call this.
    public static async Task PlayExplicitAsync(Track track)
    {
        RequestPlayer(PlayerTab.Queue);
        await Player.PlayTrackWithRadioAsync(track);
    }

    public static async Task StartRadioExplicitAsync(Track track)
    {
        RequestPlayer(PlayerTab.Queue);
        var radio = FilterRadio(MixRadio(await Yt.GetRadioAsync(track.VideoId), track));
        var queue = new List<Track> { track };
        queue.AddRange(radio.Where(t => t.VideoId != track.VideoId));
        await Player.PlayQueueAsync(queue, 0);
    }

    // YouTube's radio queue is deterministic for a seed. Rotate it and push songs already played
    // in this session to the back so repeated starts do not feel identical.
    private static IReadOnlyList<Track> MixRadio(IReadOnlyList<Track> source, Track seed)
    {
        HashSet<string> recent =
            Player?.SessionHistory?.Select(t => t.VideoId).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var candidates = source
            .Where(t => t.VideoId != seed.VideoId && !recent.Contains(t.VideoId))
            .OrderBy(_ => Random.Shared.Next())
            .ToList();
        if (candidates.Count > 0) return candidates;
        return source.Where(t => t.VideoId != seed.VideoId).OrderBy(_ => Random.Shared.Next()).ToList();
    }

    public static async Task ActivateAsync(MediaItem item)
    {
        switch (item.Kind)
        {
            case MediaKind.Song or MediaKind.Video when item.Track is not null:
                await PlayExplicitAsync(item.Track);
                break;

            case MediaKind.Album or MediaKind.Playlist:
                // Open the collection first; the page offers Play, Shuffle, or picking a track.
                Navigate(new PageRequest(item.Title, PageKind.Collection, Item: item));
                break;

            case MediaKind.Artist:
                if (item.BrowseId is not null)
                    Navigate(new PageRequest(item.Title, PageKind.Artist, item.BrowseId, Item: item));
                else
                    Navigate(new PageRequest(item.Title, PageKind.Search, Query: item.Title));
                break;

            case MediaKind.Nav when item.BrowseId is not null:
                Navigate(new PageRequest(item.Title, PageKind.Browse, item.BrowseId, item.BrowseParams));
                break;
        }
    }
}
