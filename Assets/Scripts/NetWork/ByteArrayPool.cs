using System;
using System.Collections.Concurrent;
using System.Threading;

/// <summary>
/// Shared byte[] pool to reduce allocations for UDP send/receive workloads.
/// </summary>
public static class ByteArrayPool
{
    private const int MaxArraySize = 8*1024 * 1024; // 1 MB
    private const int MaxBuffersPerSize = 32;
    private static readonly ConcurrentDictionary<int, ConcurrentQueue<byte[]>> Pools = new();
    private static readonly ConcurrentDictionary<int, PoolCounter> PoolCounts = new();

    /// <summary>
    /// Rent a buffer with the exact requested length (or a new buffer if none are available).
    /// </summary>
    public static byte[] Rent(int size)
    {
        if (size <= 0)
        {
            size = 1;
        }

        if (size > MaxArraySize)
        {
            return new byte[size];
        }

        var (pool, counter) = GetOrCreatePool(size);
        if (pool.TryDequeue(out var buffer))
        {
            Interlocked.Decrement(ref counter.Value);
            return buffer;
        }

        return new byte[size];
    }

    /// <summary>
    /// Return a buffer previously obtained via <see cref="Rent"/>.
    /// </summary>
    public static void Return(byte[] buffer)
    {
        if (buffer == null || buffer.Length == 0 || buffer.Length > MaxArraySize)
        {
            return;
        }

        var (pool, counter) = GetOrCreatePool(buffer.Length);
        int count = Interlocked.Increment(ref counter.Value);
        if (count > MaxBuffersPerSize)
        {
            Interlocked.Decrement(ref counter.Value);
            return;
        }

        pool.Enqueue(buffer);
    }

    /// <summary>
    /// Clear all pools (mainly for tests / diagnostics).
    /// </summary>
    public static void Clear()
    {
        Pools.Clear();
        PoolCounts.Clear();
    }

    private static (ConcurrentQueue<byte[]>, PoolCounter) GetOrCreatePool(int size)
    {
        var pool = Pools.GetOrAdd(size, _ => new ConcurrentQueue<byte[]>());
        var counter = PoolCounts.GetOrAdd(size, _ => new PoolCounter());
        return (pool, counter);
    }

    private sealed class PoolCounter
    {
        public int Value;
    }
}
