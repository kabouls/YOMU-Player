# YOMU Player

A lightweight YouTube Music desktop client for Windows, built with WinUI 3 and .NET 8. It is a native app (no
Electron, no embedded browser): pages come from YouTube Music's InnerTube API, audio is played by LibVLC, and stream
URLs are resolved by [yt-dlp](https://github.com/yt-dlp/yt-dlp).

## Requirements

- Windows 10 1809+ or Windows 11, x64
- .NET 8 SDK (to build)
- yt-dlp, either on `PATH` or installed by `tools\install-fast-ytdlp.ps1` into
  `%LOCALAPPDATA%\YomuPlayer\yt-dlp\yt-dlp.exe`

## Build and run

```powershell
dotnet build src\YomuPlayer.Desktop\YomuPlayer.Desktop.csproj -c Debug
.\src\YomuPlayer.Desktop\bin\Debug\net8.0-windows10.0.19041.0\win-x64\YomuPlayer.exe
```

The build is self-contained (the Windows App SDK runtime and LibVLC ship next to the exe), so the output folder can
be copied to another machine.

## Features

- **Browse**: Home, Explore, search with rich predictions, albums, playlists, artists, mood/genre chips.
- **Play**: song radio, persistent queue, autoplay, shuffle, repeat, quality selection, prefetch of the next song.
- **Now playing page**: large centered artwork with the library sidebar still visible, and two tabs - **Up next**
  and **Lyrics**. The queue header shows "Playing from ..." and a Save-to-playlist button; tracks show explicit
  badges and the current row uses YouTube Music's gray highlight.
- **Lite mode**: a 420x228 window with transport, shuffle/repeat and volume. It releases the page and thumbnails and
  trims the working set, while playback and the queue keep running.
- **Windows integration**: System Media Transport Controls (media keys, volume flyout), taskbar thumbnail buttons,
  `yomu://` links, single instance, close confirmation.
- **Links**: paste a YouTube Music / YouTube link or send one with `yomu://`; supported kinds are previewed
  before anything plays.
- **Library sidebar**: when signed in, the sidebar lists **Liked Music** and the account's playlists, plus a
  **New playlist** action. Playlists open as normal collections.
- **Google account**: sign in through your default browser (OAuth loopback + PKCE, no password in the app), then open
  liked songs and your playlists from Library. One-time Google Cloud setup is required; see
  [docs/google-signin.md](docs/google-signin.md).
- **Playback device seam**: the Core library exposes a serializable playback snapshot and command set so Android /
  Android TV remotes can control this app later. See [docs/remote-playback.md](docs/remote-playback.md).

### What was added in this update

- **Now playing page** (`PlayerPage`): a large centered square artwork, the library sidebar stays visible, and only
  **UP NEXT** / **LYRICS** remain (with a white selected underline). No Comments, Related, filter chips or metadata
  under the art.
- **Lyrics**: fetched from the song's YouTube Music lyrics page (provider shown in the corner), cached per track,
  selectable text, and a "Search the web" fallback when a song has none. Loading is lazy: nothing is fetched until
  the tab is opened.
- **Up next**: "Playing from <radio/mix/playlist>", a white Save pill that saves the queue to an existing or new
  playlist, click-to-play rows, the current row as a gray highlight with a play glyph, explicit `E` badges, and the
  right-click menu (including *Remove from queue*). Autoplay moved to the gear flyout.
- **Open/close control**: the separate Lyrics and Up next buttons were replaced by one chevron at the far right of
  the bar - up opens the player, down closes it.
- **Library sidebar**: signed-in accounts get Liked Music, their playlists and a New playlist action in the sidebar.
- **Backend**: playlist listing/creation/edit calls (`playlist/create`, `browse/edit_playlist`), queue source title,
  and explicit-flag parsing from YouTube Music badges.
- **Google sign-in**: a red *Sign in with Google* button in Library opens the default browser, captures the loopback
  redirect, exchanges the code with PKCE, and stores the refresh token with DPAPI. Liked Music and playlists then
  appear in the sidebar; the token is refreshed silently on later launches.
- **Contrast/UI**: Lite mode is a red **Lite mode** pill with white text, accent button glyphs, dialog primary
  buttons and slider thumbs are white, and the Autoplay toggle knob is white.
- **Player bar**: the now-playing area is a button; volume/mute, devices and the open/close chevron live on the
  right; volume is driven by a change event.
- **Home/Explore layout fix**: list shelves are measured against the page width instead of inside the horizontal
  carousel, so the second column is no longer clipped at 125% display scaling.
- **Startup stability**: the taskbar-button retry no longer uses a short-lived `DispatcherQueueTimer`, which could
  crash with an `AccessViolationException` in `Stop()`.

## Keyboard shortcuts

| Keys | Action |
| --- | --- |
| `Space` / `K` | Play / pause |
| `J` / `L` | Seek 10 s back / forward |
| `←` / `→` | Seek 5 s (when focus is not in a list) |
| `Ctrl+←` / `Ctrl+→` | Seek 5 s |
| `Shift+N` / `Shift+P` | Next / previous |
| `M` | Mute |
| `↑` / `↓` | Volume +/- 5% |
| `Ctrl+↑` / `Ctrl+↓` | Volume +/- 5% |
| `0`-`9` | Jump to 0-90% |
| `/` | Focus search |
| `Alt+←` / `Alt+→` | Back / forward |
| `Ctrl+V` | Open a copied link |
| Mouse back/forward | Back / forward |

## Diagnostics

`YomuPlayer.Cli` is a console harness for the same Core code:

```powershell
$cli = "src\YomuPlayer.Cli\bin\Debug\net8.0\YomuPlayer.Cli.exe"
& $cli next fa5IWHDbftI       # lyrics + related ids, then the lyrics text
& $cli related fa5IWHDbftI    # parsed Related shelves
& $cli search "daft punk"
& $cli parse-dump dump        # parse captured API responses without network
```

Paths:

- log: `%LOCALAPPDATA%\YomuPlayer\log.txt`
- settings: `%LOCALAPPDATA%\YomuPlayer\settings.json`
- encrypted Google credentials/token: `%LOCALAPPDATA%\YomuPlayer\auth.bin`

## Google sign-in

Library -> **Sign in with Google** opens Google in your default browser. If you are already signed in there, you only
pick the account and approve access; the browser redirects to a loopback listener in this app and the window comes
back. The refresh token is stored with Windows DPAPI, so later launches sign in silently.

The OAuth client belongs to the **app**, not to each user:

- **Release builds** ship a git-ignored `google-client.json` next to the exe. Downloading the app is enough: press
  Sign in, pick your Google account, approve, and you are back. No Google Cloud project or client ID.
- **Running from source without that file** asks for a one-time OAuth client: enable **YouTube Data API v3**, create
  an OAuth client of type **Desktop app**, and paste its ID/secret. Full steps and troubleshooting:
  [docs/google-signin.md](docs/google-signin.md).

Account library data (Liked Music, playlists, saving a queue) uses the official **YouTube Data API**, because YouTube
Music's InnerTube endpoints now reject OAuth tokens with `400 INVALID_ARGUMENT` (the same breakage documented in
[ytmusicapi #813](https://github.com/sigma67/ytmusicapi/issues/813)). Home, Explore, search, queue generation and
playback stay on **anonymous** InnerTube, so they keep working without the account token; account-specific home
recommendations are therefore not personalized.

While the OAuth app is in Google's *Testing* mode, only Test users can sign in and refresh tokens expire after
7 days. Publishing and **verifying** the OAuth app removes both restrictions; verification does not change the app's
code. See [docs/google-oauth-verification.md](docs/google-oauth-verification.md).

## Publishing a build

The app owner creates the OAuth client once, then bakes it into the release:

```powershell
Copy-Item google-client.example.json google-client.json   # fill in clientId/clientSecret; git-ignored
powershell -File tools\pack-release.ps1 -Version 1.0.0
```

The script builds a self-contained Release, copies `google-client.json` and (when available) `yt-dlp.exe` into the
folder, and writes `dist\YOMU-Player-1.0.0-win-x64.zip` plus its SHA-256. Users then only approve access in the
browser. Test-user limits, verification, code signing and the yt-dlp/YouTube terms caveat are covered in
[docs/distribution.md](docs/distribution.md). Privacy/terms templates for Google verification are in
[docs/privacy-policy-template.md](docs/privacy-policy-template.md) and [docs/terms-template.md](docs/terms-template.md).

## Architecture

```
src/
  YomuPlayer.Core/        InnerTube (anonymous browse/playback), YouTube Data API (account library),
                         response parsers, playback queue, PlayerService, stream resolution, device contracts
  YomuPlayer.Audio.Vlc/   LibVLCSharp implementation of IAudioPlayer
  YomuPlayer.Desktop/     WinUI 3 app: pages, player bar, lite mode, SMTC, window interop
  YomuPlayer.Desktop/Auth OAuth browser flow, PKCE, loopback listener, DPAPI token store
  YomuPlayer.Cli/         diagnostics / parser capture tool
  YomuPlayer.PlayCli/     small playback smoke tool
```

Lightweight choices worth keeping:

- No XAML framework beyond WinUI, no web view, no DI container, no MVVM toolkit.
- Caches are bounded: search 3 min, browse 5 min, lyrics 16 tracks, extras 24 tracks.
- Lyrics are fetched only when the Lyrics tab is opened, and the lyrics browse id is captured from the radio
  response that already has to be fetched, so opening the player page usually costs no extra request.
- Lite mode releases the frame's page (navigates to `BlankPage`), stops the bar timer, and calls the memory trimmer.
- Artwork decode sizes match what is drawn (96 px in rows, 544 px on the player page).

## Next steps

1. **Flesh out the account features**: likes/dislikes, subscriptions, history and playlist editing on top of the
   existing authenticated InnerTube calls; show the account avatar in the title bar.
2. **Android and Android TV apps**: share the Core contracts (queue, snapshot, commands) and implement the transport
   described in [docs/remote-playback.md](docs/remote-playback.md), including "play on this device" and transfer.
3. **Public release**: verify the OAuth app, publish signed builds and add update checks; reassess the
   yt-dlp/YouTube Terms of Service risk before a wide distribution.
4. **Polish**: drag-to-reorder in Up next, synced lyrics when YouTube provides timing, richer artist pages, and
   library pagination.

## Notes

- Playback needs yt-dlp. Without it the UI still browses and lyrics load, but audio resolution fails with a clear
  error in the info bar. Release builds bundle it when `tools\pack-release.ps1` can find a local copy.
- The app is not affiliated with YouTube or Google. Use it with your own account and content. Review YouTube's Terms
  of Service before distributing a public build that resolves streams.
#   Y O M U - P l a y e r  
 