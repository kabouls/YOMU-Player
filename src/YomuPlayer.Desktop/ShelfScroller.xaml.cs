using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace YomuPlayer.Desktop;

// A horizontal shelf with YouTube-Music-style arrows. A normal mouse wheel scrolls the page vertically;
// Shift+wheel scrolls this shelf horizontally. Trackpad horizontal scrolling keeps working, and touch/pen
// gets native horizontal panning. Native mouse-wheel horizontal scrolling is suppressed so the page does not
// jump sideways when the pointer happens to be over a shelf.
public sealed partial class ShelfScroller : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(object), typeof(ShelfScroller), new PropertyMetadata(null));

    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(ShelfScroller), new PropertyMetadata(null));

    public ShelfScroller()
    {
        InitializeComponent();
        Scroller.AddHandler(PointerWheelChangedEvent,
            new PointerEventHandler(Scroller_PointerWheelChanged), true);
    }

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    private void ShelfScroller_Loaded(object sender, RoutedEventArgs e) => UpdateArrows();

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateArrows();

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateArrows();

    private void Scroller_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Touch/pen should retain native horizontal panning. Mouse never gets native horizontal scrolling;
        // wheel is handled explicitly above so it cannot move the shelf when the page is being scrolled.
        if (e.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen)
            Scroller.HorizontalScrollMode = ScrollMode.Enabled;
    }

    private void Scroller_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen)
            Scroller.HorizontalScrollMode = ScrollMode.Disabled;
    }

    private void Scroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Scroller);
        var props = point.Properties;
        var delta = props.MouseWheelDelta;
        if (delta == 0) return;

        var shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        if (props.IsHorizontalMouseWheel || shift)
        {
            e.Handled = true;
            var step = Math.Max(120, Math.Abs(delta) * 1.5);
            var target = Scroller.HorizontalOffset - Math.Sign(delta) * step;
            Scroller.ChangeView(ClampHorizontal(target), null, null, true);
            return;
        }

        // Ordinary vertical wheel: leave the event alone so the page's ScrollViewer handles it natively
        // (smooth, same step as scrolling over a vertical list). The shelf has horizontal mouse wheel disabled,
        // so it cannot consume this event or move sideways.
    }

    private void LeftButton_Click(object sender, RoutedEventArgs e) => ScrollBy(-1);

    private void RightButton_Click(object sender, RoutedEventArgs e) => ScrollBy(1);

    private void ScrollBy(int direction)
    {
        var step = Math.Max(260, Scroller.ViewportWidth * 0.8);
        var target = Scroller.HorizontalOffset + direction * step;
        Scroller.ChangeView(ClampHorizontal(target), null, null, false);
    }

    private double ClampHorizontal(double target) =>
        Math.Clamp(target, 0, Math.Max(0, Scroller.ScrollableWidth));

    private void UpdateArrows()
    {
        if (Scroller.ScrollableWidth <= 1)
        {
            LeftButton.Visibility = Visibility.Collapsed;
            RightButton.Visibility = Visibility.Collapsed;
            return;
        }

        LeftButton.Visibility = Scroller.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
        RightButton.Visibility = Scroller.HorizontalOffset < Scroller.ScrollableWidth - 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
