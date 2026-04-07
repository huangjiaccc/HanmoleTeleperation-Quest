using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Debug = AppLog;
///
/// quest3上单独的颜色校准
/// /// /// 
namespace Quest3VideoPlayer
{
    public class VideoColorCalibrator : MonoBehaviour
    {
        private QuestStreamVideoPlayer owner;

        public enum ConfigMode
        {
            ProductionFixed = 0,
            DebugInspector = 1,
        }

        [Header("Config")]
        [SerializeField] private ConfigMode configMode = ConfigMode.ProductionFixed;

        [Header("Vulkan Auto Calibration")]
        [Tooltip("Auto-calibrate manual GPU YUV->RGB conversion by comparing against a one-time CPU-decoded reference frame (captured from Java during startup).")]
        [SerializeField] private bool autoCalibrateGpuYuvFromCpuFirstFrame = true;
        [Tooltip("How many frames to wait after applying a candidate before sampling GPU output.")]
        [SerializeField, Range(0, 10)] private int autoCalibrateFramesToSettle = 1;
        [Tooltip("Downsample size used for GPU readback during calibration (kept small to avoid stalls).")]
        [SerializeField, Range(8, 128)] private int autoCalibrateSampleSize = 32;
        [Tooltip("Verbose calibration logs (candidate scores).")]
        [SerializeField] private bool autoCalibrateVerbose = false;
        [Tooltip("If hardware YCbCr conversion matches CPU reference better than manual conversion, prefer it.")]
        [SerializeField] private bool autoCalibrateAllowHardwareYcbcr = true;
        [Tooltip("Accept hardware YCbCr conversion when error is below this threshold.")]
        [SerializeField, Range(0.0f, 2.0f)] private float autoCalibrateHardwareAcceptErr = 0.25f;
        [Tooltip("Only APPLY an auto-calibrated configuration when the best error is below this threshold. If not met, the player keeps the current settings and will retry later.")]
        [SerializeField, Range(0.0f, 2.0f)] private float autoCalibrateApplyMaxErr = 0.10f;
        [Tooltip("When calibration doesn't meet the apply threshold, wait this long before trying again (avoids constant GPU readback stalls).")]
        [SerializeField, Range(0.1f, 10.0f)] private float autoCalibrateRetryDelaySeconds = 2.0f;
        [Tooltip("After calibration, apply a simple per-channel RGB gain to reduce residual tint (useful for slight pink/yellow cast).")]
        [SerializeField] private bool autoCalibrateApplyPostColorMul = true;
        [Tooltip("Clamp range for post colorMul gains (prevents extreme compensation).")]
        [SerializeField, Range(0.1f, 4.0f)] private float autoCalibrateColorMulMin = 0.5f;
        [SerializeField, Range(0.1f, 4.0f)] private float autoCalibrateColorMulMax = 2.0f;
        [Tooltip("Apply a 3x3+bias color matrix on display after calibration (root-cause fix for residual tint that per-channel gains cannot remove).")]
        [SerializeField] private bool autoCalibrateApplyDisplayColorMatrix = true;
        [Tooltip("Ridge regularization for color-matrix fitting (stabilizes inversion).")]
        [SerializeField, Range(0.0f, 0.1f)] private float autoCalibrateColorMatrixRidge = 0.0025f;

        [Header("Calibration Override")]
        [Tooltip("If true, skip auto-calibration and force a known-good YUV->RGBA conversion config (manual params + post colorMul + display matrix).")]
        [SerializeField] private bool forceFixedManualYuvParams = false;

        [Tooltip("Allow this component to replace target materials (e.g. display color matrix). Disable to preserve existing UI/renderer materials.")]
        [SerializeField] private bool allowMaterialOverride = true;

        [Tooltip("Controls whether the Unity Texture2D is treated as linear (no sRGB sampling). If colors look wrong, disable this so Unity treats video as sRGB.")]
        [SerializeField] private bool unityVideoTextureIsLinear = true;

        [Header("Color Transform")]
        [Tooltip("RGBA multiplier applied in the native shader after YUV->RGBA conversion.")]
        [SerializeField] private Vector4 colorMul = new Vector4(1f, 1f, 1f, 1f);
        [Tooltip("RGBA add (bias) applied in the native shader after YUV->RGBA conversion.")]
        [SerializeField] private Vector4 colorAdd = new Vector4(0f, 0f, 0f, 0f);

        [Header("Targets")]
        [SerializeField] private Renderer targetRenderer;
        [SerializeField] private UnityEngine.UI.RawImage targetUI;
        [SerializeField] private UnityEngine.UI.RawImage targetUI2;

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
            autoCalibrateGpuYuvFromCpuFirstFrame = false;
            autoCalibrateFramesToSettle = 0;
            autoCalibrateSampleSize = 32;
            autoCalibrateVerbose = false;
            autoCalibrateAllowHardwareYcbcr = false;
            autoCalibrateHardwareAcceptErr = 0.25f;
            autoCalibrateApplyMaxErr = 0.10f;
            autoCalibrateRetryDelaySeconds = 2.0f;
            autoCalibrateApplyPostColorMul = false;
            autoCalibrateColorMulMin = 0.5f;
            autoCalibrateColorMulMax = 2.0f;
            autoCalibrateApplyDisplayColorMatrix = false;
            autoCalibrateColorMatrixRidge = 0.0025f;

            forceFixedManualYuvParams = true;
            allowMaterialOverride = true;
            unityVideoTextureIsLinear = true;
            colorMul = Vector4.one;
            colorAdd = Vector4.zero;
        }

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

        private struct ManualYuvCandidate
        {
            public int SwapUv;
            public int InvertU;
            public int InvertV;
            public int ChannelOrder;
            public int InputMode;

            public override string ToString()
            {
                return $"swapUv={SwapUv} invertU={InvertU} invertV={InvertV} order={ChannelOrder} inputMode={InputMode}";
            }
        }

        private struct DecoderColorSignature
        {
            public int Standard;
            public int Range;
            public int Transfer;
            public int ColorFormat;
            public int Width;
            public int Height;
            public bool UnityVideoTextureIsLinear;

            public override string ToString()
            {
                return $"standard={Standard} range={Range} transfer={Transfer} format={ColorFormat} size={Width}x{Height} linear={UnityVideoTextureIsLinear}";
            }
        }

        private bool cpuCalibrationReferenceCaptured;
        private Vector3 cpuCalibrationMeanRgb;
        private Color32[] cpuCalibrationSamplePixels;
        private int[] cpuCalibrationHistogram;
        private QuestStreamVideoPlayer.VideoReleasePacketHeader? cpuCalibrationReleaseHeader;
        private bool gpuCalibrationRunning;
        private bool gpuCalibrationCompleted;
        private Coroutine gpuCalibrationRoutine;
        private float nextGpuCalibrationAttemptTime;
        private RenderTexture gpuCalibrationRt;
        private Texture2D gpuCalibrationReadback;

        private Material displayColorMatrixMaterial;
        private Material originalRendererMaterial;
        private Material originalUiMaterial;
        private bool displayColorMatrixApplied;
        private bool displayColorMatrixHasRows;
        private Vector4 displayColorMatrixRow0 = new Vector4(1, 0, 0, 0);
        private Vector4 displayColorMatrixRow1 = new Vector4(0, 1, 0, 0);
        private Vector4 displayColorMatrixRow2 = new Vector4(0, 0, 1, 0);
        private bool displayColorMatrixShaderMissing;
        private bool displayColorMatrixShaderMissingLogged;
        private bool displayColorMatrixNoTargetLogged;

