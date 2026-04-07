# P/Invoke调用条件矩阵和代码路径决策树

## 1. 条件状态矩阵

### ProductionFixed mode 状态快照

| 条件变量 | ProductionFixed值 | 影响范围 | 状态 |
|---------|-----------------|--------|------|
| **Startup Phase** |||||
| configMode | ProductionFixed | 全体 | 锁定 |
| ApplyProductionDefaults() | invoked | 31个字段 | 覆盖 |
| ||||
| **Resolution** | | | |
| expectedWidth | 2560 | 解码大小 | 固定 |
| expectedHeight | 1440 | 解码大小 | 固定 |
| ||||
| **Hardware Pipeline** | | | |
| preferVulkanHardwareBufferFrames | true | 启用GPU路径 | 强制 |
| usingHardwareBufferFrames | true | 实际使用 | 强制 |
| useNativeHardwareBufferImporter | false | 使用Unity API优先 | 默认 |
| ||||
| **Color Info Handling** | | | |
| useJavaDecoderColorInfo | true | Java控制YCbCr | 强制 |
| ||||
| **Auto-Calibration** | | | |
| autoCalibrateGpuYuvFromCpuFirstFrame | **false** | 禁用校准 | ⚠️ 关键 |
| forceFixedManualYuvParams | **true** | 使用硬编码 | ⚠️ 关键 |
| autoCalibrateAllowHardwareYcbcr | false | 跳过硬件YCbCr | 禁用 |
| autoCalibrateApplyPostColorMul | false | 无RGB增益 | 禁用 |
| autoCalibrateApplyDisplayColorMatrix | false | 无矩阵修正 | 禁用 |
| ||||
| **Manual YUV** | | | |
| manualYuvConversion | true | 启用手动转换 | 强制 |
| forceHardwareYcbcrConversion | false | 不用硬件YCbCr | 禁用 |
| manualYuvChannelOrder | YUV → **UYV** | 通道顺序 | 固定值 |
| manualYuvInputMode | **ByteNarrowJava** | 参数编码 | 固定值 |
| ||||
| **Texture Control** | | | |
| flipTextureHorizontally | true | 水平翻转 | 固定 |
| flipTextureVertically | true | 竖直翻转 | 固定 |
| ||||
| **Logging** | | | |
| verboseLogging | false | 隐藏细节日志 | 禁用 |
| logFrameChecksums | false | 隐藏校验日志 | 禁用 |
| ||||
| **Other** | | | |
| maxInFlightHardwareFrames | 2 | 帧缓冲深度 | 最小 |
| listenPort | 5000 | UDP端口 | 默认 |
| listenPortSecondary | 4000 | 辅助端口 | 默认 |

---

## 2. 代码路径决策树

### StartStream() 执行树

