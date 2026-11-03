using Microsoft.UI.Xaml.Controls;

namespace YomuPlayer.Desktop;

// Shown while the window is minimized: the real page (and every thumbnail on it) is released.
public sealed partial class BlankPage : Page
{
    public BlankPage()
    {
        InitializeComponent();
    }
}
