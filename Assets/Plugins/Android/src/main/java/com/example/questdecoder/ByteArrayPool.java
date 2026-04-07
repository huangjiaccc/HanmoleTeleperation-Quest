package com.example.questdecoder;

import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentLinkedQueue;

final class ByteArrayPool {
    // NOTE: Unity upload buffers (RGBA) can be several megabytes. Keep pooling enabled for larger arrays
    // to avoid per-frame allocations, but cap per-size retention to control memory usage.
    private static final int MAX_ARRAY_SIZE = 32 * 1024 * 1024; // 32MB
    private static final int MAX_POOL_PER_SIZE_SMALL = 64;
    private static final int MAX_POOL_PER_SIZE_LARGE = 8;
    private static final ConcurrentHashMap<Integer, ConcurrentLinkedQueue<byte[]>> POOLS =
            new ConcurrentHashMap<>();

    private ByteArrayPool() { }

    static byte[] rent(int size) {
        if (size <= 0) {
            size = 1;
        }
        if (size > MAX_ARRAY_SIZE) {
            return new byte[size];
        }
        ConcurrentLinkedQueue<byte[]> queue = POOLS.get(size);
        if (queue != null) {
            byte[] buffer = queue.poll();
            if (buffer != null) {
                return buffer;
            }
        }
        return new byte[size];
    }

    static void give(byte[] buffer) {
        if (buffer == null) {
            return;
        }
        int length = buffer.length;
        if (length == 0 || length > MAX_ARRAY_SIZE) {
            return;
        }
        ConcurrentLinkedQueue<byte[]> queue = POOLS.computeIfAbsent(length, k -> new ConcurrentLinkedQueue<>());
        int maxPoolPerSize = length <= 1024 * 1024 ? MAX_POOL_PER_SIZE_SMALL : MAX_POOL_PER_SIZE_LARGE;
        if (queue.size() >= maxPoolPerSize) {
            return;
        }
        queue.offer(buffer);
    }
}
