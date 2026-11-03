using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// A basic artist page: the artist's own shelves (top songs, albums, singles, videos, similar artists) under a Play / Shuffle header.
public sealed partial class ArtistPage : Page
{
    private IReadOnlyList<Track> _songs = Array.Empty<Track>();

    public ObservableCollection<ShelfVM> Shelves { get; } = new();

    public ArtistPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not PageRequest { BrowseId: { } channelId } request) return;

        NameText.Text = request.Title;
        CoverBrush.ImageSource = Thumbnails.Create(request.Item?.ThumbnailUrl, 400);

        Busy.IsActive = true;
        try
        {
            var page = await AppServices.GetArtistAsync(channelId, request.Title);
            if (!string.IsNullOrWhiteSpace(page.Title)) NameText.Text = page.Title;
            if (page.ThumbnailUrl is not null) CoverBrush.ImageSource = Thumbnails.Create(page.ThumbnailUrl, 400);
            if (!string.IsNullOrWhiteSpace(page.Description))
            {
                AboutText.Text = page.Description;
                AboutText.Visibility = Visibility.Visible;
            }

            foreach (var shelf in page.Shelves) Shelves.Add(new ShelfVM(shelf));
            EmptyText.Visibility = Shelves.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Play / Shuffle use the artist's songs (the first shelf that holds songs), falling back to any playable track.
            var songShelf = page.Shelves.FirstOrDefault(s => s.Items.Any(i => i.Kind == MediaKind.Song && i.Track is not null));
            _songs = (songShelf?.Items ?? page.Shelves.SelectMany(s => s.Items))
                .Where(i => i.IsTrack && i.Track is not null)
                .Select(i => i.Track!)
                .Take(25)
                .ToList();
            PlayButton.IsEnabled = ShuffleButton.IsEnabled = _songs.Count > 0;
            AppServices.Prefetch(_songs.FirstOrDefault());
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

    private async void PlayButton_Click(object sender, RoutedEventArgs e) =>
        await AppServices.PlayCollectionAsync(_songs, 0, shuffle: false);

    private async void ShuffleButton_Click(object sender, RoutedEventArgs e) =>
        await AppServices.PlayCollectionAsync(_songs, Random.Shared.Next(Math.Max(1, _songs.Count)), shuffle: true);
}
