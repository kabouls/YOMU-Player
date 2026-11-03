using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YomuPlayer.Desktop;

// One-time Google Cloud OAuth client setup. Shown only when no client ID/secret is stored yet.
public sealed partial class GoogleSetupDialog : ContentDialog
{
    public GoogleSetupDialog()
    {
        InitializeComponent();
    }

    public string ClientId => ClientIdBox.Text.Trim();

    public string ClientSecret => ClientSecretBox.Password.Trim();

    private async void Console_Click(object sender, RoutedEventArgs e) =>
        await OpenAsync("https://console.cloud.google.com/apis/credentials");

    private async void Api_Click(object sender, RoutedEventArgs e) =>
        await OpenAsync("https://console.cloud.google.com/apis/library/youtube.googleapis.com");

    private static async Task OpenAsync(string url)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception e)
        {
            AppServices.ReportError(e.Message);
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            ErrorText.Text = "Paste both the OAuth client ID and the client secret.";
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        if (!ClientId.Contains(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            ErrorText.Text = "That does not look like a Google OAuth client ID. It usually ends with " +
                             ".apps.googleusercontent.com.";
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
        }
    }
}
