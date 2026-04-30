# AV1视频解码帧率问题修复

## 问题诊断

从Quest日志确认的问题：
```
Av1StreamingDecoder: Stats: 
- Decoded=18.3/s (解码器输出)
- Enqueued=16.7/s (入队)
- Dequeued=5.1/s (Unity取出) ⚠️ 问题所在
```

## 根本原因

1. **帧排空限制**：原代码每次Update只排空3帧（`maxHardwareFrameDrainPerTick=3`）
2. **反射调用慢**：`TryUpdateTextureFromHardwareBufferFrame`中的反射调用拖慢主线程
3. **Unity主循环慢**：由于处理开销大，Unity主循环降到5-6 FPS
4. **队列积压**：无法及时取帧，导致Java端ImageReader队列满，新帧被丢弃

## 已修改内容

### 文件：`Assets/Scripts/Video/QuestStreamVideoPlayer.cs`

**修改位置**：第837-880行的`PullFramesLoop()`方法

**改动前**：
```csharp
int drainAttempts = Mathf.Max(1, maxHardwareFrameDrainPerTick);
for (int drainIndex = 0; drainIndex < drainAttempts; drainIndex++)
{
    // 最多排空3帧
}
```

**改动后**：
```csharp
int drainedCount = 0;
const int maxDrainPerFrame = 60;
while (drainedCount < maxDrainPerFrame)
{
    // 尽可能排空所有可用帧，最多60帧
    drainedCount++;
}
```

**效果**：
- 每次Update尽可能排空所有可用帧
- 即使Unity主循环只有5-6 FPS，也能处理更多帧
- 添加了调试日志，当排空多帧时会输出提示

## 部署步骤

1. **重新构建APK**：
   - 在Unity中：File → Build Settings → Build
   - 或使用快捷键 Ctrl+Shift+B

2. **部署到Quest**：
   ```bash
   adb install -r path/to/your.apk
   ```

3. **验证修复**：
   运行应用后，通过adb查看日志：
   ```bash
   adb logcat | grep "Av1StreamingDecoder: Stats"
   ```
   
   期望看到：
   - `Dequeued` 接近 `Decoded` 的值（15-20/s）
   - 不再是5-6/s

## 预期效果

- ✅ Dequeued从5fps提升到15-20fps
- ✅ 视频输出帧率从6-10fps提升到接近服务端发送帧率
- ✅ 减少帧丢失和队列积压

## 如果问题仍存在

如果部署后Dequeued仍然很低，说明Unity主循环本身有性能问题，需要：

1. **检查Unity帧率**：在DebugTextManager中启用`enableJavaVideoStats = true`
2. **优化反射调用**：考虑缓存反射结果或使用更快的JNI调用方式
3. **检查其他性能瓶颈**：使用Unity Profiler分析主线程耗时
