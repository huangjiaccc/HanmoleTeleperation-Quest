using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using Debug = AppLog;

namespace Quest3VideoPlayer
{
    /// <summary>
    /// Receives AV1 frames over UDP and feeds them to the Android streaming decoder.
    /// </summary>
    public class QuestStreamVideoPlayer : MonoBehaviour
    {
        public enum ConfigMode
        {
            ProductionFixed = 0,
            DebugInspector = 1,
        }

        [Header("Config")]
        [SerializeField] private ConfigMode configMode = ConfigMode.ProductionFixed;

        public struct VideoReleasePacketHeader
        {
            public int Timestamp;
            public int FrameId;
            public int SplitId;
            public int TotalSplits;
            public int FragmentId;
            public int TotalFragments;
            public int FragmentSize;
            public int TestingId;
        }

        [Header("Stream Format")]
        [SerializeField, Min(16)] private int expectedWidth = 2560;
        [SerializeField, Min(16)] private int expectedHeight = 1440;

        [Header("Stability")]
        [SerializeField, Range(0.5f, 10f)] private float hardwareStallRestartSeconds = 2.0f;
        [SerializeField, Range(1, 5)] private int hardwareStallMaxRestarts = 2;
        [SerializeField, Range(0.5f, 30f)] private float hardwareStallCooldownSeconds = 3.0f;
        [SerializeField] private bool enableHardwareStallRecovery = false;

        [Header("Targets")]
        [SerializeField] private Renderer targetRenderer;
        [SerializeField] private RawImage targetUI;
        [SerializeField] private RawImage targetUI2;

        [Header("Behavior")]
        [SerializeField] private bool autoStart = true;
        [SerializeField] private bool verboseLogging = false;
        [SerializeField] private bool flipTextureHorizontally = true;
        [SerializeField] private bool flipTextureVertically = true;
        [Tooltip("When enabled (Vulkan + AV1), Java outputs frames as Android HardwareBuffers to avoid CPU YUV->RGBA conversion.")]
        [SerializeField] private bool preferVulkanHardwareBufferFrames = true;
        [Tooltip("How many HardwareBuffer frames Unity will drain per update before presenting only the newest one.")]
        [SerializeField, Range(1, 8)] private int maxHardwareFrameDrainPerTick = 5;

        [Header("Side-By-Side Blend")]
        [Tooltip("Blend a side-by-side (left|right) stream into a single stitched image.")]
        [SerializeField] private bool enableSideBySideBlend = false;
        public enum SideBySideOutputMode
        {
            SeamBlend = 0,
            OverlayAverage = 1,
        }
        [Tooltip("SeamBlend keeps left/right split with a seam mix; OverlayAverage blends both eyes over the full frame.")]
        [SerializeField] private SideBySideOutputMode sideBySideOutputMode = SideBySideOutputMode.SeamBlend;
        [Tooltip("Overlay blend weight (0 = left only, 1 = right only).")]
        [SerializeField, Range(0f, 1f)] private float sideBySideOverlayWeight = 0.5f;
        [SerializeField] private Vector4 sideBySideLeftRect = new Vector4(0f, 0f, 0.5f, 1f);
        [SerializeField] private Vector4 sideBySideRightRect = new Vector4(0.5f, 0f, 0.5f, 1f);
        [SerializeField, Range(0f, 1f)] private float sideBySideSeamCenter = 0.5f;
        [SerializeField, Range(0f, 0.5f)] private float sideBySideBlendWidth = 0.05f;
        [Tooltip("Optional material override. If set, it is assigned to the Renderer/RawImage targets.")]
        [SerializeField] private Material sideBySideBlendMaterialOverride;

        [Header("Vulkan YCbCr")]
        [Tooltip("When enabled, decoder color info from Java is applied in native; C# will not push YCbCr/manual params.")]
        [SerializeField] private bool useJavaDecoderColorInfo = true;
        [Tooltip("Controls the Vulkan sampler YCbCr model used for HardwareBuffer import. 'Auto' uses the driver suggestion (plus a small heuristic if the driver reports identity).")]
        [SerializeField] private YcbcrModelSetting ycbcrModel = YcbcrModelSetting.Auto;
        [Tooltip("Controls the Vulkan sampler YCbCr range used for HardwareBuffer import. 'Auto' uses the driver suggestion.")]
        [SerializeField] private YcbcrRangeSetting ycbcrRange = YcbcrRangeSetting.Auto;
        [Tooltip("If colors look magenta/pink, try enabling this to swap Cb/Cr during Vulkan YCbCr sampling.")]
        [SerializeField] private bool swapCbCr = false;
        [Tooltip("If colors look like R/B channels are swapped (pink/yellow cast), enable this to swap Red/Blue after conversion (swizzleMode=2).")]
        [SerializeField] private bool swapRedBlue = false;
        [Tooltip("Overrides Vulkan YCbCr chroma siting. If you see colored fringes or black areas turning magenta/yellow, try Midpoint vs CositedEven.")]
        [SerializeField] private YcbcrChromaOffsetSetting chromaX = YcbcrChromaOffsetSetting.Auto;
        [Tooltip("Overrides Vulkan YCbCr chroma siting. If you see colored fringes or black areas turning magenta/yellow, try Midpoint vs CositedEven.")]
        [SerializeField] private YcbcrChromaOffsetSetting chromaY = YcbcrChromaOffsetSetting.Auto;
        [Tooltip("Uses manual YUV->RGB conversion in the native shader (forces YCBCR_IDENTITY sampler). Enable this when fixed-function YCbCr conversion produces tinted output.")]
        [SerializeField] private bool manualYuvConversion = true;
        [Tooltip("Force the native path to use hardware YCbCr conversion (disables manual shader conversion).")]
        [SerializeField] private bool forceHardwareYcbcrConversion = true;
        [Tooltip("Force manual YUV conversion to use the same BT.601 narrow-range matrix as the legacy Java CPU path.")]
        [SerializeField] private bool forceJavaCpuYuvMatrix = false;
        [Tooltip("Swaps U/V in manual conversion. If the image looks magenta/green, try enabling this.")]
        [SerializeField] private bool swapUv = false;
        [Tooltip("Inverts U (u = 1 - u) in manual conversion.")]
        [SerializeField] private bool invertU = false;
        [Tooltip("Inverts V (v = 1 - v) in manual conversion.")]
        [SerializeField] private bool invertV = false;
        [Tooltip("Selects which sampled channel corresponds to Y/U/V when using manual conversion.")]
        [SerializeField] private ManualYuvChannelOrder manualYuvChannelOrder = ManualYuvChannelOrder.YUV;
        [Tooltip("Debug output for manual conversion: show raw channels (Y/U/V).")]
        [SerializeField] private ManualYuvDebugMode manualYuvDebug = ManualYuvDebugMode.Normal;
        [Tooltip("How the shader interprets sampled Y/U/V when using manual conversion.")]
        [SerializeField] private ManualYuvInputMode manualYuvInputMode = ManualYuvInputMode.Normalized;
        private int manualYuvInputModeOverride = int.MinValue;

        [SerializeField] private bool logFrameChecksums = true;

        [Header("Network")]
        private string listenAddress = "0.0.0.0";
        private string expectedSenderIp = string.Empty;
        private string remoteHost = string.Empty;
        private int remotePort;
        [SerializeField, Range(1024, 65535)] private int listenPort = 5000;
        [SerializeField, Range(0, 65535)] private int listenPortSecondary = 4000;

        private static readonly int SideBySideLeftRectId = Shader.PropertyToID("_LeftRect");
        private static readonly int SideBySideRightRectId = Shader.PropertyToID("_RightRect");
        private static readonly int SideBySideSeamCenterId = Shader.PropertyToID("_SeamCenter");
        private static readonly int SideBySideBlendWidthId = Shader.PropertyToID("_BlendWidth");
        private static readonly int SideBySideOverlayModeId = Shader.PropertyToID("_OverlayMode");
        private static readonly int SideBySideOverlayWeightId = Shader.PropertyToID("_OverlayWeight");
        private const string SideBySideBlendShaderName = "Quest3VideoPlayer/SideBySideBlend";
        private Material sideBySideRuntimeMaterial;
        private Material sideBySideOriginalRendererMaterial;
        private Material sideBySideOriginalUiMaterial;
        private Material sideBySideOriginalUi2Material;
        private bool sideBySideMaterialApplied;

        private void Awake()
        {
            if (configMode == ConfigMode.ProductionFixed)
            {
                ApplyProductionDefaults();
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (configMode == ConfigMode.ProductionFixed)
            {
                ApplyProductionDefaults();
            }
        }
#endif

        private void ApplyProductionDefaults()
        {
            // ProductionFixed defaults are based on the current QuestMain scene baseline.
            expectedWidth = 2560;
            expectedHeight = 1440;
            autoStart = true;
            verboseLogging = false;
            flipTextureHorizontally = true;
            flipTextureVertically = true;

            preferVulkanHardwareBufferFrames = true;
            useJavaDecoderColorInfo = true;

            ycbcrModel = YcbcrModelSetting.Auto;
            ycbcrRange = YcbcrRangeSetting.Auto;
            swapCbCr = false;
            swapRedBlue = false;
            chromaX = YcbcrChromaOffsetSetting.Auto;
            chromaY = YcbcrChromaOffsetSetting.Auto;

            manualYuvConversion = true;
            forceHardwareYcbcrConversion = false;
            forceJavaCpuYuvMatrix = false;
            swapUv = false;
            invertU = false;
            invertV = false;
            manualYuvChannelOrder = ManualYuvChannelOrder.YUV;
            manualYuvDebug = ManualYuvDebugMode.Normal;
            manualYuvInputMode = ManualYuvInputMode.ByteNarrowJava;
            manualYuvInputModeOverride = int.MinValue;

            logFrameChecksums = false;
            listenPort = 5000;
            listenPortSecondary = 4000;
            maxInFlightHardwareFrames = 2;
            maxHardwareFrameDrainPerTick = 5;
            enableHardwareStallRecovery = false;
        }

        private void EnsureSideBySideBlendMaterial()
        {
            if (!enableSideBySideBlend)
            {
                RestoreSideBySideOriginalMaterials();
                return;
            }

            Material material = ResolveSideBySideMaterial();
            if (material == null)
            {
                return;
            }

            if (!sideBySideMaterialApplied)
            {
                CacheSideBySideOriginalMaterials();
                ApplySideBySideMaterialToTargets(material);
                sideBySideMaterialApplied = true;
            }

            ApplySideBySideProperties(material);
        }

        private Material ResolveSideBySideMaterial()
        {
            if (sideBySideBlendMaterialOverride != null)
            {
                return sideBySideBlendMaterialOverride;
            }

            if (sideBySideRuntimeMaterial != null)
            {
                return sideBySideRuntimeMaterial;
            }

            Material resourceMaterial = Resources.Load<Material>("QuestSideBySideBlend");
            if (resourceMaterial != null)
            {
                sideBySideRuntimeMaterial = new Material(resourceMaterial);
                return sideBySideRuntimeMaterial;
            }

            Shader shader = Shader.Find(SideBySideBlendShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] Blend shader not found: {SideBySideBlendShaderName}");
                return null;
            }

            sideBySideRuntimeMaterial = new Material(shader);
            return sideBySideRuntimeMaterial;
        }

