using TMPro;
using UnityEngine;

public class DebugTextManager : MonoBehaviour
{
    public static DebugTextManager Instance;

    public TextMeshProUGUI _debugClienttext;
    public TextMeshProUGUI _debugServertext;

    public TextMeshProUGUI _debugStatetext1;
    public TextMeshProUGUI _debugStatetext2;
    public TextMeshProUGUI _debugStatetext3;

    public TextMeshProUGUI _debugText1;
    public TextMeshProUGUI _debugText2;
    public TextMeshProUGUI _debugText3;

    public float updateInterval = 1f;
    [SerializeField] private bool enableJavaVideoStats = false;

    private float _accum;
    private int _frames;
    private float _timeLeft;
    private string _lastNotification;

    public float CurrentFPS { get; private set; }
    public float CurrentVideoFPS { get; private set; }

    private int _videoFramesPresented;
    private float _videoTimeAccum;

    private Quest3VideoPlayer.QuestStreamVideoPlayer _questStreamPlayer;

    private bool _hasPrevJavaVideoTotals;
    private long _prevJavaPacketsReceived;
    private long _prevJavaBytesReceived;
    private long _prevJavaFramesReassembled;
    private long _prevJavaNalSubmitted;
    private long _prevJavaFramesDequeued;
    private DecoderFlavor? _lastJavaVideoDecoderFlavor;

    public void NotifyVideoFramePresented()
    {
        _videoFramesPresented++;
    }

    public void PushNotification(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message == _lastNotification)
        {
            return;
        }

        _lastNotification = message;

        if (_debugStatetext3 != null)
        {
            _debugStatetext3.text = _debugStatetext2 != null ? _debugStatetext2.text : string.Empty;
        }

        if (_debugStatetext2 != null)
        {
            _debugStatetext2.text = _debugStatetext1 != null ? _debugStatetext1.text : string.Empty;
        }

