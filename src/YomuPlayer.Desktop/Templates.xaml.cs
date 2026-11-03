using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

public sealed partial class Templates : ResourceDictionary
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private DispatcherQueueTimer? _hoverTimer;
    private Track? _hoverTrack;

    public Templates()
    {
        InitializeComponent();
    }

    // Hovering a song shows a play button over its art, and after a short dwell warms its stream
    // so the click that follows starts almost immediately.
    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row) return;
        if (FindByTag(row, "HoverPlay") is { } overlay) overlay.Opacity = 1;

        var track = TrackOf(row.DataContext);
        if (track is null) return;

        _hoverTrack = track;
        if (_hoverTimer is null)
        {
            _hoverTimer = _ui.CreateTimer();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(450);
            _hoverTimer.IsRepeating = false;
            _hoverTimer.Tick += (t, _) =>
            {
                t.Stop();
                if (_hoverTrack is { } warm) AppServices.Prefetch(warm);
            };
        }
        else
        {
            _hoverTimer.Stop();
        }
        _hoverTimer.Start();
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
        _hoverTimer?.Stop();
        _hoverTrack = null;
    }

    private static Track? TrackOf(object? context) => context switch
    {
        ItemVM { Item.IsTrack: true } vm => vm.Item.Track,
        TrackVM tv => tv.Track,
        _ => null
    };

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
