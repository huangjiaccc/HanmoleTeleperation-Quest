# Unity 视频播放链路深度代码分析

## 执行环境说明
- 版本: Unity 6000.2.10f1, URP, Meta XR SDK, OpenXR
- 主场景: Assets/Scenes/QuestMain.unity
- 分析时间: 当前代码状态
- 关键条件: ProductionFixed模式, 检查AutoCalibrate=false

---

## 1. ProductionFixed 模式下的Inspector字段覆盖

### QuestStreamVideoPlayer.ApplyProductionDefaults()
在 `Awake()` 和 `OnValidate()` 中（编辑器）被调用，**不可逆地覆盖以下字段**：

#### 确定被覆盖的字段（强制生产值）

| 字段 | 生产默认值 | 影响范围 |
|------|----------|--------|
| `expectedWidth` | 2560 | 接收帧分辨率 |
| `expectedHeight` | 1440 | 接收帧分辨率 |
| `autoStart` | true | 自动启动解码器 |
| `verboseLogging` | false | 禁用详细日志 |
| `flipTextureHorizontally` | true | 水平翻转纹理 |
| `flipTextureVertically` | true | 竖直翻转纹理 |
| `preferVulkanHardwareBufferFrames` | true | 强制GPU路径 |
| `useJavaDecoderColorInfo` | true | **启用Java色彩信息** |
| `ycbcrModel` | Auto | 自动检测色彩模型 |
| `ycbcrRange` | Auto | 自动检测色彩范围 |
| `swapCbCr` | false | 保持Cb/Cr顺序 |
| `swapRedBlue` | false | 保持RGB顺序 |
| `chromaX` | Auto | 自动色度采样X |
| `chromaY` | Auto | 自动色度采样Y |
| `manualYuvConversion` | true | **启用手动转换** |
| `forceHardwareYcbcrConversion` | false | 禁用硬件转换 |
| `forceJavaCpuYuvMatrix` | false | 不强制本地矩阵 |
| `swapUv` | false | 不交换U/V |
| `invertU` | false | 不反演U |
| `invertV` | false | 不反演V |
| `manualYuvChannelOrder` | YUV | YUV顺序 |
| `manualYuvDebug` | Normal | 不显示调试通道 |
| `manualYuvInputMode` | **ByteNarrowJava** | ⚠️ **Java兼容模式** |
| `logFrameChecksums` | false | 禁用校验和日志 |
| `listenPort` | 5000 | UDP监听端口 |
| `listenPortSecondary` | 4000 | 辅助UDP端口 |
| `maxInFlightHardwareFrames` | 2 | 最多保留2帧 |
| `enableHardwareStallRecovery` | false | 禁用失速恢复 |

### VideoColorCalibrator.ApplyProductionDefaults()
在同一生命周期被覆盖：

| 字段 | 生产默认值 | 关键含义 |
|------|----------|---------|
| `autoCalibrateGpuYuvFromCpuFirstFrame` | **false** | ⚠️ **禁用GPU自动校准** |
| `autoCalibrateFramesToSettle` | 0 | 不等待稳定帧 |
| `autoCalibrateSampleSize` | 32 | 校准采样大小 |
| `autoCalibrateVerbose` | false | 禁用校准日志 |
| `autoCalibrateAllowHardwareYcbcr` | **false** | ⚠️ **禁用硬件YCbCr** |
| `autoCalibrateApplyPostColorMul` | **false** | ⚠️ **禁用色彩增益** |
| `autoCalibrateApplyDisplayColorMatrix` | **false** | ⚠️ **禁用显示矩阵** |
| `forceFixedManualYuvParams` | **true** | ⚠️ **强制固定参数** |
| `allowMaterialOverride` | true | 允许覆盖材质 |
| `unityVideoTextureIsLinear` | true | 线性色彩空间 |
| `colorMul` | (1,1,1,1) | 默认无色增益 |
| `colorAdd` | (0,0,0,0) | 默认无色偏移 |

**关键观察**：ProductionFixed 模式下的 VideoColorCalibrator 配置**完全禁用了所有自动校准功能**，转而依赖 `forceFixedManualYuvParams=true` 的硬编码值。

---

## 2. autoCalibrateGpuYuvFromCpuFirstFrame = false 时的死代码路径

### 2.1 完全不执行的代码段

