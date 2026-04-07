using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using UnityEngine;
using Debug = AppLog;
using UnityEngine.Networking;
using Quest3VideoPlayer;
#nullable enable
public class NetClient : MonoBehaviour
{
    private enum MessageType
    {
        Unknown = 0,
        RobotState = 1,
        Task = 2,
        RobotJointState = 3,
        TtsList = 4,
        Action = 5,
        Count = 6
    }

    public static NetClient instance;
    private bool connecting = false; // Whether a server connection attempt is already running

    public ServerRobotList serverRobotLists;
    private string robotdataUrl;
    [HideInInspector]
    public UdpManager udpDataManager;
    [HideInInspector]
    public UdpManager udpAudioManager;
    [HideInInspector]
    public HeartbeatMessage _heartbeatMessage;
    

    private readonly float[] _lastProcessTimeByType = new float[(int)MessageType.Count];
    private readonly bool[] _hasLastProcessTime = new bool[(int)MessageType.Count];
    private readonly long[] _lastProcessTicksByType = new long[(int)MessageType.Count];
    private const float MinProcessInterval = 0.02f; // 鏈€灏忓鐞嗛棿闅旓紝50Hz
    private static readonly long MinProcessIntervalTicks = (long)(MinProcessInterval * Stopwatch.Frequency);
    private static readonly byte[] HeartbeatPrefix = { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
    private readonly ConcurrentQueue<QueuedDataPacket> _dataPacketQueue = new();
    private readonly ConcurrentQueue<(string message, bool isError)> _dataLogQueue = new();
    private readonly AutoResetEvent _dataPacketAvailable = new(false);
    private readonly object _pendingDataMessageLock = new();
    private Thread dataPacketThread;
    private volatile bool stopDataPacketThread;
    private int queuedDataPacketCount;
    private long droppedDataPackets;
    private RobotStateMessage _pendingRobotStateMessage;
    private TaskStateMessage _pendingTaskStateMessage;
    private RobotJointStateMessage _pendingRobotJointStateMessage;
    private bool _hasPendingRobotStateMessage;
    private bool _hasPendingTaskStateMessage;
    private bool _hasPendingRobotJointStateMessage;
    private byte[] _heartbeatsenddata;
    public ServerConfigSO configSO;
    private Coroutine heartbeatCoroutine;
    private QuestStreamVideoPlayer cachedVideoPlayer;


    [Header("Video Debug")]
    [SerializeField, Min(0.2f)] private float videoBridgeStatsIntervalSeconds = 1f;

    [Header("UDP Dispatch")]
    [SerializeField, Min(1)] private int maxDataPacketsPerFrame = 256;
    [SerializeField, Min(1)] private int maxAudioPacketsPerFrame = 256;
    [SerializeField, Min(0.1f)] private float maxDataDispatchMilliseconds = 1.5f;
    [SerializeField, Min(0.1f)] private float maxAudioDispatchMilliseconds = 1.5f;

    [Header("UDP Queue")]
    [SerializeField, Min(128)] private int maxDataQueuedPackets = 2048;
    [SerializeField, Min(128)] private int maxAudioQueuedPackets = 2048;
    [SerializeField] private bool dropOldestOnQueueOverflow = true;

    [Header("UDP Debug")]
    [SerializeField] private bool logDataPayload = true;
    [SerializeField] private bool logVideoUdpPackets = false;
    [SerializeField] private bool logUnknownUdpPackets = false;
    [SerializeField] private bool filterUnexpectedDataAndAudioSenders = true;
    [SerializeField, Min(0.1f)] private float unexpectedSenderLogIntervalSeconds = 1f;
    [Header("Data Parse")]
    [SerializeField] private bool processDataJsonOnBackgroundThread = true;
    [SerializeField, Min(128)] private int maxQueuedDataPackets = 2048;
    [SerializeField, Min(1)] private int maxDataLogsPerFrame = 128;
    [SerializeField, Min(4096)] private int maxDataJsonReassemblyBytes = 1024 * 1024;
    [SerializeField, Min(0.1f)] private float robotJointStateLengthLogIntervalSeconds = 0.5f;

    private readonly object _ttsOptionsLock = new();
    private readonly object _actionOptionsLock = new();
    private List<string> _pendingTTSOptions;
    private List<string> _pendingActionOptions;
    private bool _hasPendingTTSUpdate;
    private bool _hasPendingActionUpdate;
    private float _lastUnexpectedSenderLogAt = float.NegativeInfinity;
    private long _droppedUnexpectedSenderPackets;
    private bool _invalidExpectedSenderIpLogged;
    private long _lastRobotJointStateLengthLogTicks;
    private readonly object _dataJsonStreamLock = new();
    // Data UDP is filtered to a single expected sender, so one reusable reassembly buffer
    // avoids the per-packet endpoint string key and List<byte> churn.
    private byte[] _dataJsonReassemblyBuffer = Array.Empty<byte>();
    private int _dataJsonReassemblyCount;
    private int LocalVideoPort => RuntimeServerConfig.curislan ? RuntimeServerConfig.serverVideoPort : RuntimeServerConfig.clientVideoPort;
    private int LocalAudioPort => RuntimeServerConfig.curislan ? RuntimeServerConfig.serverAudioPort : RuntimeServerConfig.clientAudioPort;
    private int LocalDataPort => RuntimeServerConfig.curislan ? RuntimeServerConfig.serverDataPort : RuntimeServerConfig.clientDataPort;
    private int RemoteVideoPort => RuntimeServerConfig.curislan ? RuntimeServerConfig.clientVideoPort : RuntimeServerConfig.serverVideoPort;
    private int RemoteAudioPort => RuntimeServerConfig.curislan ? RuntimeServerConfig.clientAudioPort : RuntimeServerConfig.serverAudioPort;
    private int RemoteDataPort => RuntimeServerConfig.curislan ? RuntimeServerConfig.clientDataPort : RuntimeServerConfig.serverDataPort;

    private struct QueuedDataPacket
    {
        public byte[] Data;
        public int Length;
        public IPEndPoint? SourceEndpoint;
    }

    void Awake()
    {
        instance = this;
        StartCoroutine(CopyFile());
        RuntimeServerConfig.Load(configSO);
        StartDataPacketThread();
    }

    private void OnEnable()
    {
        StartDataPacketThread();
    }

    private void Start()
    {
        NetWorkStart();
    }

    private void OnDisable()
    {
        if (heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
            heartbeatCoroutine = null;
        }
        StopDataPacketThread();
        CloseUdpManagers();
    }

    private void Update()
    {
        udpDataManager?.DispatchQueuedPackets(maxDataPacketsPerFrame, maxDataDispatchMilliseconds);
        udpAudioManager?.DispatchQueuedPackets(maxAudioPacketsPerFrame, maxAudioDispatchMilliseconds);
        ProcessPendingDataMessages();
        FlushDataLogs();
        ProcessPendingTTSOptions();
        ProcessPendingActionOptions();
    }

    private void StartDataPacketThread()
    {
        if (!processDataJsonOnBackgroundThread || dataPacketThread != null)
        {
            return;
        }

        stopDataPacketThread = false;
        dataPacketThread = new Thread(DataPacketThreadLoop)
        {
            IsBackground = true,
            Name = "NetClientDataJson"
        };
        dataPacketThread.Start();
    }

    private void StopDataPacketThread()
    {
        stopDataPacketThread = true;
        try { _dataPacketAvailable.Set(); } catch { }

        if (dataPacketThread != null && dataPacketThread.IsAlive)
        {
            try
            {
                if (!dataPacketThread.Join(80))
                {
                    dataPacketThread.Interrupt();
                    dataPacketThread.Join(20);
                }
            }
            catch { }
        }
        dataPacketThread = null;

        while (_dataPacketQueue.TryDequeue(out var packet))
        {
            if (packet.Data != null)
            {
                ByteArrayPool.Return(packet.Data);
            }
            Interlocked.Decrement(ref queuedDataPacketCount);
        }

        lock (_pendingDataMessageLock)
        {
            _pendingRobotStateMessage = null;
            _pendingTaskStateMessage = null;
            _pendingRobotJointStateMessage = null;
            _hasPendingRobotStateMessage = false;
            _hasPendingTaskStateMessage = false;
            _hasPendingRobotJointStateMessage = false;
        }

        while (_dataLogQueue.TryDequeue(out _)) { }
        lock (_dataJsonStreamLock)
        {
            _dataJsonReassemblyCount = 0;
        }
    }

    private void DataPacketThreadLoop()
    {
        while (!stopDataPacketThread)
        {
            try
            {
                if (!_dataPacketQueue.TryDequeue(out var packet))
                {
                    _dataPacketAvailable.WaitOne(50);
                    continue;
                }

                Interlocked.Decrement(ref queuedDataPacketCount);
                try
                {
                    ProcessIncomingJsonOnBackground(packet.Data, 0, packet.Length, packet.SourceEndpoint);
                }
                finally
                {
                    if (packet.Data != null)
                    {
                        ByteArrayPool.Return(packet.Data);
                    }
                }
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _dataLogQueue.Enqueue(($"[NetClient] Data parser thread exception: {ex.Message}", true));
            }
        }
    }

    private bool EnqueueDataPacketForBackground(byte[] data, int length, IPEndPoint? sourceEndpoint)
    {
        if (!processDataJsonOnBackgroundThread || data == null || length <= 0)
        {
            return false;
        }

        int maxQueued = Mathf.Max(128, maxQueuedDataPackets);
        if (Volatile.Read(ref queuedDataPacketCount) >= maxQueued)
        {
            if (dropOldestOnQueueOverflow && _dataPacketQueue.TryDequeue(out var dropped))
            {
                if (dropped.Data != null)
                {
                    ByteArrayPool.Return(dropped.Data);
                }
                Interlocked.Decrement(ref queuedDataPacketCount);
                Interlocked.Increment(ref droppedDataPackets);
            }
            else
            {
                Interlocked.Increment(ref droppedDataPackets);
                return false;
            }
        }

        _dataPacketQueue.Enqueue(new QueuedDataPacket
        {
            Data = data,
            Length = Mathf.Min(length, data.Length),
            SourceEndpoint = sourceEndpoint
        });
        Interlocked.Increment(ref queuedDataPacketCount);
        _dataPacketAvailable.Set();
        return true;
    }

    private void ProcessIncomingJsonOnBackground(byte[] payload, int offset, int length, IPEndPoint? sourceEndpoint = null)
    {
        ProcessJsonPayload(payload, offset, length, sourceEndpoint, processOnBackgroundThread: true);
    }

    private bool ShouldProcessTypeOnBackground(MessageType messageType)
    {
        if (messageType == MessageType.Unknown)
        {
            return false;
        }

        int index = (int)messageType;
        long now = Stopwatch.GetTimestamp();
        long last = _lastProcessTicksByType[index];
        if (last > 0 && now - last < MinProcessIntervalTicks)
        {
            return false;
        }

        _lastProcessTicksByType[index] = now;
        return true;
    }

    private void DeserializeAndQueueJsonByType(MessageType messageType, string singleJson)
    {
        if (string.IsNullOrWhiteSpace(singleJson))
        {
            return;
        }

        try
        {
            switch (messageType)
            {
                case MessageType.RobotState:
                {
                    RobotStateMessage stateMsg = JsonConvert.DeserializeObject<RobotStateMessage>(singleJson);
                    if (stateMsg != null)
                    {
                        lock (_pendingDataMessageLock)
                        {
                            _pendingRobotStateMessage = stateMsg;
                            _hasPendingRobotStateMessage = true;
                        }
                    }
                    break;
                }
                case MessageType.Task:
                {
                    TaskStateMessage taskMsg = JsonConvert.DeserializeObject<TaskStateMessage>(singleJson);
                    if (taskMsg != null)
                    {
                        lock (_pendingDataMessageLock)
                        {
                            _pendingTaskStateMessage = taskMsg;
                            _hasPendingTaskStateMessage = true;
                        }
                    }
                    break;
                }
                case MessageType.RobotJointState:
                {
                    RobotJointStateMessage jointMsg = JsonConvert.DeserializeObject<RobotJointStateMessage>(singleJson);
                    if (jointMsg != null)
                    {
                        lock (_pendingDataMessageLock)
                        {
                            _pendingRobotJointStateMessage = jointMsg;
                            _hasPendingRobotJointStateMessage = true;
                        }
                    }
                    break;
                }
                case MessageType.TtsList:
                {
                    var ttsListMsg = JsonConvert.DeserializeObject<StringArrayMessage>(singleJson);
                    if (ttsListMsg?.data != null)
                    {
                        QueueTTSOptions(ttsListMsg.data);
                    }
                    break;
                }
                case MessageType.Action:
                {
                    var actionListMsg = JsonConvert.DeserializeObject<StringArrayMessage>(singleJson);
                    if (actionListMsg?.data != null)
                    {
                        QueueActionOptions(actionListMsg.data);
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _dataLogQueue.Enqueue(($"[NetClient] Background JSON parse failed: {ex.Message}", true));
        }
    }

    private void ProcessPendingDataMessages()
    {
        if (DataManager.Instance == null)
        {
            return;
        }

        RobotStateMessage stateMsg = null;
        TaskStateMessage taskMsg = null;
        RobotJointStateMessage jointMsg = null;

        lock (_pendingDataMessageLock)
        {
            if (_hasPendingRobotStateMessage)
            {
                stateMsg = _pendingRobotStateMessage;
                _pendingRobotStateMessage = null;
                _hasPendingRobotStateMessage = false;
            }

            if (_hasPendingTaskStateMessage)
            {
                taskMsg = _pendingTaskStateMessage;
                _pendingTaskStateMessage = null;
                _hasPendingTaskStateMessage = false;
            }

            if (_hasPendingRobotJointStateMessage)
            {
                jointMsg = _pendingRobotJointStateMessage;
                _pendingRobotJointStateMessage = null;
                _hasPendingRobotJointStateMessage = false;
            }
        }

        if (stateMsg != null)
        {
            DataManager.Instance.UpdateRobotState(stateMsg);
        }

        if (taskMsg != null)
        {
            DataManager.Instance.UpdateTaskState(taskMsg);
        }

        if (jointMsg != null)
        {
            DataManager.Instance.UpdateRobotJointState(jointMsg);
        }
    }

    private void FlushDataLogs()
    {
        int budget = Mathf.Max(1, maxDataLogsPerFrame);
        while (budget > 0 && _dataLogQueue.TryDequeue(out var log))
        {
            if (log.isError)
            {
                Debug.LogWarning(log.message);
            }
            else
            {
                Debug.Log(log.message);
            }

            budget--;
        }
    }
    public static IEnumerator CopyFile()
    {
        if (!File.Exists(RuntimeServerConfig.configPath)) 
        {
#if UNITY_ANDROID
            // Android must load this via WWW request
            UnityWebRequest req = UnityWebRequest.Get(RuntimeServerConfig.streamingconfigPath);
            yield return req.SendWebRequest();
            File.WriteAllText(RuntimeServerConfig.configPath, req.downloadHandler.text);
#else
            File.Copy(RuntimeServerConfig.streamingconfigPath, RuntimeServerConfig.configPath);
            yield return null;
#endif
        }
    }

    public void NetWorkStart() 
    {
        StartDataPacketThread();
        InitializeHeartbeatMessage();
        ReloadUdpManagers();
        RestartHeartbeatCoroutine();
    }

    private void RestartHeartbeatCoroutine()
    {
        if (heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
        }

        heartbeatCoroutine = StartCoroutine(HeartbeatSendData());
    }

    private void InitializeHeartbeatMessage()
    {
        _heartbeatMessage = new HeartbeatMessage
        {
            user_email = "chaochen497@gmail.com",
            device_id = SystemInfo.deviceUniqueIdentifier,
            target_device_id = "",
            token = ""
        };
    }

    private IEnumerator HeartbeatSendData()
    {
        while (true)
        {
            PrepareHeartbeatData();
            SendHeartbeat();
            yield return new WaitForSeconds(3);
        }
    }

    private void PrepareHeartbeatData()
    {
        string heartbeatJson = JsonConvert.SerializeObject(_heartbeatMessage);
        byte[] messageBytes = Encoding.UTF8.GetBytes(heartbeatJson);
        int totalLength = HeartbeatPrefix.Length + messageBytes.Length;
        if (_heartbeatsenddata == null || _heartbeatsenddata.Length != totalLength)
        {
            _heartbeatsenddata = new byte[totalLength];
        }

        Buffer.BlockCopy(HeartbeatPrefix, 0, _heartbeatsenddata, 0, HeartbeatPrefix.Length);
        Buffer.BlockCopy(messageBytes, 0, _heartbeatsenddata, HeartbeatPrefix.Length, messageBytes.Length);
    }

    private void SendHeartbeat()
    {
        if (_heartbeatsenddata == null || _heartbeatsenddata.Length == 0)
        {
            Debug.LogError("Heartbeat data is null or empty.");
            return;
        }
        udpDataManager?.Send(_heartbeatsenddata);
        udpAudioManager?.Send(_heartbeatsenddata);
        var videoPlayer = GetQuestStreamVideoPlayerCached();
        if (videoPlayer != null)
        {
            videoPlayer.SubmitVideoHeartbeat(_heartbeatsenddata, _heartbeatsenddata.Length);
        }
        else
        {
            Debug.LogWarning("[NetClient] Video player not found, unable to submit heartbeat to Java decoder");
        }
        //Debug.Log("sendheartbeat");
    }

    private QuestStreamVideoPlayer GetQuestStreamVideoPlayerCached()
    {
        if (cachedVideoPlayer != null)
        {
            return cachedVideoPlayer;
        }

#if UNITY_2023_1_OR_NEWER
        cachedVideoPlayer = UnityEngine.Object.FindFirstObjectByType<QuestStreamVideoPlayer>();
#else
        cachedVideoPlayer = FindObjectOfType<QuestStreamVideoPlayer>();
#endif

        return cachedVideoPlayer;
    }


    public void StartRequest()
    {
        if (connecting) return;
        connecting = true;
        StartCoroutine(RequestGetRobotData());
    }

    private IEnumerator RequestGetRobotData()
    {

        string jsonData = "{\"email\":\"chaochen497@gmail.com\"}";
        UnityWebRequest request = new UnityWebRequest(robotdataUrl, "POST")
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonData)),
            downloadHandler = new DownloadHandlerBuffer()
        };

        SetRequestHeaders(request);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Error: " + request.error);
            connecting = false;
            yield break;
        }

        Debug.Log("Response: " + request.downloadHandler.text);
        ReceiveRobotData(request.downloadHandler.text);
        connecting = false;
    }

