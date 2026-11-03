using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// Account entry point: sign in with Google, then open liked songs or the account's playlists.
public sealed partial class LibraryPage : Page
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _signInCts;

    public LibraryPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.Auth.Changed += OnAuthChanged;
        RefreshUi();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.Auth.Changed -= OnAuthChanged;
        _signInCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private void OnAuthChanged() => _ui.TryEnqueue(RefreshUi);

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
        }
        else
        {
            ResetClientLink.Visibility = auth.HasClient ? Visibility.Visible : Visibility.Collapsed;
            if (string.IsNullOrEmpty(SignInStatus.Text))
                SignInStatus.Text = auth.HasClient ? "" : "One-time Google Cloud setup is needed first.";
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
