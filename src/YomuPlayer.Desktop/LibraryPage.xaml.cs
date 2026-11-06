using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.ObjectModel;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// Account entry point: profile, playlists, and local listening history.
public sealed partial class LibraryPage : Page
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _signInCts;
    private bool _loading;

    public ObservableCollection<ItemVM> Playlists { get; } = new();
    public ObservableCollection<ItemVM> HistoryRows { get; } = new();

    public LibraryPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.Auth.Changed += OnAuthChanged;
        AppServices.LibraryChanged += OnLibraryChanged;
        PlaybackHistory.Changed += OnHistoryChanged;
        RefreshUi();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.Auth.Changed -= OnAuthChanged;
        AppServices.LibraryChanged -= OnLibraryChanged;
        PlaybackHistory.Changed -= OnHistoryChanged;
        _signInCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private void OnAuthChanged() => _ui.TryEnqueue(RefreshUi);
    private void OnLibraryChanged() => _ui.TryEnqueue(() => _ = LoadLibraryAsync());
    private void OnHistoryChanged() => _ui.TryEnqueue(FillHistory);

    private void RefreshUi()
    {
        var auth = AppServices.Auth;
        var signedIn = auth.IsSignedIn;

        SignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

        if (signedIn)
        {
            NameText.Text = string.IsNullOrWhiteSpace(auth.Name) ? "Signed in" : auth.Name;
            EmailText.Text = auth.Email ?? "";
            EmailText.Visibility = string.IsNullOrWhiteSpace(EmailText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            Avatar.DisplayName = NameText.Text;
            Avatar.Initials = Initials(NameText.Text);
            Avatar.ProfilePicture = Thumbnails.Create(auth.PhotoUrl, 112);
            _ = LoadLibraryAsync();
        }
        else
        {
            Playlists.Clear();
            HistoryRows.Clear();
            PlaylistsEmpty.Visibility = Visibility.Collapsed;
            HistoryEmpty.Visibility = Visibility.Collapsed;
            ResetClientLink.Visibility = auth.HasClient ? Visibility.Visible : Visibility.Collapsed;
            if (string.IsNullOrEmpty(SignInStatus.Text))
                SignInStatus.Text = auth.HasClient ? "" : "One-time Google Cloud setup is needed first.";
        }
    }

    private async Task LoadLibraryAsync()
    {
        if (_loading || !AppServices.Auth.IsSignedIn) return;
        _loading = true;
        try
        {
            var playlists = await AppServices.GetLibraryPlaylistsAsync();
            Playlists.Clear();
            foreach (var playlist in playlists) Playlists.Add(new ItemVM(playlist));
            PlaylistsEmpty.Visibility = Playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FillHistory();
        }
        catch (Exception ex)
        {
            AppServices.ReportError(ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private void FillHistory()
    {
        HistoryRows.Clear();
        foreach (var entry in PlaybackHistory.Items.Where(e => !AppServices.IsDisliked(e.VideoId)).Take(20))
        {
            var track = PlaybackHistory.ToTrack(entry);
            var subtitle = string.IsNullOrWhiteSpace(entry.Album) ? entry.Artists : $"{entry.Artists} • {entry.Album}";
            HistoryRows.Add(new ItemVM(new MediaItem(MediaKind.Song, entry.Title, subtitle,
                entry.ThumbnailUrl, VideoId: entry.VideoId, Track: track)));
        }
        HistoryEmpty.Visibility = HistoryRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new CreatePlaylistDialog();
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            await AppServices.CreatePlaylistAsync(dialog.PlaylistName);
            await LoadLibraryAsync();
        }
        catch (Exception ex)
        {
            AppServices.ReportError(ex.Message);
        }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Clear local history?",
            Content = new TextBlock
            {
                Text = "This clears the play history stored on this PC. Your YouTube account history is not touched.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            PlaybackHistory.Clear();
            FillHistory();
        }
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        var auth = AppServices.Auth;

        if (!auth.HasClient)
        {
            var setup = new GoogleSetupDialog();
            if (await Dialogs.ShowAsync(setup) != ContentDialogResult.Primary) return;
            try
            {
                auth.ConfigureClient(setup.ClientId, setup.ClientSecret);
            }
            catch (Exception ex)
            {
                SignInStatus.Text = "Could not store the Google app credentials: " + ex.Message;
                AppServices.ReportError(SignInStatus.Text);
                return;
            }
        }

        _signInCts?.Cancel();
        var cts = _signInCts = new CancellationTokenSource();
        SetBusy(true, "Opening your default browser\u2026");

        try
        {
            await auth.SignInAsync(cts.Token);
            SignInStatus.Text = "Signed in.";
        }
        catch (OperationCanceledException)
        {
            SignInStatus.Text = "Sign-in was cancelled or timed out. Press Sign in to try again.";
        }
        catch (Exception ex)
        {
            SignInStatus.Text = ex.Message;
            AppServices.ReportError(ex.Message);
        }
        finally
        {
            if (_signInCts == cts)
            {
                _signInCts = null;
                SetBusy(false, SignInStatus.Text);
            }
        }

        RefreshUi();
    }

    private void CancelSignIn_Click(object sender, RoutedEventArgs e)
    {
        _signInCts?.Cancel();
        SignInStatus.Text = "Cancelling\u2026";
    }

    private void SetBusy(bool busy, string status)
    {
        SignInButton.IsEnabled = !busy;
        CancelSignInButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SignInBusy.IsActive = busy;
        SignInBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrEmpty(status)) SignInStatus.Text = status;
    }

    // Liked Music is the account's special "LM" playlist.
    private void LikedSongs_Click(object sender, RoutedEventArgs e) =>
        AppServices.Navigate(new PageRequest("Liked Music", PageKind.Collection, Item: AppServices.LikedMusic));

    private void Playlists_Click(object sender, RoutedEventArgs e) =>
        AppServices.Navigate(new PageRequest("Your playlists", PageKind.Browse, "yomu_library_playlists"));

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Auth.SignOut();
        SignInStatus.Text = "Signed out.";
        RefreshUi();
    }

    private void ResetClient_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Auth.ResetAll();
        SignInStatus.Text = "Google app credentials cleared. Press Sign in to set them up again.";
        RefreshUi();
    }

    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(1, words[0].Length)].ToUpperInvariant(),
            _ => (words[0][0].ToString() + words[^1][0]).ToUpperInvariant()
        };
    }
}