#### VideoColorCalibrator.ShouldAutoCalibrateGpuYuv()
```csharp
return owner.IsAndroidTargetForCalibrator &&
       owner.UseJavaDecoderColorInfoForCalibrator &&
       owner.PreferVulkanHardwareBufferFramesForCalibrator &&
       owner.ManualYuvConversionForCalibrator &&
       !owner.ForceHardwareYcbcrConversionForCalibrator &&
       owner.SupportsCpuCalibrationReferenceForCalibrator &&
       !forceFixedManualYuvParams &&
       autoCalibrateGpuYuvFromCpuFirstFrame &&        // ⚠️ FALSE in ProductionFixed
       !gpuCalibrationCompleted;
```

**当 `autoCalibrateGpuYuvFromCpuFirstFrame=false` 时，以下整个代码路径永不执行：**

| 方法/代码块 | 位置 | 触发条件 |
|-----------|------|---------|
| `MaybeCaptureCpuCalibrationReferenceFromJava()` | 全部 | `ShouldAutoCalibrateGpuYuv()` 返回false |
| `TryCaptureGpuCalibrationAhbFromBundle()` | 全部 | 同上 |
| `MaybeStartGpuYuvAutoCalibration()` | 全部 | 同上 |
| `AutoCalibrateGpuYuvRoutine()` | 全部协程 | `ShouldAutoCalibrateGpuYuv()` 早期返回 |

#### 具体未执行的代码行

**VideoColorCalibrator.MaybeCaptureCpuCalibrationReferenceFromJava() (第 ~310 行)**
```csharp
#if UNITY_ANDROID && !UNITY_EDITOR
if (!ShouldAutoCalibrateGpuYuv() || cpuCalibrationReferenceCaptured || ...)
{
    return;  // ⚠️ 总是在此返回
}

// 以下永不执行:
AndroidJavaObject frameBundle = owner.DecoderForCalibrator.Call<AndroidJavaObject>("dequeueCalibrationFrameBundle");
sbyte[] javaFrame = frameBundle.Call<sbyte[]>("getImage");
int[] headerData = frameBundle.Call<int[]>("getHeader");
// ... (所有CPU参考采样逻辑)
cpuCalibrationReferenceCaptured = true;
owner.DecoderForCalibrator.Call("trimCpuCalibrationResources");
#endif
```

**死代码特征**：
- Java 解码器侧的 `dequeueCalibrationFrameBundle()` 调用永不发生
- CPU参考RGB均值不会被计算
- `cpuCalibrationReferenceCaptured` 永远 = false
- CPU采样像素网格永不构建

**VideoColorCalibrator.AutoCalibrateGpuYuvRoutine() (第 ~950 行)**

即使例程开始，也会在 ProductionFixed 条件下立即返回：
```csharp
private IEnumerator AutoCalibrateGpuYuvRoutine()
{
    gpuCalibrationRunning = true;
    try
    {
        if (forceFixedManualYuvParams)  // ⚠️ ProductionFixed = true
        {
            yield break;  // 立即退出
        }

        if (cpuCalibrationSamplePixels == null || ...)  // ⚠️ CPU样本永为null
        {
            yield break;  // 第二个早期返回点
        }

        // 以下所有代码都不执行:
        // - 硬件YCbCr五层嵌套循环 (~1100-1200行)
        // - GPU像素采样和误差计算
        // - 手动YUV候选遍历 (~2000+ 候选)
        // - 显示色彩矩阵拟合
        // - 后处理色彩增益应用
    }
}
```

### 2.2 有条件地跳过的初始化

**VideoColorCalibrator.OnStreamStarted()**
```csharp
lastDecoderSignature = null;
nextDecoderSignaturePollTime = 0f;  // ⚠️ 设为0，但...
nextGpuCalibrationAttemptTime = 0f;  // ⚠️ 设为0，但...
displayColorMatrixNoTargetLogged = false;
```

这些时间戳在 ProductionFixed 下虽然被初始化为0，但对应的 poll/attempt 方法会立即被跳过：

- `MaybePollDecoderColorSignatureAndInvalidate()` - 检查 `nextDecoderSignaturePollTime` 但 `ShouldAutoCalibrateGpuYuv()` 返回false
- `MaybeStartGpuYuvAutoCalibration()` - 检查 `nextGpuCalibrationAttemptTime` 但全被跳过

