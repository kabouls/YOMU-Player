using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly SmtcService _smtc;
    private readonly NativeWindowHooks? _hooks;
    private readonly GlobalShortcuts? _shortcuts;
    private CancellationTokenSource? _suggestCts;
    private string _lastSearchText = "";
    private string? _liteTrackId;
    private string? _liteRatingTrackId;
    private TrackRating _liteRating;
    private CancellationTokenSource? _liteRatingCts;

    private static readonly SolidColorBrush LiteIdleRatingBrush = new(Microsoft.UI.Colors.Transparent);
    private static readonly SolidColorBrush LiteIdleIconBrush = new(Microsoft.UI.Colors.White);
    private SizeInt32 _savedSize;
    private SizeInt32 _lastNormalSize;
    private PointInt32 _lastNormalPosition;
    private bool _maximizedBeforeLite;
    private bool _windowStateReady;
    private bool _navSync;
    private bool _lite;
    private string? _liteQueueKey;
    public ObservableCollection<QueueRowVM> LiteUpNextRows { get; } = new();
    private bool _allowClose;
    private bool _initialized;
    private bool _liteVolumeSync;
    private readonly List<NavigationViewItem> _sidebarPlaylists = new();
    private int _sidebarVersion;
    private PageRequest? _currentRequest;
    private UpdateInfo? _availableUpdate;

    public MainWindow()
    {
        InitializeComponent();
        Title = AppPaths.ProductName;
        WordmarkText.Text = AppPaths.ProductName;
        ApplyWindowState();
        AppWindow.Changed += OnAppWindowChanged;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        try { AppWindow.SetIcon(AppPaths.AppIconPath); }
        catch { /* the exe icon is used instead */ }

        _smtc = new SmtcService(this, AppServices.Player);
        Bar.Ticked += _smtc.Sync;
        Bar.Ticked += () => { if (_lite) UpdateLite(); };
        Bar.OpenPlayerRequested += OpenPlayer;
        Bar.TogglePlayerRequested += TogglePlayer;

        // Taskbar thumbnail media buttons. YTM_NO_HOOKS=1 skips them (diagnostics).
        if (Environment.GetEnvironmentVariable("YTM_NO_HOOKS") != "1")
        {
            var hooks = _hooks = new NativeWindowHooks(WinRT.Interop.WindowNative.GetWindowHandle(this));
            hooks.ThumbButtonClicked += OnThumbButton;
            hooks.MediaCommand += OnPlaybackShortcut;
            Bar.Ticked += () => hooks.SetPlaying(AppServices.Player.IsPlaying);
            _shortcuts = new GlobalShortcuts(
                () => AppServices.Player.Queue.Current is not null,
                OnPlaybackShortcut);
        }

        AppServices.NavigateRequested += r => DispatcherQueue.TryEnqueue(() => Navigate(r, top: false));
        AppServices.PlayerRequested += tab => DispatcherQueue.TryEnqueue(() => OpenPlayer(tab));
        AppServices.ErrorRaised += m => DispatcherQueue.TryEnqueue(() => ShowError(m));
        AppServices.InfoRaised += m => DispatcherQueue.TryEnqueue(() => ShowInfo(m));
        AppServices.Auth.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateProfileButton();
            if (_lite)
            {
                _liteRatingTrackId = null;
                UpdateLite();
            }
            _ = RefreshSidebarLibrary();
        });
        AppServices.RatingChanged += (videoId, rating) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_lite && _liteRatingTrackId == videoId)
            {
                _liteRating = rating;
                UpdateLiteRating();
            }
        });
        AppServices.LibraryChanged += () => DispatcherQueue.TryEnqueue(() => _ = RefreshSidebarLibrary());
        AppServices.Player.TrackChanged += t => DispatcherQueue.TryEnqueue(() =>
        {
            Title = t is null ? AppPaths.ProductName : $"{t.Title} - {t.ArtistLine}";
            if (_lite) UpdateLite();
        });
        AppServices.Player.Queue.Changed += () => DispatcherQueue.TryEnqueue(UpdateLiteQueue);
        UpdateService.UpdateAvailable += info =>
            DispatcherQueue.TryEnqueue(() => ShowUpdateChip(info));

        RootGrid.Loaded += (_, _) => Dialogs.Root = RootGrid.XamlRoot;
        RootGrid.Loaded += (_, _) =>
        {
            _ = CheckForUpdatesAsync(manual: false);
            _ = ShowPostUpdateChangelogAsync();
        };
        AppTitleBar.Loaded += (_, _) => UpdateTitleBarRegions();
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        SearchBox.SizeChanged += (_, _) => UpdateTitleBarRegions();
        ContentFrame.Navigated += ContentFrame_Navigated;

        // Keyboard shortcuts and mouse side buttons work anywhere in the window.
        RootGrid.PreviewKeyDown += Root_PreviewKeyDown;
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), true);

        // Hidden or minimized: let the UI clock tick slowly (SMTC and the taskbar buttons stay in sync).
        VisibilityChanged += (_, e) => Bar.SetActive(e.Visible && !_lite);

        // Closing always asks first.
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) =>
        {
            SaveWindowState();
            _shortcuts?.Dispose();
            _hooks?.Dispose();
            MemoryTrimmer.Stop();
            AppServices.Shutdown();
        };

        // The taskbar sometimes isn't ready for thumbnail buttons right away: try again a few times.
        Activated += OnFirstActivated;

        NavView.SelectedItem = NavView.MenuItems[0];

        AppServices.Player.VolumeChanged += _ => DispatcherQueue.TryEnqueue(SyncLiteVolume);
        _initialized = true;
        SyncLiteVolume();
        UpdateProfileButton();

        // Load the signed-in sidebar only after the window is visible; the music-playlist filter makes several
        // Data API requests and must not delay the first frame.
        RootGrid.Loaded += (_, _) => _ = RefreshSidebarLibrary();
    }

    private async void OnFirstActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
    {
        Activated -= OnFirstActivated;

        // Plain delays instead of DispatcherQueueTimer: a timer whose only reference is its own Tick handler can be
        // finalized while WinRT is invoking it, which surfaced as an AccessViolationException in Stop().
        foreach (var delay in new[] { 800, 2500, 6000 })
        {
            await Task.Delay(delay);
            if (_allowClose) return;
            _hooks?.Retry();
        }
    }

    // ---- taskbar buttons and links from outside ----

    private void OnThumbButton(uint id)
    {
        var p = AppServices.Player;
        switch (id)
        {
            case NativeWindowHooks.ButtonPrev: _ = p.PreviousAsync(); break;
            case NativeWindowHooks.ButtonPlayPause: p.TogglePause(); break;
            case NativeWindowHooks.ButtonNext: _ = p.NextAsync(); break;
        }
    }

    private void OnPlaybackShortcut(PlaybackShortcut shortcut)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnPlaybackShortcut(shortcut));
            return;
        }

        var p = AppServices.Player;
        switch (shortcut)
        {
            case PlaybackShortcut.PlayPause: p.TogglePause(); break;
            case PlaybackShortcut.Next: _ = p.NextAsync(); break;
            case PlaybackShortcut.Previous: _ = p.PreviousAsync(); break;
            case PlaybackShortcut.Stop: if (p.IsPlaying) p.TogglePause(); break;
            case PlaybackShortcut.Mute: p.ToggleMute(); break;
            case PlaybackShortcut.VolumeUp: p.Volume += 5; break;
            case PlaybackShortcut.VolumeDown: p.Volume -= 5; break;
        }
    }

    // A link arrived from another launch (yomu://, the bookmarklet, a second start of the exe). It was a deliberate
    // action in the browser, so it opens without the paste confirmation.
    public void HandleExternal(string message)
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        try { SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)); }
        catch { /* best effort */ }
        if (message != "focus") _ = AppServices.OpenLinkAsync(message, confirm: false);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private async Task OpenClipboardLinkAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = (await content.GetTextAsync()).Trim();
            if (LinkParser.LooksLikeLink(text)) await AppServices.OpenLinkAsync(text, confirm: true);
        }
        catch
        {
            // clipboard can be busy; ignore
        }
    }

    // ---- close confirmation ----

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;

        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();

        var dialog = new ContentDialog
        {
            Title = "Exit YOMU Player?",
            Content = "Playback will stop and the queue will be cleared.",
            PrimaryButtonText = "Exit",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (!_lite) dialog.SecondaryButtonText = "Lite mode instead";

        var result = await Dialogs.ShowAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            _allowClose = true;
            Close();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            EnterLite();
        }
    }

    // ---- Lite mode ----
    // A tiny window with only the playback essentials. The page, thumbnails and caches are released and the
    // process is trimmed, while playback, queue, autoplay, shuffle and repeat carry on exactly as before.

    private async void LiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AppSettings.Current.SkipLiteConfirm)
        {
            var dontAsk = new CheckBox { Content = "Don't show this again" };
            var body = new StackPanel { Spacing = 14 };
            body.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Lite mode shrinks the window to basic playback controls and releases the page, pictures and " +
                       "caches, so it uses as little memory and CPU as possible.\n\n" +
                       "Music keeps playing exactly as before: the queue, related songs, shuffle and repeat all keep " +
                       "working. Use \u201CBack to normal mode\u201D to return."
            });
            body.Children.Add(dontAsk);

            var dialog = new ContentDialog
            {
                Title = "Switch to Lite mode?",
                Content = body,
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

            if (dontAsk.IsChecked == true)
            {
                AppSettings.Current.SkipLiteConfirm = true;
                AppSettings.Save();
            }
        }
        EnterLite();
    }

    private void LiteExitButton_Click(object sender, RoutedEventArgs e) => ExitLite();

    // Restores the last normal size/position, then maximizes if that was the state at close.
    private void ApplyWindowState()
    {
        var s = AppSettings.Current;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Clamp(s.WindowWidth > 0 ? s.WindowWidth : 1280, 640, Math.Max(640, area.Width));
        var height = Math.Clamp(s.WindowHeight > 0 ? s.WindowHeight : 820, 480, Math.Max(480, area.Height));
        var x = s.WindowX == int.MinValue ? area.X + (area.Width - width) / 2 : s.WindowX;
        var y = s.WindowY == int.MinValue ? area.Y + (area.Height - height) / 2 : s.WindowY;
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - height));

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        _lastNormalSize = new SizeInt32(width, height);
        _lastNormalPosition = new PointInt32(x, y);

        if (s.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();

        _windowStateReady = true;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_windowStateReady || _lite) return;
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _lastNormalSize = sender.Size;
            _lastNormalPosition = sender.Position;
        }
    }

    private void SaveWindowState()
    {
        var s = AppSettings.Current;
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        s.WindowMaximized = _lite
            ? _maximizedBeforeLite
            : presenter?.State == OverlappedPresenterState.Maximized;

        if (!_lite && presenter?.State == OverlappedPresenterState.Restored)
        {
            _lastNormalSize = AppWindow.Size;
            _lastNormalPosition = AppWindow.Position;
        }

        s.WindowWidth = _lastNormalSize.Width > 0 ? _lastNormalSize.Width : 1280;
        s.WindowHeight = _lastNormalSize.Height > 0 ? _lastNormalSize.Height : 820;
        s.WindowX = _lastNormalPosition.X;
        s.WindowY = _lastNormalPosition.Y;
        AppSettings.Save();
    }

    private void EnterLite()
    {
        if (_lite) return;

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        _maximizedBeforeLite = presenter?.State == OverlappedPresenterState.Maximized;
        if (presenter?.State == OverlappedPresenterState.Restored)
        {
            _lastNormalSize = AppWindow.Size;
            _lastNormalPosition = AppWindow.Position;
        }

        _lite = true;
        _savedSize = AppWindow.Size;

        NavView.Visibility = Visibility.Collapsed;
        Bar.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Collapsed;
        TitleLeft.Visibility = Visibility.Collapsed;
        LiteButton.Visibility = Visibility.Collapsed;
        LiteExitButton.Visibility = Visibility.Visible;
        LitePanel.Visibility = Visibility.Visible;

        // release the page and every thumbnail on it
        ContentFrame.Navigate(typeof(BlankPage), null, new SuppressNavigationTransitionInfo());
        Bar.SetActive(false);

        if (presenter is not null)
        {
            if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32((int)(420 * scale), (int)(300 * scale)));

        _liteTrackId = null;
        _liteRatingTrackId = null;
        _liteRatingCts?.Cancel();
        UpdateLite();
        UpdateTitleBarRegions();
        MemoryTrimmer.Start();
    }

    private void ExitLite()
    {
        if (!_lite) return;
        _lite = false;
        MemoryTrimmer.Stop();

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null)
        {
            presenter.IsMaximizable = true;
            presenter.IsResizable = true;
        }
        AppWindow.Resize(_savedSize);
        if (_maximizedBeforeLite && presenter is not null) presenter.Maximize();

        LitePanel.Visibility = Visibility.Collapsed;
        LiteExitButton.Visibility = Visibility.Collapsed;
        LiteButton.Visibility = Visibility.Visible;
        TitleLeft.Visibility = Visibility.Visible;
        SearchBox.Visibility = Visibility.Visible;
        NavView.Visibility = Visibility.Visible;
        Bar.Visibility = Visibility.Visible;
        Bar.SetActive(true);

        PopBlankPage();
        UpdateTitleBarRegions();
    }

    private void PopBlankPage()
    {
        if (ContentFrame.Content is not BlankPage) return;
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
        ContentFrame.ForwardStack.Clear();
    }

    private void UpdateLite()
    {
        var p = AppServices.Player;
        var track = p.Queue.Current;
        if (track?.VideoId != _liteTrackId)
        {
            _liteTrackId = track?.VideoId;
            LiteTitle.Text = track?.Title ?? "Nothing playing";
        }

        if (track?.VideoId != _liteRatingTrackId)
        {
            _liteRatingCts?.Cancel();
            _liteRatingTrackId = track?.VideoId;
            _liteRating = TrackRating.None;
            var visible = track is null ? Visibility.Collapsed : Visibility.Visible;
            LiteLike.Visibility = visible;
            LiteDislike.Visibility = visible;
            if (track is not null && AppServices.Auth.IsSignedIn)
                _ = LoadLiteRatingAsync(track);
        }
        UpdateLiteRating();

        LiteArtist.Text = p.IsLoading ? "Loading\u2026" : track?.ArtistLine ?? "";
        LitePlayIcon.Glyph = p.IsPlaying ? "\uE769" : "\uE768";
        UpdateLiteQueue();

        if ((LiteShuffle.IsChecked == true) != p.Queue.Shuffle) LiteShuffle.IsChecked = p.Queue.Shuffle;
        LiteRepeatIcon.Glyph = p.Queue.Repeat == RepeatMode.One ? "\uE8ED" : "\uE8EE";
        LiteRepeatIcon.Opacity = p.Queue.Repeat == RepeatMode.Off ? 0.5 : 1.0;
        SyncLiteVolume();
    }

    private void UpdateLiteQueue()
    {
        if (!_lite) return;
        var entries = AppServices.Player.Queue.Entries();
        var position = AppServices.Player.Queue.Position;
        var previous = entries.Where(e => e.OrderIndex < position).TakeLast(10);
        var currentAndNext = entries.Where(e => e.OrderIndex >= position).Take(11);
        var visible = previous.Concat(currentAndNext).ToList();
        var key = string.Join("|", visible.Select(e => e.OrderIndex + ":" + e.Track.VideoId));
        if (key == _liteQueueKey) return;
        _liteQueueKey = key;
        LiteUpNextRows.Clear();
        foreach (var entry in visible)
            LiteUpNextRows.Add(new QueueRowVM(entry.Track, entry.OrderIndex, entry.IsCurrent,
                PlayLiteOrderAsync, isPlayed: entry.OrderIndex < position));
        LiteUpNext.Visibility = LiteUpNextRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private Task PlayLiteOrderAsync(int orderIndex) => AppServices.Player.PlayOrderIndexAsync(orderIndex);

    private void LiteVolume_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(LiteVolume).Properties.MouseWheelDelta;
        AppServices.Player.Volume += delta > 0 ? 5 : -5;
        e.Handled = true;
    }

    private async void LiteDevices_Click(object sender, RoutedEventArgs e)
    {
        var names = string.Join("\n", AppServices.Devices.Select(d => d.DeviceName));
        await Dialogs.ShowAsync(new ContentDialog
        {
            Title = "Playback devices",
            Content = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(names)
                    ? "This PC"
                    : "This PC\n" + names,
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = "Close"
        });
    }

    private async void LiteSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;
        var quality = new RadioButtons
        {
            Header = "Audio quality",
            SelectedIndex = Math.Clamp(settings.Quality, 0, 2),
            Items =
            {
                "High (opus, ~130 kbps)",
                "Balanced (opus, ~70 kbps)",
                "Data saver (~50 kbps)"
            }
        };
        var autoplay = new ToggleSwitch
        {
            Header = "Autoplay similar songs",
            IsOn = settings.Autoplay
        };
        var dialog = new ContentDialog
        {
            Title = "Playback settings",
            Content = new StackPanel
            {
                Spacing = 14,
                Children = { quality, autoplay }
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        settings.Quality = Math.Clamp(quality.SelectedIndex, 0, 2);
        settings.Autoplay = autoplay.IsOn;
        AppSettings.Save();
        AppServices.Player.Quality = (AudioQuality)settings.Quality;
        AppServices.Player.Autoplay = settings.Autoplay;
        AppServices.ReportInfo("Playback settings saved.");
    }

    private void SyncLiteVolume()
    {
        if (!_initialized) return;

        var volume = AppServices.Player.Volume;
        if ((int)LiteVolume.Value != volume)
        {
            _liteVolumeSync = true;
            LiteVolume.Value = volume;
            _liteVolumeSync = false;
        }
        LiteVolumeText.Text = volume.ToString();
        LiteVolumeIcon.Glyph = volume == 0 ? "\uE74F" : "\uE767";
        ToolTipService.SetToolTip(LiteMute, volume == 0 ? "Unmute" : "Mute");
    }

    private void UpdateLiteRating()
    {
        var enabled = _liteRatingTrackId is not null && AppServices.Auth.IsSignedIn;
        LiteLike.IsEnabled = enabled;
        LiteDislike.IsEnabled = enabled;
        var liked = _liteRating == TrackRating.Like;
        var disliked = _liteRating == TrackRating.Dislike;
        LiteLikeIcon.Visibility = liked ? Visibility.Collapsed : Visibility.Visible;
        LiteLikeFilledIcon.Visibility = liked ? Visibility.Visible : Visibility.Collapsed;
        LiteDislikeIcon.Visibility = disliked ? Visibility.Collapsed : Visibility.Visible;
        LiteDislikeFilledIcon.Visibility = disliked ? Visibility.Visible : Visibility.Collapsed;
        LiteLikeIcon.Opacity = 0.55;
        LiteDislikeIcon.Opacity = 0.55;
        LiteLikeIcon.Foreground = LiteIdleIconBrush;
        LiteDislikeIcon.Foreground = LiteIdleIconBrush;
        LiteLike.Background = LiteIdleRatingBrush;
        LiteDislike.Background = LiteIdleRatingBrush;
        ToolTipService.SetToolTip(LiteLike,
            !AppServices.Auth.IsSignedIn ? "Sign in to like songs"
            : liked ? "Remove like" : "Like this song");
        ToolTipService.SetToolTip(LiteDislike,
            !AppServices.Auth.IsSignedIn ? "Sign in to dislike songs"
            : disliked ? "Remove dislike" : "Dislike this song");
    }

    private async Task LoadLiteRatingAsync(Track track)
    {
        var cts = new CancellationTokenSource();
        _liteRatingCts = cts;
        try
        {
            var rating = await AppServices.GetTrackRatingAsync(track, cts.Token);
            if (cts.IsCancellationRequested || AppServices.Player.Queue.Current?.VideoId != track.VideoId)
                return;
            _liteRating = rating;
            UpdateLiteRating();
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

    private async void LiteLike_Click(object sender, RoutedEventArgs e) =>
        await ToggleLiteRatingAsync(TrackRating.Like);

    private async void LiteDislike_Click(object sender, RoutedEventArgs e) =>
        await ToggleLiteRatingAsync(TrackRating.Dislike);

    private async Task ToggleLiteRatingAsync(TrackRating target)
    {
        var track = AppServices.Player.Queue.Current;
        if (track is null) return;
        if (!AppServices.Auth.IsSignedIn)
        {
            AppServices.ReportError("Sign in with Google to like or dislike songs.");
            return;
        }

        var next = _liteRating == target ? TrackRating.None : target;
        LiteLike.IsEnabled = false;
        LiteDislike.IsEnabled = false;
        try
        {
            await AppServices.SetTrackRatingAsync(track, next);
            if (AppServices.Player.Queue.Current?.VideoId == track.VideoId)
            {
                _liteRating = next;
                UpdateLiteRating();
            }
        }
        catch (Exception ex)
        {
            AppServices.ReportError(ex.Message);
        }
        finally
        {
            UpdateLiteRating();
        }
    }

    private void LitePlayPause_Click(object sender, RoutedEventArgs e) => AppServices.Player.TogglePause();

    private async void LitePrev_Click(object sender, RoutedEventArgs e) => await AppServices.Player.PreviousAsync();

    private async void LiteNext_Click(object sender, RoutedEventArgs e) => await AppServices.Player.NextAsync();

    private void LiteShuffle_Click(object sender, RoutedEventArgs e) =>
        AppServices.Player.Queue.Shuffle = LiteShuffle.IsChecked == true;

    private void LiteRepeat_Click(object sender, RoutedEventArgs e)
    {
        var queue = AppServices.Player.Queue;
        queue.Repeat = queue.Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off
        };
        UpdateLite();
    }

    private void LiteMute_Click(object sender, RoutedEventArgs e) => AppServices.Player.ToggleMute();

    private void LiteVolume_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_initialized || _liteVolumeSync) return;
        AppServices.Player.Volume = (int)e.NewValue;
        LiteVolumeText.Text = ((int)e.NewValue).ToString();
    }

    // ---- keyboard / mouse ----
    // Follows YouTube's published shortcuts (YouTube Music uses the same set):
    //   Space / K play-pause   N/P next/previous   J / L -10 s / +10 s   M mute   0-9 jump to 0-90%   / search
    //   Shift+N/P next/previous   Ctrl+Left/Right seek 5 s   Ctrl+Up/Down volume 5%   Ctrl+V open a copied link
    // Plain arrows seek/adjust volume only when focus isn't inside the page or sidebar, so list navigation keeps working.
    // Alt+Left / Alt+Right: history back / forward.
    // Hardware media keys and Ctrl+Alt combos are handled by GlobalShortcuts, with fallbacks here for focus edge cases.

    private static bool Down(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focus = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        var typing = focus is TextBox or PasswordBox or RichEditBox;
        var ctrl = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        var alt = Down(VirtualKey.Menu);
        var p = AppServices.Player;

        // Native media keys and the fallback global combos work even when a search box or a page control has focus.
        var media = e.Key switch
        {
            (VirtualKey)0xB3 => PlaybackShortcut.PlayPause,
            (VirtualKey)0xB0 => PlaybackShortcut.Next,
            (VirtualKey)0xB1 => PlaybackShortcut.Previous,
            (VirtualKey)0xB2 => PlaybackShortcut.Stop,
            _ => (PlaybackShortcut?)null
        };
        if (media is not null)
        {
            OnPlaybackShortcut(media.Value);
            e.Handled = true;
            return;
        }

        if (ctrl && alt)
        {
            var combo = e.Key switch
            {
                VirtualKey.Space => PlaybackShortcut.PlayPause,
                VirtualKey.Right => PlaybackShortcut.Next,
                VirtualKey.Left => PlaybackShortcut.Previous,
                VirtualKey.Up => PlaybackShortcut.VolumeUp,
                VirtualKey.Down => PlaybackShortcut.VolumeDown,
                VirtualKey.M => PlaybackShortcut.Mute,
                _ => (PlaybackShortcut?)null
            };
            if (combo is not null)
            {
                OnPlaybackShortcut(combo.Value);
                e.Handled = true;
                return;
            }
        }

        if (alt && !ctrl && e.Key == VirtualKey.Left) { GoBack(); e.Handled = true; return; }
        if (alt && !ctrl && e.Key == VirtualKey.Right) { GoForward(); e.Handled = true; return; }
        if (typing || alt) return;

        var handled = true;
        if (ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.V: _ = OpenClipboardLinkAsync(); break;
                case VirtualKey.Left: p.SeekRelative(TimeSpan.FromSeconds(-5)); break;
                case VirtualKey.Right: p.SeekRelative(TimeSpan.FromSeconds(5)); break;
                case VirtualKey.Up: p.Volume += 5; break;
                case VirtualKey.Down: p.Volume -= 5; break;
                default: handled = false; break;
            }
        }
        else if (shift)
        {
            switch (e.Key)
            {
                case VirtualKey.N: _ = p.NextAsync(); break;
                case VirtualKey.P: _ = p.PreviousAsync(); break;
                default: handled = false; break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case VirtualKey.Space:
                case VirtualKey.K: p.TogglePause(); break;
                case VirtualKey.N: _ = p.NextAsync(); break;
                case VirtualKey.P: _ = p.PreviousAsync(); break;
                case VirtualKey.J: p.SeekRelative(TimeSpan.FromSeconds(-10)); break;
                case VirtualKey.L: p.SeekRelative(TimeSpan.FromSeconds(10)); break;
                case VirtualKey.M: p.ToggleMute(); break;
                case (VirtualKey)191:
                    if (!_lite) SearchBox.Focus(FocusState.Keyboard);
                    break; // "/"
                case VirtualKey.Left when !FocusInsideContent(focus): p.SeekRelative(TimeSpan.FromSeconds(-5)); break;
                case VirtualKey.Right when !FocusInsideContent(focus): p.SeekRelative(TimeSpan.FromSeconds(5)); break;
                case VirtualKey.Up when !FocusInsideContent(focus): p.Volume += 5; break;
                case VirtualKey.Down when !FocusInsideContent(focus): p.Volume -= 5; break;
                default:
                    if (e.Key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
                        p.SeekFraction((e.Key - VirtualKey.Number0) / 10.0);
                    else if (e.Key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9)
                        p.SeekFraction((e.Key - VirtualKey.NumberPad0) / 10.0);
                    else
                        handled = false;
                    break;
            }
        }
        if (handled) e.Handled = true;
    }

    private bool FocusInsideContent(object? focus) =>
        focus is DependencyObject d && (IsDescendantOf(d, ContentFrame) || IsDescendantOf(d, NavView));

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(RootGrid).Properties;
        if (Down(VirtualKey.Control))
        {
            if (props.IsXButton1Pressed) { OnPlaybackShortcut(PlaybackShortcut.Previous); e.Handled = true; return; }
            if (props.IsXButton2Pressed) { OnPlaybackShortcut(PlaybackShortcut.Next); e.Handled = true; return; }
            if (props.IsMiddleButtonPressed) { OnPlaybackShortcut(PlaybackShortcut.PlayPause); e.Handled = true; return; }
        }
        if (props.IsXButton1Pressed) { GoBack(); e.Handled = true; }
        else if (props.IsXButton2Pressed) { GoForward(); e.Handled = true; }
    }

    private void GoBack()
    {
        if (!_lite && ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void GoForward()
    {
        if (!_lite && ContentFrame.CanGoForward) ContentFrame.GoForward();
    }

    // ---- navigation ----

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_navSync || args.SelectedItem is not NavigationViewItem item) return;

        // Dynamic sidebar entries carry the playlist itself.
        if (item.Tag is MediaItem playlist)
        {
            Navigate(new PageRequest(playlist.Title, PageKind.Collection, Item: playlist), top: true);
            return;
        }

        switch (item.Tag as string)
        {
            case "liked":
                Navigate(new PageRequest("Liked Music", PageKind.Collection, Item: AppServices.LikedMusic), top: true);
                return;
            case "newplaylist":
                _ = CreatePlaylistAsync();
                return;
        }

        var request = (item.Tag as string) switch
        {
            "home" => new PageRequest("Home", PageKind.Home, "FEmusic_home"),
            "explore" => new PageRequest("Explore", PageKind.Browse, "FEmusic_explore"),
            _ => new PageRequest("Library", PageKind.Library)
        };
        Navigate(request, top: true);
    }

    // ---- signed-in sidebar library ----

    private async Task RefreshSidebarLibrary()
    {
        var version = ++_sidebarVersion;
        var signedIn = AppServices.Auth.IsSignedIn;

        LibraryHeader.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        NewPlaylistItem.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        LikedMusicItem.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in _sidebarPlaylists) NavView.MenuItems.Remove(item);
        _sidebarPlaylists.Clear();
        if (_currentRequest is not null) SyncSidebarSelection(_currentRequest);
        if (!signedIn) return;

        try
        {
            var playlists = await AppServices.GetLibraryPlaylistsAsync();
            if (version != _sidebarVersion || !AppServices.Auth.IsSignedIn) return;

            var insertAt = NavView.MenuItems.IndexOf(LikedMusicItem) + 1;
            foreach (var playlist in playlists.Take(30))
            {
                var content = new TextBlock
                {
                    Text = playlist.Title,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                var item = new NavigationViewItem
                {
                    Content = content,
                    Tag = playlist,
                    Icon = new FontIcon { Glyph = "\uE90B" }
                };
                ToolTipService.SetToolTip(item, playlist.Title);
                NavView.MenuItems.Insert(insertAt++, item);
                _sidebarPlaylists.Add(item);
            }
            if (_currentRequest is not null) SyncSidebarSelection(_currentRequest);
        }
        catch (Exception e)
        {
            Log.Write("sidebar playlists: " + e.Message);
        }
    }

    private void SyncSidebarSelection(PageRequest? request)
    {
        object? selected = null;
        if (request is { } r)
        {
            if (r.Kind == PageKind.Home) selected = NavView.MenuItems[0];
            else if (r.Kind == PageKind.Browse && r.BrowseId == "FEmusic_explore") selected = NavView.MenuItems[1];
            else if (r.Kind == PageKind.Browse && r.BrowseId == "yomu_library_playlists") selected = NavView.MenuItems[2];
            else if (r.Kind == PageKind.Library) selected = NavView.MenuItems[2];
            else if (r.Kind == PageKind.Profile) selected = NavView.MenuItems[2];
            else if (r.Kind == PageKind.Collection && r.Item?.PlaylistId == "LM") selected = LikedMusicItem;
            else if (r.Item?.PlaylistId is { } playlistId)
                selected = _sidebarPlaylists.FirstOrDefault(i =>
                    i.Tag is MediaItem m && m.PlaylistId == playlistId);
        }

        _navSync = true;
        NavView.SelectedItem = selected;
        _navSync = false;
    }

    private async Task CreatePlaylistAsync()
    {
        if (!AppServices.Auth.IsSignedIn)
        {
            ShowError("Sign in with Google to create a playlist.");
            SyncSidebarSelection(_currentRequest);
            return;
        }

        var dialog = new CreatePlaylistDialog();
        var result = await Dialogs.ShowAsync(dialog);
        SyncSidebarSelection(_currentRequest);
        if (result != ContentDialogResult.Primary) return;

        try
        {
            var playlist = await AppServices.CreatePlaylistAsync(dialog.PlaylistName);
            AppServices.ReportInfo($"Created playlist \u201C{playlist.Title}\u201D.");
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    // The bar's art/title and the chevron land here. Opening the page keeps the sidebar visible.
    private void OpenPlayer(PlayerTab tab)
    {
        if (_lite) ExitLite();

        if (ContentFrame.Content is PlayerPage page)
        {
            page.SelectTab(tab);
            return;
        }
        Navigate(new PageRequest("Now playing", PageKind.Player, Tab: tab), top: false);
    }

    // One chevron: open the page, or close it back to wherever it was opened from.
    private void TogglePlayer()
    {
        if (_lite) ExitLite();

        if (ContentFrame.Content is PlayerPage)
        {
            if (ContentFrame.CanGoBack) ContentFrame.GoBack();
            else Navigate(new PageRequest("Home", PageKind.Home, "FEmusic_home"), top: true);
            return;
        }
        OpenPlayer(PlayerTab.Queue);
    }

    private void Navigate(PageRequest request, bool top)
    {
        if (_lite) ExitLite(); // anything that needs a page brings the normal window back

        // Search cards carry decoded artwork. Once we return to Home, let that transient page data go
        // instead of keeping several result sets alive for the rest of the session.
        if (request.Kind == PageKind.Home && ContentFrame.Content is not HomePage)
            AppServices.ClearSearchCache();

        var page = request.Kind switch
        {
            PageKind.Home => typeof(HomePage),
            PageKind.Search => typeof(SearchPage),
            PageKind.Library => typeof(LibraryPage),
            PageKind.Collection => typeof(CollectionPage),
            PageKind.Artist => typeof(ArtistPage),
            PageKind.Player => typeof(PlayerPage),
            PageKind.Profile => typeof(ProfilePage),
            _ => typeof(ShelvesPage)
        };

        PopBlankPage();
        // Page construction already has real work to do; the default full-page entrance/drill
        // transitions make sidebar clicks feel sticky. Keep navigation immediate and let content
        // paint as soon as it is ready.
        NavigationTransitionInfo transition = new SuppressNavigationTransitionInfo();

        ContentFrame.Navigate(page, request, transition);
        if (top)
        {
            ContentFrame.BackStack.Clear();
            BackButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        if (e.SourcePageType == typeof(HomePage)) AppServices.ClearSearchCache();
        BackButton.Visibility = ContentFrame.CanGoBack && !_lite ? Visibility.Visible : Visibility.Collapsed;
        Bar.SetPlayerOpen(ContentFrame.Content is PlayerPage);
        ErrorBar.IsOpen = false;

        _currentRequest = e.Parameter as PageRequest;
        SyncSidebarSelection(_currentRequest);
        UpdateTitleBarRegions();
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e) => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void ProfileButton_Click(object sender, RoutedEventArgs e) =>
        AppServices.Navigate(new PageRequest("Profile", PageKind.Profile));

    private void UpdateProfileButton()
    {
        var auth = AppServices.Auth;
        var signedIn = auth.IsSignedIn;
        ProfileButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (!signedIn)
        {
            ProfileAvatar.ProfilePicture = null;
            ProfileAvatar.DisplayName = "";
            ProfileAvatar.Initials = "";
            return;
        }

        var name = auth.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = auth.Email?.Split('@')[0];
        if (string.IsNullOrWhiteSpace(name)) name = "Account";
        ProfileAvatar.DisplayName = name;
        ProfileAvatar.Initials = Initials(name);
        ProfileAvatar.ProfilePicture = Thumbnails.Create(auth.PhotoUrl, 64);
        ToolTipService.SetToolTip(ProfileButton, name + " - account and sign out");
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        return parts.Length == 1
            ? parts[0][..1].ToUpperInvariant()
            : (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => GoBack();

    private void ShowUpdateChip(UpdateInfo info)
    {
        _availableUpdate = info;
        UpdateChip.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(UpdateChip, $"YOMU Player {info.Version} is available");
    }

    private async void UpdateChip_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is { } info) await ShowUpdateDialogAsync(info);
        else await CheckForUpdatesAsync(manual: true);
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        try
        {
            var info = await UpdateService.CheckAsync(manual);
            if (info is not null)
            {
                ShowUpdateChip(info);
                if (manual) await ShowUpdateDialogAsync(info);
            }
            else if (manual)
            {
                ShowInfo($"{AppPaths.ProductName} {AppPaths.Version} is up to date.");
            }
        }
        catch (Exception e)
        {
            if (manual) ShowError(e.Message);
        }
    }

    private async Task ShowUpdateDialogAsync(UpdateInfo info)
    {
        var dialog = new UpdateDialog(info);
        await Dialogs.ShowAsync(dialog);
    }

    public void CloseForUpdate()
    {
        _allowClose = true;
        Close();
    }

    private async Task ShowPostUpdateChangelogAsync()
    {
        await Task.Delay(900);
        var settings = AppSettings.Current;
        var current = AppPaths.Version;

        if (string.IsNullOrWhiteSpace(settings.LastSeenVersion))
        {
            settings.LastSeenVersion = current;
            AppSettings.Save();
            return;
        }
        if (string.Equals(settings.LastSeenVersion, current, StringComparison.OrdinalIgnoreCase)) return;
        if (Dialogs.IsOpen) return;

        var entry = ChangelogService.ForVersion(current);
        var notes = entry?.Notes
                    ?? new[] { $"Updated to {AppPaths.ProductName} {current}." };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = $"You're now on {AppPaths.ProductName} {current}.",
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var note in notes)
            content.Children.Add(new TextBlock { Text = "•  " + note, TextWrapping = TextWrapping.Wrap });

        var dialog = new ContentDialog
        {
            Title = "What's new",
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        await Dialogs.ShowAsync(dialog);
        settings.LastSeenVersion = current;
        AppSettings.Save();
    }

    private void ShowError(string message)
    {
        // InnerTube returns the raw 403 JSON when the OAuth token lacks the YouTube scope. Replace it with the
        // actionable fix instead of showing a wall of JSON.
        if (message.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase)
            || message.Contains("insufficientPermissions", StringComparison.OrdinalIgnoreCase))
        {
            message = "Google did not grant YouTube access. In Google Auth Platform -> Data Access, add " +
                      "https://www.googleapis.com/auth/youtube. Then revoke YOMU Player at " +
                      "myaccount.google.com/permissions, sign out of the app, and sign in again. " +
                      "On the Google consent screen, tick the YouTube permission before continuing.";
        }

        ErrorBar.Severity = InfoBarSeverity.Error;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        if (_lite) ExitLite(); // errors need the normal window to be seen
    }

    private void ShowInfo(string message)
    {
        if (_lite) return;
        ErrorBar.Severity = InfoBarSeverity.Success;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    // ---- search: predictive dropdown and pasted links ----
    // Up to 3 rich results (art, title, artist, type; click to act immediately), then up to 3 plain suggestions
    // (click to open the full results page). Requests are debounced and shared with the results page.

    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        _suggestCts?.Cancel();
        var query = sender.Text.Trim();

        // A big jump in one change means a paste. Links open (after confirmation) instead of being searched for;
        // a half-typed URL is left alone until Enter.
        var pasted = query.Length - _lastSearchText.Length > 8;
        _lastSearchText = query;
        if (pasted && LinkParser.LooksLikeLink(query))
        {
            sender.ItemsSource = null;
            sender.Text = "";
            _lastSearchText = "";
            _ = AppServices.OpenLinkAsync(query, confirm: true);
            return;
        }

        if (query.Length < 2 || LinkParser.LooksLikeLink(query))
        {
            sender.ItemsSource = null;
            return;
        }

        var cts = _suggestCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(220, cts.Token);

            var rich = AppServices.GetSearchAsync(query);
            var text = AppServices.Yt.GetSearchSuggestionsAsync(query, cts.Token);

            IReadOnlyList<string> suggestions;
            try { suggestions = await text; }
            catch (OperationCanceledException) { throw; }
            catch { suggestions = Array.Empty<string>(); }

            cts.Token.ThrowIfCancellationRequested();

            // Show the cheap text suggestions immediately; the richer artwork rows replace/augment them when the
            // full search request lands. This keeps the dropdown responsive while still sharing that request with
            // the results page.
            var quick = suggestions
                .Where(s => !string.Equals(s, query, StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .Select(s => (object)new TextSuggestion(s))
                .ToList();
            if (quick.Count > 0) sender.ItemsSource = quick;

            IReadOnlyList<MediaItem> results;
            try { results = await rich; }
            catch { results = Array.Empty<MediaItem>(); }

            cts.Token.ThrowIfCancellationRequested();

            var list = new List<object>();
            foreach (var r in results.Take(3)) list.Add(new ItemVM(r));
            foreach (var s in suggestions.Where(s => !string.Equals(s, query, StringComparison.OrdinalIgnoreCase)).Take(3))
                list.Add(new TextSuggestion(s));
            sender.ItemsSource = list;

            // the most likely click: have its stream ready
            AppServices.Prefetch(results.FirstOrDefault(r => r.IsTrack)?.Track);
        }
        catch (OperationCanceledException)
        {
            // a newer keystroke superseded this one
        }
        catch
        {
            // predictions are optional; Enter still works
        }
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _suggestCts?.Cancel();

        if (args.ChosenSuggestion is ItemVM vm)
        {
            sender.ItemsSource = null;
            try { await AppServices.ActivateAsync(vm.Item); }
            catch (Exception e) { AppServices.ReportError(e.Message); }
            return;
        }

        var query = args.ChosenSuggestion is TextSuggestion ts ? ts.Text : args.QueryText?.Trim();
        if (string.IsNullOrEmpty(query)) return;

        // anything shaped like a link goes through the link flow: supported ones are confirmed, others get a clear error
        if (LinkParser.LooksLikeLink(query))
        {
            sender.Text = "";
            _lastSearchText = "";
            sender.ItemsSource = null;
            await AppServices.OpenLinkAsync(query, confirm: true);
            return;
        }

        sender.Text = query;
        _lastSearchText = query;
        sender.ItemsSource = null;
        Navigate(new PageRequest($"Results for \u201C{query}\u201D", PageKind.Search, Query: query), top: false);
    }

    // ---- custom title bar ----

    // Keeps the search box and buttons clickable inside the draggable title bar, and leaves room for the caption buttons.
    private void UpdateTitleBarRegions()
    {
        if (AppTitleBar.XamlRoot is null) return;
        var scale = AppTitleBar.XamlRoot.RasterizationScale;
        RightInsetColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);

        try
        {
            var rects = new List<RectInt32>();
            foreach (var el in new FrameworkElement[] { MenuButton, UpdateChip, BackButton, SearchBox, ProfileButton, LiteButton, LiteExitButton })
            {
                if (el.Visibility != Visibility.Visible || el.ActualWidth <= 0) continue;
                var b = el.TransformToVisual(null).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                rects.Add(new RectInt32(
                    (int)Math.Round(b.X * scale), (int)Math.Round(b.Y * scale),
                    (int)Math.Round(b.Width * scale), (int)Math.Round(b.Height * scale)));
            }
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                .SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch
        {
            // purely cosmetic; the title bar still works without passthrough regions
        }
    }
}
