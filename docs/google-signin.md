# Google sign-in

The app signs in through your default browser using the installed-app OAuth flow:

1. **Sign in with Google** starts a loopback HTTP listener on `127.0.0.1` and opens Google's authorization page in
   your default browser.
2. Because the browser already has your Google session, you only choose the account and approve access - no password
   is typed into the app.
3. Google redirects to `http://127.0.0.1:<port>/?code=...`; the app validates `state`, exchanges the code at
   `https://oauth2.googleapis.com/token` with PKCE, and closes the listener.
4. The refresh token and the OAuth client credentials are encrypted with Windows DPAPI (current user) in
   `%LOCALAPPDATA%\YomuPlayer\auth.bin`.
5. Later launches refresh the access token silently. Requests to YouTube Music carry
   `Authorization: Bearer ...` and `X-Goog-AuthUser: 0`.

Scopes: `openid`, `email`, `profile` and `https://www.googleapis.com/auth/youtube`.

## Who needs the one-time Google Cloud setup

Since November 2024, YouTube Music requires an OAuth client ID and secret for the YouTube Data API. Google requires
each app to register its own client. That registration belongs to the **app**, not to each user:

- **If you downloaded a build that includes `google-client.json`** (the normal release), you do **not** need Google
  Cloud at all. Sign in with your Google account and approve access.
- **If you are building/running the app from source without a bundled client**, the app asks you for one once. The
  steps below are for that case (the app owner/developer).
- A build that does not include a client and is shared with others forces every user through the steps below. Do not
  distribute that; use [distribution.md](distribution.md).

### Steps (only if the app asks for them)

1. Open the [Google Cloud Console](https://console.cloud.google.com/) and create or select a project. Keep that
   project selected in the top bar for the rest of the steps.
2. Open the [YouTube Data API v3](https://console.cloud.google.com/apis/library/youtube.googleapis.com) page and
   click **Enable**. The free quota is enough; you do not need billing for this.
3. Configure the OAuth consent screen. The console now calls this **Google Auth Platform**; older accounts show
   **APIs & Services -> OAuth consent screen**.
   - **Branding**: app name (e.g. `YOMU Player`), user support email, developer contact email.
   - **Audience**: User type **External**. Keep the publishing status **Testing** for now.
   - **Data Access / Scopes**: add `https://www.googleapis.com/auth/youtube`. The basic `openid`, `email` and
     `profile` scopes are included automatically.
   - **Test users**: add the exact Google account you will sign in with (important - without this Google returns
     `access_denied`). You can add up to 100 while in Testing.
4. Create the OAuth client:
   - New UI: **Google Auth Platform -> Clients -> Create client**.
   - Old UI: **APIs & Services -> Credentials -> Create credentials -> OAuth client ID**.
   - Application type: **Desktop app**. This is the type that supports the loopback redirect used here. Do not pick
     "Web application" or "TVs and Limited Input devices".
   - Create it and copy the **client ID** and **client secret** (the client detail page or the Download JSON has
     both).
5. In the app: **Library -> Sign in with Google**. If the setup dialog appears, paste the client ID and secret and
   press **Continue**. The values are stored encrypted; you will not need them again on this PC.

The consent screen will show an "unverified app" warning while the OAuth app is in Testing mode. Choose **Advanced ->
Go to ... (unsafe)** to continue; this is expected for a personal OAuth client. Google may also show a separate
checkbox for each permission: **tick the YouTube permission** before continuing. If it is unchecked, the app signs in
with only your profile and every library request returns `403 insufficient authentication scopes`.

If you created the OAuth app before the YOMU Player rename, update **Branding -> App name** to `YOMU Player`; the
client ID and secret do not change.

### Why Home still works anonymously

YouTube Music's InnerTube endpoints currently reject OAuth access tokens with `400 INVALID_ARGUMENT`, even when the
token has the YouTube scope (this is a server-side change, tracked in
[ytmusicapi #813](https://github.com/sigma67/ytmusicapi/issues/813)). YOMU Player therefore uses the OAuth token only
with the official **YouTube Data API** for account library data (Liked Music, playlists, saving a queue), and sends
Home/Explore/search/playback requests to InnerTube **without** the token. This keeps browsing and playback working;
account-specific Home recommendations are not personalized.

## Distributing a build

The app owner can ship the client so users never see the setup dialog:

1. Create `google-client.json` from `google-client.example.json` and fill in the client ID/secret. It is git-ignored.
2. Run `powershell -File tools\pack-release.ps1 -Version 1.0.0`.
3. The zip includes the client (and yt-dlp when available). Users only approve access in the browser.
4. For arbitrary Google accounts, publish/verify the OAuth app:
   [google-oauth-verification.md](google-oauth-verification.md).

Full release steps: [distribution.md](distribution.md).

## What is stored

`%LOCALAPPDATA%\YomuPlayer\auth.bin` contains, encrypted with DPAPI:

- OAuth client ID and client secret
- refresh token
- display name, email and profile picture URL (from the OpenID `id_token`)

The access token is kept in memory only. Nothing is sent anywhere except Google's OAuth endpoints and
`music.youtube.com`. Signing out clears the tokens but keeps the client configuration; **Use different Google app
credentials** clears both.

## Limitations

- In **Testing** mode, only accounts added under **Test users** can sign in (up to 100), and refresh tokens expire
  after 7 days. Publishing/verifying the OAuth app removes both restrictions. When a token expires, the Library
  page shows the signed-out state and one click signs in again.
- The client secret for a Desktop app is not confidential (Google documents this); PKCE and the loopback listener are
  what protect the flow.
- The loopback listener only runs during a sign-in and is bound to `127.0.0.1`, not the network.
- Workspace accounts may be blocked by an administrator policy (`admin_policy_enforced`).

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `redirect_uri_mismatch` | The OAuth client is not type **Desktop app**. Create a Desktop app client, not Web/TV. |
| `access_denied` | Your account is not listed under **Test users**, or you declined the consent screen. Add the account and retry. |
| Signed in, but Home shows `403 Request had insufficient authentication scopes` | The token has no YouTube scope. Add `https://www.googleapis.com/auth/youtube` under **Data Access/Scopes**, remove the app at [myaccount.google.com/permissions](https://myaccount.google.com/permissions), sign out/in, and tick the YouTube permission on the consent screen. |
| Signed in, but Home or library shows `400 INVALID_ARGUMENT` | This build is older than the Data API workaround. Update to the current build; InnerTube no longer accepts OAuth tokens, while the Data API library path does. |
| `invalid_client` / "client_id and client_secret mismatch" | Wrong ID/secret, or the YouTube Data API is not enabled for the project. Re-copy both values. |
| "Google did not return a refresh token" | Remove the app at [myaccount.google.com/permissions](https://myaccount.google.com/permissions), then sign in again. |
| Signed out after about 7 days | Testing-mode refresh-token limit; sign in again or publish the OAuth app. |
| Browser shows "This site can't be reached" after approving | The app was closed or a firewall/antivirus blocked the loopback port. Keep the app open and retry; check `log.txt`. |
| Sign-in window never comes back | The browser tab may still be on the local success page. The app also activates itself after a successful exchange. |
| Nothing happens after Continue | Check `%LOCALAPPDATA%\YomuPlayer\log.txt`; the token exchange error is logged and shown in the info bar. |
| Downloaded release still asks for a client | The release was built without `google-client.json`. Ask the maintainer for a build with it, or use your own client with the steps above. |

To reset manually, close the app and delete `%LOCALAPPDATA%\YomuPlayer\auth.bin`.