```
StartStream()
│
├─ 条件: IsAndroidTarget() = true
│  └─ 继续执行
│
├─ 条件: decoder == null
│  └─ 继续执行
│
├─ 构造: new AndroidJavaObject("com.example.questdecoder.Av1StreamingDecoder", ...)
│  
├─ 调用: decoder.Call("setVerbose", false)
├─ 调用: decoder.Call("setDebugChecksums", false)
│
├─ 判断: ShouldUseHardwareBufferFrames()
│  ├─ preferVulkanHardwareBufferFrames = true? → YES
│  ├─ SystemInfo.graphicsDeviceType = Vulkan? → YES
│  └─ usingHardwareBufferFrames = true ✓
│
├─ 判断: TryEnsureAhbReflection()
│  ├─ Try Unity AndroidHardwareBuffer API
│  ├─ Success? → useNativeHardwareBufferImporter = false ✓
│  └─ Fail? → TryEnableNativeHardwareBufferImporter()
│     ├─ Android && Vulkan? → YES
│     ├─ Load "com.example.questdecoder.HardwareBufferNativeBridge"
│     ├─ Load libunity_vulkan_hwbuffer.so
│     ├─ Get QuestVulkan_GetRenderEventFunc()
│     └─ Success? → useNativeHardwareBufferImporter = true
│        Fail? → AbortStartStreamForGpuOnly() [ERROR]
│
├─ 调用: decoder.Call("setUseHardwareBufferFrames", true)
├─ 调用: decoder.Call("setNativeHardwareBufferImporterEnabled", false)
│
├─ 调用: ConfigureJavaColorPipeline()
│  ├─ decoder.Call("setUnityAutoCalibrationEnabled", false)
│  │  (ShouldAutoCalibrateGpuYuv() = false due to autoCalibrateGpuYuvFromCpuFirstFrame=false)
│  └─ decoder.Call("setNativeColorInfoHandoffEnabled", true)
│     (useJavaDecoderColorInfo = true)
│
├─ 调用: decoder.Call("start")
│
├─ 调用: colorCalibrator?.OnStreamStarted()
│
├─ 调用: colorCalibrator.ApplyFixedManualYuvParams("StartStream")
│  ├─ forceFixedManualYuvParams = true? → YES, 继续
│  ├─ 停止任何运行的校准例程
│  ├─ 应用硬编码参数:
│  │  ├─ swapUv = 0
│  │  ├─ invertU = 0
│  │  ├─ invertV = 0
│  │  ├─ channelOrder = 2 (UYV)
│  │  ├─ inputMode = 1 (ByteNarrowJava)
│  │  ├─ colorMul = (0.993, 0.991, 0.989, 1.0)
│  │  └─ colorAdd = (0, 0, 0, 0)
│  ├─ 调用: QuestVulkanExt.TrySetManualYuvParams(1, 0, 0, 0, 2, 0, 1) [Android仅]
│  └─ 调用: ApplyDisplayColorMatrix(m0, m1, m2)
│
└─ 启动: frameRoutine = StartCoroutine(PullFramesLoop())
```

### PullFramesLoop() 执行树

