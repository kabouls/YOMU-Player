# Remote playback: Windows, Android, Android TV

The goal is the YouTube Music device model: each signed-in client is a playback device, one device is the active
one, and the others can transfer playback to themselves or send transport commands.

This document is the contract. The Windows app already implements the local endpoint; the phone/TV apps and the
transport are the next step.

## What exists today

`YomuPlayer.Core/PlaybackSession.cs`:

- `PlaybackSnapshot` - the complete, serializable state: device id/name/kind, track metadata, playing/loading,
  position and duration, volume/mute, shuffle, repeat, queue length, timestamp.
- `PlaybackCommand` - a polymorphic record (`System.Text.Json` type discriminators are already configured):
  `play`, `pause`, `toggle`, `next`, `previous`, `seekTo`, `setVolume`, `setShuffle`, `setRepeat`, `playQueueIndex`.
- `IPlaybackEndpoint` - `Snapshot()`, `ApplyAsync(command)`, plus identity (`DeviceId`, `DeviceName`, `Kind`,
  `IsLocal`).
- `LocalPlaybackEndpoint` - binds the interface to `PlayerService` / `PlaybackQueue`. This is the only endpoint the
  Windows app registers today; the Devices button in the player bar lists the endpoints from
  `AppServices.Devices`.

Nothing in the contract knows about WinUI, VLC or HTTP, so a companion app can reuse it as-is.

## Transport proposal

Keep the local network path simple and private; do not require a cloud account for same-LAN control.

1. **Discovery**: mDNS/DNS-SD service `_ytmremote._tcp` with TXT records `id`, `name`, `kind` (`windows` /
   `android` / `androidtv`) and `proto=1`. Android/TV browse for `_ytmremote._tcp`; Windows advertises while the
   setting "Allow device control" is on.
2. **Pairing**: first connection shows a 6-digit code on the receiver; the sender includes it in the handshake.
   Store the paired device's public key/id so reconnects are silent. Only paired devices may issue commands.
3. **Channel**: one WebSocket per device (`wss`, self-signed certificate pinned on first pairing) carrying JSON
   messages:
   - `hello` { deviceId, name, kind, proto, code? }
   - `state` { snapshot }            server -> clients on connect and after changes
   - `command` { command }           client -> active device
   - `queue` { entries[] }           only when a client asks for the full queue
   - `transfer` { targetId }         active device -> hub
4. **Single active device**: the server keeps one "active playback device". Commands go to it; other devices show
   the snapshot read-only ("Playing on Living Room TV").

## State and command semantics

- The active device is the source of truth. After every command it broadcasts a fresh `PlaybackSnapshot` (throttled
  to ~4 Hz for position updates, immediately for state changes).
- `seekTo` is absolute; senders should not compute relative seeks from a stale snapshot.
- `playQueueIndex` uses **play order**, not the underlying track index, so shuffle does not change the meaning.
- A sender must ignore its own optimistic state once a snapshot arrives; snapshots win.

## Transfer ("move playback here")

The receiver needs more than a snapshot because the queue lives on the sender. Proposed transfer message:

```json
{
  "type": "transferRequest",
  "targetId": "tv-1",
  "state": { "snapshot": { } },
  "queue": [
    { "videoId": "…", "title": "…", "artists": "…", "album": "…", "durationMs": 320000, "thumbnailUrl": "…" }
  ],
  "currentIndex": 3,
  "startPositionMs": 91000
}
```

Rules:

1. The sender pauses after sending, keeps ownership until the target replies `transferAccepted`.
2. The target resolves the current `videoId` itself (each device needs yt-dlp / an extractor), seeks to
   `startPositionMs`, resumes, then broadcasts its snapshot.
3. If the target fails to start within ~10 s, the sender resumes locally and reports "couldn't move playback".
4. Queue items are plain metadata; no stream URLs cross the wire, so URLs and cookies never leak between devices.
5. Volume and shuffle/repeat are **per device** preferences and are not transferred by default; position and queue
   are.

## Windows implementation notes

- `AppServices.Devices` is the registry the UI reads; add remote endpoints there and the Devices flyout picks them up.
- The player page and lite mode both read the same `PlayerService`, so commands applied to `LocalPlaybackEndpoint`
  are reflected in every window state automatically.
- Add a setting `AllowDeviceControl` (default off) before opening any socket; advertise only while it is on.
- The app already runs one named-pipe instance server for `yomu://` links; keep remote control on a separate
  channel so a malformed link can never issue playback commands.

## Android / TV notes

- Reuse `PlaybackSnapshot`, `PlaybackCommand`, `PlaybackQueue` semantics. The Android app can keep a local
  `PlayerService`-like player and switch between "local" and "remote" endpoints with the same UI.
- TV: D-pad focus order should follow the YouTube Music TV layout - artwork, title, transport, Up next; device
  selection lives in the top bar. Leanback is optional; Compose for TV is enough.
- Phone: MediaSession/MediaBrowserService for lock screen and Android Auto; the remote endpoint implements the same
  command surface so the UI code is shared.

## Open questions

- Whether to support a cloud relay for devices on different networks. If added, it should carry the same messages
  end-to-end encrypted, with the relay unable to play audio.
- Whether transfer should also carry the upcoming radio seed so the receiving device can extend the queue when it
  ends (today the queue is finite on the receiver).
- Lyrics are not part of playback state; each client fetches them independently.
