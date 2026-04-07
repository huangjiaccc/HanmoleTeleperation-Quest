using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using Debug = AppLog;

#nullable enable

public class UdpManager : MonoBehaviour
{
    private Socket _socket = null!;
    private byte[] _rxBuffer = null!;

    private IPEndPoint? _txEndpoint;

    private volatile bool _rxEnabled;
    private volatile bool _closed;
    private Thread? _rxThread;

    // Unity 主线程队列
    private readonly ConcurrentQueue<(byte[], IPEndPoint)> _mainThreadQueue = new();
    private readonly ConcurrentQueue<(string message, bool isError)> _logQueue = new();
    private readonly int _maxQueuedPackets;
    private readonly bool _dropOldestOnOverflow;
    private int _queuedPackets;
    private long _droppedPackets;

    public int QueuedPacketCount => Volatile.Read(ref _queuedPackets);
    public long DroppedPacketCount => Interlocked.Read(ref _droppedPackets);


    public Action<byte[], IPEndPoint>? OnDataReceived;

    // ------------------------ 初始化 ------------------------

    public UdpManager(
        int? rxPort = null,
        string rxHost = "0.0.0.0",
        string? txHost = null,
        int? txPort = null,
        int rxBufferSize = 1024 * 1024 * 2,
        bool reuseAddress = true,
        bool enableReceive = true,
        int maxQueuedPackets = 2048,
        bool dropOldestOnOverflow = true)
    {
        if ((txHost == null) != (txPort == null))
            throw new ArgumentException("txHost 和 txPort 必须同时提供或同时为 null");
        if (rxPort == null && txHost == null)
            throw new ArgumentException("必须提供接收端口或发送端点至少一个");

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Blocking = true;
        _socket.ReceiveBufferSize = rxBufferSize;
        _socket.SendBufferSize = rxBufferSize;

        if (reuseAddress)
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        _rxBuffer = new byte[rxBufferSize];
        _maxQueuedPackets = Math.Max(0, maxQueuedPackets);
        _dropOldestOnOverflow = dropOldestOnOverflow;

        bool hasRxPort = rxPort.HasValue;
        _rxEnabled = enableReceive && hasRxPort;

        // 绑定接收端口（可选开启接收线程）
        if (hasRxPort)
        {
            var localEP = new IPEndPoint(IPAddress.Parse(rxHost), rxPort.Value);
            _socket.Bind(localEP);
            Debug.Log($"[UdpManager] 绑定到本地端口: {localEP} (接收{(enableReceive ? "开启" : "关闭")})");
        }

        StartReceiveThread();

        // 设置发送端
        if (txHost != null)
            UpdateTxEndpoint(txHost, txPort!.Value);

        Debug.Log($"[UdpManager] 初始化完成 - 接收:{rxPort.HasValue} 发送:{txHost != null}");
    }

    // ------------------------ 接收 ------------------------