### 2.3 从不被设置为true的标志位

| 标志位 | 预期值 | ProductionFixed下值 | 影响 |
|-------|-------|------------------|------|
| `cpuCalibrationReferenceCaptured` | true（经过采样后） | **永远false** | 无CPU参考 |
| `gpuCalibrationRunning` | true（例程中） | **永远false** | 不启动例程 |
| `gpuCalibrationCompleted` | true（成功校准后） | false → set via `ApplyFixedManualYuvParams()` | 使用硬编码值 |
| `gpuCalibrationAhbPtr` | 有效指针 | **永远IntPtr.Zero** | 无GPU读回 |

---

## 3. useJavaDecoderColorInfo=true 和 forceFixedManualYuvParams=false 时被跳过的Vulkan设置

### 3.1 条件分析

ProductionFixed 模式下的关键条件组合：
```
useJavaDecoderColorInfo         = true   (从Java解码器获取色彩)
forceFixedManualYuvParams       = true   (使用硬编码参数)
manualYuvConversion             = true   (启用手动转换)
forceHardwareYcbcrConversion    = false  (不使用硬件转换)
```

**注**: 用户提问条件为 `forceFixedManualYuvParams=false`，但ProductionFixed总是设为true。以下分析基于代码实际执行路径。

### 3.2 Vulkan设置应用的两个路径

#### 路径A: useJavaDecoderColorInfo=false
```csharp
// QuestStreamVideoPlayer.EnsureQuestVulkanStreamTexture()
if (!useJavaDecoderColorInfo)
{
    ApplyQuestVulkanYcbcrOverride();  // ⚠️ 直接应用YCbCr参数
}
```

**应用的参数**：
```csharp
int nativeModel = GetNativeYcbcrModel(ycbcrModel);      // Auto → -1
int nativeRange = GetNativeYcbcrRange(ycbcrRange);      // Auto → -1
int nativeSwap = swapRedBlue ? 2 : (swapCbCr ? 1 : 0); // 0
int nativeX = GetNativeChromaOffset(chromaX);           // Auto → -1
int nativeY = GetNativeChromaOffset(chromaY);           // Auto → -1
int nativeManualEnabled = manualYuvConversion ? 1 : 0;  // 1
// ... 更多手动参数
```

所有值都通过 `ycbcrOverrideApplier.ApplyYcbcrAndManual()` 应用到原生插件。

#### 路径B: useJavaDecoderColorInfo=true （ProductionFixed 此路径）
```csharp
else
{
    ApplyQuestVulkanManualYuvOverrideOnly();  // ⚠️ 跳过YCbCr设置
}
```

### 3.3 被跳过的Vulkan YCbCr设置

当 `useJavaDecoderColorInfo=true` 时，**以下YCbCr参数不被C#代码应用**：

| Vulkan参数 | C#侧值 | 跳过原因 | 本应作用 |
|----------|-------|--------|---------|
| `YcbcrModel` | Auto(-1) | Java控制 | VkSamplerYcbcrModelConversion |
| `YcbcrRange` | Auto(-1) | Java控制 | ITU Full/Narrow范围 |
| `SwapCbCr` (swizzleMode 1) | false | Java控制 | Cb/Cr通道顺序 |
| `SwapRedBlue` (swizzleMode 2) | false | Java控制 | R/B通道交换 |
| `ChromaOffsetX` | Auto(-1) | Java控制 | VkChromaLocation采样位置X |
| `ChromaOffsetY` | Auto(-1) | Java控制 | VkChromaLocation采样位置Y |

**实际应用代码**：
```csharp
// QuestVulkanYcbcrOverrideApplier.ApplyYcbcrAndManual()
#if UNITY_ANDROID && !UNITY_EDITOR
if (!ycbcrSame)  // 检查YCbCr参数是否改变
{
    QuestVulkanExt.TrySetYcbcrOverride(nativeModel, nativeRange, nativeSwap, nativeX, nativeY);
    // ... 更新缓存
}
#endif
```

