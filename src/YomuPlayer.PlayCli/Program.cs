using YomuPlayer.Audio.Vlc;
using YomuPlayer.Core;

if (args.Length == 0)
{
    Console.WriteLine("usage: <search query>");
    return 1;
}

using var yt = new InnerTubeClient();
var tracks = await yt.SearchSongsAsync(string.Join(' ', args));
if (tracks.Count == 0)
{
    Console.WriteLine("no results");
    return 2;
}

using var player = new VlcAudioPlayer();
using var svc = new PlayerService(new StreamResolver(), player) { Volume = 70 };
svc.TrackChanged += t => { if (t is not null) Console.WriteLine($"> {t.Title} - {t.ArtistLine} [{t.Duration}]"); };
svc.ErrorOccurred += e => Console.WriteLine($"error: {e}");

Console.WriteLine("space=pause  n=next  p=prev  +/-=volume  s=shuffle  r=repeat  q=quit");
await svc.PlayQueueAsync(tracks, 0);

while (true)
{
    var key = Console.ReadKey(true).KeyChar;
    switch (key)
    {
        case ' ': svc.TogglePause(); break;
        case 'n': await svc.NextAsync(); break;
        case 'p': await svc.PreviousAsync(); break;
        case '+': svc.Volume += 5; Console.WriteLine($"volume {svc.Volume}"); break;
        case '-': svc.Volume -= 5; Console.WriteLine($"volume {svc.Volume}"); break;
        case 's': svc.Queue.Shuffle = !svc.Queue.Shuffle; Console.WriteLine($"shuffle {svc.Queue.Shuffle}"); break;
        case 'r':
            svc.Queue.Repeat = svc.Queue.Repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };
            Console.WriteLine($"repeat {svc.Queue.Repeat}");
            break;
        case 'q': return 0;
    }
}