        private void CacheSideBySideOriginalMaterials()
        {
            if (targetRenderer != null && sideBySideOriginalRendererMaterial == null)
            {
                sideBySideOriginalRendererMaterial = targetRenderer.sharedMaterial;
            }

            if (targetUI != null && sideBySideOriginalUiMaterial == null)
            {
                sideBySideOriginalUiMaterial = targetUI.material;
            }

            if (targetUI2 != null && sideBySideOriginalUi2Material == null)
            {
                sideBySideOriginalUi2Material = targetUI2.material;
            }
        }

        private void ApplySideBySideMaterialToTargets(Material material)
        {
            if (targetRenderer != null)
            {
                targetRenderer.sharedMaterial = material;
            }

            if (targetUI != null)
            {
                targetUI.material = material;
            }

            if (targetUI2 != null)
            {
                targetUI2.material = material;
            }
        }

        private void RestoreSideBySideOriginalMaterials()
        {
            if (!sideBySideMaterialApplied)
            {
                return;
            }

            if (targetRenderer != null && sideBySideOriginalRendererMaterial != null)
            {
                targetRenderer.sharedMaterial = sideBySideOriginalRendererMaterial;
            }

            if (targetUI != null)
            {
                targetUI.material = sideBySideOriginalUiMaterial;
            }

            if (targetUI2 != null)
            {
                targetUI2.material = sideBySideOriginalUi2Material;
            }

            sideBySideMaterialApplied = false;
        }

        private void ApplySideBySideProperties(Material material)
        {
            material.SetVector(SideBySideLeftRectId, sideBySideLeftRect);
            material.SetVector(SideBySideRightRectId, sideBySideRightRect);
            material.SetFloat(SideBySideSeamCenterId, Mathf.Clamp01(sideBySideSeamCenter));
            material.SetFloat(SideBySideBlendWidthId, Mathf.Clamp(sideBySideBlendWidth, 0f, 0.5f));
            material.SetFloat(SideBySideOverlayModeId, sideBySideOutputMode == SideBySideOutputMode.OverlayAverage ? 1f : 0f);
            material.SetFloat(SideBySideOverlayWeightId, Mathf.Clamp01(sideBySideOverlayWeight));
        }

        private AndroidJavaObject decoder;
        private Coroutine frameRoutine;
        private int framesDecoded;
        private int emptyFrameTicks;
        private float lastFramePresentedTime;
        private float nextHardwareStallRestartTime;
        private int hardwareStallRestartAttempts;
        private const int MaxCachedReleasePacketHeaders = 32;
        private readonly Queue<VideoReleasePacketHeader> releasePacketHeaderQueue = new Queue<VideoReleasePacketHeader>();
        private VideoReleasePacketHeader? latestReleasePacketHeader;
        private sbyte[] javaHeartbeatBridge;
        private IntPtr submitHeartbeatPayloadMethodId = IntPtr.Zero;

        private readonly Queue<InFlightHardwareResource> inFlightHardwareResources = new Queue<InFlightHardwareResource>(8);
        private AndroidJavaObject calibrationHoldBundle;
        private AndroidJavaObject retainedHardwareRetryBundle;
        [SerializeField, Range(1, 8)] private int maxInFlightHardwareFrames = 2;
        private bool usingHardwareBufferFrames;
        private Texture externalHardwareTexture;
        private bool resumeStreamAfterPause;
        private bool applicationPauseState;
        private bool applicationFocusState = true;
        private Coroutine resumeStreamCoroutine;
        private bool useNativeHardwareBufferImporter;
        private IntPtr questVulkanStreamHandle = IntPtr.Zero;
        private IntPtr questVulkanRenderEventFunc = IntPtr.Zero;
        private bool questVulkanUnityTextureBound;
        private AndroidJavaClass hardwareBufferNativeBridge;
        private int nativeHardwareImportFailureCount;
        private bool nativeHardwareImportIssued;
        private bool nativeHardwareImportCompletedOnce;
        private float lastNativeImportPendingLogTime;
        private float lastDequeueLogTime;
        private float lastNativeImportStatusLogTime;
        private float lastPipelineStatsLogTime;
        private long lastPipelineStatsFramesEnqueued;
        private long lastPipelineStatsFramesDequeued;
        private long lastPipelineStatsProduced;
        private long lastPipelineStatsPresented;
        private bool zeroCopyPathConfirmedThisStream;
        private readonly QuestVulkanYcbcrOverrideApplier ycbcrOverrideApplier = new QuestVulkanYcbcrOverrideApplier();

        private VideoColorCalibrator colorCalibrator;

        private bool ahbReflectionInitialized;
        private Type unityAndroidHardwareBufferType;
        private MethodInfo unityAndroidHardwareBufferImportMethod;
        private bool unityAndroidHardwareBufferImportTakesBool;
        private MethodInfo unityAndroidHardwareBufferGetNativeTexturePtrMethod;
        private MethodInfo unityAndroidHardwareBufferDisposeMethod;

        private enum YcbcrModelSetting
        {
            Auto = 0,
            Force709 = 1,
            Force601 = 2,
            Force2020 = 3,
            ForceRgbIdentity = 4,
            ForceYcbcrIdentity = 5,
        }

        private enum YcbcrRangeSetting
        {
            Auto = 0,
            ForceNarrow = 1,
            ForceFull = 2,
        }

        private enum YcbcrChromaOffsetSetting
        {
            Auto = 0,
            CositedEven = 1,
            Midpoint = 2,
        }

        private enum ManualYuvDebugMode
        {
            Normal = 0,
            ShowY = 1,
            ShowU = 2,
            ShowV = 3,
        }

        private enum ManualYuvChannelOrder
        {
            YUV = 0,
            YVU = 1,
            UYV = 2,
            UVY = 3,
            VYU = 4,
            VUY = 5,
        }

        private enum ManualYuvInputMode
        {
            Normalized = 0,
            ByteNarrowJava = 1,
            ByteFull = 2,
        }

        private enum QuestVulkanTextureOperationStatus
        {
            Invalid = -1,
            Pending = 0,
            Success = 1,
            Failed = 2,
        }

        private enum HardwareFrameApplyResult
        {
            Dropped = 0,
            Presented = 1,
            RetryNextTick = 2,
        }

        private struct InFlightHardwareResource
        {
            public AndroidJavaObject Bundle;
            public object UnityHardwareBuffer;
        }

        public event Action<VideoReleasePacketHeader> ReleasePacketHeaderReceived;

        private void Start()
        {
            AppLog.Log("[QuestStreamVideoPlayer] Start() called");
            colorCalibrator = GetComponent<VideoColorCalibrator>();
            if (colorCalibrator == null)
            {
                colorCalibrator = gameObject.AddComponent<VideoColorCalibrator>();
            }
            colorCalibrator.Initialize(this, targetRenderer, targetUI, targetUI2);

            if (autoStart)
            {
                AppLog.Log("[QuestStreamVideoPlayer] autoStart=true, starting AutoStartWhenConfigured");
                StartCoroutine(AutoStartWhenConfigured());
            }
            else
            {
                AppLog.Log("[QuestStreamVideoPlayer] autoStart=false, not starting");
            }

        }

        private IEnumerator AutoStartWhenConfigured()
        {
            // NetClient may call ConfigureNetwork shortly after scene load; wait briefly so we don't start with an empty remote endpoint.
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 2.0f)
            {
                if (!string.IsNullOrWhiteSpace(remoteHost) && remotePort > 0)
                {
                    break;
                }
                yield return null;
            }