```
while (decoder != null)
│
├─ 调用: MaybeCaptureCpuCalibrationReferenceFromJava()
│  ├─ 检查: ShouldAutoCalibrateGpuYuv()
│  │  ├─ autoCalibrateGpuYuvFromCpuFirstFrame = false? → [DEAD RETURN]
│  │  └─ 以下代码永不执行:
│  │     ├─ decoder.Call("dequeueCalibrationFrameBundle")
│  │     ├─ 计算CPU参考RGB
│  │     └─ cpuCalibrationReferenceCaptured = true
│  └─ [返回]
│
├─ 调用: ApplyFixedManualYuvParams("PullFramesLoop")
│  ├─ forceFixedManualYuvParams = true? → YES
│  ├─ gpuCalibrationRoutine == null? → YES
│  ├─ QuestVulkanExt.TrySetManualYuvParams(1, 0, 0, 0, 2, 0, 1)
│  ├─ SetColorTransform((0.993, 0.991, 0.989, 1.0), (0,0,0,0))
│  │  └─ QuestVulkanExt.QuestVulkan_SetColorTransform(...) [Android仅]
│  ├─ ApplyDisplayColorMatrix(m0, m1, m2) [已应用]
│  └─ gpuCalibrationCompleted = true (强制完成标记)
│
├─ 调用: MaybeStartGpuYuvAutoCalibration()
│  ├─ 检查: forceFixedManualYuvParams = true? → YES [EARLY RETURN]
│  └─ 以下代码永不执行:
│     ├─ StartCoroutine(AutoCalibrateGpuYuvRoutine())
│     └─ gpuCalibrationRunning 永 = false
│
├─ 调用: decoder.Call("dequeueHardwareBufferFrame")
│  └─ AndroidJavaObject frameBundle (或 null)
│
├─ 若 frameBundle != null:
│  │
│  ├─ 获取: frameWidth, frameHeight
│  ├─ 调用: ApplyHardwareFrameToTargets(frameBundle, width, height)
│  │  │
│  │  ├─ 调用: colorCalibrator?.TryCaptureGpuCalibrationAhbFromBundle(...)
│  │  │  └─ [DEAD] ShouldAutoCalibrateGpuYuv() = false
│  │  │
│  │  ├─ 检查: calibrationHoldBundle != null && 校准未运行?
│  │  │  └─ 释放之前的calibrationHoldBundle
│  │  │
│  │  ├─ 调用: TryUpdateTextureFromHardwareBufferFrame(frameBundle, width, height)
│  │  │  │
│  │  │  ├─ 分支A: 使用Unity AndroidHardwareBuffer API (首选)
│  │  │  │  └─ TryEnsureAhbReflection() = true?
│  │  │  │     ├─ AndroidHardwareBuffer.Import(hardwareBuffer)
│  │  │  │     ├─ GetNativeTexturePtr()
│  │  │  │     ├─ Texture2D.CreateExternalTexture()/UpdateExternalTexture()
│  │  │  │     └─ externalTexture2D (Texture2D, 由Unity管理)
│  │  │  │
│  │  │  ├─ 分支B: 使用Native Vulkan Importer (备选, useNativeHardwareBufferImporter=true时)
│  │  │  │  ├─ 调用: TryUpdateTextureFromHardwareBufferViaQuestVulkanExt(...)
│  │  │  │  │  │
│  │  │  │  │  ├─ 调用: EnsureQuestVulkanStreamTexture(width, height)
│  │  │  │  │  │  │
│  │  │  │  │  │  ├─ 创建: questVulkanStreamHandle = QuestVulkan_CreateStreamTexture(...)
│  │  │  │  │  │  │
│  │  │  │  │  │  ├─ 检查: useJavaDecoderColorInfo = true? → YES
│  │  │  │  │  │  │  └─ 调用: ApplyQuestVulkanManualYuvOverrideOnly()
│  │  │  │  │  │  │     ├─ 构建: nativeManualInputMode = 1 (ByteNarrowJava)
│  │  │  │  │  │  │     ├─ 构建: nativeManualOrder = 0 (YUV)
│  │  │  │  │  │  │     └─ ycbcrOverrideApplier.ApplyManualOnly(...)
│  │  │  │  │  │  │        └─ TrySetManualYuvParams(1, 0, 0, 0, 0, 0, 1)
│  │  │  │  │  │  │           ├─ Try QuestVulkan_SetManualYuvParams3 [native]
│  │  │  │  │  │  │           ├─ Fail → Try QuestVulkan_SetManualYuvParams2 [native]
│  │  │  │  │  │  │           ├─ Fail → Try QuestVulkan_SetManualYuvParams [native]
│  │  │  │  │  │  │           └─ Fail → return false
│  │  │  │  │  │  │
│  │  │  │  │  │  └─ 检查: !useJavaDecoderColorInfo? [CONDITION FALSE]
│  │  │  │  │  │     └─ [此代码分支永不执行]
│  │  │  │  │  │        └─ ApplyQuestVulkanYcbcrOverride()
│  │  │  │  │  │           └─ TrySetYcbcrOverride(...)  [DEAD]
│  │  │  │  │  │
│  │  │  │  │  ├─ 调用: EnsureQuestVulkanUnityTexture(width, height)
│  │  │  │  │  │  ├─ 创建: RenderTexture (若需要)
│  │  │  │  │  │  └─ QuestVulkan_AssignUnityTexture(handle, texturePtr)
│  │  │  │  │  │
│  │  │  │  │  ├─ 检查: nativeHardwareImportIssued? 
│  │  │  │  │  │  └─ 若否: continue (等待首帧)
│  │  │  │  │  │
│  │  │  │  │  ├─ 获取: hardwareBuffer = frameBundle.Call("getHardwareBuffer")
│  │  │  │  │  ├─ 调用: acquireAHardwareBuffer(hardwareBuffer)
│  │  │  │  │  │
│  │  │  │  │  ├─ 获取: fenceFd = frameBundle.Call("takeFenceFd")
│  │  │  │  │  │
│  │  │  │  │  ├─ 条件: fenceFd >= 0?
│  │  │  │  │  │  ├─ YES:
│  │  │  │  │  │  │  ├─ Try: QuestVulkan_SetHardwareBufferWithFence(handle, ahbPtr, width, height, fenceFd)
│  │  │  │  │  │  │  │  usedFence = true [成功]
│  │  │  │  │  │  │  └─ Fail: usedFence = false [降级]
│  │  │  │  │  │  │
│  │  │  │  │  │  └─ NO:
│  │  │  │  │  │     usedFence = false
│  │  │  │  │  │
│  │  │  │  │  ├─ 若 usedFence = false:
│  │  │  │  │  │  ├─ 若 fenceFd >= 0: QuestVulkan_CloseFenceFd(fenceFd)
│  │  │  │  │  │  └─ QuestVulkan_SetHardwareBuffer(handle, ahbPtr, width, height)
│  │  │  │  │  │
│  │  │  │  │  ├─ 调用: IssueQuestVulkanEvent(RenderEventImportHardwareBuffer)
│  │  │  │  │  │  └─ GraphicsCommandBuffer + PluginEvent [异步渲染]
│  │  │  │  │  │
│  │  │  │  │  ├─ nativeHardwareImportIssued = true
│  │  │  │  │  └─ return true
│  │  │  │  │
│  │  │  │  └─ externalTexture (RenderTexture, 由native plugin写入)
│  │  │  │
│  │  │  └─ externalHardwareTexture = Texture or RenderTexture
│  │  │
│  │  ├─ 调用: framesDecoded++, emptyFrameTicks = 0
│  │  │
│  │  ├─ 调用: BindTexture(externalHardwareTexture, targetRenderer, targetUI)
│  │  │
│  │  ├─ 调用: colorCalibrator?.MaybeStartGpuYuvAutoCalibration()
│  │  │  └─ [DEAD 第二次] ShouldAutoCalibrateGpuYuv() = false
│  │  │
│  │  └─ 调用: TrackInFlightHardwareResource(frameBundle, unityHardwareBuffer)
│  │     └─ 保留frameBundle在队列中最多 maxInFlightHardwareFrames(=2) 帧
│  │
│  └─ yield return null
│
└─ yield return null
```

