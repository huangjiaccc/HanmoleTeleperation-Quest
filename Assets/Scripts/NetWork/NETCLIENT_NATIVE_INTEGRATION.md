# NetClient Native视频播放器集成说明

## 修改总结

已将`NetClient.cs`修改为支持`GStreamerAV1PlayerNative`的动态端口配置。

## 修改内容

### 1. 添加了私有字段

```csharp
private GStreamerAV1PlayerNative? gstreamerAV1PlayerNative; // Native视频播放器
```

### 2. 修改了`ReloadUdpManagers()`方法

#### 查找Native播放器

```csharp
gstreamerAV1PlayerNative = FindObjectOfType<GStreamerAV1PlayerNative>(); // 查找Native播放器
```

#### 调整Unity UDP桥接逻辑

```csharp
?????Java????UDP? =
    gstreamerAv1Player != null ||
    gstreamerAV1PlayerNative == null && // Native播放器不需要Unity UDP桥接
    (questStreamVideoPlayer != null && questStreamVideoPlayer.?????);
```

**逻辑说明**：
- 如果使用`GStreamerAV1PlayerNative`（Native播放器），**不需要**Unity UDP桥接
- Native播放器内部已经有UDP接收器，直接在C层处理

#### 调用新的配置方法

```csharp
// 配置Native视频播放器端口
ConfigureNativeVideoPlayer(localVideoPort, remoteVideoPort);
```

#### 增强日志输出

```csharp
Debug.Log($"[NetClient] UDP Managers Reloaded. isLAN={islan}\n" +
          $"JavaVideoUdpOnly: {?????Java????UDP?}\n" +
          $"Native Player: {(gstreamerAV1PlayerNative != null ? "Active" : "None")}\n" +
          $"Server IP: {RuntimeServerConfig.serverIp}\n" +
          ...
```

### 3. 新增了`ConfigureNativeVideoPlayer()`方法

```csharp
/// <summary>
/// 配置Native视频播放器的端口（GStreamerAV1PlayerNative）
/// </summary>
private void ConfigureNativeVideoPlayer(int localPort, int remotePort)
{
    gstreamerAV1PlayerNative = FindObjectOfType<GStreamerAV1PlayerNative>();

    if (gstreamerAV1PlayerNative == null)
    {
        // Native播放器不在场景中，跳过
        return;
    }

    try
    {
        // 使用ReloadWithNewPorts方法动态切换端口
        gstreamerAV1PlayerNative.ReloadWithNewPorts(
            newRemotePort: remotePort,
            newLocalPort: localPort,
            newRemoteIP: RuntimeServerConfig.serverIp
        );

        Debug.Log($"[NetClient] GStreamerAV1PlayerNative 已配置端口: " +
                 $"本地监听={localPort}, 远程发送={remotePort}, 服务端IP={RuntimeServerConfig.serverIp}");
    }
    catch (Exception ex)
    {
        Debug.LogError($"[NetClient] 配置Native视频播放器失败: {ex.Message}\n{ex.StackTrace}");
    }
}
```

---

## 工作流程

### 初始化流程

```
NetClient.Start()
    ↓
NetWorkStart()
    ↓
ReloadUdpManagers()
    ↓
查找GStreamerAV1PlayerNative
    ↓
如果找到 → ConfigureNativeVideoPlayer()
    ↓
调用 gstreamerAV1PlayerNative.ReloadWithNewPorts()
    ↓
Native层重新初始化UDP socket和GStreamer
```

### 运行时切换流程

```
用户修改服务器配置
    ↓
调用 NetClient.ReloadUdpManagers()
    ↓
重新创建UDP Managers（Data/Audio）
    ↓
ConfigureNativeVideoPlayer()
    ↓
Native层销毁旧socket，创建新socket
    ↓
使用新端口重新连接
```

---

## 使用方式

### 场景配置

#### 方式1：使用GStreamerAV1PlayerNative（推荐）

1. 在场景中添加GameObject
2. 添加`GStreamerAV1PlayerNative`组件
3. 配置Inspector参数（初始端口可以随意设置，会被`ReloadUdpManagers`覆盖）
4. `NetClient`会自动找到它并配置端口

#### 方式2：使用旧的播放器

1. 使用`QuestStreamVideoPlayer`或`GStreamerAV1PlayerOBU`
2. `NetClient`会自动判断并使用Unity UDP桥接

#### 方式3：混合使用

- 可以同时存在多个播放器
- `NetClient`会分别配置它们
- Native播放器优先，不需要Unity UDP桥接

### 端口分配逻辑

```csharp
// LAN模式（islan = true）
localVideoPort = RuntimeServerConfig.serverVideoPort;   // 服务端端口作为本地端口
remoteVideoPort = RuntimeServerConfig.clientVideoPort;  // 客户端端口作为远程端口

// WAN模式（islan = false）
localVideoPort = RuntimeServerConfig.clientVideoPort;   // 客户端端口作为本地端口
remoteVideoPort = RuntimeServerConfig.serverVideoPort;  // 服务端端口作为远程端口
```

