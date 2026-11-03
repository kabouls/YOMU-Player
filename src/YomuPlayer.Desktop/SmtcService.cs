using Microsoft.UI.Xaml;
using Windows.Media;
using Windows.Storage.Streams;
using WinRT.Interop;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// System Media Transport Controls: hardware media keys, the Windows volume flyout and lock screen.
public sealed class SmtcService
{
    private readonly SystemMediaTransportControls _smtc;
    private readonly PlayerService _player;
    private MediaPlaybackStatus? _lastStatus;

    public SmtcService(Window window, PlayerService player)
    {
        _player = player;
        var hwnd = WindowNative.GetWindowHandle(window);
        _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.ButtonPressed += OnButtonPressed;
        player.TrackChanged += OnTrackChanged;
    }

    // Called from the UI timer: VLC reports "playing" a moment after Play(), so poll instead of trusting events.
    public void Sync()
    {
        var status = _player.Queue.Current is null
            ? MediaPlaybackStatus.Closed
            : _player.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        if (status == _lastStatus) return;
        _lastStatus = status;
        _smtc.PlaybackStatus = status;
    }

    private void OnTrackChanged(Track? t)
    {
        var u = _smtc.DisplayUpdater;
        if (t is null)
        {
            u.ClearAll();
            u.Update();
            return;
        }

        u.Type = MediaPlaybackType.Music;
        u.MusicProperties.Title = t.Title;
        u.MusicProperties.Artist = t.ArtistLine;
        u.MusicProperties.AlbumTitle = t.Album ?? "";
        if (Uri.TryCreate(t.ThumbnailUrl, UriKind.Absolute, out var uri))
            u.Thumbnail = RandomAccessStreamReference.CreateFromUri(uri);
        u.Update();
    }

    private async void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                if (!_player.IsPlaying) _player.TogglePause();
                break;
            case SystemMediaTransportControlsButton.Pause:
            case SystemMediaTransportControlsButton.Stop:
                if (_player.IsPlaying) _player.TogglePause();
                break;
            case SystemMediaTransportControlsButton.Next:
                await _player.NextAsync();
                break;
            case SystemMediaTransportControlsButton.Previous:
                await _player.PreviousAsync();
                break;
        }
    }
}
