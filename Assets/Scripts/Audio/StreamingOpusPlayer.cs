using System;
using System.Collections;
using System.IO;
using System.Threading;
using Concentus;
using Concentus.Oggfile;
using UnityEngine;
using Debug = AppLog;
using UnityEngine.Networking;

[RequireComponent(typeof(AudioSource))]
public class StreamingOpusPlayer : MonoBehaviour
{
    public string filePath = "TestOpus.opus";
    public float prebufferSeconds = 0.5f;
    public float ringBufferSeconds = 10f;

    private AudioSource audioSource;

    // ring buffer
    private float[] ringBuffer;
    private int ringCapacity;
    private int ringReadPos = 0;
    private int ringWritePos = 0;
    private object ringLock = new object();

    //private volatile bool isPlaying = false;
    private volatile bool stopRequested = false;

    private AutoResetEvent dataAvailable = new AutoResetEvent(false);

    private int sampleRate = 48000;
    private int channels = 2;

    private Thread decodeThread;
    private byte[] sourceBytes;
    private Stream sourceStream;

    private AudioClip streamingClip;

    void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.loop = false;
            }

    void Start()
    {
        StartCoroutine(PrepareAndStart());
    }

    
    private void OnDisable()
    {
        StopDecoder();
    }

    private void OnDestroy()
    {
        StopDecoder();
    }

    private void StopDecoder()
    {
        stopRequested = true;
        try
        {
            dataAvailable?.Set();
        }
        catch { }

        if (decodeThread != null && decodeThread.IsAlive)
        {
            try
            {
                if (!decodeThread.Join(500))
                {
                    decodeThread.Interrupt();
                }
            }
            catch { }
        }
        decodeThread = null;

        try
        {
            sourceStream?.Dispose();
        }
        catch { }
        sourceStream = null;

        try
        {
            audioSource?.Stop();
        }
        catch { }
    }IEnumerator PrepareAndStart()
    {
        string fullPath = Path.Combine(Application.streamingAssetsPath, filePath);

        // ---- Load OPUS file ----
        if (Application.platform == RuntimePlatform.Android)
        {
            using (var uwr = UnityWebRequest.Get(fullPath))
            {
                uwr.SendWebRequest();
                while (!uwr.isDone) yield return null;

                if (uwr.result != UnityWebRequest.Result.Success)
                {
                    yield break;
                }

                sourceBytes = uwr.downloadHandler.data;
            }
        }
        else
        {
            if (File.Exists(fullPath))
            {
                sourceStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            else
            {
                yield break;
            }
        }

        // ---- Init Ring Buffer ----
        ringCapacity = Mathf.CeilToInt(ringBufferSeconds * sampleRate * channels);
        ringBuffer = new float[ringCapacity];
        ringReadPos = ringWritePos = 0;

        // ---- AudioClip ----
        // 至少给几分钟长度，否则 Unity 不会调用 OnAudioRead
        int clipLengthSamples = sampleRate * channels * 300; // 5 min placeholder

        streamingClip = AudioClip.Create(
            "OpusStreamStreaming",
            clipLengthSamples,
            channels,
            sampleRate,
            true,
            OnAudioRead,
            OnAudioSetPosition
        );
        audioSource.clip = streamingClip;

        // ---- Start Decoder Thread ----
        stopRequested = false;

        decodeThread = new Thread(DecodeThreadLoop)
        {
            IsBackground = true,
            Name = "OpusDecodeThread"
        };

        decodeThread.Start();

        // ---- Wait for Prebuffer ----
        float waited = 0f;
        int requiredSamples = Mathf.CeilToInt(prebufferSeconds * sampleRate * channels);

        while (!stopRequested)
        {
            int available;
            lock (ringLock)
            {
                available = (ringWritePos >= ringReadPos)
                    ? ringWritePos - ringReadPos
                    : ringCapacity - ringReadPos + ringWritePos;
            }

            if (available >= requiredSamples)
            {
                break;
            }

            yield return null;
            waited += Time.deltaTime;

            if (waited > 5f)
            {
                break;
            }
        }

        // ---- Start Playback ----
        //isPlaying = true;

        audioSource.Play();

    }

    // ---- Audio Thread (Unity calls this) ----
    private void OnAudioRead(float[] data)
    {
        int len = data.Length;
        int filled = 0;

        lock (ringLock)
        {
            while (filled < len)
            {
                int available = (ringWritePos >= ringReadPos)
                    ? ringWritePos - ringReadPos
                    : ringCapacity - ringReadPos + ringWritePos;

                if (available <= 0)
                {
                    break;
                }

                int chunk = Math.Min(len - filled, ringCapacity - ringReadPos);
                chunk = Math.Min(chunk, available);

                Array.Copy(ringBuffer, ringReadPos, data, filled, chunk);

                ringReadPos = (ringReadPos + chunk) % ringCapacity;
                filled += chunk;
            }
        }

        for (int i = filled; i < len; i++)
            data[i] = 0f;

        dataAvailable.Set();
    }

    private void OnAudioSetPosition(int newPosition)
    {

    }

    // ---- Background decode thread ----
    private void DecodeThreadLoop()
    {
        try
        {
            Stream streamToUse = sourceBytes != null
                ? new MemoryStream(sourceBytes)
                : sourceStream;

            if (streamToUse == null)
            {
                return;
            }

            IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
            OpusOggReadStream ogg = new OpusOggReadStream(decoder, streamToUse);

            while (!stopRequested && ogg.HasNextPacket)
            {
                short[] decoded = ogg.DecodeNextPacket();

                if (decoded == null || decoded.Length == 0)
                {
                    continue;
                }

                float[] pcm = ConvertShortsToFloats(decoded);

                bool written = false;

                while (!written && !stopRequested)
                {
                    lock (ringLock)
                    {
                        int free = (ringWritePos >= ringReadPos)
                            ? ringCapacity - (ringWritePos - ringReadPos) - 1
                            : (ringReadPos - ringWritePos) - 1;

                        if (pcm.Length <= free)
                        {
                            int first = Math.Min(pcm.Length, ringCapacity - ringWritePos);
                            Array.Copy(pcm, 0, ringBuffer, ringWritePos, first);

                            if (first < pcm.Length)
                            {
                                int second = pcm.Length - first;
                                Array.Copy(pcm, first, ringBuffer, 0, second);
                                ringWritePos = second;
                            }
                            else
                            {
                                ringWritePos = (ringWritePos + first) % ringCapacity;
                            }

                            written = true;
                        }
                    }

                    if (!written)
                    {
                        dataAvailable.WaitOne(20);
                    }
                }
            }
        }
                catch (ThreadAbortException)
        {
            // Domain reload / application quit: safe to ignore.
        }
        catch (Exception ex)
        {
            if (!stopRequested)
            {
                Debug.LogError("[StreamingOpus] DecodeThread exception: " + ex);
            }
        }
    }

    private float[] ConvertShortsToFloats(short[] s)
    {
        float[] f = new float[s.Length];
        for (int i = 0; i < s.Length; i++) f[i] = s[i] / 32768f;
        return f;
    }
}

