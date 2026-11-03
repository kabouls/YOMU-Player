namespace YomuPlayer.Core;

public interface IAudioPlayer : IDisposable
{
    // Events may fire on a native audio thread. Never call back into the player synchronously from a handler.
    event Action? Ended;
    event Action<string>? Error;
    event Action<TimeSpan>? PositionChanged;

    bool IsPlaying { get; }
    TimeSpan Position { get; }
    TimeSpan? Duration { get; }
    int Volume { get; set; }

    void Load(ResolvedStream stream);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
}
