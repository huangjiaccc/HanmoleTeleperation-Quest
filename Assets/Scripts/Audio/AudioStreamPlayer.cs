using Concentus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using UnityEngine;
using Debug = AppLog;

[RequireComponent(typeof(AudioSource))]
public class AudioStreamPlayer : MonoBehaviour
{
    public static AudioStreamPlayer Instance;

    private const int SampleRate = 16000;
    private const int OutputChannels = 2;
    private const int MaxOpusFrameSize = 5760;

    [Header("Playback")]
    [SerializeField] private bool autoPlayOnAwake = true;
    [SerializeField] private bool autoResumeOnPacket = true;

    [Header("Debug")]
    [SerializeField] private bool debugAudio = false;
    [SerializeField, Range(0.1f, 5f)] private float debugIntervalSeconds = 1f;

    private AudioSource audioSource;

    [Header("Buffer")]
    [SerializeField, Range(0f, 2f)] private float prebufferSeconds = 0.2f;
    [SerializeField, Range(0.2f, 10f)] private float ringBufferSeconds = 3f;

    private readonly object pcmLock = new object();
    private float[] pcmRing;
    private int pcmRingCapacity;
    private int pcmReadPos;
    private int pcmWritePos;
    private int pcmCount;
    private long pcmSamplesDropped;

    private IOpusDecoder opusDecoder;
    private int opusChannels = 2;
    private int opusPreSkipFrames;
    private byte[] opusScratch = new byte[2048];
    private short[] opusPcmScratch;

    private long packetsTotal;
    private long bytesTotal;

    private long opusPacketsDecoded;

    private long samplesQueued;
    private long audioUnderruns;

    private long decodeErrors;
    private long opusDecodeFailures;

    private float lastDecodedPcmRms;
    private int lastDecodedPcmSamples;
    private int lastDecodedPacketBytes;
    private string lastOpusDecodeError;

    private long lastAudioDecodedAtTicks;

    private string lastRemote;
    private string lastPrefix;
    private int lastPacketSize;
    private long lastPacketAtTicks;

    private float lastDebugAt;
    private long lastDebugPackets;
    private long lastDebugBytes;
    private float[] probeBuffer;

    [Header("Decode Thread")]
    [SerializeField] private bool decodeOnBackgroundThread = true;
    [SerializeField, Range(32, 4096)] private int maxQueuedPackets = 512;
    [SerializeField] private bool dropOldestOnQueueOverflow = true;

    private readonly ConcurrentQueue<QueuedAudioPacket> packetQueue = new ConcurrentQueue<QueuedAudioPacket>();
    private readonly AutoResetEvent packetAvailable = new AutoResetEvent(false);
    private Thread decodeThread;
    private volatile bool stopDecodeThread;
    private int queuedPacketCount;
    private long droppedQueuedPackets;
    private volatile bool resumeRequested;

    private struct QueuedAudioPacket
    {
        public byte[] Data;
        public int Length;
    }

    private static float MsSince(long ticks)
    {
        if (ticks <= 0) return -1f;
        long now = Stopwatch.GetTimestamp();
        return (now - ticks) * 1000f / Stopwatch.Frequency;
    }

    private void TryStartPlaybackIfBuffered()
    {
        if (audioSource == null || audioSource.isPlaying)
        {
            return;
        }

        float bufferedSeconds;
        lock (pcmLock)
        {
            bufferedSeconds = pcmCount / (float)(SampleRate * OutputChannels);
        }

        if (bufferedSeconds >= prebufferSeconds)
        {
            audioSource.Play();
        }
    }

    private void EnsureRingSpaceUnsafe(int neededSamples)
    {
        if (pcmRing == null || pcmRingCapacity <= 0 || neededSamples <= 0)
        {
            return;
        }

        int overflow = pcmCount + neededSamples - pcmRingCapacity;
        if (overflow <= 0)
        {
            return;
        }

        overflow = Math.Min(overflow, pcmCount);
        pcmReadPos = (pcmReadPos + overflow) % pcmRingCapacity;
        pcmCount -= overflow;
        pcmSamplesDropped += overflow;
    }

    private void WriteSampleUnsafe(float sample)
    {
        if (pcmRing == null || pcmRingCapacity <= 0)
        {
            return;
        }

        if (pcmCount >= pcmRingCapacity)
        {
            pcmReadPos = (pcmReadPos + 1) % pcmRingCapacity;
            pcmCount--;
            pcmSamplesDropped++;
        }

        pcmRing[pcmWritePos] = Mathf.Clamp(sample, -1f, 1f);
        pcmWritePos = (pcmWritePos + 1) % pcmRingCapacity;
        pcmCount++;
    }

