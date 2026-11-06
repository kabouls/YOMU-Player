using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// Signed-in Home: pseudo "For You" shelves plus a vertical Liked Music preview. Signed out it is the normal
// anonymous Home (Quick picks included).
public sealed partial class HomePage : Page
{
    private readonly List<Track> _likedTracks = new();
    private bool _shuffle;
    private DateTime _lastLoadedAt = DateTime.MinValue;

    public ObservableCollection<ShelfVM> ForYouShelves { get; } = new();
    public ObservableCollection<ShelfVM> MoreShelves { get; } = new();
    public ObservableCollection<ShelfVM> GenreShelves { get; } = new();
    public ObservableCollection<TrackVM> LikedRows { get; } = new();

    public HomePage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.TrackRemoved += OnTrackRemoved;
        AppServices.LikedMusicChanged += OnLikedMusicChanged;
        NotInterested.Changed += OnNotInterestedChanged;

        // Home keeps its visual tree while the user opens a song or another tab. Reuse it briefly
        // instead of rebuilding every card and firing another batch of image decodes on the UI thread.
        if (DateTime.UtcNow - _lastLoadedAt < TimeSpan.FromSeconds(20) &&
            (ForYouShelves.Count > 0 || MoreShelves.Count > 0))
            return;

        await LoadAsync();
        _lastLoadedAt = DateTime.UtcNow;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.TrackRemoved -= OnTrackRemoved;
        AppServices.LikedMusicChanged -= OnLikedMusicChanged;
        NotInterested.Changed -= OnNotInterestedChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnNotInterestedChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        _ = LoadForYouAsync();
        _ = LoadGenreAsync();
    });

    private async Task LoadAsync()
    {
        Busy.IsActive = true;
        try
        {
            var signedIn = AppServices.Auth.IsSignedIn;
            ForYouPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            LikedPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

            if (signedIn)
            {
                // Start the account work immediately instead of waiting for the anonymous Home browse. Both
                // requests then share one Liked Music fetch.
                var forYouTask = LoadForYouAsync();
                var likedTask = LoadLikedAsync();
                await LoadMoreShelvesAsync(signedIn);
                await Task.WhenAll(forYouTask, likedTask);
            }
            else
            {
                await LoadMoreShelvesAsync(false);
            }
            await LoadGenreAsync();
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

    private async Task LoadMoreShelvesAsync(bool signedIn)
    {
        var home = await AppServices.GetBrowseAsync("FEmusic_home", null);
        MoreShelves.Clear();
        foreach (var shelf in home.Shelves)
        {
            // Signed-in Home replaces Quick picks with For You + Liked Music.
            if (signedIn && shelf.Title?.Contains("quick picks", StringComparison.OrdinalIgnoreCase) == true)
                continue;
            MoreShelves.Add(new ShelfVM(shelf));
        }
    }

    private async Task LoadForYouAsync()
    {
        ForYouLoading.Visibility = Visibility.Visible;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var shelves = await AppServices.GetForYouShelvesAsync(_shuffle);
                    ForYouShelves.Clear();
                    foreach (var shelf in shelves) ForYouShelves.Add(new ShelfVM(shelf));
                    ForYouEmpty.Visibility = ForYouShelves.Count == 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    return;
                }
                catch (Exception) when (attempt == 0)
                {
                    // A cold first launch can hit a token/network hiccup; retry once before surfacing it.
                    await Task.Delay(900);
                }
            }
        }
        catch (Exception ex)
        {
            ForYouShelves.Clear();
            ForYouEmpty.Text = "Couldn't build For You. Press Refresh to try again.";
            ForYouEmpty.Visibility = Visibility.Visible;
            AppServices.ReportError(ex.Message);
        }
        finally
        {
            ForYouLoading.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadLikedAsync()
    {
        try
        {
            var liked = await AppServices.GetLikedPreviewAsync(20);
            _likedTracks.Clear();
            _likedTracks.AddRange(liked);
            FillLikedRows();
        }
        catch (Exception ex)
        {
            LikedEmpty.Text = "Couldn't load Liked Music.";
            LikedEmpty.Visibility = Visibility.Visible;
            AppServices.ReportError(ex.Message);
        }
    }

    private async Task LoadGenreAsync()
    {
        GenreShelves.Clear();
        GenrePanel.Visibility = Visibility.Collapsed;
        var settings = AppSettings.Current;
        if (string.IsNullOrWhiteSpace(settings.GenreBrowseId)) return;

        try
        {
            var shelves = await AppServices.GetGenreShelvesAsync();
            foreach (var shelf in shelves) GenreShelves.Add(new ShelfVM(shelf));
            if (GenreShelves.Count == 0) return;
            GenreHeader.Text = $"{settings.GenreTitle} picks";
            GenrePanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            AppServices.ReportError("Couldn't load the selected genre: " + ex.Message);
        }
    }

    private async void Genres_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var genres = await AppServices.GetGenresAsync();
            if (genres.Count == 0)
            {
                AppServices.ReportError("YouTube Music did not return any genres.");
                return;
            }

            var picker = new GenrePickerDialog(genres, AppSettings.Current.GenreBrowseId);
            var result = await Dialogs.ShowAsync(picker);
            if (result == ContentDialogResult.Primary)
            {
                AppServices.SetGenre(picker.SelectedGenre);
                await LoadGenreAsync();
            }
            else if (result == ContentDialogResult.Secondary)
            {
                AppServices.SetGenre(null);
                await LoadGenreAsync();
            }
        }
        catch (Exception ex)
        {
            AppServices.ReportError(ex.Message);
        }
    }

    private void FillLikedRows()
    {
        LikedRows.Clear();
        for (var i = 0; i < _likedTracks.Count; i++)
            LikedRows.Add(new TrackVM(_likedTracks[i], i, PlayLikedAsync, canEditPlaylist: true));
        LikedEmpty.Visibility = LikedRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Task PlayLikedAsync(int index)
    {
        if (index < 0 || index >= _likedTracks.Count) return Task.CompletedTask;
        return AppServices.PlayCollectionAsync(_likedTracks, index, shuffle: null, sourceTitle: "Liked Music");
    }

    private async void ShuffleToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!AppServices.Auth.IsSignedIn) return;
        _shuffle = ShuffleToggle.IsChecked == true;
        Busy.IsActive = true;
        try { await LoadForYouAsync(); }
        catch (Exception ex) { AppServices.ReportError(ex.Message); }
        finally { Busy.IsActive = false; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!AppServices.Auth.IsSignedIn) return;
        AppServices.ClearForYouCache();
        Busy.IsActive = true;
        try { await LoadForYouAsync(); }
        catch (Exception ex) { AppServices.ReportError(ex.Message); }
        finally { Busy.IsActive = false; }
    }

    private void SeeAllLiked_Click(object sender, RoutedEventArgs e) =>
        AppServices.Navigate(new PageRequest("Liked Music", PageKind.Collection, Item: AppServices.LikedMusic));

    private void OnTrackRemoved(string videoId)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var index = _likedTracks.FindIndex(t => t.VideoId == videoId);
            if (index < 0) return;
            _likedTracks.RemoveAt(index);
            FillLikedRows();
        });
    }

    private async void OnLikedMusicChanged()
    {
        try
        {
            var liked = await AppServices.GetLikedPreviewAsync(20);
            DispatcherQueue.TryEnqueue(() =>
            {
                _likedTracks.Clear();
                _likedTracks.AddRange(liked);
                FillLikedRows();
            });
        }
        catch
        {
            // The next Home visit retries.
        }
    }
}
