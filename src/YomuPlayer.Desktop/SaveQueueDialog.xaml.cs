using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// Saves the current queue to an existing playlist, or creates a new private playlist containing the queue.
public sealed partial class SaveQueueDialog : ContentDialog
{
    private readonly IReadOnlyList<Track> _tracks;

    public SaveQueueDialog(IReadOnlyList<Track> tracks, string? title = null)
    {
        _tracks = tracks;
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title)) Title = title;
        Loaded += OnLoaded;
    }

    private string CountText => _tracks.Count == 1 ? "1 song" : $"{_tracks.Count} songs";

    // Set after a successful save; the caller shows it in the info bar because the dialog hides itself.
    public string? SavedMessage { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var playlists = await AppServices.GetLibraryPlaylistsAsync();
            PlaylistList.ItemsSource = playlists;
            EmptyText.Visibility = playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = NewName.Text.Trim();
        var selected = PlaylistList.SelectedItem as MediaItem;
        if (name.Length == 0 && selected is null)
        {
            ShowError("Choose a playlist or enter a name for a new one.");
            args.Cancel = true;
            return;
        }

        args.Cancel = true; // keep the dialog open while the write runs
        ErrorText.Visibility = Visibility.Collapsed;
        SetBusy(true);

        try
        {
            if (name.Length > 0)
            {
                var created = await AppServices.CreatePlaylistAsync(name, _tracks);
                SavedMessage = $"Saved {CountText} to \u201C{created.Title}\u201D.";
            }
            else
            {
                await AppServices.AddToPlaylistAsync(selected!, _tracks);
                SavedMessage = $"Saved {CountText} to \u201C{selected!.Title}\u201D.";
            }
            Hide();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        PlaylistList.IsEnabled = !busy;
        NewName.IsEnabled = !busy;
        IsPrimaryButtonEnabled = !busy;
        Busy.IsActive = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
