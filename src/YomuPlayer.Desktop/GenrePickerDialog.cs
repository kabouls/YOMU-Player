using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

public sealed class GenrePickerDialog : ContentDialog
{
    private readonly ListView _list;

    public GenrePickerDialog(IReadOnlyList<MediaItem> genres, string? selectedBrowseId)
    {
        Title = "Pick a genre";
        PrimaryButtonText = "Use genre";
        SecondaryButtonText = "Clear";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        _list = new ListView
        {
            ItemsSource = genres,
            DisplayMemberPath = nameof(MediaItem.Title),
            SelectionMode = ListViewSelectionMode.Single,
            Height = 340,
            MinWidth = 320
        };
        _list.SelectedItem = genres.FirstOrDefault(g =>
            string.Equals(g.BrowseId, selectedBrowseId, StringComparison.Ordinal));

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Choose a YouTube Music mood or genre. This adds a manual genre row to Home; it does not change your account recommendations.",
                    TextWrapping = TextWrapping.Wrap
                },
                _list
            }
        };
    }

    public MediaItem? SelectedGenre => _list.SelectedItem as MediaItem;
}