        private DecoderColorSignature? lastDecoderSignature;
        private float nextDecoderSignaturePollTime;

#if UNITY_ANDROID && !UNITY_EDITOR
        private IntPtr gpuCalibrationAhbPtr = IntPtr.Zero;
        private int gpuCalibrationAhbWidth;
        private int gpuCalibrationAhbHeight;
#endif

        public bool GpuCalibrationCompleted => gpuCalibrationCompleted;
        public bool GpuCalibrationRunning => gpuCalibrationRunning;
        public bool CpuCalibrationReferenceCaptured => cpuCalibrationReferenceCaptured;
        public Vector3 CpuCalibrationMeanRgb => cpuCalibrationMeanRgb;
        public Color32[] CpuCalibrationSamplePixels => cpuCalibrationSamplePixels;
        public int[] CpuCalibrationHistogram => cpuCalibrationHistogram;
        public Vector4 ColorMul => colorMul;
        public Vector4 ColorAdd => colorAdd;
        public bool DisplayColorMatrixApplied => displayColorMatrixApplied;
        public bool ForceFixedManualYuvParams => forceFixedManualYuvParams;
        public bool UnityVideoTextureIsLinear => unityVideoTextureIsLinear;

        public bool AutoCalibrateGpuYuvFromCpuFirstFrame => autoCalibrateGpuYuvFromCpuFirstFrame;

        public void Initialize(QuestStreamVideoPlayer newOwner, Renderer renderer, UnityEngine.UI.RawImage ui,UnityEngine.UI.RawImage ui2)
        {
            owner = newOwner;
            targetRenderer = renderer;
            targetUI = ui;
            targetUI2 = ui2;
        }

        public void OnStreamStarted()
        {
            lastDecoderSignature = null;
            nextDecoderSignaturePollTime = 0f;
            nextGpuCalibrationAttemptTime = 0f;
            displayColorMatrixNoTargetLogged = false;
            displayColorMatrixShaderMissingLogged = false;
        }

        public void OnStreamStopped(string reason)
        {
            if (gpuCalibrationRoutine != null)
            {
                try { StopCoroutine(gpuCalibrationRoutine); } catch { }
                gpuCalibrationRoutine = null;
            }
            gpuCalibrationRunning = false;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (gpuCalibrationAhbPtr != IntPtr.Zero)
            {
                try { QuestVulkanExt.QuestVulkan_ReleaseAHardwareBuffer(gpuCalibrationAhbPtr); } catch { }
                gpuCalibrationAhbPtr = IntPtr.Zero;
                gpuCalibrationAhbWidth = 0;
                gpuCalibrationAhbHeight = 0;
            }
#endif

            if (gpuCalibrationRt != null)
            {
                gpuCalibrationRt.Release();
                Destroy(gpuCalibrationRt);
                gpuCalibrationRt = null;
            }

            if (gpuCalibrationReadback != null)
            {
                Destroy(gpuCalibrationReadback);
                gpuCalibrationReadback = null;
            }

            InvalidateGpuCalibration(reason);
        }

        private bool ShouldAutoCalibrateGpuYuv()
        {
            if (owner == null)
            {
                return false;
            }

            return owner.IsAndroidTargetForCalibrator &&
                   owner.UseJavaDecoderColorInfoForCalibrator &&
                   owner.PreferVulkanHardwareBufferFramesForCalibrator &&
                   owner.ManualYuvConversionForCalibrator &&
                   !owner.ForceHardwareYcbcrConversionForCalibrator &&
                   owner.SupportsCpuCalibrationReferenceForCalibrator &&
                   !forceFixedManualYuvParams &&
                   autoCalibrateGpuYuvFromCpuFirstFrame &&
                   !gpuCalibrationCompleted;
        }