---

## 3. 硬编码固定值表

### VideoColorCalibrator.ApplyFixedManualYuvParams() 中的值

| 参数类型 | 参数名 | 值 | 备注 |
|---------|-------|-----|------|
| **Manual YUV** | forcedSwapUv | 0 | 不交换 |
| | forcedInvertU | 0 | 不反演 |
| | forcedInvertV | 0 | 不反演 |
| | forcedOrder | 2 | **UYV** (通道顺序) |
| | forcedInputMode | 1 | **ByteNarrowJava** |
| **Color Mul** | forcedColorMul | (0.993, 0.991, 0.989, 1.0) | RGB增益 |
| **Color Add** | forcedColorAdd | (0, 0, 0, 0) | RGB偏移 |
| **Display Matrix** | forcedM0 | (0.93, -0.08, 0.03, 0.05) | 行0 显示矩阵 |
| | forcedM1 | (-0.06, 0.84, 0.11, 0.05) | 行1 |
| | forcedM2 | (-0.05, 0.10, 0.83, 0.05) | 行2 |
| **Vulkan YCbCr** | forcedYcbcrModel | 2 | VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_709 |
| | forcedYcbcrRange | 0 | VK_SAMPLER_YCBCR_RANGE_ITU_FULL |
| | forcedYcbcrSwizzleMode | 2 | swapRedBlue |
| | forcedYcbcrXOff | 1 | VK_CHROMA_LOCATION_MIDPOINT |
| | forcedYcbcrYOff | 1 | VK_CHROMA_LOCATION_MIDPOINT |

### 枚举值映射

**ManualYuvChannelOrder**:
```
0 = YUV
1 = YVU
2 = UYV  ← [ProductionFixed固定值]
3 = UVY
4 = VYU
5 = VUY
```

**ManualYuvInputMode**:
```
0 = Normalized
1 = ByteNarrowJava  ← [ProductionFixed固定值]
2 = ByteFull
```

**YcbcrModelSetting** (GetNativeYcbcrModel 映射):
```
return值 ← Setting
0 ← ForceRgbIdentity
1 ← ForceYcbcrIdentity
2 ← Force709  ← [ProductionFixed forcedYcbcrModel]
3 ← Force601
4 ← Force2020
-1 ← Auto
```

---

## 4. 调试技巧

### 验证执行路径

