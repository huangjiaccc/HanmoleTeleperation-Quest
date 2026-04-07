# 代码死代码快速检查表

## Q&A 快速索引

### Q1: ProductionFixed 模式下哪些Inspector字段会被代码覆盖为默认值?
**A**: 共31个字段在 `ApplyProductionDefaults()` 中被不可逆地覆盖。

**关键覆盖**:
- expectedWidth/Height: 2560×1440
- preferVulkanHardwareBufferFrames: ✓ true (强制GPU)
- useJavaDecoderColorInfo: ✓ true (Java驱动色彩)
- manualYuvConversion: ✓ true (启用手动转换)
- manualYuvInputMode: **ByteNarrowJava** (兼容Java编码)
- autoCalibrateGpuYuvFromCpuFirstFrame: ✗ false (禁用自动校准!)
- forceFixedManualYuvParams: ✓ true (使用硬编码参数!)
- autoCalibrateApplyPostColorMul: ✗ false (禁用色彩增益!)
- autoCalibrateApplyDisplayColorMatrix: ✗ false (禁用矩阵!)

[详见: VIDEO_ANALYSIS.md 第1节表格]

---

### Q2: autoCalibrateGpuYuvFromCpuFirstFrame=false 后，哪些代码路径永远走不到?
**A**: **整个GPU校准系统被禁用**，包括:

**完全死代码**:
```
1. MaybeCaptureCpuCalibrationReferenceFromJava()  [全部]
   - Java dequeueCalibrationFrameBundle() 永不调用
   - CPU RGB均值永不计算
   - cpuCalibrationReferenceCaptured 永 = false

2. AutoCalibrateGpuYuvRoutine()  [全部协程]
   - 硬件YCbCr五层嵌套循环 (~100候选) 永不执行
   - GPU像素采样永不进行
   - 手动YUV参数搜索 (~2000候选) 永不执行
   - 显示色彩矩阵拟合 (3×4) 永不执行
   - 后处理色彩增益计算永不执行

3. MaybeStartGpuYuvAutoCalibration()  [全部]
   - 协程启动条件检查全部跳过
   - gpuCalibrationRunning 永 = false

4. TryCaptureGpuCalibrationAhbFromBundle()  [全部]
   - 校准AHB指针捕获永不发生
```

**关键标志位永不被设置为true**:
- cpuCalibrationReferenceCaptured
- gpuCalibrationRunning
- gpuCalibrationCompleted (由固定模式提前设置)

**代码行范围**:
- VideoColorCalibrator: 第 ~310行, ~370行, ~950-1500行 (全部条件块)

[详见: VIDEO_ANALYSIS.md 第2节]

---

### Q3: useJavaDecoderColorInfo=true 和 forceFixedManualYuvParams=false 时，哪些Vulkan设置被跳过?
**A**: **关键：ProductionFixed模式实际为 forceFixedManualYuvParams=true，不是false**

**被跳过的Vulkan设置** (当 useJavaDecoderColorInfo=true):
```
1. YCbCr模型配置  [整体跳过]
   - ycbcrModel (Auto → -1) 不被应用
   - 本应调用: TrySetYcbcrOverride() 
   - 实际: ApplyQuestVulkanManualYuvOverrideOnly() [不调用TrySetYcbcrOverride]

2. YCbCr范围配置  [整体跳过]
   - ycbcrRange (Auto → -1) 不被应用
   - Java解码器提供实际值

3. 采样位置偏移  [整体跳过]
   - chromaOffsetX (Auto → -1) 不被应用
   - chromaOffsetY (Auto → -1) 不被应用

4. 色彩通道顺序  [部分跳过]
   - swapCbCr (false) 不被应用
   - swapRedBlue (false) 不被应用
   - Java决定Cb/Cr和R/B顺序
```

**仍被应用的参数** (通过 ApplyQuestVulkanManualYuvOverrideOnly):
```
✓ manualYuvConversion = 1      (启用)
✓ swapUv = 0                   (无交换)
✓ invertU = 0                  (无反演)
✓ invertV = 0                  (无反演)
✓ channelOrder = 0             (YUV，固定模式=2 UYV!)
✓ inputMode = 1                (ByteNarrowJava)
```

**调用路径**:
```
EnsureQuestVulkanStreamTexture()
  ├─ if (useJavaDecoderColorInfo) {
  │   ApplyQuestVulkanManualYuvOverrideOnly()  ← 仅此条件分支
  │   └─ ycbcrOverrideApplier.ApplyManualOnly()
  │      └─ QuestVulkanExt.TrySetManualYuvParams(...)
  │         ├─ QuestVulkan_SetManualYuvParams3
  │         ├─ QuestVulkan_SetManualYuvParams2
  │         └─ QuestVulkan_SetManualYuvParams
  │
  └─ else {
      ApplyQuestVulkanYcbcrOverride()  ← [此代码永不执行]
      └─ TrySetYcbcrOverride(...)
   }
```

