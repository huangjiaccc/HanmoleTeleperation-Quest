package com.example.questdecoder;

import android.hardware.HardwareBuffer;
import android.os.Build;

public final class HardwareBufferNativeBridge {
    static {
        System.loadLibrary("unity_vulkan_hwbuffer");
    }

    private HardwareBufferNativeBridge() {}

    public static long acquireAHardwareBuffer(HardwareBuffer buffer) {
        if (buffer == null) {
            return 0L;
        }
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            return 0L;
        }
        return nativeAcquireAHardwareBuffer(buffer);
    }

    private static native long nativeAcquireAHardwareBuffer(HardwareBuffer buffer);

    public static void setDecoderColorInfo(int standard, int range, int transfer, int format) {
        nativeSetDecoderColorInfo(standard, range, transfer, format);
    }

    private static native void nativeSetDecoderColorInfo(int standard, int range, int transfer, int format);
}
