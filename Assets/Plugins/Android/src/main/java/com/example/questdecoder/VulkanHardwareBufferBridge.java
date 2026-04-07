package com.example.questdecoder;

import android.hardware.HardwareBuffer;
import android.os.Build;
import android.util.Log;

/**
 * Bridge between Java HardwareBuffer and native Vulkan texture.
 * Provides JNI interface for importing Android HardwareBuffer into Vulkan.
 */
public class VulkanHardwareBufferBridge {
    private static final String TAG = "VulkanHWBufferBridge";

    // Note: Do NOT load library here via static block!
    // The library (libunity_vulkan_hwbuffer.so) is loaded by Unity's native plugin system
    // which calls JNI_OnLoad before this class is even loaded.
    // Adding System.loadLibrary here would cause a duplicate load attempt and fail.

    // If you need to verify the library is loaded, check from native code in JNI_OnLoad.
    static {
        Log.i(TAG, "VulkanHardwareBufferBridge class loaded (library already loaded by Unity plugin)");
    }

    /**
     * Import Android HardwareBuffer into Vulkan and return the native VkImage handle.
     *
     * @param hardwareBuffer Android HardwareBuffer from MediaCodec
     * @param width Image width
     * @param height Image height
     * @return Native VkImage handle (pointer), or 0 if failed
     */
    public static native long importHardwareBufferToVulkan(
        HardwareBuffer hardwareBuffer,
        int width,
        int height
    );

    /**
     * Release the imported Vulkan image.
     *
     * @param vkImageHandle The VkImage handle returned by importHardwareBufferToVulkan
     */
    public static native void releaseVulkanImage(long vkImageHandle);

    /**
     * Get Unity texture pointer from Vulkan image.
     *
     * @param vkImageHandle The VkImage handle
     * @return Unity native texture pointer
     */
    public static native long getUnityTextureFromVkImage(long vkImageHandle);

    /**
     * Initialize Vulkan context from Unity's Vulkan instance.
     * This is called automatically by the Unity native plugin.
     *
     * @param vkInstance VkInstance handle from Unity
     * @param vkPhysicalDevice VkPhysicalDevice handle from Unity
     * @param vkDevice VkDevice handle from Unity
     */
    public static native void initializeVulkanContext(
        long vkInstance,
        long vkPhysicalDevice,
        long vkDevice
    );

    /**
     * Check if Vulkan context has been initialized.
     *
     * @return true if Vulkan context is ready for HardwareBuffer import
     */
    public static native boolean isVulkanContextInitialized();

    /**
     * Check if Vulkan hardware buffer import is supported on this device.
     *
     * @return true if supported
     */



        /** Step 1: Java → Native acquire */
    public static native long nativeAcquireHardwareBuffer(
        HardwareBuffer hardwareBuffer
    );

    /** Step 2: Java → Native release（丢帧 / shutdown） */
    public static native void nativeReleaseHardwareBuffer(
        long ahbPtr
    );

    /** Capability check */
    public static native boolean isVulkanHardwareBufferSupported();
}
