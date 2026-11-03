using Microsoft.UI.Xaml;

namespace YomuPlayer.Desktop;

public partial class App : Application
{
    private MainWindow? _window;

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
        // A second launch (e.g. a yomu:// link from the browser) hands its link to the running window and exits.
        var link = SingleInstance.FindLinkArg(Environment.GetCommandLineArgs());
        if (SingleInstance.TryForward(link))
        {
            Environment.Exit(0);
            return;
        }

        Log.Write("starting");
        AppSettings.Load();
        AppServices.Init();

        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();

        // Start Menu shortcut / protocol registration is not needed to paint the first frame. Do it after the
        // window is visible so a slow shell COM call cannot delay startup.
        _window.DispatcherQueue.TryEnqueue(() => AppIdentity.Register());

        SingleInstance.StartServer(message => _window.DispatcherQueue.TryEnqueue(() => _window.HandleExternal(message)));
        if (link is not null) _window.HandleExternal(link);
    }
}