当 `ApplyQuestVulkanManualYuvOverrideOnly()` 被调用时：
```csharp
// QuestVulkanYcbcrOverrideApplier.ApplyManualOnly()
// ⚠️ 仅调用此方法，不调用 TrySetYcbcrOverride()
ycbcrOverrideApplier.ApplyManualOnly(
    questVulkanStreamHandle,
    nativeManualEnabled,        // 手动转换启用
    nativeManualSwapUv,         // 0
    nativeManualInvertU,        // 0
    nativeManualInvertV,        // 0
    nativeManualOrder,          // YUV
    nativeManualDebug,          // Normal
    nativeManualInputMode);     // ByteNarrowJava
```

### 3.4 手动YUV参数的部分应用

尽管YCbCr设置被跳过，**手动YUV转换参数仍被应用**：

| 参数 | ProductionFixed值 | 应用方式 |
|------|-----------------|--------|
| `manualYuvConversion` | true | `TrySetManualYuvParams(enabled=1, ...)` |
| `swapUv` | false | `TrySetManualYuvParams(..., swapUv=0, ...)` |
| `invertU` | false | `TrySetManualYuvParams(..., invertU=0, ...)` |
| `invertV` | false | `TrySetManualYuvParams(..., invertV=0, ...)` |
| `manualYuvChannelOrder` | YUV (0) | `TrySetManualYuvParams(..., channelOrder=0, ...)` |
| `manualYuvInputMode` | **ByteNarrowJava** | `TrySetManualYuvParams(..., inputMode=1, ...)` |

**调用堆栈**：
```
ApplyQuestVulkanManualYuvOverrideOnly()
  → ycbcrOverrideApplier.ApplyManualOnly()
    → QuestVulkanExt.TrySetManualYuvParams3()  // EntryPoint: "QuestVulkan_SetManualYuvParams3"
      → native plugin (VulkanExternalTexture.cpp)
```

### 3.5 颜色校准被绕过的机制

**在固定模式下应用的色彩参数**：
```csharp
// VideoColorCalibrator.ApplyFixedManualYuvParams()
const int forcedSwapUv = 0;
const int forcedInvertU = 0;
const int forcedInvertV = 0;
const ManualYuvChannelOrder forcedOrder = (ManualYuvChannelOrder)2;  // UYV(!!)
const ManualYuvInputMode forcedInputMode = (ManualYuvInputMode)1;   // ByteNarrowJava
Vector4 forcedColorMul = new Vector4(0.993f, 0.991f, 0.989f, 1.00f); // RGB增益
Vector4 forcedColorAdd = Vector4.zero;
Vector4 forcedM0 = new Vector4(0.93f, -0.08f, 0.03f, 0.05f);  // 显示矩阵行0
Vector4 forcedM1 = new Vector4(-0.06f, 0.84f, 0.11f, 0.05f);  // 显示矩阵行1
Vector4 forcedM2 = new Vector4(-0.05f, 0.10f, 0.83f, 0.05f);  // 显示矩阵行2
```

**⚠️ 注意**：`forcedOrder = 2` 对应 `ManualYuvChannelOrder.UYV`，而非生产默认的 `YUV (0)`！这是硬编码的固定校准值。

---

## 4. QuestVulkanExt中的P/Invoke调用的条件路径

### 4.1 条件编译与平台检查

所有Vulkan操作都被包含在条件编译中：
```csharp
#if UNITY_ANDROID && !UNITY_EDITOR
```

这意味着在编辑器或非Android构建中，所有以下操作都是编译时不包含的。

### 4.2 P/Invoke签名的向后兼容性降级

#### TrySetManualYuvParams() - 三层降级
```csharp
public static bool TrySetManualYuvParams(int enabled, int swapUv, int invertU, 
                                         int invertV, int channelOrder, 
                                         int debugMode, int inputMode)
{
    try
    {
        QuestVulkan_SetManualYuvParams3(enabled, swapUv, invertU, invertV, 
                                        channelOrder, debugMode, inputMode);  // 第1次尝试
        return true;
    }
    catch
    {
        try
        {
            QuestVulkan_SetManualYuvParams2(enabled, swapUv, debugMode, inputMode);
            return true;  // 第2次尝试成功
        }
        catch
        {
            try
            {
                QuestVulkan_SetManualYuvParams(enabled, swapUv, debugMode);  // 第3次尝试
                return true;
            }
            catch
            {
                return false;  // 全部失败
            }
        }
    }
}
```

