namespace YomuPlayer.Core;

public interface IStreamResolver
{
    Task<ResolvedStream> ResolveAsync(string videoId, AudioQuality quality = AudioQuality.High, CancellationToken ct = default);
    Task PrefetchAsync(string videoId, AudioQuality quality = AudioQuality.High);
    void Invalidate(string videoId);
}