    private void SetRequestHeaders(UnityWebRequest request)
    {
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Accept", "*/*");
        request.SetRequestHeader("Host", RuntimeServerConfig.serverIp + ":8080");
        request.SetRequestHeader("User-Agent", "Apifox/1.0.0 (https://apifox.com)");
        request.SetRequestHeader("Connection", "keep-alive");
    }

    private void ReceiveRobotData(string data)
    {
        serverRobotLists = JsonUtility.FromJson<ServerRobotList>("{\"list\":" + data + "}");
        foreach (ServerRobotData item in serverRobotLists.list)
        {
            Debug.Log($"device_id: {item.device_id}, Name: {item.name}, id: {item.id}");
        }

        UIManager.Instance.CreateRobotList(serverRobotLists);
    }

    
    /// <param name="data"></param>
    /// <param name="iPEndPoint"></param>
    public void ReceiveData(byte[] data, IPEndPoint iPEndPoint)
    {
        bool ownershipTransferred = false;
        try
        {
            // IMPORTANT: Avoid decoding binary UDP payloads (audio/video) as UTF-8 strings.
            // This creates large allocations/GC spikes and can stall both video + audio playback.
            bool isDataPort = IsDataPort(iPEndPoint.Port);
            bool isAudioPort = IsAudioPort(iPEndPoint.Port);

            if ((isDataPort || isAudioPort) && !ShouldAcceptDataOrAudioSender(iPEndPoint))
            {
                return;
            }

            if (isDataPort)
            {
                int headerLength = 0;
                int jsonLength = data.Length - headerLength;
                ownershipTransferred = EnqueueDataPacketForBackground(data, jsonLength, iPEndPoint);
                if (!ownershipTransferred)
                {
                    HandleIncomingJson(data, headerLength, jsonLength, iPEndPoint);
                }
            }
            else if (isAudioPort)
            {
                if (AudioStreamPlayer.Instance != null)
                {
                    AudioStreamPlayer.Instance.FeedNetworkPacket(data, data.Length, iPEndPoint);
                }
            }
            else if (IsVideoPort(iPEndPoint.Port))
            {
                if (logVideoUdpPackets)
                {
                    Debug.Log($"[NetClient] {data.Length} {iPEndPoint.Port} (鏉ヨ嚜 {iPEndPoint.Address})");
                }
            }
            else if (logUnknownUdpPackets)
            {
                Debug.LogWarning($"[NetClient] unknown port : {iPEndPoint.Port} ip: {iPEndPoint.Address})");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError(ex);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                ByteArrayPool.Return(data);
            }
        }
    }

    private static string BuildReceivePayloadLog(string? sourceLabel, string jsonString)
    {
        if (string.IsNullOrEmpty(sourceLabel))
        {
            return $"receive::{jsonString}";
        }

        return $"receive[{sourceLabel}]::{jsonString}";
    }

    private static string FormatEndpoint(IPEndPoint? endpoint)
    {
        if (endpoint == null || endpoint.Address == null)
        {
            return "unknown";
        }

        return endpoint.Address + ":" + endpoint.Port;
    }

    private bool ShouldAcceptDataOrAudioSender(IPEndPoint remote)
    {
        if (!filterUnexpectedDataAndAudioSenders || remote == null || remote.Address == null)
        {
            return true;
        }

        string expectedSenderIp = RuntimeServerConfig.serverIp != null ? RuntimeServerConfig.serverIp.Trim() : string.Empty;
        if (string.IsNullOrEmpty(expectedSenderIp))
        {
            return true;
        }

        if (!IPAddress.TryParse(expectedSenderIp, out IPAddress expectedAddress))
        {
            if (!_invalidExpectedSenderIpLogged)
            {
                _invalidExpectedSenderIpLogged = true;
                Debug.LogWarning($"[NetClient] UDP sender filter skipped: configured serverIp '{expectedSenderIp}' is not a literal IP address.");
            }
            return true;
        }

        IPAddress remoteAddress = remote.Address;
        if (remoteAddress.IsIPv4MappedToIPv6)
        {
            remoteAddress = remoteAddress.MapToIPv4();
        }

        if (expectedAddress.IsIPv4MappedToIPv6)
        {
            expectedAddress = expectedAddress.MapToIPv4();
        }

        if (remoteAddress.Equals(expectedAddress))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedUnexpectedSenderPackets);
        float now = Time.unscaledTime;
        if (now - _lastUnexpectedSenderLogAt >= unexpectedSenderLogIntervalSeconds)
        {
            _lastUnexpectedSenderLogAt = now;
            Debug.LogWarning(
                $"[NetClient] Dropping UDP packet from unexpected sender {FormatEndpoint(remote)}. " +
                $"Expected {expectedAddress}. dropped={Interlocked.Read(ref _droppedUnexpectedSenderPackets)}");
        }

        return false;
    }

    public void HandleIncomingJson(byte[] payload, int offset, int length, IPEndPoint? sourceEndpoint = null)
    {
        ProcessJsonPayload(payload, offset, length, sourceEndpoint, processOnBackgroundThread: false);
    }

    private void ProcessJsonPayload(byte[] payload, int offset, int length, IPEndPoint? sourceEndpoint, bool processOnBackgroundThread)
    {
        if (payload == null || length <= 0)
        {
            return;
        }

        int safeOffset = Mathf.Clamp(offset, 0, payload.Length);
        int safeEnd = Mathf.Clamp(offset + length, safeOffset, payload.Length);
        int safeLength = safeEnd - safeOffset;
        if (safeLength <= 0)
        {
            return;
        }

        lock (_dataJsonStreamLock)
        {
            if (!TryAppendToDataJsonReassemblyBufferLocked(payload, safeOffset, safeLength, sourceEndpoint, processOnBackgroundThread))
            {
                return;
            }

            ProcessDataJsonReassemblyBufferLocked(sourceEndpoint, processOnBackgroundThread);
        }
    }

    private bool ShouldProcessType(MessageType messageType)
    {
        if (messageType == MessageType.Unknown)
        {
            return false;
        }

        int index = (int)messageType;
        float currentTime = Time.time;
        if (_hasLastProcessTime[index])
        {
            if (currentTime - _lastProcessTimeByType[index] < MinProcessInterval)
            {
                return false;
            }
        }
        _lastProcessTimeByType[index] = currentTime;
        _hasLastProcessTime[index] = true;
        return true;
    }

    private void ProcessJsonByType(MessageType messageType, string singleJson)
    {
        switch (messageType)
        {
            case MessageType.RobotState:
                RobotStateMessage stateMsg = JsonConvert.DeserializeObject<RobotStateMessage>(singleJson);
                if (stateMsg != null)
                {
                    DataManager.Instance?.UpdateRobotState(stateMsg);
                }
                break;
            case MessageType.Task:
                TaskStateMessage taskMsg = JsonUtility.FromJson<TaskStateMessage>(singleJson);
                if (taskMsg != null)
                {
                    DataManager.Instance?.UpdateTaskState(taskMsg);
                }
                break;
            case MessageType.RobotJointState:
                RobotJointStateMessage jointMsg = JsonConvert.DeserializeObject<RobotJointStateMessage>(singleJson);
                if (jointMsg != null)
                {
                    DataManager.Instance?.UpdateRobotJointState(jointMsg);
                }
                break;
            case MessageType.TtsList:
                var ttsListMsg = JsonConvert.DeserializeObject<StringArrayMessage>(singleJson);
                if (ttsListMsg?.data != null)
                {
                    QueueTTSOptions(ttsListMsg.data);
                }
                break;
            case MessageType.Action:
                var actionListMsg = JsonConvert.DeserializeObject<StringArrayMessage>(singleJson);
                if (actionListMsg?.data != null)
                {
                    QueueActionOptions(actionListMsg.data);
                }
                break;
        }
    }

    private static bool TryExtractMessageType(byte[] rawJson, int offset, int length, out MessageType messageType)
    {
        messageType = MessageType.Unknown;
        if (rawJson == null || length <= 0)
        {
            return false;
        }

        int end = Math.Min(rawJson.Length, offset + length);
        if (offset < 0 || offset >= end)
        {
            return false;
        }

        for (int i = offset; i + 6 < end; i++)
        {
            if (rawJson[i] != 34 ||
                rawJson[i + 1] != 116 ||
                rawJson[i + 2] != 121 ||
                rawJson[i + 3] != 112 ||
                rawJson[i + 4] != 101 ||
                rawJson[i + 5] != 34)
            {
                continue;
            }

            int j = i + 6;
            while (j < end && IsWhitespace(rawJson[j])) j++;
            if (j >= end || rawJson[j] != 58)
            {
                continue;
            }
            j++;
            while (j < end && IsWhitespace(rawJson[j])) j++;
            if (j >= end || rawJson[j] != 34)
            {
                continue;
            }
            j++;
            int valueStart = j;
            while (j < end)
            {
                byte b = rawJson[j];
                if (b == 34)
                {
                    int valueLen = j - valueStart;
                    if (valueLen <= 0)
                    {
                        return false;
                    }

                    if (MatchesAscii(rawJson, valueStart, valueLen, "robot_state"))
                    {
                        messageType = MessageType.RobotState;
                        return true;
                    }
                    if (MatchesAscii(rawJson, valueStart, valueLen, "task"))
                    {
                        messageType = MessageType.Task;
                        return true;
                    }
                    if (MatchesAscii(rawJson, valueStart, valueLen, "robot_joint_state"))
                    {
                        messageType = MessageType.RobotJointState;
                        return true;
                    }
                    if (MatchesAscii(rawJson, valueStart, valueLen, "tts_list"))
                    {
                        messageType = MessageType.TtsList;
                        return true;
                    }
                    if (MatchesAscii(rawJson, valueStart, valueLen, "action"))
                    {
                        messageType = MessageType.Action;
                        return true;
                    }

                    messageType = MessageType.Unknown;
                    return true;
                }
                if (b == 92)
                {
                    j += 2;
                    continue;
                }
                j++;
            }
        }

        return false;
    }

    private void ProcessJsonSlice(byte[] buffer, int offset, int length, IPEndPoint? sourceEndpoint, bool processOnBackgroundThread, ref string? sourceLabelCache)
    {
        if (buffer == null || length <= 0 || offset < 0 || offset + length > buffer.Length)
        {
            return;
        }

        if (!TryExtractMessageType(buffer, offset, length, out MessageType messageType) || messageType == MessageType.Unknown)
        {
            if (!logDataPayload)
            {
                return;
            }

            string unknownJson = Encoding.UTF8.GetString(buffer, offset, length);
            EmitDataLog(BuildReceivePayloadLog(GetOrCreateSourceLabel(sourceEndpoint, ref sourceLabelCache), unknownJson), false, processOnBackgroundThread);
            return;
        }

        bool shouldLogLength = ShouldLogRobotJointStateLengthNow(messageType);
        bool shouldProcess = processOnBackgroundThread
            ? ShouldProcessTypeOnBackground(messageType)
            : ShouldProcessType(messageType);

        if (!logDataPayload && !shouldLogLength && !shouldProcess)
        {
            return;
        }

        string jsonString = Encoding.UTF8.GetString(buffer, offset, length);

        if (logDataPayload)
        {
            EmitDataLog(BuildReceivePayloadLog(GetOrCreateSourceLabel(sourceEndpoint, ref sourceLabelCache), jsonString), false, processOnBackgroundThread);
        }

        if (shouldLogLength)
        {
            LogMessageLength(messageType, length, jsonString.Length, sourceEndpoint, processOnBackgroundThread, ref sourceLabelCache);
        }

        if (!shouldProcess)
        {
            return;
        }

        if (processOnBackgroundThread)
        {
            DeserializeAndQueueJsonByType(messageType, jsonString);
        }
        else
        {
            ProcessJsonByType(messageType, jsonString);
        }
    }

    private static bool MatchesAscii(byte[] rawJson, int start, int length, string ascii)
    {
        if (length != ascii.Length)
        {
            return false;
        }

        if (start < 0 || start + length > rawJson.Length)
        {
            return false;
        }

        for (int i = 0; i < length; i++)
        {
            if (rawJson[start + i] != (byte)ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWhitespace(byte value)
    {
        return value == 32 || value == 10 || value == 13 || value == 9;
    }

    private bool TryAppendToDataJsonReassemblyBufferLocked(byte[] payload, int offset, int length, IPEndPoint? sourceEndpoint, bool processOnBackgroundThread)
    {
        if (payload == null || length <= 0)
        {
            return false;
        }

        int requiredCount = _dataJsonReassemblyCount + length;
        if (requiredCount > maxDataJsonReassemblyBytes)
        {
            string? sourceLabelCache = null;
            EmitDataLog(
                $"[NetClient] Data JSON reassembly buffer overflow for {GetOrCreateSourceLabel(sourceEndpoint, ref sourceLabelCache)}, dropping {requiredCount} bytes.",
                true,
                processOnBackgroundThread);
            _dataJsonReassemblyCount = 0;
            return false;
        }

        EnsureDataJsonReassemblyCapacityLocked(requiredCount);
        Buffer.BlockCopy(payload, offset, _dataJsonReassemblyBuffer, _dataJsonReassemblyCount, length);
        _dataJsonReassemblyCount = requiredCount;
        return true;
    }

    private void ProcessDataJsonReassemblyBufferLocked(IPEndPoint? sourceEndpoint, bool processOnBackgroundThread)
    {
        if (_dataJsonReassemblyCount <= 0)
        {
            return;
        }

        string? sourceLabelCache = null;
        int firstObjectStart = -1;
        int depth = 0;
        bool inString = false;
        bool escape = false;
        int consumedEndExclusive = 0;

        for (int i = 0; i < _dataJsonReassemblyCount; i++)
        {
            byte current = _dataJsonReassemblyBuffer[i];

            if (firstObjectStart < 0)
            {
                if (current == (byte)'{')
                {
                    firstObjectStart = i;
                    depth = 1;
                    inString = false;
                    escape = false;
                }
                continue;
            }

            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (current == (byte)'\\')
                {
                    escape = true;
                }
                else if (current == (byte)'"')
                {
                    inString = false;
                }
                continue;
            }

            if (current == (byte)'"')
            {
                inString = true;
                continue;
            }

            if (current == (byte)'{')
            {
                depth++;
                continue;
            }

            if (current != (byte)'}')
            {
                continue;
            }

            depth--;
            if (depth != 0)
            {
                continue;
            }

            int messageLength = i - firstObjectStart + 1;
            if (messageLength > 0)
            {
                ProcessJsonSlice(_dataJsonReassemblyBuffer, firstObjectStart, messageLength, sourceEndpoint, processOnBackgroundThread, ref sourceLabelCache);
                consumedEndExclusive = i + 1;
            }

            firstObjectStart = -1;
            inString = false;
            escape = false;
        }

        if (consumedEndExclusive > 0)
        {
            int remaining = _dataJsonReassemblyCount - consumedEndExclusive;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_dataJsonReassemblyBuffer, consumedEndExclusive, _dataJsonReassemblyBuffer, 0, remaining);
            }
            _dataJsonReassemblyCount = remaining;
        }
        else if (firstObjectStart > 0)
        {
            int remaining = _dataJsonReassemblyCount - firstObjectStart;
            Buffer.BlockCopy(_dataJsonReassemblyBuffer, firstObjectStart, _dataJsonReassemblyBuffer, 0, remaining);
            _dataJsonReassemblyCount = remaining;
        }
        else if (firstObjectStart < 0)
        {
            _dataJsonReassemblyCount = 0;
        }
    }

    private void EnsureDataJsonReassemblyCapacityLocked(int requiredCount)
    {
        if (_dataJsonReassemblyBuffer.Length >= requiredCount)
        {
            return;
        }

        int newCapacity = _dataJsonReassemblyBuffer.Length > 0 ? _dataJsonReassemblyBuffer.Length : 4096;
        while (newCapacity < requiredCount)
        {
            newCapacity *= 2;
        }

        Array.Resize(ref _dataJsonReassemblyBuffer, newCapacity);
    }

    private static string GetOrCreateSourceLabel(IPEndPoint? sourceEndpoint, ref string? sourceLabelCache)
    {
        if (sourceLabelCache != null)
        {
            return sourceLabelCache;
        }

        sourceLabelCache = FormatEndpoint(sourceEndpoint);
        return sourceLabelCache;
    }

    private void EmitDataLog(string message, bool isError, bool enqueueToBackgroundLogQueue)
    {
        if (enqueueToBackgroundLogQueue)
        {
            _dataLogQueue.Enqueue((message, isError));
            return;
        }

        if (isError)
        {
            Debug.LogWarning(message);
        }
        else
        {
            Debug.Log(message);
        }
    }

    private bool ShouldLogRobotJointStateLengthNow(MessageType messageType)
    {
        if (messageType != MessageType.RobotJointState)
        {
            return false;
        }

        long now = Stopwatch.GetTimestamp();
        long intervalTicks = (long)(Mathf.Max(0.1f, robotJointStateLengthLogIntervalSeconds) * Stopwatch.Frequency);
        long last = Interlocked.Read(ref _lastRobotJointStateLengthLogTicks);
        if (last > 0 && now - last < intervalTicks)
        {
            return false;
        }

        Interlocked.Exchange(ref _lastRobotJointStateLengthLogTicks, now);
        return true;
    }

    private void LogMessageLength(MessageType messageType, int utf8Bytes, int charCount, IPEndPoint? sourceEndpoint, bool enqueueToBackgroundLogQueue, ref string? sourceLabelCache)
    {
        if (messageType != MessageType.RobotJointState)
        {
            return;
        }

        string source = GetOrCreateSourceLabel(sourceEndpoint, ref sourceLabelCache);
        string message = $"[NetClient] robot_joint_state length: utf8Bytes={utf8Bytes}, chars={charCount}, source={source}";
        EmitDataLog(message, false, enqueueToBackgroundLogQueue);
    }

    private void ProcessPendingTTSOptions()
    {
        if (UIManager.Instance == null)
        {
            return;
        }

        List<string> options = null;
        lock (_ttsOptionsLock)
        {
            if (_hasPendingTTSUpdate)
            {
                options = _pendingTTSOptions;
                _pendingTTSOptions = null;
                _hasPendingTTSUpdate = false;
            }
        }
        if (options != null)
        {
            UIManager.Instance.UpdateTTSOptions(options);
        }
    }

    private void ProcessPendingActionOptions()
    {
        if (UIManager.Instance == null)
        {
            return;
        }

        List<string> options = null;
        lock (_actionOptionsLock)
        {
            if (_hasPendingActionUpdate)
            {
                options = _pendingActionOptions;
                _pendingActionOptions = null;
                _hasPendingActionUpdate = false;
            }
        }
        if (options != null)
        {
            UIManager.Instance.UpdateActionOptions(options);
        }
    }

    private void QueueTTSOptions(List<string> options)
    {
        if (options == null) return;
        lock (_ttsOptionsLock)
        {
            _pendingTTSOptions = new List<string>(options);
            _hasPendingTTSUpdate = true;
        }
    }

    private void QueueActionOptions(List<string> options)
    {
        if (options == null) return;
        lock (_actionOptionsLock)
        {
            _pendingActionOptions = new List<string>(options);
            _hasPendingActionUpdate = true;
        }
    }
    private void OnDestroy()
    {
        StopDataPacketThread();
        CloseUdpManagers();
        if (instance == this)
        {
            instance = null;
        }
    }

    private void OnApplicationQuit()
    {
        StopDataPacketThread();
        CloseUdpManagers();
    }

    private void OnApplicationPause(bool pause)
    {
        if (pause)
        {
            StopDataPacketThread();
            CloseUdpManagers();
        }
    }

    private void CloseUdpManagers()
    {
        udpDataManager?.Close();
        udpAudioManager?.Close();
        udpDataManager = null;
        udpAudioManager = null;
    }

    private bool IsVideoPort(int port) => port == RemoteVideoPort || port == LocalVideoPort;
    private bool IsAudioPort(int port) => port == RemoteAudioPort || port == LocalAudioPort;
    private bool IsDataPort(int port) => port == RemoteDataPort || port == LocalDataPort;


    public void ReloadUdpManagers()
    {
        Debug.Log("[NetClient] Reloading UdpManagers with new server config...");
        // 1. 鍏抽棴鏃х殑
        var oldUdpDataManager = udpDataManager;
        var oldUdpAudioManager = udpAudioManager;

        UdpManager newUdpDataManager;
        UdpManager newUdpAudioManager;
        UdpManager? newUdpVideoManager;

        // 2. Reload IP/Port since RuntimeServerConfig has already been updated
        Debug.Log(RuntimeServerConfig.serverIp);
        robotdataUrl = $"http://{RuntimeServerConfig.serverIp}:8080/devices/list";
        int localDataPort = LocalDataPort;
        int localAudioPort = LocalAudioPort;
        int localVideoPort = LocalVideoPort;
        int remoteDataPort = RemoteDataPort;
        int remoteAudioPort = RemoteAudioPort;
        int remoteVideoPort = RemoteVideoPort;

        try
        {
            newUdpDataManager = new UdpManager(
                localDataPort,
                "0.0.0.0",
                RuntimeServerConfig.serverIp,
                remoteDataPort,
                maxQueuedPackets: maxDataQueuedPackets,
                dropOldestOnOverflow: dropOldestOnQueueOverflow);
            newUdpDataManager.OnDataReceived += ReceiveData;

            newUdpAudioManager = new UdpManager(
                localAudioPort,
                "0.0.0.0",
                RuntimeServerConfig.serverIp,
                remoteAudioPort,
                maxQueuedPackets: maxAudioQueuedPackets,
                dropOldestOnOverflow: dropOldestOnQueueOverflow);
            newUdpAudioManager.OnDataReceived += ReceiveData;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[NetClient] ReloadUdpManagers failed to create UDP managers: {ex.Message}");
            return;
        }

        // Quest decoders bind the video UDP port directly (Unity UDP video bridge removed).
        var questStreamPlayer = GetQuestStreamVideoPlayerCached();

        udpDataManager = newUdpDataManager;
        udpAudioManager = newUdpAudioManager;

        oldUdpDataManager?.Close();
        oldUdpAudioManager?.Close();
        //udpHeartbeatManager?.Close();

        // ??QuestStreamVideoPlayer??Java?UDP?????????
        if (questStreamPlayer != null)
        {
            questStreamPlayer.ConfigureNetwork(
                "0.0.0.0",
                localVideoPort,
                0,  // secondaryPort (unused)
                RuntimeServerConfig.serverIp,
                remoteVideoPort,
                RuntimeServerConfig.serverIp  // expectedSenderIp
            );
            Debug.Log($"[NetClient] QuestStreamVideoPlayer configured for Java UDP mode: listen={localVideoPort}, remote={RuntimeServerConfig.serverIp}:{remoteVideoPort}");
        }

        Debug.Log($"[NetClient] UDP Managers Reloaded. isLAN={RuntimeServerConfig.curislan}\n" +
                  $"Quest Stream Player （支持Unity桥接）: {(questStreamPlayer != null ? "Active" : "None")}\n" +
                  $"Server IP: {RuntimeServerConfig.serverIp}\n" +
                  $"Local Ports: Video({localVideoPort}), Audio({localAudioPort}), Data({localDataPort})\n" +
                  $"Remote Ports: Video({remoteVideoPort}), Audio({remoteAudioPort}), Data({remoteDataPort})");
    }
}

[Serializable]
public class StringArrayMessage
{
    public string type;
    public List<string> data;
}



























