# Google OAuth verification (for a public release)

Verification is a Google review of the **OAuth app** (consent screen + client), not of this source code. You need it
when you want **any Google account** to sign in, without adding each person as a Test user, without the "unverified
app" warning, and without the 7-day Testing-mode expiry.

This is the app owner's task; you submit it from your Google Cloud project. The steps below are a checklist, not a
guarantee of approval.

## What you need

| Item | Notes |
| --- | --- |
| Public homepage | Your GitHub repository URL works |
| Privacy policy URL | GitHub Pages is fine; use [privacy-policy-template.md](privacy-policy-template.md) |
| Terms of service URL | GitHub Pages; use [terms-template.md](terms-template.md) |
| Support email | A Google account email you monitor |
| App name | e.g. "YOMU Player" |
| App logo | Square PNG, at least 120x120 |
| Authorized domain | The domain hosting the privacy policy/homepage |
| Demo video | 1-3 minutes on YouTube (unlisted is fine) showing the full sign-in flow |
| Test account | A Google account that is **not** on the Test users list |
| Scope justification | Why the app needs `https://www.googleapis.com/auth/youtube` |

## 1. Publish the privacy policy and terms

Put both pages on a public URL. The simplest setup is GitHub Pages from the repository's `docs/` folder, or raw
pages served by GitHub Pages. Google must be able to open them without signing in.

Fill in the templates:
- [privacy-policy-template.md](privacy-policy-template.md)
- [terms-template.md](terms-template.md)

## 2. Fill the OAuth consent screen

Google Cloud Console → **APIs & Services → OAuth consent screen → Edit app**:

- **App information**: app name, user support email, app logo.
- **App domain**: application home page, privacy policy, terms of service, authorized domains.
- **Developer contact information**: your email.
- **Scopes**: `https://www.googleapis.com/auth/youtube` (and the basic `openid`, `email`, `profile`).
- **Test users**: keep your own account while testing.

### Scope justification (suggested text)

> YOMU Player uses the YouTube scope to read the signed-in user's liked music, saved playlists and library so the
> app can display them, and to add tracks to a playlist when the user explicitly chooses Save/New playlist. It does
> not access Gmail, Drive, contacts or any other Google data. All tokens are stored locally on the user's device; the
> app has no server.

## 3. Test the consent screen

1. With the app still in **Testing**, add your account as a Test user and sign in once.
2. After publishing to production, use a **different Google account that is not a Test user** and confirm it can
   sign in.
3. If that account sees "Access blocked" or a test-user error, the app is still in Testing or the consent screen is
   incomplete.

## 4. Publish and submit for verification

1. OAuth consent screen → **Publish app** (Testing → In production).
2. Open **Verification Center** and submit verification for the `youtube` scope.
3. Answer Google's questions; respond to any emails promptly.

Publishing removes the 7-day Testing refresh-token expiry. Verification removes the "unverified app" warning and is
what makes a public GitHub release invisible to Google Cloud for your users.

## 5. Demo video script

Record the actual app, not slides:

1. Open the app; show Library and the sidebar.
2. Click **Sign in with Google**; the default browser opens Google's consent page.
3. Show the account chooser and the consent screen (scope: YouTube).
4. Approve; show the redirect back and the app's signed-in Library/profile.
5. Show Liked Music/playlists loading, and the Save queue action (if asked about the write scope).
6. Show Sign out and sign back in (optional).
7. Mention in the video or description that the app stores tokens locally and has no backend.

Keep it 1-3 minutes. No music with copyright claims.

## Common rejection reasons

- Privacy policy URL is missing, private, or does not mention Google/YouTube data.
- The demo video does not show the OAuth consent screen or the redirect back.
- Scope justification is vague ("to improve the app") or does not mention the YouTube scope.
- App name/logo on the consent screen does not match the app.
- The app is not publicly accessible or the homepage is a placeholder.
- The app requests more scopes than it uses.

## Important: YouTube API and playback

OAuth verification only covers the **account/library** access. The app's audio playback uses yt-dlp to resolve
streams, which is outside the YouTube Data API and may conflict with YouTube's Terms of Service. Verification does
not make that compliant. Review that risk before publishing a public build; this is not legal advice.

## What I can prepare

- Draft privacy policy and terms text (templates in this folder).
- The verification checklist above and the demo-video script.
- The in-app setup wizard and baked-client packaging.
- The test plan and troubleshooting messages.

## What you must do

- Create the Google Cloud project and OAuth client in your account.
- Publish the privacy policy/terms at public URLs.
- Record and upload the demo video.
- Submit and respond to the verification review.
- Accept Google's terms and the YouTube API/ToS risk.
