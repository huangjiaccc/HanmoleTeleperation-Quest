using System;
using UnityEngine;
using Debug = AppLog;

namespace Quest3VideoPlayer
{
    internal sealed class QuestVulkanYcbcrOverrideApplier
    {
        private int lastAppliedYcbcrModel = int.MinValue;
        private int lastAppliedYcbcrRange = int.MinValue;
        private int lastAppliedSwapCbCr = int.MinValue;
        private int lastAppliedChromaX = int.MinValue;
        private int lastAppliedChromaY = int.MinValue;

        private int lastAppliedManualYuvEnabled = int.MinValue;
        private int lastAppliedManualSwapUv = int.MinValue;
        private int lastAppliedManualInvertU = int.MinValue;
        private int lastAppliedManualInvertV = int.MinValue;
        private int lastAppliedManualOrder = int.MinValue;
        private int lastAppliedManualDebugMode = int.MinValue;
        private int lastAppliedManualInputMode = int.MinValue;

        public void InvalidateAll()
        {
            lastAppliedYcbcrModel = int.MinValue;
            lastAppliedYcbcrRange = int.MinValue;
            lastAppliedSwapCbCr = int.MinValue;
            lastAppliedChromaX = int.MinValue;
            lastAppliedChromaY = int.MinValue;
            InvalidateManual();
        }

        public void InvalidateManual()
        {
            lastAppliedManualYuvEnabled = int.MinValue;
            lastAppliedManualSwapUv = int.MinValue;
            lastAppliedManualInvertU = int.MinValue;
            lastAppliedManualInvertV = int.MinValue;
            lastAppliedManualOrder = int.MinValue;
            lastAppliedManualDebugMode = int.MinValue;
            lastAppliedManualInputMode = int.MinValue;
        }

        public void ApplyYcbcrAndManual(
            IntPtr questVulkanStreamHandle,
            int nativeModel,
            int nativeRange,
            int nativeSwap,
            int nativeX,
            int nativeY,
            int nativeManualEnabled,
            int nativeManualSwapUv,
            int nativeManualInvertU,
            int nativeManualInvertV,
            int nativeManualOrder,
            int nativeManualDebug,
            int nativeManualInputMode)
        {
            if (questVulkanStreamHandle == IntPtr.Zero)
            {
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            bool ycbcrSame = nativeModel == lastAppliedYcbcrModel &&
                            nativeRange == lastAppliedYcbcrRange &&
                            nativeSwap == lastAppliedSwapCbCr &&
                            nativeX == lastAppliedChromaX &&
                            nativeY == lastAppliedChromaY;

            bool manualSame = nativeManualEnabled == lastAppliedManualYuvEnabled &&
                              nativeManualSwapUv == lastAppliedManualSwapUv &&
                              nativeManualInvertU == lastAppliedManualInvertU &&
                              nativeManualInvertV == lastAppliedManualInvertV &&
                              nativeManualOrder == lastAppliedManualOrder &&
                              nativeManualDebug == lastAppliedManualDebugMode &&
                              nativeManualInputMode == lastAppliedManualInputMode;

            if (ycbcrSame && manualSame)
            {
                return;
            }

            if (!ycbcrSame)
            {
                QuestVulkanExt.TrySetYcbcrOverride(nativeModel, nativeRange, nativeSwap, nativeX, nativeY);

                lastAppliedYcbcrModel = nativeModel;
                lastAppliedYcbcrRange = nativeRange;
                lastAppliedSwapCbCr = nativeSwap;
                lastAppliedChromaX = nativeX;
                lastAppliedChromaY = nativeY;
            }

            if (!manualSame)
            {
                bool ok = QuestVulkanExt.TrySetManualYuvParams(
                    nativeManualEnabled,
                    nativeManualSwapUv,
                    nativeManualInvertU,
                    nativeManualInvertV,
                    nativeManualOrder,
                    nativeManualDebug,
                    nativeManualInputMode);
                if (!ok)
                {
                    Debug.LogWarning("[QuestVulkanYcbcrOverrideApplier] QuestVulkan_SetManualYuvParams call failed; manual conversion settings may not apply.");
                }

                lastAppliedManualYuvEnabled = nativeManualEnabled;
                lastAppliedManualSwapUv = nativeManualSwapUv;
                lastAppliedManualInvertU = nativeManualInvertU;
                lastAppliedManualInvertV = nativeManualInvertV;
                lastAppliedManualOrder = nativeManualOrder;
                lastAppliedManualDebugMode = nativeManualDebug;
                lastAppliedManualInputMode = nativeManualInputMode;
            }
#endif
        }

        public void ApplyManualOnly(
            IntPtr questVulkanStreamHandle,
            int nativeManualEnabled,
            int nativeManualSwapUv,
            int nativeManualInvertU,
            int nativeManualInvertV,
            int nativeManualOrder,
            int nativeManualDebug,
            int nativeManualInputMode)
        {
            if (questVulkanStreamHandle == IntPtr.Zero)
            {
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            bool manualSame = nativeManualEnabled == lastAppliedManualYuvEnabled &&
                              nativeManualSwapUv == lastAppliedManualSwapUv &&
                              nativeManualInvertU == lastAppliedManualInvertU &&
                              nativeManualInvertV == lastAppliedManualInvertV &&
                              nativeManualOrder == lastAppliedManualOrder &&
                              nativeManualDebug == lastAppliedManualDebugMode &&
                              nativeManualInputMode == lastAppliedManualInputMode;

            if (manualSame)
            {
                return;
            }

            bool ok = QuestVulkanExt.TrySetManualYuvParams(
                nativeManualEnabled,
                nativeManualSwapUv,
                nativeManualInvertU,
                nativeManualInvertV,
                nativeManualOrder,
                nativeManualDebug,
                nativeManualInputMode);
            if (!ok)
            {
                Debug.LogWarning("[QuestVulkanYcbcrOverrideApplier] QuestVulkan_SetManualYuvParams call failed; manual conversion settings may not apply.");
            }

            lastAppliedManualYuvEnabled = nativeManualEnabled;
            lastAppliedManualSwapUv = nativeManualSwapUv;
            lastAppliedManualInvertU = nativeManualInvertU;
            lastAppliedManualInvertV = nativeManualInvertV;
            lastAppliedManualOrder = nativeManualOrder;
            lastAppliedManualDebugMode = nativeManualDebug;
            lastAppliedManualInputMode = nativeManualInputMode;
#endif
        }
    }
}