**三个导出符号按优先级**：
1. `QuestVulkan_SetManualYuvParams3` (新版本，支持所有参数)
2. `QuestVulkan_SetManualYuvParams2` (中版本，有inputMode但无invert/order)
3. `QuestVulkan_SetManualYuvParams` (旧版本，仅基础参数)

**失败处理**：任意捕获异常即降级，最终所有异常返回false。

#### TrySetYcbcrOverride() - 无降级
```csharp
[DllImport("unity_vulkan_hwbuffer", EntryPoint = "QuestVulkan_SetYcbcrOverride2")]
private static extern void QuestVulkan_SetYcbcrOverride2(int model, int range, 
                                                          int swizzleMode, 
                                                          int xChromaOffset, 
                                                          int yChromaOffset);

public static bool TrySetYcbcrOverride(int model, int range, int swizzleMode, 
                                       int xChromaOffset, int yChromaOffset)
{
    try
    {
        QuestVulkan_SetYcbcrOverride2(...);  // ⚠️ 无降级，直接失败
        return true;
    }
    catch
    {
        return false;
    }
}
```

**特点**：仅尝试一个版本，若抛出异常则整个操作失败。

### 4.3 可选的条件路径

#### QuestVulkan_SetHardwareBufferWithFence() vs QuestVulkan_SetHardwareBuffer()
```csharp
// QuestStreamVideoPlayer.TryUpdateTextureFromHardwareBufferViaQuestVulkanExt()
#if UNITY_ANDROID && !UNITY_EDITOR
try
{
    bool usedFence = false;
    if (fenceFd >= 0)  // ⚠️ 仅当文件描述符有效时
    {
        try
        {
            QuestVulkan_SetHardwareBufferWithFence(questVulkanStreamHandle, ahbPtr, 
                                                   frameWidth, frameHeight, fenceFd);
            usedFence = true;
        }
        catch
        {
            usedFence = false;  // 降级到无围栏版本
        }
    }

    if (!usedFence)  // ⚠️ 围栏版本失败或fenceFd < 0
    {
        if (fenceFd >= 0)  // ⚠️ 需要关闭文件描述符
        {
            try { QuestVulkan_CloseFenceFd(fenceFd); } catch { }
        }
        QuestVulkan_SetHardwareBuffer(questVulkanStreamHandle, ahbPtr, 
                                      frameWidth, frameHeight);  // 无围栏版本
    }

    IssueQuestVulkanEvent(QuestVulkanExt.RenderEventImportHardwareBuffer);
    nativeHardwareImportIssued = true;
    return true;
}
#endif
```

**条件优先级**：
1. 若 `fenceFd >= 0` 且围栏版本不抛异常 → 使用 `SetHardwareBufferWithFence`
2. 若围栏版本抛异常或 `fenceFd < 0` → 使用 `SetHardwareBuffer` (关闭fd)
3. 最终都会触发 `RenderEventImportHardwareBuffer`

#### QuestVulkan_WaitForTexture() - 无超时等待
```csharp
[DllImport("unity_vulkan_hwbuffer")]
[return: MarshalAs(UnmanagedType.I1)]
public static extern bool QuestVulkan_WaitForTexture(IntPtr handle, uint timeoutMs);
```

在校准时总是以 `timeoutMs=0` 调用：
```csharp
// VideoColorCalibrator.AutoCalibrateGpuYuvRoutine()
QuestVulkanExt.QuestVulkan_WaitForTexture(owner.QuestVulkanStreamHandleForCalibrator, 0);
```

这表示"不等待，立即返回"。

### 4.4 可选但通常不执行的初始化路径

#### QuestVulkan_GetRenderEventFunc() - 延迟加载
```csharp
private bool TryEnableNativeHardwareBufferImporter()
{
    if (!IsAndroidTarget()) return false;
    if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan) return false;

    try
    {
        hardwareBufferNativeBridge = new AndroidJavaClass(
            "com.example.questdecoder.HardwareBufferNativeBridge");
    }
    catch { return false; }  // ⚠️ Java桥接加载失败

    try
    {
        questVulkanRenderEventFunc = QuestVulkan_GetRenderEventFunc();  // ⚠️ 仅此处加载
        if (questVulkanRenderEventFunc == IntPtr.Zero) return false;   // ⚠️ 加载失败
        return true;
    }
    catch { return false; }
}
```

**使用场景**：仅当Unity AndroidHardwareBuffer API 不可用时才进入此路径。

