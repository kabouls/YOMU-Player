# Privacy Policy for [APP NAME]

**Last updated:** [DATE]

[APP NAME] ("the app") is a desktop client for YouTube Music. This policy explains what data the app accesses,
where it is stored, and how you can delete it. It is a template: fill in the bracketed fields and have it reviewed
before publishing.

## 1. Who we are

[APP NAME] is developed and published by [YOUR NAME / ORGANIZATION]. Contact: [SUPPORT EMAIL]. Source code and
releases: [GITHUB URL].

The app is not affiliated with, endorsed by, or sponsored by YouTube or Google.

## 2. What the app accesses

When you sign in with Google, the app requests these scopes:

- `openid`, `email`, `profile` — your basic Google account profile (name, email address, profile picture) so the app
  can show who is signed in.
- `https://www.googleapis.com/auth/youtube` — read your YouTube Music library (liked music, saved playlists and
  related library data) so the app can display it, and add tracks to a playlist **only when you explicitly choose
  Save or New playlist**.

The app does **not** access your Google password, Gmail, Google Drive, contacts, calendars or any other Google
service. The password is entered only in Google's own browser page; the app never sees it.

The app also contacts YouTube Music's public endpoints to browse music and resolve audio streams. Audio stream
resolution is performed locally by the third-party open-source tool yt-dlp; the app does not pass your Google
account token to yt-dlp.

## 3. How data is stored

- The OAuth **refresh token** and the OAuth **client configuration** are stored on your device in
  `%LOCALAPPDATA%\YomuPlayer\auth.bin`, encrypted with Windows DPAPI for your Windows user account.
- A short-lived **access token** is kept in memory and is not written to disk.
- A local diagnostic log may be written to `%LOCALAPPDATA%\YomuPlayer\log.txt`. It is not uploaded anywhere.
- **The app has no backend server.** It does not collect telemetry, analytics or advertising identifiers, and it
  does not sell or share your data.

## 4. Third parties

- **Google / YouTube** process your account data under their own privacy policies when you sign in and when the app
  requests your library. See Google's Privacy Policy and the YouTube Terms of Service.
- **yt-dlp** is used locally to resolve audio streams from YouTube. It contacts YouTube directly; it does not
  receive your Google account token from the app.

## 5. Retention and deletion

Your data stays on your device until you delete it. To remove it:

1. In the app, choose **Sign out** (clears the tokens) and/or **Use different Google app credentials**.
2. Delete `%LOCALAPPDATA%\YomuPlayer\auth.bin` (and optionally the `YomuPlayer` folder) to remove all local data.
3. Revoke the app's access to your Google account at
   [myaccount.google.com/permissions](https://myaccount.google.com/permissions).

Uninstalling the app does not automatically delete `%LOCALAPPDATA%\YomuPlayer`; delete it manually if desired.

## 6. Children

The app is not directed to children under the age required by Google's terms in your country. Do not use it with a
child's Google account.

## 7. Changes

This policy may be updated when the app changes. The "Last updated" date at the top will change; continued use after
an update means you accept the revised policy.

## 8. Contact

Questions or deletion requests: [SUPPORT EMAIL].

---

*Template note: Google's OAuth verification expects a public privacy policy that clearly describes the data accessed
and how it is used/stored. Review this with a qualified professional before publishing; this is not legal advice.*
