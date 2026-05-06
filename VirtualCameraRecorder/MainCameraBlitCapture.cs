using UnityEngine;

namespace VirtualCameraRecorder
{
    internal sealed class MainCameraBlitCapture : MonoBehaviour
    {
        public RenderTexture Target;

        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            Graphics.Blit(src, dest);
            if (Target != null)
                Graphics.Blit(src, Target);
        }
    }
}