    private void WriteStereoUnsafe(float left, float right)
    {
        WriteSampleUnsafe(left);
        WriteSampleUnsafe(right);
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[AudioStream] Duplicate AudioStreamPlayer found, destroying this component on: " + name);
            Destroy(this);
            return;
        }

        Instance = this;

        audioSource = GetComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.loop = true;

        pcmRingCapacity = Mathf.Max(1, Mathf.CeilToInt(ringBufferSeconds * SampleRate * OutputChannels));
        pcmRing = new float[pcmRingCapacity];
        pcmReadPos = 0;
        pcmWritePos = 0;
        pcmCount = 0;
        pcmSamplesDropped = 0;

        opusDecoder = OpusCodecFactory.CreateDecoder(SampleRate, opusChannels);
        opusPcmScratch = new short[MaxOpusFrameSize * 2];

        audioSource.clip = AudioClip.Create(
            "NetworkStream",
            SampleRate * 300,
            OutputChannels,
            SampleRate,
            true,
            OnAudioRead);

        probeBuffer = new float[1024];

        if (autoPlayOnAwake)
        {
            audioSource.Play();
        }

        lastDebugAt = Time.unscaledTime;

        if (decodeOnBackgroundThread)
        {
            StartDecodeThread();
        }
    }

    private void OnEnable()
    {
        if (decodeOnBackgroundThread)
        {
            StartDecodeThread();
        }
    }

    private void OnDisable()
    {
        StopDecodeThread();
    }

    private void OnDestroy()
    {
        StopDecodeThread();
        if (audioSource != null)
        {
            audioSource.Stop();
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void Update()
    {
        if (autoResumeOnPacket && resumeRequested)
        {
            if (audioSource != null && !audioSource.isPlaying)
            {
                TryStartPlaybackIfBuffered();
            }

            if (audioSource != null && audioSource.isPlaying)
            {
                resumeRequested = false;
            }
        }

        if (!debugAudio)
        {
            return;
        }

        float now = Time.unscaledTime;
        if (now - lastDebugAt < debugIntervalSeconds)
        {
            return;
        }

        int queuedSamples;
        lock (pcmLock)
        {
            queuedSamples = pcmCount;
        }

        float bufferedSeconds = queuedSamples / (float)(SampleRate * OutputChannels);

        long pkt = packetsTotal;
        long byt = bytesTotal;
        float dt = Mathf.Max(0.001f, now - lastDebugAt);
        long dPkt = pkt - lastDebugPackets;
        long dByt = byt - lastDebugBytes;

        float rms = 0f;
        try
        {
            if (audioSource != null)
            {
                audioSource.GetOutputData(probeBuffer, 0);
                double sum = 0;
                for (int i = 0; i < probeBuffer.Length; i++)
                {
                    float v = probeBuffer[i];
                    sum += v * v;
                }
                rms = (float)Math.Sqrt(sum / probeBuffer.Length);
            }
        }
        catch { }

        float lastPktAgeMs = MsSince(lastPacketAtTicks);
        float lastDecodeAgeMs = MsSince(lastAudioDecodedAtTicks);

        //Debug.Log(
        //    "[AudioStream] " +
        //    "go=" + name +
        //    " id=" + GetInstanceID() +
        //    " isPlaying=" + (audioSource != null && audioSource.isPlaying) +
        //    " vol=" + (audioSource != null ? audioSource.volume.ToString("F2") : "n/a") +
        //    " sr=" + SampleRate +
        //    " outCh=" + OutputChannels +
        //    " opusCh=" + opusChannels +
        //    " preSkip=" + opusPreSkipFrames +
        //    " queuedSamples=" + queuedSamples +
        //    " bufferedMs=" + (bufferedSeconds * 1000f).ToString("F0") +
        //    " underruns=" + audioUnderruns +
        //    " decodeErr=" + decodeErrors +
        //    " opusDecoded=" + opusPacketsDecoded +
        //    " opusFail=" + opusDecodeFailures +
        //    " decRms=" + lastDecodedPcmRms.ToString("F4") +
        //    " decSamples=" + lastDecodedPcmSamples +
        //    " decPktBytes=" + lastDecodedPacketBytes +
        //    " lastOpusErr=" + (string.IsNullOrEmpty(lastOpusDecodeError) ? "n/a" : lastOpusDecodeError) +
        //    " lastDecodeMsAgo=" + (lastDecodeAgeMs >= 0 ? lastDecodeAgeMs.ToString("F0") : "n/a") +
        //    " lastPktAgeMs=" + (lastPktAgeMs >= 0 ? lastPktAgeMs.ToString("F0") : "n/a") +
        //    " lastRemote=" + (string.IsNullOrEmpty(lastRemote) ? "n/a" : lastRemote) +
        //    " lastPrefix=" + (string.IsNullOrEmpty(lastPrefix) ? "n/a" : lastPrefix) +
        //    " lastSize=" + lastPacketSize +
        //    " pktsPerSec=" + (dPkt / dt).ToString("F1") +
        //    " bytesPerSec=" + (dByt / dt).ToString("F0") +
        //    " outRms=" + rms.ToString("F4"));

        lastDebugAt = now;
        lastDebugPackets = pkt;
        lastDebugBytes = byt;
    }

    private void OnAudioRead(float[] data)
    {
        if (data == null || data.Length == 0)
        {
            return;
        }

        int filled = 0;
        lock (pcmLock)
        {
            int toRead = Mathf.Min(data.Length, pcmCount);
            int remaining = toRead;
            int writeIndex = 0;

            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, pcmRingCapacity - pcmReadPos);
                Array.Copy(pcmRing, pcmReadPos, data, writeIndex, chunk);
                pcmReadPos = (pcmReadPos + chunk) % pcmRingCapacity;
                pcmCount -= chunk;
                writeIndex += chunk;
                remaining -= chunk;
            }

            filled = toRead;
        }

        if (filled < data.Length)
        {
            audioUnderruns++;
            Array.Clear(data, filled, data.Length - filled);
        }
    }

    private void StartDecodeThread()
    {
        if (decodeThread != null)
        {
            return;
        }

        stopDecodeThread = false;
        decodeThread = new Thread(DecodeThreadLoop)
        {
            IsBackground = true,
            Name = "OpusDecodeThread"
        };
        decodeThread.Start();
    }

    private void StopDecodeThread()
    {
        stopDecodeThread = true;
        try { packetAvailable.Set(); } catch { }

        if (decodeThread != null && decodeThread.IsAlive)
        {
            try
            {
                if (!decodeThread.Join(80))
                {
                    decodeThread.Interrupt();
                    decodeThread.Join(20);
                }
            }
            catch { }
        }
        decodeThread = null;

        ClearQueuedPackets();
    }

    private void ClearQueuedPackets()
    {
        while (packetQueue.TryDequeue(out var packet))
        {
            if (packet.Data != null)
            {
                ByteArrayPool.Return(packet.Data);
            }
            Interlocked.Decrement(ref queuedPacketCount);
        }
    }

    private void EnqueuePacketCopy(byte[] data, int length)
    {
        if (data == null || length <= 0)
        {
            return;
        }

        if (length > data.Length)
        {
            length = data.Length;
        }

        int maxQueued = Mathf.Max(0, maxQueuedPackets);
        if (maxQueued > 0 && Volatile.Read(ref queuedPacketCount) >= maxQueued)
        {
            if (dropOldestOnQueueOverflow && packetQueue.TryDequeue(out var dropped))
            {
                if (dropped.Data != null)
                {
                    ByteArrayPool.Return(dropped.Data);
                }
                Interlocked.Decrement(ref queuedPacketCount);
                Interlocked.Increment(ref droppedQueuedPackets);
            }
            else
            {
                Interlocked.Increment(ref droppedQueuedPackets);
                return;
            }
        }

        byte[] copy = ByteArrayPool.Rent(length);
        Buffer.BlockCopy(data, 0, copy, 0, length);
        packetQueue.Enqueue(new QueuedAudioPacket { Data = copy, Length = length });
        Interlocked.Increment(ref queuedPacketCount);
        try { packetAvailable.Set(); } catch { }
    }

    private void DecodeThreadLoop()
    {
        try
        {
            while (!stopDecodeThread)
            {
                if (!packetQueue.TryDequeue(out var packet))
                {
                    packetAvailable.WaitOne(20);
                    continue;
                }

                Interlocked.Decrement(ref queuedPacketCount);
                try
                {
                    FeedAudioAuto(packet.Data, packet.Length);
                    if (autoResumeOnPacket)
                    {
                        lock (pcmLock)
                        {
                            if (pcmCount > 0)
                            {
                                resumeRequested = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (debugAudio)
                    {
                        Debug.LogWarning("[AudioStream] DecodeThread exception: " + ex.Message);
                    }
                }
                finally
                {
                    if (packet.Data != null)
                    {
                        ByteArrayPool.Return(packet.Data);
                    }
                }
            }
        }
        catch (ThreadAbortException)
        {
            // Domain reload / application quit: safe to ignore.
        }
        catch (ThreadInterruptedException)
        {
        }
    }

    public void FeedNetworkPacket(byte[] data, int length, IPEndPoint remote)
    {
        if (data == null || length <= 0)
        {
            return;
        }

        if (length > data.Length)
        {
            length = data.Length;
        }

        packetsTotal++;
        bytesTotal += length;

        lastPacketAtTicks = Stopwatch.GetTimestamp();
        lastPacketSize = length;
        lastRemote = remote != null ? (remote.Address + ":" + remote.Port) : lastRemote;
        lastPrefix = GetAsciiPrefix(data, length, 8);

        if (IsHeartbeatPacket(data, length))
        {
            return;
        }

        if (!decodeOnBackgroundThread)
        {
            FeedAudioAuto(data, length);
            if (autoResumeOnPacket)
            {
                TryStartPlaybackIfBuffered();
            }
            return;
        }

        EnqueuePacketCopy(data, length);
    }

    public void FeedPcmFloats(float[] samples, int length, int channels, float gain = 1f)
    {
        if (samples == null || length <= 0)
        {
            return;
        }

        if (channels <= 0)
        {
            channels = 1;
        }

        if (length > samples.Length)
        {
            length = samples.Length;
        }

        int frames = length / channels;
        if (frames <= 0)
        {
            return;
        }

        gain = Mathf.Clamp(gain, 0f, 10f);

        lock (pcmLock)
        {
            EnsureRingSpaceUnsafe(frames * OutputChannels);

            if (channels == 1)
            {
                for (int f = 0; f < frames; f++)
                {
                    float v = Mathf.Clamp(samples[f] * gain, -1f, 1f);
                    WriteStereoUnsafe(v, v);
                }
                samplesQueued += frames * OutputChannels;
            }
            else
            {
                for (int f = 0; f < frames; f++)
                {
                    int baseIndex = f * channels;
                    float l = Mathf.Clamp(samples[baseIndex] * gain, -1f, 1f);
                    float r = Mathf.Clamp(samples[baseIndex + 1] * gain, -1f, 1f);
                    WriteStereoUnsafe(l, r);
                }
                samplesQueued += frames * OutputChannels;
            }
        }

        lastAudioDecodedAtTicks = Stopwatch.GetTimestamp();

        if (autoResumeOnPacket)
        {
            TryStartPlaybackIfBuffered();
        }
    }

    private static string GetAsciiPrefix(byte[] data, int length, int maxChars)
    {
        if (data == null || length <= 0)
        {
            return string.Empty;
        }

        int n = Math.Min(length, maxChars);
        char[] chars = new char[n];
        for (int i = 0; i < n; i++)
        {
            byte b = data[i];
            chars[i] = (b >= 32 && b <= 126) ? (char)b : '.';
        }
        return new string(chars);
    }


    private bool TryDecodeOpusAudioPacket(byte[] packet, int offset, int length)
    {
        if (opusDecoder == null || packet == null || length <= 0)
        {
            return false;
        }

        if (offset < 0) offset = 0;
        if (length > packet.Length - offset) length = packet.Length - offset;
        if (length <= 0)
        {
            return false;
        }

        lastDecodedPacketBytes = length;

        int requiredPcmShorts = MaxOpusFrameSize * Math.Max(1, opusChannels);
        if (opusPcmScratch == null || opusPcmScratch.Length < requiredPcmShorts)
        {
            opusPcmScratch = new short[Math.Max(requiredPcmShorts, MaxOpusFrameSize * 2)];
        }
        short[] pcmShort = opusPcmScratch;

        byte[] decodeBytes = packet;
        if (offset != 0 || length != packet.Length)
        {
            if (opusScratch == null || opusScratch.Length < length)
            {
                int newLen = opusScratch != null ? opusScratch.Length : 0;
                while (newLen < length) newLen = Math.Max(2048, newLen * 2);
                opusScratch = new byte[newLen];
            }
            Buffer.BlockCopy(packet, offset, opusScratch, 0, length);
            decodeBytes = opusScratch;
        }

        int frames;
        try
        {
            frames = opusDecoder.Decode(decodeBytes, pcmShort.AsSpan(), MaxOpusFrameSize, false);
        }
        catch (Exception ex)
        {
            // If channel count is mismatched (mono vs stereo), retry once with the other common channel count.
            int altChannels = opusChannels == 2 ? 1 : 2;
            try
            {
                opusDecoder = OpusCodecFactory.CreateDecoder(SampleRate, altChannels);
                opusChannels = altChannels;
                opusPreSkipFrames = 0;

                requiredPcmShorts = MaxOpusFrameSize * Math.Max(1, opusChannels);
                if (opusPcmScratch == null || opusPcmScratch.Length < requiredPcmShorts)
                {
                    opusPcmScratch = new short[Math.Max(requiredPcmShorts, MaxOpusFrameSize * 2)];
                }
                pcmShort = opusPcmScratch;
                frames = opusDecoder.Decode(decodeBytes, pcmShort.AsSpan(), MaxOpusFrameSize, false);
            }
            catch (Exception ex2)
            {
                decodeErrors++;
                opusDecodeFailures++;
                lastOpusDecodeError = ex2.Message;
                if (debugAudio)
                {
                    Debug.LogWarning("[AudioStream] Opus decode failed: " + ex.Message + " (retry: " + ex2.Message + ")");
                }
                return false;
            }
        }

        if (frames <= 0)
        {
            return true;
        }

        int skipFrames = 0;
        if (opusPreSkipFrames > 0)
        {
            skipFrames = Math.Min(opusPreSkipFrames, frames);
            opusPreSkipFrames -= skipFrames;
        }

        int startFrame = skipFrames;
        int framesToWrite = frames - startFrame;
        if (framesToWrite <= 0)
        {
            return true;
        }

        float peak = 0f;
        double sumSq = 0;
        int outSamples = 0;

        lock (pcmLock)
        {
            EnsureRingSpaceUnsafe(framesToWrite * OutputChannels);

            if (opusChannels == 1)
            {
                for (int f = startFrame; f < frames; f++)
                {
                    float v = pcmShort[f] / 32768f;
                    WriteStereoUnsafe(v, v);

                    if (debugAudio)
                    {
                        float av = Mathf.Abs(v);
                        if (av > peak) peak = av;
                        sumSq += (double)v * v;
                        sumSq += (double)v * v;
                        outSamples += 2;
                    }
                }
                samplesQueued += framesToWrite * OutputChannels;
            }
            else
            {
                int baseIndex = startFrame * opusChannels;
                int sampleCount = framesToWrite * opusChannels;
                for (int i = 0; i < sampleCount; i += opusChannels)
                {
                    float l = pcmShort[baseIndex + i] / 32768f;
                    float r = pcmShort[baseIndex + i + 1] / 32768f;
                    WriteStereoUnsafe(l, r);

                    if (debugAudio)
                    {
                        float al = Mathf.Abs(l);
                        float ar = Mathf.Abs(r);
                        if (al > peak) peak = al;
                        if (ar > peak) peak = ar;
                        sumSq += (double)l * l;
                        sumSq += (double)r * r;
                        outSamples += 2;
                    }
                }
                samplesQueued += framesToWrite * OutputChannels;
            }
        }

        if (debugAudio)
        {
            lastDecodedPcmRms = outSamples > 0 ? (float)Math.Sqrt(sumSq / outSamples) : 0f;
            lastDecodedPcmSamples = outSamples;
        }

        opusPacketsDecoded++;
        lastAudioDecodedAtTicks = Stopwatch.GetTimestamp();
        return true;
    }

    private static bool IsHeartbeatPacket(byte[] data, int length)
    {
        if (data == null || length < 6) return false;
        if (length > data.Length) length = data.Length;
        return data[0] == 0xAA &&
               data[1] == 0xBB &&
               data[2] == 0xCC &&
               data[3] == 0xDD &&
               data[4] == 0xEE &&
               data[5] == 0xFF;
    }

    private void FeedAudioAuto(byte[] data, int length)
    {
        if (data == null || length <= 0)
        {
            return;
        }

        if (length > data.Length)
        {
            length = data.Length;
        }

        if (IsHeartbeatPacket(data, length))
        {
            return;
        }

        // Server sends one Opus packet per UDP datagram (raw Opus frames).
        if (TryDecodeOpusAudioPacket(data, 0, length))
        {
            return;
        }

        // Some senders prepend a small custom header (message type / seq / timestamp) before the Opus payload.
        // Try a few common header sizes before giving up.
        int[] candidateOffsets = { 1, 2, 4, 8, 12, 16, 20, 24, 28, 32 };
        for (int i = 0; i < candidateOffsets.Length; i++)
        {
            int offset = candidateOffsets[i];
            if (length > offset && TryDecodeOpusAudioPacket(data, offset, length - offset))
            {
                return;
            }
        }

        // If it isn't Opus, do not interpret random bytes as PCM (that produces "tick" noise).
        return;
    }

}
