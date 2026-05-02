// VirtualCameraRecorder – KSP1 mod
// Adds a secondary Unity camera that renders to a RenderTexture, shows a
// live preview inside an IMGUI window, and streams raw RGB24 frames over a
// NamedPipe to an external FFmpeg process for video encoding.
//
// Build requirements:
//   Add the following DLLs from <KSP_DIR>\KSP_x64_Data\Managed\ as references
//   (Copy Local = False):
//     Assembly-CSharp.dll
//     UnityEngine.CoreModule.dll
//     UnityEngine.IMGUIModule.dll
//     UnityEngine.RenderingModule.dll
//
// Deploy:
//   Copy VirtualCameraRecorder.dll to
//     <KSP_DIR>\GameData\VirtualCameraRecorder\Plugins\
//
// Usage:
//   Start KSP, load a flight scene.  The preview window appears top-right.
//   Drag inside the preview to rotate the virtual camera.
//   FFmpeg must be on PATH; it is launched automatically and writes output.mp4
//   to the KSP working directory.

