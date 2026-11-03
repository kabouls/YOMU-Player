using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// "This is what we found in your pasted link. Open it?"
public sealed partial class LinkConfirmDialog : ContentDialog
{
    public LinkConfirmDialog(LinkPreview preview)
    {
        InitializeComponent();

        Title = preview.Kind switch
        {
            MediaKind.Song or MediaKind.Video => "Play this song?",
            MediaKind.Playlist => "Open this playlist?",
            MediaKind.Album => "Open this album?",
            MediaKind.Artist => "Open this artist?",
            _ => "Open this link?"
        };
        PrimaryButtonText = preview.ActionText;

        KindText.Text = Kinds.Label(preview.Kind);
        KindPill.Background = Kinds.Brush(preview.Kind);
        TitleText.Text = preview.Title;
        DetailText.Text = preview.Detail;
        ArtBrush.ImageSource = Thumbnails.Create(preview.ThumbnailUrl, 240);
        ArtBorder.CornerRadius = preview.Kind == MediaKind.Artist ? new CornerRadius(56) : new CornerRadius(10);
    }
}