        public void MaybeCaptureCpuCalibrationReferenceFromJava()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!ShouldAutoCalibrateGpuYuv() || cpuCalibrationReferenceCaptured || owner == null || owner.DecoderForCalibrator == null)
            {
                return;
            }

            AndroidJavaObject frameBundle = null;
            try
            {
                frameBundle = owner.DecoderForCalibrator.Call<AndroidJavaObject>("dequeueCalibrationFrameBundle");
                if (frameBundle == null)
                {
                    return;
                }

                sbyte[] javaFrame = frameBundle.Call<sbyte[]>("getImage");
                int[] headerData = null;
                try { headerData = frameBundle.Call<int[]>("getHeader"); } catch { }
                int frameWidth = frameBundle.Call<int>("getWidth");
                int frameHeight = frameBundle.Call<int>("getHeight");
                if (frameWidth <= 0 || frameHeight <= 0)
                {
                    frameWidth = owner.ExpectedWidthForCalibrator;
                    frameHeight = owner.ExpectedHeightForCalibrator;
                }

                if (!TryComputeCpuReferenceMeanRgb(javaFrame, frameWidth, frameHeight, out var mean))
                {
                    return;
                }

                cpuCalibrationMeanRgb = mean;
                cpuCalibrationSamplePixels = BuildCpuReferenceSamplePixelsAveraged(javaFrame, frameWidth, frameHeight, autoCalibrateSampleSize);
                cpuCalibrationHistogram = BuildRgbHistogram(cpuCalibrationSamplePixels, 16);
                if (TryParseReleasePacketHeader(headerData, out var rh))
                {
                    cpuCalibrationReleaseHeader = rh;
                }
                cpuCalibrationReferenceCaptured = true;
                try
                {
                    owner.DecoderForCalibrator.Call("trimCpuCalibrationResources");
                }
                catch { }
                Debug.Log($"[VideoColorCalibrator] AutoCalib: captured CPU reference mean RGB={cpuCalibrationMeanRgb} from {frameWidth}x{frameHeight} frame.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VideoColorCalibrator] AutoCalib: failed to capture CPU reference frame: {ex.Message}");
            }
            finally
            {
                try { frameBundle?.Call("release"); } catch { }
                try { frameBundle?.Dispose(); } catch { }
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        public void TryCaptureGpuCalibrationAhbFromBundle(AndroidJavaObject frameBundle, int frameWidth, int frameHeight)
        {
            if (frameBundle == null || owner == null || owner.HardwareBufferNativeBridgeForCalibrator == null)
            {
                return;
            }

            if (!ShouldAutoCalibrateGpuYuv() || gpuCalibrationAhbPtr != IntPtr.Zero)
            {
                return;
            }

            try
            {
                if (cpuCalibrationReleaseHeader.HasValue)
                {
                    int[] headerData = null;
                    try { headerData = frameBundle.Call<int[]>("getHeader"); } catch { }
                    if (!TryParseReleasePacketHeader(headerData, out var hwHeader))
                    {
                        return;
                    }

                    var cpuHeader = cpuCalibrationReleaseHeader.Value;
                    if (hwHeader.FrameId != cpuHeader.FrameId || hwHeader.Timestamp != cpuHeader.Timestamp)
                    {
                        return;
                    }
                }

                AndroidJavaObject hardwareBuffer = frameBundle.Call<AndroidJavaObject>("getHardwareBuffer");
                if (hardwareBuffer == null)
                {
                    return;
                }

                long rawPtr = owner.HardwareBufferNativeBridgeForCalibrator.CallStatic<long>("acquireAHardwareBuffer", hardwareBuffer);
                if (rawPtr == 0)
                {
                    return;
                }

                gpuCalibrationAhbPtr = new IntPtr(rawPtr);
                gpuCalibrationAhbWidth = frameWidth;
                gpuCalibrationAhbHeight = frameHeight;
                Debug.Log($"[VideoColorCalibrator] AutoCalib: captured calibration AHB ptr=0x{rawPtr:X} size={frameWidth}x{frameHeight}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VideoColorCalibrator] AutoCalib: failed to capture calibration AHB: {ex.Message}");
            }
        }
#endif

        public void MaybeStartGpuYuvAutoCalibration()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (forceFixedManualYuvParams)
            {
                return;
            }

            if (!ShouldAutoCalibrateGpuYuv() || gpuCalibrationRunning || gpuCalibrationRoutine != null)
            {
                return;
            }

            if (Time.realtimeSinceStartup < nextGpuCalibrationAttemptTime)
            {
                return;
            }

            if (!cpuCalibrationReferenceCaptured)
            {
                return;
            }

            if (owner == null || owner.ExternalHardwareTextureForCalibrator == null)
            {
                return;
            }

            gpuCalibrationRoutine = StartCoroutine(AutoCalibrateGpuYuvRoutine());
#endif
        }

        private static bool TryParseReleasePacketHeader(int[] headerData, out QuestStreamVideoPlayer.VideoReleasePacketHeader header)
        {
            header = default;
            if (headerData == null || headerData.Length < 8)
            {
                return false;
            }

            header = new QuestStreamVideoPlayer.VideoReleasePacketHeader
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

        private static bool TryComputeCpuReferenceMeanRgb(sbyte[] frame, int frameWidth, int frameHeight, out Vector3 meanRgb)
        {
            meanRgb = default;
            if (frame == null || frame.Length == 0 || frameWidth <= 0 || frameHeight <= 0)
            {
                return false;
            }

            int expectedRgbaSize = frameWidth * frameHeight * 4;
            if (frame.Length != expectedRgbaSize)
            {
                return false;
            }

            long sumR = 0;
            long sumG = 0;
            long sumB = 0;
            int pixelCount = frameWidth * frameHeight;
            for (int i = 0; i + 3 < frame.Length; i += 4)
            {
                sumR += (byte)frame[i + 0];
                sumG += (byte)frame[i + 1];
                sumB += (byte)frame[i + 2];
            }

            float inv = 1.0f / (pixelCount * 255.0f);
            meanRgb = new Vector3(sumR * inv, sumG * inv, sumB * inv);
            return true;
        }

        private static Color32[] BuildCpuReferenceSamplePixelsAveraged(sbyte[] frame, int frameWidth, int frameHeight, int sampleSize)
        {
            int size = Mathf.Clamp(sampleSize, 8, 256);
            int expectedRgbaSize = frameWidth * frameHeight * 4;
            if (frame == null || frame.Length != expectedRgbaSize || frameWidth <= 0 || frameHeight <= 0)
            {
                return null;
            }

            var sample = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                int y0 = (y * frameHeight) / size;
                int y1 = ((y + 1) * frameHeight) / size;
                if (y1 <= y0) y1 = y0 + 1;
                for (int x = 0; x < size; x++)
                {
                    int x0 = (x * frameWidth) / size;
                    int x1 = ((x + 1) * frameWidth) / size;
                    if (x1 <= x0) x1 = x0 + 1;

                    long sumR = 0;
                    long sumG = 0;
                    long sumB = 0;
                    long sumA = 0;
                    int count = 0;

                    for (int yy = y0; yy < y1; yy++)
                    {
                        int rowBase = (yy * frameWidth) * 4;
                        for (int xx = x0; xx < x1; xx++)
                        {
                            int srcIndex = rowBase + (xx * 4);
                            sumR += (byte)frame[srcIndex + 0];
                            sumG += (byte)frame[srcIndex + 1];
                            sumB += (byte)frame[srcIndex + 2];
                            sumA += (byte)frame[srcIndex + 3];
                            count++;
                        }
                    }

                    if (count <= 0) count = 1;
                    byte r = (byte)(sumR / count);
                    byte g = (byte)(sumG / count);
                    byte b = (byte)(sumB / count);
                    byte a = (byte)(sumA / count);
                    sample[y * size + x] = new Color32(r, g, b, a);
                }
            }

            return sample;
        }

        private static int[] BuildRgbHistogram(Color32[] pixels, int binsPerChannel)
        {
            if (pixels == null || pixels.Length == 0)
            {
                return null;
            }

            int bins = Mathf.Clamp(binsPerChannel, 4, 64);
            int[] hist = new int[bins * 3];

            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                int br = (c.r * bins) >> 8;
                int bg = (c.g * bins) >> 8;
                int bb = (c.b * bins) >> 8;
                if (br >= bins) br = bins - 1;
                if (bg >= bins) bg = bins - 1;
                if (bb >= bins) bb = bins - 1;
                hist[0 * bins + br]++;
                hist[1 * bins + bg]++;
                hist[2 * bins + bb]++;
            }

            return hist;
        }

        private void EnsureGpuCalibrationResources()
        {
            int size = Mathf.Clamp(autoCalibrateSampleSize, 8, 256);

            if (gpuCalibrationRt == null || gpuCalibrationRt.width != size || gpuCalibrationRt.height != size)
            {
                if (gpuCalibrationRt != null)
                {
                    gpuCalibrationRt.Release();
                    Destroy(gpuCalibrationRt);
                }

                var readWrite = unityVideoTextureIsLinear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
                gpuCalibrationRt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, readWrite);
                gpuCalibrationRt.wrapMode = TextureWrapMode.Clamp;
                gpuCalibrationRt.filterMode = FilterMode.Point;
                gpuCalibrationRt.useMipMap = false;
                gpuCalibrationRt.autoGenerateMips = false;
                gpuCalibrationRt.Create();
            }

            if (gpuCalibrationReadback == null || gpuCalibrationReadback.width != size || gpuCalibrationReadback.height != size)
            {
                if (gpuCalibrationReadback != null)
                {
                    Destroy(gpuCalibrationReadback);
                }

                gpuCalibrationReadback = new Texture2D(size, size, TextureFormat.RGBA32, false, unityVideoTextureIsLinear);
            }
        }

        private bool TryCaptureGpuSamplePixels(out NativeArray<Color32> pixels, out Vector3 meanRgb)
        {
            pixels = default;
            meanRgb = default;
            if (owner == null || owner.ExternalHardwareTextureForCalibrator == null)
            {
                return false;
            }

            EnsureGpuCalibrationResources();

            try
            {
                Graphics.Blit(owner.ExternalHardwareTextureForCalibrator, gpuCalibrationRt);

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = gpuCalibrationRt;
                gpuCalibrationReadback.ReadPixels(new Rect(0, 0, gpuCalibrationRt.width, gpuCalibrationRt.height), 0, 0, false);
                gpuCalibrationReadback.Apply(false, false);
                RenderTexture.active = previous;

                pixels = gpuCalibrationReadback.GetRawTextureData<Color32>();
                if (pixels.Length == 0)
                {
                    return false;
                }

                long sumR = 0;
                long sumG = 0;
                long sumB = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 c = pixels[i];
                    sumR += c.r;
                    sumG += c.g;
                    sumB += c.b;
                }
                float inv = 1.0f / (pixels.Length * 255.0f);
                meanRgb = new Vector3(sumR * inv, sumG * inv, sumB * inv);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VideoColorCalibrator] AutoCalib: GPU readback failed: {ex.Message}");
                return false;
            }
        }

        private static float ComputeMeanRgbError(Vector3 gpuMeanRgb, Vector3 cpuMeanRgb)
        {
            return Mathf.Abs(gpuMeanRgb.x - cpuMeanRgb.x) +
                   Mathf.Abs(gpuMeanRgb.y - cpuMeanRgb.y) +
                   Mathf.Abs(gpuMeanRgb.z - cpuMeanRgb.z);
        }

        private static float ComputePixelErrorL1(NativeArray<Color32> gpuPixels, Color32[] cpuPixels)
        {
            if (!gpuPixels.IsCreated || gpuPixels.Length == 0 || cpuPixels == null || cpuPixels.Length != gpuPixels.Length)
            {
                return float.PositiveInfinity;
            }

            int size = Mathf.RoundToInt(Mathf.Sqrt(gpuPixels.Length));
            if (size <= 0 || size * size != gpuPixels.Length)
            {
                long linearSum = 0;
                for (int i = 0; i < gpuPixels.Length; i++)
                {
                    Color32 g = gpuPixels[i];
                    Color32 c = cpuPixels[i];
                    linearSum += Mathf.Abs(g.r - c.r);
                    linearSum += Mathf.Abs(g.g - c.g);
                    linearSum += Mathf.Abs(g.b - c.b);
                }
                float linearDenom = gpuPixels.Length * 255.0f;
                return linearSum / linearDenom;
            }

            float best = float.PositiveInfinity;
            for (int flipX = 0; flipX <= 1; flipX++)
            {
                for (int flipY = 0; flipY <= 1; flipY++)
                {
                    long sum = 0;
                    for (int y = 0; y < size; y++)
                    {
                        int yy = flipY != 0 ? (size - 1 - y) : y;
                        for (int x = 0; x < size; x++)
                        {
                            int xx = flipX != 0 ? (size - 1 - x) : x;
                            int gi = y * size + x;
                            int ci = yy * size + xx;
                            Color32 g = gpuPixels[gi];
                            Color32 c = cpuPixels[ci];
                            sum += Mathf.Abs(g.r - c.r);
                            sum += Mathf.Abs(g.g - c.g);
                            sum += Mathf.Abs(g.b - c.b);
                        }
                    }

                    float denom = gpuPixels.Length * 255.0f;
                    float err = sum / denom;
                    if (err < best)
                    {
                        best = err;
                    }
                }
            }

            return best;
        }

        private static List<ManualYuvCandidate> BuildAutoCalibrationCandidates()
        {
            var candidates = new List<ManualYuvCandidate>();

            int[] swap = { 0, 1 };
            (int u, int v)[] invs = { (0, 0), (1, 0), (0, 1), (1, 1) };
            int[] orders = { 0, 1, 2, 3, 4, 5 };
            int[] inputModes = { 0, 1, 2 };

            foreach (int swapUvFlag in swap)
            {
                foreach (var inv in invs)
                {
                    foreach (int order in orders)
                    {
                        foreach (int inputMode in inputModes)
                        {
                            candidates.Add(new ManualYuvCandidate
                            {
                                SwapUv = swapUvFlag,
                                InvertU = inv.u,
                                InvertV = inv.v,
                                ChannelOrder = order,
                                InputMode = inputMode
                            });
                        }
                    }
                }
            }

            return candidates;
        }

        private Vector4 ComputePostColorMul(Vector3 cpuMean, Vector3 gpuMean)
        {
            float SafeRatio(float target, float current)
            {
                if (current <= 1e-4f)
                {
                    return 1.0f;
                }
                return Mathf.Clamp(target / current, autoCalibrateColorMulMin, autoCalibrateColorMulMax);
            }

            float r = SafeRatio(cpuMean.x, gpuMean.x);
            float g = SafeRatio(cpuMean.y, gpuMean.y);
            float b = SafeRatio(cpuMean.z, gpuMean.z);
            return new Vector4(r, g, b, 1f);
        }

        private bool TryFitColorMatrix3x4(Color32[] gpuPixels, Color32[] cpuPixels, out Vector4 row0, out Vector4 row1, out Vector4 row2)
        {
            row0 = default;
            row1 = default;
            row2 = default;

            if (gpuPixels == null || cpuPixels == null || gpuPixels.Length == 0 || cpuPixels.Length != gpuPixels.Length)
            {
                return false;
            }

            double s00 = 0, s01 = 0, s02 = 0, s03 = 0;
            double s11 = 0, s12 = 0, s13 = 0;
            double s22 = 0, s23 = 0;
            double s33 = gpuPixels.Length;

            double tr0 = 0, tr1 = 0, tr2 = 0, tr3 = 0;
            double tg0 = 0, tg1 = 0, tg2 = 0, tg3 = 0;
            double tb0 = 0, tb1 = 0, tb2 = 0, tb3 = 0;

            for (int i = 0; i < gpuPixels.Length; i++)
            {
                Color32 g = gpuPixels[i];
                Color32 c = cpuPixels[i];

                double r = g.r / 255.0;
                double g1 = g.g / 255.0;
                double b = g.b / 255.0;

                double yr = c.r / 255.0;
                double yg = c.g / 255.0;
                double yb = c.b / 255.0;

                s00 += r * r;
                s01 += r * g1;
                s02 += r * b;
                s03 += r;

                s11 += g1 * g1;
                s12 += g1 * b;
                s13 += g1;

                s22 += b * b;
                s23 += b;

                tr0 += r * yr; tr1 += g1 * yr; tr2 += b * yr; tr3 += yr;
                tg0 += r * yg; tg1 += g1 * yg; tg2 += b * yg; tg3 += yg;
                tb0 += r * yb; tb1 += g1 * yb; tb2 += b * yb; tb3 += yb;
            }

            double[,] S = new double[4, 4];
            S[0, 0] = s00; S[0, 1] = s01; S[0, 2] = s02; S[0, 3] = s03;
            S[1, 0] = s01; S[1, 1] = s11; S[1, 2] = s12; S[1, 3] = s13;
            S[2, 0] = s02; S[2, 1] = s12; S[2, 2] = s22; S[2, 3] = s23;
            S[3, 0] = s03; S[3, 1] = s13; S[3, 2] = s23; S[3, 3] = s33;

            double lambda = autoCalibrateColorMatrixRidge;
            S[0, 0] += lambda;
            S[1, 1] += lambda;
            S[2, 2] += lambda;

            if (!Solve4x4(S, new[] { tr0, tr1, tr2, tr3 }, out var xr) ||
                !Solve4x4(S, new[] { tg0, tg1, tg2, tg3 }, out var xg) ||
                !Solve4x4(S, new[] { tb0, tb1, tb2, tb3 }, out var xb))
            {
                return false;
            }

            row0 = new Vector4((float)xr[0], (float)xr[1], (float)xr[2], (float)xr[3]);
            row1 = new Vector4((float)xg[0], (float)xg[1], (float)xg[2], (float)xg[3]);
            row2 = new Vector4((float)xb[0], (float)xb[1], (float)xb[2], (float)xb[3]);
            return true;
        }

        private static bool Solve4x4(double[,] A, double[] b, out double[] x)
        {
            x = new double[4];
            double[,] m = new double[4, 5];
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    m[r, c] = A[r, c];
                }
                m[r, 4] = b[r];
            }

            for (int col = 0; col < 4; col++)
            {
                int pivot = col;
                double best = System.Math.Abs(m[pivot, col]);
                for (int r = col + 1; r < 4; r++)
                {
                    double v = System.Math.Abs(m[r, col]);
                    if (v > best)
                    {
                        best = v;
                        pivot = r;
                    }
                }

                if (best < 1e-12)
                {
                    return false;
                }

                if (pivot != col)
                {
                    for (int c = col; c < 5; c++)
                    {
                        (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                    }
                }

                double invPivot = 1.0 / m[col, col];
                for (int c = col; c < 5; c++)
                {
                    m[col, c] *= invPivot;
                }

                for (int r = 0; r < 4; r++)
                {
                    if (r == col) continue;
                    double f = m[r, col];
                    if (System.Math.Abs(f) < 1e-12) continue;
                    for (int c = col; c < 5; c++)
                    {
                        m[r, c] -= f * m[col, c];
                    }
                }
            }

            x[0] = m[0, 4];
            x[1] = m[1, 4];
            x[2] = m[2, 4];
            x[3] = m[3, 4];
            return true;
        }

        private static int GetNativeYcbcrModel(YcbcrModelSetting setting)
        {
            switch (setting)
            {
                case YcbcrModelSetting.ForceRgbIdentity: return 0;
                case YcbcrModelSetting.ForceYcbcrIdentity: return 1;
                case YcbcrModelSetting.Force709: return 2;
                case YcbcrModelSetting.Force601: return 3;
                case YcbcrModelSetting.Force2020: return 4;
                case YcbcrModelSetting.Auto:
                default:
                    return -1;
            }
        }

        private static int GetNativeYcbcrRange(YcbcrRangeSetting setting)
        {
            switch (setting)
            {
                case YcbcrRangeSetting.ForceFull: return 0;
                case YcbcrRangeSetting.ForceNarrow: return 1;
                case YcbcrRangeSetting.Auto:
                default:
                    return -1;
            }
        }

        private static int GetNativeChromaOffset(YcbcrChromaOffsetSetting setting)
        {
            switch (setting)
            {
                case YcbcrChromaOffsetSetting.CositedEven: return 0;
                case YcbcrChromaOffsetSetting.Midpoint: return 1;
                case YcbcrChromaOffsetSetting.Auto:
                default:
                    return -1;
            }
        }

        private IEnumerator AutoCalibrateGpuYuvRoutine()
        {
            gpuCalibrationRunning = true;
            try
            {
                if (forceFixedManualYuvParams)
                {
                    yield break;
                }

                if (cpuCalibrationSamplePixels == null || cpuCalibrationSamplePixels.Length == 0)
                {
                    Debug.LogWarning("[VideoColorCalibrator] AutoCalib: cannot start (missing CPU sample pixels).");
                    yield break;
                }

#if UNITY_ANDROID && !UNITY_EDITOR
                if (autoCalibrateAllowHardwareYcbcr && gpuCalibrationAhbPtr == IntPtr.Zero)
                {
                    int waited = 0;
                    const int maxWaitFrames = 90;
                    while (gpuCalibrationAhbPtr == IntPtr.Zero && waited < maxWaitFrames && ShouldAutoCalibrateGpuYuv())
                    {
                        waited++;
                        yield return null;
                    }

                    if (gpuCalibrationAhbPtr == IntPtr.Zero)
                    {
                        Debug.LogWarning("[VideoColorCalibrator] AutoCalib: no calibration AHB captured in time; HW YCbCr stage will be skipped.");
                    }
                    else
                    {
                        Debug.Log($"[VideoColorCalibrator] AutoCalib: calibration AHB became available after {waited} frames (ptr=0x{gpuCalibrationAhbPtr.ToInt64():X}).");
                    }
                }
#endif

                int settleFrames = Mathf.Clamp(autoCalibrateFramesToSettle, 0, 30);
                int instanceId = owner != null ? owner.GetInstanceID() : 0;
                Debug.Log($"[VideoColorCalibrator] AutoCalib: routine entered (FixedMode={forceFixedManualYuvParams} instanceId={instanceId})");
                Debug.Log($"[VideoColorCalibrator] AutoCalib: starting GPU YUV calibration. cpuMean={cpuCalibrationMeanRgb} sample={autoCalibrateSampleSize}px settleFrames={settleFrames}");

                ManualYuvCandidate best = default;
                float bestErr = float.PositiveInfinity;
                Color32[] bestGpuSamplePixels = null;

#if UNITY_ANDROID && !UNITY_EDITOR
                try { QuestVulkanExt.TrySetYcbcrOverride(-1, -1, 0, -1, -1); } catch { }

                if (autoCalibrateAllowHardwareYcbcr &&
                    gpuCalibrationAhbPtr != IntPtr.Zero &&
                    owner != null &&
                    owner.QuestVulkanStreamHandleForCalibrator != IntPtr.Zero)
                {
                    float bestHwErr = float.PositiveInfinity;
                    int bestHwModel = int.MinValue;
                    int bestHwRange = int.MinValue;
                    int bestHwSwizzle = int.MinValue;
                    int bestHwX = int.MinValue;
                    int bestHwY = int.MinValue;

                    int[] models = { -1, GetNativeYcbcrModel(YcbcrModelSetting.Force601), GetNativeYcbcrModel(YcbcrModelSetting.Force709) };
                    int[] ranges = { -1, GetNativeYcbcrRange(YcbcrRangeSetting.ForceNarrow), GetNativeYcbcrRange(YcbcrRangeSetting.ForceFull) };
                    int[] swizzles = { 0, 2 };
                    int[] xOffs = { -1, 1 };
                    int[] yOffs = { -1, 1 };

                    foreach (int model in models)
                    {
                        foreach (int range in ranges)
                        {
                            foreach (int swizzle in swizzles)
                            {
                                foreach (int xOff in xOffs)
                                {
                                    foreach (int yOff in yOffs)
                                    {
                                        if (!ShouldAutoCalibrateGpuYuv())
                                        {
                                            break;
                                        }

                                        try
                                        {
                                            QuestVulkanExt.TrySetManualYuvParams(0, 0, 0, 0, 0, 0, 0);
                                            QuestVulkanExt.TrySetYcbcrOverride(model, range, swizzle, xOff, yOff);

                                            QuestVulkanExt.QuestVulkan_SetHardwareBuffer(owner.QuestVulkanStreamHandleForCalibrator, gpuCalibrationAhbPtr, gpuCalibrationAhbWidth, gpuCalibrationAhbHeight);
                                            owner.IssueQuestVulkanEventForCalibrator(QuestVulkanExt.RenderEventImportHardwareBuffer);
                                            QuestVulkanExt.QuestVulkan_WaitForTexture(owner.QuestVulkanStreamHandleForCalibrator, 0);
                                        }
                                        catch { }

                                        for (int f = 0; f < settleFrames; f++)
                                        {
                                            yield return null;
                                        }

                                        yield return new WaitForEndOfFrame();

                                        if (!TryCaptureGpuSamplePixels(out var gpuPixels0, out var gpuMean0))
                                        {
                                            continue;
                                        }

                                        float pixelErr0 = ComputePixelErrorL1(gpuPixels0, cpuCalibrationSamplePixels);
                                        float meanErr0 = ComputeMeanRgbError(gpuMean0, cpuCalibrationMeanRgb);
                                        float err0 = pixelErr0 + (0.05f * meanErr0);
                                        if (autoCalibrateVerbose)
                                        {
                                            Debug.Log($"[VideoColorCalibrator] AutoCalib(HW): model={model} range={range} swizzle={swizzle} xOff={xOff} yOff={yOff} err={err0} (pixel={pixelErr0} mean={meanErr0})");
                                        }

                                        if (err0 < bestHwErr)
                                        {
                                            bestHwErr = err0;
                                            bestHwModel = model;
                                            bestHwRange = range;
                                            bestHwSwizzle = swizzle;
                                            bestHwX = xOff;
                                            bestHwY = yOff;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (float.IsPositiveInfinity(bestHwErr))
                    {
                        Debug.LogWarning("[VideoColorCalibrator] AutoCalib(HW): no valid GPU readback; skipping hardware YCbCr stage.");
                    }

                    Debug.Log($"[VideoColorCalibrator] AutoCalib(HW): best model={bestHwModel} range={bestHwRange} swizzle={bestHwSwizzle} xOff={bestHwX} yOff={bestHwY} err={bestHwErr}");

                    float hwAccept = Mathf.Min(autoCalibrateHardwareAcceptErr, autoCalibrateApplyMaxErr);
                    if (bestHwErr <= hwAccept)
                    {
                        owner.SetHardwareYcbcrModeForCalibrator();
                        QuestVulkanExt.TrySetManualYuvParams(0, 0, 0, 0, 0, 0, 0);
                        QuestVulkanExt.TrySetYcbcrOverride(bestHwModel, bestHwRange, bestHwSwizzle, bestHwX, bestHwY);

                        if (autoCalibrateApplyDisplayColorMatrix)
                        {
                            try
                            {
                                QuestVulkanExt.QuestVulkan_SetHardwareBuffer(owner.QuestVulkanStreamHandleForCalibrator, gpuCalibrationAhbPtr, gpuCalibrationAhbWidth, gpuCalibrationAhbHeight);
                                owner.IssueQuestVulkanEventForCalibrator(QuestVulkanExt.RenderEventImportHardwareBuffer);
                                QuestVulkanExt.QuestVulkan_WaitForTexture(owner.QuestVulkanStreamHandleForCalibrator, 0);
                            }
                            catch { }

                            yield return new WaitForEndOfFrame();

                            if (TryCaptureGpuSamplePixels(out var gpuPixels1, out _))
                            {
                                Color32[] gpuPixelsCopy = null;
                                try { gpuPixelsCopy = gpuPixels1.ToArray(); } catch { }
                                if (gpuPixelsCopy != null &&
                                    cpuCalibrationSamplePixels != null &&
                                    gpuPixelsCopy.Length == cpuCalibrationSamplePixels.Length &&
                                    TryFitColorMatrix3x4(gpuPixelsCopy, cpuCalibrationSamplePixels, out var m0, out var m1, out var m2))
                                {
                                    ApplyDisplayColorMatrix(m0, m1, m2);
                                    Debug.Log($"[VideoColorCalibrator] AutoCalib: applied display color matrix rows: m0={m0} m1={m1} m2={m2}");
                                }
                                else
                                {
                                    Debug.LogWarning("[VideoColorCalibrator] AutoCalib: failed to fit display color matrix.");
                                }
                            }
                        }

                        gpuCalibrationCompleted = true;
                        Debug.Log($"[VideoColorCalibrator] AutoCalib: accepted hardware YCbCr conversion (err={bestHwErr}).");
                        yield break;
                    }
                    if (autoCalibrateVerbose)
                    {
                        Debug.LogWarning($"[VideoColorCalibrator] AutoCalib(HW): not applying hardware conversion (err={bestHwErr} > {hwAccept}).");
                    }
                }

                try { QuestVulkanExt.TrySetYcbcrOverride(-1, -1, 0, -1, -1); } catch { }
#endif

                var candidates = BuildAutoCalibrationCandidates();
                int debugModeNow = owner != null ? owner.ManualYuvDebugModeForCalibrator : 0;

                for (int i = 0; i < candidates.Count; i++)
                {
                    if (!ShouldAutoCalibrateGpuYuv())
                    {
                        break;
                    }

                    ManualYuvCandidate candidate = candidates[i];

#if UNITY_ANDROID && !UNITY_EDITOR
                    QuestVulkanExt.TrySetManualYuvParams(1, candidate.SwapUv, candidate.InvertU, candidate.InvertV, candidate.ChannelOrder, debugModeNow, candidate.InputMode);

                    for (int f = 0; f < settleFrames; f++)
                    {
                        yield return null;
                    }

                    if (gpuCalibrationAhbPtr != IntPtr.Zero && owner != null && owner.QuestVulkanStreamHandleForCalibrator != IntPtr.Zero)
                    {
                        try
                        {
                            QuestVulkanExt.QuestVulkan_SetHardwareBuffer(owner.QuestVulkanStreamHandleForCalibrator, gpuCalibrationAhbPtr, gpuCalibrationAhbWidth, gpuCalibrationAhbHeight);
                            owner.IssueQuestVulkanEventForCalibrator(QuestVulkanExt.RenderEventImportHardwareBuffer);
                            QuestVulkanExt.QuestVulkan_WaitForTexture(owner.QuestVulkanStreamHandleForCalibrator, 0);
                        }
                        catch { }
                    }
#endif

                    yield return new WaitForEndOfFrame();

                    if (!TryCaptureGpuSamplePixels(out var gpuPixels, out var gpuMean))
                    {
                        continue;
                    }

                    float pixelErr = ComputePixelErrorL1(gpuPixels, cpuCalibrationSamplePixels);
                    float meanErr = ComputeMeanRgbError(gpuMean, cpuCalibrationMeanRgb);
                    float err = pixelErr + (0.05f * meanErr);
                    if (autoCalibrateVerbose)
                    {
                        Debug.Log($"[VideoColorCalibrator] AutoCalib: candidate[{i}] {candidate} err={err} (pixel={pixelErr} mean={meanErr})");
                    }

                    if (err < bestErr)
                    {
                        bestErr = err;
                        best = candidate;
                        try { bestGpuSamplePixels = gpuPixels.ToArray(); } catch { bestGpuSamplePixels = null; }
                    }
                }

                if (float.IsPositiveInfinity(bestErr))
                {
                    Debug.LogWarning("[VideoColorCalibrator] AutoCalib: failed to evaluate any candidates (no GPU readback).");
                    yield break;
                }

                if (bestErr > autoCalibrateApplyMaxErr)
                {
                    Debug.LogWarning($"[VideoColorCalibrator] AutoCalib: bestErr={bestErr} exceeds apply threshold {autoCalibrateApplyMaxErr}; keeping current settings and retrying later.");
                    nextGpuCalibrationAttemptTime = Time.realtimeSinceStartup + autoCalibrateRetryDelaySeconds;
                    owner?.ApplyCurrentManualYuvToNativeForCalibrator();
                    yield break;
                }

                if (best.InputMode < 0)
                {
                    Debug.LogWarning($"[VideoColorCalibrator] AutoCalib: invalid best candidate (inputMode={best.InputMode}); forcing re-calibration next tick.");
                    gpuCalibrationCompleted = false;
                    owner?.ApplyCurrentManualYuvToNativeForCalibrator();
                    yield break;
                }

                owner?.SetManualYuvParamsFromCalibrationForCalibrator(best.SwapUv != 0, best.InvertU != 0, best.InvertV != 0, best.ChannelOrder, best.InputMode);
                owner?.SetManualConversionModeForCalibrator();

#if UNITY_ANDROID && !UNITY_EDITOR
                QuestVulkanExt.TrySetManualYuvParams(1, best.SwapUv, best.InvertU, best.InvertV, best.ChannelOrder, debugModeNow, best.InputMode);
#endif

                if (autoCalibrateApplyPostColorMul)
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    if (gpuCalibrationAhbPtr != IntPtr.Zero && owner != null && owner.QuestVulkanStreamHandleForCalibrator != IntPtr.Zero)
                    {
                        try
                        {
                            QuestVulkanExt.QuestVulkan_SetHardwareBuffer(owner.QuestVulkanStreamHandleForCalibrator, gpuCalibrationAhbPtr, gpuCalibrationAhbWidth, gpuCalibrationAhbHeight);
                            owner.IssueQuestVulkanEventForCalibrator(QuestVulkanExt.RenderEventImportHardwareBuffer);
                            QuestVulkanExt.QuestVulkan_WaitForTexture(owner.QuestVulkanStreamHandleForCalibrator, 0);
                        }
                        catch { }
                    }
#endif
                    yield return new WaitForEndOfFrame();
                    if (TryCaptureGpuSamplePixels(out var finalGpuPixels, out var finalGpuMean))
                    {
                        Vector4 mul = ComputePostColorMul(cpuCalibrationMeanRgb, finalGpuMean);
                        Vector4 add = Vector4.zero;
                        SetColorTransform(mul, add);
                        Debug.Log($"[VideoColorCalibrator] AutoCalib: applied post colorMul={mul} (cpuMean={cpuCalibrationMeanRgb} gpuMean={finalGpuMean})");
                        try { bestGpuSamplePixels = finalGpuPixels.ToArray(); } catch { }
                    }
                }

                if (autoCalibrateApplyDisplayColorMatrix &&
                    bestGpuSamplePixels != null &&
                    cpuCalibrationSamplePixels != null &&
                    bestGpuSamplePixels.Length == cpuCalibrationSamplePixels.Length)
                {
                    if (TryFitColorMatrix3x4(bestGpuSamplePixels, cpuCalibrationSamplePixels, out var m00, out var m01, out var m02))
                    {
                        ApplyDisplayColorMatrix(m00, m01, m02);
                        Debug.Log($"[VideoColorCalibrator] AutoCalib: applied display color matrix rows: m0={m00} m1={m01} m2={m02}");
                    }
                    else
                    {
                        Debug.LogWarning("[VideoColorCalibrator] AutoCalib: failed to fit display color matrix.");
                    }
                }

                gpuCalibrationCompleted = true;
                Debug.Log($"[VideoColorCalibrator] AutoCalib: completed. best={best} bestErr={bestErr}");
            }
            finally
            {
                gpuCalibrationRunning = false;
                gpuCalibrationRoutine = null;

#if UNITY_ANDROID && !UNITY_EDITOR
                if (gpuCalibrationAhbPtr != IntPtr.Zero)
                {
                    try { QuestVulkanExt.QuestVulkan_ReleaseAHardwareBuffer(gpuCalibrationAhbPtr); } catch { }
                    gpuCalibrationAhbPtr = IntPtr.Zero;
                    gpuCalibrationAhbWidth = 0;
                    gpuCalibrationAhbHeight = 0;
                }
#endif
            }
        }

        public void InvalidateGpuCalibration(string reason)
        {
            gpuCalibrationCompleted = false;
            cpuCalibrationReferenceCaptured = false;
            cpuCalibrationMeanRgb = default;
            cpuCalibrationSamplePixels = null;
            cpuCalibrationHistogram = null;
            cpuCalibrationReleaseHeader = null;

            colorMul = Vector4.one;
            colorAdd = Vector4.zero;
            SetColorTransform(colorMul, colorAdd);
            RestoreDisplayMaterials();
            displayColorMatrixHasRows = false;
            displayColorMatrixRow0 = new Vector4(1, 0, 0, 0);
            displayColorMatrixRow1 = new Vector4(0, 1, 0, 0);
            displayColorMatrixRow2 = new Vector4(0, 0, 1, 0);
            displayColorMatrixShaderMissing = false;

            Debug.Log($"[VideoColorCalibrator] AutoCalib: invalidated ({reason}).");
        }

        public void ApplyFixedManualYuvParams(string reason)
        {
            if (!forceFixedManualYuvParams)
            {
                return;
            }

            if (gpuCalibrationRoutine != null)
            {
                try { StopCoroutine(gpuCalibrationRoutine); } catch { }
                gpuCalibrationRoutine = null;
            }
            gpuCalibrationRunning = false;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (gpuCalibrationAhbPtr != IntPtr.Zero)
            {
                try { QuestVulkanExt.QuestVulkan_ReleaseAHardwareBuffer(gpuCalibrationAhbPtr); } catch { }
                gpuCalibrationAhbPtr = IntPtr.Zero;
                gpuCalibrationAhbWidth = 0;
                gpuCalibrationAhbHeight = 0;
            }
#endif

            const int forcedSwapUv = 0;
            const int forcedInvertU = 0;
            const int forcedInvertV = 0;
            const ManualYuvChannelOrder forcedOrder = (ManualYuvChannelOrder)2;
            const ManualYuvInputMode forcedInputMode = (ManualYuvInputMode)1;
            Vector4 forcedColorMul = new Vector4(0.993f, 0.991f, 0.989f, 1.00f);
            Vector4 forcedColorAdd = Vector4.zero;
            Vector4 forcedM0 = new Vector4(0.93f, -0.08f, 0.03f, 0.05f);
            Vector4 forcedM1 = new Vector4(-0.06f, 0.84f, 0.11f, 0.05f);
            Vector4 forcedM2 = new Vector4(-0.05f, 0.10f, 0.83f, 0.05f);

            const int forcedYcbcrModel = 2;
            const int forcedYcbcrRange = 0;
            const int forcedYcbcrSwizzleMode = 2;
            const int forcedYcbcrXOff = 1;
            const int forcedYcbcrYOff = 1;

            bool matrixOk = true;
            if (!allowMaterialOverride)
            {
                matrixOk = true;
            }
            else
            {
                EnsureDisplayColorMatrixMaterial();
                if (displayColorMatrixShaderMissing)
                {
                    matrixOk = true;
                }
                else if (targetRenderer == null && targetUI == null && targetUI2 == null)
                {
                    if (!displayColorMatrixNoTargetLogged)
                    {
                        displayColorMatrixNoTargetLogged = true;
                        Debug.LogWarning("[VideoColorCalibrator] Display color matrix targets not assigned; skipping matrix correction.");
                    }
                    matrixOk = true;
                }
                else
                {
                    bool rowsMatch =
                        displayColorMatrixHasRows &&
                        displayColorMatrixRow0 == forcedM0 &&
                        displayColorMatrixRow1 == forcedM1 &&
                        displayColorMatrixRow2 == forcedM2;
                    matrixOk = rowsMatch && displayColorMatrixApplied;
                }
            }

            if (gpuCalibrationCompleted && matrixOk)
            {
                return;
            }

            if (owner != null)
            {
                owner.SetManualYuvParamsFromCalibrationForCalibrator(
                    swapUvValue: forcedSwapUv != 0,
                    invertUValue: forcedInvertU != 0,
                    invertVValue: forcedInvertV != 0,
                    channelOrder: (int)forcedOrder,
                    inputMode: (int)forcedInputMode);
                owner.SetManualConversionModeForCalibrator();
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                int debugMode = owner != null ? owner.ManualYuvDebugModeForCalibrator : 0;
                QuestVulkanExt.TrySetManualYuvParams(
                    enabled: 1,
                    swapUv: forcedSwapUv,
                    invertU: forcedInvertU,
                    invertV: forcedInvertV,
                    channelOrder: (int)forcedOrder,
                    debugMode: debugMode,
                    inputMode: (int)forcedInputMode);
            }
            catch { }
#endif

            colorMul = forcedColorMul;
            colorAdd = forcedColorAdd;
            SetColorTransform(colorMul, colorAdd);

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                QuestVulkanExt.TrySetYcbcrOverride(forcedYcbcrModel, forcedYcbcrRange, forcedYcbcrSwizzleMode, forcedYcbcrXOff, forcedYcbcrYOff);
            }
            catch { }
#endif

            if (allowMaterialOverride && !displayColorMatrixShaderMissing && (targetRenderer != null || targetUI != null || targetUI2 != null))
            {
                ApplyDisplayColorMatrix(forcedM0, forcedM1, forcedM2);
            }

            gpuCalibrationCompleted = true;
            nextGpuCalibrationAttemptTime = float.PositiveInfinity;

            Debug.Log($"[VideoColorCalibrator] AutoCalib: fixed params enabled ({reason}) swapUv={forcedSwapUv} invertU={forcedInvertU} invertV={forcedInvertV} order={(int)forcedOrder} inputMode={(int)forcedInputMode} colorMul={forcedColorMul} colorAdd={forcedColorAdd} m0={forcedM0} m1={forcedM1} m2={forcedM2}");
        }

        public void MaybePollDecoderColorSignatureAndInvalidate(bool force, AndroidJavaObject decoder, int expectedWidth, int expectedHeight)
        {
            if (decoder == null)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (!force && now < nextDecoderSignaturePollTime)
            {
                return;
            }
            nextDecoderSignaturePollTime = now + 1.0f;

            if (!TryGetDecoderColorSignature(decoder, expectedWidth, expectedHeight, out var sig))
            {
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (gpuCalibrationAhbWidth > 0 && gpuCalibrationAhbHeight > 0)
            {
                sig.Width = gpuCalibrationAhbWidth;
                sig.Height = gpuCalibrationAhbHeight;
            }
#endif

            if (!IsValidDecoderSignature(sig))
            {
                return;
            }

            if (lastDecoderSignature.HasValue && DecoderSignaturesEqual(lastDecoderSignature.Value, sig))
            {
                return;
            }

            Debug.Log($"[VideoColorCalibrator] Decoder color signature changed: old={lastDecoderSignature} new={sig}");
            lastDecoderSignature = sig;
            InvalidateGpuCalibration("decoder color signature changed");
        }

        private bool TryGetDecoderColorSignature(AndroidJavaObject decoder, int expectedWidth, int expectedHeight, out DecoderColorSignature sig)
        {
            sig = default;
            sig.Width = expectedWidth;
            sig.Height = expectedHeight;
            sig.UnityVideoTextureIsLinear = unityVideoTextureIsLinear;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (decoder == null)
            {
                return false;
            }

            try
            {
                sig.Standard = decoder.Call<int>("getLastColorStandard");
                sig.Range = decoder.Call<int>("getLastColorRange");
                sig.Transfer = decoder.Call<int>("getLastColorTransfer");
                sig.ColorFormat = decoder.Call<int>("getLastColorFormat");
            }
            catch
            {
                return false;
            }
#endif

            return true;
        }

        private static bool IsValidDecoderSignature(DecoderColorSignature sig)
        {
            return sig.Standard >= 0 &&
                   sig.Range >= 0 &&
                   sig.Transfer >= 0 &&
                   sig.ColorFormat >= 0 &&
                   sig.Width > 0 &&
                   sig.Height > 0;
        }

        private static bool DecoderSignaturesEqual(DecoderColorSignature a, DecoderColorSignature b)
        {
            return a.Standard == b.Standard &&
                   a.Range == b.Range &&
                   a.Transfer == b.Transfer &&
                   a.ColorFormat == b.ColorFormat &&
                   a.Width == b.Width &&
                   a.Height == b.Height &&
                   a.UnityVideoTextureIsLinear == b.UnityVideoTextureIsLinear;
        }

        public void SetColorTransform(Vector4 mul, Vector4 add)
        {
            colorMul = mul;
            colorAdd = add;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                QuestVulkanExt.QuestVulkan_SetColorTransform(mul, add);
            }
            catch { }
#endif
        }

        private void EnsureDisplayColorMatrixMaterial()
        {
            if (displayColorMatrixMaterial != null)
            {
                return;
            }

            Shader shader = Shader.Find("Quest3VideoPlayer/QuestVideoColorMatrix");
            if (shader == null)
            {
                displayColorMatrixShaderMissing = true;
                if (!displayColorMatrixShaderMissingLogged)
                {
                    displayColorMatrixShaderMissingLogged = true;
                    Debug.LogWarning("[VideoColorCalibrator] Display color matrix shader not found; skipping matrix correction.");
                }
                return;
            }

            displayColorMatrixShaderMissing = false;
            displayColorMatrixMaterial = new Material(shader);
        }

        private void ApplyDisplayColorMatrix(Vector4 row0, Vector4 row1, Vector4 row2)
        {
            if (!allowMaterialOverride)
            {
                return;
            }

            EnsureDisplayColorMatrixMaterial();
            if (displayColorMatrixMaterial == null)
            {
                return;
            }

            displayColorMatrixMaterial.SetVector("_CM0", row0);
            displayColorMatrixMaterial.SetVector("_CM1", row1);
            displayColorMatrixMaterial.SetVector("_CM2", row2);

            displayColorMatrixHasRows = true;
            displayColorMatrixRow0 = row0;
            displayColorMatrixRow1 = row1;
            displayColorMatrixRow2 = row2;

            if (targetRenderer != null)
            {
                if (originalRendererMaterial == null)
                {
                    originalRendererMaterial = targetRenderer.sharedMaterial;
                }
                targetRenderer.sharedMaterial = displayColorMatrixMaterial;
            }
            if (targetUI != null)
            {
                if (originalUiMaterial == null)
                {
                    originalUiMaterial = targetUI.material;
                }
                targetUI.material = displayColorMatrixMaterial;
            }
            if (targetUI2 != null)
            {
                if (originalUiMaterial == null)
                {
                    originalUiMaterial = targetUI2.material;
                }
                targetUI2.material = displayColorMatrixMaterial;
            }

            displayColorMatrixApplied = true;
        }

        public void RestoreDisplayMaterials()
        {
            if (!displayColorMatrixApplied)
            {
                return;
            }

            if (targetRenderer != null && originalRendererMaterial != null)
            {
                targetRenderer.sharedMaterial = originalRendererMaterial;
            }
            if (targetUI != null)
            {
                targetUI.material = originalUiMaterial;
            }
            if (targetUI2 != null)
            {
                targetUI2.material = originalUiMaterial;
            }

            originalRendererMaterial = null;
            originalUiMaterial = null;
            displayColorMatrixApplied = false;
        }

        private void OnDestroy()
        {
            if (gpuCalibrationRoutine != null)
            {
                StopCoroutine(gpuCalibrationRoutine);
                gpuCalibrationRoutine = null;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (gpuCalibrationAhbPtr != IntPtr.Zero)
            {
                try { QuestVulkanExt.QuestVulkan_ReleaseAHardwareBuffer(gpuCalibrationAhbPtr); } catch { }
                gpuCalibrationAhbPtr = IntPtr.Zero;
            }
#endif

            if (gpuCalibrationRt != null)
            {
                gpuCalibrationRt.Release();
                Destroy(gpuCalibrationRt);
                gpuCalibrationRt = null;
            }

            if (gpuCalibrationReadback != null)
            {
                Destroy(gpuCalibrationReadback);
                gpuCalibrationReadback = null;
            }

            RestoreDisplayMaterials();
        }
    }
}
