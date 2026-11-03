using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

public sealed partial class ShelvesPage : Page
{
    public ObservableCollection<ShelfVM> Shelves { get; } = new();

    public ShelvesPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not PageRequest request) return;

        PageTitle.Text = request.Title;
        Busy.IsActive = true;
        try
        {
            BrowsePage page;
            if (request.BrowseId == "yomu_library_playlists")
            {
                var items = await AppServices.GetLibraryPlaylistsAsync();
                page = new BrowsePage("Your playlists",
                    new[] { new Shelf("Your playlists", ShelfStyle.List, items) });
            }
            else
            {
                page = await AppServices.GetBrowseAsync(request.BrowseId ?? "FEmusic_home", request.BrowseParams);
            }

            if (request.Kind == PageKind.Browse && !string.IsNullOrWhiteSpace(page.Title))
                PageTitle.Text = page.Title;

            foreach (var shelf in page.Shelves) Shelves.Add(new ShelfVM(shelf));
            EmptyText.Visibility = Shelves.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppServices.ReportError(ex.Message);
        }
        finally
        {
            Busy.IsActive = false;
        }
    }
}
