using System.Reflection;

namespace YomuPlayer.Desktop;

// Resolves which build channel is running and where that build keeps its data.
// Packaging drops channel.txt next to the exe (nightly builds) and portable.marker for the portable zip.
public static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string Channel { get; } = ReadChannel();
    public static bool IsNightly => string.Equals(Channel, "nightly", StringComparison.OrdinalIgnoreCase);
    public static bool IsPortable { get; } = File.Exists(Path.Combine(BaseDirectory, "portable.marker"));
    public static bool IsInstalled => !IsPortable && File.Exists(Path.Combine(BaseDirectory, "installed.marker"));

    public static string AppIconPath
    {
        get
        {
            var nightly = Path.Combine(BaseDirectory, "Assets", "app-nightly.ico");
            return IsNightly && File.Exists(nightly)
                ? nightly
                : Path.Combine(BaseDirectory, "Assets", "app.ico");
        }
    }

    public static string ProductName => IsNightly ? "YOMU Player Nightly" : "YOMU Player";
    public static string AppId => IsNightly ? "YOMU.Player.Nightly" : "YOMU.Player";
    public static string Scheme => IsNightly ? "yomu-nightly" : "yomu";
    public static string PipeName => IsNightly
        ? (IsPortable ? "YOMU.Player.Nightly.Portable.Pipe" : "YOMU.Player.Nightly.Pipe")
        : (IsPortable ? "YOMU.Player.Portable.Pipe" : "YOMU.Player.Pipe");

    public static string Version { get; } = ReadVersion();
    public static string DataRoot { get; } = ResolveDataRoot();

    public static string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public static string AuthPath => Path.Combine(DataRoot, "auth.bin");
    public static string LogPath => Path.Combine(DataRoot, "log.txt");
    public static string UpdatesRoot => Path.Combine(DataRoot, "Updates");
    public static string ChangelogPath => Path.Combine(BaseDirectory, "changelog.json");
    public static string UpdateSourcePath => Path.Combine(BaseDirectory, "update-source.json");

    private static string ReadChannel()
    {
        try
        {
            var path = Path.Combine(BaseDirectory, "channel.txt");
            if (File.Exists(path))
            {
                var value = File.ReadAllText(path).Trim();
                if (string.Equals(value, "nightly", StringComparison.OrdinalIgnoreCase)) return "nightly";
            }
        }
        catch
        {
            // a damaged channel marker must not prevent startup
        }
        return "stable";
    }

    private static string ReadVersion()
    {
        try
        {
            var info = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
                return info.Split('+')[0].Trim();
        }
        catch
        {
            // fall through
        }
        return "1.0.0";
    }

    private static string ResolveDataRoot()
    {
        var productFolder = IsNightly ? "YomuPlayer Nightly" : "YomuPlayer";
        if (IsPortable)
        {
            var local = Path.Combine(BaseDirectory, "data");
            if (CanWrite(local)) return local;
        }

        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), productFolder);
        Directory.CreateDirectory(installed);
        return installed;
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
