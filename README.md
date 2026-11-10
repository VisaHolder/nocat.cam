# nocat.cam

A small tray app for the **EMEET PIXY** webcam: your own hand gestures, keybinds, nine saved camera
positions with smooth glides, lossless zoom, background blur / replace, auto-sleep, and the camera's
picture settings. All of it reaches Discord, OBS and any other app through its own **nocat.cam**
virtual camera. EMEET Studio isn't needed.

Windows 10/11 x64, .NET 10.

## What it does

- **Gestures** (Google MediaPipe): closed fist, thumbs up/down, peace sign, pointing up and rock-on can each
  trigger any action, with their own hold time. Open palm stays the camera's own tracking toggle.
- **Keybinds** for everything (any combo; defaults: `Alt+1`–`Alt+9` go to a position, `Alt+Shift+1`–`9`
  save one, `Alt+0` sleep/wake). They use Windows' own hotkeys, so nothing watches your keyboard.
- **Positions**: nine slots that remember aim *and* zoom, with a smooth eased glide between them.
- **Zoom**: crops the 4K picture, so it stays sharp up to about 2×.
- **Background**: blur, or any picture, cut out around you.
- **Sleep**: *privacy* (camera turns away and is released, so its in-use light goes off) or *soft*
  (camera stays on, apps see a "camera asleep" card, and a gesture can wake it). Optional auto-sleep
  when nobody is in view or no app is using nocat.cam, and auto-wake when an app starts using it.
- **Picture**: brightness, contrast, white balance, exposure, focus and more, plus a *Smooth 30 fps* switch
  (dim rooms drop auto exposure to 20 fps).
- **Light on resources**: pictures stay in the camera's own NV12 format end to end (Windows decodes the
  camera's MJPEG in hardware), and when no app shows nocat.cam it only sends 2 pictures a second.
  Measured on a Ryzen 9800X3D: about 0.5% CPU idle, ~2.5% while Discord shows you at 1080p.

## Setup

1. Install [OBS Studio](https://obsproject.com/) once. nocat.cam uses its virtual camera driver. OBS
   itself doesn't need to run, and you shouldn't start OBS's own virtual camera while nocat.cam is on.
2. Run `nocat.cam.exe`. It goes to the tray; click the icon for settings.
3. Optional: right-click `tools\rename-vcam.ps1` → *Run with PowerShell* as admin, so apps list the camera as
   **nocat.cam** instead of "OBS Virtual Camera". `-Undo` puts it back.
4. In Discord (or anywhere), pick **nocat.cam** as your camera.

Settings live in `%APPDATA%\nocat.cam\config.json`, the log next to it. To move settings, drop a
`config.json` next to the exe renamed to `import-config.json`; it's imported on the next start.

## Building

```powershell
powershell -ExecutionPolicy Bypass -File tools\get-mediapipe.ps1   # fetches libmediapipe.dll (68 MB) from PyPI
dotnet build -c Release
dotnet publish -c Release -o publish
```

`nocat.cam.exe --selftest result.txt` checks the camera, USB control, capture speed, AI models, picture
controls and virtual camera without opening any windows.

## How the PIXY is controlled

EMEET doesn't document it. The commands were worked out by logging what EMEET Studio sends over the
camera's HID interface (VID 328F, PID 00C0, report 0x09):

| what | bytes |
|---|---|
| set mode | `09 01 01 00  00 01 00 01  <mode>` (0 manual, 1 tracking, 2 privacy) |
| get mode | `09 01 01 01` |
| move | `09 63 01 00  00 05 00 05  <axis> <float32 degrees>` (axis 1 pan, 2 tilt) |
| where is it | `09 63 01 01  00 01 00 01  <axis>` → axis, target, actual |

## Credits

- [MediaPipe](https://github.com/google-ai-edge/mediapipe) (Google, Apache 2.0): `libmediapipe.dll`, the
  gesture recogniser and selfie segmenter models in `models\`.
- [HidSharp](https://www.zer7.com/software/hidsharp) (Apache 2.0), [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT),
  [Microsoft WebView2](https://developer.microsoft.com/microsoft-edge/webview2/).
- The virtual camera driver is [OBS Studio](https://obsproject.com/)'s (GPL 2.0), used as installed; nocat.cam only writes pictures to it.
