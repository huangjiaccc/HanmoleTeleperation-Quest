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

import java.io.IOException;
import java.io.FileDescriptor;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.SocketException;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Arrays;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.TreeMap;
import java.util.HashSet;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import java.util.List;
import java.util.ArrayList;
import java.util.zip.CRC32;

@RequiresApi(api = Build.VERSION_CODES.LOLLIPOP)
public class H264StreamingDecoder implements ImageReader.OnImageAvailableListener {
    private static final String TAG = "H264StreamingDecoder";
    private static final int MAX_QUEUE = 24;
    // Keep only the newest HardwareBuffer frame queued in Java. Holding onto more decoded Images
    // here leaves no headroom for acquireLatestImage() and can saturate ImageReader(maxImages=3).
    private static final int MAX_HARDWARE_FRAME_QUEUE = 1;
    private static final int HARDWARE_IMAGE_READER_DEPTH = 5;
    private static final int CPU_IMAGE_READER_DEPTH = 4;
    private static final int MAX_RELEASE_FRAME_ASSEMBLERS = 6;
    private static final int MAX_RELEASE_ASSEMBLY_BYTES = 6 * 1024 * 1024;
    private static final int MAX_RELEASE_FRAME_BYTES = 2 * 1024 * 1024;
    private static final int FENCE_WAIT_TIMEOUT_MS = 10;
    private static final int FENCE_RESULT_WAIT_FAILED = -2;
    private static final byte[] START_CODE = new byte[]{0, 0, 0, 1};
    private static final int RELEASE_PACKET_HEADER_SIZE = Integer.BYTES * 8;

