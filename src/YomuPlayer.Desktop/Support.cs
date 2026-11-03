using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace YomuPlayer.Desktop;

// Plain-text diagnostics in the active build's data folder.
public static class Log
{
    private static readonly string Dir = AppPaths.DataRoot;

    public static string FilePath => AppPaths.LogPath;

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

// Small persisted preferences in the active build's data folder.
public sealed class AppSettings
{
    private static readonly string FilePath = AppPaths.SettingsPath;

    public bool SkipLiteConfirm { get; set; }
    public int Volume { get; set; } = 70;
    public int Quality { get; set; } = 1; // 0 High, 1 Balanced, 2 Data saver
    public bool Autoplay { get; set; } = true;
    public string? LastSeenVersion { get; set; }

    // Last non-maximized window geometry, plus whether the window was maximized at close.
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 820;
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public bool WindowMaximized { get; set; }

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}

// One ContentDialog at a time (WinUI throws if a second one opens).
public static class Dialogs
{
    private static bool _open;

    public static XamlRoot? Root { get; set; }
    public static bool IsOpen => _open;

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_open || Root is null) return ContentDialogResult.None;
        _open = true;
        try
        {
            // ContentDialog gives its default button the framework's AccentButtonStyle. That style's foreground
            // can resolve to black on the app's red accent; local resources make every accent button in the
            // dialog white, in all button states.
            dialog.Resources["AccentButtonForeground"] = new SolidColorBrush(Microsoft.UI.Colors.White);
            dialog.Resources["AccentButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.White);
            dialog.Resources["AccentButtonForegroundPressed"] = new SolidColorBrush(Microsoft.UI.Colors.White);
            dialog.Resources["TextOnAccentFillColorPrimaryBrush"] = new SolidColorBrush(Microsoft.UI.Colors.White);
            dialog.Resources["TextOnAccentFillColorSecondaryBrush"] = new SolidColorBrush(
                Windows.UI.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));

            dialog.XamlRoot = Root;
            dialog.RequestedTheme = ElementTheme.Dark;
            return await dialog.ShowAsync();
        }
        finally
        {
            _open = false;
        }
    }

    public static async Task<bool> ConfirmDeletePlaylistAsync(string title)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete playlist?",
            Content = new TextBlock
            {
                Text = $"Delete \u201C{title}\u201D?\n\n" +
                       "This removes the playlist from your YouTube Music library. The songs themselves are not deleted.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
