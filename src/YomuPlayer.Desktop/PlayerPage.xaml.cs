using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// The full player page: a large centered artwork on the left, and Up next / Lyrics on the right.
// The library sidebar stays visible; the bottom bar owns playback and track metadata.
public sealed partial class PlayerPage : Page
{
    private readonly PlayerService _player = AppServices.Player;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    private string? _shownTrackId = "?";
    private string? _lyricsFor;
    private string? _lyricsLoadingFor;
    private CancellationTokenSource? _lyricsCts;
    private bool _loaded;
    private bool _selectingTab;
    private bool? _narrow;
    private PlayerTab? _pendingTab;

    public ObservableCollection<QueueRowVM> QueueRows { get; } = new();

    public PlayerPage()
    {
        InitializeComponent();

        // A tab requested while navigating is applied once the controls exist; setting it in OnNavigatedTo can be
        // overwritten by the SelectorBar's initial IsSelected state during its first layout pass.
        Loaded += (_, _) =>
        {
            ApplyLayout(ActualWidth, ActualHeight);
            FitArt();
            if (_pendingTab is { } tab)
            {
                _pendingTab = null;
                SelectTab(tab);
            }
        };

        // The art is sized from the pane, which can settle after the page's first SizeChanged.
        LeftPane.SizeChanged += (_, _) => FitArt();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _player.TrackChanged += OnTrackChanged;
        _player.Queue.Changed += OnQueueChanged;

        _loaded = true;
        SelectTab(PlayerTab.Queue, load: false);
        RebuildQueue();
        RefreshTrack(e.Parameter as PageRequest);

        _pendingTab = (e.Parameter as PageRequest)?.Tab;
        if (IsLoaded && _pendingTab is { } tab)
        {
            _pendingTab = null;
            SelectTab(tab);
        }
        ApplyLayout(ActualWidth, ActualHeight);
        FitArt();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _loaded = false;
        _player.TrackChanged -= OnTrackChanged;
        _player.Queue.Changed -= OnQueueChanged;
        _lyricsCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    // Called by MainWindow when the page is already open and another tab is requested from the bar.
    public void SelectTab(PlayerTab tab) => SelectTab(tab, load: true);

    private void SelectTab(PlayerTab tab, bool load)
    {
        _selectingTab = true;
        Tabs.SelectedItem = tab == PlayerTab.Lyrics ? LyricsTab : QueueTab;
        _selectingTab = false;

        QueuePanel.Visibility = tab == PlayerTab.Queue ? Visibility.Visible : Visibility.Collapsed;
        LyricsPanel.Visibility = tab == PlayerTab.Lyrics ? Visibility.Visible : Visibility.Collapsed;

        if (!load || !_loaded) return;
        if (tab == PlayerTab.Lyrics) _ = LoadLyricsAsync();
    }

    private PlayerTab CurrentTab => Tabs.SelectedItem == LyricsTab ? PlayerTab.Lyrics : PlayerTab.Queue;

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_selectingTab) return;
        SelectTab(CurrentTab, load: true);
    }

    // ---- track ----

    private void OnTrackChanged(Track? track) => _ui.TryEnqueue(() => RefreshTrack(null));

    private void OnQueueChanged() => _ui.TryEnqueue(RebuildQueue);

    private void RefreshTrack(PageRequest? request)
    {
        var track = _player.Queue.Current;
        var changed = track?.VideoId != _shownTrackId;

        var artPixels = (int)Math.Clamp(
            Math.Max(LeftPane.ActualWidth, 1200) * (XamlRoot?.RasterizationScale ?? 1.0),
            1200, 1800);
        SetCover(track, artPixels);
        ArtPlaceholder.Visibility = string.IsNullOrEmpty(track?.ThumbnailUrl)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateQueueSource();
        DispatcherQueue.TryEnqueue(FitArt);

        if (changed)
        {
            _shownTrackId = track?.VideoId;
            ResetContent();
        }

        if (request?.Tab is null && changed && CurrentTab == PlayerTab.Lyrics)
            _ = LoadLyricsAsync();
    }

    private void ResetContent()
    {
        _lyricsCts?.Cancel();
        _lyricsFor = null;
        _lyricsLoadingFor = null;

        LyricsBlock.Text = "";
        LyricsFooter.Text = "";
        LyricsScroller.Visibility = Visibility.Collapsed;
        LyricsBusy.IsActive = false;
        LyricsBusy.Visibility = Visibility.Collapsed;
        if (_player.Queue.Current is null)
            ShowLyricsMessage("Nothing playing.", false);
        else
            LyricsMessage.Visibility = Visibility.Collapsed;
    }

    // ---- up next ----

    private void RebuildQueue()
    {
        if (!_loaded) return;

        var entries = _player.Queue.Entries();
        QueueRows.Clear();
        foreach (var entry in entries)
            QueueRows.Add(new QueueRowVM(entry.Track, entry.OrderIndex, entry.IsCurrent, PlayAtAsync,
                isPlayed: entry.OrderIndex < _player.Queue.Position));

        UpdateQueueSource();
        QueueEmpty.Visibility = QueueRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateQueueSource()
    {
        var title = _player.Queue.SourceTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            var track = _player.Queue.Current;
            title = track is null ? null : AppServices.Yt.TryGetCachedExtras(track.VideoId)?.QueueTitle;
        }
        QueueSourceText.Text = string.IsNullOrWhiteSpace(title) ? "This queue" : title;
    }

    private Task PlayAtAsync(int orderIndex) => _player.PlayOrderIndexAsync(orderIndex);

    private void QueueRow_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement row || row.DataContext is not QueueRowVM vm) return;

        if (!e.TryGetPosition(row, out var position))
            position = new Windows.Foundation.Point(row.ActualWidth / 2, row.ActualHeight / 2);

        ContextMenus.Show(row, ContextMenus.FromTrack(vm.Track), position, flyout =>
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem
            {
                Text = "Remove from queue",
                Icon = new FontIcon { Glyph = "\uE74D" },
                IsEnabled = !vm.IsCurrent
            };
            remove.Click += (_, _) => _player.Queue.RemoveAt(vm.OrderIndex);
            flyout.Items.Add(remove);
        });
        e.Handled = true;
    }

    private async void SaveQueue_Click(object sender, RoutedEventArgs e)
    {
        var tracks = _player.Queue.OrderedTracks;
        if (tracks.Count == 0)
        {
            AppServices.ReportError("There is nothing in the queue to save.");
            return;
        }
        if (!AppServices.Auth.IsSignedIn)
        {
            AppServices.ReportError("Sign in with Google to save this queue to a playlist.");
            return;
        }

        var dialog = new SaveQueueDialog(tracks);
        await Dialogs.ShowAsync(dialog);
        if (!string.IsNullOrWhiteSpace(dialog.SavedMessage))
            AppServices.ReportInfo(dialog.SavedMessage);
    }

    // ---- lyrics ----

    private async Task LoadLyricsAsync()
    {
        var track = _player.Queue.Current;
        if (track is null)
        {
            ShowLyricsMessage("Play a song to see its lyrics.", false);
            return;
        }
        if (_lyricsFor == track.VideoId || _lyricsLoadingFor == track.VideoId)
        {
            if (!string.IsNullOrEmpty(LyricsBlock.Text)) ShowLyrics();
            return;
        }

        _lyricsCts?.Cancel();
        var cts = _lyricsCts = new CancellationTokenSource();
        _lyricsLoadingFor = track.VideoId;

        LyricsBusy.IsActive = true;
        LyricsBusy.Visibility = Visibility.Visible;
        LyricsScroller.Visibility = Visibility.Collapsed;
        LyricsMessage.Visibility = Visibility.Collapsed;
        LyricsFooter.Text = "";

        try
        {
            var lyrics = await AppServices.GetLyricsAsync(track, cts.Token);
            if (cts.IsCancellationRequested || _player.Queue.Current?.VideoId != track.VideoId) return;

            _lyricsFor = track.VideoId;
            if (lyrics is null || lyrics.IsEmpty)
            {
                ShowLyricsMessage($"Lyrics aren't available for \u201C{track.Title}\u201D.", true);
            }
            else
            {
                LyricsBlock.Text = lyrics.Text;
                LyricsFooter.Text = lyrics.Footer ?? "";
                LyricsScroller.ChangeView(null, 0, null, true);
                ShowLyrics();
            }
        }
        catch (OperationCanceledException)
        {
            // a newer track or tab replaced this request
        }
        catch (Exception ex)
        {
            ShowLyricsMessage(ex.Message, true);
        }
        finally
        {
            if (_lyricsLoadingFor == track.VideoId) _lyricsLoadingFor = null;
            if (!cts.IsCancellationRequested)
            {
                LyricsBusy.IsActive = false;
                LyricsBusy.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ShowLyrics()
    {
        LyricsScroller.Visibility = Visibility.Visible;
        LyricsMessage.Visibility = Visibility.Collapsed;
        LyricsBusy.IsActive = false;
        LyricsBusy.Visibility = Visibility.Collapsed;
    }

    private void ShowLyricsMessage(string text, bool offerSearch)
    {
        LyricsMessageText.Text = text;
        LyricsSearchButton.Visibility = offerSearch ? Visibility.Visible : Visibility.Collapsed;
        LyricsMessage.Visibility = Visibility.Visible;
        LyricsScroller.Visibility = Visibility.Collapsed;
    }

    private async void LyricsSearchButton_Click(object sender, RoutedEventArgs e)
    {
        var track = _player.Queue.Current;
        if (track is null) return;
        var query = Uri.EscapeDataString($"{track.Title} {track.ArtistLine} lyrics");
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.google.com/search?q=" + query));
        }
        catch (Exception ex)
        {
            AppServices.ReportError(ex.Message);
        }
    }

    // ---- responsive layout ----

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyLayout(e.NewSize.Width, e.NewSize.Height);
        DispatcherQueue.TryEnqueue(FitArt);
    }

    private void ApplyLayout(double width, double? height = null)
    {
        if (width <= 0) return;

        var narrow = width < 760;
        if (_narrow != narrow)
        {
            _narrow = narrow;
            if (narrow)
            {
                TopRow.Height = new GridLength(55, GridUnitType.Star);
                BottomRow.Height = new GridLength(45, GridUnitType.Star);
                LeftColumn.Width = new GridLength(1, GridUnitType.Star);
                RightColumn.Width = new GridLength(0);

                Grid.SetRow(LeftPane, 0);
                Grid.SetColumn(LeftPane, 0);
                Grid.SetColumnSpan(LeftPane, 2);
                Grid.SetRow(RightPane, 1);
                Grid.SetColumn(RightPane, 0);
                Grid.SetColumnSpan(RightPane, 2);
                RightPane.Margin = new Thickness(0, 10, 0, 0);
            }
            else
            {
                TopRow.Height = new GridLength(1, GridUnitType.Star);
                BottomRow.Height = new GridLength(0);
                LeftColumn.Width = new GridLength(1, GridUnitType.Star);
                RightColumn.Width = new GridLength(400);

                Grid.SetRow(LeftPane, 0);
                Grid.SetColumn(LeftPane, 0);
                Grid.SetColumnSpan(LeftPane, 1);
                Grid.SetRow(RightPane, 0);
                Grid.SetColumn(RightPane, 1);
                Grid.SetColumnSpan(RightPane, 1);
                RightPane.Margin = new Thickness(0);
            }
        }

        FitArt();
    }

    private void SetCover(Track? track, int pixels)
    {
        if (track is null)
        {
            CoverBrush.ImageSource = null;
            return;
        }

        var candidates = new List<string>();
        var thumb = track.ThumbnailUrl;
        var isYoutubeThumb = thumb?.Contains("i.ytimg.com", StringComparison.OrdinalIgnoreCase) == true
                             || thumb?.Contains("yt3.googleusercontent.com", StringComparison.OrdinalIgnoreCase) == true;

        // YTM often returns a 480 px hqdefault or a 544 px channel avatar. The video still is
        // available at 1280 px (maxresdefault), then sddefault is the safe fallback.
        if (!string.IsNullOrWhiteSpace(track.VideoId) && (isYoutubeThumb || string.IsNullOrWhiteSpace(thumb)))
            candidates.Add($"https://i.ytimg.com/vi/{track.VideoId}/maxresdefault.jpg");
        if (!string.IsNullOrWhiteSpace(track.VideoId))
            candidates.Add($"https://i.ytimg.com/vi/{track.VideoId}/sddefault.jpg");
        if (!string.IsNullOrWhiteSpace(thumb)) candidates.Add(thumb);

        TryCover(track, pixels, candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), 0);
    }

    private void TryCover(Track track, int pixels, IReadOnlyList<string> candidates, int index)
    {
        while (index < candidates.Count)
        {
            var candidate = candidates[index++];
            if (Thumbnails.Create(candidate, pixels) is BitmapImage image)
            {
                var next = index;
                image.ImageFailed += (_, _) =>
                {
                    if (ReferenceEquals(CoverBrush.ImageSource, image))
                        TryCover(track, pixels, candidates, next);
                };
                CoverBrush.ImageSource = image;
                return;
            }
        }

        // Last-resort thumbnail is better than a blank pane, even if it is only 544 px.
        CoverBrush.ImageSource = Thumbnails.Create(track.ThumbnailUrl, Math.Min(pixels, 544));
    }

    private void FitArt()
    {
        var width = LeftPane.ActualWidth;
        var height = LeftPane.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var size = Math.Max(120, Math.Min(width, height) - 8);
        if (Math.Abs(ArtHost.Width - size) < 1) return;
        ArtHost.Width = size;
        ArtHost.Height = size;
    }
}