    // 修正头部结构以匹配发送端的8字节格式
    private InetSocketAddress createBindAddress(String address, int port) throws IOException {
        if (address == null || address.isEmpty() || "0.0.0.0".equals(address) || "::".equals(address)) {
            InetAddress ipv4Any = InetAddress.getByAddress(new byte[]{0, 0, 0, 0});
            return new InetSocketAddress(ipv4Any, port);
        }
        InetAddress bindIp = InetAddress.getByName(address);
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

    private final ConcurrentLinkedQueue<FrameBundle> frameQueue = new ConcurrentLinkedQueue<>();
    private final ConcurrentLinkedQueue<HardwareBufferFrame> hardwareFrameQueue = new ConcurrentLinkedQueue<>();
    private final ConcurrentLinkedQueue<int[]> pendingHeaders = new ConcurrentLinkedQueue<>();
    private final ConcurrentHashMap<Integer, ReleaseFrameAssembler> releaseFrameAssemblers = new ConcurrentHashMap<>();

    private final java.util.List<DatagramSocket> sockets = new ArrayList<>();
    private final java.util.List<Thread> receiverThreads = new ArrayList<>();
    private DatagramSocket primarySocket;
    private DatagramSocket heartbeatSocket;
    private Thread heartbeatThread;
    private volatile boolean heartbeatRunning;
    private volatile byte[] heartbeatPayload;
    private volatile int heartbeatPayloadLength;
    private volatile InetSocketAddress lastPacketEndpoint;
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
    private boolean useHardwareBufferFrames;
    private volatile boolean nativeHardwareBufferImporterEnabled;
    private volatile boolean decoderUsingHardwareBuffers;
    private volatile boolean unityAutoCalibrationEnabled;
    private volatile boolean nativeColorInfoHandoffEnabled;

    private volatile boolean running;
    private byte[] sps;
    private byte[] pps;
    private byte[] activeSps;
    private byte[] activePps;
    private boolean verbose;
    private boolean debugChecksums;
    private long frameCounter;
    private long packetsReceived;
    private long nalSubmitted;
        private long framesEnqueued;
    private long framesDequeued;
    private int consecutiveFenceTimeouts;
    private long lastFenceTimeoutLogMs;

    private long bytesReceived;
    private long framesReceived;
    private long framesReassembled;
    private final java.util.concurrent.atomic.AtomicLong lastImageTimestampMs = new java.util.concurrent.atomic.AtomicLong(0);
    private long lastNoFrameLogMs = 0;
    private long lastUnexpectedCpuFallbackLogMs = 0;
    private long lastCpuFrameDropLogMs = 0;
    private long lastHardwareFrameDropLogMs = 0;
    private volatile int lastSentColorStandard = Integer.MIN_VALUE;
    private volatile int lastSentColorRange = Integer.MIN_VALUE;
    private volatile int lastSentColorTransfer = Integer.MIN_VALUE;
    private volatile int lastSentColorFormat = Integer.MIN_VALUE;
    private volatile boolean nativeBridgeAvailable = true;
    private volatile boolean nativeBridgeLoggedUnavailable = false;

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

    // 分片重组器：按frameId重组NAL单元
    private static class NalAssembler {
        final int frameId;
        final int totalFragments;
        final TreeMap<Integer, byte[]> fragments = new TreeMap<>();
        int receivedFragments = 0;
        int totalBytes = 0;
        final int nalType;
        final long firstReceivedTime;
        long lastReceivedTime;

        NalAssembler(int frameId, int totalFragments, int nalType) {
            this.frameId = frameId;
            this.totalFragments = totalFragments;
            this.nalType = nalType;
            this.firstReceivedTime = System.currentTimeMillis();
            this.lastReceivedTime = System.currentTimeMillis();
        }

        boolean addFragment(int fragmentIndex, byte[] data) {
            if (fragmentIndex < 0 || fragmentIndex >= totalFragments) {
                return false; // 无效的分片索引
            }
            
            if (fragments.containsKey(fragmentIndex)) {
                return false; // 已收到该分片
            }
            
            if (data == null || data.length == 0) {
                return false; // 无效的数据
            }
            
            fragments.put(fragmentIndex, data);
            receivedFragments++;
            totalBytes += data.length;
            lastReceivedTime = System.currentTimeMillis();
            return true;
        }

        boolean isComplete() {
            return receivedFragments == totalFragments;
        }

        byte[] assemble() {
            if (!isComplete()) {
                return null;
            }

            // 验证所有分片都已正确接收
            if (fragments.size() != totalFragments) {
                return null;
            }

            // 检查分片索引是否连续且对齐
            int expectedIndex = 0;
            for (Map.Entry<Integer, byte[]> entry : fragments.entrySet()) {
                int actualIndex = entry.getKey();
                if (actualIndex != expectedIndex) {
                    return null; // 分片索引不连续
                }
                if (actualIndex < 0 || actualIndex >= totalFragments) {
                    return null; // 分片索引超出范围
                }
                expectedIndex++;
            }

            // 确保总字节数对齐
            if (totalBytes <= 0) {
                return null;
            }

            // NOTE: Returned buffer is rented from ByteArrayPool; caller must ByteArrayPool.give(...) after use.
            byte[] nal = ByteArrayPool.rent(totalBytes);
            int offset = 0;
            for (Map.Entry<Integer, byte[]> entry : fragments.entrySet()) {
                byte[] fragment = entry.getValue();
                if (fragment == null || fragment.length == 0) {
                    ByteArrayPool.give(nal);
                    release();
                    return null; // 无效的分片数据
                }
                if (offset + fragment.length > nal.length) {
                    ByteArrayPool.give(nal);
                    release();
                    return null; // 缓冲区溢出保护
                }
                System.arraycopy(fragment, 0, nal, offset, fragment.length);
                offset += fragment.length;
            }
            
            // 验证最终偏移量对齐
            if (offset != totalBytes) {
                ByteArrayPool.give(nal);
                release();
                return null; // 字节数不匹配
            }
            
            release();
            return nal;
        }

        boolean isExpired() {
            return System.currentTimeMillis() - firstReceivedTime > 5000; // 增加到5秒超时
        }
        
        boolean isStalled() {
            return System.currentTimeMillis() - lastReceivedTime > 2000; // 增加到2秒无新数据
        }
        
        int getProgress() {
            return (int)((receivedFragments * 100.0) / totalFragments);
        }

        void release() {
            for (byte[] fragment : fragments.values()) {
                ByteArrayPool.give(fragment);
            }
            fragments.clear();
            receivedFragments = 0;
            totalBytes = 0;
        }
    }

    // 存储正在重组的NAL单元，key为frameId
    private final ConcurrentHashMap<Integer, NalAssembler> assemblingNals = new ConcurrentHashMap<>();

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

    public H264StreamingDecoder(int width, int height, String listenAddress, int port, int secondaryPort, String udpRemoteHost, int udpRemotePort, String expectedSenderIp) {
        this.width = width;
        this.height = height;
        this.port = port;
        this.secondaryPort = secondaryPort;
        this.listenAddress = (listenAddress == null || listenAddress.isEmpty()) ? "0.0.0.0" : listenAddress;
        this.udpRemoteHost = (udpRemoteHost == null) ? "" : udpRemoteHost;
        this.udpRemotePort = udpRemotePort;
        this.expectedSenderIp = (expectedSenderIp == null || expectedSenderIp.isEmpty()) ? null : expectedSenderIp;
        this.configuredRemoteEndpoint = createRemoteEndpoint(this.udpRemoteHost, this.udpRemotePort);
    }

    public H264StreamingDecoder(int width, int height, String listenAddress, int port, int secondaryPort, String expectedSenderIp) {
        this(width, height, listenAddress, port, secondaryPort, "", 0, expectedSenderIp);
    }

    public H264StreamingDecoder(int width, int height, int port) {
        this(width, height, "0.0.0.0", port, 0, "", 0, null);
    }

    public H264StreamingDecoder(int width, int height, String listenAddress, int port) {
        this(width, height, listenAddress, port, 0, "", 0, null);
    }

    public H264StreamingDecoder(int width, int height, String listenAddress, int port, String expectedSenderIp) {
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

    public synchronized void setUnityAutoCalibrationEnabled(boolean enable) {
        unityAutoCalibrationEnabled = enable;
        if (enable) {
            Log.i(TAG, "CPU-first-frame calibration request ignored for H264; decoder remains GPU-only.");
        }
    }

    public synchronized void setNativeColorInfoHandoffEnabled(boolean enable) {
        nativeColorInfoHandoffEnabled = enable;
        if (!enable) {
            lastSentColorStandard = Integer.MIN_VALUE;
            lastSentColorRange = Integer.MIN_VALUE;
            lastSentColorTransfer = Integer.MIN_VALUE;
            lastSentColorFormat = Integer.MIN_VALUE;
        }
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

    public int getAv1FormatHeaderVersion() {
        return -1;
    }

    public int getAv1FormatHeaderSizeBytes() {
        return -1;
    }

    public long getAv1FormatHeaderCrc32() {
        return -1L;
    }

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

    public synchronized void submitHeartbeatPayload(byte[] payload, int length) {
        if (payload == null || length <= 0) {
            heartbeatPayload = null;
            heartbeatPayloadLength = 0;
            return;
        }
        if (length > payload.length) {
            length = payload.length;
        }
        heartbeatPayload = Arrays.copyOf(payload, length);
        heartbeatPayloadLength = length;
    }

    private void logDebug(String message) {
        if (!verbose) {
            return;
        }
        Log.d(TAG, message);
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

    public void start() {
        stop();
        running = true;
        logDebug("=== Starting streaming decoder (" + width + "x" + height + ") on UDP port " + port + " ===");
        
        checkNetworkConnectivity();
        startUdpReceivers();
        startDecoder();
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
        if (heartbeatRunning) {
            return;
        }

        heartbeatRunning = true;
        heartbeatThread = new Thread(() -> {
            while (heartbeatRunning && running) {
                try {
                    Thread.sleep(3000);
                } catch (InterruptedException ex) {
                    break;
                }

                byte[] payload = heartbeatPayload;
                int length = heartbeatPayloadLength;
                InetSocketAddress target = configuredRemoteEndpoint != null ? configuredRemoteEndpoint : lastPacketEndpoint;
                if (payload == null || length <= 0 || target == null) {
                    continue;
                }

                DatagramSocket socket = primarySocket;
                if (socket == null || socket.isClosed()) {
                    continue;
                }

                try {
                    DatagramPacket packet = new DatagramPacket(payload, length, target.getAddress(), target.getPort());
                    socket.send(packet);
                } catch (Exception ex) {
                    Log.w(TAG, "Heartbeat send failed", ex);
                }
            }
        }, "UdpHeartbeatSender");
        heartbeatThread.setDaemon(true);
        heartbeatThread.start();
    }

    private void stopHeartbeatSender() {
        heartbeatRunning = false;
        if (heartbeatThread != null) {
            heartbeatThread.interrupt();
            try {
                heartbeatThread.join(500);
            } catch (InterruptedException ignored) {
            }
            heartbeatThread = null;
        }
        lastPacketEndpoint = null;
    }

    public void stop() {
        running = false;
        logDebug("=== Stopping streaming decoder ===");
        
        printStats();

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
        sps = null;
        pps = null;
        activeSps = null;
        activePps = null;
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
        for (NalAssembler assembler : assemblingNals.values()) {
            assembler.release();
        }
        assemblingNals.clear();
        for (ReleaseFrameAssembler assembler : releaseFrameAssemblers.values()) {
            assembler.release();
        }
        releaseFrameAssemblers.clear();
        lastImageTimestampMs.set(0);
        lastNoFrameLogMs = 0;
    }

    public void release() {
        stop();
    }

    public FrameBundle dequeueFrameBundle() {
        if (verbose) {
            Log.i(TAG, "dequeueFrameBundle called - current queue size: " + frameQueue.size() + 
                    ", frameCounter: " + frameCounter + ", nalSubmitted: " + nalSubmitted);
        }
        FrameBundle bundle = frameQueue.poll();
        if (bundle != null && bundle.getImage() != null) {
            framesDequeued++;

            if (verbose) {
                Log.i(TAG, "dequeueFrameBundle: returned frame of size " + bundle.getImage().length + 
                        " bytes, remaining queue size: " + frameQueue.size());
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
        return decoderUsingHardwareBuffers ? hardwareFrameQueue.size() : frameQueue.size();
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
        if (!running) {
            return;
        }

        if (decoder != null) {
            return;
        }

        if (!hasCodecConfig()) {
            Log.i(TAG, "Decoder start deferred until SPS/PPS are received.");
            return;
        }

        handlerThread = new HandlerThread("DecoderImageReaderThread");
        handlerThread.start();
        handler = new Handler(handlerThread.getLooper());

        if (imageReader != null) {
            try {
                imageReader.close();
            } catch (Exception ignored) {
            }
            imageReader = null;
        }

        final boolean hardwareBuffersEnabled = useHardwareBufferFrames && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q;
        decoderUsingHardwareBuffers = hardwareBuffersEnabled;
        final int outputFormat = hardwareBuffersEnabled ? ImageFormat.PRIVATE : ImageFormat.YUV_420_888;
        final int maxImages = hardwareBuffersEnabled ? HARDWARE_IMAGE_READER_DEPTH : CPU_IMAGE_READER_DEPTH;

        if (hardwareBuffersEnabled) {
            long usage = 0;
            try {
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
        if (imageReader != null) {
            imageReader.setOnImageAvailableListener(this, handler);
            surface = imageReader.getSurface();
        } else {
            surface = null;
        }

        try {
            decoder = MediaCodec.createDecoderByType("video/avc");
        } catch (IOException e) {
            Log.e(TAG, "Unable to create decoder", e);
            releaseDecoderResourcesForRetry();
            return;
        }

        MediaFormat format = MediaFormat.createVideoFormat("video/avc", width, height);
        // 使用更兼容的YUV420格式，避免格式不匹配问题
        format.setInteger(
                MediaFormat.KEY_COLOR_FORMAT,
                hardwareBuffersEnabled
                        ? MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface
                        : MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible);
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, width * height * 3 / 2);
        // 添加关键的解码器配置参数
        applyRealtimeDecoderHints(format);
        
        if (sps != null && pps != null) {
            // 确保SPS/PPS数据正确配置
            logDebug(String.format("Configuring decoder with SPS(%d bytes) and PPS(%d bytes)", sps.length, pps.length));
            
            // 检查SPS/PPS的有效性
            if (sps.length >= 4 && pps.length >= 1) {
                // 创建对齐的CSD缓冲区
                format.setByteBuffer("csd-0", ByteBuffer.wrap(withStartCode(sps)));
                format.setByteBuffer("csd-1", ByteBuffer.wrap(withStartCode(pps)));
                
                // 添加额外的配置参数
                format.setInteger("csd-valid", 1);
                logDebug("Successfully configured decoder with valid SPS/PPS");
            } else {
                Log.w(TAG, "Invalid SPS/PPS data - SPS: " + (sps != null ? sps.length : 0) + 
                        ", PPS: " + (pps != null ? pps.length : 0));
            }
        } else {
            Log.w(TAG, "No SPS/PPS available for decoder configuration");
        }

        try {
            decoder.configure(format, surface, null, 0);
            decoder.start();
            startDecoderOutputDrainer();
            activeSps = sps != null ? Arrays.copyOf(sps, sps.length) : null;
            activePps = pps != null ? Arrays.copyOf(pps, pps.length) : null;
            frameCounter = 0;
            framesEnqueued = 0;
            framesDequeued = 0;
            packetsReceived = 0;
            bytesReceived = 0;
            framesReassembled = 0;
            lastStatsTime = System.currentTimeMillis();
            logDebug("MediaCodec configured for UDP stream (ImageReader format=" + outputFormat
                    + ", maxImages=" + maxImages
                    + ", hardwareBuffers=" + hardwareBuffersEnabled + ").");
        } catch (Exception e) {
            Log.e(TAG, "Failed to configure decoder", e);
            releaseDecoderResourcesForRetry();
            Log.w(TAG, "Hardware decode configure failed. Keeping H264 on HardwareBuffer path and waiting for restart.");
        }
    }

    private boolean hasCodecConfig() {
        return sps != null && pps != null && sps.length >= 4 && pps.length >= 1;
    }

    private void applyRealtimeDecoderHints(MediaFormat format) {
        if (format == null) {
            return;
        }

        try {
            format.setInteger(MediaFormat.KEY_PRIORITY, 0);
        } catch (Throwable ignored) {
        }

        try {
            format.setInteger(MediaFormat.KEY_OPERATING_RATE, 60);
        } catch (Throwable ignored) {
        }

        try {
            format.setInteger(MediaFormat.KEY_FRAME_RATE, 30);
        } catch (Throwable ignored) {
        }

        try {
            format.setInteger("low-latency", 1);
        } catch (Throwable ignored) {
        }
    }

    private void releaseDecoderResourcesForRetry() {
        stopDecoderOutputDrainer();

        if (decoder != null) {
            try {
                decoder.stop();
            } catch (Exception ignored) {
            }
            try {
                decoder.release();
            } catch (Exception ignored) {
            }
            decoder = null;
        }

        if (handlerThread != null) {
            try {
                handlerThread.quitSafely();
            } catch (Exception ignored) {
            }
            handlerThread = null;
            handler = null;
        }

        if (imageReader != null) {
            try {
                imageReader.close();
            } catch (Exception ignored) {
            }
            imageReader = null;
        }

        if (surface != null) {
            try {
                surface.release();
            } catch (Exception ignored) {
            }
            surface = null;
        }

        activeSps = null;
        activePps = null;

        FrameBundle pendingBundle;
        while ((pendingBundle = frameQueue.poll()) != null) {
            pendingBundle.release();
        }
        HardwareBufferFrame pendingHardware;
        while ((pendingHardware = hardwareFrameQueue.poll()) != null) {
            pendingHardware.release();
        }
        pendingHeaders.clear();
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
        Log.i(TAG, "UDP receiver loop started on port " + bindPort);
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
                    // Log.i(TAG, String.format(
                    //         "Received UDP video packet from %s:%d length=%d localPort=%d",
                    //         remoteIp,
                    //         remotePort,
                    //         packet.getLength(),
                    //         bindPort));
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
                        Log.w(TAG, "No packets received for " + timeSinceLastPacket + "ms on port " + bindPort);
                        consecutiveTimeouts = 0;
                    }
                }
            } catch (OutOfMemoryError oom) {
                Log.e(TAG, "OutOfMemoryError in receive loop on port " + bindPort + ". Clearing release assemblers and byte pools.", oom);
                handleMemoryPressure("receiveLoop");
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

        if (header.fragmentSize > MAX_RELEASE_FRAME_BYTES) {
            Log.w(TAG, "Dropping release packet fragment because it exceeds per-frame budget: " + header.fragmentSize);
            return;
        }

        enforceReleaseFrameBudget();

        byte[] payload = ByteArrayPool.rent(header.fragmentSize);
        System.arraycopy(data, RELEASE_PACKET_HEADER_SIZE, payload, 0, header.fragmentSize);

        ReleaseFrameAssembler frameAssembler = releaseFrameAssemblers.computeIfAbsent(
            header.frameId,
            id -> new ReleaseFrameAssembler(header.frameId, header.totalSplits, header.timestamp, header.testingId)
        );

        if (frameAssembler.getBufferedBytes() + header.fragmentSize > MAX_RELEASE_FRAME_BYTES) {
            Log.w(TAG, "Dropping oversized release frame " + header.frameId
                    + " bufferedBytes=" + frameAssembler.getBufferedBytes()
                    + " incoming=" + header.fragmentSize);
            releaseFrameAssemblers.remove(header.frameId);
            frameAssembler.release();
            ByteArrayPool.give(payload);
            return;
        }

        frameAssembler.updateMetadata(header);
        ReleaseSplitAssembler splitAssembler = frameAssembler.getOrCreateSplit(header);
        if (!splitAssembler.addFragment(header.fragmentId, payload)) {
            ByteArrayPool.give(payload);
            return;
        }

        enforceReleaseFrameBudget();

        if (splitAssembler.isComplete()) {
            frameAssembler.markSplitComplete(splitAssembler.splitId);
        }

        if (frameAssembler.isComplete()) {
            try {
                submitReleaseFrame(frameAssembler);
            } finally {
                releaseFrameAssemblers.remove(header.frameId);
                frameAssembler.release();
            }
        }

        cleanupReleaseFrameAssemblers();
    }

    private void cleanupExpiredAssemblers() {
        List<Integer> toRemove = new ArrayList<>();
        for (Map.Entry<Integer, NalAssembler> entry : assemblingNals.entrySet()) {
            NalAssembler assembler = entry.getValue();
            if (assembler.isExpired()) {
                toRemove.add(entry.getKey());
                logDebug(String.format(
                    "Cleaning up expired NAL assembler - frameId: %d, progress: %d%%, fragments: %d/%d, stalled: %b",
                    entry.getKey(), assembler.getProgress(), assembler.receivedFragments, 
                    assembler.totalFragments, assembler.isStalled()
                ));
                assembler.release();
            } else if (assembler.isStalled()) {
                // 对于停滞的重组器，不立即清理，而是记录警告
                logDebug(String.format(
                    "NAL assembler stalled - frameId: %d, progress: %d%%, fragments: %d/%d, last received: %dms ago",
                    entry.getKey(), assembler.getProgress(), assembler.receivedFragments, 
                    assembler.totalFragments, System.currentTimeMillis() - assembler.lastReceivedTime
                ));
                // 如果进度超过50%，尝试等待更多时间
                if (assembler.getProgress() > 50) {
                    logDebug("High progress stalled assembler, keeping for potential recovery");
                }
            }
        }
        for (int frameId : toRemove) {
            assemblingNals.remove(frameId);
        }
        
        // 定期输出重组状态统计
        if (assemblingNals.size() > 0 && System.currentTimeMillis() - lastStatsTime > 2000) {
            logDebug(String.format(
                "Active NAL assemblers: %d, total fragments received: %d, frames reassembled: %d, success rate: %.1f%%",
                assemblingNals.size(), framesReceived, framesReassembled,
                framesReceived > 0 ? (framesReassembled * 100.0 / framesReceived) : 0.0
            ));
            lastStatsTime = System.currentTimeMillis();
        }
    }

    private void submitReleaseFrame(ReleaseFrameAssembler assembler) {
        List<byte[]> nalUnits;
        try {
            nalUnits = assembler.buildNalUnits();
        } catch (OutOfMemoryError oom) {
            Log.e(TAG, "OutOfMemoryError while assembling release frame " + assembler.frameId + ". Dropping buffered release state.", oom);
            handleMemoryPressure("submitReleaseFrame");
            return;
        }
        if (nalUnits.isEmpty()) {
            logDebug("Release frame " + assembler.frameId + " has no NAL units");
            return;
        }


        for (byte[] annexBNal : nalUnits) {
            if (annexBNal == null || annexBNal.length == 0) {
                continue;
            }
            try {
                if (!dispatchFramePayloadNalUnits(annexBNal)) {
                    dispatchSingleNalPayload(annexBNal, 0, annexBNal.length);
                }
            } finally {
                ByteArrayPool.give(annexBNal);
            }
        }
        enqueueFrameHeader(createReleasePacketHeaderArray(assembler.getLatestHeader()));

        framesReassembled++;
        framesReceived++;
    }

    private void cleanupReleaseFrameAssemblers() {
        long now = System.currentTimeMillis();
        List<Integer> stale = new ArrayList<>();
        for (Map.Entry<Integer, ReleaseFrameAssembler> entry : releaseFrameAssemblers.entrySet()) {
            if (now - entry.getValue().createdAtMs > 5000) {
                stale.add(entry.getKey());
            }
        }
        for (int frameId : stale) {
            ReleaseFrameAssembler assembler = releaseFrameAssemblers.remove(frameId);
            if (assembler != null) {
                assembler.release();
            }
            logDebug("Dropping stale release frame " + frameId);
        }

        enforceReleaseFrameBudget();
    }

    private void enforceReleaseFrameBudget() {
        while (releaseFrameAssemblers.size() > MAX_RELEASE_FRAME_ASSEMBLERS
                || getTotalBufferedReleaseBytes() > MAX_RELEASE_ASSEMBLY_BYTES) {
            ReleaseFrameAssembler oldest = null;
            for (ReleaseFrameAssembler candidate : releaseFrameAssemblers.values()) {
                if (candidate == null) {
                    continue;
                }
                if (oldest == null || candidate.createdAtMs < oldest.createdAtMs) {
                    oldest = candidate;
                }
            }

            if (oldest == null) {
                return;
            }

            releaseFrameAssemblers.remove(oldest.frameId);
            Log.w(TAG, "Dropping release frame " + oldest.frameId
                    + " to enforce assembler budget. frames=" + releaseFrameAssemblers.size()
                    + " bufferedBytes=" + getTotalBufferedReleaseBytes());
            oldest.release();
        }
    }

    private void handleMemoryPressure(String reason) {
        clearReleaseFrameAssemblers(reason);
        ByteArrayPool.clearAll();
        System.gc();
    }

    private void clearReleaseFrameAssemblers(String reason) {
        for (ReleaseFrameAssembler assembler : releaseFrameAssemblers.values()) {
            if (assembler != null) {
                assembler.release();
            }
        }
        releaseFrameAssemblers.clear();
        pendingHeaders.clear();
        Log.w(TAG, "Cleared release frame assemblers due to memory pressure: " + reason);
    }

    private int getTotalBufferedReleaseBytes() {
        int total = 0;
        for (ReleaseFrameAssembler assembler : releaseFrameAssemblers.values()) {
            if (assembler != null) {
                total += assembler.getBufferedBytes();
            }
        }
        return total;
    }

    private int findAnnexBStartCodeLength(byte[] data) {
        if (data.length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1) {
            return 4;
        }
        if (data.length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1) {
            return 3;
        }
        return 0;
    }

    private int findAnnexBStartCodeLength(byte[] data, int offset) {
        if (data == null || offset < 0 || offset >= data.length) {
            return 0;
        }
        if (offset + 4 <= data.length
                && data[offset] == 0
                && data[offset + 1] == 0
                && data[offset + 2] == 0
                && data[offset + 3] == 1) {
            return 4;
        }
        if (offset + 3 <= data.length
                && data[offset] == 0
                && data[offset + 1] == 0
                && data[offset + 2] == 1) {
            return 3;
        }
        return 0;
    }

    private boolean dispatchFramePayloadNalUnits(byte[] frameData) {
        if (frameData == null || frameData.length == 0) {
            return false;
        }

        if (dispatchAnnexBNalUnits(frameData)) {
            return true;
        }

        return dispatchLengthPrefixedNalUnits(frameData);
    }

    private boolean dispatchAnnexBNalUnits(byte[] frameData) {
        int firstStart = -1;
        for (int i = 0; i < frameData.length; i++) {
            if (findAnnexBStartCodeLength(frameData, i) > 0) {
                firstStart = i;
                break;
            }
        }

        if (firstStart < 0) {
            return false;
        }

        boolean dispatched = false;
        int nalStart = firstStart;
        while (nalStart < frameData.length) {
            int startCodeLen = findAnnexBStartCodeLength(frameData, nalStart);
            if (startCodeLen <= 0) {
                nalStart++;
                continue;
            }

            int payloadStart = nalStart + startCodeLen;
            int nextStart = frameData.length;
            for (int i = payloadStart; i < frameData.length; i++) {
                if (findAnnexBStartCodeLength(frameData, i) > 0) {
                    nextStart = i;
                    break;
                }
            }

            int payloadLength = nextStart - payloadStart;
            if (payloadLength > 0) {
                dispatchSingleNalPayload(frameData, payloadStart, payloadLength);
                dispatched = true;
            }

            if (nextStart >= frameData.length) {
                break;
            }
            nalStart = nextStart;
        }

        return dispatched;
    }

    private boolean dispatchLengthPrefixedNalUnits(byte[] frameData) {
        int offset = 0;
        boolean dispatched = false;

        while (offset + 4 <= frameData.length) {
            int nalLength = ((frameData[offset] & 0xFF) << 24)
                    | ((frameData[offset + 1] & 0xFF) << 16)
                    | ((frameData[offset + 2] & 0xFF) << 8)
                    | (frameData[offset + 3] & 0xFF);
            offset += 4;

            if (nalLength <= 0 || offset + nalLength > frameData.length) {
                return false;
            }

            dispatchSingleNalPayload(frameData, offset, nalLength);
            dispatched = true;
            offset += nalLength;
        }

        return dispatched && offset == frameData.length;
    }

    private void dispatchSingleNalPayload(byte[] source, int offset, int length) {
        if (source == null || length <= 0 || offset < 0 || offset + length > source.length) {
            return;
        }

        int nalType = source[offset] & 0x1F;
        byte[] payload = ByteArrayPool.rent(length);
        System.arraycopy(source, offset, payload, 0, length);
        try {
            handleNalUnit(payload, nalType);
        } finally {
            ByteArrayPool.give(payload);
        }
    }

    private byte[] withStartCode(byte[] nalPayload) {
        if (nalPayload == null || nalPayload.length == 0) {
            return START_CODE.clone();
        }

        if (findAnnexBStartCodeLength(nalPayload) > 0) {
            return Arrays.copyOf(nalPayload, nalPayload.length);
        }

        byte[] prefixed = new byte[START_CODE.length + nalPayload.length];
        System.arraycopy(START_CODE, 0, prefixed, 0, START_CODE.length);
        System.arraycopy(nalPayload, 0, prefixed, START_CODE.length, nalPayload.length);
        return prefixed;
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
        final int splitId;
        final int totalFragments;
        final ReleasePacketHeader headerSnapshot;
        final byte[][] fragments;
        int receivedFragments = 0;
        int totalBytes = 0;

        ReleaseSplitAssembler(ReleasePacketHeader header) {
            this.splitId = header.splitId;
            this.totalFragments = Math.max(1, header.totalFragments);
            this.headerSnapshot = header;
            this.fragments = new byte[totalFragments][];
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

        synchronized boolean isComplete() {
            return receivedFragments == totalFragments;
        }

        synchronized int getBufferedBytes() {
            return totalBytes;
        }

        synchronized byte[] assemble() {
            if (!isComplete()) {
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
                byte[] fragment = fragments[i];
                if (fragment != null) {
                    ByteArrayPool.give(fragment);
                    fragments[i] = null;
                }
            }
            receivedFragments = 0;
            totalBytes = 0;
        }
    }

    private static class ReleaseFrameAssembler {
        final int frameId;
        final int totalSplits;
        final long timestampMs;
        final long createdAtMs;
        final int testingId;
        final TreeMap<Integer, ReleaseSplitAssembler> splits = new TreeMap<>();
        final HashSet<Integer> completedSplitIds = new HashSet<>();
        int completedSplits = 0;
        ReleasePacketHeader latestHeader;

        ReleaseFrameAssembler(int frameId, int totalSplits, int timestampMs, int testingId) {
            this.frameId = frameId;
            this.totalSplits = Math.max(1, totalSplits);
            this.timestampMs = timestampMs;
            this.createdAtMs = System.currentTimeMillis();
            this.testingId = testingId;
        }

        synchronized ReleaseSplitAssembler getOrCreateSplit(ReleasePacketHeader header) {
            ReleaseSplitAssembler assembler = splits.get(header.splitId);
            if (assembler == null) {
                assembler = new ReleaseSplitAssembler(header);
                splits.put(header.splitId, assembler);
            }
            return assembler;
        }

        synchronized void markSplitComplete(int splitId) {
            if (completedSplitIds.add(splitId)) {
                completedSplits = Math.min(totalSplits, completedSplits + 1);
            }
        }

        synchronized void updateMetadata(ReleasePacketHeader header) {
            if (header != null) {
                this.latestHeader = header;
            }
        }

        synchronized ReleasePacketHeader getLatestHeader() {
            return latestHeader;
        }

        synchronized boolean isComplete() {
            return completedSplits >= totalSplits;
        }

        synchronized int getBufferedBytes() {
            int total = 0;
            for (ReleaseSplitAssembler assembler : splits.values()) {
                total += assembler.getBufferedBytes();
            }
            return total;
        }

        synchronized List<byte[]> buildNalUnits() {
            List<byte[]> nalUnits = new ArrayList<>(splits.size());
            for (ReleaseSplitAssembler assembler : splits.values()) {
                byte[] data = assembler.assemble();
                if (data != null && data.length > 0) {
                    nalUnits.add(data);
                }
            }
            return nalUnits;
        }

        synchronized void release() {
            for (ReleaseSplitAssembler assembler : splits.values()) {
                assembler.releaseFragments();
            }
            splits.clear();
            completedSplitIds.clear();
            completedSplits = 0;
            latestHeader = null;
        }
    }

    private void handleNalUnit(byte[] nalData, int nalType) {
        // 处理SPS和PPS
        if (nalType == 7) { // SPS
            logDebug("Received SPS - length: " + nalData.length);
            sps = Arrays.copyOf(nalData, nalData.length);
            if (pps != null) {
                restartDecoderWithNewParams();
            }
        } else if (nalType == 8) { // PPS
            logDebug("Received PPS - length: " + nalData.length);
            pps = Arrays.copyOf(nalData, nalData.length);
            if (sps != null) {
                restartDecoderWithNewParams();
            }
        }

        // 向解码器提交NAL单元（添加Annex-B起始码）
        if (nalData != null && nalData.length > 0) {
            submitNalToDecoder(nalData);
        }
    }

    private void restartDecoderWithNewParams() {
        if (sps == null || pps == null) return;
        
        // 检查参数是否有变化
        boolean spsChanged = !Arrays.equals(sps, activeSps);
        boolean ppsChanged = !Arrays.equals(pps, activePps);
        
        if (spsChanged || ppsChanged) {
            Log.i(TAG, "SPS/PPS changed, restarting decoder with new parameters");
            releaseDecoderResourcesForRetry();
            startDecoder();
        }
    }

    private void submitNalToDecoder(byte[] nalData) {
        byte[] dataWithStartCode = null;
        try {
            MediaCodec codec = decoder;
            if (codec == null) {
                return;
            }
            // 添加Annex-B起始码并确保4字节对齐
            int totalLength = START_CODE.length + nalData.length;
            int alignedLength = (totalLength + 3) & ~3; // 向上对齐到4字节边界
            
            if (totalLength != alignedLength) {
                logDebug(String.format("NAL data aligned from %d to %d bytes", totalLength, alignedLength));
            }
            
            dataWithStartCode = ByteArrayPool.rent(alignedLength);
            System.arraycopy(START_CODE, 0, dataWithStartCode, 0, START_CODE.length);
            System.arraycopy(nalData, 0, dataWithStartCode, START_CODE.length, nalData.length);
            
            // 填充剩余字节为0以确保对齐
            for (int i = totalLength; i < alignedLength; i++) {
                dataWithStartCode[i] = 0;
            }

            int inputBufferId = codec.dequeueInputBuffer(10000);
            if (inputBufferId >= 0) {
                ByteBuffer inputBuffer = codec.getInputBuffer(inputBufferId);
                if (inputBuffer != null) {
                    // 确保输入数据对齐到4字节边界
                    if (dataWithStartCode.length % 4 != 0) {
                        logDebug(String.format("Input buffer data length %d not aligned to 4 bytes", dataWithStartCode.length));
                    }
                    
                    inputBuffer.clear();
                    inputBuffer.put(dataWithStartCode);
                    codec.queueInputBuffer(
                        inputBufferId,
                        0,
                        dataWithStartCode.length,
                        System.nanoTime() / 1000,
                        0
                    );
                    nalSubmitted++;
                    logDebug("Submitted NAL to decoder - size: " + dataWithStartCode.length + ", total submitted: " + nalSubmitted);
                }
            } else {
                Log.w(TAG, "No input buffer available for NAL submission");
            }
        } catch (Exception e) {
            Log.e(TAG, "Error submitting NAL to decoder", e);
        } finally {
            if (dataWithStartCode != null) {
                ByteArrayPool.give(dataWithStartCode);
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
                MediaCodec codec = decoder;
                if (codec == null) {
                    break;
                }

                int outputBufferId = codec.dequeueOutputBuffer(bufferInfo, 1000);
                if (outputBufferId >= 0) {
                    // 检查解码器输出是否有效
                    // NOTE: For Surface output, BufferInfo.size is often 0 even for valid frames.
                    // Releasing with render=true is what makes frames show up in the ImageReader.
                    boolean isCodecConfig = (bufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                    boolean isEndOfStream = (bufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0;
                    boolean shouldRender = !isCodecConfig && !isEndOfStream;

                    if (shouldRender
                            && decoderUsingHardwareBuffers
                            && hardwareFrameQueue.size() >= MAX_HARDWARE_FRAME_QUEUE) {
                        long now = System.currentTimeMillis();
                        if (now - lastHardwareFrameDropLogMs > 1000) {
                            lastHardwareFrameDropLogMs = now;
                            Log.w(TAG, "HardwareBuffer queue saturated. Dropping decoder output buffer without rendering.");
                        }
                        codec.releaseOutputBuffer(outputBufferId, false);
                        continue;
                    }

                    codec.releaseOutputBuffer(outputBufferId, shouldRender);

                    if (shouldRender) {
                        frameCounter++;
                        lastImageTimestampMs.set(System.currentTimeMillis());
                        logDebug("Rendered frame " + frameCounter + " - flags: " + bufferInfo.flags + ", size: " + bufferInfo.size);
                    } else {
                        logDebug("Skipped non-renderable output buffer - flags: " + bufferInfo.flags + ", size: " + bufferInfo.size);
                    }
                } else if (outputBufferId == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    MediaFormat newFormat = codec.getOutputFormat();
                    Log.i(TAG, "Decoder output format changed: " + newFormat);
                    maybeSendDecoderColorInfo(newFormat, "INFO_OUTPUT_FORMAT_CHANGED");
                    // 记录新的输出格式信息
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
            int currentWidth = image.getWidth();
            int currentHeight = image.getHeight();

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

                Log.w(TAG, "onImageAvailable: hardwareBuffer is null - dropping frame (avoid planes fallback)");
                return;
            }
            
            long now = System.currentTimeMillis();
            if (now - lastUnexpectedCpuFallbackLogMs > 5000) {
                lastUnexpectedCpuFallbackLogMs = now;
                Log.w(TAG, "onImageAvailable: CPU fallback disabled for H264; dropping non-HardwareBuffer frame.");
            }
            return;
            /* byte[] frameData = convertYuvToRgba(image);
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
                while (frameQueue.size() >= MAX_QUEUE) {
                    FrameBundle dropped = frameQueue.poll(); // 移除最旧的帧
                    if (dropped != null) {
                        dropped.release();
                    }
                    Log.w(TAG, "Frame queue full, dropping oldest frame");
                }
                frameQueue.add(bundle);
                framesEnqueued++;

                logDebug("Added frame to queue - size now: " + frameQueue.size() + ", format: RGBA, resolution: " + currentWidth + "x" + currentHeight + ", size: " + frameData.length + " bytes");
            }
            */
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

        // 获取图像尺寸信息
        int width = image.getWidth();
        int height = image.getHeight();

        // 调试信息：记录图像格式和尺寸
        int imageFormat = image.getFormat();
        if (verbose) {
            Log.i(TAG, String.format("convertYuvToRgba: image format=%d, width=%d, height=%d",
                    imageFormat, width, height));
        }

        if (imageFormat == PixelFormat.RGBA_8888) {
            return extractRgba(image);
        }

        Image.Plane[] planes = image.getPlanes();
        if (planes == null || planes.length < 3) {
            Log.w(TAG, String.format("convertYuvToRgba: invalid planes count=%d (format=%d)",
                    planes != null ? planes.length : -1, imageFormat));
            return null;
        }

        // 获取裁剪区域，如果存在的话
        android.graphics.Rect cropRect = image.getCropRect();
        
        if (cropRect == null) {
            cropRect = new android.graphics.Rect(0, 0, width, height);
        }
        return convertYuvToRgbaWithCrop(image, planes, width, height, cropRect);
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

        Log.i(TAG, String.format("YUV plane info: format=%d Y stride=%d pixel=%d; U stride=%d pixel=%d; V stride=%d pixel=%d",
                image.getFormat(), yStride, yPixelStride, uStride, uPixelStride, vStride, vPixelStride));

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
        if (format == null || !nativeColorInfoHandoffEnabled) {
            return;
        }

        if (!useHardwareBufferFrames || !decoderUsingHardwareBuffers) {
            return;
        }

        int standard = getFormatInt(format, MediaFormat.KEY_COLOR_STANDARD, -1);
        int range = getFormatInt(format, MediaFormat.KEY_COLOR_RANGE, -1);
        int transfer = getFormatInt(format, MediaFormat.KEY_COLOR_TRANSFER, -1);
        int colorFormat = getFormatInt(format, MediaFormat.KEY_COLOR_FORMAT, -1);

        if (standard < 0) {
            standard = 2;
        }
        if (range < 0) {
            range = 2;
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

        Log.i(TAG, "Decoder color info (" + reason + ") -> standard=" + standard
                + " range=" + range
                + " transfer=" + transfer
                + " format=" + colorFormat
                + (unityAutoCalibrationEnabled ? " (cpu-calibration-disabled)" : ""));
        logColorInfo(format, "Decoder output color info:");

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

    private void printPeriodicStats() {
        long now = System.currentTimeMillis();
        if (now - lastStatsTime > STATS_INTERVAL_MS) {
            long elapsed = now - lastStatsTime;
            float packetsPerSec = (packetsReceived * 1000f) / elapsed;
            float bytesPerSec = (bytesReceived * 1000f) / elapsed;
            float reassembledPerSec = (framesReassembled * 1000f) / elapsed;
            float submittedPerSec = (nalSubmitted * 1000f) / elapsed;

            Log.i(TAG, String.format(
                "Stats: Packets=%.1f/s, Bytes=%.1f KB/s, Reassembled=%.1f/s, Submitted=%.1f/s, Queue=%d",
                packetsPerSec, bytesPerSec / 1024, reassembledPerSec, submittedPerSec, frameQueue.size()
            ));

            lastStatsTime = now;
        }
    }

    private void printStats() {
        // 计算重组成功率
        double reassemblyRate = framesReceived > 0 ? (framesReassembled * 100.0) / framesReceived : 0;
        
        // 计算当前活跃重组器的平均进度
        double avgProgress = 0;
        if (assemblingNals.size() > 0) {
            int totalProgress = 0;
            for (NalAssembler assembler : assemblingNals.values()) {
                totalProgress += assembler.getProgress();
            }
            avgProgress = totalProgress / (double)assemblingNals.size();
        }
        
        Log.i(TAG, String.format(
            "=== Statistics ===\n" +
            "Packets received: %d\n" +
            "Bytes received: %d\n" +
            "Frames received: %d\n" +
            "Frames reassembled: %d (%.1f%%)\n" +
            "NAL units submitted: %d\n" +
            "Frames decoded: %d\n" +
            "Active NAL assemblers: %d (avg progress: %.1f%%)",
            packetsReceived, bytesReceived, framesReceived,
            framesReassembled, reassemblyRate, 
            nalSubmitted, frameCounter, assemblingNals.size(), avgProgress
        ));
    }
}
