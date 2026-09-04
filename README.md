# PairDrop Native for Windows — v0.2

A tray-only Windows client that speaks PairDrop's WebSocket fallback protocol directly.

There is **no embedded browser and no WebView2**.

## What it does

- Runs only in the Windows system tray
- Connects to your existing self-hosted PairDrop server
- Appears as a normal PairDrop device
- Automatically accepts incoming files
- Streams incoming files directly to disk
- Automatically copies received text to the Windows clipboard
- Native Windows notifications
- Send clipboard text to any currently visible PairDrop device
- Send files to any currently visible PairDrop device
- Starts with Windows
- Remembers the PairDrop peer identity between launches

## Server requirement

Your PairDrop server must have:

```text
WS_FALLBACK=true
```

This native client advertises:

```text
webrtc_supported=false
```

so PairDrop browsers use the existing WSPeer fallback protocol to communicate with it.

Because it uses WebSocket fallback, file data passes through your PairDrop server rather than a direct WebRTC P2P data channel.

## Build

Put the files in:

```text
C:\pairdrop-native
```

Then:

```powershell
cd C:\pairdrop-native
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

If the .NET 8 SDK is missing:

```powershell
winget install Microsoft.DotNet.SDK.8
```

Finished executable:

```text
C:\pairdrop-native\publish\PairDropNative.exe
```

## First launch

The settings window asks for:

- PairDrop URL, for example `https://drop.example.com`
- Download folder
- Auto accept
- Auto-copy received text
- Windows notifications
- Start with Windows

The app then disappears into the tray.

## Tray menu

- Connection status
- Visible devices
- Send clipboard to → device
- Send files to → device
- Open downloads
- Open PairDrop website
- Settings
- Reconnect
- Quit

## Notes

This implementation follows PairDrop's current open-source WebSocket fallback protocol:
- `/server?webrtc_supported=false`
- `join-ip-room`
- `signal`
- `request`
- `files-transfer-response`
- `header`
- `ws-chunk`
- `partition`
- `partition-received`
- `file-transfer-complete`
- `text`
- `message-transfer-complete`

It is intentionally independent of PairDrop's HTML/CSS UI.


## v0.2 fixes

- Transfer completion now mirrors PairDrop's receiver behaviour more closely.
- Incoming transfers send PairDrop `progress` updates, including final 100%, so the sending phone/browser leaves its transfer state.
- `partition-received` now echoes the partition offset.
- Zero-byte files complete correctly.
- Text sends now wait for PairDrop's `message-transfer-complete` acknowledgement.
- Settings window enlarged to 940 × 500 client area.
- New **Play a sound when text / files arrive** setting (enabled by default).
- Clipboard notifications now say **Clipboard received**.
- Image notifications identify incoming images separately from generic files.


## v0.3 UI refresh

- Settings window updated to a dark theme
- More modern, cleaner spacing and typography
- Larger settings window
- Wider URL and download fields
- Better styled Save / Cancel / Browse buttons


## v0.4 settings popup

- Actual WinForms settings code redesigned (not an image/mock-up)
- Dark Windows title bar on supported Windows 10/11 builds
- Rounded dark input fields and receiving card
- Custom dark checkboxes with blue accent
- Modern rounded buttons
- Larger 1000 × 640 settings window
- DPI-aware layout to stop text clipping at 125% / 150% Windows scaling
