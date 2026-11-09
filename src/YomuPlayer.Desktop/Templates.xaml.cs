using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

public sealed partial class Templates : ResourceDictionary
{
    public Templates()
    {
        InitializeComponent();
    }

    // Hovering a song shows a play button over its art. Stream warm-up is deliberately not started on
    // hover: the yt-dlp process consumes CPU/disk and causes system-wide cursor hitches on some PCs.
    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row) return;
        if (FindByTag(row, "HoverPlay") is { } overlay) overlay.Opacity = 1;
    }

    private void Row_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement row) return;
        var item = row.DataContext switch
        {
            ItemVM vm => vm.Item,
            TrackVM tv => ContextMenus.FromTrack(tv.Track, tv.CanEditPlaylist),
            _ => null
        };
        if (item is null || item.Kind == MediaKind.Nav) return;

        if (!e.TryGetPosition(row, out var position))
            position = new Windows.Foundation.Point(row.ActualWidth / 2, row.ActualHeight / 2);
        ContextMenus.Show(row, item, position);
        e.Handled = true;
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row) return;
        if (FindByTag(row, "HoverPlay") is { } overlay) overlay.Opacity = 0;
    }

    private static FrameworkElement? FindByTag(DependencyObject root, string tag)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Tag as string == tag) return fe;
            if (FindByTag(child, tag) is { } found) return found;
        }
        return null;
    }
}
