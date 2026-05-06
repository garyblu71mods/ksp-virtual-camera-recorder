# Copilot Instructions

## Project Guidelines
- After every build, always copy the output DLL to the KSP game folder: `Copy-Item "VirtualCameraRecorder\bin\Release\VirtualCameraRecorder.dll" "C:\Program Files\Epic Games\KerbalSpaceProgram\KerbalSpaceProgram\GameData\VirtualCameraRecorder\Plugins\VirtualCameraRecorder.dll" -Force`
- In this project UI, remove Snap here, and ensure Aim keeps the current camera mode while reframing the vessel; recordings must include TUFX visuals.
- Always finalize recordings to the end when changing scenes (e.g., Space Center) or switching vehicles, ensuring no loss of file closure.
- FFmpeg must never be hard killed; always perform a soft close. When switching vehicles during rendering, recording must continue without interruption.
- In VirtualCameraRecorder, the Hide parts highlight should not be user-configurable in the UI, and part highlighting should always stay suppressed/disabled.