            if (string.IsNullOrWhiteSpace(remoteHost) || remotePort <= 0)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] AutoStart proceeding without remote endpoint (remoteHost='{remoteHost}', remotePort={remotePort}). Video sender may not start if it relies on heartbeats.");
            }

            StartStream();
        }

        private void OnDisable()
        {
            resumeStreamAfterPause = false;
            StopResumeStreamCoroutine();
            StopStream();
            RestoreSideBySideOriginalMaterials();
        }
        private void OnApplicationQuit()
        {
            resumeStreamAfterPause = false;
            StopResumeStreamCoroutine();
            StopStream();
            RestoreSideBySideOriginalMaterials();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            applicationPauseState = pauseStatus;
            HandleApplicationSuspendState($"OnApplicationPause({pauseStatus})", pauseStatus || !applicationFocusState);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            applicationFocusState = hasFocus;
#if UNITY_ANDROID && !UNITY_EDITOR
            HandleApplicationSuspendState($"OnApplicationFocus({hasFocus})", applicationPauseState);
#else
            HandleApplicationSuspendState($"OnApplicationFocus({hasFocus})", applicationPauseState || !hasFocus);
#endif
        }

        public void ConfigureNetwork(string address, int primaryPort, int secondaryPort, string senderIp = "")
        {
            ConfigureNetwork(address, primaryPort, secondaryPort, string.Empty, 0, senderIp);
        }

        public void ConfigureNetwork(
            string address,
            int primaryPort,
            int secondaryPort,
            string udpRemoteHost,
            int udpRemotePort,
            string senderIp = "")
        {
            listenAddress = string.IsNullOrWhiteSpace(address) ? "0.0.0.0" : address.Trim();
            listenPort = Mathf.Clamp(primaryPort, 1024, 65535);
            listenPortSecondary = Mathf.Clamp(secondaryPort, 0, 65535);
            remoteHost = string.IsNullOrWhiteSpace(udpRemoteHost) ? string.Empty : udpRemoteHost.Trim();
            remotePort = Mathf.Clamp(udpRemotePort, 0, 65535);
            expectedSenderIp = string.IsNullOrEmpty(senderIp) ? string.Empty : senderIp.Trim();

            if (decoder != null)
            {
                RestartStream();
            }
        }

        public void ConfigureExpectedResolution(int width, int height)
        {
            expectedWidth = Mathf.Max(16, width);
            expectedHeight = Mathf.Max(16, height);

            if (decoder != null)
            {
                RestartStream();
            }
        }

        public bool TryDequeueReleasePacketHeader(out VideoReleasePacketHeader header)
        {
            if (releasePacketHeaderQueue.Count > 0)
            {
                header = releasePacketHeaderQueue.Dequeue();
                return true;
            }

            header = default;
            return false;
        }

        public bool TryGetLatestReleasePacketHeader(out VideoReleasePacketHeader header)
        {
            if (latestReleasePacketHeader.HasValue)
            {
                header = latestReleasePacketHeader.Value;
                return true;
            }

            header = default;
            return false;
        }

        private void EnsureSubmitHeartbeatPayloadMethod()
        {
            if (decoder == null || submitHeartbeatPayloadMethodId != IntPtr.Zero)
            {
                return;
            }

            try
            {
                submitHeartbeatPayloadMethodId = AndroidJNIHelper.GetMethodID(
                    decoder.GetRawClass(),
                    "submitHeartbeatPayload",
                    "([BI)V");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] Failed to cache submitHeartbeatPayload method: {ex.Message}");
            }
        }
        private void EnsureHeartbeatBridgeCapacity(int requiredLength)
        {
            if (requiredLength <= 0)
            {
                return;
            }

            if (javaHeartbeatBridge == null || javaHeartbeatBridge.Length < requiredLength)
            {
                javaHeartbeatBridge = new sbyte[requiredLength];
            }
        }

        public void SubmitVideoHeartbeat(byte[] payload, int length)
        {
            if (!IsAndroidTarget() || decoder == null)
            {
                return;
            }

            if (payload == null || length <= 0)
            {
                return;
            }

            if (length > payload.Length)
            {
                length = payload.Length;
            }

            EnsureHeartbeatBridgeCapacity(length);
            for (int i = 0; i < length; i++)
            {
                javaHeartbeatBridge[i] = unchecked((sbyte)payload[i]);
            }

            EnsureSubmitHeartbeatPayloadMethod();
            if (submitHeartbeatPayloadMethodId == IntPtr.Zero)
            {
                return;
            }

            try
            {
                IntPtr nativeArray = AndroidJNIHelper.ConvertToJNIArray(javaHeartbeatBridge);
                jvalue[] args = new jvalue[2];
                args[0].l = nativeArray;
                args[1].i = length;
                AndroidJNI.CallVoidMethod(decoder.GetRawObject(), submitHeartbeatPayloadMethodId, args);
                AndroidJNI.DeleteLocalRef(nativeArray);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] SubmitVideoHeartbeat JNI call failed: {ex.Message}");
            }
        }
        public void RestartStream()
        {
            StopStream();
            StartStream();
        }

        public void RestartStreamIfRunning()
        {
            if (decoder == null)
            {
                return;
            }

            RestartStream();
        }

        private void AbortStartStreamForGpuOnly(string reason)
        {
            Debug.LogError($"[QuestStreamVideoPlayer] GPU-only video path unavailable: {reason}");

            FlushHardwareQueues();

            if (decoder != null)
            {
                try
                {
                    decoder.Call("release");
                }
                catch { }

                try
                {
                    decoder.Dispose();
                }
                catch { }

                decoder = null;
            }

            usingHardwareBufferFrames = false;
            useNativeHardwareBufferImporter = false;
            nativeHardwareImportIssued = false;
            nativeHardwareImportCompletedOnce = false;
            zeroCopyPathConfirmedThisStream = false;
        }

        public void StartStream()
        {
            AppLog.Log("[QuestStreamVideoPlayer] StartStream called");
            LogVerbose("StartStream requested.");
            Debug.Log($"[QuestStreamVideoPlayer] FixedMode={(colorCalibrator != null && colorCalibrator.ForceFixedManualYuvParams)} (includesPost=True) instanceId={GetInstanceID()}");
            Debug.Log(DataManager.Instance.videoDecodeMode + "videoDecodeMode:isav1---->" + (DataManager.Instance.videoDecodeMode == DecoderFlavor.AV1));

            if (!IsAndroidTarget())
            {
                Debug.LogWarning("[QuestStreamVideoPlayer] UDP decoder only runs on Quest/Android devices.");
                return;
            }

            if (decoder != null)
            {
                return;
            }

            try
            {
                string safeAddress = string.IsNullOrWhiteSpace(listenAddress) ? "0.0.0.0" : listenAddress.Trim();

                // 涓嶈繘琛屽彂閫佺IP妫€娴嬶紝鎺ユ敹鎵€鏈夋潵婧愮殑鏁版嵁
                string safeSender = string.IsNullOrWhiteSpace(expectedSenderIp) ? string.Empty : expectedSenderIp;
                string decoderClass;
                switch (DataManager.Instance.videoDecodeMode)
                {
                    case DecoderFlavor.AV1:
                        decoderClass = "com.example.questdecoder.Av1StreamingDecoder";
                        break;
                    case DecoderFlavor.HEVC:
                        decoderClass = "com.example.questdecoder.HevcStreamingDecoder";
                        break;
                    default:
                        decoderClass = "com.example.questdecoder.H264StreamingDecoder";
                        break;
                }

                decoder = new AndroidJavaObject(
                    decoderClass,
                    expectedWidth,
                    expectedHeight,
                    safeAddress,
                    listenPort,
                    listenPortSecondary,
                    remoteHost,
                    remotePort,
                    safeSender);
                bool enableVerboseLogs = verboseLogging;
                if (verboseLogging && !UnityEngine.Debug.isDebugBuild)
                {
                    Debug.LogWarning("[QuestStreamVideoPlayer] Verbose logging forced OFF (non-development build).");
                    enableVerboseLogs = false;
                }

                bool enableChecksums = logFrameChecksums;
                if (logFrameChecksums && !UnityEngine.Debug.isDebugBuild)
                {
                    Debug.LogWarning("[QuestStreamVideoPlayer] Frame checksum logging forced OFF (non-development build).");
                    enableChecksums = false;
                }

                decoder.Call("setVerbose", enableVerboseLogs);
                decoder.Call("setDebugChecksums", enableChecksums);

                usingHardwareBufferFrames = ShouldUseHardwareBufferFrames();
                if (!usingHardwareBufferFrames)
                {
                    AbortStartStreamForGpuOnly("Vulkan HardwareBuffer rendering is required.");
                    return;
                }

                useNativeHardwareBufferImporter = false;
                nativeHardwareImportIssued = false;
                nativeHardwareImportCompletedOnce = false;
                zeroCopyPathConfirmedThisStream = false;
                LogZeroCopyReachabilitySelfCheck("gate");

                // Try Unity's native AndroidHardwareBuffer API first (preferred)
                if (usingHardwareBufferFrames && !TryEnsureAhbReflection())
                {
                    Debug.LogWarning("[QuestStreamVideoPlayer] Unity AndroidHardwareBuffer API unavailable. Trying native Vulkan importer as fallback.");

                    // Fallback to native importer if Unity API is unavailable
                    if (TryEnableNativeHardwareBufferImporter())
                    {
                        useNativeHardwareBufferImporter = true;
                        Debug.Log("[QuestStreamVideoPlayer] Using native Vulkan HardwareBuffer importer (libunity_vulkan_hwbuffer.so).");
                    }
                    else
                    {
                        AbortStartStreamForGpuOnly("Neither Unity AndroidHardwareBuffer API nor native Vulkan importer is available.");
                        return;
                    }
                }
                else if (usingHardwareBufferFrames)
                {
                    Debug.Log("[QuestStreamVideoPlayer] Using Unity native AndroidHardwareBuffer API (preferred path).");
                }

                if (!TrySetJavaHardwareBufferMode(usingHardwareBufferFrames))
                {
                    AbortStartStreamForGpuOnly("Failed to enable Java HardwareBuffer frame mode.");
                    return;
                }

                if (!TrySetJavaNativeHardwareBufferImporterMode(usingHardwareBufferFrames && useNativeHardwareBufferImporter))
                {
                    AbortStartStreamForGpuOnly("Failed to configure Java native HardwareBuffer importer mode.");
                    return;
                }

                LogZeroCopyReachabilitySelfCheck("java-configured");
 
                ConfigureJavaColorPipeline();

                decoder.Call("start");
                LogZeroCopyReachabilitySelfCheck("decoder-started");

                colorCalibrator?.OnStreamStarted();

                // No persistence: always auto-calibrate at runtime when frames arrive.
                colorCalibrator.ApplyFixedManualYuvParams("StartStream");

                if (verboseLogging)
                {
                    Debug.Log($"[QuestStreamVideoPlayer] Listening on UDP {listenPort}, expected sender: {safeSender}");
                }

                framesDecoded = 0;
                emptyFrameTicks = 0;
                ResetStreamClock();
                frameRoutine = StartCoroutine(PullFramesLoop());
            }
            catch (AndroidJavaException ex)
            {
                Debug.LogError($"[QuestStreamVideoPlayer] Failed to start decoder: {ex.Message}");
                decoder = null;
            }
        }

        private IEnumerator PullFramesLoop()
        {
            int consecutivePacketErrors = 0;
            int maxConsecutiveErrors = 10; // 鏈€澶ц繛缁敊璇鏁?
            
            while (decoder != null)
            {
                colorCalibrator?.MaybePollDecoderColorSignatureAndInvalidate(force: false, decoder: decoder, expectedWidth: expectedWidth, expectedHeight: expectedHeight);
                colorCalibrator?.ApplyFixedManualYuvParams("PullFramesLoop");
                colorCalibrator?.MaybeCaptureCpuCalibrationReferenceFromJava();

                if (SyncDecoderOutputModeIfNeeded())
                {
                    AppLog.Log("[QuestStreamVideoPlayer] Exit: SyncDecoderOutputModeIfNeeded");
                    yield break;
                }

                if (!usingHardwareBufferFrames)
                {
                    AppLog.Log("[QuestStreamVideoPlayer] Exit: !usingHardwareBufferFrames");
                    yield break;
                }
                  
                AndroidJavaObject frameBundle = retainedHardwareRetryBundle;
                retainedHardwareRetryBundle = null;
                int drainedCount = 0;
                int maxDrainPerFrame = Mathf.Max(1, maxHardwareFrameDrainPerTick);

                while (drainedCount < maxDrainPerFrame)
                {
                    AndroidJavaObject latestFrameBundle = null;
                    try
                    {
                        bool shouldLogDequeue = Time.realtimeSinceStartup - lastDequeueLogTime >= 1f;
                        if (drainedCount == 0 && shouldLogDequeue)
                        {
                            lastDequeueLogTime = Time.realtimeSinceStartup;
                            AppLog.Log("[QuestStreamVideoPlayer] Attempting dequeue...");
                        }
                        latestFrameBundle = decoder.Call<AndroidJavaObject>("dequeueHardwareBufferFrame");
                        if (drainedCount == 0 && latestFrameBundle != null && shouldLogDequeue)
                        {
                            AppLog.Log("[QuestStreamVideoPlayer] Successfully dequeued frame");
                        }
                        else if (drainedCount == 0 && latestFrameBundle == null && shouldLogDequeue)
                        {
                            AppLog.Log("[QuestStreamVideoPlayer] Dequeue returned null");
                        }
                    }
                    catch (AndroidJavaException ex)
                    {
                        consecutivePacketErrors++;
                        AppLog.Log($"[QuestStreamVideoPlayer] dequeueFrameBundle failed (error #{consecutivePacketErrors}): {ex.Message}");

                        if (consecutivePacketErrors >= maxConsecutiveErrors)
                        {
                            Debug.LogError($"[QuestStreamVideoPlayer] Too many consecutive errors ({consecutivePacketErrors}), attempting to recover...");
                            consecutivePacketErrors = 0;
                            RestartStream();
                            yield break;
                        }

                        break;
                    }

                    if (latestFrameBundle == null)
                    {
                        break;
                    }

                    consecutivePacketErrors = 0;
                    drainedCount++;

                    if (frameBundle != null && frameBundle != retainedHardwareRetryBundle)
                    {
                        ReleaseHardwareBundle(frameBundle);
                    }

                    frameBundle = latestFrameBundle;
                }

                if (frameBundle == null)
                {
                    HandleEmptyFrameTick();
                    yield return null;
                    continue;
                }

                if (drainedCount > 1 && verboseLogging)
                {
                    Debug.Log($"[QuestStreamVideoPlayer] Drained {drainedCount} frames this tick, presenting latest.");
                }

                try
                {
                    int[] headerData = frameBundle.Call<int[]>("getHeader");
                    HandlePacketHeader(headerData);
                    int frameWidth = frameBundle.Call<int>("getWidth");
                    int frameHeight = frameBundle.Call<int>("getHeight");
                    if (frameWidth <= 0 || frameHeight <= 0)
                    {
                        frameWidth = expectedWidth;
                        frameHeight = expectedHeight;
                    }

                    HardwareFrameApplyResult applyResult = ApplyHardwareFrameToTargets(frameBundle, frameWidth, frameHeight);
                    consecutivePacketErrors = 0;
                    switch (applyResult)
                    {
                        case HardwareFrameApplyResult.Presented:
                            frameBundle = null; // ownership transferred to in-flight queue
                            break;
                        case HardwareFrameApplyResult.RetryNextTick:
                            retainedHardwareRetryBundle = frameBundle;
                            frameBundle = null;
                            break;
                    }
                }
                finally
                {
                    if (frameBundle != null)
                    {
                        try
                        {
                            frameBundle.Call("release");
                        }
                        catch (AndroidJavaException ex)
                        {
                            if (verboseLogging)
                            {
                                Debug.LogWarning($"[QuestStreamVideoPlayer] Failed to release decoder frame: {ex.Message}");
                            }
                        }
                        frameBundle.Dispose();
                    }
                }

                yield return null;
            }
        }

        private void ResetStreamClock()
        {
            lastFramePresentedTime = Time.realtimeSinceStartup;
            nextHardwareStallRestartTime = 0f;
            hardwareStallRestartAttempts = 0;
        }

        public void StopStream()
        {
            LogVerbose("StopStream requested.");
            StopResumeStreamCoroutine();
            if (frameRoutine != null)
            {
                StopCoroutine(frameRoutine);
                frameRoutine = null;
            }

            FlushHardwareQueues();

            Debug.Log($"[QuestStreamVideoPlayer] Stream summary: presented={framesDecoded}, emptyTicks={emptyFrameTicks}, usingHardwareBufferFrames={usingHardwareBufferFrames}, nativeImporter={useNativeHardwareBufferImporter}");

            if (decoder == null)
            {
                return;
            }

            try
            {
                decoder.Call("stop");
                decoder.Call("release");
            }
            catch (AndroidJavaException ex)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] Shutdown error: {ex.Message}");
            }
            finally
            {
                decoder.Dispose();
                decoder = null;
            }

            ResetStreamClock();
            manualYuvInputModeOverride = int.MinValue;
            colorCalibrator?.OnStreamStopped("StopStream");
        }

        private void HandleApplicationSuspendState(string source, bool shouldSuspend)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (shouldSuspend)
            {
                if (decoder != null)
                {
                    resumeStreamAfterPause = true;
                    Debug.Log($"[QuestStreamVideoPlayer] {source}: stopping AV1 hardware stream for app suspend.");
                    StopStream();
                }
                return;
            }

            if (!resumeStreamAfterPause || decoder != null || !isActiveAndEnabled)
            {
                return;
            }

            Debug.Log($"[QuestStreamVideoPlayer] {source}: scheduling AV1 hardware stream resume after app resume.");
            StopResumeStreamCoroutine();
            resumeStreamCoroutine = StartCoroutine(ResumeStreamAfterPause());