#### QuestVulkan_SetColorTransform() - 条件应用
```csharp
public static void QuestVulkan_SetColorTransform(Vector4 mul, Vector4 add)
{
    QuestVulkan_SetColorTransform(mul.x, mul.y, mul.z, mul.w, add.x, add.y, add.z, add.w);
}

// 调用处：
#if UNITY_ANDROID && !UNITY_EDITOR
public void SetColorTransform(Vector4 mul, Vector4 add)
{
    colorMul = mul;
    colorAdd = add;
    try
    {
        QuestVulkan_SetColorTransform(mul, add);  // ⚠️ 仅Android构建
    }
    catch { }
}
#endif
```

在ProductionFixed下被调用于：
- `ApplyFixedManualYuvParams()` - 应用硬编码 `0.993, 0.991, 0.989, 1.0`
- `AutoCalibrateGpuYuvRoutine()` - 应用自动校准计算的增益（永不执行）
- `SetColorTransform(mil, add)` - 暴露的公共接口

### 4.5 完全条件的P/Invoke组合

```
EnsureQuestVulkanStreamTexture()  // 仅当 useNativeHardwareBufferImporter=true
  ├─ TrySetYcbcrOverride()  // 仅当 !useJavaDecoderColorInfo
  └─ TrySetManualYuvParams()  // 仅当 manualYuvConversion || forceJavaCpuYuvMatrix

TryUpdateTextureFromHardwareBufferViaQuestVulkanExt()  // 仅当native importer启用
  ├─ EnsureQuestVulkanStreamTexture()
  ├─ EnsureQuestVulkanUnityTexture()
  ├─ QuestVulkan_SetHardwareBuffer(With/WithoutFence)()
  ├─ QuestVulkan_GetTextureOperationStatus()  // 检查进度
  └─ IssueQuestVulkanEvent()

QuestVulkanYcbcrOverrideApplier.ApplyYcbcrAndManual()  // 仅在 !#if 块内
  ├─ TrySetYcbcrOverride()  // 仅当 ycbcrSame == false
  └─ TrySetManualYuvParams()  // 仅当 manualSame == false
```

在ProductionFixed下的实际调用路径：
```
StartStream()
  ├─ useNativeHardwareBufferImporter = false  (Unity API优先)
  ├─ useJavaDecoderColorInfo = true
  └─ ConfigureJavaColorPipeline()
       └─ decoder.Call("setNativeColorInfoHandoffEnabled", true)

PullFramesLoop() in GameObject.Update
  ├─ TryCaptureGpuCalibrationAhbFromBundle()  [不执行]
  ├─ ApplyFixedManualYuvParams()
  │   └─ SetColorTransform(0.993, 0.991, 0.989, 1.0)  [Android仅]
  └─ TryUpdateTextureFromHardwareBufferFrame()
       └─ (Unity API或native importer，不调用任何Vulkan参数设置方法)
```

---

## 5. 汇总：完整的死代码列表

### 5.1 条件编译外的死代码（非Android/编辑器）

所有包含在 `#if UNITY_ANDROID && !UNITY_EDITOR` 中的代码在编辑器运行时不执行：

**VideoColorCalibrator**:
- 第 ~310 行: `MaybeCaptureCpuCalibrationReferenceFromJava()` 整体
- 第 ~370 行: `TryCaptureGpuCalibrationAhbFromBundle()` 整体  
- 第 ~950 行: `AutoCalibrateGpuYuvRoutine()` - 硬件YCbCr阶段 (行 ~1100-1200)

**QuestStreamVideoPlayer**:
- 第 ~1630 行: `EnsureQuestVulkanUnityTexture()` 整体
- 第 ~1730 行: `ApplyQuestVulkanYcbcrOverride()` 整体
- 第 ~1755 行: `ApplyQuestVulkanManualYuvOverrideOnly()` 整体
- 第 ~1900 行: `TryUpdateTextureFromHardwareBufferViaQuestVulkanExt()` 整体
- 第 ~1970 行: `IssueQuestVulkanEvent()` 整体

### 5.2 ProductionFixed=true 时的逻辑死代码

