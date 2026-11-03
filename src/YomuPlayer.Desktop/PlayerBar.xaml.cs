using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

public sealed partial class PlayerBar : UserControl
{
    private readonly PlayerService _player;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _initialized;
    private bool _updating;
    private bool _dragging;
    private string _artist = "";
    private bool _active = true;
    private bool _syncingVolume;
    private string? _ratingTrackId;
    private TrackRating _rating;
    private CancellationTokenSource? _ratingCts;

    private static readonly SolidColorBrush IdleRatingBrush = new(Microsoft.UI.Colors.Transparent);
    private static readonly SolidColorBrush IdleIconBrush = new(Microsoft.UI.Colors.White);

    // Raised on every UI tick so the window can keep SMTC in sync with the same clock.
    public event Action? Ticked;

    // Raised when the bar asks for the full player page (art/title).
    public event Action<PlayerTab>? OpenPlayerRequested;

    // Asks the window to open the player page, or close it when it is already open.
    public event Action? TogglePlayerRequested;

    public PlayerBar()
    {
        InitializeComponent();
        _player = AppServices.Player;
        VolumeSlider.Value = _player.Volume;
        QualityRadios.SelectedIndex = (int)_player.Quality;
        AutoplayToggle.IsOn = _player.Autoplay;

        _player.TrackChanged += t => _ui.TryEnqueue(() => OnTrackChanged(t));
        _player.VolumeChanged += v => _ui.TryEnqueue(() => SyncVolume(v));
        AppServices.RatingChanged += (videoId, rating) => _ui.TryEnqueue(() =>
        {
            if (_ratingTrackId == videoId)
            {
                _rating = rating;
                UpdateRatingVisuals();
            }
        });
        AppServices.Auth.Changed += () => _ui.TryEnqueue(() => OnTrackChanged(_player.Queue.Current));

        // Seek only when the user lets go of the thumb, so the 500 ms position updates never fight a drag.
        PositionSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _dragging = true), true);
        PositionSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        PositionSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndDrag()), true);

        // The slider consumes wheel input by default; catch it before that and treat the wheel as a volume knob.
        VolumePanel.AddHandler(PointerWheelChangedEvent,
            new PointerEventHandler(VolumePanel_PointerWheelChanged), true);

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _initialized = true;
        SyncVolume(_player.Volume);
    }

    // While a track is being prepared the previous audio is already stopped; make that unmistakable.
    private void UpdateLoading()
    {
        var loading = _player.IsLoading;
        var vis = loading ? Visibility.Visible : Visibility.Collapsed;

        ResolvingBar.Visibility = vis;
        ArtScrim.Visibility = vis;
        ArtRing.Visibility = vis;
        ArtRing.IsActive = loading;
        PlayRing.Visibility = vis;
        PlayRing.IsActive = loading;
        PlayPauseIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        PlayPauseIcon.Glyph = _player.IsPlaying ? "\uE769" : "\uE768";
        NowTitle.Opacity = loading ? 0.6 : 1.0;
        NowArtist.Text = loading ? "Loading\u2026" : _artist;
    }

    // Hidden or minimized: tick slowly and skip drawing. The window still gets Ticked for SMTC.
    public void SetActive(bool active)
    {
        _active = active;
        _timer.Interval = TimeSpan.FromMilliseconds(active ? 500 : 2000);
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _player.Seek(TimeSpan.FromSeconds(PositionSlider.Value));
    }

    private void OnTrackChanged(Track? t)
    {
        NowTitle.Text = t?.Title ?? "Nothing playing";
        _artist = t?.ArtistLine ?? "";
        NowArtist.Text = _artist;
        NowExplicitBadge.Visibility = t?.Explicit == true ? Visibility.Visible : Visibility.Collapsed;
        NowArt.ImageSource = Thumbnails.Create(t?.ThumbnailUrl, 128);
        UpdateLoading();

        _updating = true;
        PositionSlider.Value = 0;
        _updating = false;
        TimeText.Text = $"0:00 / {Fmt.Time(t?.Duration)}";
        UpdateRating(t);
    }

    private void UpdateRating(Track? track)
    {
        _ratingCts?.Cancel();
        _ratingTrackId = track?.VideoId;
        _rating = TrackRating.None;
        var visible = track is null ? Visibility.Collapsed : Visibility.Visible;
        LikeButton.Visibility = visible;
        DislikeButton.Visibility = visible;
        var enabled = track is not null && AppServices.Auth.IsSignedIn;
        LikeButton.IsEnabled = enabled;
        DislikeButton.IsEnabled = enabled;
        UpdateRatingVisuals();

        if (track is not null && enabled) _ = LoadRatingAsync(track);
    }

    private async Task LoadRatingAsync(Track track)
    {
        var cts = new CancellationTokenSource();
        _ratingCts = cts;
        try
        {
            var rating = await AppServices.GetTrackRatingAsync(track, cts.Token);
            if (cts.IsCancellationRequested || _player.Queue.Current?.VideoId != track.VideoId) return;
            _rating = rating;
            UpdateRatingVisuals();
        }
        catch (OperationCanceledException)
        {
            // a new track superseded this lookup
        }
        catch
        {
            // unknown rating: keep neutral buttons
        }
    }

    private void UpdateRatingVisuals()
    {
        var liked = _rating == TrackRating.Like;
        var disliked = _rating == TrackRating.Dislike;
        LikeIcon.Visibility = liked ? Visibility.Collapsed : Visibility.Visible;
        LikeFilledIcon.Visibility = liked ? Visibility.Visible : Visibility.Collapsed;
        DislikeIcon.Visibility = disliked ? Visibility.Collapsed : Visibility.Visible;
        DislikeFilledIcon.Visibility = disliked ? Visibility.Visible : Visibility.Collapsed;
        LikeIcon.Opacity = 0.55;
        DislikeIcon.Opacity = 0.55;
        LikeIcon.Foreground = IdleIconBrush;
        DislikeIcon.Foreground = IdleIconBrush;
        LikeButton.Background = IdleRatingBrush;
        DislikeButton.Background = IdleRatingBrush;
        ToolTipService.SetToolTip(LikeButton,
            !AppServices.Auth.IsSignedIn ? "Sign in to like songs"
            : liked ? "Remove like" : "Like this song");
        ToolTipService.SetToolTip(DislikeButton,
            !AppServices.Auth.IsSignedIn ? "Sign in to dislike songs"
            : disliked ? "Remove dislike" : "Dislike this song");
    }

    private async void LikeButton_Click(object sender, RoutedEventArgs e) =>
        await ToggleRatingAsync(TrackRating.Like);

    private async void DislikeButton_Click(object sender, RoutedEventArgs e) =>
        await ToggleRatingAsync(TrackRating.Dislike);

    private async Task ToggleRatingAsync(TrackRating target)
    {
        var track = _player.Queue.Current;
        if (track is null) return;
        if (!AppServices.Auth.IsSignedIn)
        {
            AppServices.ReportError("Sign in with Google to like or dislike songs.");
            return;
        }

        var next = _rating == target ? TrackRating.None : target;
        LikeButton.IsEnabled = false;
        DislikeButton.IsEnabled = false;
        try
        {
            await AppServices.SetTrackRatingAsync(track, next);
            if (_player.Queue.Current?.VideoId == track.VideoId)
            {
                _rating = next;
                UpdateRatingVisuals();
            }
        }
        catch (Exception ex)
        {
            AppServices.ReportError(ex.Message);
        }
        finally
        {
            var enabled = AppServices.Auth.IsSignedIn;
            LikeButton.IsEnabled = enabled;
            DislikeButton.IsEnabled = enabled;
        }
    }

    // Polled: LibVLC reports state changes late, so the bar reads the player directly.
    private void Tick()
    {
        if (!_active)
        {
            Ticked?.Invoke();
            return;
        }

        UpdateLoading();

        // "Play all" turns shuffle off and "Shuffle" turns it on from other pages; keep the toggle honest.
        if ((ShuffleToggle.IsChecked == true) != _player.Queue.Shuffle)
            ShuffleToggle.IsChecked = _player.Queue.Shuffle;

        if (!_dragging)
        {
            var duration = _player.Duration ?? _player.Queue.Current?.Duration;
            var position = _player.Position;

            _updating = true;
            try
            {
                if (duration is { } d && d > TimeSpan.Zero)
                {
                    PositionSlider.Maximum = d.TotalSeconds;
                    PositionSlider.Value = Math.Min(position.TotalSeconds, d.TotalSeconds);
                }
            }
            finally
            {
                _updating = false;
            }
            TimeText.Text = $"{Fmt.Time(position)} / {Fmt.Time(duration)}";
        }

        Ticked?.Invoke();
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _player.TogglePause();

    private async void NextButton_Click(object sender, RoutedEventArgs e) => await _player.NextAsync();

    private async void PrevButton_Click(object sender, RoutedEventArgs e) => await _player.PreviousAsync();

    private void ShuffleToggle_Click(object sender, RoutedEventArgs e) =>
        _player.Queue.Shuffle = ShuffleToggle.IsChecked == true;

    private void NowPlayingButton_Click(object sender, RoutedEventArgs e) =>
        OpenPlayerRequested?.Invoke(PlayerTab.Queue);

    private void TogglePlayerButton_Click(object sender, RoutedEventArgs e) =>
        TogglePlayerRequested?.Invoke();

    // Called by MainWindow after navigation so the chevron reflects whether the player page is showing.
    public void SetPlayerOpen(bool open)
    {
        TogglePlayerIcon.Glyph = open ? "\uE70D" : "\uE70E";
        ToolTipService.SetToolTip(TogglePlayerButton, open ? "Close player" : "Open player");
    }

    // Only this PC exists today; the list is fed by AppServices so a phone/TV endpoint shows up without UI changes.
    private void DevicesButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (var device in AppServices.Devices)
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = device.IsLocal ? device.DeviceName + " (this device)" : device.DeviceName,
                Icon = new FontIcon { Glyph = device.Kind == DeviceKind.Windows ? "\uE7F4" : "\uE772" },
                IsEnabled = false
            });
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Phone and TV control is coming with the mobile app",
            IsEnabled = false
        });
        flyout.ShowAt(DevicesButton);
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e) => _player.ToggleMute();

    private void SyncVolume(int volume)
    {
        if ((int)VolumeSlider.Value != volume)
        {
            _syncingVolume = true;
            VolumeSlider.Value = volume;
            _syncingVolume = false;
        }
        VolumeIcon.Glyph = volume == 0 ? "\uE74F" : "\uE767";
        ToolTipService.SetToolTip(MuteButton, volume == 0 ? "Unmute" : "Mute");
    }

    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        _player.Queue.Repeat = _player.Queue.Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off
        };
        RepeatIcon.Glyph = _player.Queue.Repeat == RepeatMode.One ? "\uE8ED" : "\uE8EE";
        RepeatIcon.Opacity = _player.Queue.Repeat == RepeatMode.Off ? 0.5 : 1.0;
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_initialized || _syncingVolume) return;
        _player.Volume = (int)e.NewValue;
    }

    private void VolumePanel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(VolumePanel).Properties.MouseWheelDelta;
        if (delta == 0) return;

        var next = Math.Clamp(_player.Volume + (delta > 0 ? 5 : -5), 0, 100);
        _player.Volume = next;
        SyncVolume(next);
        e.Handled = true;
    }

    private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_initialized || _updating) return;
        if (_dragging)
        {
            TimeText.Text = $"{Fmt.Time(TimeSpan.FromSeconds(e.NewValue))} / {Fmt.Time(_player.Duration ?? _player.Queue.Current?.Duration)}";
            return;
        }
        _player.Seek(TimeSpan.FromSeconds(e.NewValue)); // keyboard arrows
    }

    private void QualityRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _player.Quality = (AudioQuality)Math.Max(0, QualityRadios.SelectedIndex);
        AppSettings.Current.Quality = (int)_player.Quality;
        AppSettings.Save();
    }

    private void AutoplayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        AppServices.SetAutoplay(AutoplayToggle.IsOn);
    }
}