```csharp
// OnGUI或日志输出:
Debug.Log($"[Debug Path] " +
    $"configMode={configMode} " +
    $"autoCalibrateGpuYuvFromCpuFirstFrame={colorCalibrator?.AutoCalibrateGpuYuvFromCpuFirstFrame} " +
    $"forceFixedManualYuvParams={colorCalibrator?.ForceFixedManualYuvParams} " +
    $"cpuCalibrationReferenceCaptured={colorCalibrator?.CpuCalibrationReferenceCaptured} " +
    $"gpuCalibrationRunning={colorCalibrator?.GpuCalibrationRunning} " +
    $"gpuCalibrationCompleted={colorCalibrator?.GpuCalibrationCompleted} " +
    $"useNativeHardwareBufferImporter={useNativeHardwareBufferImporter} " +
    $"useJavaDecoderColorInfo={useJavaDecoderColorInfo} " +
    $"externalTexture={externalHardwareTexture != null ? externalHardwareTexture.GetType().Name : "null"}"
);
```

### 追踪P/Invoke调用

```csharp
// QuestVulkanExt.TrySetManualYuvParams 中添加:
Debug.Log($"[P/Invoke] TrySetManualYuvParams(enabled={enabled}, swapUv={swapUv}, order={channelOrder}, inputMode={inputMode})");
try {
    QuestVulkan_SetManualYuvParams3(enabled, swapUv, invertU, invertV, channelOrder, debugMode, inputMode);
    Debug.Log("[P/Invoke] → QuestVulkan_SetManualYuvParams3 SUCCESS");
    return true;
} catch (Exception ex) {
    Debug.LogWarning($"[P/Invoke] → QuestVulkan_SetManualYuvParams3 FAIL: {ex.Message}");
    // 降级...
}
```

### 条件断点

```
在 ApplyQuestVulkanManualYuvOverrideOnly() 处:
  Break if: useJavaDecoderColorInfo == false  [会到达ApplyQuestVulkanYcbcrOverride]
  Break if: ApplyQuestVulkanYcbcrOverride调用成功

在 AutoCalibrateGpuYuvRoutine() 处:
  Break if: forceFixedManualYuvParams == false  [会执行校准]
```

---

## 5. 参考对应关系

| 代码位置 | 行号范围 | 关键变量 | 条件 |
|---------|---------|--------|------|
| QuestStreamVideoPlayer.ApplyProductionDefaults() | 165-200 | 31个字段 | 覆盖 |
| QuestStreamVideoPlayer.StartStream() | 400-550 | decoder | 初始化 |
| QuestStreamVideoPlayer.ConfigureJavaColorPipeline() | 2015-2040 | useJavaDecoderColorInfo | Java色彩 |
| QuestStreamVideoPlayer.PullFramesLoop() | 550-800 | frameBundle | 帧循环 |
| VideoColorCalibrator.ApplyProductionDefaults() | 100-130 | forceFixedManualYuvParams=true | 固定 |
| VideoColorCalibrator.ApplyFixedManualYuvParams() | 1450-1550 | 硬编码值 | 应用 |
| VideoColorCalibrator.ShouldAutoCalibrateGpuYuv() | 300-320 | autoCalibrateGpuYuvFromCpuFirstFrame | 检查 |
| VideoColorCalibrator.AutoCalibrateGpuYuvRoutine() | 950-1500 | 校准例程 | 禁用 |
| QuestStreamVideoPlayer.EnsureQuestVulkanStreamTexture() | 1630-1680 | useJavaDecoderColorInfo | 分支 |
| QuestStreamVideoPlayer.ApplyQuestVulkanYcbcrOverride() | 1700-1730 | [DEAD] YCbCr | 不执行 |
| QuestStreamVideoPlayer.ApplyQuestVulkanManualYuvOverrideOnly() | 1732-1750 | ManualYUV | 执行 |
| QuestVulkanYcbcrOverrideApplier.ApplyYcbcrAndManual() | 全部 | 两层调用 | 有条件 |
| QuestVulkanYcbcrOverrideApplier.ApplyManualOnly() | 全部 | 手动仅 | 执行 |