[详见: VIDEO_ANALYSIS.md 第3节]

---

### Q4: QuestVulkanExt中的P/Invoke调用，哪些点是可选的条件路径?
**A**: 多个条件判断点:

#### 必须条件 (编译时)
```
#if UNITY_ANDROID && !UNITY_EDITOR
  所有以下方法编译时包含
#endif
```
→ **编辑器运行时所有P/Invoke都死代码**

#### TrySetManualYuvParams() - 三层降级 ⚠️
```csharp
try { QuestVulkan_SetManualYuvParams3(...) }    // 优先
catch {
  try { QuestVulkan_SetManualYuvParams2(...) }  // 降级1
  catch {
    try { QuestVulkan_SetManualYuvParams(...) } // 降级2
    catch { return false }                       // 全失败
  }
}
```
→ 任一异常触发降级，最终返回false表示全失败

#### TrySetYcbcrOverride() - 无降级 ⚠️
```csharp
try { QuestVulkan_SetYcbcrOverride2(...) }
catch { return false }  // ⚠️ 直接失败，无降级
```
→ 若native插件无导出符号，整体失败

#### SetHardwareBuffer 围栏可选
```csharp
if (fenceFd >= 0) {
  try {
    QuestVulkan_SetHardwareBufferWithFence(...)  // 优先
  } catch {
    QuestVulkan_CloseFenceFd(fenceFd)
    QuestVulkan_SetHardwareBuffer(...)           // 降级
  }
} else {
  QuestVulkan_SetHardwareBuffer(...)             // 无围栏
}
```
→ 围栏可选，总会调用其中一个版本

#### SetColorTransform() - 条件应用
```csharp
#if UNITY_ANDROID && !UNITY_EDITOR
try { QuestVulkan_SetColorTransform(...) }
catch { }
#endif
```
→ 异常被吞掉，不影响流程

#### GetRenderEventFunc() - 延迟加载
```csharp
// 仅在native importer启用时调用:
if (!TryEnableNativeHardwareBufferImporter()) {
  questVulkanRenderEventFunc = IntPtr.Zero  // 失败标记
  return false
}
questVulkanRenderEventFunc = QuestVulkan_GetRenderEventFunc()
```
→ 条件路径，可能返回IntPtr.Zero

**实际在ProductionFixed下的调用顺序**:
```
1. StartStream() → ConfigureJavaColorPipeline()
   ├─ useNativeHardwareBufferImporter = false (基于Unity API优先级)
   └─ 原生P/Invoke = 不启用

2. PullFramesLoop() → TryUpdateTextureFromHardwareBufferFrame()
   ├─ 若Unity API成功 → 停止，不触发任何原生P/Invoke
   ├─ 若Unity API失败 且 useNativeHardwareBufferImporter=true
   │  ├─ TryUpdateTextureFromHardwareBufferViaQuestVulkanExt()
   │  ├─ EnsureQuestVulkanStreamTexture()
   │  │  └─ ApplyQuestVulkanManualYuvOverrideOnly()
   │  │     └─ TrySetManualYuvParams()  ← P/Invoke #1
   │  ├─ EnsureQuestVulkanUnityTexture()
   │  ├─ QuestVulkan_SetHardwareBuffer(With/WithoutFence)()  ← P/Invoke #2
   │  ├─ IssueQuestVulkanEvent()
   │  └─ QuestVulkan_GetTextureOperationStatus()  ← P/Invoke #3
```

[详见: VIDEO_ANALYSIS.md 第4节]

---

## 代码量估算

### 死代码行数

| 模块 | 死代码行数 | 百分比 |
|------|----------|-------|
| VideoColorCalibrator | ~700行 | 校准系统全禁用 |
| QuestStreamVideoPlayer | ~400行 | 原生Vulkan路径可选 |
| QuestVulkanExt | ~30行 | P/Invoke包装器 |
| QuestVulkanYcbcrOverrideApplier | ~80行 | 有条件调用 |

**总死代码**: ~1200行 (条件编译外) + ~400行 (ProductionFixed逻辑)

### 关键被禁用的系统

