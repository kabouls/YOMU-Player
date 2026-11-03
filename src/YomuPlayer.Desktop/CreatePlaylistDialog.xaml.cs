using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YomuPlayer.Desktop;

public sealed partial class CreatePlaylistDialog : ContentDialog
{
    public CreatePlaylistDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus(FocusState.Programmatic);
    }

    public string PlaylistName => NameBox.Text.Trim();

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(PlaylistName))
        {
            ErrorText.Text = "Enter a playlist name.";
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
        }
    }
}
