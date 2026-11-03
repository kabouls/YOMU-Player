using LibVLCSharp.Shared;
using YomuPlayer.Core;
using VlcCore = LibVLCSharp.Shared.Core;

namespace YomuPlayer.Audio.Vlc;

public sealed class VlcAudioPlayer : IAudioPlayer
{
    private readonly LibVLC _vlc;
    private readonly MediaPlayer _player;
    private Media? _media;

    public VlcAudioPlayer()
    {
        VlcCore.Initialize();
        _vlc = new LibVLC("--no-video", "--no-video-title-show", "--no-snapshot-preview", "--no-osd", "--no-stats", "--no-spu", "--network-caching=600");
        _player = new MediaPlayer(_vlc);
        _player.EndReached += (_, _) => Ended?.Invoke();
        _player.EncounteredError += (_, _) => Error?.Invoke("LibVLC could not play the stream");
        _player.TimeChanged += (_, e) => PositionChanged?.Invoke(TimeSpan.FromMilliseconds(e.Time));
    }

    public event Action? Ended;
    public event Action<string>? Error;
    public event Action<TimeSpan>? PositionChanged;

    public bool IsPlaying => _player.IsPlaying;
    public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, _player.Time));

    public TimeSpan? Duration => _player.Length > 0 ? TimeSpan.FromMilliseconds(_player.Length) : null;

    public int Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 100);
    }

    public void Load(ResolvedStream stream)
    {
        _player.Stop();
        var old = _media;
        var media = new Media(_vlc, new Uri(stream.Url));
        if (stream.Headers.TryGetValue("User-Agent", out var ua))
            media.AddOption($":http-user-agent={ua}");
        _player.Media = media;
        _media = media;
        old?.Dispose();
    }

    public void Play() => _player.Play();
    public void Pause() => _player.SetPause(true);
    public void Stop() => _player.Stop();
    public void Seek(TimeSpan position) => _player.Time = (long)position.TotalMilliseconds;

    public void Dispose()
    {
        _player.Stop();
        _player.Dispose();
        _media?.Dispose();
        _vlc.Dispose();
    }
}
