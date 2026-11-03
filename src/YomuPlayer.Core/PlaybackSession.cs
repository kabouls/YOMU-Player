using System.Text.Json.Serialization;

namespace YomuPlayer.Core;

// ---- Device control seam ------------------------------------------------------------------------------------
// Everything needed to drive playback over a wire lives here and knows nothing about WinUI.
// The Windows app uses LocalPlaybackEndpoint today. A phone / TV companion can speak the same snapshot+command
// shapes over a transport (WebSocket, named pipe, relay) without the player code changing. See
// docs/remote-playback.md for the discovery and transfer design.

public enum DeviceKind { Windows, Android, AndroidTv, Web, Unknown }

public sealed record RemoteTrack(
    string VideoId,
    string Title,
    string Artists,
    string? Album,
    string? ThumbnailUrl,
    int DurationMs)
{
    public static RemoteTrack From(Track track) => new(
        track.VideoId,
        track.Title,
        track.ArtistLine,
        track.Album,
        track.ThumbnailUrl,
        (int)(track.Duration?.TotalMilliseconds ?? 0));
}

// The complete, serializable playback state. Sent when a device connects and after every command.
public sealed record PlaybackSnapshot(
    string DeviceId,
    string DeviceName,
    DeviceKind Kind,
    RemoteTrack? Track,
    bool IsPlaying,
    bool IsLoading,
    int PositionMs,
    int DurationMs,
    int Volume,
    bool Muted,
    bool Shuffle,
    RepeatMode Repeat,
    int QueueLength,
    DateTimeOffset UpdatedAt);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "command")]
[JsonDerivedType(typeof(PlaybackCommand.Play), "play")]
[JsonDerivedType(typeof(PlaybackCommand.Pause), "pause")]
[JsonDerivedType(typeof(PlaybackCommand.Toggle), "toggle")]
[JsonDerivedType(typeof(PlaybackCommand.Next), "next")]
[JsonDerivedType(typeof(PlaybackCommand.Previous), "previous")]
[JsonDerivedType(typeof(PlaybackCommand.SeekTo), "seekTo")]
[JsonDerivedType(typeof(PlaybackCommand.SetVolume), "setVolume")]
[JsonDerivedType(typeof(PlaybackCommand.SetShuffle), "setShuffle")]
[JsonDerivedType(typeof(PlaybackCommand.SetRepeat), "setRepeat")]
[JsonDerivedType(typeof(PlaybackCommand.PlayQueueIndex), "playQueueIndex")]
public abstract record PlaybackCommand
{
    public sealed record Play : PlaybackCommand;
    public sealed record Pause : PlaybackCommand;
    public sealed record Toggle : PlaybackCommand;
    public sealed record Next : PlaybackCommand;
    public sealed record Previous : PlaybackCommand;
    public sealed record SeekTo(int PositionMs) : PlaybackCommand;
    public sealed record SetVolume(int Volume) : PlaybackCommand;
    public sealed record SetShuffle(bool On) : PlaybackCommand;
    public sealed record SetRepeat(RepeatMode Mode) : PlaybackCommand;
    public sealed record PlayQueueIndex(int OrderIndex) : PlaybackCommand;
}

public interface IPlaybackEndpoint
{
    string DeviceId { get; }
    string DeviceName { get; }
    DeviceKind Kind { get; }
    bool IsLocal { get; }

    PlaybackSnapshot Snapshot();
    Task ApplyAsync(PlaybackCommand command, CancellationToken ct = default);
}

public sealed class LocalPlaybackEndpoint : IPlaybackEndpoint
{
    private readonly PlayerService _player;

    public LocalPlaybackEndpoint(PlayerService player, string deviceName)
    {
        _player = player;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "This PC" : deviceName;
    }

    public string DeviceId => "local";
    public string DeviceName { get; }
    public DeviceKind Kind => DeviceKind.Windows;
    public bool IsLocal => true;

    public PlaybackSnapshot Snapshot()
    {
        var track = _player.Queue.Current;
        var duration = _player.Duration ?? track?.Duration ?? TimeSpan.Zero;
        return new PlaybackSnapshot(
            DeviceId,
            DeviceName,
            Kind,
            track is null ? null : RemoteTrack.From(track),
            _player.IsPlaying,
            _player.IsLoading,
            (int)Math.Max(0, _player.Position.TotalMilliseconds),
            (int)duration.TotalMilliseconds,
            _player.Volume,
            _player.Volume == 0,
            _player.Queue.Shuffle,
            _player.Queue.Repeat,
            _player.Queue.UpcomingCount,
            DateTimeOffset.UtcNow);
    }

    public async Task ApplyAsync(PlaybackCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        switch (command)
        {
            case PlaybackCommand.Play when !_player.IsPlaying:
            case PlaybackCommand.Pause when _player.IsPlaying:
            case PlaybackCommand.Toggle:
                _player.TogglePause();
                break;
            case PlaybackCommand.Next:
                await _player.NextAsync();
                break;
            case PlaybackCommand.Previous:
                await _player.PreviousAsync();
                break;
            case PlaybackCommand.SeekTo seek:
                _player.Seek(TimeSpan.FromMilliseconds(seek.PositionMs));
                break;
            case PlaybackCommand.SetVolume volume:
                _player.Volume = volume.Volume;
                break;
            case PlaybackCommand.SetShuffle shuffle:
                _player.Queue.Shuffle = shuffle.On;
                break;
            case PlaybackCommand.SetRepeat repeat:
                _player.Queue.Repeat = repeat.Mode;
                break;
            case PlaybackCommand.PlayQueueIndex play:
                await _player.PlayOrderIndexAsync(play.OrderIndex);
                break;
        }
    }
}
