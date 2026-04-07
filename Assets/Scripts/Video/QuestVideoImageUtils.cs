using UnityEngine;

namespace Quest3VideoPlayer
{
    /// <summary>
    /// Utility helpers for binding Unity textures to display targets.
    /// </summary>
    public static class QuestVideoImageUtils
    {
        /// <summary>
        /// Assigns the texture to the stereo display presenter.
        /// </summary>
        public static void BindTexture(
            Texture texture,
            //Renderer renderer,
            //UnityEngine.UI.RawImage uiImage,
            //UnityEngine.UI.RawImage uiImage2,
            bool flipHorizontal,
            bool flipVertical,
            int actualWidth,
            int actualHeight,
            int desiredWidth,
            int desiredHeight)
        {
            if (texture == null || UIVideoSbsPlayer.instance == null)
            {
                return;
            }

            UIVideoSbsPlayer.instance.StaticTexture = texture;
            UIVideoSbsPlayer.instance.FlipTextureHorizontally = flipHorizontal;
            UIVideoSbsPlayer.instance.FlipTextureVertically = flipVertical;
        }
    }
}
