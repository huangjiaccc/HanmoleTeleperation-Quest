using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Concentus;
using Concentus.Enums;
using UnityEngine;
using Debug = AppLog;

public class RecordingManager : MonoBehaviour
{
    [Header("Microphone")]
    [SerializeField] private int sampleRate = 16000;
    [SerializeField, Range(10, 100)] private int packetDurationMs = 20;
    [SerializeField, Min(1)] private int recordBufferSeconds = 10;

    [Header("Opus Send")]
    [SerializeField, Range(6000, 96000)] private int opusBitrate = 16000;
    [SerializeField, Range(0, 10)] private int opusComplexity = 5;
    [SerializeField] private bool opusUseVbr = true;
    [SerializeField] private bool opusUseInbandFec = false;
    [SerializeField] private bool opusUseDtx = false;
    [SerializeField, Min(256)] private int opusMaxPacketBytes = 2048;

    [Header("Debug")]
    [SerializeField] private bool debugMicrophonePosition = false;
    [SerializeField, Range(0.1f, 5f)] private float debugIntervalSeconds = 1f;

    [Header("Resilience")]
    [SerializeField] private bool autoRestartIfPositionStalls = true;
    [SerializeField, Range(0.5f, 10f)] private float stallRestartSeconds = 2f;

    [Header("Local Monitor (Test Playback)")]
    [SerializeField] private bool monitorToAudioStreamPlayer = false;
    [SerializeField, Range(0f, 5f)] private float monitorGain = 1f;

    [Header("Pipeline")]
    [SerializeField, Range(1, 8)] private int maxPacketsPerFrame = 4;
    [SerializeField, Range(2, 16)] private int pcmQueueSlotCount = 6;
    [SerializeField, Range(2, 16)] private int backlogDropThresholdPackets = 6;
    [SerializeField, Range(1, 8)] private int backlogRetainPackets = 2;


    private AudioClip recordingClip;
    private string microphone;
    private bool isRecording;

    private int lastSamplePosition;
    private int recordingChannels;
    private int clipSamples;
    private int cachedFramesPerPacket;
    private int cachedFloatCount;
    private int lastOpusPacketBytes;

    private float lastMicDebugAt;
    private int lastMicPosition = -999;
    private float lastMicPositionChangedAt;
    private int lastPumpPosition = -999;
    private float lastPumpPositionChangedAt;
    private int lastPumpedPackets;
    private int lastPumpedFrames;
    private long totalPumpedPackets;
    private long totalPumpedFrames;
    private float lastPacketRms;
    private float lastPacketPeak;
    private float lastBacklogDropLogAt = float.NegativeInfinity;
    private bool loggedMonitorDisabledForReleaseBuild;

    private readonly object pcmQueueLock = new();
    private readonly AutoResetEvent pcmEncodeSignal = new(false);
    private readonly Queue<int> freePcmSlots = new();
    private readonly Queue<int> readyPcmSlots = new();
    private float[][] pcmPacketSlots;
    private Thread pcmEncodeThread;
    private volatile bool stopPcmEncodeThread;
    private int queuedPcmPackets;
    private long droppedQueuedPcmPackets;
    private long droppedBacklogPackets;

    private int FramesPerPacket => Mathf.Max(1, sampleRate * packetDurationMs / 1000);

    private void Start()
    {
        StartCoroutine(EnsureMicrophoneReady());
    }

    private IEnumerator EnsureMicrophoneReady()
    {
        bool hasAuth = Application.HasUserAuthorization(UserAuthorization.Microphone);
        if (!hasAuth)
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
            hasAuth = Application.HasUserAuthorization(UserAuthorization.Microphone);
        }

        int deviceCount = Microphone.devices != null ? Microphone.devices.Length : 0;
        microphone = deviceCount > 0 ? Microphone.devices[0] : null;

        Debug.Log(
            "[RecordingManager] Mic auth=" + hasAuth +
            " devices=" + deviceCount +
            " selected=" + (microphone ?? "null"));

