using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using YomuPlayer.Core;

namespace YomuPlayer.Desktop;

// Right-click menu, modelled on YouTube Music's. Items that need an account are shown disabled until login exists.
public static class ContextMenus
{
    public static MediaItem FromTrack(Track t, bool canEdit = false) =>
        new(MediaKind.Song, t.Title, t.ArtistLine, t.ThumbnailUrl, VideoId: t.VideoId, Track: t, CanEdit: canEdit);

    public static void Show(FrameworkElement anchor, MediaItem item, Point position, Action<MenuFlyout>? customize = null)
    {
        var flyout = Build(item);
        customize?.Invoke(flyout);
        flyout.ShowAt(anchor, new FlyoutShowOptions { Position = position });
    }

    private static MenuFlyout Build(MediaItem item)
    {
        var f = new MenuFlyout();
        var url = LinkOf(item);

        void Add(string text, string? glyph, Func<Task> run, bool enabled = true, IconElement? icon = null)
        {
            var mi = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            if (icon is not null) mi.Icon = icon;
            else if (glyph is not null) mi.Icon = new FontIcon { Glyph = glyph };
            mi.Click += async (_, _) =>
            {
                try { await run(); }
                catch (Exception e) { AppServices.ReportError(e.Message); }
            };
            f.Items.Add(mi);
        }

        void Sep() => f.Items.Add(new MenuFlyoutSeparator());

        void Link()
        {
            if (url is null) return;
            Sep();
            Add("Copy link", "\uE71B", () =>
            {
                var data = new DataPackage();
                data.SetText(url);
                Clipboard.SetContent(data);
                return Task.CompletedTask;
            });
            Add("Open in browser", "\uE8A7", async () => await Launcher.LaunchUriAsync(new Uri(url)));
        }

        void NeedsSignIn(params (string Text, string? Glyph)[] entries)
        {
            Sep();
            foreach (var (text, glyph) in entries) Add(text + " (sign in)", glyph, () => Task.CompletedTask, enabled: false);
        }

        if (item.IsTrack && item.Track is { } t)
        {
            Add("Play", "\uE768", () => AppServices.Player.PlayTrackWithRadioAsync(t));
            Add("Start radio", "\uE93E", async () =>
            {
                var radio = await AppServices.Yt.GetRadioAsync(t.VideoId);
                await AppServices.Player.PlayQueueAsync(radio.Count > 0 ? radio : new[] { t }, 0);
            });
            Add("Play next", "\uE893", () => AppServices.Player.AddNextAsync(new[] { t }));
            Add("Add to queue", "\uE710", () => AppServices.Player.AddToQueueAsync(new[] { t }));

            if (t.Artists.Count > 0 || t.AlbumId is not null) Sep();
            if (t.Artists.Count > 0)
            {
                var artist = t.Artists[0];
                Add("Go to artist", "\uE77B", () =>
                {
                    AppServices.OpenArtist(artist);
                    return Task.CompletedTask;
                });
            }
            if (t.AlbumId is not null && !string.IsNullOrWhiteSpace(t.Album))
            {
                var album = new MediaItem(MediaKind.Album, t.Album!, t.ArtistLine, t.ThumbnailUrl, BrowseId: t.AlbumId);
                Add("Go to album", null, () =>
                {
                    AppServices.Navigate(new PageRequest(album.Title, PageKind.Collection, Item: album));
                    return Task.CompletedTask;
                });
            }

            Link();
            if (AppServices.Auth.IsSignedIn)
            {
                Sep();
                Add("Add to playlist", "\uE710", () => AddToPlaylistAsync(t));
                Add("Like", null, () => AppServices.LikeTrackAsync(t));

                if (item.CanEdit && t.PlaylistItemId is not null && t.PlaylistId is not null)
                {
                    var liked = t.PlaylistId is "LM" or "LL";
                    Add(liked ? "Remove from Liked Music" : "Remove from this playlist", "\uE74D",
                        () => AppServices.RemoveFromPlaylistAsync(t));
                }
            }
            else
            {
                NeedsSignIn(("Add to playlist", "\uE710"), ("Like", null));
            }
            return f;
        }

        if (item.Kind is MediaKind.Album or MediaKind.Playlist)
        {
            Add("Play", "\uE768", async () =>
                await AppServices.PlayCollectionAsync(await LoadAsync(item), 0, shuffle: false,
                    sourceTitle: item.Title));
            Add("Shuffle", "\uE8B1", async () =>
            {
                var tracks = await LoadAsync(item);
                await AppServices.PlayCollectionAsync(tracks, Random.Shared.Next(Math.Max(1, tracks.Count)),
                    shuffle: true, sourceTitle: item.Title);
            });
            Add("Play next", "\uE893", async () => await AppServices.Player.AddNextAsync(await LoadAsync(item)));
            Add("Add to queue", "\uE710", async () => await AppServices.Player.AddToQueueAsync(await LoadAsync(item)));
            Sep();
            Add("Open", "\uE8A7", () =>
            {
                AppServices.Navigate(new PageRequest(item.Title, PageKind.Collection, Item: item));
                return Task.CompletedTask;
            });
            Link();
            if (item.CanEdit && item.PlaylistId is { } playlistId && playlistId is not "LM" and not "LL")
            {
                Sep();
                Add("Delete playlist", "\uE74D", () => DeletePlaylistAsync(item));
            }
            return f;
        }

        if (item.Kind == MediaKind.Artist)
        {
            Add("Open", "\uE8A7", () => AppServices.ActivateAsync(item));
            Link();
        }
        return f;
    }

    private static async Task DeletePlaylistAsync(MediaItem playlist)
    {
        if (!await Dialogs.ConfirmDeletePlaylistAsync(playlist.Title)) return;

        await AppServices.DeletePlaylistAsync(playlist);
        AppServices.Navigate(new PageRequest("Home", PageKind.Home, "FEmusic_home"));
    }

    private static async Task AddToPlaylistAsync(Track track)
    {
        if (!AppServices.Auth.IsSignedIn)
        {
            AppServices.ReportError("Sign in with Google to add to a playlist.");
            return;
        }

        var dialog = new SaveQueueDialog(new[] { track }, "Add to playlist");
        await Dialogs.ShowAsync(dialog);
        if (!string.IsNullOrWhiteSpace(dialog.SavedMessage)) AppServices.ReportInfo(dialog.SavedMessage);
    }

    private static async Task<IReadOnlyList<Track>> LoadAsync(MediaItem item)
    {
        var tracks = await AppServices.LoadCollectionTracksAsync(item);
        if (tracks.Count == 0) AppServices.ReportError("Couldn't load the tracks of \u201C" + item.Title + "\u201D.");
        return tracks;
    }

    private static string? LinkOf(MediaItem item) => item.Kind switch
    {
        MediaKind.Song or MediaKind.Video when item.VideoId is not null => "https://music.youtube.com/watch?v=" + item.VideoId,
        MediaKind.Album when item.BrowseId is not null => "https://music.youtube.com/browse/" + item.BrowseId,
        MediaKind.Playlist when item.PlaylistId is not null => "https://music.youtube.com/playlist?list=" + item.PlaylistId,
        MediaKind.Artist when item.BrowseId is not null => "https://music.youtube.com/channel/" + item.BrowseId,
        _ => null
    };
}
