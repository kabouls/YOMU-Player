using System.Text.RegularExpressions;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

public enum PageKind { Home, Browse, Search, Library, Collection, Artist, Player, Profile }

// Tabs of the player page, in the same order YouTube Music shows them.
public enum PlayerTab { Queue, Lyrics }

public sealed record PageRequest(string Title, PageKind Kind, string? BrowseId = null, string? BrowseParams = null, string? Query = null, MediaItem? Item = null, PlayerTab? Tab = null);

public static class Fmt
{
    public static string Time(TimeSpan? t) =>
        t is null ? "" : t.Value.TotalHours >= 1 ? t.Value.ToString(@"h\:mm\:ss") : t.Value.ToString(@"m\:ss");
}

public static class Thumbnails
{
    // Googleusercontent art URLs carry their size in the URL (=w226-h226-... or =s192); ask for what we draw.
    private static readonly Regex Size = new(@"=(w\d+-h\d+|s\d+)", RegexOptions.Compiled);
    private static readonly Dictionary<string, WeakReference<ImageSource>> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static ImageSource? Create(string? url, int px)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var resized = Size.Replace(url, m => m.Groups[1].Value.StartsWith("w") ? $"=w{px}-h{px}" : $"=s{px}");
        if (!Uri.TryCreate(resized, UriKind.Absolute, out var uri)) return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(resized, out var weak) && weak.TryGetTarget(out var cached))
                return cached;
            if (Cache.Count > 240)
            {
                foreach (var key in Cache.Where(kv => !kv.Value.TryGetTarget(out _)).Select(kv => kv.Key).ToList())
                    Cache.Remove(key);
            }
            var image = new BitmapImage(uri) { DecodePixelWidth = px };
            Cache[resized] = new WeakReference<ImageSource>(image);
            return image;
        }
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> _run;

    public RelayCommand(Func<Task> run) => _run = run;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter)
    {
        try { await _run(); }
        catch (Exception e) { AppServices.ReportError(e.Message); }
    }
}

public sealed class ItemVM
{
    private ImageSource? _art;
    private ImageSource? _smallArt;
    private bool _artLoaded;
    private bool _smallArtLoaded;

    public ItemVM(MediaItem item)
    {
        Item = item;
        ActivateCommand = new RelayCommand(() => AppServices.ActivateAsync(item));
    }

    public MediaItem Item { get; }
    public ICommand ActivateCommand { get; }
    public string Title => Item.Title;
    public string Subtitle => Item.Subtitle;
    public string Trailing => Fmt.Time(Item.Track?.Duration);

    public ImageSource? Art
    {
        get
        {
            if (!_artLoaded)
            {
                _art = Thumbnails.Create(Item.ThumbnailUrl, 320);
                _artLoaded = true;
            }
            return _art;
        }
    }

    public ImageSource? SmallArt
    {
        get
        {
            if (!_smallArtLoaded)
            {
                _smallArt = Thumbnails.Create(Item.ThumbnailUrl, 96);
                _smallArtLoaded = true;
            }
            return _smallArt;
        }
    }

    // ---- type distinction: label pill, color and (for playlists) a stacked-card look ----
    public string KindLabel => Kinds.Label(Item.Kind);

    public Brush KindBrush => Kinds.Brush(Item.Kind);

    public Visibility StackVisibility => Item.Kind == MediaKind.Playlist ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlayOverlayVisibility => Item.IsTrack ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => KindLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExplicitVisibility =>
        Item.Track?.Explicit == true ? Visibility.Visible : Visibility.Collapsed;

    public CornerRadius ArtRadius => Item.Kind == MediaKind.Artist ? new CornerRadius(80) : new CornerRadius(8);
    public CornerRadius SmallArtRadius => Item.Kind == MediaKind.Artist ? new CornerRadius(24) : new CornerRadius(6);

    public Brush Stripe
    {
        get
        {
            var c = Item.Color ?? 0xFF808080u;
            return new SolidColorBrush(Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c));
        }
    }
}

public sealed class ShelfVM
{
    public ShelfVM(Shelf shelf)
    {
        Title = shelf.Title;
        Style = shelf.Style;
        Items = shelf.Items.Select(i => new ItemVM(i)).ToList();
    }

    public string Title { get; }
    public ShelfStyle Style { get; }
    public IReadOnlyList<ItemVM> Items { get; }

