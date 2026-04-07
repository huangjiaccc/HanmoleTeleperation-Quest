package com.example.questdecoder;

import android.graphics.ImageFormat;
import android.graphics.PixelFormat;
import android.hardware.HardwareBuffer;
import android.media.Image;
import android.media.ImageReader;
import android.media.MediaCodec;
import android.media.MediaFormat;
import android.media.MediaCodecInfo;
import android.os.Build;
import android.os.ParcelFileDescriptor;
import android.util.Log;
import android.view.Surface;

import androidx.annotation.RequiresApi;

import java.io.File;
import java.io.FileDescriptor;
import java.io.FileOutputStream;
import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.SocketException;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayDeque;
import java.util.Arrays;
import java.util.HashSet;
import java.util.Map;
import java.util.TreeMap;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentLinkedQueue;
import android.content.Context;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.os.Environment;
import com.unity3d.player.UnityPlayer;
import java.util.List;
import java.util.ArrayList;
import java.util.zip.CRC32;
import java.util.concurrent.atomic.AtomicInteger;

@RequiresApi(api = Build.VERSION_CODES.LOLLIPOP)
public class Av1StreamingDecoder implements ImageReader.OnImageAvailableListener {
    private static final String TAG = "Av1StreamingDecoder";
    private static final int MAX_QUEUE = 24;
    private static final int MAX_CPU_CALIBRATION_QUEUE = 1;
    private static final int CPU_CALIBRATION_IMAGE_READER_DEPTH = 2;
    private static final int RELEASE_PACKET_HEADER_SIZE = Integer.BYTES * 8;
    // Keep only the newest HardwareBuffer frame queued in Java. Holding onto more decoded Images
    // here leaves no headroom for acquireLatestImage() and can saturate ImageReader(maxImages=3).
    private static final int MAX_HARDWARE_FRAME_QUEUE = 1;
    private static final int HARDWARE_IMAGE_READER_DEPTH = 3;
    private static final int FENCE_WAIT_TIMEOUT_MS = 10;
    private static final int FENCE_RESULT_WAIT_FAILED = -2;
    private static final int AV1_OBU_TYPE_SEQUENCE_HEADER = 1;
    private static final int AV1_OBU_TYPE_FRAME_HEADER = 3;
    private static final int AV1_OBU_TYPE_FRAME = 6;

    private InetSocketAddress createBindAddress(String address, int port) throws IOException {
        // 强制使用IPv4地址，确保与服务端的IPv4通信兼容
        if (address == null || address.isEmpty() || "0.0.0.0".equals(address) || "::".equals(address)) {
            // 使用IPv4的0.0.0.0地址（监听所有IPv4接口）
            InetAddress ipv4Any = InetAddress.getByAddress(new byte[]{0, 0, 0, 0});
            return new InetSocketAddress(ipv4Any, port);
        }

        // 解析用户指定的地址
        InetAddress bindIp = InetAddress.getByName(address);

        // 如果解析出的是IPv6地址，强制转换为IPv4
        if (bindIp instanceof java.net.Inet6Address) {
            Log.w(TAG, "IPv6 address detected (" + address + "), forcing IPv4 fallback to 0.0.0.0");
            bindIp = InetAddress.getByAddress(new byte[]{0, 0, 0, 0});
        }

        return new InetSocketAddress(bindIp, port);
    }

    private final int width;
    private final int height;
    private final int port;
    private final int secondaryPort;
    private final String listenAddress;
    private final String expectedSenderIp;
    private final String udpRemoteHost;
    private final int udpRemotePort;
    private volatile InetSocketAddress configuredRemoteEndpoint;

    private static final int MAX_IN_FLIGHT_FRAMES = 3;
    private static final long FRAME_TIMEOUT_MS = 4000;
    private final ConcurrentLinkedQueue<FrameBundle> frameQueue = new ConcurrentLinkedQueue<>();
    private final ConcurrentLinkedQueue<HardwareBufferFrame> hardwareFrameQueue = new ConcurrentLinkedQueue<>();
    private final ConcurrentLinkedQueue<int[]> pendingHeaders = new ConcurrentLinkedQueue<>();
    private final ConcurrentHashMap<Integer, ReleaseFrameAssembler> releaseFrameAssemblers = new ConcurrentHashMap<>();
    private final ConcurrentHashMap<Long, ReleaseSplitAssembler> splitAssemblers = new ConcurrentHashMap<>();
    private final Object frameWindowLock = new Object();
    private final ArrayDeque<Integer> activeFrameOrder = new ArrayDeque<>(MAX_IN_FLIGHT_FRAMES);

    private final java.util.List<DatagramSocket> sockets = new ArrayList<>();
    private final java.util.List<Thread> receiverThreads = new ArrayList<>();
    private DatagramSocket primarySocket;
    private DatagramSocket heartbeatSocket;
    private Thread heartbeatThread;
    private volatile boolean heartbeatRunning;
    private volatile byte[] heartbeatPayload;
    private volatile int heartbeatPayloadLength;
    private volatile long lastHeartbeatPayloadUpdateMs;
    private volatile long lastHeartbeatSentMs;
    private volatile long lastHeartbeatSkipLogMs;
    private volatile long heartbeatSentCount;
    private volatile long lastPacketRxLogMs;
    private volatile long lastDequeueLogMs;
    private volatile InetSocketAddress lastPacketEndpoint;
    private boolean useHardwareBufferFrames;
    private volatile boolean nativeHardwareBufferImporterEnabled;
    private MediaCodec decoder;
    private ImageReader imageReader;
    private Surface surface;
    private Thread decoderOutputThread;
    private byte[] rgbaScratch;
    private byte[] rgbaRowScratch;
    private byte[] yRowScratch;
    private byte[] uRowScratch;
    private byte[] vRowScratch;

    private HandlerThread handlerThread;
    private Handler handler;

    private volatile boolean running;
    private boolean verbose;
    private boolean debugChecksums;
    private long frameCounter;
    private long packetsReceived;
    private long nalSubmitted;
    private long bytesReceived;
    private long framesReceived;
    private long framesReassembled;
    private long framesEnqueued;
    private long framesDequeued;
    private long noInputBufferCount;
    private long lastNoInputBufferLogMs;

    private long lastStatsPacketsReceived;
    private long lastStatsBytesReceived;
    private long lastStatsFramesReassembled;
    private long lastStatsNalSubmitted;
    private long lastStatsFrameCounter;
    private long lastStatsFramesEnqueued;
    private long lastStatsFramesDequeued;

    private long yuvToRgbaConvertCount;
    private long yuvToRgbaConvertNsTotal;
    private long yuvToRgbaConvertNsMax;
    private int consecutiveFenceTimeouts;
    private long lastFenceTimeoutLogMs;

    // --- First-frame CPU decode + decoder color info handoff (AHardwareBuffer GPU path) ---
    // Unity-controlled switches (C# decides; Java must not auto-calibrate by default).
    private volatile boolean unityAutoCalibrationEnabled = false;
    private volatile boolean nativeColorInfoHandoffEnabled = false;

    public synchronized void setUnityAutoCalibrationEnabled(boolean enable) {
        unityAutoCalibrationEnabled = enable;
    }

    public synchronized void setNativeColorInfoHandoffEnabled(boolean enable) {
        nativeColorInfoHandoffEnabled = enable;
        // Force re-send on next format change when re-enabled.
        if (!enable) {
            lastSentColorStandard = Integer.MIN_VALUE;
            lastSentColorRange = Integer.MIN_VALUE;
            lastSentColorTransfer = Integer.MIN_VALUE;
            lastSentColorFormat = Integer.MIN_VALUE;
        }
    }
    private final Object codecRestartLock = new Object();
    private volatile boolean decoderUsingHardwareBuffers;
    private volatile boolean cpuFirstFrameDecoded;
    private volatile boolean codecRestarting;
    private volatile boolean lockGpuColorParamsToCpu;

    // --- CPU-first-frame -> GPU calibration frame replay ---
    // We need CPU and GPU to see the *same content* for meaningful auto-calibration.
    // Correlate submitted AV1 OBUs by ptsUs so we can replay the exact frame that produced the CPU first output.
    private final Object recentObuLock = new Object();
    private final ArrayDeque<Long> recentObuPtsUs = new ArrayDeque<>(32);
    private final java.util.HashMap<Long, byte[]> recentObuByPtsUs = new java.util.HashMap<>(32);
    private volatile byte[] calibrationObuFrame;
    private volatile boolean calibrationObuReady;
    private volatile int[] calibrationPacketHeader;

    private volatile int lastSentColorStandard = Integer.MIN_VALUE;
    private volatile int lastSentColorRange = Integer.MIN_VALUE;
    private volatile int lastSentColorTransfer = Integer.MIN_VALUE;
    private volatile int lastSentColorFormat = Integer.MIN_VALUE;
    private final java.util.concurrent.atomic.AtomicLong lastImageTimestampMs = new java.util.concurrent.atomic.AtomicLong(0);
    private long lastNoFrameLogMs = 0;
    private long lastUnexpectedCpuFallbackLogMs = 0;
    private static final int MAX_DEBUG_FRAME_DUMPS = 5;
    private final AtomicInteger dumpedFrames = new AtomicInteger(0);
    private File frameDumpDirectory;
    private boolean enableFrameDump = true;

    // Native handoff (HardwareBufferNativeBridge) is optional: builds that do not ship libunity_vulkan_hwbuffer.so
    // must not crash when we attempt to push decoder color info into native.
    private volatile boolean nativeBridgeAvailable = true;
    private volatile boolean nativeBridgeLoggedUnavailable = false;

    // Cache the latest AV1 sequence header OBU (bitstream format header). If future frames do not carry
    // a sequence header, we can prepend the latest one to keep decoder format state stable.
    private final Object formatHeaderLock = new Object();
    private volatile byte[] latestAv1FormatHeader;
    private volatile long latestAv1FormatHeaderCrc = -1L;
    private volatile int latestAv1FormatHeaderBytes = 0;
    private volatile int latestAv1FormatHeaderFrameId = -1;
    private volatile int av1FormatHeaderVersion = 0;

    // Expose the last color info we handed off to native so Unity can persist/reuse calibration across runs.
    // Return -1 until a value has been observed/sent.
    public int getLastColorStandard() {
        int v = lastSentColorStandard;
        return v == Integer.MIN_VALUE ? -1 : v;
    }

    public int getLastColorRange() {
        int v = lastSentColorRange;
        return v == Integer.MIN_VALUE ? -1 : v;
    }

    public int getLastColorTransfer() {
        int v = lastSentColorTransfer;
        return v == Integer.MIN_VALUE ? -1 : v;
    }

    public int getLastColorFormat() {
        int v = lastSentColorFormat;
        return v == Integer.MIN_VALUE ? -1 : v;
    }

    public int getAv1FormatHeaderVersion() {
        return av1FormatHeaderVersion;
    }

    public int getAv1FormatHeaderSizeBytes() {
        return latestAv1FormatHeaderBytes;
    }

    public long getAv1FormatHeaderCrc32() {
        return latestAv1FormatHeaderCrc;
    }

    // 存储正在重组的帧，key为frameId

    public static class FrameBundle {
        private final byte[] image;
        private final int width;
        private final int height;
        private final int[] header;
        private final boolean isYuvFormat;  // 新增：标识是否为YUV格式数据
        private final boolean pooled;
        private boolean released;

        FrameBundle(byte[] image, int width, int height, int[] header, boolean isYuvFormat, boolean pooled) {
            this.image = image;
            this.width = width;
            this.height = height;
            this.header = header;
            this.isYuvFormat = isYuvFormat;
            this.pooled = pooled;
        }

        FrameBundle(byte[] image, int width, int height, int[] header, boolean isYuvFormat) {
            this(image, width, height, header, isYuvFormat, false);  // 默认为RGBA格式
        }