#endif
        }

        private IEnumerator ResumeStreamAfterPause()
        {
            yield return null;
            yield return null;

            resumeStreamCoroutine = null;
            if (!resumeStreamAfterPause || decoder != null || !isActiveAndEnabled)
            {
                yield break;
            }

            resumeStreamAfterPause = false;
            StartStream();
        }

        private void StopResumeStreamCoroutine()
        {
            if (resumeStreamCoroutine == null)
            {
                return;
            }

            StopCoroutine(resumeStreamCoroutine);
            resumeStreamCoroutine = null;
        }

        private void FlushHardwareQueues()
        {
            while (inFlightHardwareResources.Count > 0)
            {
                var resource = inFlightHardwareResources.Dequeue();
                ReleaseHardwareBundle(resource.Bundle);
                DisposeUnityHardwareBuffer(resource.UnityHardwareBuffer);
            }

            if (calibrationHoldBundle != null)
            {
                ReleaseHardwareBundle(calibrationHoldBundle);
                calibrationHoldBundle = null;
            }

            if (retainedHardwareRetryBundle != null)
            {
                ReleaseHardwareBundle(retainedHardwareRetryBundle);
                retainedHardwareRetryBundle = null;
            }

            ShutdownQuestVulkanStream();

            if (externalHardwareTexture != null)
            {
                Destroy(externalHardwareTexture);
                externalHardwareTexture = null;
            }
            questVulkanUnityTextureBound = false;
        }

        private void ReleaseHardwareBundle(AndroidJavaObject bundle)
        {
            if (bundle == null)
            {
                return;
            }

            try
            {
                bundle.Call("release");
            }
            catch { }

            try
            {
                bundle.Dispose();
            }
            catch { }
        }

        private void TrackInFlightHardwareResource(AndroidJavaObject bundle, object unityHardwareBuffer)
        {
            if (bundle == null)
            {
                DisposeUnityHardwareBuffer(unityHardwareBuffer);
                return;
            }

            inFlightHardwareResources.Enqueue(new InFlightHardwareResource
            {
                Bundle = bundle,
                UnityHardwareBuffer = unityHardwareBuffer
            });

            // Java decoders use ImageReader(maxImages=8) for HardwareBuffer output.
            // Leave room for one queued decoder frame plus the next acquireLatestImage() call;
            // retaining more than one previously presented frame on the Unity side can saturate ImageReader.
            int maxRetainedFrames = Mathf.Max(1, maxInFlightHardwareFrames);
            if (usingHardwareBufferFrames)
            {
                maxRetainedFrames = Mathf.Min(maxRetainedFrames, 2);
            }

            while (inFlightHardwareResources.Count > maxRetainedFrames)
            {
                var old = inFlightHardwareResources.Dequeue();
                ReleaseHardwareBundle(old.Bundle);
                DisposeUnityHardwareBuffer(old.UnityHardwareBuffer);
            }
        }

        private HardwareFrameApplyResult ApplyHardwareFrameToTargets(AndroidJavaObject frameBundle, int frameWidth, int frameHeight)
        {
            if (frameBundle == null)
            {
                HandleEmptyFrameTick();
                return HardwareFrameApplyResult.Dropped;
            }

            EnsureSideBySideBlendMaterial();
            int desiredWidth = enableSideBySideBlend ? 0 : expectedWidth;
            int desiredHeight = enableSideBySideBlend ? 0 : expectedHeight;

#if UNITY_ANDROID && !UNITY_EDITOR
            // Hold one stable AHB during auto-calibration so every candidate re-runs conversion on identical content.
            colorCalibrator?.TryCaptureGpuCalibrationAhbFromBundle(frameBundle, frameWidth, frameHeight);
#endif
            if (calibrationHoldBundle != null && (colorCalibrator == null || !colorCalibrator.GpuCalibrationRunning))
            {
                ReleaseHardwareBundle(calibrationHoldBundle);
                calibrationHoldBundle = null;
            }

            // During GPU YUV auto-calibration we want a stable input frame while probing candidate params.
            // Dropping incoming frames for a short time is fine (startup-only) and avoids comparing different content.
            if (colorCalibrator != null && colorCalibrator.GpuCalibrationRunning && externalHardwareTexture != null)
            {
                if (calibrationHoldBundle == null)
                {
                    calibrationHoldBundle = frameBundle;
                }
                else
                {
                    ReleaseHardwareBundle(frameBundle);
                }
                QuestVideoImageUtils.BindTexture(
                    externalHardwareTexture,
                    //targetRenderer,
                    //targetUI,
                    //targetUI2,
                    flipTextureHorizontally,
                    flipTextureVertically,
                    frameWidth,
                    frameHeight,
                    desiredWidth,
                    desiredHeight);
                lastFramePresentedTime = Time.realtimeSinceStartup;
                emptyFrameTicks = 0;
                return HardwareFrameApplyResult.Presented;
            }

            if (!TryUpdateTextureFromHardwareBufferFrame(frameBundle, frameWidth, frameHeight, out var unityHardwareBuffer, out bool retryNextTick))
            {
                if (retryNextTick)
                {
                    return HardwareFrameApplyResult.RetryNextTick;
                }

                ReleaseHardwareBundle(frameBundle);
                return HardwareFrameApplyResult.Dropped;
            }

            framesDecoded++;
            lastFramePresentedTime = Time.realtimeSinceStartup;
            emptyFrameTicks = 0;
            hardwareStallRestartAttempts = 0;
            QuestVideoImageUtils.BindTexture(
                externalHardwareTexture,
                //targetRenderer,
                //targetUI,
                //targetUI2,
                flipTextureHorizontally,
                flipTextureVertically,
                frameWidth,
                frameHeight,
                desiredWidth,
                desiredHeight);

            DebugTextManager.Instance?.NotifyVideoFramePresented();
            colorCalibrator?.MaybeStartGpuYuvAutoCalibration();

            if (useNativeHardwareBufferImporter && unityHardwareBuffer == null)
            {
                // Native importer acquires the AHardwareBuffer into the plugin immediately.
                // Holding the Java Image/HardwareBuffer bundle on the Unity side starves
                // ImageReader(maxImages=3) and eventually stalls decoder output.
                ReleaseHardwareBundle(frameBundle);
                return HardwareFrameApplyResult.Presented;
            }

            if (!useNativeHardwareBufferImporter && unityHardwareBuffer != null)
            {
                // Unity's imported AndroidHardwareBuffer wrapper owns the native buffer lifetime.
                // Releasing the Java Image/HardwareBuffer bundle immediately keeps ImageReader
                // from hitting maxImages under high AV1 decode rates.
                ReleaseHardwareBundle(frameBundle);
                frameBundle = null;
            }

            TrackInFlightHardwareResource(frameBundle, unityHardwareBuffer);
            return HardwareFrameApplyResult.Presented;
        }

        private bool TryUpdateTextureFromHardwareBufferFrame(
            AndroidJavaObject frameBundle,
            int frameWidth,
            int frameHeight,
            out object unityHardwareBuffer,
            out bool retryNextTick)
        {
            unityHardwareBuffer = null;
            retryNextTick = false;

            if (!IsAndroidTarget() || decoder == null)
            {
                return false;
            }

            AndroidJavaObject hardwareBuffer = null;
            try
            {
                hardwareBuffer = frameBundle.Call<AndroidJavaObject>("getHardwareBuffer");
                if (hardwareBuffer == null)
                {
                    return false;
                }

                int textureWidth = frameWidth;
                int textureHeight = frameHeight;
                ResolveHardwareBufferTextureSize(hardwareBuffer, ref textureWidth, ref textureHeight);

                if (!TryEnsureAhbReflection())
                {
                    if (!useNativeHardwareBufferImporter)
                    {
                        Debug.LogError("[QuestStreamVideoPlayer] AndroidHardwareBuffer API not found and CPU fallback is disabled in GPU-only mode.");
                        return false;
                    }

                    int fenceFd = -1;
                    try
                    {
                        fenceFd = frameBundle.Call<int>("takeFenceFd");
                    }
                    catch
                    {
                        fenceFd = -1;
                    }

                    if (!TryUpdateTextureFromHardwareBufferViaQuestVulkanExt(
                            hardwareBuffer,
                            textureWidth,
                            textureHeight,
                            fenceFd,
                            out unityHardwareBuffer,
                            out retryNextTick))
                    {
                        if (fenceFd >= 0)
                        {
                            try { QuestVulkanExt.QuestVulkan_CloseFenceFd(fenceFd); } catch { }
                        }
                        return false;
                    }

                    bool nativeImportOk = externalHardwareTexture != null;
                    if (nativeImportOk && nativeHardwareImportCompletedOnce && !zeroCopyPathConfirmedThisStream)
                    {
                        zeroCopyPathConfirmedThisStream = true;
                        LogZeroCopyReachabilitySelfCheck("first-hardware-frame", "path=native-importer");
                    }
                    return nativeImportOk;
                }

                object[] importArgs = unityAndroidHardwareBufferImportTakesBool
                    ? new object[] { hardwareBuffer, SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan }
                    : new object[] { hardwareBuffer };
                unityHardwareBuffer = unityAndroidHardwareBufferImportMethod.Invoke(null, importArgs);
                if (unityHardwareBuffer == null)
                {
                    return false;
                }

                IntPtr nativeTexPtr = IntPtr.Zero;
                if (unityAndroidHardwareBufferGetNativeTexturePtrMethod != null)
                {
                    object ptrObj = unityAndroidHardwareBufferGetNativeTexturePtrMethod.Invoke(unityHardwareBuffer, null);
                    if (ptrObj is IntPtr p)
                    {
                        nativeTexPtr = p;
                    }
                    else if (ptrObj is long l)
                    {
                        nativeTexPtr = new IntPtr(l);
                    }
                    else if (ptrObj is ulong ul)
                    {
                        nativeTexPtr = unchecked(new IntPtr((long)ul));
                    }
                }

                if (nativeTexPtr == IntPtr.Zero)
                {
                    DisposeUnityHardwareBuffer(unityHardwareBuffer);
                    unityHardwareBuffer = null;
                    return false;
                }

                var externalTexture2D = externalHardwareTexture as Texture2D;
                if (externalTexture2D == null || externalTexture2D.width != textureWidth || externalTexture2D.height != textureHeight)
                {
                    if (externalHardwareTexture != null)
                    {
                        Destroy(externalHardwareTexture);
                    }

                     externalTexture2D = Texture2D.CreateExternalTexture(
                         textureWidth,
                         textureHeight,
                         TextureFormat.RGBA32,
                         false,
                         (colorCalibrator != null && colorCalibrator.UnityVideoTextureIsLinear),
                         nativeTexPtr);
                    externalTexture2D.wrapMode = TextureWrapMode.Clamp;
                    externalTexture2D.filterMode = FilterMode.Bilinear;
                    externalHardwareTexture = externalTexture2D;
                }
                else
                {
                    externalTexture2D.UpdateExternalTexture(nativeTexPtr);
                }

                bool unityImportOk = externalHardwareTexture != null;
                if (unityImportOk && !zeroCopyPathConfirmedThisStream)
                {
                    zeroCopyPathConfirmedThisStream = true;
                    LogZeroCopyReachabilitySelfCheck("first-hardware-frame", "path=unity-ahb");
                }
                return unityImportOk;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] Failed to import HardwareBuffer frame: {ex.Message}");
                DisposeUnityHardwareBuffer(unityHardwareBuffer);
                unityHardwareBuffer = null;
                return false;
            }
            finally
            {
                try { hardwareBuffer?.Dispose(); } catch { }
            }
        }

        private void ResolveHardwareBufferTextureSize(AndroidJavaObject hardwareBuffer, ref int textureWidth, ref int textureHeight)
        {
            if (hardwareBuffer == null)
            {
                return;
            }

            try
            {
                int bufferWidth = hardwareBuffer.Call<int>("getWidth");
                int bufferHeight = hardwareBuffer.Call<int>("getHeight");
                if (bufferWidth > 0)
                {
                    textureWidth = bufferWidth;
                }
                if (bufferHeight > 0)
                {
                    textureHeight = bufferHeight;
                }
            }
            catch
            {
            }
        }

        private bool TryEnsureAhbReflection()
        {
            if (ahbReflectionInitialized)
            {
                return unityAndroidHardwareBufferImportMethod != null;
            }

            ahbReflectionInitialized = true;
            unityAndroidHardwareBufferType = FindPreferredHardwareBufferType();
            if (unityAndroidHardwareBufferType == null)
            {
                Debug.LogWarning("[QuestStreamVideoPlayer] Unity AndroidHardwareBuffer type not found. Current runtime likely does not expose the expected managed wrapper type, or Unity moved it to a different namespace/assembly.");
                DumpHardwareBufferTypesOnce();
                return false;
            }

            unityAndroidHardwareBufferImportMethod = ResolveHardwareBufferImportMethod(unityAndroidHardwareBufferType, out unityAndroidHardwareBufferImportTakesBool);
            unityAndroidHardwareBufferGetNativeTexturePtrMethod = ResolveHardwareBufferTextureAccessor(unityAndroidHardwareBufferType);
            unityAndroidHardwareBufferDisposeMethod = ResolveHardwareBufferDisposeMethod(unityAndroidHardwareBufferType);

            if (unityAndroidHardwareBufferImportMethod == null)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] AndroidHardwareBuffer.Import not found on type {unityAndroidHardwareBufferType.FullName} (Unity version/API mismatch).");
            }
            if (unityAndroidHardwareBufferGetNativeTexturePtrMethod == null)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] AndroidHardwareBuffer native texture accessor not found on type {unityAndroidHardwareBufferType.FullName}.");
            }

            return unityAndroidHardwareBufferImportMethod != null;
        }

        private static bool hardwareBufferDumped;

        private static void DumpHardwareBufferTypesOnce()
        {
            if (hardwareBufferDumped)
            {
                return;
            }

            hardwareBufferDumped = true;
            try
            {
                var matches = new List<string>(16);
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (Type type in types)
                    {
                        string name = type?.FullName;
                        if (string.IsNullOrEmpty(name))
                        {
                            continue;
                        }

                        if (name.IndexOf("HardwareBuffer", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matches.Add(name);
                            if (matches.Count >= 30)
                            {
                                break;
                            }
                        }
                    }

                    if (matches.Count >= 30)
                    {
                        break;
                    }
                }

                if (matches.Count == 0)
                {
                    Debug.LogWarning("[QuestStreamVideoPlayer] No types containing 'HardwareBuffer' were found via reflection.");
                }
                else
                {
                    Debug.LogWarning("[QuestStreamVideoPlayer] Reflection types containing 'HardwareBuffer':\n" + string.Join("\n", matches));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] DumpHardwareBufferTypesOnce failed: {ex.Message}");
            }
        }

        private static Type FindType(string fullName)
        {
            // Fast path: assembly-qualified lookups (IL2CPP may not enumerate all assemblies).
            string[] candidateAssemblies =
            {
                "UnityEngine.AndroidModule",
                "UnityEngine.CoreModule",
                "UnityEngine",
                "UnityEngine.AndroidJNIModule",
            };

            foreach (string asm in candidateAssemblies)
            {
                try
                {
                    Type direct = Type.GetType($"{fullName}, {asm}", false);
                    if (direct != null)
                    {
                        return direct;
                    }
                }
                catch { }
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private Type FindPreferredHardwareBufferType()
        {
            string[] candidateTypeNames =
            {
                "UnityEngine.Android.AndroidHardwareBuffer",
                "UnityEngine.AndroidHardwareBuffer",
                "UnityEngine.Rendering.AndroidHardwareBuffer",
                "UnityEngine.Experimental.Rendering.AndroidHardwareBuffer",
            };

            foreach (string fullName in candidateTypeNames)
            {
                Type type = FindType(fullName);
                if (type != null)
                {
                    return type;
                }
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    string fullName = type?.FullName;
                    if (string.IsNullOrEmpty(fullName) ||
                        fullName.IndexOf("HardwareBuffer", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (ResolveHardwareBufferImportMethod(type, out _) != null)
                    {
                        Debug.Log($"[QuestStreamVideoPlayer] Discovered HardwareBuffer wrapper via reflection scan: {fullName}");
                        return type;
                    }
                }
            }

            return null;
        }

        private static MethodInfo ResolveHardwareBufferImportMethod(Type hardwareBufferType, out bool takesBool)
        {
            takesBool = false;
            if (hardwareBufferType == null)
            {
                return null;
            }

            MethodInfo importMethod = hardwareBufferType.GetMethod(
                "Import",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(AndroidJavaObject) },
                null);
            if (importMethod != null)
            {
                return importMethod;
            }

            importMethod = hardwareBufferType.GetMethod(
                "Import",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(AndroidJavaObject), typeof(bool) },
                null);
            if (importMethod != null)
            {
                takesBool = true;
                return importMethod;
            }

            foreach (MethodInfo method in hardwareBufferType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!string.Equals(method.Name, "Import", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(AndroidJavaObject))
                {
                    return method;
                }

                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(AndroidJavaObject) &&
                    parameters[1].ParameterType == typeof(bool))
                {
                    takesBool = true;
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo ResolveHardwareBufferTextureAccessor(Type hardwareBufferType)
        {
            if (hardwareBufferType == null)
            {
                return null;
            }

            MethodInfo accessor = hardwareBufferType.GetMethod(
                "GetNativeTexturePtr",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null) ??
                hardwareBufferType.GetMethod(
                    "GetNativeTexture",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null) ??
                hardwareBufferType.GetMethod(
                    "get_NativeTexture",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);

            if (accessor != null)
            {
                return accessor;
            }

            foreach (MethodInfo method in hardwareBufferType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.GetParameters().Length != 0)
                {
                    continue;
                }

                if (method.Name.IndexOf("Native", StringComparison.OrdinalIgnoreCase) < 0 ||
                    method.Name.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (method.ReturnType == typeof(IntPtr) ||
                    method.ReturnType == typeof(long) ||
                    method.ReturnType == typeof(ulong))
                {
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo ResolveHardwareBufferDisposeMethod(Type hardwareBufferType)
        {
            if (hardwareBufferType == null)
            {
                return null;
            }

            return hardwareBufferType.GetMethod(
                       "Dispose",
                       BindingFlags.Public | BindingFlags.Instance,
                       null,
                       Type.EmptyTypes,
                       null) ??
                   hardwareBufferType.GetMethod(
                       "Release",
                       BindingFlags.Public | BindingFlags.Instance,
                       null,
                       Type.EmptyTypes,
                       null);
        }

        private void DisposeUnityHardwareBuffer(object unityHardwareBuffer)
        {
            if (unityHardwareBuffer == null)
            {
                return;
            }

            if (unityHardwareBuffer is IntPtr ptr && ptr != IntPtr.Zero)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                try { QuestVulkanExt.QuestVulkan_ReleaseAHardwareBuffer(ptr); } catch { }
#endif
                return;
            }

            if (unityAndroidHardwareBufferDisposeMethod == null)
            {
                return;
            }

            try
            {
                unityAndroidHardwareBufferDisposeMethod.Invoke(unityHardwareBuffer, null);
            }
            catch { }
        }

        private bool TryEnableNativeHardwareBufferImporter()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!IsAndroidTarget())
            {
                Debug.Log("[QuestStreamVideoPlayer] Native importer: not Android target");
                return false;
            }

            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
            {
                Debug.Log($"[QuestStreamVideoPlayer] Native importer: requires Vulkan, current={SystemInfo.graphicsDeviceType}");
                return false;
            }

            try
            {
                hardwareBufferNativeBridge ??= new AndroidJavaClass("com.example.questdecoder.HardwareBufferNativeBridge");
                Debug.Log("[QuestStreamVideoPlayer] Native importer: HardwareBufferNativeBridge loaded successfully");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] Native importer: Failed to init HardwareBufferNativeBridge: {ex.Message}");
                return false;
            }

            try
            {
                Debug.Log("[QuestStreamVideoPlayer] Native importer: Trying to load libunity_vulkan_hwbuffer.so");
                questVulkanRenderEventFunc = QuestVulkanExt.QuestVulkan_GetRenderEventFunc();
                if (questVulkanRenderEventFunc != IntPtr.Zero)
                {
                    Debug.Log($"[QuestStreamVideoPlayer] Native importer: Successfully loaded libunity_vulkan_hwbuffer.so, renderEventFunc={questVulkanRenderEventFunc}");
                    return true;
                }
                else
                {
                    Debug.LogWarning("[QuestStreamVideoPlayer] Native importer: QuestVulkan_GetRenderEventFunc returned null");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] Native importer: Failed to load libunity_vulkan_hwbuffer.so: {ex.Message}");
                return false;
            }
#else
            return false;
#endif
        }
        private bool EnsureQuestVulkanStreamTexture(int frameWidth, int frameHeight)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!useNativeHardwareBufferImporter)
            {
                return false;
            }

            if (questVulkanRenderEventFunc == IntPtr.Zero)
            {
                questVulkanRenderEventFunc = QuestVulkanExt.QuestVulkan_GetRenderEventFunc();
                if (questVulkanRenderEventFunc == IntPtr.Zero)
                {
                    return false;
                }
            }

            if (questVulkanStreamHandle == IntPtr.Zero)
            {
                questVulkanStreamHandle = QuestVulkanExt.QuestVulkan_CreateStreamTexture(frameWidth, frameHeight);
                if (questVulkanStreamHandle == IntPtr.Zero)
                {
                    return false;
                }
                questVulkanUnityTextureBound = false;
                ycbcrOverrideApplier.InvalidateAll();
            }

            if (!useJavaDecoderColorInfo)
            {
                ApplyQuestVulkanYcbcrOverride();
            }
            else
            {
                // Keep Java-provided decoder color info for model/range, but still allow C# to drive manual YUV debug/auto-calibration params.
                ApplyQuestVulkanManualYuvOverrideOnly();
            }

            // Unity texture pointer is assigned via EnsureQuestVulkanUnityTexture before import.
            // The native plugin now prefers rebinding Unity to its RGBA output image directly,
            // and only falls back to per-frame copy if direct binding is unavailable.

            return true;
#else
            return false;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private bool EnsureQuestVulkanUnityTexture(int frameWidth, int frameHeight)
        {
            var renderTexture = externalHardwareTexture as RenderTexture;
            if (renderTexture == null ||
                renderTexture.width != frameWidth ||
                renderTexture.height != frameHeight)
            {
                if (externalHardwareTexture != null)
                {
                    Destroy(externalHardwareTexture);
                }

                var readWrite = (colorCalibrator != null && colorCalibrator.UnityVideoTextureIsLinear)
                    ? RenderTextureReadWrite.Linear
                    : RenderTextureReadWrite.sRGB;
                renderTexture = new RenderTexture(
                    frameWidth,
                    frameHeight,
                    0,
                    RenderTextureFormat.ARGB32,
                    readWrite);
                renderTexture.wrapMode = TextureWrapMode.Clamp;
                renderTexture.filterMode = FilterMode.Bilinear;
                renderTexture.useMipMap = false;
                renderTexture.autoGenerateMips = false;
                if (!renderTexture.Create())
                {
                    Debug.LogWarning("[QuestStreamVideoPlayer] Failed to create RenderTexture for Vulkan binding.");
                    Destroy(renderTexture);
                    return false;
                }
                externalHardwareTexture = renderTexture;

                questVulkanUnityTextureBound = false;

                IntPtr unityTexturePtr = renderTexture.GetNativeTexturePtr();
                if (unityTexturePtr == IntPtr.Zero)
                {
                    Debug.LogWarning("[QuestStreamVideoPlayer] Failed to get Unity texture pointer for Vulkan binding.");
                    return false;
                }

                QuestVulkanExt.QuestVulkan_AssignUnityTexture(questVulkanStreamHandle, unityTexturePtr);
                questVulkanUnityTextureBound = true;
            }
            else if (!questVulkanUnityTextureBound)
            {
                IntPtr unityTexturePtr = renderTexture.GetNativeTexturePtr();
                if (unityTexturePtr == IntPtr.Zero)
                {
                    Debug.LogWarning("[QuestStreamVideoPlayer] Unity texture pointer is invalid for Vulkan binding.");
                    return false;
                }

                QuestVulkanExt.QuestVulkan_AssignUnityTexture(questVulkanStreamHandle, unityTexturePtr);
                questVulkanUnityTextureBound = true;
            }

            return true;
        }
        private void ApplyQuestVulkanYcbcrOverride()
        {
            if (questVulkanStreamHandle == IntPtr.Zero)
            {
                return;
            }

            int nativeModel = GetNativeYcbcrModel(ycbcrModel);
            int nativeRange = GetNativeYcbcrRange(ycbcrRange);
            int nativeSwap = swapRedBlue ? 2 : (swapCbCr ? 1 : 0);
            int nativeX = GetNativeChromaOffset(chromaX);
            int nativeY = GetNativeChromaOffset(chromaY);
            int nativeManualEnabled = manualYuvConversion ? 1 : 0;
            int nativeManualSwapUv = swapUv ? 1 : 0;
            int nativeManualInvertU = invertU ? 1 : 0;
            int nativeManualInvertV = invertV ? 1 : 0;
            int nativeManualOrder = (int)manualYuvChannelOrder;
            int nativeManualDebug = (int)manualYuvDebug;
            int nativeManualInputMode = (int)manualYuvInputMode;

            if (forceHardwareYcbcrConversion)
            {
                nativeManualEnabled = 0;
            }
            else if (forceJavaCpuYuvMatrix)
            {
                nativeManualEnabled = 1;
                nativeModel = GetNativeYcbcrModel(YcbcrModelSetting.Force601);
                nativeRange = GetNativeYcbcrRange(YcbcrRangeSetting.ForceNarrow);
                nativeManualInputMode = (int)ManualYuvInputMode.ByteNarrowJava;
            }

            ycbcrOverrideApplier.ApplyYcbcrAndManual(
                questVulkanStreamHandle,
                nativeModel,
                nativeRange,
                nativeSwap,
                nativeX,
                nativeY,
                nativeManualEnabled,
                nativeManualSwapUv,
                nativeManualInvertU,
                nativeManualInvertV,
                nativeManualOrder,
                nativeManualDebug,
                nativeManualInputMode);
        }
        private void ApplyQuestVulkanManualYuvOverrideOnly()
        {
            if (questVulkanStreamHandle == IntPtr.Zero)
            {
                return;
            }

            int nativeManualEnabled = manualYuvConversion ? 1 : 0;
            int nativeManualSwapUv = swapUv ? 1 : 0;
            int nativeManualInvertU = invertU ? 1 : 0;
            int nativeManualInvertV = invertV ? 1 : 0;
            int nativeManualOrder = (int)manualYuvChannelOrder;
            int nativeManualDebug = (int)manualYuvDebug;
            int nativeManualInputMode = manualYuvInputModeOverride != int.MinValue
                ? manualYuvInputModeOverride
                : (int)manualYuvInputMode;

            if (forceHardwareYcbcrConversion)
            {
                nativeManualEnabled = 0;
            }
            else if (forceJavaCpuYuvMatrix)
            {
                nativeManualEnabled = 1;
                nativeManualInputMode = (int)ManualYuvInputMode.ByteNarrowJava;
            }

            ycbcrOverrideApplier.ApplyManualOnly(
                questVulkanStreamHandle,
                nativeManualEnabled,
                nativeManualSwapUv,
                nativeManualInvertU,
                nativeManualInvertV,
                nativeManualOrder,
                nativeManualDebug,
                nativeManualInputMode);
        }
#endif
        private static int GetNativeYcbcrModel(YcbcrModelSetting setting)
        {
            switch (setting)
            {
                case YcbcrModelSetting.ForceRgbIdentity:
                    return 0; // VK_SAMPLER_YCBCR_MODEL_CONVERSION_RGB_IDENTITY
                case YcbcrModelSetting.ForceYcbcrIdentity:
                    return 1; // VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_IDENTITY
                case YcbcrModelSetting.Force709:
                    return 2; // VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_709
                case YcbcrModelSetting.Force601:
                    return 3; // VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_601
                case YcbcrModelSetting.Force2020:
                    return 4; // VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_2020
                case YcbcrModelSetting.Auto:
                default:
                    return -1;
            }
        }

        private static int GetNativeYcbcrRange(YcbcrRangeSetting setting)
        {
            switch (setting)
            {
                case YcbcrRangeSetting.ForceFull:
                    return 0; // VK_SAMPLER_YCBCR_RANGE_ITU_FULL
                case YcbcrRangeSetting.ForceNarrow:
                    return 1; // VK_SAMPLER_YCBCR_RANGE_ITU_NARROW
                case YcbcrRangeSetting.Auto:
                default:
                    return -1;
            }
        }

        private static int GetNativeChromaOffset(YcbcrChromaOffsetSetting setting)
        {
            switch (setting)
            {
                case YcbcrChromaOffsetSetting.CositedEven:
                    return 0; // VK_CHROMA_LOCATION_COSITED_EVEN
                case YcbcrChromaOffsetSetting.Midpoint:
                    return 1; // VK_CHROMA_LOCATION_MIDPOINT
                case YcbcrChromaOffsetSetting.Auto:
                default:
                    return -1;
            }
        }

        private bool TryUpdateTextureFromHardwareBufferViaQuestVulkanExt(
            AndroidJavaObject hardwareBuffer,
            int frameWidth,
            int frameHeight,
            int fenceFd,
            out object unityHardwareBuffer,
            out bool retryNextTick)
        {
            unityHardwareBuffer = null;
            retryNextTick = false;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!EnsureQuestVulkanStreamTexture(frameWidth, frameHeight))
            {
                return false;
            }

            if (!EnsureQuestVulkanUnityTexture(frameWidth, frameHeight))
            {
                return false;
            }

            if (nativeHardwareImportIssued)
            {
                QuestVulkanTextureOperationStatus importStatus = QuestVulkanTextureOperationStatus.Invalid;
                try
                {
                    importStatus = (QuestVulkanTextureOperationStatus)QuestVulkanExt.QuestVulkan_GetTextureOperationStatus(questVulkanStreamHandle);
                }
                catch
                {
                    importStatus = QuestVulkanTextureOperationStatus.Invalid;
                }

                if (importStatus == QuestVulkanTextureOperationStatus.Pending)
                {
                    if (Time.realtimeSinceStartup - lastNativeImportPendingLogTime >= 1f)
                    {
                        lastNativeImportPendingLogTime = Time.realtimeSinceStartup;
                        Debug.Log($"[QuestStreamVideoPlayer] Native HardwareBuffer import still pending; retrying current frame next tick. streamHandle={(questVulkanStreamHandle != IntPtr.Zero)}, nativeIssued={nativeHardwareImportIssued}, decoded={framesDecoded}");
                    }
                    retryNextTick = true;
                    return false;
                }

                if (importStatus == QuestVulkanTextureOperationStatus.Success)
                {
                    nativeHardwareImportFailureCount = 0;
                    nativeHardwareImportCompletedOnce = true;
                    questVulkanUnityTextureBound = true;
                    if (verboseLogging && Time.realtimeSinceStartup - lastNativeImportStatusLogTime >= 1f)
                    {
                        lastNativeImportStatusLogTime = Time.realtimeSinceStartup;
                        AppLog.Log("[QuestStreamVideoPlayer] Native HardwareBuffer import completed successfully.");
                    }
                }
                else if (importStatus == QuestVulkanTextureOperationStatus.Failed)
                {
                    nativeHardwareImportFailureCount++;
                    questVulkanUnityTextureBound = false;
                    if (Time.realtimeSinceStartup - lastNativeImportStatusLogTime >= 1f)
                    {
                        lastNativeImportStatusLogTime = Time.realtimeSinceStartup;
                        Debug.LogWarning($"[QuestStreamVideoPlayer] Native HardwareBuffer import failed. consecutiveFailures={nativeHardwareImportFailureCount}");
                    }
                    if (nativeHardwareImportFailureCount >= 10)
                    {
                        nativeHardwareImportFailureCount = 0;
                        Debug.LogError("[QuestStreamVideoPlayer] HardwareBuffer import failed repeatedly in GPU-only mode. Restarting stream.");
                        RestartStream();
                        return false;
                    }
                }
            }

            long rawPtr;
            try
            {
                rawPtr = hardwareBufferNativeBridge.CallStatic<long>("acquireAHardwareBuffer", hardwareBuffer);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] acquireAHardwareBuffer failed: {ex.Message}");
                return false;
            }

            if (rawPtr == 0)
            {
                return false;
            }

            IntPtr ahbPtr = new IntPtr(rawPtr);

            try
            {
                bool usedFence = false;
                if (fenceFd >= 0)
                {
                    try
                    {
                        QuestVulkanExt.QuestVulkan_SetHardwareBufferWithFence(questVulkanStreamHandle, ahbPtr, frameWidth, frameHeight, fenceFd);
                        usedFence = true;
                    }
                    catch
                    {
                        usedFence = false;
                    }
                }

                if (!usedFence)
                {
                    if (fenceFd >= 0)
                    {
                        try { QuestVulkanExt.QuestVulkan_CloseFenceFd(fenceFd); } catch { }
                    }
                    QuestVulkanExt.QuestVulkan_SetHardwareBuffer(questVulkanStreamHandle, ahbPtr, frameWidth, frameHeight);
                }
                IssueQuestVulkanEvent(QuestVulkanExt.RenderEventImportHardwareBuffer);
                nativeHardwareImportIssued = true;

                unityHardwareBuffer = null;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] questvulkanext import failed: {ex.Message}");
                nativeHardwareImportFailureCount++;
                return false;
            }
