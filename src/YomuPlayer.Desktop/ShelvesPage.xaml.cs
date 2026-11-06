using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

public sealed partial class ShelvesPage : Page
{
    private PageRequest? _request;
    private bool _regionChanging;

    public ObservableCollection<ShelfVM> Shelves { get; } = new();

    private sealed record RegionChoice(string Code, string Label);

    public ShelvesPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not PageRequest request) return;
        _request = request;

        PageTitle.Text = request.Title;
        if (request.Kind == PageKind.Browse && request.BrowseId == "FEmusic_explore")
            SetupRegion();
        else
            RegionPanel.Visibility = Visibility.Collapsed;

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_request is not { } request) return;
        Busy.IsActive = true;
        Shelves.Clear();
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

    private void SetupRegion()
    {
        var choices = new List<RegionChoice>
        {
            new("auto", AppServices.CurrentRegionLabel),
            new("worldwide", "Worldwide — no country filter")
        };

        try
        {
            var regions = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                .Select(culture => new RegionInfo(culture.Name))
                .Where(region => region.TwoLetterISORegionName.Length == 2)
                .GroupBy(region => region.TwoLetterISORegionName)
                .Select(group => group.First())
                .OrderBy(region => region.EnglishName)
                .Select(region => new RegionChoice(region.TwoLetterISORegionName,
                    region.EnglishName + " (" + region.TwoLetterISORegionName + ")"));
            choices.AddRange(regions);
        }
        catch
        {
            // A partial region list still lets the user pick Auto/Worldwide.
        }

        _regionChanging = true;
        RegionBox.ItemsSource = choices;
        RegionBox.SelectedItem = choices.FirstOrDefault(c => c.Code == AppSettings.Current.Region) ?? choices[0];
        _regionChanging = false;
        RegionPanel.Visibility = Visibility.Visible;
    }

    private async void RegionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_regionChanging || RegionBox.SelectedItem is not RegionChoice choice) return;
        _regionChanging = true;
        try
        {
            AppServices.SetRegion(choice.Code);
            await LoadAsync();
        }
        finally
        {
            _regionChanging = false;
        }
    }
}
