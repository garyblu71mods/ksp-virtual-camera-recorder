# VirtualCameraRecorder — KSP Mod

A cinematic camera recorder for Kerbal Space Program 1.x.  
Place a free-floating or vessel-anchored virtual camera anywhere in the scene and record to MP4 (H.264).

---

## Requirements

- Kerbal Space Program 1.x (tested on Unity 5 / Mono)
- **FFmpeg** — must be placed in the mod's `Plugins` folder as `ffmpeg.exe`  
  Download: https://ffmpeg.org/download.html → Windows builds (e.g. gyan.dev or BtbN)

---

## Installation

1. Copy the `VirtualCameraRecorder` folder into your KSP `GameData` folder:
   ```
   KerbalSpaceProgram/
   └── GameData/
       └── VirtualCameraRecorder/
           └── Plugins/
               ├── VirtualCameraRecorder.dll
               └── ffmpeg.exe          ← place here!
   ```
2. Launch KSP and load a **Flight** scene.

---

## Opening the Window

Click the **camera icon** in the KSP toolbar (top-right of screen) to show/hide the VCR window.

---

## Camera Modes

| Button | Description |
|--------|-------------|
| **Vessel** | Camera follows the active vessel (offset in vessel-local space) |
| **Free** | Camera floats freely — stays where you left it |
| **Surface** | Camera rotates with the planet surface |
| **Track** | Camera stays in place but always looks at the active vessel |

Use **Snap here** to anchor the camera at its current world position relative to the vessel or surface.

---

## Camera Controls

All controls work inside the **preview window**:

| Input | Action |
|-------|--------|
| **LMB drag** | Rotate (yaw + pitch) |
| **RMB drag** | Pan (up / down / left / right) |
| **MMB drag** | Dolly (forward / back / strafe) |
| **MMB + RMB drag** | Roll |
| **Scroll wheel** | Zoom (FOV) |

---

## Recording

1. Click **● REC** to start recording — a new MP4 file is created immediately.
2. The timer and bitrate indicator show recording is active.
3. Click **■ STOP** to stop — FFmpeg finalises the file automatically.

Recordings are saved to:
```
KerbalSpaceProgram/Recordings/vcr_YYYY-MM-DD_HH-mm-ss.mp4
```

Video format: **1920×1080, 30 fps, H.264, yuv420p** — playable in VLC, Windows Media Player, etc.

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| No video file created | Make sure `ffmpeg.exe` is in `GameData\VirtualCameraRecorder\Plugins\` |
| Black preview | Switch to Flight scene and wait for vessel to load |
| KSP log errors | Check `KSP_Data/output_log.txt` for `[VCR]` entries |
| FFmpeg errors | Check `Recordings/ffmpeg_lastrun.log` |

---

## License

MIT — free to use, modify and redistribute.
