using System.IO.Pipes;
using System.Text;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// One running window per channel. A second launch (for example from a yomu:// link) hands its link
// to the first and exits. Stable and nightly keep separate mutexes and pipes; portable and installed
// builds of the same channel deliberately share one.
public static class SingleInstance
{
#if DEBUG
    private const string BuildSuffix = ".Debug";
#else
    private const string BuildSuffix = "";
#endif

    private static string PipeName => AppPaths.PipeName + BuildSuffix;
    private static string MutexName => $@"Local\YOMU.Player.{AppPaths.Channel}.SingleInstance{BuildSuffix}";
    private static Mutex? _mutex;

    public static string? FindLinkArg(string[] args) =>
        args.Skip(1).FirstOrDefault(a => LinkParser.Parse(a) is not null);

    // Must be called before any window or long startup work. The mutex, not the pipe, is the
    // authority: two processes can race before the first pipe server exists.
    public static bool Acquire()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
            {
                _mutex = mutex;
                return true;
            }

            try
            {
                if (mutex.WaitOne(TimeSpan.Zero, false))
                {
                    _mutex = mutex;
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous owner crashed; this process now owns the named mutex.
                _mutex = mutex;
                return true;
            }

            mutex.Dispose();
            return false;
        }
        catch
        {
            // Never block startup because a mutex name is unavailable.
            return true;
        }
    }

    // True when another instance is running and accepted the request.
    public static bool TryForward(string? message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(250);
                var bytes = Encoding.UTF8.GetBytes((message ?? "focus") + "\n");
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
                return true;
            }
            catch
            {
                // The first instance may still be creating its window/pipe server.
                Thread.Sleep(120);
            }
        }
        return false;
    }

    public static void StartServer(Action<string> onMessage)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line)) onMessage(line.Trim());
                }
                catch
                {
                    await Task.Delay(500);
                }
            }
        });
    }
}
