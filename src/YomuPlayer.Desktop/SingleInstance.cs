using System.IO.Pipes;
using System.Text;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// One running window: a second launch (for example from a yomu:// link) hands its link to the first and exits.
public static class SingleInstance
{
    private static string PipeName => AppPaths.PipeName;

    public static string? FindLinkArg(string[] args) =>
        args.Skip(1).FirstOrDefault(a => LinkParser.Parse(a) is not null);

    // True when another instance is running and accepted the request.
    public static bool TryForward(string? message)
    {
        if (!File.Exists(@"\\.\pipe\" + PipeName)) return false;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(500);
            var bytes = Encoding.UTF8.GetBytes((message ?? "focus") + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
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