    private void StartReceiveThread()
    {
        if (!_rxEnabled || _closed || _rxThread != null)
        {
            return;
        }

        _rxThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "UdpManagerRx"
        };
        _rxThread.Start();
    }

    private void ReceiveLoop()
    {
        while (!_closed)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                int received = _socket.ReceiveFrom(_rxBuffer, 0, _rxBuffer.Length, SocketFlags.None, ref remote);
                if (received > 0)
                {
                    byte[] data = ByteArrayPool.Rent(received);
                    Buffer.BlockCopy(_rxBuffer, 0, data, 0, received);
                    EnqueuePacket(data, (IPEndPoint)remote);
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
            catch (SocketException sex)
            {
                if (_closed || sex.SocketErrorCode == SocketError.Interrupted || sex.SocketErrorCode == SocketError.OperationAborted)
                {
                    break;
                }
                _logQueue.Enqueue(($"[UdpManager] 接收 Socket 异常: {sex.SocketErrorCode}", true));
            }
            catch (Exception ex)
            {
                if (_closed)
                {
                    break;
                }
                _logQueue.Enqueue(($"[UdpManager] 接收异常: {ex.Message}", true));
            }
        }
    }

    // ------------------------ 主线程分发 ------------------------

    public int DispatchQueuedPackets(int maxPacketsPerDispatch = int.MaxValue, float maxDispatchMilliseconds = float.PositiveInfinity)
    {
        if (maxPacketsPerDispatch <= 0)
        {
            maxPacketsPerDispatch = 1;
        }

        if (maxDispatchMilliseconds <= 0f)
        {
            maxDispatchMilliseconds = 0.01f;
        }

        int dispatchedPackets = 0;
        float startTime = Time.realtimeSinceStartup;

        while (dispatchedPackets < maxPacketsPerDispatch && _mainThreadQueue.TryDequeue(out var pair))
        {
            Interlocked.Decrement(ref _queuedPackets);
            OnDataReceived?.Invoke(pair.Item1, pair.Item2);
            dispatchedPackets++;

            if (!float.IsPositiveInfinity(maxDispatchMilliseconds))
            {
                float elapsedMs = (Time.realtimeSinceStartup - startTime) * 1000f;
                if (elapsedMs >= maxDispatchMilliseconds)
                {
                    break;
                }
            }
        }

        int logBudget = 64;
        while (logBudget > 0 && _logQueue.TryDequeue(out var log))
        {
            if (log.isError)
            {
                Debug.LogWarning(log.message);
            }
            else
            {
                Debug.Log(log.message);
            }

            logBudget--;
        }

        return dispatchedPackets;
    }

    // ------------------------ 发送 ------------------------

    public void Send(byte[] data, IPEndPoint? address = null, int length = -1)
    {
        if (data == null || data.Length == 0) return;
        if (_closed) return;

        var target = address ?? _txEndpoint;
        if (target == null) return;

        int payloadLength = (length > 0 && length <= data.Length) ? length : data.Length;

        try
        {
            _socket.SendTo(data, 0, payloadLength, SocketFlags.None, target);
            //_logQueue.Enqueue(("[UdpManager] 发送：" + payloadLength + "target:" + target, false));
        }
        catch (ObjectDisposedException)
        {
            // Socket 已关闭/释放，通常发生在退出或重载过程中；静默忽略即可
        }
        catch (SocketException sex) when (_closed || sex.SocketErrorCode == SocketError.Interrupted || sex.SocketErrorCode == SocketError.OperationAborted)
        {
            // 关闭过程中可能抛出，静默忽略
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _logQueue.Enqueue(($"[UdpManager] 发送异常: {ex.Message}", true));
        }
    }

    private void EnqueuePacket(byte[] data, IPEndPoint remote)
    {
        if (_maxQueuedPackets <= 0)
        {
            _mainThreadQueue.Enqueue((data, remote));
            Interlocked.Increment(ref _queuedPackets);
            return;
        }

        if (Volatile.Read(ref _queuedPackets) >= _maxQueuedPackets)
        {
            if (_dropOldestOnOverflow && _mainThreadQueue.TryDequeue(out var dropped))
            {
                ByteArrayPool.Return(dropped.Item1);
                Interlocked.Decrement(ref _queuedPackets);
                Interlocked.Increment(ref _droppedPackets);
            }
            else
            {
                ByteArrayPool.Return(data);
                Interlocked.Increment(ref _droppedPackets);
                return;
            }
        }

        _mainThreadQueue.Enqueue((data, remote));
        Interlocked.Increment(ref _queuedPackets);
    }

    public void UpdateTxEndpoint(string host, int port)
    {
        _txEndpoint = new IPEndPoint(IPAddress.Parse(host), port);
        Debug.Log($"[UdpManager] 设置发送端点: {_txEndpoint}");
        // ❗ UDP 多通道接收：不使用 Connect
    }

    // ------------------------ 生命周期 ------------------------

    public void Close()
    {
        if (_closed) return;
        _closed = true;

        try
        {
            _socket?.Close();
            _socket?.Dispose();
        }
        catch { }

        if (_rxThread != null && _rxThread.IsAlive)
        {
            try
            {
                if (!_rxThread.Join(60))
                {
                    _rxThread.Interrupt();
                    _rxThread.Join(20);
                }
            }
            catch { }
        }
        _rxThread = null;

        while (_mainThreadQueue.TryDequeue(out var pair))
        {
            ByteArrayPool.Return(pair.Item1);
            Interlocked.Decrement(ref _queuedPackets);
        }

        Debug.Log("[UdpManager] 已关闭");
    }

    private void OnDestroy()
    {
        Close();
    }
}