        FrameBundle(byte[] image, int width, int height, int[] header) {
            this(image, width, height, header, false, false);  // 默认为RGBA格式
        }

        public byte[] getImage() {
            return image;
        }

        public int getWidth() {
            return width;
        }

        public int getHeight() {
            return height;
        }

        public int[] getHeader() {
            return header;
        }

        // 新增：获取数据格式标识
        public boolean isYuvFormat() {
            return isYuvFormat;
        }

        public void release() {
            if (released) {
                return;
            }
            if (pooled && image != null) {
                ByteArrayPool.give(image);
            }
            released = true;
        }
    }

    private static class RgbaImage {
        final byte[] data;
        final int width;
        final int height;

        RgbaImage(byte[] data, int width, int height) {
            this.data = data;
            this.width = width;
            this.height = height;
        }
    }

    private static final long STATS_INTERVAL_MS = 5000;
    private long lastStatsTime = 0;

    private static InetSocketAddress createRemoteEndpoint(String host, int port) {
        if (host == null || host.isEmpty() || port <= 0 || port > 65535) {
            return null;
        }

        try {
            InetAddress resolved = InetAddress.getByName(host);
            return new InetSocketAddress(resolved, port);
        } catch (Exception ex) {
            Log.w(TAG, "Unable to resolve remote endpoint " + host + ":" + port, ex);
            return null;
        }
    }

    private DatagramSocket ensureHeartbeatSocket() {
        try {
            if (heartbeatSocket == null || heartbeatSocket.isClosed()) {
                heartbeatSocket = new DatagramSocket();
                heartbeatSocket.setReuseAddress(true);
                heartbeatSocket.setBroadcast(true);
            }
            return heartbeatSocket;
        } catch (Exception ex) {
            Log.w(TAG, "Unable to create heartbeat socket", ex);
            return null;
        }
    }

    public Av1StreamingDecoder(int width, int height, String listenAddress, int port, int secondaryPort, String udpRemoteHost, int udpRemotePort, String expectedSenderIp) {
        this.width = width;
        this.height = height;
        this.port = port;
        this.secondaryPort = secondaryPort;
        this.listenAddress = (listenAddress == null || listenAddress.isEmpty()) ? "0.0.0.0" : listenAddress;
        this.udpRemoteHost = (udpRemoteHost == null) ? "" : udpRemoteHost;
        this.udpRemotePort = udpRemotePort;
        this.expectedSenderIp = (expectedSenderIp == null || expectedSenderIp.isEmpty()) ? null : expectedSenderIp;
        this.configuredRemoteEndpoint = createRemoteEndpoint(this.udpRemoteHost, this.udpRemotePort);
        this.useHardwareBufferFrames = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q;

        Log.i(TAG, "Av1StreamingDecoder config: listen=" + this.listenAddress
                + " port=" + this.port
                + " secondaryPort=" + this.secondaryPort
                + " remoteHost=" + this.udpRemoteHost
                + " remotePort=" + this.udpRemotePort
                + " expectedSenderIp=" + (this.expectedSenderIp == null ? "<any>" : this.expectedSenderIp)
                + " resolvedRemote=" + (this.configuredRemoteEndpoint == null ? "<none>" : this.configuredRemoteEndpoint));
    }

    public static class HardwareBufferFrame {
        private final Image image;
        private final HardwareBuffer hardwareBuffer;
        private final int width;
        private final int height;
        private final int[] header;
        private int fenceFd;
        private boolean released;

        HardwareBufferFrame(Image image, HardwareBuffer hardwareBuffer, int width, int height, int[] header, int fenceFd) {
            this.image = image;
            this.hardwareBuffer = hardwareBuffer;
            this.width = width;
            this.height = height;
            this.header = header;
            this.fenceFd = fenceFd;
        }

        public HardwareBuffer getHardwareBuffer() {
            return hardwareBuffer;
        }

        public int getWidth() {
            return width;
        }

        public int getHeight() {
            return height;
        }

        public int[] getHeader() {
            return header;
        }

        public synchronized int takeFenceFd() {
            int fd = fenceFd;
            fenceFd = -1;
            return fd;
        }

        public synchronized void release() {
            if (released) {
                return;
            }
            released = true;
            try {
                if (image != null) {
                    image.close();
                }
            } catch (Exception ignored) {
            }
            if (fenceFd >= 0) {
                closeFenceFd(fenceFd);
                fenceFd = -1;
            }
            try {
                if (hardwareBuffer != null) {
                    hardwareBuffer.close();
                }
            } catch (Exception ignored) {
            }
        }
    }

    public Av1StreamingDecoder(int width, int height, String listenAddress, int port, int secondaryPort, String expectedSenderIp) {
        this(width, height, listenAddress, port, secondaryPort, "", 0, expectedSenderIp);
    }

    public Av1StreamingDecoder(int width, int height, int port) {
        this(width, height, "0.0.0.0", port, 0, "", 0, null);
    }

    public Av1StreamingDecoder(int width, int height, String listenAddress, int port) {
        this(width, height, listenAddress, port, 0, "", 0, null);
    }

    public Av1StreamingDecoder(int width, int height, String listenAddress, int port, String expectedSenderIp) {
        this(width, height, listenAddress, port, 0, "", 0, expectedSenderIp);
    }

    public void setVerbose(boolean enable) {
        verbose = enable;
        Log.i(TAG, "Verbose logging " + (enable ? "enabled" : "disabled"));
    }

    public void setDebugChecksums(boolean enable) {
        debugChecksums = enable;
        Log.i(TAG, "Checksum logging " + (enable ? "enabled" : "disabled"));
    }

