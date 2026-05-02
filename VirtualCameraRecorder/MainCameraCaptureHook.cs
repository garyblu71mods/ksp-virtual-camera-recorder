using UnityEngine;

namespace VirtualCameraRecorder
{
    internal sealed class MainCameraCaptureHook : MonoBehaviour
    {
        public RenderTexture TargetTexture;

        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            Graphics.Blit(src, dest);
            if (TargetTexture != null)
                Graphics.Blit(src, TargetTexture);
        }
    }
}