| 代码位置 | 为什么永不执行 | 影响 |
|--------|-------------|------|
| `MaybeCaptureCpuCalibrationReferenceFromJava()` | `ShouldAutoCalibrateGpuYuv()` 返回false | 无CPU参考帧 |
| `MaybeStartGpuYuvAutoCalibration()` | 同上 | 不启动异步校准 |
| `AutoCalibrateGpuYuvRoutine()` 协程体 | `forceFixedManualYuvParams=true` 导致早期返回 | 不执行48000+个YCbCr组合 |
| `TryFitColorMatrix3x4()` (自动校准路径) | 校准例程未启动 | 不计算3x3矩阵 |
| `autoCalibrateAllowHardwareYcbcr` 分支 | `autoCalibrateGpuYuvFromCpuFirstFrame=false` | 跳过硬件YCbCr探索 |
| 硬件YCbCr细网搜索 (~100 候选) | 同上 | 不尝试model/range/swizzle组合 |

### 5.3 useJavaDecoderColorInfo=true 时被跳过的Vulkan调用

| P/Invoke | 被跳过原因 | 本应应用的值 |
|---------|---------|-----------|
| `TrySetYcbcrOverride()` | `useJavaDecoderColorInfo=true` | Java提供的model/range |
| `Get/SetYcbcrModel()` (Auto值) | Java优先 | -1 (Auto) → Java驱动 |
| `Get/SetYcbcrRange()` | Java优先 | -1 (Auto) → Java驱动 |
| `swapRedBlue`/`swapCbCr` 应用 | Java优先 | 色彩通道顺序 |
| `chromaOffsetX`/`chromaOffsetY` | Java优先 | 色度采样位置 |

**实际应用的参数** (通过 `ApplyQuestVulkanManualYuvOverrideOnly()`):
- 手动转换启用 (always true)
- swapUv: 0
- invertU: 0
- invertV: 0
- channelOrder: 0 (YUV) [⚠️ 固定模式下改为2=UYV]
- inputMode: 1 (ByteNarrowJava)

---

## 6. 关键发现与建议

### 6.1 关键发现

1. **完全禁用自动校准系统** (ProductionFixed)
   - 所有GPU校准代码被 `forceFixedManualYuvParams=true` 和 `autoCalibrateGpuYuvFromCpuFirstFrame=false` 禁用
   - CPU参考帧采样永不发生
   - 48000+个YUV参数候选永不被评估

2. **硬编码色彩参数**
   - RGB增益: (0.993, 0.991, 0.989, 1.0) - 略微色彩校正
   - 显示矩阵: 3x4矩阵 (见 section 3.5) - 额外的色彩转换
   - 通道顺序: UYV (不是YUV!)

3. **Java驱动的YCbCr处理**
   - `useJavaDecoderColorInfo=true` 将YCbCr模型/范围/采样决策权交给Java解码器
   - C#仅应用手动YUV参数，不发送YCbCr模型覆盖
   - 原生插件接收Java提供的缓冲区和色彩空间提示

4. **双路径架构** (已实现)
   - 路径A: Unity AndroidHardwareBuffer API (首选)
   - 路径B: Native Vulkan importer (备选)
   - 两条路在目前生产配置下不同时启用

5. **P/Invoke向后兼容性设计**
   - `TrySetManualYuvParams()` 有三层降级机制
   - `TrySetYcbcrOverride()` 无降级，直接失败
   - 意味着旧版原生插件可能无法设置所有参数

### 6.2 死代码清理建议

1. **删除无条件编译块** (仅编辑器运行时)
   - 不影响构建的代码可被编译器内联或优化掉

2. **条件删除 (需谨慎)**
   ```
   如果 forceFixedManualYuvParams 总是=true:
   - 删除整个 AutoCalibrateGpuYuvRoutine()
   - 删除 MaybeCaptureCpuCalibrationReferenceFromJava()
   - 删除 TryCaptureGpuCalibrationAhbFromBundle()
   - 删除 autoCalibrateXxx 所有检查
   
   如果 useJavaDecoderColorInfo 总是=true:
   - 删除 ApplyQuestVulkanYcbcrOverride() 调用
   - 删除 ycbcrModel/ycbcrRange/chromaX/chromaY Inspector字段
   ```

3. **三层P/Invoke降级**
   - 可消减为单层 (仅保留最新版本) 若确认目标设备总是最新原生插件

### 6.3 测试建议