        if (microphone == null)
        {
            Debug.LogWarning("[RecordingManager] No microphone device available.");
        }
    }

    private void Update()
    {
        bool shouldRecord = false;
        if (DataManager.Instance != null)
        {
            shouldRecord = (DataManager.Instance.currobotState != null 
                && DataManager.Instance.robotStateMessage.data.audio_mode == (int)AudioMode.DIRECT);
        }

        if (!shouldRecord)
        {
            if (isRecording)
            {
                StopRecording();
            }
            return;
        }

        if (!isRecording)
        {
            StartRecording();
        }

        if (isRecording)
        {
            PumpMicrophoneAndSend();
            DebugMicrophoneState();
        }
    }

    private void DebugMicrophoneState()
    {
        if (!debugMicrophonePosition)
        {
            return;
        }

        float now = Time.unscaledTime;
        if (now - lastMicDebugAt < debugIntervalSeconds)
        {
            return;
        }
        lastMicDebugAt = now;

        bool authorized = Application.HasUserAuthorization(UserAuthorization.Microphone);
        int deviceCount = Microphone.devices != null ? Microphone.devices.Length : 0;
        bool isMicRecording = microphone != null && Microphone.IsRecording(microphone);
        int position = microphone != null ? Microphone.GetPosition(microphone) : -2;
        int clipSamples = recordingClip != null ? recordingClip.samples : -1;
        int channels = recordingClip != null ? recordingClip.channels : -1;
        int clipFrequency = recordingClip != null ? recordingClip.frequency : -1;

        if (position != lastMicPosition)
        {
            lastMicPosition = position;
            lastMicPositionChangedAt = now;
        }

        int availableFrames = 0;
        if (position >= 0 && clipSamples > 0)
        {
            availableFrames = position - lastSamplePosition;
            if (availableFrames < 0) availableFrames += clipSamples;
        }

    }

    private void StartRecording()
    {
        if (microphone == null)
        {
            Debug.LogWarning("[RecordingManager] StartRecording skipped: microphone is null.");
            return;
        }

        if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            Debug.LogWarning("[RecordingManager] StartRecording skipped: microphone permission not granted.");
            return;
        }

        int lengthSec = Mathf.Clamp(recordBufferSeconds, 1, 60);
        recordingClip = Microphone.Start(microphone, true, lengthSec, sampleRate);
        if (recordingClip == null)
        {
            Debug.LogWarning("[RecordingManager] Microphone.Start returned null AudioClip.");
            isRecording = false;
            return;
        }

        ConfigureCapturePipeline(recordingClip, restartEncoderThread: true);

        lastSamplePosition = 0;
        lastPumpPosition = -999;
        lastPumpPositionChangedAt = Time.unscaledTime;
        isRecording = true;
        lastOpusPacketBytes = 0;
        lastPacketRms = 0f;
        lastPacketPeak = 0f;
        queuedPcmPackets = 0;
        droppedQueuedPcmPackets = 0;
        droppedBacklogPackets = 0;

        if (monitorToAudioStreamPlayer && !UnityEngine.Debug.isDebugBuild && !loggedMonitorDisabledForReleaseBuild)
        {
            loggedMonitorDisabledForReleaseBuild = true;
            Debug.Log("[RecordingManager] Local monitor disabled in non-development build.");
        }

        Debug.Log("Recording started...");
    }

    private void StopRecording()
    {
        if (microphone != null)
        {
            Microphone.End(microphone);
        }

        isRecording = false;
        ShutdownPcmEncodeThread();
        recordingClip = null;
        recordingChannels = 0;
        clipSamples = 0;
        cachedFramesPerPacket = 0;
        cachedFloatCount = 0;
        lastOpusPacketBytes = 0;
        Debug.Log("Recording stopped.");
    }

    private void PumpMicrophoneAndSend()
    {
        if (recordingClip == null || string.IsNullOrEmpty(microphone))
        {
            return;
        }

        int position = Microphone.GetPosition(microphone);
        if (position < 0)
        {
            return;
        }

        float now = Time.unscaledTime;
        if (position != lastPumpPosition)
        {
            lastPumpPosition = position;
            lastPumpPositionChangedAt = now;
        }
        else if (autoRestartIfPositionStalls && now - lastPumpPositionChangedAt > stallRestartSeconds)
        {
            Debug.LogWarning(
                "[RecordingManager] Microphone.GetPosition stalled at " + position +
                " for " + (now - lastPumpPositionChangedAt).ToString("F1") + "s, restarting Microphone.");

            try { Microphone.End(microphone); } catch { }

            int lengthSec = Mathf.Clamp(recordBufferSeconds, 1, 60);
            recordingClip = Microphone.Start(microphone, true, lengthSec, sampleRate);
            if (recordingClip == null)
            {
                Debug.LogWarning("[RecordingManager] Microphone restart failed: Microphone.Start returned null AudioClip.");
                ShutdownPcmEncodeThread();
                isRecording = false;
                return;
            }

            ConfigureCapturePipeline(recordingClip, restartEncoderThread: true);
            lastSamplePosition = 0;
            lastPumpPosition = -999;
            lastPumpPositionChangedAt = now;
            return;
        }


        int availableFrames = position - lastSamplePosition;
        if (availableFrames < 0)
        {
            availableFrames += clipSamples;
        }

        int framesPerPacket = cachedFramesPerPacket > 0 ? cachedFramesPerPacket : FramesPerPacket;
        int channels = recordingChannels > 0 ? recordingChannels : Mathf.Clamp(recordingClip.channels, 1, 2);
        int maxPacketsThisFrame = Mathf.Max(1, maxPacketsPerFrame);

        TrimMicrophoneBacklog(position, framesPerPacket, ref availableFrames);

        int packetsSentThisTick = 0;
        int framesPumpedThisTick = 0;

        try
        {
            while (availableFrames >= framesPerPacket && packetsSentThisTick < maxPacketsThisFrame)
            {
                if (!TryAcquireWritablePcmSlot(out int slot))
                {
                    break;
                }

                float[] packetBuffer = pcmPacketSlots != null && slot >= 0 && slot < pcmPacketSlots.Length
                    ? pcmPacketSlots[slot]
                    : null;
                if (packetBuffer == null)
                {
                    ReleasePcmSlot(slot);
                    break;
                }

                if (!recordingClip.GetData(packetBuffer, lastSamplePosition))
                {
                    ReleasePcmSlot(slot);
                    Debug.LogWarning("[RecordingManager] AudioClip.GetData failed for microphone packet.");
                    break;
                }

                UpdatePacketDebugStats(packetBuffer, cachedFloatCount);

                if (ShouldFeedLocalMonitor())
                {
                    AudioStreamPlayer.Instance.FeedPcmFloats(packetBuffer, cachedFloatCount, channels, monitorGain);
                }

                EnqueuePcmSlot(slot);

                lastSamplePosition += framesPerPacket;
                if (lastSamplePosition >= clipSamples)
                {
                    lastSamplePosition -= clipSamples;
                }

                availableFrames -= framesPerPacket;
                packetsSentThisTick++;
                framesPumpedThisTick += framesPerPacket;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[RecordingManager] PumpMicrophoneAndSend failed: " + ex);
        }

        lastPumpedPackets = packetsSentThisTick;
        lastPumpedFrames = framesPumpedThisTick;
        totalPumpedPackets += packetsSentThisTick;
        totalPumpedFrames += framesPumpedThisTick;
    }

    private void InitializePcmQueue(int floatCount)
    {
        int slotCount = Mathf.Max(2, pcmQueueSlotCount);
        if (pcmPacketSlots == null || pcmPacketSlots.Length != slotCount)
        {
            pcmPacketSlots = new float[slotCount][];
        }

        lock (pcmQueueLock)
        {
            freePcmSlots.Clear();
            readyPcmSlots.Clear();
            queuedPcmPackets = 0;

            for (int i = 0; i < slotCount; i++)
            {
                if (pcmPacketSlots[i] == null || pcmPacketSlots[i].Length != floatCount)
                {
                    pcmPacketSlots[i] = new float[floatCount];
                }
                freePcmSlots.Enqueue(i);
            }
        }
    }

    private void ConfigureCapturePipeline(AudioClip clip, bool restartEncoderThread)
    {
        if (clip == null)
        {
            return;
        }

        recordingChannels = Mathf.Clamp(clip.channels, 1, 2);
        clipSamples = Mathf.Max(1, clip.samples);
        cachedFramesPerPacket = FramesPerPacket;
        cachedFloatCount = cachedFramesPerPacket * recordingChannels;
        InitializePcmQueue(cachedFloatCount);

        if (restartEncoderThread)
        {
            StartPcmEncodeThread();
        }
    }

    private void StartPcmEncodeThread()
    {
        ShutdownPcmEncodeThread();
        stopPcmEncodeThread = false;
        pcmEncodeThread = new Thread(PcmEncodeThreadLoop)
        {
            IsBackground = true,
            Name = "RecordingOpusEncode"
        };
        pcmEncodeThread.Start();
    }

    private void ShutdownPcmEncodeThread()
    {
        stopPcmEncodeThread = true;
        try { pcmEncodeSignal.Set(); } catch { }

        if (pcmEncodeThread != null && pcmEncodeThread.IsAlive)
        {
            try
            {
                if (!pcmEncodeThread.Join(80))
                {
                    pcmEncodeThread.Interrupt();
                    pcmEncodeThread.Join(20);
                }
            }
            catch { }
        }
        pcmEncodeThread = null;

        lock (pcmQueueLock)
        {
            readyPcmSlots.Clear();
            freePcmSlots.Clear();

            if (pcmPacketSlots != null)
            {
                for (int i = 0; i < pcmPacketSlots.Length; i++)
                {
                    freePcmSlots.Enqueue(i);
                }
            }

            queuedPcmPackets = 0;
        }
    }

    private void PcmEncodeThreadLoop()
    {
        IOpusEncoder encoder = null;
        byte[] packetBytes = null;
        try
        {
            encoder = OpusCodecFactory.CreateEncoder(sampleRate, Mathf.Max(1, recordingChannels), OpusApplication.OPUS_APPLICATION_VOIP, null);
            encoder.Bitrate = opusBitrate;
            encoder.Complexity = opusComplexity;
            encoder.UseVBR = opusUseVbr;
            encoder.UseInbandFEC = opusUseInbandFec;
            encoder.UseDTX = opusUseDtx;
            packetBytes = new byte[Mathf.Max(256, opusMaxPacketBytes)];
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[RecordingManager] Failed to create Opus encoder: " + ex.Message);
            lastOpusPacketBytes = 0;
            return;
        }

        while (!stopPcmEncodeThread)
        {
            int slot = TakeReadyPcmSlot();
            if (slot < 0)
            {
                try { pcmEncodeSignal.WaitOne(20); } catch (ThreadInterruptedException) { break; } catch { }
                continue;
            }

            try
            {
                float[] packetBuffer = pcmPacketSlots != null && slot < pcmPacketSlots.Length ? pcmPacketSlots[slot] : null;
                if (packetBuffer == null)
                {
                    continue;
                }

                int encodedBytes = 0;
                try
                {
                    encodedBytes = encoder.Encode(
                        packetBuffer.AsSpan(0, cachedFloatCount),
                        cachedFramesPerPacket,
                        packetBytes.AsSpan(),
                        packetBytes.Length);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[RecordingManager] Opus encode failed: " + ex.Message);
                    lastOpusPacketBytes = 0;
                    continue;
                }

                lastOpusPacketBytes = encodedBytes;
                if (encodedBytes <= 0)
                {
                    continue;
                }

                var client = NetClient.instance;
                var audioUdp = client != null ? client.udpAudioManager : null;
                if (client == null || (object)audioUdp == null)
                {
                    continue;
                }

                audioUdp.Send(packetBytes, null, encodedBytes);
            }
            finally
            {
                ReleasePcmSlot(slot);
            }
        }
    }

    private bool TryAcquireWritablePcmSlot(out int slot)
    {
        lock (pcmQueueLock)
        {
            if (freePcmSlots.Count > 0)
            {
                slot = freePcmSlots.Dequeue();
                return true;
            }

            if (readyPcmSlots.Count > 0)
            {
                int droppedSlot = readyPcmSlots.Dequeue();
                queuedPcmPackets = readyPcmSlots.Count;
                freePcmSlots.Enqueue(droppedSlot);
                droppedQueuedPcmPackets++;
                slot = freePcmSlots.Dequeue();
                return true;
            }
        }

        slot = -1;
        return false;
    }

    private void EnqueuePcmSlot(int slot)
    {
        lock (pcmQueueLock)
        {
            readyPcmSlots.Enqueue(slot);
            queuedPcmPackets = readyPcmSlots.Count;
        }

        try { pcmEncodeSignal.Set(); } catch { }
    }

    private int TakeReadyPcmSlot()
    {
        lock (pcmQueueLock)
        {
            if (readyPcmSlots.Count == 0)
            {
                return -1;
            }

            int slot = readyPcmSlots.Dequeue();
            queuedPcmPackets = readyPcmSlots.Count;
            return slot;
        }
    }

    private void ReleasePcmSlot(int slot)
    {
        if (slot < 0)
        {
            return;
        }

        lock (pcmQueueLock)
        {
            freePcmSlots.Enqueue(slot);
        }
    }

    private bool ShouldFeedLocalMonitor()
    {
        return monitorToAudioStreamPlayer &&
               UnityEngine.Debug.isDebugBuild &&
               AudioStreamPlayer.Instance != null;
    }

    private void UpdatePacketDebugStats(float[] packetBuffer, int floatCount)
    {
        if (!debugMicrophonePosition || packetBuffer == null || floatCount <= 0)
        {
            return;
        }

        float peak = 0f;
        double sumSq = 0;
        for (int i = 0; i < floatCount; i++)
        {
            float v = packetBuffer[i];
            float av = Mathf.Abs(v);
            if (av > peak) peak = av;
            sumSq += (double)v * v;
        }

        lastPacketPeak = peak;
        lastPacketRms = (float)Math.Sqrt(sumSq / floatCount);
    }

    private void TrimMicrophoneBacklog(int position, int framesPerPacket, ref int availableFrames)
    {
        int dropThresholdPackets = Mathf.Max(2, backlogDropThresholdPackets);
        int retainPackets = Mathf.Clamp(backlogRetainPackets, 1, dropThresholdPackets - 1);
        int thresholdFrames = dropThresholdPackets * framesPerPacket;
        if (availableFrames < thresholdFrames)
        {
            return;
        }

        int retainFrames = retainPackets * framesPerPacket;
        int droppedFrames = Math.Max(0, availableFrames - retainFrames);
        if (droppedFrames <= 0)
        {
            return;
        }

        lastSamplePosition = position - retainFrames;
        while (lastSamplePosition < 0)
        {
            lastSamplePosition += clipSamples;
        }

        availableFrames = retainFrames;
        droppedBacklogPackets += Math.Max(1, droppedFrames / Mathf.Max(1, framesPerPacket));

        float now = Time.unscaledTime;
        if (now - lastBacklogDropLogAt >= 1f)
        {
            lastBacklogDropLogAt = now;
            Debug.LogWarning(
                "[RecordingManager] Capture backlog trimmed: droppedFrames=" + droppedFrames +
                " droppedPackets~=" + (droppedFrames / Mathf.Max(1, framesPerPacket)) +
                " retainPackets=" + retainPackets +
                " queuedPcm=" + queuedPcmPackets +
                " droppedQueued=" + droppedQueuedPcmPackets);
        }
    }

    private void OnDisable()
    {
        if (isRecording)
        {
            StopRecording();
        }
        else
        {
            ShutdownPcmEncodeThread();
        }
    }

    private void OnDestroy()
    {
        if (isRecording)
        {
            StopRecording();
        }
        else
        {
            ShutdownPcmEncodeThread();
        }

        try { pcmEncodeSignal.Dispose(); } catch { }
    }
}
