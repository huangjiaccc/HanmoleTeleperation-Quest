using System;
using System.Runtime.InteropServices;
using UnityEngine;


namespace Quest3VideoPlayer
{
    internal static class QuestVulkanExt
    {
        public const int RenderEventImportHardwareBuffer = 0x5154;
        public const int RenderEventDestroyTexture = 0x5153;

        [DllImport("unity_vulkan_hwbuffer")]
        public static extern IntPtr QuestVulkan_GetRenderEventFunc();

        [DllImport("unity_vulkan_hwbuffer")]
        public static extern IntPtr QuestVulkan_CreateStreamTexture(int width, int height);

        [DllImport("unity_vulkan_hwbuffer")]
        public static extern void QuestVulkan_AssignUnityTexture(IntPtr handle, IntPtr unityTexturePtr);

        [DllImport("unity_vulkan_hwbuffer", EntryPoint = "QuestVulkan_SetYcbcrOverride2")]
        private static extern void QuestVulkan_SetYcbcrOverride2(int model, int range, int swizzleMode, int xChromaOffset, int yChromaOffset);

        public static bool TrySetYcbcrOverride(int model, int range, int swizzleMode, int xChromaOffset, int yChromaOffset)
        {
            try
            {
                QuestVulkan_SetYcbcrOverride2(model, range, swizzleMode, xChromaOffset, yChromaOffset);
                return true;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("unity_vulkan_hwbuffer", EntryPoint = "QuestVulkan_SetManualYuvParams")]
        private static extern void QuestVulkan_SetManualYuvParams(int enabled, int swapUv, int debugMode);

        [DllImport("unity_vulkan_hwbuffer", EntryPoint = "QuestVulkan_SetManualYuvParams2")]
        private static extern void QuestVulkan_SetManualYuvParams2(int enabled, int swapUv, int debugMode, int inputMode);

        [DllImport("unity_vulkan_hwbuffer", EntryPoint = "QuestVulkan_SetManualYuvParams3")]
        private static extern void QuestVulkan_SetManualYuvParams3(int enabled, int swapUv, int invertU, int invertV, int channelOrder, int debugMode, int inputMode);

        public static bool TrySetManualYuvParams(int enabled, int swapUv, int invertU, int invertV, int channelOrder, int debugMode, int inputMode)
        {
            try
            {
                QuestVulkan_SetManualYuvParams3(enabled, swapUv, invertU, invertV, channelOrder, debugMode, inputMode);
                return true;
            }
            catch
            {
                try
                {
                    QuestVulkan_SetManualYuvParams2(enabled, swapUv, debugMode, inputMode);
                    return true;
                }
                catch
                {
                    try
                    {
                        QuestVulkan_SetManualYuvParams(enabled, swapUv, debugMode);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        [DllImport("unity_vulkan_hwbuffer")]
        public static extern void QuestVulkan_SetHardwareBuffer(IntPtr handle, IntPtr hardwareBufferPtr, int width, int height);

        [DllImport("unity_vulkan_hwbuffer", EntryPoint = "QuestVulkan_SetHardwareBufferWithFence")]
        public static extern void QuestVulkan_SetHardwareBufferWithFence(IntPtr handle, IntPtr hardwareBufferPtr, int width, int height, int fenceFd);

        [DllImport("unity_vulkan_hwbuffer")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool QuestVulkan_WaitForTexture(IntPtr handle, uint timeoutMs);

        [DllImport("unity_vulkan_hwbuffer")]
        public static extern int QuestVulkan_GetTextureOperationStatus(IntPtr handle);

        [DllImport("unity_vulkan_hwbuffer")]
        public static extern void QuestVulkan_RequestDestroyTexture(IntPtr handle);

        [DllImport("unity_vulkan_hwbuffer")]
        public static extern void QuestVulkan_DestroyTexture(IntPtr handle);

        [DllImport("unity_vulkan_hwbuffer")]
        public static extern void QuestVulkan_ReleaseAHardwareBuffer(IntPtr hardwareBufferPtr);

        [DllImport("unity_vulkan_hwbuffer", EntryPoint = "QuestVulkan_CloseFenceFd")]
        public static extern void QuestVulkan_CloseFenceFd(int fenceFd);

        [DllImport("unity_vulkan_hwbuffer")]
        private static extern void QuestVulkan_SetColorTransform(
            float mulR,
            float mulG,
            float mulB,
            float mulA,
            float addR,
            float addG,
            float addB,
            float addA);

        public static void QuestVulkan_SetColorTransform(Vector4 mul, Vector4 add)
        {
            QuestVulkan_SetColorTransform(mul.x, mul.y, mul.z, mul.w, add.x, add.y, add.z, add.w);
        }
    }
}