**对于Native播放器**：
- `localPort`: 本地监听端口（接收视频数据）
- `remotePort`: 远程端口（发送心跳包的目标端口）
- `remoteIP`: 服务端IP地址

---

## 日志示例

### 成功配置的日志

```
[NetClient] Reloading UdpManagers with new server config...
192.168.1.100
[NetClient] QuestStreamVideoPlayer 未在当前场景中找到，跳过视频端口配置。
[GStreamerAV1PlayerNative] Reloading with new ports: local=5000, remote=5000
[UnityAV1VideoNative] av1_video_stop: stopping video reception and decoding
[GStreamer] Stopping GStreamer pipeline...
[UnityAV1VideoNative] av1_video_destroy: cleaning up resources
[UnityAV1VideoNative] av1_video_init: initialization complete
  remote_ip: 192.168.1.100
  remote_port: 5000
  local_port: 5000
[UnityAV1VideoNative] av1_video_start: starting video reception and decoding
[UdpReceiver] UDP receiver listening on port 5000
[HeartbeatSender] Heartbeat send thread started
[UnityAV1VideoNative] av1_video_start: started successfully
  UDP receiver: listening on port 5000
  Heartbeat sender: sending to 192.168.1.100:5000 every 3000ms
[GStreamerAV1PlayerNative] Successfully reloaded with new ports: local=5000, remote=5000, ip=192.168.1.100
[NetClient] GStreamerAV1PlayerNative 已配置端口: 本地监听=5000, 远程发送=5000, 服务端IP=192.168.1.100
[NetClient] UDP Managers Reloaded. isLAN=False
JavaVideoUdpOnly: False
Native Player: Active
Server IP: 192.168.1.100
Local Ports: Video(5000), Audio(5002), Data(5001)
Remote Ports: Video(5000), Audio(5002), Data(5001)
```

### 找不到播放器的日志

```
[NetClient] Reloading UdpManagers with new server config...
[NetClient] UDP Managers Reloaded. isLAN=False
JavaVideoUdpOnly: False
Native Player: None
Server IP: 192.168.1.100
...
```

---

## 优势

### 1. 自动端口同步

每次调用`ReloadUdpManagers()`，Native播放器都会自动获取最新的端口配置。

### 2. 统一管理

所有网络端口（视频、音频、数据）都在`NetClient`中统一管理和切换。

### 3. 灵活性

- 支持运行时动态切换服务器
- 支持LAN/WAN模式切换
- 自动处理旧连接的清理

### 4. 向后兼容

- 不影响现有的`QuestStreamVideoPlayer`和`GStreamerAV1PlayerOBU`
- 可以共存，`NetClient`会自动判断

---

## 故障排查

### 1. Native播放器找不到

**症状**：日志显示`Native Player: None`

**原因**：
- 场景中没有`GStreamerAV1PlayerNative`组件
- GameObject被禁用

**解决**：
- 确认场景中有GameObject附加了`GStreamerAV1PlayerNative`组件
- 确认GameObject是激活状态

### 2. 端口配置失败

**症状**：日志显示`配置Native视频播放器失败`

**原因**：
- 端口被占用
- Native层初始化失败
- 权限问题

**解决**：
- 检查adb logcat查看Native层日志
- 确认端口未被其他程序占用
- 尝试更换端口

### 3. 视频无法接收

**症状**：Native播放器配置成功但收不到视频

**原因**：
- 服务端未启动
- 网络不通
- 防火墙阻止
- 端口配置错误

**解决**：
- ping服务端IP验证网络连通性
- 使用Wireshark抓包检查UDP数据包
- 检查服务端是否在正确的端口发送数据
- 对比`localVideoPort`和服务端发送端口是否一致

---

## 测试建议

### 基本测试

1. 启动应用，确认初始端口配置成功
2. 修改`RuntimeServerConfig`中的端口
3. 调用`ReloadUdpManagers()`
4. 验证新端口是否生效

### 网络切换测试

1. 从WAN模式切换到LAN模式（或相反）
2. 验证端口是否正确交换
3. 验证视频是否正常接收

### 多播放器测试

1. 同时添加`GStreamerAV1PlayerNative`和`QuestStreamVideoPlayer`
2. 验证两者都能正确配置端口
3. 验证不会相互干扰

---

## 相关文件

- **NetClient.cs**: 网络管理器（已修改）
- **GStreamerAV1PlayerNative.cs**: Native视频播放器
- **DYNAMIC_PORT_SWITCHING_GUIDE.md**: 端口切换详细指南
- **README_NATIVE_INTEGRATION.md**: Native集成总体说明
