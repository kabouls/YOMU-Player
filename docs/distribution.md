# Building and publishing a release

The goal: someone downloads the zip, runs the app, presses **Sign in with Google**, and uses their own Google
account. They should never create a Google Cloud client.

## One-time prerequisites (the app owner)

1. Create a **Desktop app** OAuth client in Google Cloud and enable **YouTube Data API v3**.
2. If you want arbitrary users (not just test users), publish and verify the OAuth app. See
   [google-oauth-verification.md](google-oauth-verification.md).
3. Prepare a public **privacy policy** and **terms** URL, app logo and support email for the consent screen.

## Build a release with the client baked in

1. Copy the example and fill in your own client values:

   ```powershell
   Copy-Item google-client.example.json google-client.json
   # edit google-client.json: clientId + clientSecret
   ```

   `google-client.json` is in `.gitignore`. **Do not commit it.** The release zip will contain it; that is expected
   for an installed desktop app (Google treats installed-app client secrets as non-confidential).

2. Build the zip:

   ```powershell
   powershell -File tools\pack-release.ps1 -Version 1.0.0
   ```

   The script:
   - builds the desktop project in Release (self-contained),
   - copies the output to `dist\YOMU-Player-1.0.0-win-x64\`,
   - includes `google-client.json` if it exists,
   - bundles `yt-dlp.exe` from `%LOCALAPPDATA%\YomuPlayer\yt-dlp`, `tools\`, or PATH when available,
   - creates `dist\YOMU-Player-1.0.0-win-x64.zip` and a `.sha256` file.

3. Test on a **clean Windows profile** (or after deleting `%LOCALAPPDATA%\YomuPlayer`):

   - unzip the release,
   - run `YomuPlayer.exe`,
   - Library → Sign in with Google,
   - browser → choose account → approve → back,
   - verify Library and sidebar load, then restart and verify silent sign-in.

   With the shipped client, the setup dialog must not appear.

## GitHub release

1. Create a tag and a release (for example `v1.0.0`).
2. Upload:
   - `YOMU-Player-1.0.0-win-x64.zip`
   - `YOMU-Player-1.0.0-win-x64.zip.sha256`
3. Release notes: requirements (Windows 10 1809+/11 x64), that the app is not affiliated with YouTube/Google, and
   how to sign in.
4. Do **not** commit `google-client.json` to the repository. The client lives in the release asset only.
5. Optional: sign the exe with a code-signing certificate. Unsigned releases trigger Windows SmartScreen warnings.

## What users need

- Windows 10 1809+ or Windows 11, x64.
- A Google account. **No Google Cloud project, no client ID, no test-user setup** when the release includes
  `google-client.json` and the OAuth app is published.
- If the OAuth app is still in **Testing**, they must be added as a Test user (max 100), see the unverified warning,
  and sign in again after 7 days.
- yt-dlp is bundled in the release zip when the pack script found it; otherwise the app looks on PATH.

## Security notes

- The refresh token and the client configuration are stored with Windows DPAPI in
  `%LOCALAPPDATA%\YomuPlayer\auth.bin`.
- Account library calls (Liked Music, playlists, saving a queue) use the official YouTube Data API quota of **your
  Cloud project** (10,000 units/day). Every user of the release shares that quota; heavy public use can exhaust it.
  Monitor **APIs & Services → YouTube Data API v3 → Quotas** and consider per-user clients or a backend if the app
  grows. Verification does not increase quota.
- The app has no backend. No account data leaves the machine except requests to Google/YouTube.
- A desktop app's client secret can be extracted from any build. That is normal for installed apps; PKCE protects
  the flow. If the client is abused, rotate it in Google Cloud and ship an update.
- Rotating the client does not change the copy already stored in an existing user's `auth.bin`. To pick up a new
  client, they can use **Use different Google app credentials (advanced)** or delete `auth.bin` and sign in again.
  Keep this in the release notes when you rotate.
- Do not put a client secret in a public source file and assume it is secret. Treat `google-client.json` as a
  **release asset**, not source code.

## Optional: GitHub Actions

You can generate `google-client.json` from repository secrets during a release build, so the client is never stored
in the repo. In **Settings → Secrets and variables → Actions**, add `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET`,
then use a Windows runner:

```yaml
name: release
on:
  push:
    tags: ["v*"]

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "8.0.x"
      - name: Write OAuth client
        shell: pwsh
        run: |
          @{
            clientId = "${{ secrets.GOOGLE_CLIENT_ID }}"
            clientSecret = "${{ secrets.GOOGLE_CLIENT_SECRET }}"
          } | ConvertTo-Json | Set-Content google-client.json -Encoding utf8
      - name: Build release
        shell: pwsh
        run: ./tools/pack-release.ps1 -Version ${{ github.ref_name }}
      - uses: actions/upload-artifact@v4
        with:
          name: YomuPlayer
          path: dist/*
```

This workflow is a starting point; validate it on your repository before relying on it. It also needs yt-dlp:
download it or use `winget install yt-dlp` in a preceding step so the pack script can bundle it.

## License and third-party tools

- Choose a license for your source (MIT, GPL, etc.). The app currently has no LICENSE file.
- yt-dlp is a separate open-source project; check its license before redistributing a bundled binary.
- Playback by extracting streams with yt-dlp may conflict with YouTube's Terms of Service. Review that before a
  public release; verification of the OAuth app does not change this.