    public void setUseHardwareBufferFrames(boolean enable) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            useHardwareBufferFrames = false;
            Log.w(TAG, "HardwareBuffer frames require API 29+. Disabled.");
            return;
        }
        useHardwareBufferFrames = enable;
        Log.i(TAG, "HardwareBuffer frames " + (enable ? "enabled" : "disabled"));
    }

    public void setNativeHardwareBufferImporterEnabled(boolean enable) {
        nativeHardwareBufferImporterEnabled = enable;
        Log.i(TAG, "Native HardwareBuffer importer " + (enable ? "enabled" : "disabled"));
    }

    public boolean isHardwareBufferFramesRequested() {
        return useHardwareBufferFrames;
    }

    public boolean isDecoderUsingHardwareBuffers() {
        return decoderUsingHardwareBuffers;
    }

    public boolean isNativeHardwareBufferImporterEnabled() {
        return nativeHardwareBufferImporterEnabled;
    }

    public int getHardwareFrameQueueSize() {
        return hardwareFrameQueue.size();
    }

    private boolean dropOldestQueuedHardwareFrame(String reason) {
        HardwareBufferFrame dropped = hardwareFrameQueue.poll();
        if (dropped == null) {
            return false;
        }

        dropped.release();
        Log.w(TAG, reason + " Dropped oldest queued HardwareBuffer frame to recover.");
        return true;
    }

    public synchronized void submitHeartbeatPayload(byte[] payload, int length) {
        if (payload == null || length <= 0) {
            heartbeatPayload = null;
            heartbeatPayloadLength = 0;
            lastHeartbeatPayloadUpdateMs = System.currentTimeMillis();
            return;
        }
        if (length > payload.length) {
            length = payload.length;
        }
        heartbeatPayload = Arrays.copyOf(payload, length);
        heartbeatPayloadLength = length;
        lastHeartbeatPayloadUpdateMs = System.currentTimeMillis();
        if (heartbeatRunning && running) {
            sendHeartbeatPacket();
        }
    }

    private void startUdpReceivers() {
        stopUdpReceivers();
        primarySocket = null;
        List<Integer> portsToBind = new ArrayList<>();
        if (port > 0) {
            portsToBind.add(port);
        }
        if (secondaryPort > 0 && secondaryPort != port) {
            portsToBind.add(secondaryPort);
        }
        for (int targetPort : portsToBind) {
            try {
                DatagramSocket socket = openSocketForPort(targetPort);
                if (targetPort == port) {
                    primarySocket = socket;
                }
                sockets.add(socket);
                Thread thread = new Thread(() -> receiveLoop(socket, targetPort), "UDPReceiver-" + targetPort);
                thread.setDaemon(true);
                thread.start();
                receiverThreads.add(thread);
            } catch (Exception e) {
                Log.e(TAG, "Failed to start UDP receiver on port " + targetPort, e);
            }
        }

        startHeartbeatSender();
    }

    private void stopUdpReceivers() {
        stopHeartbeatSender();
        for (Thread thread : receiverThreads) {
            thread.interrupt();
            try {
                thread.join(2000);
            } catch (InterruptedException ignored) {
            }
        }
        receiverThreads.clear();

        for (DatagramSocket socket : sockets) {
            try {
                socket.close();
            } catch (Exception ignored) {
            }
        }
        sockets.clear();
        primarySocket = null;
        if (heartbeatSocket != null) {
            try {
                heartbeatSocket.close();
            } catch (Exception ignored) {
            }
            heartbeatSocket = null;
        }
    }

    private void startHeartbeatSender() {
        // 强制停止旧的heartbeat线程（如果存在）
        if (heartbeatRunning || heartbeatThread != null) {
            Log.i(TAG, "startHeartbeatSender: stopping existing heartbeat thread first");
            stopHeartbeatSender();
        }

        heartbeatRunning = true;
        heartbeatThread = new Thread(() -> {
            Log.i(TAG, "Heartbeat thread started - flags: heartbeatRunning=" + heartbeatRunning + " running=" + running + " javaUdp=true");

            // 立即发送第一个心跳包（用于UDP握手）
            sendHeartbeatPacket();

            // 进入周期性发送循环
            while (heartbeatRunning && running) {
                try {
                    Thread.sleep(3000);
                } catch (InterruptedException ex) {
                    Log.i(TAG, "Heartbeat thread interrupted during sleep");
                    break;
                }

                if (!heartbeatRunning || !running ) {
                    Log.i(TAG, "Heartbeat thread exiting - flags: heartbeatRunning=" + heartbeatRunning + " running=" + running + " javaUdp=true");
                    break;
                }

                sendHeartbeatPacket();
            }

            Log.i(TAG, "Heartbeat thread EXITED - final flags: heartbeatRunning=" + heartbeatRunning + " running=" + running + " javaUdp=true");
        }, "UdpHeartbeatSender");
        heartbeatThread.setDaemon(true);
        heartbeatThread.start();
        Log.i(TAG, "Heartbeat sender started (immediate first send, then every 3s)");
    }

    // 发送单个心跳包（提取为独立方法）
    private void sendHeartbeatPacket() {
        byte[] payload = heartbeatPayload;
        int length = heartbeatPayloadLength;
        InetSocketAddress target = configuredRemoteEndpoint != null ? configuredRemoteEndpoint : lastPacketEndpoint;
        if (payload == null || length <= 0 || target == null) {
            long now = System.currentTimeMillis();
            if (now - lastHeartbeatSkipLogMs > 5000) {
                lastHeartbeatSkipLogMs = now;
                Log.w(TAG, "Heartbeat skipped: payload=" + (payload == null ? "null" : ("len=" + length))
                        + " target=" + (target == null ? "null" : target)
                        + " lastPayloadUpdateMsAgo=" + (now - lastHeartbeatPayloadUpdateMs)
                        + " lastPacketEndpoint=" + (lastPacketEndpoint == null ? "<none>" : lastPacketEndpoint)
                        + " configuredRemoteEndpoint=" + (configuredRemoteEndpoint == null ? "<none>" : configuredRemoteEndpoint));
            }
            return;
        }

        DatagramSocket socket = primarySocket;
        if (socket == null || socket.isClosed()) {
            long now = System.currentTimeMillis();
            if (now - lastHeartbeatSkipLogMs > 5000) {
                lastHeartbeatSkipLogMs = now;
                Log.w(TAG, "Heartbeat skipped: primarySocket is null/closed (must send from localVideoPort)");
            }
            return;
        }

        try {
            DatagramPacket packet = new DatagramPacket(payload, length, target.getAddress(), target.getPort());
            socket.send(packet);
            lastHeartbeatSentMs = System.currentTimeMillis();
            heartbeatSentCount++;
            if (verbose) {
                logDebug(TAG, "Heartbeat sent: bytes=" + length
                        + " to " + target.getAddress().getHostAddress() + ":" + target.getPort()
                        + " count=" + heartbeatSentCount);
            } else if (heartbeatSentCount == 1 || heartbeatSentCount % 10 == 0) {
                Log.i(TAG, "Heartbeat sent: bytes=" + length
                        + " to " + target.getAddress().getHostAddress() + ":" + target.getPort()
                        + " count=" + heartbeatSentCount);
            }

        } catch (Exception ex) {
            Log.w(TAG, "Heartbeat send failed", ex);
        }
    }

    private void stopHeartbeatSender() {
        heartbeatRunning = false;
        if (heartbeatThread != null) {
            heartbeatThread.interrupt();
            try {
                heartbeatThread.join(1000);  // 增加等待时间到1秒，确保线程完全退出
            } catch (InterruptedException ignored) {
            }
            heartbeatThread = null;
        }
        lastPacketEndpoint = null;
        Log.i(TAG, "Heartbeat sender stopped completely");
    }

    private void logDebug(String message) {
        if (!verbose) {
            return;
        }
        Log.d(TAG, message);
    }

    private void logDebug(String tag, String message) {
        if (!verbose) {
            return;
        }
        Log.d(tag, message);
    }
     
    private void checkNetworkConnectivity() {
        Log.i(TAG, "Checking network connectivity...");
        
        try {
            java.util.Enumeration<java.net.NetworkInterface> interfaces = java.net.NetworkInterface.getNetworkInterfaces();
            int activeInterfaces = 0;
            
            while (interfaces != null && interfaces.hasMoreElements()) {
                java.net.NetworkInterface iface = interfaces.nextElement();
                if (iface.isUp() && !iface.isLoopback()) {
                    activeInterfaces++;
                }
            }
            
            Log.i(TAG, "Number of active network interfaces: " + activeInterfaces);
            
        } catch (Exception e) {
            Log.e(TAG, "Failed to check network interfaces", e);
        }
    }
    
    // 移除发送端连接检查功能，接收所有来源的数据

    private boolean shouldUseCpuCalibrationFrame() {
        return useHardwareBufferFrames
                && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
                && unityAutoCalibrationEnabled
                && !cpuFirstFrameDecoded;
    }

    private void resetCpuCalibrationStateForStream() {
        cpuFirstFrameDecoded = false;
        lockGpuColorParamsToCpu = false;
        calibrationObuFrame = null;
        calibrationObuReady = false;
        calibrationPacketHeader = null;
        releaseCpuRgbaScratchBuffers();
        synchronized (recentObuLock) {
            recentObuPtsUs.clear();
            recentObuByPtsUs.clear();
        }
    }

    private void releaseCpuRgbaScratchBuffers() {
        rgbaScratch = null;
        rgbaRowScratch = null;
        yRowScratch = null;
        uRowScratch = null;
        vRowScratch = null;
    }

    public synchronized void trimCpuCalibrationResources() {
        FrameBundle pendingBundle;
        while ((pendingBundle = frameQueue.poll()) != null) {
            pendingBundle.release();
        }
        releaseCpuRgbaScratchBuffers();
    }

    public void start() {
        stop();
        running = true;
        logDebug("=== Starting streaming decoder (" + width + "x" + height + ") on UDP port " + port + " ===");
        resetAv1FormatHeaderCache();
        resetCpuCalibrationStateForStream();

        checkNetworkConnectivity();

        startDecoder();
        startUdpReceivers();
        // 启动心跳发送（如果配置了远程端点）
        if (configuredRemoteEndpoint != null) {
            startHeartbeatSender();
        }
    }


    public void stop() {
        running = false;
        logDebug("=== Stopping streaming decoder ===");

        printStats();

        stopHeartbeatSender();
        stopUdpReceivers();

        if (decoder != null) {
            stopDecoderOutputDrainer();
            try {
                decoder.stop();
            } catch (Exception ignored) {
            }
            decoder.release();
            decoder = null;
        }
        if (handlerThread != null) {
            handlerThread.quitSafely();
            handlerThread = null;
            handler = null;
        }

        if (imageReader != null) {
            imageReader.close();
            imageReader = null;
        }

        if (surface != null) {
            surface.release();
            surface = null;
        }

        FrameBundle pendingBundle;
        while ((pendingBundle = frameQueue.poll()) != null) {
            pendingBundle.release();
        }
        HardwareBufferFrame pendingHardware;
        while ((pendingHardware = hardwareFrameQueue.poll()) != null) {
            pendingHardware.release();
        }
        pendingHeaders.clear();
        for (ReleaseFrameAssembler assembler : releaseFrameAssemblers.values()) {
            if (assembler != null) {
                assembler.release();
            }
        }
        releaseFrameAssemblers.clear();
        lastImageTimestampMs.set(0);
        lastNoFrameLogMs = 0;
        resetCpuCalibrationStateForStream();
    }

    public void release() {
        stop();
    }

    public FrameBundle dequeueFrameBundle() {
        FrameBundle bundle = frameQueue.poll();
        if (bundle != null && bundle.getImage() != null) {
            framesDequeued++;
            if (verbose) {
                long now = System.currentTimeMillis();
                if (now - lastDequeueLogMs > 1000) {
                    lastDequeueLogMs = now;
                    logDebug(TAG, "dequeueFrameBundle: returned frame size=" + bundle.getImage().length
                            + " remainingQueue=" + frameQueue.size()
                            + " produced=" + frameCounter
                            + " nalSubmitted=" + nalSubmitted);
                }
            }
            return bundle;
        }

        if (verbose) {
            long now = System.currentTimeMillis();
            if (now - lastNoFrameLogMs > 1000) {
                long lastImageMs = lastImageTimestampMs.get();
                long idleMs = lastImageMs > 0 ? (now - lastImageMs) : -1;
                Log.i(TAG, "dequeueFrameBundle: no frame available, queue size: " + frameQueue.size() +
                        ", last decoder image " + (lastImageMs > 0 ? idleMs + "ms ago" : "never"));
                lastNoFrameLogMs = now;
            }
        }
        return null;
    }

    public FrameBundle dequeueCalibrationFrameBundle() {
        FrameBundle bundle = frameQueue.poll();
        if (bundle != null && bundle.getImage() != null) {
            framesDequeued++;
            return bundle;
        }
        return null;
    }

    public HardwareBufferFrame dequeueHardwareBufferFrame() {
        HardwareBufferFrame frame = hardwareFrameQueue.poll();
        if (frame != null) {
            framesDequeued++;
        }
        return frame;
    }

    public long getProducedFrameCount() {
        return frameCounter;
    }

    public int getPendingFrameCount() {
        return frameQueue.size();
    }

    public long getPacketsReceived() {
        return packetsReceived;
    }

    public long getBytesReceived() {
        return bytesReceived;
    }

    public long getFramesReceived() {
        return framesReceived;
    }

    public long getFramesReassembled() {
        return framesReassembled;
    }

    public long getNalSubmitted() {
        return nalSubmitted;
    }

    public long getFramesEnqueued() {
        return framesEnqueued;
    }

    public long getFramesDequeued() {
        return framesDequeued;
    }
    private int[] createReleasePacketHeaderArray(ReleasePacketHeader header) {
        if (header == null) {
            return null;
        }
        return new int[]{
            header.timestamp,
            header.frameId,
            header.splitId,
            header.totalSplits,
            header.fragmentId,
            header.totalFragments,
            header.fragmentSize,
            header.testingId
        };
    }

    private void enqueueFrameHeader(int[] headerData) {
        if (headerData == null) {
            return;
        }
        while (pendingHeaders.size() >= MAX_QUEUE) {
            pendingHeaders.poll();
        }
        pendingHeaders.offer(headerData);
    }
    
    private void startDecoder() {
        handlerThread = new HandlerThread("DecoderImageReaderThread");
        handlerThread.start();
        handler = new Handler(handlerThread.getLooper());

        if (imageReader != null) {
            imageReader.close();
            imageReader = null;
        }

        final boolean requestedHardwareBuffersEnabled = useHardwareBufferFrames && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q;
        // AV1 only permits one CPU-readable first frame for calibration. After that, the stream must stay on
        // the HardwareBuffer path; we no longer allow CPU image fallback as a runtime safety net.
        final boolean cpuCalibrationFrameRequired = shouldUseCpuCalibrationFrame();
        final boolean hardwareBuffersEnabled = requestedHardwareBuffersEnabled && !cpuCalibrationFrameRequired;
        decoderUsingHardwareBuffers = hardwareBuffersEnabled;
        // Use PRIVATE for hardware buffers to match codec surface output (avoids ImageReader format mismatch)
        final int outputFormat = hardwareBuffersEnabled
                ? ImageFormat.PRIVATE
                : ImageFormat.YUV_420_888;
        final int maxImages = hardwareBuffersEnabled
                ? HARDWARE_IMAGE_READER_DEPTH
                : CPU_CALIBRATION_IMAGE_READER_DEPTH;

        if (hardwareBuffersEnabled) {
            // Prefer an AHardwareBuffer-backed ImageReader with PRIVATE format for HardwareBuffer extraction
            long usage = 0;
            try {
                // USAGE_GPU_SAMPLED_IMAGE allows Vulkan to import as VkImage
                // USAGE_GPU_COLOR_OUTPUT allows MediaCodec to write RGBA directly
                usage = (long) HardwareBuffer.USAGE_GPU_SAMPLED_IMAGE | (long) HardwareBuffer.USAGE_GPU_COLOR_OUTPUT;
            } catch (Throwable ignored) {
                usage = 0;
            }

            Log.i(TAG, "Creating ImageReader with PRIVATE format (width=" + width + ", height=" + height + ", usage=0x" + Long.toHexString(usage) + ")");

            ImageReader readerWithUsage = null;
            if (usage != 0) {
                try {
                    java.lang.reflect.Method newInstanceWithUsage = ImageReader.class.getMethod(
                            "newInstance",
                            int.class,
                            int.class,
                            int.class,
                            int.class,
                            long.class);
                    Object obj = newInstanceWithUsage.invoke(null, width, height, outputFormat, maxImages, usage);
                    if (obj instanceof ImageReader) {
                        readerWithUsage = (ImageReader) obj;
                    }
                } catch (Throwable ignored) {
                }
            }

            imageReader = readerWithUsage != null
                    ? readerWithUsage
                    : ImageReader.newInstance(width, height, outputFormat, maxImages);
        } else {
            // On Quest (and some OEM pipelines), Image.getPlanes() can crash the runtime if the ImageReader
            // was backed by GPU-only buffers. Request CPU-readable buffers when we need YUV->RGBA conversion.
            long usage = 0;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                try {
                    usage = (long) HardwareBuffer.USAGE_CPU_READ_OFTEN;
                } catch (Throwable ignored) {
                    usage = 0;
                }
            }

            ImageReader readerWithUsage = null;
            if (usage != 0) {
                try {
                    java.lang.reflect.Method newInstanceWithUsage = ImageReader.class.getMethod(
                            "newInstance",
                            int.class,
                            int.class,
                            int.class,
                            int.class,
                            long.class);
                    Object obj = newInstanceWithUsage.invoke(null, width, height, outputFormat, maxImages, usage);
                    if (obj instanceof ImageReader) {
                        readerWithUsage = (ImageReader) obj;
                    }
                } catch (Throwable ignored) {
                }
            }

            imageReader = readerWithUsage != null
                    ? readerWithUsage
                    : ImageReader.newInstance(width, height, outputFormat, maxImages);
        }
        imageReader.setOnImageAvailableListener(this, handler);

        surface = imageReader.getSurface();

        try {
            decoder = MediaCodec.createDecoderByType("video/av01");
        } catch (IOException e) {
            Log.e(TAG, "Unable to create decoder", e);
            stop();
            return;
        }

        MediaFormat format = MediaFormat.createVideoFormat("video/av01", width, height);
        // Use Surface output when we can consume the buffers without CPU plane access.
        // For the legacy YUV->RGBA path, some devices (Quest) expose planes reliably only when
        // requesting a flexible YUV format here.
        format.setInteger(
                MediaFormat.KEY_COLOR_FORMAT,
                hardwareBuffersEnabled
                        ? MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface
                        : MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible);
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, width * height * 3 / 2);
        // 添加关键的解码器配置参数
        format.setInteger(MediaFormat.KEY_FRAME_RATE, 30);
        format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);
        
        try {
            decoder.configure(format, surface, null, 0);
            decoder.start();
            startDecoderOutputDrainer();
            frameCounter = 0;
            packetsReceived = 0;
            bytesReceived = 0;
            framesReceived = 0;
            framesReassembled = 0;
            nalSubmitted = 0;
            framesEnqueued = 0;
            framesDequeued = 0;
            noInputBufferCount = 0;
            lastNoInputBufferLogMs = 0;
            yuvToRgbaConvertCount = 0;
            yuvToRgbaConvertNsTotal = 0;
            yuvToRgbaConvertNsMax = 0;
            lastStatsTime = System.currentTimeMillis();
            lastStatsPacketsReceived = packetsReceived;
            lastStatsBytesReceived = bytesReceived;
            lastStatsFramesReassembled = framesReassembled;
            lastStatsNalSubmitted = nalSubmitted;
            lastStatsFrameCounter = frameCounter;
            lastStatsFramesEnqueued = framesEnqueued;
            lastStatsFramesDequeued = framesDequeued;
            logDebug("MediaCodec configured for UDP stream (ImageReader format=" + outputFormat
                    + ", maxImages=" + maxImages
                    + ", hardwareBuffers=" + hardwareBuffersEnabled + ").");
        } catch (Exception e) {
            Log.e(TAG, "Failed to configure decoder", e);
            stop();
        }
    }

    private DatagramSocket openSocketForPort(int bindPort) throws IOException {
        DatagramSocket socket = new DatagramSocket(null);
        socket.setReuseAddress(true);
        socket.setBroadcast(true);
        socket.setReceiveBufferSize(16 * 1024 * 1024);
        socket.setSoTimeout(1000);
        InetSocketAddress bindAddress = createBindAddress(listenAddress, bindPort);
        socket.bind(bindAddress);

        Log.i(TAG, "  Broadcast enabled: " + socket.getBroadcast());
        Log.i(TAG, "  Socket bound to: " + socket.getLocalSocketAddress());
        logDebug("UDP socket bound on port " + bindPort + " with buffer size: " + socket.getReceiveBufferSize());
        return socket;
    }

    private void receiveLoop(DatagramSocket socket, int bindPort) {
        Log.i(TAG, String.format("UDP receiver loop started on port %d (javaUdp=true)", bindPort));
        byte[] buffer = new byte[1500];
        DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
        long lastPacketTime = System.currentTimeMillis();
        int consecutiveTimeouts = 0;

        while (running && !Thread.currentThread().isInterrupted()) {
            try {
                socket.receive(packet);

                consecutiveTimeouts = 0;
                lastPacketTime = System.currentTimeMillis();

                InetSocketAddress remoteAddress = (InetSocketAddress) packet.getSocketAddress();
                String remoteIp = remoteAddress != null && remoteAddress.getAddress() != null
                        ? remoteAddress.getAddress().getHostAddress()
                        : "unknown";
                int remotePort = remoteAddress != null ? remoteAddress.getPort() : -1;
                if (remoteAddress != null) {
                    lastPacketEndpoint = remoteAddress;
                }
                if (expectedSenderIp == null || expectedSenderIp.equals(remoteIp)) {
                    if (verbose) {
                        long now = System.currentTimeMillis();
                        if (now - lastPacketRxLogMs > 1000) {
                            lastPacketRxLogMs = now;
                            logDebug(String.format(
                                    "Received UDP video packets from %s:%d (sampled) lastLen=%d localPort=%d packets=%d",
                                    remoteIp,
                                    remotePort,
                                    packet.getLength(),
                                    bindPort,
                                    packetsReceived));
                        }
                    }
                }

                packetsReceived++;
                bytesReceived += packet.getLength();

                processReleasePacket(packet.getData(), packet.getLength());
                printPeriodicStats();

            } catch (SocketTimeoutException e) {
                consecutiveTimeouts++;
                if (consecutiveTimeouts >= 10) {
                    long timeSinceLastPacket = System.currentTimeMillis() - lastPacketTime;
                    if (timeSinceLastPacket > 5000) {
                        Log.w(TAG, String.format(
                            "UDP receiver on port %d waiting for data (no packets for %d ms)",
                            bindPort,
                            timeSinceLastPacket));
                        consecutiveTimeouts = 0;
                    }
                }
            } catch (Exception e) {
                if (running) {
                    Log.e(TAG, "Error in receive loop on port " + bindPort, e);
                } else {
                    Log.i(TAG, "Receive loop exiting due to stop on port " + bindPort);
                }
                break;
            }
        }

        try {
            socket.close();
        } catch (Exception ignored) {
        }
        Log.i(TAG, "UDP receiver loop exited on port " + bindPort);
    }

    private void processReleasePacket(byte[] data, int length) {
        if (length < RELEASE_PACKET_HEADER_SIZE) {
            logDebug("Release packet too small: " + length);
            return;
        }

        ReleasePacketHeader header = ReleasePacketHeader.parse(data);
        if (header == null) {
            logDebug("Failed to parse release packet header");
            return;
        }

        if (header.fragmentSize <= 0 || header.fragmentSize > length - RELEASE_PACKET_HEADER_SIZE) {
            logDebug("Invalid fragment size " + header.fragmentSize + " for packet length " + length);
            return;
        }

        byte[] payload = ByteArrayPool.rent(header.fragmentSize);
        System.arraycopy(data, RELEASE_PACKET_HEADER_SIZE, payload, 0, header.fragmentSize);

        ReleaseFrameAssembler frameAssembler = releaseFrameAssemblers.compute(header.frameId, (id, existing) -> {
            if (existing == null || !existing.isCompatible(header)) {
                if (existing != null) {
                    existing.release();
                }
                ReleaseFrameAssembler created = new ReleaseFrameAssembler(
                    header.frameId,
                    header.totalSplits,
                    header.timestamp,
                    header.testingId
                );
                created.updateMetadata(header);
                registerFrameWindow(id, created);
                return created;
            }
            existing.updateMetadata(header);
            return existing;
        });

        if (frameAssembler == null) {
            ByteArrayPool.give(payload);
            return;
        }

        final long splitKey = makeSplitKey(header.frameId, header.splitId);
        ReleaseSplitAssembler splitAssembler = splitAssemblers.compute(splitKey, (key, existing) -> {
            if (existing == null || !existing.isCompatible(header)) {
                if (existing != null) {
                    existing.releaseFragments();
                }
                return new ReleaseSplitAssembler(header, header.frameId);
            }
            existing.updateSnapshot(header);
            return existing;
        });

        if (splitAssembler == null) {
            ByteArrayPool.give(payload);
            return;
        }

        frameAssembler.registerSplitKey(splitKey);

        if (!splitAssembler.addFragment(header.fragmentId, payload)) {
            ByteArrayPool.give(payload);
            return;
        }

        if (splitAssembler.isComplete()) {
            byte[] nalPayload = splitAssembler.assemble();
            splitAssemblers.remove(splitKey);
            frameAssembler.unregisterSplitKey(splitKey);
            if (nalPayload != null && nalPayload.length > 0) {
                frameAssembler.onSplitCompleted(header.splitId, nalPayload);
            }
        }

        if (frameAssembler.isComplete()) {
            try {
                submitReleaseFrame(frameAssembler);
            } finally {
                releaseFrameAssemblers.remove(header.frameId);
                frameAssembler.release();
                dropFrameFromWindow(header.frameId);
            }
        }

        cleanupReleaseFrameAssemblers();
    }



    private void cleanupReleaseFrameAssemblers() {
        long now = System.currentTimeMillis();
        List<Integer> staleIds = new ArrayList<>();
        for (Map.Entry<Integer, ReleaseFrameAssembler> entry : releaseFrameAssemblers.entrySet()) {
            ReleaseFrameAssembler assembler = entry.getValue();
            if (assembler == null) {
                staleIds.add(entry.getKey());
                continue;
            }
            if (now - assembler.createdAtMs > FRAME_TIMEOUT_MS) {
                staleIds.add(entry.getKey());
            }
        }
        for (int frameId : staleIds) {
            ReleaseFrameAssembler assembler = releaseFrameAssemblers.remove(frameId);
            if (assembler != null) {
                assembler.release();
            }
            dropFrameFromWindow(frameId);
            logDebug("Dropping stale AV1 frame " + frameId);
        }
    }

    private void submitReleaseFrame(ReleaseFrameAssembler assembler) {
        if (assembler == null) {
            return;
        }
        byte[] frameData = assembler.buildFrame();
        if (frameData == null || frameData.length == 0) {
            logDebug("Release frame " + assembler.frameId + " produced empty payload");
            return;
        }

        ReleasePacketHeader effectiveHeader = assembler.getLatestHeader();
        if (effectiveHeader == null) {
            effectiveHeader = assembler.buildFallbackHeader(frameData.length);
        }

        logDebug(String.format(
            "Assembled AV1 frameId=%d splits=%d bytes=%d",
            assembler.frameId,
            assembler.totalSplits,
            frameData.length
        ));

        maybeUpdateAv1FormatHeader(frameData, effectiveHeader);
        dumpFrameForDebug(frameData, effectiveHeader);
        submitObuFrame(frameData);
        ByteArrayPool.give(frameData);
        enqueueFrameHeader(createReleasePacketHeaderArray(effectiveHeader));
        framesReassembled++;
        framesReceived++;
    }

    private static long makeSplitKey(int frameId, int splitId) {
        return ((long) frameId << 32) | (splitId & 0xFFFFFFFFL);
    }

    private void registerFrameWindow(int frameId, ReleaseFrameAssembler assembler) {
        synchronized (frameWindowLock) {
            if (activeFrameOrder.contains(frameId)) {
                return;
            }
            activeFrameOrder.addLast(frameId);
            while (activeFrameOrder.size() > MAX_IN_FLIGHT_FRAMES) {
                Integer oldId = activeFrameOrder.pollFirst();
                if (oldId == null) {
                    break;
                }
                ReleaseFrameAssembler removed = releaseFrameAssemblers.remove(oldId);
                if (removed != null) {
                    removed.release();
                    logDebug("Dropping AV1 frame " + oldId + " due to window limit");
                }
            }
        }
    }

    private void dropFrameFromWindow(int frameId) {
        synchronized (frameWindowLock) {
            activeFrameOrder.remove(frameId);
        }
    }

    private void dumpFrameForDebug(byte[] frameData, ReleasePacketHeader header) {
        if (!enableFrameDump || frameData == null) {
            return;
        }
        if (dumpedFrames.get() >= MAX_DEBUG_FRAME_DUMPS) {
            return;
        }
        File dir = ensureDumpDirectory();
        if (dir == null) {
            return;
        }

        int index = dumpedFrames.getAndIncrement();
        String fileName = String.format(
            "frame_%03d_id_%d_frag_%d.bin",
            index,
            header != null ? header.frameId : -1,
            header != null ? header.totalFragments : -1
        );
        File outFile = new File(dir, fileName);
        FileOutputStream fos = null;
        try {
            fos = new FileOutputStream(outFile);
            fos.write(frameData);
            fos.flush();
            logDebug("Dumped AV1 frame to " + outFile.getAbsolutePath());
        } catch (Exception e) {
            Log.w(TAG, "Failed to dump AV1 frame: " + e.getMessage());
        } finally {
            if (fos != null) {
                try {
                    fos.close();
                } catch (IOException ignored) {
                }
            }
        }

        if (header != null && frameData.length >= 4) {
            ByteBuffer probe = ByteBuffer.wrap(frameData, 0, Math.min(frameData.length, 12)).order(ByteOrder.LITTLE_ENDIAN);
            Log.i(TAG, String.format(
                "Frame %d first bytes: %02X %02X %02X %02X",
                header.frameId,
                frameData[0] & 0xFF,
                frameData[1] & 0xFF,
                frameData[2] & 0xFF,
                frameData[3] & 0xFF
            ));
            int potentialSize = probe.getInt();
            Log.i(TAG, "Little-endian 32-bit value at frame start: " + potentialSize);
        }
    }

    private File ensureDumpDirectory() {
        if (frameDumpDirectory != null) {
            return frameDumpDirectory;
        }
        try {
            Context context = null;
            try {
                if (UnityPlayer.currentActivity != null) {
                    context = UnityPlayer.currentActivity.getApplicationContext();
                }
            } catch (Throwable ignored) {
            }

            if (context != null) {
                File appDir = context.getExternalFilesDir("Av1FrameDumps");
                if (appDir != null && (appDir.exists() || appDir.mkdirs())) {
                    frameDumpDirectory = appDir;
                    return frameDumpDirectory;
                }
            }

            File fallbackRoot = Environment.getExternalStorageDirectory();
            File fallback = new File(fallbackRoot, "Android/data/Av1FrameDumps");
            if (!fallback.exists() && !fallback.mkdirs()) {
                Log.w(TAG, "Failed to create dump directory " + fallback.getAbsolutePath());
                return null;
            }
            frameDumpDirectory = fallback;
            return frameDumpDirectory;
        } catch (Exception e) {
            Log.w(TAG, "Failed to prepare dump directory: " + e.getMessage());
            return null;
        }
    }

    private static class ReleasePacketHeader {
        final int timestamp;
        final int frameId;
        final int splitId;
        final int totalSplits;
        final int fragmentId;
        final int totalFragments;
        final int fragmentSize;
        final int testingId;

        ReleasePacketHeader(int timestamp, int frameId, int splitId, int totalSplits,
                         int fragmentId, int totalFragments, int fragmentSize, int testingId) {
            this.timestamp = timestamp;
            this.frameId = frameId;
            this.splitId = splitId;
            this.totalSplits = totalSplits;
            this.fragmentId = fragmentId;
            this.totalFragments = totalFragments;
            this.fragmentSize = fragmentSize;
            this.testingId = testingId;
        }

        static ReleasePacketHeader parse(byte[] data) {
            if (data.length < RELEASE_PACKET_HEADER_SIZE) {
                return null;
            }
            ByteBuffer buffer = ByteBuffer.wrap(data, 0, RELEASE_PACKET_HEADER_SIZE).order(ByteOrder.LITTLE_ENDIAN);
            int timestamp = buffer.getInt();
            int frameId = buffer.getInt();
            int splitId = buffer.getInt();
            int totalSplits = buffer.getInt();
            int fragmentId = buffer.getInt();
            int totalFragments = buffer.getInt();
            int fragmentSize = buffer.getInt();
            int testingId = buffer.getInt();
            return new ReleasePacketHeader(timestamp, frameId, splitId, totalSplits, fragmentId, totalFragments, fragmentSize, testingId);
        }
    }

    private static class ReleaseSplitAssembler {
        final int frameId;
        final int splitId;
        final int totalFragments;
        final byte[][] fragments;
        ReleasePacketHeader headerSnapshot;
        int receivedFragments = 0;
        int totalBytes = 0;

        ReleaseSplitAssembler(ReleasePacketHeader header, int frameId) {
            this.frameId = frameId;
            this.splitId = header.splitId;
            this.totalFragments = Math.max(1, header.totalFragments);
            this.fragments = new byte[this.totalFragments][];
            this.headerSnapshot = header;
        }

        synchronized boolean addFragment(int fragmentId, byte[] payload) {
            if (fragmentId < 0 || fragmentId >= totalFragments) {
                return false;
            }
            if (fragments[fragmentId] != null) {
                return false;
            }
            fragments[fragmentId] = payload;
            receivedFragments++;
            totalBytes += payload.length;
            return true;
        }

        synchronized boolean isCompatible(ReleasePacketHeader header) {
            return header != null && header.totalFragments == totalFragments;
        }

        synchronized void updateSnapshot(ReleasePacketHeader header) {
            if (header != null) {
                this.headerSnapshot = header;
            }
        }

        synchronized boolean isComplete() {
            return receivedFragments == totalFragments;
        }

        synchronized byte[] assemble() {
            if (!isComplete() || totalBytes <= 0) {
                return null;
            }
            byte[] data = ByteArrayPool.rent(totalBytes);
            int offset = 0;
            for (byte[] fragment : fragments) {
                if (fragment == null) {
                    ByteArrayPool.give(data);
                    releaseFragments();
                    return null;
                }
                System.arraycopy(fragment, 0, data, offset, fragment.length);
                offset += fragment.length;
            }
            releaseFragments();
            return data;
        }

        synchronized void releaseFragments() {
            for (int i = 0; i < fragments.length; i++) {
                if (fragments[i] != null) {
                    ByteArrayPool.give(fragments[i]);
                    fragments[i] = null;
                }
            }
            receivedFragments = 0;
            totalBytes = 0;
        }
    }

    private class ReleaseFrameAssembler {
        final int frameId;
        final int totalSplits;
        final int timestampMs;
        final long createdAtMs;
        final int testingId;
        final TreeMap<Integer, byte[]> splitPayloads = new TreeMap<>();
        final HashSet<Long> activeSplitKeys = new HashSet<>();
        ReleasePacketHeader latestHeader;

        ReleaseFrameAssembler(int frameId, int totalSplits, int timestampMs, int testingId) {
            this.frameId = frameId;
            this.totalSplits = Math.max(1, totalSplits);
            this.timestampMs = timestampMs;
            this.createdAtMs = System.currentTimeMillis();
            this.testingId = testingId;
        }

        synchronized boolean isCompatible(ReleasePacketHeader header) {
            return header != null && header.totalSplits == this.totalSplits;
        }

        synchronized void registerSplitKey(long splitKey) {
            activeSplitKeys.add(splitKey);
        }

        synchronized void unregisterSplitKey(long splitKey) {
            activeSplitKeys.remove(splitKey);
        }

        synchronized void updateMetadata(ReleasePacketHeader header) {
            if (header != null) {
                this.latestHeader = header;
            }
        }

        synchronized ReleasePacketHeader getLatestHeader() {
            return latestHeader;
        }

        synchronized void onSplitCompleted(int splitId, byte[] payload) {
            splitPayloads.put(splitId, payload);
        }

        synchronized boolean isComplete() {
            return splitPayloads.size() >= totalSplits;
        }

        synchronized byte[] buildFrame() {
            if (!isComplete()) {
                return null;
            }
            int total = 0;
            for (byte[] payload : splitPayloads.values()) {
                if (payload == null) {
                    return null;
                }
                total += payload.length;
            }
            byte[] frame = ByteArrayPool.rent(total);
            int offset = 0;
            for (byte[] payload : splitPayloads.values()) {
                System.arraycopy(payload, 0, frame, offset, payload.length);
                offset += payload.length;
            }
            return frame;
        }

        synchronized ReleasePacketHeader buildFallbackHeader(int frameSize) {
            return new ReleasePacketHeader(
                timestampMs,
                frameId,
                Math.max(0, totalSplits - 1),
                totalSplits,
                0,
                1,
                frameSize,
                testingId
            );
        }

        synchronized void release() {
            for (Long key : activeSplitKeys) {
                ReleaseSplitAssembler assembler = splitAssemblers.remove(key);
                if (assembler != null) {
                    assembler.releaseFragments();
                }
            }
            activeSplitKeys.clear();
            for (byte[] payload : splitPayloads.values()) {
                if (payload != null) {
                    ByteArrayPool.give(payload);
                }
            }
            splitPayloads.clear();
            latestHeader = null;
        }
    }

    private static final class Av1ObuScanResult {
        byte[] sequenceHeaderObu;
        boolean sawFrameData;
    }

    private static Av1ObuScanResult scanAv1Obus(byte[] payload) {
        Av1ObuScanResult result = new Av1ObuScanResult();
        if (payload == null || payload.length == 0) {
            return result;
        }

        int offset = 0;
        while (offset < payload.length) {
            final int obuStart = offset;
            final int header = payload[offset++] & 0xFF;
            final int obuType = (header >> 3) & 0x0F;
            final boolean extensionFlag = ((header >> 2) & 0x01) != 0;
            final boolean hasSizeField = ((header >> 1) & 0x01) != 0;

            if (extensionFlag) {
                if (offset >= payload.length) {
                    break;
                }
                offset++; // obu_extension_header
            }

            if (!hasSizeField) {
                // Low-overhead OBU streams should carry size fields. If not present we cannot parse safely.
                break;
            }

            long payloadSize = 0;
            int shift = 0;
            int lebBytes = 0;
            boolean lebDone = false;
            while (offset < payload.length && lebBytes < 8) {
                int b = payload[offset++] & 0xFF;
                payloadSize |= (long) (b & 0x7F) << shift;
                lebBytes++;
                if ((b & 0x80) == 0) {
                    lebDone = true;
                    break;
                }
                shift += 7;
            }
            if (!lebDone || payloadSize < 0 || payloadSize > Integer.MAX_VALUE) {
                break;
            }

            int size = (int) payloadSize;
            if (offset + size > payload.length) {
                break;
            }

            int obuEnd = offset + size;
            if (obuType == AV1_OBU_TYPE_SEQUENCE_HEADER) {
                result.sequenceHeaderObu = Arrays.copyOfRange(payload, obuStart, obuEnd);
            } else if (obuType == AV1_OBU_TYPE_FRAME_HEADER || obuType == AV1_OBU_TYPE_FRAME) {
                result.sawFrameData = true;
            }

            offset = obuEnd;
        }

        return result;
    }

    private static long computeCrc32(byte[] payload) {
        if (payload == null || payload.length == 0) {
            return 0L;
        }
        CRC32 crc = new CRC32();
        crc.update(payload, 0, payload.length);
        return crc.getValue();
    }

    private void resetAv1FormatHeaderCache() {
        synchronized (formatHeaderLock) {
            latestAv1FormatHeader = null;
            latestAv1FormatHeaderCrc = -1L;
            latestAv1FormatHeaderBytes = 0;
            latestAv1FormatHeaderFrameId = -1;
            av1FormatHeaderVersion = 0;
        }
    }

    private void maybeUpdateAv1FormatHeader(byte[] frameData, ReleasePacketHeader header) {
        Av1ObuScanResult scan = scanAv1Obus(frameData);
        byte[] sequenceHeader = scan.sequenceHeaderObu;
        if (sequenceHeader == null || sequenceHeader.length == 0) {
            return;
        }

        long crc = computeCrc32(sequenceHeader);
        boolean changed;
        int version;
        synchronized (formatHeaderLock) {
            changed = latestAv1FormatHeader == null
                    || latestAv1FormatHeader.length != sequenceHeader.length
                    || !Arrays.equals(latestAv1FormatHeader, sequenceHeader);
            if (!changed) {
                return;
            }
            latestAv1FormatHeader = sequenceHeader;
            latestAv1FormatHeaderCrc = crc;
            latestAv1FormatHeaderBytes = sequenceHeader.length;
            latestAv1FormatHeaderFrameId = header != null ? header.frameId : -1;
            av1FormatHeaderVersion++;
            version = av1FormatHeaderVersion;
        }

        Log.i(TAG, "AV1 format header updated: version=" + version
                + " bytes=" + sequenceHeader.length
                + " crc32=0x" + Long.toHexString(crc)
                + " frameId=" + latestAv1FormatHeaderFrameId
                + " timestamp=" + (header != null ? header.timestamp : -1));
    }

    private byte[] maybePrefixLatestAv1FormatHeader(byte[] frameData) {
        if (frameData == null || frameData.length == 0) {
            return frameData;
        }

        Av1ObuScanResult scan = scanAv1Obus(frameData);
        if (scan.sequenceHeaderObu != null || !scan.sawFrameData) {
            return frameData;
        }

        byte[] formatHeader = latestAv1FormatHeader;
        if (formatHeader == null || formatHeader.length == 0) {
            return frameData;
        }

        int mergedLength = formatHeader.length + frameData.length;
        byte[] merged = ByteArrayPool.rent(mergedLength);
        System.arraycopy(formatHeader, 0, merged, 0, formatHeader.length);
        System.arraycopy(frameData, 0, merged, formatHeader.length, frameData.length);
        if (verbose) {
            logDebug("Prefixed AV1 format header before decode (headerBytes=" + formatHeader.length
                    + ", frameBytes=" + frameData.length
                    + ", crc32=0x" + Long.toHexString(latestAv1FormatHeaderCrc) + ")");
        }
        return merged;
    }

    private void submitObuFrame(byte[] frameData) {
        if (frameData == null || frameData.length == 0 || decoder == null) {
            return;
        }
        byte[] decodePayload = frameData;
        boolean borrowedDecodePayload = false;
        try {
            decodePayload = maybePrefixLatestAv1FormatHeader(frameData);
            borrowedDecodePayload = decodePayload != frameData;
            int inputBufferId = decoder.dequeueInputBuffer(10000);
            if (inputBufferId >= 0) {
                ByteBuffer inputBuffer = decoder.getInputBuffer(inputBufferId);
                if (inputBuffer != null) {
                    inputBuffer.clear();
                    if (inputBuffer.capacity() < decodePayload.length) {
                        Log.w(TAG, "Input buffer too small for AV1 OBU frame (capacity=" + inputBuffer.capacity()
                                + ", required=" + decodePayload.length + ")");
                        decoder.queueInputBuffer(inputBufferId, 0, 0, System.nanoTime() / 1000, 0);
                        return;
                    }
                    inputBuffer.put(decodePayload);

                    // Use a stable ptsUs value for both queueing and correlation with Surface/Image timestamps.
                    final long ptsUs = System.nanoTime() / 1000;

                    // Before we decode the CPU-first-frame, keep a copy of submitted frames keyed by ptsUs.
                    // We'll use the Image timestamp to grab the exact OBU that produced the CPU first output image.
                    if (unityAutoCalibrationEnabled &&
                            useHardwareBufferFrames &&
                            Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q &&
                            !cpuFirstFrameDecoded &&
                            !calibrationObuReady) {
                        byte[] copy = Arrays.copyOf(decodePayload, decodePayload.length);
                        synchronized (recentObuLock) {
                            recentObuByPtsUs.put(ptsUs, copy);
                            recentObuPtsUs.addLast(ptsUs);
                            while (recentObuPtsUs.size() > 24) {
                                Long old = recentObuPtsUs.pollFirst();
                                if (old != null) {
                                    recentObuByPtsUs.remove(old);
                                }
                            }
                        }
                    }

                    decoder.queueInputBuffer(
                        inputBufferId,
                        0,
                        decodePayload.length,
                        ptsUs,
                        0
                    );
                    nalSubmitted++;
                    logDebug("Submitted AV1 OBU frame to decoder - size: " + decodePayload.length + ", total submitted: " + nalSubmitted);
                }
            } else {
                noInputBufferCount++;
                long now = System.currentTimeMillis();
                if (now - lastNoInputBufferLogMs > 1000) {
                    lastNoInputBufferLogMs = now;
                    Log.w(TAG, "No input buffer available for AV1 submission (count=" + noInputBufferCount
                            + ", frameQueue=" + frameQueue.size()
                            + ", produced=" + frameCounter
                            + ", enqueued=" + framesEnqueued
                            + ", dequeued=" + framesDequeued + ")");
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "Error submitting AV1 OBU frame", e);
        } finally {
            if (borrowedDecodePayload) {
                ByteArrayPool.give(decodePayload);
            }
        }
    }

    private void startDecoderOutputDrainer() {
        decoderOutputThread = new Thread(this::drainDecoderOutput, "DecoderOutputDrainer");
        decoderOutputThread.setDaemon(true);
        decoderOutputThread.start();
    }

    private void stopDecoderOutputDrainer() {
        if (decoderOutputThread != null) {
            decoderOutputThread.interrupt();
            try {
                decoderOutputThread.join(1000);
            } catch (InterruptedException e) {
                Log.w(TAG, "Interrupted while waiting for decoder output thread", e);
            }
            decoderOutputThread = null;
        }
    }

    private void drainDecoderOutput() {
        MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();
        while (running && !Thread.currentThread().isInterrupted()) {
            try {
                int outputBufferId = decoder.dequeueOutputBuffer(bufferInfo, 1000);
                if (outputBufferId >= 0) {
                    // 检查解码器输出是否有效
                    // NOTE: For Surface output, BufferInfo.size is often 0 even for valid frames.
                    // Releasing with render=true is what makes frames show up in the ImageReader.
                    boolean isCodecConfig = (bufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                    boolean isEndOfStream = (bufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0;
                    boolean shouldRender = !isCodecConfig && !isEndOfStream;

                    decoder.releaseOutputBuffer(outputBufferId, shouldRender);

                    if (shouldRender) {
                        frameCounter++;
                        lastImageTimestampMs.set(System.currentTimeMillis());
                        logDebug("Rendered frame " + frameCounter + " - flags: " + bufferInfo.flags + ", size: " + bufferInfo.size);
                    } else {
                        logDebug("Skipped non-renderable output buffer - flags: " + bufferInfo.flags + ", size: " + bufferInfo.size);
                    }
                } else if (outputBufferId == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    MediaFormat newFormat = decoder.getOutputFormat();
                    Log.i(TAG, "Decoder output format changed: " + newFormat);
                    maybeSendDecoderColorInfo(newFormat, "INFO_OUTPUT_FORMAT_CHANGED");


                    logDebug("New output format - " + newFormat.toString());
                } else if (outputBufferId == MediaCodec.INFO_TRY_AGAIN_LATER) {
                    // 没有可用输出，短暂等待
                    Thread.sleep(10);
                }
            } catch (Exception e) {
                if (running) {
                    Log.e(TAG, "Error draining decoder output", e);
                } else {
                    Log.i(TAG, "Decoder output drainer exiting");
                }
                break;
            }
        }
    }

    private int prepareFenceForHardwareBuffer(Image image) {
        if (image == null) {
            return -1;
        }
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            return -1;
        }

        Object fence = null;
        try {
            java.lang.reflect.Method getFence = Image.class.getMethod("getFence");
            fence = getFence.invoke(image);
        } catch (Throwable ignored) {
        }

        if (fence == null) {
            return -1;
        }

        boolean valid = true;
        try {
            java.lang.reflect.Method isValid = fence.getClass().getMethod("isValid");
            Object result = isValid.invoke(fence);
            if (result instanceof Boolean) {
                valid = (Boolean) result;
            }
        } catch (Throwable ignored) {
        }

        if (!valid) {
            closeFenceQuietly(fence);
            return -1;
        }

        if (nativeHardwareBufferImporterEnabled) {
            int dupFd = tryDupFenceFd(fence);
            if (dupFd >= 0) {
                closeFenceQuietly(fence);
                return dupFd;
            }
        }

        boolean ok = awaitFenceObject(fence);
        closeFenceQuietly(fence);
        return ok ? -1 : FENCE_RESULT_WAIT_FAILED;
    }

    private boolean awaitFenceObject(Object fence) {
        boolean ok = true;
        try {
            java.lang.reflect.Method await = fence.getClass().getMethod("await", long.class);
            Object result = await.invoke(fence, (long) FENCE_WAIT_TIMEOUT_MS);
            if (result instanceof Boolean) {
                ok = (Boolean) result;
            } else if (result instanceof Integer) {
                ok = ((Integer) result) == 0;
            }
        } catch (Throwable ignored) {
        }
        return ok;
    }

    private int tryDupFenceFd(Object fence) {
        if (fence == null) {
            return -1;
        }
        int fd = -1;
        try {
            java.lang.reflect.Method getFd = fence.getClass().getMethod("getFd");
            Object result = getFd.invoke(fence);
            if (result instanceof Integer) {
                fd = (Integer) result;
            }
        } catch (Throwable ignored) {
            return -1;
        }

        if (fd < 0) {
            return -1;
        }

        try {
            FileDescriptor rawFd = new FileDescriptor();
            java.lang.reflect.Method setInt = FileDescriptor.class.getDeclaredMethod("setInt$", int.class);
            setInt.setAccessible(true);
            setInt.invoke(rawFd, fd);
            ParcelFileDescriptor dup = ParcelFileDescriptor.dup(rawFd);
            int dupFd = dup.detachFd();
            try {
                dup.close();
            } catch (Throwable ignored) {
            }
            return dupFd;
        } catch (Throwable ignored) {
            return -1;
        }
    }

    private void closeFenceQuietly(Object fence) {
        if (fence == null) {
            return;
        }
        try {
            java.lang.reflect.Method close = fence.getClass().getMethod("close");
            close.invoke(fence);
        } catch (Throwable ignored) {
        }
    }

    private static void closeFenceFd(int fd) {
        if (fd < 0) {
            return;
        }
        try {
            ParcelFileDescriptor.adoptFd(fd).close();
        } catch (Throwable ignored) {
        }
    }

    @Override
    public void onImageAvailable(ImageReader reader) {
        Image image = null;
        try {
            try {
                image = reader.acquireLatestImage();
            } catch (IllegalStateException acquireError) {
                if (decoderUsingHardwareBuffers
                        && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
                        && dropOldestQueuedHardwareFrame("onImageAvailable: ImageReader saturated.")) {
                    return;
                }
                throw acquireError;
            }
            if (image == null) {
                logDebug("onImageAvailable: acquired null image");
                return;
            }

            // 动态获取当前图像的实际分辨率
            if (codecRestarting) {
                logDebug("onImageAvailable: codec restarting, dropping frame");
                return;
            }

            int currentWidth = image.getWidth();
            int currentHeight = image.getHeight();
            final boolean cpuCalibrationFrameRequired = shouldUseCpuCalibrationFrame();

            if (decoderUsingHardwareBuffers && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                int fenceResult = prepareFenceForHardwareBuffer(image);
                if (fenceResult == FENCE_RESULT_WAIT_FAILED) {
                    consecutiveFenceTimeouts++;
                    long now = System.currentTimeMillis();
                    if (consecutiveFenceTimeouts == 1 || now - lastFenceTimeoutLogMs > 1000) {
                        lastFenceTimeoutLogMs = now;
                        Log.w(TAG, "Image fence wait timed out (count=" + consecutiveFenceTimeouts + "). Dropping frame.");
                    }
                    return;
                }
                consecutiveFenceTimeouts = 0;
                int fenceFd = fenceResult >= 0 ? fenceResult : -1;

                if (framesEnqueued == 0) {
                    Log.i(TAG, "HardwareBuffer frame: imageFormat=" + image.getFormat()
                            + " size=" + currentWidth + "x" + currentHeight);
                }
                HardwareBuffer hardwareBuffer = image.getHardwareBuffer();
                if (hardwareBuffer != null) {
                    if (framesEnqueued == 0) {
                        Log.i(TAG, "HardwareBuffer info: format=" + hardwareBuffer.getFormat()
                                + " usage=0x" + Long.toHexString(hardwareBuffer.getUsage()));
                    }
                    int[] header = pendingHeaders.poll();
                    if (header == null && verbose) {
                        Log.w(TAG, "No pending packet header available for decoded frame (HardwareBuffer)");
                    }

                    HardwareBufferFrame frame = new HardwareBufferFrame(image, hardwareBuffer, currentWidth, currentHeight, header, fenceFd);
                    while (hardwareFrameQueue.size() >= MAX_HARDWARE_FRAME_QUEUE) {
                        HardwareBufferFrame dropped = hardwareFrameQueue.poll();
                        if (dropped != null) {
                            dropped.release();
                        }
                    }
                    hardwareFrameQueue.add(frame);
                    framesEnqueued++;
                    image = null;
                    return;
                }

                // Do not fall back to Image.getPlanes() here: some devices crash when planes are unavailable.
                Log.w(TAG, "onImageAvailable: hardwareBuffer is null - dropping frame (avoid planes fallback)");
                return;
            }

            if (!cpuCalibrationFrameRequired) {
                if (!cpuFirstFrameDecoded && !codecRestarting) {
                    long now = System.currentTimeMillis();
                    if (now - lastUnexpectedCpuFallbackLogMs > 5000) {
                        lastUnexpectedCpuFallbackLogMs = now;
                        Log.w(TAG, "onImageAvailable: CPU fallback disabled for AV1; dropping non-HardwareBuffer frame.");
                    }
                }
                return;
            }

            byte[] frameData = convertYuvToRgba(image);
            if (frameData != null) {
                int expectedRgbaSize = currentWidth * currentHeight * 4;
                if (frameData.length != expectedRgbaSize) {
                    Log.w(TAG, "RGBA data size mismatch! Expected " + expectedRgbaSize + " bytes, got " + frameData.length + " bytes.");
                }

                int[] header = pendingHeaders.poll();
                if (header == null && verbose) {
                    Log.w(TAG, "No pending packet header available for decoded frame");
                }

                byte[] payload = frameData;
                boolean pooledPayload = false;
                if (frameData == rgbaScratch) {
                    payload = ByteArrayPool.rent(frameData.length);
                    System.arraycopy(frameData, 0, payload, 0, Math.min(frameData.length, payload.length));
                    pooledPayload = true;
                }

                FrameBundle bundle = new FrameBundle(payload, currentWidth, currentHeight, header, false, pooledPayload);
                while (frameQueue.size() >= MAX_CPU_CALIBRATION_QUEUE) {
                    FrameBundle dropped = frameQueue.poll(); // 移除最旧的帧
                    if (dropped != null) {
                        dropped.release();
                    }
                    Log.w(TAG, "Frame queue full, dropping oldest frame");
                }
                frameQueue.add(bundle);
                framesEnqueued++;
                logDebug("Added frame to queue - size now: " + frameQueue.size() + ", format: RGBA, resolution: " + currentWidth + "x" + currentHeight + ", size: " + frameData.length + " bytes");

                if (!cpuFirstFrameDecoded && useHardwareBufferFrames && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
                        && unityAutoCalibrationEnabled) {
                    // Capture the exact AV1 frame that produced this first CPU output by matching ptsUs.
                    try {
                        final long ptsUs = image.getTimestamp() / 1000;
                        byte[] match;
                        synchronized (recentObuLock) {
                            match = recentObuByPtsUs.remove(ptsUs);
                            if (match == null && !recentObuPtsUs.isEmpty()) {
                                Long last = recentObuPtsUs.peekLast();
                                if (last != null) {
                                    match = recentObuByPtsUs.get(last);
                                }
                            }
                            recentObuByPtsUs.clear();
                            recentObuPtsUs.clear();
                        }
                        if (match != null && match.length > 0) {
                            calibrationObuFrame = match;
                            calibrationObuReady = true;
                            calibrationPacketHeader = header;
                            Log.i(TAG, "Captured calibration OBU for replay into hardware decoder (bytes=" + match.length + ", ptsUs=" + ptsUs + ")");
                        } else {
                            Log.w(TAG, "Failed to capture calibration OBU for replay (ptsUs=" + ptsUs + ")");
                        }
                    } catch (Throwable t) {
                        Log.w(TAG, "Failed to capture calibration OBU for replay", t);
                    }

                    cpuFirstFrameDecoded = true;
                    lockGpuColorParamsToCpu = true;
                    releaseCpuRgbaScratchBuffers();
                    try {
                        MediaFormat fmt = decoder != null ? decoder.getOutputFormat() : null;
                        maybeSendDecoderColorInfo(fmt, "cpu_first_frame");
                    } catch (Exception ignored) {
                    }
                    Log.i(TAG, "CPU first frame decoded; switching decoder to HardwareBuffer output for zero-copy rendering.");
                    restartCodecForHardwareBuffersAsync();
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "Error processing image from reader", e);
        } finally {
            if (image != null) {
                try {
                    image.close();
                } catch (Exception ignored) {
                }
            }
        }
    }

    public byte[] convertYuvToRgba(Image image) {
        if (image == null) {
            Log.w(TAG, "convertYuvToRgba: image is null");
            return null;
        }

        long convertStartNs = System.nanoTime();

        int width = image.getWidth();
        int height = image.getHeight();

        int imageFormat = image.getFormat();
        if (verbose) {
            logDebug(String.format("convertYuvToRgba: image format=%d, width=%d, height=%d",
                    imageFormat, width, height));
        }

        byte[] result;
        if (imageFormat == PixelFormat.RGBA_8888) {
            result = extractRgba(image);
        } else {
            Image.Plane[] planes = image.getPlanes();
            if (planes == null || planes.length < 3) {
                Log.w(TAG, String.format("convertYuvToRgba: invalid planes count=%d (format=%d)",
                        planes != null ? planes.length : -1, imageFormat));
                return null;
            }

            android.graphics.Rect cropRect = image.getCropRect();
            if (cropRect == null) {
                cropRect = new android.graphics.Rect(0, 0, width, height);
            }
            result = convertYuvToRgbaWithCrop(image, planes, width, height, cropRect);
        }

        long convertNs = System.nanoTime() - convertStartNs;
        yuvToRgbaConvertCount++;
        yuvToRgbaConvertNsTotal += convertNs;
        if (convertNs > yuvToRgbaConvertNsMax) {
            yuvToRgbaConvertNsMax = convertNs;
        }

        return result;
    }

    private byte[] extractRgba(Image image) {
        Image.Plane plane = image.getPlanes()[0];
        int width = image.getWidth();
        int height = image.getHeight();
        int rowStride = plane.getRowStride();
        int pixelStride = plane.getPixelStride();

        ByteBuffer buffer = plane.getBuffer().duplicate();
        if (rgbaRowScratch == null || rgbaRowScratch.length < rowStride) {
            rgbaRowScratch = new byte[rowStride];
        }
        int rgbaSize = width * height * 4;
        if (rgbaScratch == null || rgbaScratch.length < rgbaSize) {
            rgbaScratch = new byte[rgbaSize];
        }

        byte[] rowData = rgbaRowScratch;
        byte[] rgba = rgbaScratch;
        int writeIndex = 0;

        for (int y = 0; y < height; y++) {
            int rowStart = y * rowStride;
            if (rowStart >= buffer.limit()) {
                break;
            }

            buffer.position(rowStart);
            int readable = Math.min(rowStride, buffer.remaining());
            buffer.get(rowData, 0, readable);

            for (int x = 0; x < width; x++) {
                int src = x * pixelStride;
                if (src + 3 >= readable) {
                    break;
                }
                rgba[writeIndex++] = rowData[src];
                rgba[writeIndex++] = rowData[src + 1];
                rgba[writeIndex++] = rowData[src + 2];
                rgba[writeIndex++] = rowData[src + 3];
            }
        }

        return rgbaScratch;
    }
    
    private byte[] convertYuvToRgbaWithCrop(Image image, Image.Plane[] planes, int width, int height, android.graphics.Rect cropRect) {
        Image.Plane yPlane = planes[0];
        Image.Plane uPlane = planes[1];
        Image.Plane vPlane = planes[2];

        int yStride = yPlane.getRowStride();
        int uStride = uPlane.getRowStride();
        int vStride = vPlane.getRowStride();
        int yPixelStride = yPlane.getPixelStride();
        int uPixelStride = uPlane.getPixelStride();
        int vPixelStride = vPlane.getPixelStride();

        ByteBuffer yBuffer = yPlane.getBuffer().duplicate();
        ByteBuffer uBuffer = uPlane.getBuffer().duplicate();
        ByteBuffer vBuffer = vPlane.getBuffer().duplicate();
        final int yBase = yBuffer.position();
        final int uBase = uBuffer.position();
        final int vBase = vBuffer.position();

        int cropWidth = cropRect.width();
        int cropHeight = cropRect.height();
        int cropLeft = cropRect.left;
        int cropTop = cropRect.top;

        if (verbose) {
            logDebug(String.format("YUV plane info: format=%d Y stride=%d pixel=%d; U stride=%d pixel=%d; V stride=%d pixel=%d",
                    image.getFormat(), yStride, yPixelStride, uStride, uPixelStride, vStride, vPixelStride));
        }

        int rgbaSize = cropWidth * cropHeight * 4;
        if (rgbaScratch == null || rgbaScratch.length < rgbaSize) {
            rgbaScratch = new byte[rgbaSize];
        }

        if (yRowScratch == null || yRowScratch.length < yStride) {
            yRowScratch = new byte[yStride];
        }
        boolean interleavedUv = (uPixelStride == 2 && vPixelStride == 2);
        if (uRowScratch == null || uRowScratch.length < uStride) {
            uRowScratch = new byte[uStride];
        }
        if (!interleavedUv && (vRowScratch == null || vRowScratch.length < vStride)) {
            vRowScratch = new byte[vStride];
        }

        byte[] yRowData = yRowScratch;
        byte[] uRowData = uRowScratch;
        byte[] vRowData = interleavedUv ? uRowData : vRowScratch;
        int currentUvRow = -1;

        int rgbaIndex = 0;
        for (int row = 0; row < cropHeight; row++) {
            int y = cropTop + row;
            int yRowStart = yBase + y * yStride;
            readPlaneRow(yBuffer, yRowStart, yRowData);

            int uvRow = (y / 2);
            if (uvRow != currentUvRow) {
                int uRowStart = uBase + uvRow * uStride;
                int vRowStart = vBase + uvRow * vStride;
                readPlaneRow(uBuffer, uRowStart, uRowData);
                if (!interleavedUv) {
                    readPlaneRow(vBuffer, vRowStart, vRowData);
                }
                currentUvRow = uvRow;
            }

            for (int col = 0; col < cropWidth; col++) {
                int x = cropLeft + col;
                int yIndex = Math.min(yRowData.length - 1, x * yPixelStride);
                int uvIndex = Math.min(uRowData.length - 1, (x / 2) * uPixelStride);

                int yValue = yRowData[yIndex] & 0xFF;
                int uValue;
                int vValue;

                if (interleavedUv) {
                    uValue = uRowData[uvIndex] & 0xFF;
                    int vIndex = Math.min(uRowData.length - 1, uvIndex + 1);
                    vValue = uRowData[vIndex] & 0xFF;
                } else {
                    uValue = uRowData[uvIndex] & 0xFF;
                    int vIdx = Math.min(vRowData.length - 1, (x / 2) * vPixelStride);
                    vValue = vRowData[vIdx] & 0xFF;
                }

                float y1 = (yValue - 16.0f) * 1.164f;
                float u1 = uValue - 128.0f;
                float v1 = vValue - 128.0f;

                int r = clampColor(y1 + 1.596f * v1);
                int g = clampColor(y1 - 0.813f * v1 - 0.391f * u1);
                int b = clampColor(y1 + 2.018f * u1);

                rgbaScratch[rgbaIndex++] = (byte) r;
                rgbaScratch[rgbaIndex++] = (byte) g;
                rgbaScratch[rgbaIndex++] = (byte) b;
                rgbaScratch[rgbaIndex++] = (byte) 255;
            }
        }

        return rgbaScratch;
    }

    private void readPlaneRow(ByteBuffer buffer, int start, byte[] rowData) {
        if (buffer == null || rowData == null || start >= buffer.limit()) {
            Arrays.fill(rowData, (byte) 0);
            return;
        }

        int maxReadable = buffer.limit() - start;
        int bytesToCopy = Math.min(rowData.length, Math.max(0, maxReadable));
        buffer.position(start);
        buffer.get(rowData, 0, bytesToCopy);

        if (bytesToCopy < rowData.length) {
            byte pad = rowData[Math.max(0, bytesToCopy - 1)];
            Arrays.fill(rowData, bytesToCopy, rowData.length, pad);
        }
    }

    private int clampColor(float value) {
        return (int) Math.max(0, Math.min(255, value));
    }

    private static void logColorInfo(MediaFormat format, String label) {
        if (format == null) {
            Log.i(TAG, label + " <null>");
            return;
        }

        StringBuilder sb = new StringBuilder(label);
        appendColorKey(sb, format, MediaFormat.KEY_COLOR_STANDARD, "standard");
        appendColorKey(sb, format, MediaFormat.KEY_COLOR_RANGE, "range");
        appendColorKey(sb, format, MediaFormat.KEY_COLOR_TRANSFER, "transfer");
        appendColorKey(sb, format, MediaFormat.KEY_COLOR_FORMAT, "format");
        Log.i(TAG, sb.toString());
    }

    private static void appendColorKey(StringBuilder sb, MediaFormat format, String key, String label) {
        if (format.containsKey(key)) {
            sb.append(" ").append(label).append("=").append(format.getInteger(key));
        }
    }

    private static int getFormatInt(MediaFormat format, String key, int defaultValue) {
        if (format == null || !format.containsKey(key)) {
            return defaultValue;
        }
        try {
            return format.getInteger(key);
        } catch (Exception e) {
            return defaultValue;
        }
    }

    private void maybeSendDecoderColorInfo(MediaFormat format, String reason) {
        if (format == null) {
            return;
        }

        if (!nativeColorInfoHandoffEnabled) {
            return;
        }
// Only push color info into native when we're actually producing HardwareBuffer frames.
        // If we are in the CPU RGBA path, native won't use this information and (depending on packaging)
        // the bridge class may be unavailable.
        if (!useHardwareBufferFrames || !decoderUsingHardwareBuffers) {
            return;
        }

        int standard = getFormatInt(format, MediaFormat.KEY_COLOR_STANDARD, -1);
        int range = getFormatInt(format, MediaFormat.KEY_COLOR_RANGE, -1);
        int transfer = getFormatInt(format, MediaFormat.KEY_COLOR_TRANSFER, -1);
        int colorFormat = getFormatInt(format, MediaFormat.KEY_COLOR_FORMAT, -1);

        // When we intentionally CPU-decode the first frame for validation, prefer CPU conversion parameters
        // so the GPU path matches the known-good CPU YUV->RGBA output (prevents obvious tinting).
        // Once we lock to CPU params, ignore later INFO_OUTPUT_FORMAT_CHANGED updates for standard/range.
        final boolean forceCpuParams = unityAutoCalibrationEnabled
                && (lockGpuColorParamsToCpu || "cpu_first_frame".equals(reason));
        if (forceCpuParams) {
            // CPU conversion in convertYuvToRgbaWithCrop() uses BT.601 + limited (narrow) range coefficients.
            standard = 2; // MediaFormat.COLOR_STANDARD_BT601_PAL
            range = 2;    // MediaFormat.COLOR_RANGE_LIMITED
        }

        // Fallbacks: CPU YUV->RGBA path defaults to BT.601 + limited range (Java-style narrow).
        if (standard < 0) {
            standard = 2; // COLOR_STANDARD_BT601_PAL
        }
        if (range < 0) {
            range = 2; // COLOR_RANGE_LIMITED
        }

        if (standard == lastSentColorStandard &&
                range == lastSentColorRange &&
                transfer == lastSentColorTransfer &&
                colorFormat == lastSentColorFormat) {
            return;
        }

        lastSentColorStandard = standard;
        lastSentColorRange = range;
        lastSentColorTransfer = transfer;
        lastSentColorFormat = colorFormat;

        Log.i(TAG, "Decoder color info (" + reason + ") -> standard=" + standard + (forceCpuParams ? " (LOCKED CPU)" : "")
                + " range=" + range
                + " transfer=" + transfer
                + " format=" + colorFormat);
        logColorInfo(format, "Decoder output color info:");

        // After we lock GPU params to the known-good CPU first frame, stop pushing further format-change updates
        // into native. Auto-calibration (Unity/native) needs a stable baseline and will override swap/invert/inputMode.
        if (lockGpuColorParamsToCpu && !"cpu_first_frame".equals(reason)) {
            Log.i(TAG, "Decoder color info (" + reason + ") native update suppressed (LOCKED CPU)");
            return;
        }

        if (!nativeBridgeAvailable) {
            return;
        }

        try {
            HardwareBufferNativeBridge.setDecoderColorInfo(standard, range, transfer, colorFormat);
        } catch (Throwable t) {
            nativeBridgeAvailable = false;
            if (!nativeBridgeLoggedUnavailable) {
                nativeBridgeLoggedUnavailable = true;
                Log.e(TAG, "HardwareBufferNativeBridge unavailable; native decoder color info handoff disabled.", t);
            }
        }
    }

    private void restartCodecForHardwareBuffersAsync() {
        if (!running || codecRestarting) {
            return;
        }
        codecRestarting = true;
        new Thread(() -> {
            synchronized (codecRestartLock) {
                try {
                    Log.i(TAG, "Restarting decoder to enable HardwareBuffer output (zero-copy) after CPU first frame...");
                    stopDecoderOutputDrainer();

                    try {
                        if (decoder != null) {
                            try { decoder.stop(); } catch (Exception ignored) {}
                            try { decoder.release(); } catch (Exception ignored) {}
                            decoder = null;
                        }
                    } catch (Exception ignored) {
                    }

                    try {
                        if (imageReader != null) {
                            imageReader.close();
                            imageReader = null;
                        }
                    } catch (Exception ignored) {
                    }

                    try {
                        if (surface != null) {
                            surface.release();
                            surface = null;
                        }
                    } catch (Exception ignored) {
                    }

                    try {
                        if (handlerThread != null) {
                            handlerThread.quitSafely();
                            handlerThread = null;
                            handler = null;
                        }
                    } catch (Exception ignored) {
                    }

                    startDecoder();

                    // Replay the exact CPU-first-frame into the HardwareBuffer decoder so Unity can calibrate GPU YUV conversion
                    // against the same content (otherwise the stream advances and calibration compares mismatched frames).
                    if (calibrationObuReady && calibrationObuFrame != null && calibrationObuFrame.length > 0) {
                        Log.i(TAG, "Replaying calibration OBU into hardware decoder (bytes=" + calibrationObuFrame.length + ")");
                        // Ensure the replayed frame carries the same packet header, so Unity can match CPU/GPU reference reliably.
                        if (calibrationPacketHeader != null) {
                            try {
                                pendingHeaders.add(calibrationPacketHeader);
                            } catch (Exception ignored) {
                            }
                        }
                        submitObuFrame(calibrationObuFrame);
                    } else {
                        Log.w(TAG, "Calibration OBU not available; Unity GPU auto-calibration will compare mismatched content.");
                    }
                } finally {
                    codecRestarting = false;
                }
            }
        }, "DecoderUpgrade").start();
    }

    private void printPeriodicStats() {
        long now = System.currentTimeMillis();
        if (now - lastStatsTime > STATS_INTERVAL_MS) {
            long elapsed = now - lastStatsTime;

            long dPackets = packetsReceived - lastStatsPacketsReceived;
            long dBytes = bytesReceived - lastStatsBytesReceived;
            long dReassembled = framesReassembled - lastStatsFramesReassembled;
            long dSubmitted = nalSubmitted - lastStatsNalSubmitted;
            long dDecoded = frameCounter - lastStatsFrameCounter;
            long dEnqueued = framesEnqueued - lastStatsFramesEnqueued;
            long dDequeued = framesDequeued - lastStatsFramesDequeued;

            float packetsPerSec = (dPackets * 1000f) / elapsed;
            float bytesPerSec = (dBytes * 1000f) / elapsed;
            float reassembledPerSec = (dReassembled * 1000f) / elapsed;
            float submittedPerSec = (dSubmitted * 1000f) / elapsed;
            float decodedPerSec = (dDecoded * 1000f) / elapsed;
            float enqueuedPerSec = (dEnqueued * 1000f) / elapsed;
            float dequeuedPerSec = (dDequeued * 1000f) / elapsed;

            Log.i(TAG, String.format(
                "Stats: Packets=%.1f/s, Bytes=%.1f KB/s, Reassembled=%.1f/s, Submitted=%.1f/s, Decoded=%.1f/s, Enqueued=%.1f/s, Dequeued=%.1f/s, Queue=%d, NoInputBuf=%d, FmtHdrVer=%d, FmtHdrBytes=%d",
                packetsPerSec, bytesPerSec / 1024, reassembledPerSec, submittedPerSec, decodedPerSec, enqueuedPerSec, dequeuedPerSec, frameQueue.size(), noInputBufferCount
                , av1FormatHeaderVersion, latestAv1FormatHeaderBytes
            ));

            lastStatsTime = now;
            lastStatsPacketsReceived = packetsReceived;
            lastStatsBytesReceived = bytesReceived;
            lastStatsFramesReassembled = framesReassembled;
            lastStatsNalSubmitted = nalSubmitted;
            lastStatsFrameCounter = frameCounter;
            lastStatsFramesEnqueued = framesEnqueued;
            lastStatsFramesDequeued = framesDequeued;
        }
    }

    private void printStats() {
        // 计算重组成功率
        double reassemblyRate = framesReceived > 0 ? (framesReassembled * 100.0) / framesReceived : 0;
        
        Log.i(TAG, String.format(
            "=== Statistics ===\n" +
            "Packets received: %d\n" +
            "Bytes received: %d\n" +
            "Frames received: %d\n" +
            "Frames reassembled: %d (%.1f%%)\n" +
            "NAL units submitted: %d\n" +
            "Frames decoded: %d\n" +
            "Frames enqueued: %d\n" +
            "Frames dequeued: %d\n" +
            "No input buffer: %d\n" +
            "AV1 format header: version=%d bytes=%d crc32=0x%s frameId=%d\n" +
            "YUV->RGBA avg ms: %.2f (max %.2f, n=%d)",
            packetsReceived, bytesReceived, framesReceived,
            framesReassembled, reassemblyRate, 
            nalSubmitted, frameCounter,
            framesEnqueued, framesDequeued, noInputBufferCount,
            av1FormatHeaderVersion, latestAv1FormatHeaderBytes, Long.toHexString(latestAv1FormatHeaderCrc), latestAv1FormatHeaderFrameId,
            (yuvToRgbaConvertCount > 0 ? (yuvToRgbaConvertNsTotal / (double)yuvToRgbaConvertCount) / 1_000_000.0 : 0.0),
            (yuvToRgbaConvertNsMax / 1_000_000.0),
            yuvToRgbaConvertCount
        ));
    }
}
