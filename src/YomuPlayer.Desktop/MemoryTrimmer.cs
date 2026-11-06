using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YomuPlayer.Desktop;

// While the window is minimized or hidden: collect garbage and trim the working set now and then,
// so the process sits near its floor (playback, queue, autoplay) until the window comes back.
public static class MemoryTrimmer
{
    private static Timer? _timer;

    public static void Start()
    {
        Stop();
        // Frequent working-set release is cheap; a blocking GC every few seconds would hurt audio.
        _timer = new Timer(_ => ReleaseWorkingSet(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    }

    public static void TrimNow() => Trim();

    private static void ReleaseWorkingSet()
    {
        try { K32EmptyWorkingSet(Process.GetCurrentProcess().Handle); }
        catch { }
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private static void Trim()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            K32EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch
        {
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool K32EmptyWorkingSet(IntPtr process);
}