#else
            return false;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void IssueQuestVulkanEvent(int eventId)
        {
            if (questVulkanRenderEventFunc == IntPtr.Zero || questVulkanStreamHandle == IntPtr.Zero)
            {
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get("QuestVulkanExtEvent");
            cmd.IssuePluginEventAndData(questVulkanRenderEventFunc, eventId, questVulkanStreamHandle);
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

#endif

        private void ShutdownQuestVulkanStream()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (questVulkanStreamHandle != IntPtr.Zero)
            {
                try
                {
                    if (questVulkanRenderEventFunc == IntPtr.Zero)
                    {
                        questVulkanRenderEventFunc = QuestVulkanExt.QuestVulkan_GetRenderEventFunc();
                    }

                    QuestVulkanExt.QuestVulkan_RequestDestroyTexture(questVulkanStreamHandle);
                    IssueQuestVulkanEvent(QuestVulkanExt.RenderEventDestroyTexture);
                    QuestVulkanExt.QuestVulkan_DestroyTexture(questVulkanStreamHandle);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[QuestStreamVideoPlayer] questvulkanext shutdown failed: {ex.Message}");
                }

                questVulkanStreamHandle = IntPtr.Zero;
            }
#endif

            questVulkanUnityTextureBound = false;
            questVulkanRenderEventFunc = IntPtr.Zero;
            useNativeHardwareBufferImporter = false;
            nativeHardwareImportFailureCount = 0;
            nativeHardwareImportIssued = false;
            nativeHardwareImportCompletedOnce = false;

            try { hardwareBufferNativeBridge?.Dispose(); } catch { }
            hardwareBufferNativeBridge = null;
        }

        private bool SyncDecoderOutputModeIfNeeded()
        {
            if (!usingHardwareBufferFrames || decoder == null)
            {
                return false;
            }

            if (!TryGetDecoderBoolState("isHardwareBufferFramesRequested", out bool javaRequested))
            {
                return false;
            }

            if (javaRequested)
            {
                return false;
            }

            if (TryGetDecoderBoolState("isDecoderUsingHardwareBuffers", out bool javaUsing) && javaUsing)
            {
                return false;
            }

            Debug.LogWarning("[QuestStreamVideoPlayer] Java decoder dropped out of HardwareBuffer mode. Restarting stream to restore GPU decode.");
            useNativeHardwareBufferImporter = false;
            zeroCopyPathConfirmedThisStream = false;
            FlushHardwareQueues();
            RestartStream();
            return true;
        }


        private bool ShouldUseHardwareBufferFrames()
        {
            if (!preferVulkanHardwareBufferFrames)
            {
                Debug.Log("[QuestStreamVideoPlayer] HardwareBuffer disabled: preferVulkanHardwareBufferFrames=false");
                return false;
            }

            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
            {
                Debug.Log($"[QuestStreamVideoPlayer] HardwareBuffer disabled: graphicsDeviceType={SystemInfo.graphicsDeviceType} (Vulkan required)");
                return false;
            }

            string decoderFlavor = DataManager.Instance != null ? DataManager.Instance.videoDecodeMode.ToString() : "Unknown";
            Debug.Log($"[QuestStreamVideoPlayer] HardwareBuffer ENABLED: All checks passed (graphics={SystemInfo.graphicsDeviceType}, decoder={decoderFlavor}, preferVulkanHardwareBufferFrames=true)");
            return true;
        }

        private bool TrySetJavaHardwareBufferMode(bool enable)
        {

            try
            {
                Debug.Log($"[QuestStreamVideoPlayer] Calling Java decoder.setUseHardwareBufferFrames({enable})");
                decoder.Call("setUseHardwareBufferFrames", enable);
                Debug.Log($"[QuestStreamVideoPlayer] Successfully set Java HardwareBuffer mode to {enable}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[QuestStreamVideoPlayer] Failed to set HardwareBuffer mode in Java decoder: {ex.Message}");
                return false;
            }
        }

        private bool TrySetJavaNativeHardwareBufferImporterMode(bool enable)
        {
            if (decoder == null)
            {
                return !enable;
            }

            try
            {
                Debug.Log($"[QuestStreamVideoPlayer] Calling Java decoder.setNativeHardwareBufferImporterEnabled({enable})");
                decoder.Call("setNativeHardwareBufferImporterEnabled", enable);
                return true;
            }
            catch (Exception ex)
            {
                if (enable)
                {
                    Debug.LogError($"[QuestStreamVideoPlayer] Failed to set native importer mode in Java decoder: {ex.Message}");
                    return false;
                }

                if (verboseLogging)
                {
                    Debug.LogWarning($"[QuestStreamVideoPlayer] Failed to set native importer mode in Java decoder: {ex.Message}");
                }

                return true;
            }
        }

        private void ConfigureJavaColorPipeline()
        {
            if (decoder == null)
            {
                return;
            }

            // Java must not auto-run any calibration behavior by itself.
            // Unity (C#) explicitly controls whether any calibration assistance is enabled.
            bool enableUnityAutoCalibration = colorCalibrator != null &&
                                              colorCalibrator.AutoCalibrateGpuYuvFromCpuFirstFrame &&
                                              SupportsCpuCalibrationReferenceForCurrentDecoder();
            bool enableNativeColorInfoHandoff = useJavaDecoderColorInfo;

            try
            {
                decoder.Call("setUnityAutoCalibrationEnabled", enableUnityAutoCalibration);
            }
            catch { }

            try
            {
                decoder.Call("setNativeColorInfoHandoffEnabled", enableNativeColorInfoHandoff);
            }
            catch { }
        }

        private bool SupportsCpuCalibrationReferenceForCurrentDecoder()
        {
            return DataManager.Instance != null && DataManager.Instance.videoDecodeMode == DecoderFlavor.AV1;
        }

        private bool IsAndroidTarget()
        {
            return Application.isPlaying &&
                   !Application.isEditor &&
                   Application.platform == RuntimePlatform.Android;
        }

        private void LogZeroCopyReachabilitySelfCheck(string stage, string note = null)
        {
            string decoderFlavor = DataManager.Instance != null ? DataManager.Instance.videoDecodeMode.ToString() : "Unknown";
            bool vulkan = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan;
            bool requestedByPolicy = preferVulkanHardwareBufferFrames && vulkan;
            string ahbState = !ahbReflectionInitialized
                ? "not-probed"
                : (unityAndroidHardwareBufferImportMethod != null ? "ok" : "missing");
            string activePath = usingHardwareBufferFrames
                ? (useNativeHardwareBufferImporter ? "native-importer" : "unity-ahb")
                : "gpu-only-inactive";
            string javaRequested = GetDecoderBoolState("isHardwareBufferFramesRequested");
            string javaUsing = GetDecoderBoolState("isDecoderUsingHardwareBuffers");
            string javaImporter = GetDecoderBoolState("isNativeHardwareBufferImporterEnabled");
            string javaHwQueue = GetDecoderIntState("getHardwareFrameQueueSize");
            string av1FmtVer = GetDecoderIntState("getAv1FormatHeaderVersion");
            string av1FmtBytes = GetDecoderIntState("getAv1FormatHeaderSizeBytes");
            string av1FmtCrc = GetDecoderLongHexState("getAv1FormatHeaderCrc32");

            string suffix = string.IsNullOrEmpty(note) ? string.Empty : $", note={note}";
            Debug.Log(
                $"[QuestStreamVideoPlayer][ZeroCopySelfCheck/{stage}] requestedByPolicy={requestedByPolicy}, csharpUsing={usingHardwareBufferFrames}, activePath={activePath}, decoder={decoderFlavor}, prefer={preferVulkanHardwareBufferFrames}, androidTarget={IsAndroidTarget()}, graphics={SystemInfo.graphicsDeviceType}, unityAhbReflection={ahbState}, nativeImporterEnabled={useNativeHardwareBufferImporter}, nativeRenderEventFunc={(questVulkanRenderEventFunc != IntPtr.Zero)}, nativeStreamHandle={(questVulkanStreamHandle != IntPtr.Zero)}, zeroCopyFrameSeen={zeroCopyPathConfirmedThisStream}, javaRequested={javaRequested}, javaUsing={javaUsing}, javaImporter={javaImporter}, javaHwQueue={javaHwQueue}, av1FmtVer={av1FmtVer}, av1FmtBytes={av1FmtBytes}, av1FmtCrc={av1FmtCrc}{suffix}");
        }

        private string GetDecoderBoolState(string methodName)
        {
            if (decoder == null)
            {
                return "n/a";
            }

            try
            {
                return decoder.Call<bool>(methodName) ? "true" : "false";
            }
            catch
            {
                return "n/a";
            }
        }

        private bool TryGetDecoderBoolState(string methodName, out bool value)
        {
            value = false;
            if (decoder == null)
            {
                return false;
            }

            try
            {
                value = decoder.Call<bool>(methodName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string GetDecoderIntState(string methodName)
        {
            if (decoder == null)
            {
                return "n/a";
            }

            try
            {
                return decoder.Call<int>(methodName).ToString();
            }
            catch
            {
                return "n/a";
            }
        }

        private string GetDecoderLongHexState(string methodName)
        {
            if (decoder == null)
            {
                return "n/a";
            }

            try
            {
                long value = decoder.Call<long>(methodName);
                if (value < 0)
                {
                    return value.ToString();
                }

                return $"0x{value:x}";
            }
            catch
            {
                return "n/a";
            }
        }

        internal bool IsAndroidTargetForCalibrator => IsAndroidTarget();
        internal AndroidJavaObject DecoderForCalibrator => decoder;
        internal int ExpectedWidthForCalibrator => expectedWidth;
        internal int ExpectedHeightForCalibrator => expectedHeight;
        internal Texture ExternalHardwareTextureForCalibrator => externalHardwareTexture;
        internal IntPtr QuestVulkanStreamHandleForCalibrator => questVulkanStreamHandle;
        internal AndroidJavaClass HardwareBufferNativeBridgeForCalibrator => hardwareBufferNativeBridge;
        internal bool UseJavaDecoderColorInfoForCalibrator => useJavaDecoderColorInfo;
        internal bool PreferVulkanHardwareBufferFramesForCalibrator => preferVulkanHardwareBufferFrames;
        internal bool ManualYuvConversionForCalibrator => manualYuvConversion;
        internal bool ForceHardwareYcbcrConversionForCalibrator => forceHardwareYcbcrConversion;
        internal bool SupportsCpuCalibrationReferenceForCalibrator => SupportsCpuCalibrationReferenceForCurrentDecoder();
        internal int ManualYuvDebugModeForCalibrator => (int)manualYuvDebug;

        internal void IssueQuestVulkanEventForCalibrator(int eventId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            IssueQuestVulkanEvent(eventId);
#endif
        }

        internal void SetHardwareYcbcrModeForCalibrator()
        {
            manualYuvConversion = false;
            forceHardwareYcbcrConversion = true;
            ycbcrOverrideApplier.InvalidateAll();
        }

        internal void SetManualConversionModeForCalibrator()
        {
            manualYuvConversion = true;
            forceHardwareYcbcrConversion = false;
            ycbcrOverrideApplier.InvalidateAll();
        }

        internal void SetManualYuvParamsFromCalibrationForCalibrator(bool swapUvValue, bool invertUValue, bool invertVValue, int channelOrder, int inputMode)
        {
            swapUv = swapUvValue;
            invertU = invertUValue;
            invertV = invertVValue;
            manualYuvChannelOrder = (ManualYuvChannelOrder)channelOrder;
            manualYuvInputModeOverride = inputMode;
            if (inputMode >= 0 && inputMode <= (int)ManualYuvInputMode.ByteFull)
            {
                manualYuvInputMode = (ManualYuvInputMode)inputMode;
            }
            ycbcrOverrideApplier.InvalidateAll();
        }

        internal void ApplyCurrentManualYuvToNativeForCalibrator()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            ycbcrOverrideApplier.InvalidateAll();
            ApplyQuestVulkanManualYuvOverrideOnly();
#endif
        }

        private void HandlePacketHeader(int[] headerData)
        {
            if (headerData == null || !TryParseReleasePacketHeader(headerData, out var releaseHeader))
            {
                LogReleasePacketHeader(null);
                return;
            }

            latestReleasePacketHeader = releaseHeader;
            if (releasePacketHeaderQueue.Count >= MaxCachedReleasePacketHeaders)
            {
                releasePacketHeaderQueue.Dequeue();
            }
            releasePacketHeaderQueue.Enqueue(releaseHeader);
            ReleasePacketHeaderReceived?.Invoke(releaseHeader);
            LogReleasePacketHeader(releaseHeader);
        }

        private bool TryParseReleasePacketHeader(int[] headerData, out VideoReleasePacketHeader header)
        {
            header = default;
            if (headerData == null || headerData.Length < 8)
            {
                return false;
            }

            header = new VideoReleasePacketHeader
            {
                Timestamp = headerData[0],
                FrameId = headerData[1],
                SplitId = headerData[2],
                TotalSplits = headerData[3],
                FragmentId = headerData[4],
                TotalFragments = headerData[5],
                FragmentSize = headerData[6],
                TestingId = headerData[7]
            };
            return true;
        }

        private void LogVerbose(string message)
        {
            if (verboseLogging)
            {
                Debug.Log($"[QuestStreamVideoPlayer] {message}");
            }
        }

        private void LogDecoderHealth(int ticksWithoutFrame)
        {
            if (!verboseLogging || decoder == null)
            {
                return;
            }

            try
            {
                long produced = decoder.Call<long>("getProducedFrameCount");
                int pending = decoder.Call<int>("getPendingFrameCount");
                long packets = decoder.Call<long>("getPacketsReceived");
                string javaRequested = GetDecoderBoolState("isHardwareBufferFramesRequested");
                string javaUsing = GetDecoderBoolState("isDecoderUsingHardwareBuffers");
                string javaImporter = GetDecoderBoolState("isNativeHardwareBufferImporterEnabled");
                string javaHwQueue = GetDecoderIntState("getHardwareFrameQueueSize");
                string av1FmtVer = GetDecoderIntState("getAv1FormatHeaderVersion");
                string av1FmtBytes = GetDecoderIntState("getAv1FormatHeaderSizeBytes");
                string av1FmtCrc = GetDecoderLongHexState("getAv1FormatHeaderCrc32");
                Debug.Log($"[QuestStreamVideoPlayer] Health check (no frames for {ticksWithoutFrame} ticks). Produced={produced}, pending={pending}, packetsReceived={packets}, zeroCopyCSharp={usingHardwareBufferFrames}, javaRequested={javaRequested}, javaUsing={javaUsing}, javaImporter={javaImporter}, javaHwQueue={javaHwQueue}, av1FmtVer={av1FmtVer}, av1FmtBytes={av1FmtBytes}, av1FmtCrc={av1FmtCrc}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestStreamVideoPlayer] Failed to query decoder health: {ex.Message}");
            }
        }

        private void HandleEmptyFrameTick()
        {
            emptyFrameTicks++;
            MaybeRecoverFromHardwareStall();
            MaybeLogPipelineStats();

            if (!verboseLogging)
            {
                return;
            }

            if (emptyFrameTicks == 1 || emptyFrameTicks % 30 == 0)
            {
                Debug.Log("[QuestStreamVideoPlayer] No frame dequeued this tick.");
                LogDecoderHealth(emptyFrameTicks);
            }
        }

        private void MaybeLogPipelineStats()
        {
            if (!verboseLogging || decoder == null)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now - lastPipelineStatsLogTime < 1f)
            {
                return;
            }

            long enqueued = GetFramesEnqueued();
            long dequeued = GetFramesDequeued();
            long produced = GetProducedFrameCount();
            long presented = framesDecoded;

            float elapsed = lastPipelineStatsLogTime > 0f ? now - lastPipelineStatsLogTime : 0f;
            string perSec = "warming-up";
            if (elapsed > 0.001f)
            {
                float producedPerSec = (produced - lastPipelineStatsProduced) / elapsed;
                float enqueuedPerSec = (enqueued - lastPipelineStatsFramesEnqueued) / elapsed;
                float dequeuedPerSec = (dequeued - lastPipelineStatsFramesDequeued) / elapsed;
                float presentedPerSec = (presented - lastPipelineStatsPresented) / elapsed;
                perSec = $"produced={producedPerSec:F1}/s, enqueued={enqueuedPerSec:F1}/s, dequeued={dequeuedPerSec:F1}/s, presented={presentedPerSec:F1}/s";
            }

            lastPipelineStatsLogTime = now;
            lastPipelineStatsFramesEnqueued = enqueued;
            lastPipelineStatsFramesDequeued = dequeued;
            lastPipelineStatsProduced = produced;
            lastPipelineStatsPresented = presented;

            Debug.Log(
                $"[QuestStreamVideoPlayer] Pipeline stats: expectedNativeBuild=ycbcr_manual_yuv_v7_direct_bind, {perSec}, totals produced={produced}, enqueued={enqueued}, dequeued={dequeued}, presented={presented}, javaHwQueue={GetDecoderIntState("getHardwareFrameQueueSize")}, importer={GetDecoderBoolState("isNativeHardwareBufferImporterEnabled")}, javaUsing={GetDecoderBoolState("isDecoderUsingHardwareBuffers")}, streamHandle={(questVulkanStreamHandle != IntPtr.Zero)}, nativeIssued={nativeHardwareImportIssued}");
        }

        private void MaybeRecoverFromHardwareStall()
        {
            if (!enableHardwareStallRecovery)
            {
                return;
            }

            if (!usingHardwareBufferFrames || decoder == null)
            {
                return;
            }
            if (colorCalibrator != null && colorCalibrator.GpuCalibrationRunning)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now - lastFramePresentedTime < hardwareStallRestartSeconds)
            {
                return;
            }

            if (now < nextHardwareStallRestartTime)
            {
                return;
            }

            hardwareStallRestartAttempts++;
            nextHardwareStallRestartTime = now + hardwareStallCooldownSeconds;

            if (hardwareStallRestartAttempts >= hardwareStallMaxRestarts)
            {
                Debug.LogWarning("[QuestStreamVideoPlayer] HardwareBuffer stalled repeatedly in GPU-only mode; restarting stream.");
            }
            else
            {
                Debug.LogWarning("[QuestStreamVideoPlayer] HardwareBuffer stalled; restarting stream.");
            }

            LogZeroCopyReachabilitySelfCheck("stall-restart", $"attempt={hardwareStallRestartAttempts}");
            RestartStream();
        }

        private void LogReleasePacketHeader(VideoReleasePacketHeader? header)
        {
            if (!verboseLogging)
            {
                return;
            }

            if (!header.HasValue)
            {
                Debug.Log("[QuestStreamVideoPlayer] Release packet header unavailable for this frame.");
                return;
            }

            VideoReleasePacketHeader value = header.Value;
            DataManager.Instance.lastFrameID = value.FrameId;
            //Debug.Log($"[QuestStreamVideoPlayer] ReleasePacketHeader => timestamp:{value.Timestamp} frame:{value.FrameId} split:{value.SplitId}/{value.TotalSplits} fragment:{value.FragmentId}/{value.TotalFragments} size:{value.FragmentSize} testing:{value.TestingId}");
        }

        // ------------------------ 鍏叡缁熻API ------------------------

        public long GetPacketsReceived()
        {
            if (!IsAndroidTarget() || decoder == null)
            {
                return 0;
            }

            try
            {
                return decoder.Call<long>("getPacketsReceived");
            }
            catch
            {
                return 0;
            }
        }

        public long GetBytesReceived()
        {
            if (!IsAndroidTarget() || decoder == null)
            {
                return 0;
            }

            try
            {
                return decoder.Call<long>("getBytesReceived");
            }
            catch
            {
                return 0;
            }
        }

        public long GetFramesReceived()
        {
            if (!IsAndroidTarget() || decoder == null)
            {
                return 0;
            }

            try
            {
                return decoder.Call<long>("getFramesReceived");
            }
            catch
            {
                return 0;
            }
        }

        public long GetFramesReassembled()
        {
            if (!IsAndroidTarget() || decoder == null)
            {
                return 0;
            }

            try
            {
                return decoder.Call<long>("getFramesReassembled");
            }
            catch
            {
                return 0;
            }
        }

        public long GetNalSubmitted()
        {
            if (!IsAndroidTarget() || decoder == null)
            {
                return 0;
            }

            try
            {
                return decoder.Call<long>("getNalSubmitted");
            }
            catch
            {
                return 0;
            }
        }

        public long GetFramesEnqueued()
        {
            if (!IsAndroidTarget() || decoder == null)
            {
                return 0;
            }

            try
            {
                return decoder.Call<long>("getFramesEnqueued");
            }
            catch
            {
                return 0;
            }
        }

        public long GetFramesDequeued()
        {
            if (!IsAndroidTarget() || decoder == null)
            {
                return 0;
            }

            try
            {
                return decoder.Call<long>("getFramesDequeued");
            }
            catch
            {
                return 0;
            }
        }

        public long GetProducedFrameCount()
        {
            if (!IsAndroidTarget() || decoder == null)
            {
                return 0;
            }

            try
            {
                return decoder.Call<long>("getProducedFrameCount");
            }
            catch
            {
                return 0;
            }
        }

        public int GetPendingFrameCount()
        {
            if (!IsAndroidTarget() || decoder == null)
            {
                return 0;
            }

            try
            {
                return decoder.Call<int>("getPendingFrameCount");
            }
            catch
            {
                return 0;
            }
        }

    }
}

