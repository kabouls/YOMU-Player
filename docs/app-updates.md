# YOMU Player updates — app-side setup

Repository: https://github.com/kabouls/YOMU-Player

## Build channel

The app reads `channel.txt` next to the exe:

```text
nightly
```

Missing/other values mean **stable**. Nightly builds use:

- Product: `YOMU Player Nightly`
- App ID: `YOMU.Player.Nightly`
- Data: `%LOCALAPPDATA%\YomuPlayer Nightly`
- Protocol: `yomu-nightly`
- Separate single-instance pipe

## Portable marker

A portable zip contains `portable.marker` at its root. Then:

- Data is stored in `data` next to the exe when writable (otherwise LocalAppData)
- No Start Menu shortcut or protocol registration is created
- The portable single-instance pipe is separate from the installed app
- Google login still uses Windows DPAPI (current Windows user); copying to another PC requires signing in again

## Update source

Place `update-source.json` next to the exe:

```json
{
  "manifestUrl": "https://kabouls.github.io/YOMU-Player/stable.json"
}
```

Nightly packages use `https://kabouls.github.io/YOMU-Player/nightly.json`.

For local testing you can instead set the `YOMU_UPDATE_MANIFEST` environment variable to a local JSON
file path or local HTTP URL.

## Manifest

```json
{
  "channel": "stable",
  "version": "1.0.1",
  "published": "2026-11-03T14:30:00Z",
  "notes": [
    "What changed in this release"
  ],
  "setup": {
    "url": "https://github.com/kabouls/YOMU-Player/releases/download/v1.0.1/YOMU-Player-Setup-1.0.1.exe",
    "sha256": "..."
  },
  "portable": {
    "url": "https://github.com/kabouls/YOMU-Player/releases/download/v1.0.1/YOMU-Player-Portable-1.0.1.zip",
    "sha256": "..."
  }
}
```

The app selects `setup` or `portable` from the local `portable.marker`, downloads to the data folder,
verifies SHA-256, then starts the installer or the portable replacement helper. Version downgrades and
wrong-channel manifests are rejected.

## GitHub repository

- Repository: https://github.com/kabouls/YOMU-Player
- Git remote: `https://github.com/kabouls/YOMU-Player.git`
- Default branch: `main`
- Nightly branch: `nightly`
- Releases: https://github.com/kabouls/YOMU-Player/releases
- Update manifests:
  - https://kabouls.github.io/YOMU-Player/stable.json
  - https://kabouls.github.io/YOMU-Player/nightly.json
- GitHub Pages must use **GitHub Actions** as its source.

## Changelog

`changelog.json` is bundled next to the exe:

```json
[
  {
    "version": "1.0.1",
    "date": "2026-11-03",
    "channel": "stable",
    "notes": ["A note shown in Profile and after the update"]
  }
]
```

- Profile → Changelog shows every entry.
- The first launch after a version change shows the entry once.
- A fresh install records the current version without showing an update dialog.
