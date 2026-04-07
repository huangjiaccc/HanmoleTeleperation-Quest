#if UNITY_ANDROID
using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Quest3VideoPlayer
{
    /// <summary>
    /// Minimal harness that exercises the questvulkanext native plugin.  It creates a Vulkan-backed
    /// external texture, wraps it in a Texture2D and animates its colour so we can validate the
    /// zero-copy rendering path before wiring it to MediaCodec output.
    /// </summary>
    public class VulkanExternalTexturePrototype : MonoBehaviour
    {
        [SerializeField] private RawImage previewTarget;
        [SerializeField] private Vector2Int textureSize = new Vector2Int(1024, 1024);

        private IntPtr nativeHandle = IntPtr.Zero;
        private IntPtr renderEventFunc = IntPtr.Zero;
        private Texture2D externalTexture;
        private float hue;

        private enum RenderEventId
        {
            CreateTexture = 0x5151,
            BindTexture = 0x5152,
            DestroyTexture = 0x5153,
        }

        [DllImport("questvulkanext")]
        private static extern IntPtr QuestVulkan_CreateTestTexture(int width, int height);

        [DllImport("questvulkanext")]
        private static extern void QuestVulkan_DestroyTexture(IntPtr handle);

        [DllImport("questvulkanext")]
        private static extern void QuestVulkan_RequestDestroyTexture(IntPtr handle);

        [DllImport("questvulkanext")]
        private static extern bool QuestVulkan_WaitForTexture(IntPtr handle, uint timeoutMs);

        [DllImport("questvulkanext")]
        private static extern void QuestVulkan_SetUnityTexture(IntPtr handle, IntPtr unityTexturePtr);

        [DllImport("questvulkanext")]
        private static extern void QuestVulkan_UpdateTestTexture(
            IntPtr handle, float r, float g, float b, float a);

        [DllImport("questvulkanext")]
        private static extern IntPtr QuestVulkan_GetRenderEventFunc();

        private void Start()
        {
#if UNITY_EDITOR
            Debug.LogWarning("[VulkanExternalTexturePrototype] External textures are only available on device.");
            enabled = false;
            return;
#else
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
            {
                Debug.LogWarning("[VulkanExternalTexturePrototype] Vulkan renderer required.");
                enabled = false;
                return;
            }

            renderEventFunc = QuestVulkan_GetRenderEventFunc();
            if (renderEventFunc == IntPtr.Zero)
            {
                Debug.LogError("[VulkanExternalTexturePrototype] Failed to resolve render event function.");
                enabled = false;
                return;
            }

            nativeHandle = QuestVulkan_CreateTestTexture(textureSize.x, textureSize.y);
            if (nativeHandle == IntPtr.Zero)
            {
                Debug.LogError("[VulkanExternalTexturePrototype] Failed to create native texture.");
                enabled = false;
                return;
            }

            if (!IssueRenderEvent(RenderEventId.CreateTexture))
            {
                Debug.LogError("[VulkanExternalTexturePrototype] Render-thread creation failed.");
                QuestVulkan_DestroyTexture(nativeHandle);
                nativeHandle = IntPtr.Zero;
                enabled = false;
                return;
            }

            externalTexture = new Texture2D(
                textureSize.x,
                textureSize.y,
                TextureFormat.RGBA32,
                false,
                true);

            externalTexture.SetPixel(0, 0, Color.clear);
            externalTexture.Apply(false, false);

            IntPtr unityTexturePtr = externalTexture.GetNativeTexturePtr();
            if (unityTexturePtr == IntPtr.Zero)
            {
                Debug.LogError("[VulkanExternalTexturePrototype] Failed to acquire Unity texture pointer.");
                QuestVulkan_DestroyTexture(nativeHandle);
                nativeHandle = IntPtr.Zero;
                enabled = false;
                return;
            }

            Debug.Log($"[VulkanExternalTexturePrototype] Unity texture ptr={unityTexturePtr}");
            QuestVulkan_SetUnityTexture(nativeHandle, unityTexturePtr);
            if (!IssueRenderEvent(RenderEventId.BindTexture))
            {
                Debug.LogError("[VulkanExternalTexturePrototype] Failed to bind Unity texture.");
                QuestVulkan_DestroyTexture(nativeHandle);
                nativeHandle = IntPtr.Zero;
                enabled = false;
                return;
            }

            if (previewTarget != null)
            {
                previewTarget.texture = externalTexture;
            }
#endif
        }

        private void Update()
        {
#if !UNITY_EDITOR
            if (nativeHandle == IntPtr.Zero || externalTexture == null)
            {
                return;
            }

            hue = (hue + Time.unscaledDeltaTime * 0.15f) % 1.0f;
            Color color = Color.HSVToRGB(hue, 1f, 1f);
            QuestVulkan_UpdateTestTexture(nativeHandle, color.r, color.g, color.b, 1f);
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_EDITOR
            if (previewTarget != null && previewTarget.texture == externalTexture)
            {
                previewTarget.texture = null;
            }

            if (nativeHandle != IntPtr.Zero)
            {
                if (renderEventFunc != IntPtr.Zero)
                {
                    QuestVulkan_RequestDestroyTexture(nativeHandle);
                    if (!IssueRenderEvent(RenderEventId.DestroyTexture))
                    {
                        Debug.LogWarning("[VulkanExternalTexturePrototype] Timed out waiting for GPU destroy.");
                    }
                }

                QuestVulkan_DestroyTexture(nativeHandle);
                nativeHandle = IntPtr.Zero;
            }

            if (externalTexture != null)
            {
                Destroy(externalTexture);
                externalTexture = null;
            }
#endif
        }

        private bool IssueRenderEvent(RenderEventId renderEvent)
        {
            if (renderEventFunc == IntPtr.Zero || nativeHandle == IntPtr.Zero)
            {
                return false;
            }

            CommandBuffer cmd = CommandBufferPool.Get("QuestVulkanExtEvent");
            cmd.IssuePluginEventAndData(renderEventFunc, (int)renderEvent, nativeHandle);
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            return QuestVulkan_WaitForTexture(nativeHandle, 0);
        }
    }
}
#else
using UnityEngine;

namespace Quest3VideoPlayer
{
    public class VulkanExternalTexturePrototype : MonoBehaviour
    {
        private void Awake()
        {
            Debug.LogWarning("[VulkanExternalTexturePrototype] Only available on Android builds.");
            enabled = false;
        }
    }
}
#endif
