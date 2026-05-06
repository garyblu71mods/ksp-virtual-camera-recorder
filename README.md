# VirtualCameraRecorder — KSP Mod

A cinematic camera recorder for Kerbal Space Program 1.x.

---

## Requirements

- Kerbal Space Program 1.x (Unity/Mono runtime)

---

## Installation

1. Extract the release ZIP into your KSP folder so files end up here:
   ```
   KerbalSpaceProgram/
   └── GameData/
       └── VirtualCameraRecorder/
           └── Plugins/
               ├── VirtualCameraRecorder.dll
               └── ffmpeg.exe
   ```
2. Launch KSP and enter a **Flight** scene.

---

## Open / Close Window

Click the **camera icon** in the stock KSP toolbar (top-right).

---

## Camera Modes

| Mode | Behavior |
|------|----------|
| **Vessel** | Camera stays attached to vessel offset and keeps vessel in frame |
| **Free** | Camera position is fully free |
| **Surface** | Camera position follows planet-surface anchor |
| **Track** | Camera stays in place and smoothly tracks active vessel |

Buttons:
- **Snap here**: re-anchor current position for Vessel/Surface modes
- **Aim**: quickly reset camera near active vessel (same framing style as startup)

---

## Controls

### Analog controls (in window)
- **Look joystick** (LMB drag): smooth look/rotation
- **Pan joystick** (LMB drag): smooth pan
- **Dolly slider**: fast forward/back movement

### Preview mouse controls
- **LMB drag**: rotate
- **RMB drag**: pan
- **MMB drag**: dolly
- **MMB + RMB drag**: roll
- **Scroll**: zoom (FOV)

---

## Recording

1. Click **● REC** to start.
2. Click **■ STOP** to finish and finalize MP4.

Output path:
```
KerbalSpaceProgram/Recordings/vcr_YYYY-MM-DD_HH-mm-ss.mp4
```

Current encoder settings:
- Resolution: **1920×1080**
- FPS: **30**
- Codec: **H.264 (libx264)**
- x264 quality: **preset slow, CRF 17, profile high**
- Pixel format: **yuv420p**

The recorder keeps real-time duration (no speed-up): when the game renders fewer frames than target FPS, frames are duplicated to preserve correct timeline length.

---

## Troubleshooting

| Problem | What to check |
|---------|----------------|
| No output file | Verify ZIP was extracted to `GameData\VirtualCameraRecorder\Plugins\` |
| UI opens but no image | Make sure you are in Flight scene and vessel is loaded |
| Build/playback issues | Check `Recordings/ffmpeg_lastrun.log` |
| Runtime issues | Check `KSP_Data/output_log.txt` for `[VCR]` logs |

---

## License

MIT