    public Visibility TitleVisibility => string.IsNullOrWhiteSpace(Title) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CarouselVisibility => Style == ShelfStyle.Chips ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CardsVisibility => Style == ShelfStyle.Cards ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ListVisibility => Style == ShelfStyle.List ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChipsVisibility => Style == ShelfStyle.Chips ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class TrackVM
{
    private ImageSource? _art;
    private bool _artLoaded;

    public TrackVM(Track track, int index, Func<int, Task> play, bool canEditPlaylist = false)
    {
        Track = track;
        Number = (index + 1).ToString();
        CanEditPlaylist = canEditPlaylist;
        PlayCommand = new RelayCommand(() => play(index));
        Artists = track.Artists.Select((a, i) => new ArtistLinkVM(a, i == track.Artists.Count - 1)).ToList();
    }

    public bool CanEditPlaylist { get; }

    // Artist names are clickable links to their pages; plain text is only the fallback when none are known.
    public IReadOnlyList<ArtistLinkVM> Artists { get; }
    public Visibility LinksVisibility => Artists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlainSubtitleVisibility => Artists.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

    public Track Track { get; }
    public string Number { get; }
    public ICommand PlayCommand { get; }
    public string Title => Track.Title;
    public string Subtitle => Track.ArtistLine;
    public string Album => Track.Album ?? "";
    public string Duration => Fmt.Time(Track.Duration);
    public ImageSource? Art
    {
        get
        {
            if (!_artLoaded)
            {
                _art = Thumbnails.Create(Track.ThumbnailUrl, 96);
                _artLoaded = true;
            }
            return _art;
        }
    }
    public Visibility ExplicitVisibility => Track.Explicit ? Visibility.Visible : Visibility.Collapsed;
}

// A row in the player page's "Up next" list. OrderIndex is the position in play order, not the track index.
public sealed class QueueRowVM
{
    private static readonly Brush CurrentRow = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A));
    private static readonly Brush NormalRow = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));
    private ImageSource? _art;
    private bool _artLoaded;

    public QueueRowVM(Track track, int orderIndex, bool isCurrent, Func<int, Task> play, bool isPlayed = false)
    {
        Track = track;
        OrderIndex = orderIndex;
        IsCurrent = isCurrent;
        IsPlayed = isPlayed;
        PlayCommand = new RelayCommand(() => play(orderIndex));
    }

    // A previously played row from this listening session. It is not part of the playable queue order.
    public QueueRowVM(Track track, Func<Task> play)
    {
        Track = track;
        OrderIndex = -1;
        IsHistory = true;
        PlayCommand = new RelayCommand(play);
    }

    public Track Track { get; }
    public int OrderIndex { get; }
    public bool IsCurrent { get; }
    public bool IsPlayed { get; }
    public bool IsHistory { get; }
    public ICommand PlayCommand { get; }

    public string Title => Track.Title;
    public string Subtitle => Track.ArtistLine;
    public string Duration => Fmt.Time(Track.Duration);
    public string StatusGlyph => IsCurrent ? "\uE768" : IsPlayed ? "\uE73E" : "\uE768";
    public double StatusOpacity => IsCurrent ? 1.0 : IsPlayed ? 0.55 : 0.85;
    public Windows.UI.Text.FontWeight TitleWeight => IsCurrent
        ? Microsoft.UI.Text.FontWeights.SemiBold
        : Microsoft.UI.Text.FontWeights.Normal;
    public ImageSource? Art
    {
        get
        {
            if (!_artLoaded)
            {
                _art = Thumbnails.Create(Track.ThumbnailUrl, 96);
                _artLoaded = true;
            }
            return _art;
        }
    }
    public Brush RowBackground => IsCurrent ? CurrentRow : NormalRow;
    public Visibility PlayIconVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExplicitVisibility => Track.Explicit ? Visibility.Visible : Visibility.Collapsed;
}

// Plain-text line in the predictive search dropdown
public sealed record TextSuggestion(string Text);// Chooses between a rich result row (song/artist/album with art) and a plain text suggestion
public sealed class SuggestionSelector : DataTemplateSelector
{
    public DataTemplate? Rich { get; set; }
    public DataTemplate? Text { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is ItemVM ? Rich : Text;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

// Type labels and colors shared by result tags, search filters and dialogs
public static class Kinds
{
    public static string Label(MediaKind kind) => kind switch
    {
        MediaKind.Song => "SONG",
        MediaKind.Video => "VIDEO",
        MediaKind.Album => "ALBUM",
        MediaKind.Playlist => "PLAYLIST",
        MediaKind.Artist => "ARTIST",
        _ => ""
    };

    public static Color ColorOf(MediaKind kind) => kind switch
    {
        MediaKind.Playlist => Color.FromArgb(0xEE, 0x7B, 0x3F, 0xE4),
        MediaKind.Album => Color.FromArgb(0xEE, 0x2B, 0x7D, 0xE9),
        MediaKind.Artist => Color.FromArgb(0xEE, 0x1F, 0x9D, 0x55),
        MediaKind.Video => Color.FromArgb(0xEE, 0xD9, 0x36, 0x36),
        _ => Color.FromArgb(0xEE, 0x3A, 0x3A, 0x3A)
    };

    public static Brush Brush(MediaKind kind) => new SolidColorBrush(ColorOf(kind));
}

public sealed class ArtistLinkVM
{
    public ArtistLinkVM(Artist artist, bool last)
    {
        Label = last ? artist.Name : artist.Name + ",";
        OpenCommand = new RelayCommand(() =>
        {
            AppServices.OpenArtist(artist);
            return Task.CompletedTask;
        });
    }

    public string Label { get; }
    public ICommand OpenCommand { get; }
}

// What a pasted link points to, shown for confirmation before anything plays or opens
public sealed record LinkPreview(MediaKind Kind, string Title, string Detail, string? ThumbnailUrl, string ActionText);
