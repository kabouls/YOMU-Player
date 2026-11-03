using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// Basic account page: profile, Liked Music, Google account, sign out.
public sealed partial class ProfilePage : Page
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    public ProfilePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.Auth.Changed += OnAuthChanged;
        RefreshUi();
        BuildChangelog();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.Auth.Changed -= OnAuthChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnAuthChanged() => _ui.TryEnqueue(RefreshUi);

    private void RefreshUi()
    {
        var auth = AppServices.Auth;
        var signedIn = auth.IsSignedIn;
        SignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        if (!signedIn) return;

        var name = auth.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = auth.Email?.Split('@')[0];
        if (string.IsNullOrWhiteSpace(name)) name = "Account";
        NameText.Text = name;
        EmailText.Text = auth.Email ?? "";
        Avatar.DisplayName = name;
        Avatar.Initials = Initials(name);
        Avatar.ProfilePicture = Thumbnails.Create(auth.PhotoUrl, 192);
    }

    private void LikedButton_Click(object sender, RoutedEventArgs e) =>
        AppServices.Navigate(new PageRequest("Liked Music", PageKind.Collection, Item: AppServices.LikedMusic));

    private async void ManageAccount_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://myaccount.google.com/"));

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Auth.SignOut();
        RefreshUi();
    }

    private void SignIn_Click(object sender, RoutedEventArgs e) =>
        AppServices.Navigate(new PageRequest("Library", PageKind.Library));

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "Checking...";
        if (App.MainWindow is { } window) await window.CheckForUpdatesAsync(manual: true);
        UpdateStatusText.Text = "Check complete.";
    }

    private void BuildChangelog()
    {
        VersionLabel.Text = $"{AppPaths.ProductName} {AppPaths.Version}  ·  {AppPaths.Channel}";
        ChangelogPanel.Children.Clear();

        var entries = ChangelogService.Entries;
        if (entries.Count == 0)
        {
            ChangelogPanel.Children.Add(new TextBlock
            {
                Text = "No changelog is bundled with this build.",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            return;
        }

        foreach (var entry in entries)
        {
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(new TextBlock
            {
                Text = $"Version {entry.Version}  ·  {entry.Date}  ·  {entry.Channel}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            foreach (var note in entry.Notes)
                block.Children.Add(new TextBlock
                {
                    Text = "•  " + note,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });
            ChangelogPanel.Children.Add(block);
        }
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        return parts.Length == 1
            ? parts[0][..1].ToUpperInvariant()
            : (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
    }
}