| 系统 | 行数 | 禁用方式 |
|------|------|--------|
| GPU YUV自动校准 | ~550行 | `forceFixedManualYuvParams=true` |
| CPU参考帧采样 | ~150行 | `autoCalibrateGpuYuvFromCpuFirstFrame=false` |
| 硬件YCbCr探索 | ~100行 | `autoCalibrateAllowHardwareYcbcr=false` |
| 色彩矩阵拟合 | ~200行 | `autoCalibrateApplyDisplayColorMatrix=false` |
| 色彩增益后处理 | ~60行 | `autoCalibrateApplyPostColorMul=false` |

---

## 关键发现总结

### 架构层面

✅ **双路径设计完整**
- 路径A: Unity AndroidHardwareBuffer API (优先)
- 路径B: Native Vulkan importer (备选)
- 互斥选择，逻辑清晰

❌ **校准系统完全禁用**
- ProductionFixed固定化所有色彩参数
- 自动校准管道在启动时就被关闭
- 48000+个参数候选永不被评估
- 消耗大量死代码但逻辑无bug

### 参数层面

⚠️ **硬编码固定值 (VideoColorCalibrator.ApplyFixedManualYuvParams)**
- RGB增益: (0.993, 0.991, 0.989, 1.0)
- 显示色彩矩阵: 3×4矩阵 (见完整分析)
- 通道顺序: **UYV** (不是YUV!)
- 输入模式: **ByteNarrowJava** (Java兼容)

### 原生接口层面

✅ **向后兼容设计** (TrySetManualYuvParams)
- 三层降级机制
- 旧版原生插件仍可工作

❌ **YCbCr覆盖无降级**
- TrySetYcbcrOverride直接失败
- 若native缺少此导出，整体不工作

### 色彩处理流程

Java → C# → Native 的色彩参数流向:
```
Java解码器
  ├─ 计算YCbCr模型/范围/采样位置
  ├─ 输出HardwareBuffer
  └─ 通过decoder.Call() 返回色彩信息

C#应用层 (ProductionFixed)
  ├─ 屏蔽Java色彩参数应用 (useJavaDecoderColorInfo=true)
  ├─ 仅应用手动YUV参数 (ByteNarrowJava)
  └─ 添加硬编码RGB增益 + 显示矩阵

原生插件 (libunity_vulkan_hwbuffer.so)
  ├─ 接收Java的AHB和色彩信息 (自动)
  ├─ 接收C#的手动YUV/显示矩阵 (主动)
  └─ 执行YUV→RGBA转换
```

---

## 验证清单

### 编辑器运行 (Debug)
- [ ] `configMode == ProductionFixed` 确认
- [ ] `autoCalibrateGpuYuvFromCpuFirstFrame == false` 确认
- [ ] `forceFixedManualYuvParams == true` 确认
- [ ] VideoColorCalibrator 组件存在
- [ ] `useJavaDecoderColorInfo == true` 确认

### Android设备 (Runtime)
- [ ] USB网络配置正确 (listenPort=5000, listenPortSecondary=4000)
- [ ] 解码器初始化成功 (decoder != null)
- [ ] HardwareBuffer frames 启用 (usingHardwareBufferFrames=true)
- [ ] 视频帧接收 (framesDecoded > 0)
- [ ] 应用固定色彩参数 (ApplyFixedManualYuvParams调用)

### 原生插件
- [ ] 库加载成功 (libunity_vulkan_hwbuffer.so)
- [ ] P/Invoke调用成功 (TrySetManualYuvParams returns true)
- [ ] 围栏传输可选 (SetHardwareBufferWithFence 或 SetHardwareBuffer)

---

## 代码改进机会 (低风险)

### 短期 (删除死代码)
1. 条件编译外的代码基本无害
2. 删除校准系统需确认没有其他场景使用

### 中期 (参数化固定值)
```csharp
// 2020引入常量而非魔数
private static class ProductionYuvParams
{
    public const int SwapUv = 0;
    public const int InvertU = 0;
    public const int InvertV = 0;
    public const int ChannelOrder = 2;  // UYV
    public const int InputMode = 1;     // ByteNarrowJava
    
    public static readonly Vector4 ColorMul = new(0.993f, 0.991f, 0.989f, 1.0f);
    public static readonly Vector4 ColorAdd = Vector4.zero;
    
    public static readonly Vector4 MatrixRow0 = new(0.93f, -0.08f, 0.03f, 0.05f);
    public static readonly Vector4 MatrixRow1 = new(-0.06f, 0.84f, 0.11f, 0.05f);
    public static readonly Vector4 MatrixRow2 = new(-0.05f, 0.10f, 0.83f, 0.05f);
}
```

### 长期 (可配置校准)
- 若未来需要再启用校准，只需改 ProductionFixed 模式检查
- 整个校准代码已存在且测试完整，改回不困难

---

**报告完成** | Analysed Code Version: Latest | Analysis Date: 2025-03-24