        if (_debugStatetext1 != null)
        {
            _debugStatetext1.text = message;
        }
    }

    private void Awake()
    {
        Instance = this;
    }

    private void Start()
    {
        _timeLeft = updateInterval;
        _questStreamPlayer = FindObjectOfType<Quest3VideoPlayer.QuestStreamVideoPlayer>();
    }

    private void Update()
    {
        _timeLeft -= Time.unscaledDeltaTime;
        _accum += Time.timeScale / Time.unscaledDeltaTime;
        _frames++;
        _videoTimeAccum += Time.unscaledDeltaTime;

        if (_timeLeft > 0f)
        {
            return;
        }

        CurrentFPS = _accum / Mathf.Max(1, _frames);
        _timeLeft = updateInterval;
        _accum = 0f;
        _frames = 0;

        float videoInterval = Mathf.Max(0.0001f, _videoTimeAccum);
        CurrentVideoFPS = _videoFramesPresented / videoInterval;
        _videoFramesPresented = 0;
        _videoTimeAccum = 0f;

        if (enableJavaVideoStats && _questStreamPlayer != null)
        {
            UpdateJavaVideoStats(_questStreamPlayer, videoInterval);
        }
    }

    private interface IJavaVideoPlayer
    {
        long GetPacketsReceived();
        long GetBytesReceived();
        long GetFramesReceived();
        long GetFramesReassembled();
        long GetNalSubmitted();
        long GetFramesEnqueued();
        long GetFramesDequeued();
        long GetProducedFrameCount();
        int GetPendingFrameCount();
    }

    private sealed class QuestStreamPlayerAdapter : IJavaVideoPlayer
    {
        private readonly Quest3VideoPlayer.QuestStreamVideoPlayer _player;

        public QuestStreamPlayerAdapter(Quest3VideoPlayer.QuestStreamVideoPlayer player)
        {
            _player = player;
        }

        public long GetPacketsReceived() => _player.GetPacketsReceived();
        public long GetBytesReceived() => _player.GetBytesReceived();
        public long GetFramesReceived() => _player.GetFramesReceived();
        public long GetFramesReassembled() => _player.GetFramesReassembled();
        public long GetNalSubmitted() => _player.GetNalSubmitted();
        public long GetFramesEnqueued() => _player.GetFramesEnqueued();
        public long GetFramesDequeued() => _player.GetFramesDequeued();
        public long GetProducedFrameCount() => _player.GetProducedFrameCount();
        public int GetPendingFrameCount() => _player.GetPendingFrameCount();
    }

    private void UpdateJavaVideoStats(object player, float videoInterval)
    {
        if (player is not Quest3VideoPlayer.QuestStreamVideoPlayer streamPlayer)
        {
            return;
        }

        IJavaVideoPlayer adapter = new QuestStreamPlayerAdapter(streamPlayer);

        try
        {
            DecoderFlavor currentDecoderFlavor = DataManager.Instance != null
                ? DataManager.Instance.videoDecodeMode
                : DecoderFlavor.AV1;

            long packetsReceived = adapter.GetPacketsReceived();
            long bytesReceived = adapter.GetBytesReceived();
            long framesReceived = adapter.GetFramesReceived();
            long framesReassembled = adapter.GetFramesReassembled();
            long nalSubmitted = adapter.GetNalSubmitted();
            long framesEnqueued = adapter.GetFramesEnqueued();
            long framesDequeued = adapter.GetFramesDequeued();
            long producedFrames = adapter.GetProducedFrameCount();
            int pendingFrames = adapter.GetPendingFrameCount();

            float packetsFps = 0f;
            float byteRateMBps = 0f;
            float reassembledFps = 0f;
            float submittedFps = 0f;
            float dequeuedFps = 0f;

            bool resetTotals = !_hasPrevJavaVideoTotals ||
                               _lastJavaVideoDecoderFlavor != currentDecoderFlavor ||
                               packetsReceived < _prevJavaPacketsReceived ||
                               bytesReceived < _prevJavaBytesReceived ||
                               framesReassembled < _prevJavaFramesReassembled ||
                               nalSubmitted < _prevJavaNalSubmitted ||
                               framesDequeued < _prevJavaFramesDequeued;

            if (!resetTotals)
            {
                packetsFps = (packetsReceived - _prevJavaPacketsReceived) / videoInterval;
                long deltaBytes = bytesReceived - _prevJavaBytesReceived;
                byteRateMBps = (deltaBytes / videoInterval) / (1024f * 1024f);
                reassembledFps = (framesReassembled - _prevJavaFramesReassembled) / videoInterval;
                submittedFps = (nalSubmitted - _prevJavaNalSubmitted) / videoInterval;
                dequeuedFps = (framesDequeued - _prevJavaFramesDequeued) / videoInterval;
            }

            _prevJavaPacketsReceived = packetsReceived;
            _prevJavaBytesReceived = bytesReceived;
            _prevJavaFramesReassembled = framesReassembled;
            _prevJavaNalSubmitted = nalSubmitted;
            _prevJavaFramesDequeued = framesDequeued;
            _hasPrevJavaVideoTotals = true;
            _lastJavaVideoDecoderFlavor = currentDecoderFlavor;

            float reassemblyRate = framesReceived > 0 ? (framesReassembled * 100f / framesReceived) : 0f;
            int lastFrameId = DataManager.Instance != null ? DataManager.Instance.lastFrameID : -1;
            float videoRttMs = 0f;
            if (DataManager.Instance != null &&
                DataManager.Instance.robotStateMessage != null &&
                DataManager.Instance.robotStateMessage.data != null)
            {
                videoRttMs = (float)DataManager.Instance.robotStateMessage.data.video_rtt_ms;
            }

            if (_debugText1 != null)
            {
                _debugText1.text =
                    $"<b>Decoder:</b> {currentDecoderFlavor}\n" +
                    $"<b>Unity FPS:</b> {CurrentFPS:F1} | <b>Video Render:</b> {CurrentVideoFPS:F1}\n" +
                    $"<b>UDP:</b> {packetsReceived:N0} pkts ({packetsFps:F1}/s) | {byteRateMBps:F2} MB/s\n" +
                    $"<b>Reassembly:</b> {framesReassembled:N0}/{framesReceived:N0} ({reassemblyRate:F1}%) | {reassembledFps:F1}/s\n" +
                    $"<b>Decode:</b> submitted={nalSubmitted:N0} ({submittedFps:F1}/s) | produced={producedFrames:N0}\n" +
                    $"<b>Queue:</b> enqueued={framesEnqueued:N0} | dequeued={framesDequeued:N0} ({dequeuedFps:F1}/s) | pending={pendingFrames}\n" +
                    $"<b>Frame:</b> id={lastFrameId} | rtt={videoRttMs:N0} ms";
            }
        }
        catch (System.Exception ex)
        {
            if (_debugText1 != null)
            {
                _debugText1.text = $"<color=red>获取Java视频统计失败: {ex.Message}</color>";
            }
        }
    }
}