1. **验证ProductionFixed模式**
   ```csharp
   // 在 Awake 中验证:
   Assert.AreEqual(configMode, ConfigMode.ProductionFixed);
   Assert.IsFalse(autoCalibrateGpuYuvFromCpuFirstFrame);
   Assert.IsTrue(forceFixedManualYuvParams);
   Assert.IsTrue(useJavaDecoderColorInfo);
   ```

2. **验证P/Invoke调用**
   - 添加日志到 `TrySetManualYuvParams()` 以确认调用版本
   - 监控 `TrySetYcbcrOverride()` 返回值 (应为false当useJavaDecoderColorInfo=true)

3. **验证纹理路径**
   - 确认 `zeroCopyPathConfirmedThisStream` 被设为true
   - 检查 `useNativeHardwareBufferImporter` 的实际值

---

## 7. 流程图总结

```
QuestStreamVideoPlayer.StartStream()
  ├─ Awake() → ApplyProductionDefaults() → 覆盖所有31个字段
  │
  ├─ preferVulkanHardwareBufferFrames=true?
  │  └─ YES → usingHardwareBufferFrames=true
  │           ├─ TryEnsureAhbReflection() (Unity API反射)
  │           │  └─ 成功 → useNativeHardwareBufferImporter=false
  │           │  └─ 失败 → TryEnableNativeHardwareBufferImporter()
  │           │             └─ 成功 → useNativeHardwareBufferImporter=true
  │           │             └─ 失败 → 中止流
  │           │
  │           └─ decoder.Call("setUseHardwareBufferFrames", true)
  │
  ├─ ConfigureJavaColorPipeline()
  │  └─ decoder.Call("setNativeColorInfoHandoffEnabled", true)  ← useJavaDecoderColorInfo=true
  │
  └─ decoder.Call("start")

PullFramesLoop() (in Update)
  ├─ MaybeCaptureCpuCalibrationReferenceFromJava()
  │  └─ [DEAD] ShouldAutoCalibrateGpuYuv()=false → 早期返回
  │
  ├─ ApplyFixedManualYuvParams("PullFramesLoop")  ← ProductionFixed
  │  ├─ 硬编码手动YUV参数: swapUv=0, invertU=0, invertV=0, order=UYV, inputMode=ByteNarrowJava
  │  ├─ QuestVulkanExt.TrySetManualYuvParams(enabled=1, ...) → native
  │  ├─ 硬编码RGB增益: (0.993, 0.991, 0.989, 1.0)
  │  ├─ QuestVulkanExt.TrySetColorTransform(...) → native
  │  └─ 硬编码显示矩阵: 3x4 matrix rows
  │     └─ ApplyDisplayColorMatrix(...) → 替换材质shader参数
  │
  ├─ MaybeStartGpuYuvAutoCalibration()
  │  └─ [DEAD] ShouldAutoCalibrateGpuYuv()=false → 早期返回
  │
  └─ TryUpdateTextureFromHardwareBufferFrame()
     ├─ useNativeHardwareBufferImporter=false?
     │  └─ YES → 使用Unity API
     │           ├─ TryEnsureAhbReflection()
     │           ├─ AndroidHardwareBuffer.Import(hardwareBuffer)
     │           └─ Texture2D.CreateExternalTexture() / UpdateExternalTexture()
     │
     └─ useNativeHardwareBufferImporter=true?
        └─ YES → TryUpdateTextureFromHardwareBufferViaQuestVulkanExt()
                 ├─ EnsureQuestVulkanStreamTexture(frameWidth, frameHeight)
                 │  └─ [Java参数路径] ApplyQuestVulkanManualYuvOverrideOnly()
                 │     └─ ycbcrOverrideApplier.ApplyManualOnly(...)
                 │        └─ QuestVulkanExt.TrySetManualYuvParams(...)
                 │           └─ QuestVulkan_SetManualYuvParams3() [尝试]
                 │           └─ QuestVulkan_SetManualYuvParams2() [降级]
                 │           └─ QuestVulkan_SetManualYuvParams() [降级]
                 ├─ EnsureQuestVulkanUnityTexture(frameWidth, frameHeight)
                 ├─ hardwareBufferNativeBridge.acquireAHardwareBuffer(hwbuf)
                 ├─ QuestVulkan_SetHardwareBufferWithFence() / SetHardwareBuffer()
                 └─ IssueQuestVulkanEvent(RenderEventImportHardwareBuffer)
```

