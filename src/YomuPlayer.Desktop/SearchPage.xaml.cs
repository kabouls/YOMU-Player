using System.Collections.ObjectModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

public sealed partial class SearchPage : Page
{
    private static readonly (string Label, MediaKind? Kind)[] Filters =
    {
        ("All", null),
        ("Songs", MediaKind.Song),
        ("Artists", MediaKind.Artist),
        ("Albums", MediaKind.Album),
        ("Videos", MediaKind.Video),
        ("Playlists", MediaKind.Playlist)
    };

    private readonly HashSet<MediaKind> _filter = new();
    private readonly List<Border> _chips = new();
    private IReadOnlyList<MediaItem> _results = Array.Empty<MediaItem>();
    private MediaItem? _hero;
    private IReadOnlyList<Track> _heroSongs = Array.Empty<Track>();

    public ObservableCollection<ItemVM> Items { get; } = new();

    public SearchPage()
    {
        InitializeComponent();
        BuildChips();
    }

    // ---- filter tags ----

    private void BuildChips()
    {
        foreach (var (label, kind) in Filters)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 6, 14, 6),
                BorderThickness = new Thickness(1),
                Tag = kind,
                Child = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold }
            };
            chip.Tapped += Chip_Tapped;
            _chips.Add(chip);
            FilterPanel.Children.Add(chip);
        }
        UpdateChips();
    }

    private void Chip_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var kind = (MediaKind?)((Border)sender).Tag;
        if (kind is null) _filter.Clear();
        else if (!_filter.Add(kind.Value)) _filter.Remove(kind.Value);

        UpdateChips();
        ApplyFilter();
    }

    private void UpdateChips()
    {
        foreach (var chip in _chips)
        {
            var kind = (MediaKind?)chip.Tag;
            var selected = kind is null ? _filter.Count == 0 : _filter.Contains(kind.Value);
            var color = kind is null ? Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2) : Kinds.ColorOf(kind.Value);
            var text = (TextBlock)chip.Child;

            if (selected)
            {
                chip.Background = new SolidColorBrush(color);
                chip.BorderBrush = new SolidColorBrush(color);
                text.Foreground = new SolidColorBrush(kind is null ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
            }
            else
            {
                chip.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                chip.BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, color.R, color.G, color.B));
                text.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xDD, 0xDD, 0xDD));
            }
        }
    }

    // ---- results ----

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not PageRequest request || string.IsNullOrWhiteSpace(request.Query)) return;

        PageTitle.Text = request.Title;
        Busy.IsActive = true;
        try
        {
            _results = await AppServices.GetSearchAsync(request.Query);
            ApplyFilter();
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

    private void ApplyFilter()
    {
        var shown = _filter.Count == 0 ? _results : _results.Where(r => _filter.Contains(r.Kind)).ToList();

        Items.Clear();
        HeroPanel.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown.Count == 0) return;

        ShowHero(shown);
        foreach (var r in shown.Skip(1)) Items.Add(new ItemVM(r));
    }

    private void ShowHero(IReadOnlyList<MediaItem> shown)
    {
        var hero = shown[0];
        _hero = hero;
        var vm = new ItemVM(hero);

        HeroTitle.Text = hero.Title;
        HeroSubtitle.Text = hero.Subtitle;
        HeroKind.Text = vm.KindLabel;
        HeroPill.Background = vm.KindBrush;
        HeroArt.ImageSource = Thumbnails.Create(hero.ThumbnailUrl, 400);
        HeroArtBorder.CornerRadius = hero.Kind == MediaKind.Artist ? new CornerRadius(84) : new CornerRadius(12);
        HeroShuffle.Visibility = Visibility.Collapsed;
        HeroOpen.Visibility = Visibility.Collapsed;
        HeroPrimaryIcon.Glyph = "\uE768";
        _heroSongs = Array.Empty<Track>();

        switch (hero.Kind)
        {
            case MediaKind.Artist:
                // Play = the artist's top songs from the full result list
                _heroSongs = _results
                    .Where(i => i.Kind == MediaKind.Song && i.Track is not null
                                && i.Track.Artists.Any(a => string.Equals(a.Name, hero.Title, StringComparison.OrdinalIgnoreCase)))
                    .Select(i => i.Track!).Take(10).ToList();
                if (_heroSongs.Count == 0)
                    _heroSongs = _results.Where(i => i.Kind == MediaKind.Song && i.Track is not null)
                        .Select(i => i.Track!).Take(5).ToList();
                HeroPrimaryText.Text = "Play top songs";
                HeroShuffle.Visibility = _heroSongs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                HeroOpen.Visibility = hero.BrowseId is not null ? Visibility.Visible : Visibility.Collapsed;
                AppServices.Prefetch(_heroSongs.FirstOrDefault());
                break;

            case MediaKind.Album or MediaKind.Playlist:
                HeroPrimaryText.Text = "Open";
                HeroPrimaryIcon.Glyph = "\uE8A7";
                break;

            default:
                HeroPrimaryText.Text = "Play";
                AppServices.Prefetch(hero.Track); // the most likely click: have it ready
                break;
        }

        HeroPanel.Visibility = Visibility.Visible;
    }

    private async void HeroPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_hero is null) return;
        if (_hero.Kind == MediaKind.Artist)
            await AppServices.PlayCollectionAsync(_heroSongs, 0, shuffle: false);
        else
            await AppServices.ActivateAsync(_hero);
    }

    private async void HeroOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_hero is not null) await AppServices.ActivateAsync(_hero);
    }

    private async void HeroShuffle_Click(object sender, RoutedEventArgs e) =>
        await AppServices.PlayCollectionAsync(_heroSongs, Random.Shared.Next(Math.Max(1, _heroSongs.Count)), shuffle: true);
}
