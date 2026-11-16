using Microsoft.UI.Xaml;

namespace YomuPlayer.Desktop;

public partial class App : Application
{
    private MainWindow? _window;
    private readonly object _externalGate = new();
    private string? _pendingExternal;

    // Used by the sign-in flow to bring the app back to the foreground after the browser redirect.
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // Keep unexpected UI failures in log.txt instead of losing them with the process.
        UnhandledException += (_, e) => Log.Write("unhandled UI exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("unhandled: " + e.ExceptionObject);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // A second launch (e.g. a yomu:// link from the browser) hands its link to the running window
        // and exits. The mutex is acquired before any window or startup work.
        var link = SingleInstance.FindLinkArg(Environment.GetCommandLineArgs());
        if (!SingleInstance.Acquire())
        {
            SingleInstance.TryForward(link);
            Environment.Exit(0);
            return;
        }

        Log.Write("starting");

        // Start listening immediately; the link can arrive while MainWindow is still being built.
        SingleInstance.StartServer(HandleExternalMessage);

        AppSettings.Load();
        AppServices.Init();

        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();

        string? pending;
        lock (_externalGate)
        {
            pending = _pendingExternal;
            _pendingExternal = null;
        }
        if (pending is not null) _window.HandleExternal(pending);

        // Start Menu shortcut / protocol registration is not needed to paint the first frame. Do it after the
        // window is visible so a slow shell COM call cannot delay startup.
        _window.DispatcherQueue.TryEnqueue(() => AppIdentity.Register());

        if (link is not null) _window.HandleExternal(link);
    }

    private void HandleExternalMessage(string message)
    {
        if (_window is { } window)
        {
            window.DispatcherQueue.TryEnqueue(() => window.HandleExternal(message));
            return;
        }

        lock (_externalGate) _pendingExternal = message;
    }
}
