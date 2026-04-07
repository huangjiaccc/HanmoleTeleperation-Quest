package com.example.questdecoder;

import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.atomic.AtomicLong;

final class ByteArrayPool {
    // NOTE: Unity upload buffers (RGBA) can be several megabytes. Keep pooling enabled for larger arrays
    // to avoid per-frame allocations, but cap both per-size retention and total retained bytes.
    private static final int MAX_ARRAY_SIZE = 32 * 1024 * 1024; // 32MB
    private static final long MAX_RETAINED_BYTES = 24L * 1024 * 1024; // 24MB
    private static final int SMALL_ARRAY_LIMIT = 256 * 1024;
    private static final int MEDIUM_ARRAY_LIMIT = 1024 * 1024;
    private static final int MAX_POOL_PER_SIZE_SMALL = 8;
    private static final int MAX_POOL_PER_SIZE_MEDIUM = 4;
    private static final int MAX_POOL_PER_SIZE_LARGE = 2;
    private static final ConcurrentHashMap<Integer, ConcurrentLinkedQueue<byte[]>> POOLS =
            new ConcurrentHashMap<>();
    private static final AtomicLong POOLED_BYTES = new AtomicLong(0);

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
                POOLED_BYTES.addAndGet(-buffer.length);
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
        int maxPoolPerSize;
        if (length <= SMALL_ARRAY_LIMIT) {
            maxPoolPerSize = MAX_POOL_PER_SIZE_SMALL;
        } else if (length <= MEDIUM_ARRAY_LIMIT) {
            maxPoolPerSize = MAX_POOL_PER_SIZE_MEDIUM;
        } else {
            maxPoolPerSize = MAX_POOL_PER_SIZE_LARGE;
        }
        if (queue.size() >= maxPoolPerSize) {
            return;
        }
        while (true) {
            long current = POOLED_BYTES.get();
            long updated = current + length;
            if (updated > MAX_RETAINED_BYTES) {
                return;
            }
            if (POOLED_BYTES.compareAndSet(current, updated)) {
                break;
            }
        }
        queue.offer(buffer);
    }

    static long getPooledBytes() {
        return Math.max(POOLED_BYTES.get(), 0L);
    }

    static void clearAll() {
        POOLS.clear();
        POOLED_BYTES.set(0);
    }
}
