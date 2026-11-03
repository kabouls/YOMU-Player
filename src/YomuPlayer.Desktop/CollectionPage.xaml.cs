using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// An album or playlist: header with Play / Shuffle, then every loaded track. Signed-in account playlists load a
// page at a time so a 900-track Liked Music collection does not cost dozens of requests up front.
public sealed partial class CollectionPage : Page
{
    private readonly List<Track> _tracks = new();
    private MediaItem? _item;
    private string? _nextPageToken;
    private string _sourceTitle = "";

    public ObservableCollection<TrackVM> Tracks { get; } = new();

    public CollectionPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not PageRequest { Item: { } item }) return;

        AppServices.TrackRemoved += OnTrackRemoved;
        _item = item;
        var vm = new ItemVM(item);
        TitleText.Text = item.Title;
        _sourceTitle = item.Title;
        SubtitleText.Text = item.Subtitle;
        KindText.Text = vm.KindLabel;
        KindPill.Background = vm.KindBrush;
        DeletePlaylistButton.Visibility = item.Kind == MediaKind.Playlist && item.CanEdit
                                          && item.PlaylistId is not "LM" and not "LL"
            ? Visibility.Visible
            : Visibility.Collapsed;
        CoverBrush.ImageSource = Thumbnails.Create(item.ThumbnailUrl, 480);
        var stacked = item.Kind == MediaKind.Playlist ? Visibility.Visible : Visibility.Collapsed;
        StackBack1.Visibility = stacked;
        StackBack2.Visibility = stacked;

        Busy.IsActive = true;
        try
        {
            var data = await AppServices.LoadCollectionPageAsync(item, null);
            _nextPageToken = data.NextPageToken;

            // Links open with a generic title and no art; fill both from the page or its first track.
            if (!string.IsNullOrWhiteSpace(data.Title) && item.Title is "Playlist" or "Album")
            {
                TitleText.Text = data.Title;
                _sourceTitle = data.Title;
            }
            var cover = item.ThumbnailUrl ?? data.ThumbnailUrl ?? data.Tracks.FirstOrDefault()?.ThumbnailUrl;
            CoverBrush.ImageSource = Thumbnails.Create(cover, 480);
            UpdateCover(item, data.Tracks);

            if (data.Tracks.Count == 0)
            {
                StatsText.Text = "No songs yet";
                LoadMoreButton.Visibility = Visibility.Collapsed;
                return;
            }

            AddTracks(data.Tracks);
            UpdateStats();

            // artist names lead to their pages (the album's artists, or the most frequent ones in a playlist)
            var topArtists = _tracks.SelectMany(t => t.Artists)
                .GroupBy(a => a.Name)
                .OrderByDescending(g => g.Count())
                .Take(item.Kind == MediaKind.Album ? 2 : 4)
                .Select(g => g.First())
                .ToList();
            if (topArtists.Count > 0)
            {
                ArtistLinksLabel.Text = item.Kind == MediaKind.Album ? "By" : "Top artists:";
                ArtistLinks.ItemsSource = topArtists.Select((a, i) => new ArtistLinkVM(a, i == topArtists.Count - 1)).ToList();
                ArtistLinksRow.Visibility = Visibility.Visible;
            }

            PlayAllButton.IsEnabled = true;
            ShuffleButton.IsEnabled = true;
            LoadMoreButton.Visibility = _nextPageToken is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            AppServices.ReportError(ex.Message);
        }
        finally
        {
            Busy.IsActive = false;
        }
    }

    private void AddTracks(IReadOnlyList<Track> tracks)
    {
        foreach (var track in tracks)
        {
            var index = _tracks.Count;
            _tracks.Add(track);
            Tracks.Add(new TrackVM(track, index, PlayFromAsync, _item?.CanEdit == true));
        }
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        AppServices.TrackRemoved -= OnTrackRemoved;
        base.OnNavigatedFrom(e);
    }

    private void OnTrackRemoved(string videoId)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_tracks.Any(t => t.VideoId == videoId)) return;

            _tracks.RemoveAll(t => t.VideoId == videoId);
            Tracks.Clear();
            for (var i = 0; i < _tracks.Count; i++)
                Tracks.Add(new TrackVM(_tracks[i], i, PlayFromAsync, _item?.CanEdit == true));

            if (_tracks.Count == 0)
            {
                StatsText.Text = "No songs yet";
                PlayAllButton.IsEnabled = false;
                ShuffleButton.IsEnabled = false;
                LoadMoreButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateStats();
            }

            if (_item is { } item) UpdateCover(item, _tracks);
        });
    }

    private void UpdateCover(MediaItem item, IReadOnlyList<Track> tracks)
    {
        if (item.Kind != MediaKind.Playlist)
        {
            CollageGrid.Visibility = Visibility.Collapsed;
            CoverBrush.ImageSource = Thumbnails.Create(item.ThumbnailUrl, 480);
            return;
        }

        var urls = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.ThumbnailUrl))
            .Select(t => t.ThumbnailUrl!)
            .Take(4)
            .ToList();
        if (urls.Count == 0)
        {
            CollageGrid.Visibility = Visibility.Collapsed;
            CoverBrush.ImageSource = Thumbnails.Create(item.ThumbnailUrl, 480);
            return;
        }

        var images = new[] { Collage0, Collage1, Collage2, Collage3 };
        for (var i = 0; i < images.Length; i++)
            images[i].Source = Thumbnails.Create(urls[i % urls.Count], 224);
        CollageGrid.Visibility = Visibility.Visible;
        CoverBrush.ImageSource = null;
    }

    private void UpdateStats()
    {
        var total = TimeSpan.FromSeconds(_tracks.Sum(t => t.Duration?.TotalSeconds ?? 0));
        StatsText.Text = total > TimeSpan.Zero
            ? $"{_tracks.Count} songs \u2022 {Describe(total)}"
            : $"{_tracks.Count} songs";
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_item is null || _nextPageToken is null) return;

        LoadMoreButton.IsEnabled = false;
        try
        {
            var data = await AppServices.LoadCollectionPageAsync(_item, _nextPageToken);
            AddTracks(data.Tracks);
            _nextPageToken = data.NextPageToken;
            UpdateStats();
            PlayAllButton.IsEnabled = _tracks.Count > 0;
            ShuffleButton.IsEnabled = _tracks.Count > 0;
            LoadMoreButton.Visibility = _nextPageToken is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            AppServices.ReportError(ex.Message);
        }
        finally
        {
            LoadMoreButton.IsEnabled = true;
        }
    }

    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item) return;
        if (!await Dialogs.ConfirmDeletePlaylistAsync(item.Title)) return;

        try
        {
            await AppServices.DeletePlaylistAsync(item);
            AppServices.Navigate(new PageRequest("Home", PageKind.Home, "FEmusic_home"));
        }
        catch (Exception ex)
        {
            AppServices.ReportError(ex.Message);
        }
    }

    private static string Describe(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} hr {t.Minutes} min" : $"{Math.Max(1, t.Minutes)} min";

    // Play: in order from the top. Shuffle: shuffled, starting at a random track.
    private async void PlayAllButton_Click(object sender, RoutedEventArgs e) =>
        await AppServices.PlayCollectionAsync(_tracks, 0, shuffle: false, sourceTitle: _sourceTitle);

    private async void ShuffleButton_Click(object sender, RoutedEventArgs e) =>
        await AppServices.PlayCollectionAsync(_tracks, Random.Shared.Next(_tracks.Count), shuffle: true,
            sourceTitle: _sourceTitle);

    // Clicking a track starts there and keeps whatever shuffle setting is currently on.
    private Task PlayFromAsync(int index) =>
        AppServices.PlayCollectionAsync(_tracks, index, shuffle: null, sourceTitle: _sourceTitle);
}
