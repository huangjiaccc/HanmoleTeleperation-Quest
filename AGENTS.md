# AGENTS.md

## 项目定位
- 这是一个 Unity 6 Quest 遥操作项目，当前编辑器版本是 `6000.2.10f1`，渲染管线是 URP，XR 侧依赖 Meta XR SDK 和 OpenXR。
- 主运行场景是 `Assets/Scenes/QuestMain.unity`。`AndroidMain*.unity`、`QuestMain 1.unity`、`QuestMain-curvedmesh.unity`、`Assets/_Recovery/*.unity` 更像备份或实验场景，除非用户明确要求，不要把它们当成主改动目标。
- 这个工作区不是标准 Git 仓库，目录里存在 `.plastic`。不要假设 `git` 可用，也不要把 Git 工作流写进解决方案。

## 首先阅读这些路径
- `Packages/manifest.json`
- `ProjectSettings/ProjectVersion.txt`
- `ProjectSettings/EditorBuildSettings.asset`
- `ProjectSettings/ProjectSettings.asset`
- `Assets/Scripts/**`
- `Assets/Scripts/NetWork/NETCLIENT_NATIVE_INTEGRATION.md`
- `Assets/Plugins/Android/**`
- `VulkanExternalTexture/**`

## 关键源码区域
- `Assets/Scripts/Config`
  - `RuntimeServerConfig.cs` 是运行时网络配置单例。
  - `ServerConfigSO.cs` 是默认配置 ScriptableObject。
- `Assets/Scripts/NetWork`
  - `NetClient.cs` 管理 UDP、心跳、服务器列表请求、运行时端口重载。
  - `UdpManager.cs` 是 data/audio UDP 基础设施。
  - `ByteArrayPool.cs` 是高频网络路径的共享缓冲池。
- `Assets/Scripts/DataManager.cs`
  - 100Hz 发送头手姿态、摇杆、命令差量，是客户端上行状态的中心。
- `Assets/Scripts/Audio`
  - `AudioStreamPlayer.cs` 负责 Opus 解码播放。
  - `RecordingManager.cs` 负责麦克风采集和 Opus 编码发送。
- `Assets/Scripts/Video`
  - `QuestStreamVideoPlayer.cs` 是 Quest 视频接收/解码/显示主入口。
  - `VideoColorCalibrator.cs` 管理 GPU YUV 校准和显示矩阵。
  - `QuestVulkanExt.cs` 是 C# 到原生 Vulkan 插件的 P/Invoke 边界。
- `Assets/Plugins/Android/src/main/java/com/example/questdecoder`
  - Java 解码器和 HardwareBuffer 桥接代码都在这里。
- `VulkanExternalTexture/cpp`
  - `VulkanExternalTexture.cpp` 是 Android Vulkan 原生插件源码。
  - `build_questvulkanext.bat` 会编译并复制 `libunity_vulkan_hwbuffer.so` 到 Unity 插件目录。
- `Assets/Scripts/JointURDF`
  - `URDFJointMapper.cs` 和 `RobotJointController.cs` 是机器人 20 关节映射与应用逻辑。

## 重要事实
- 运行时网络配置的真实加载顺序是：
  1. 首次运行时把 `Assets/StreamingAssets/server_config.json` 复制到 `Application.persistentDataPath/server_config.json`
  2. 之后优先读取 persistentDataPath 中的配置
  3. `RuntimeServerConfig.Save()` 会立刻触发 `NetClient.instance.ReloadUdpManagers()`
- 这意味着：
  - 只改 `Assets/StreamingAssets/server_config.json` 只会影响“首次运行”或清空 persistentDataPath 后的运行。
  - 如果要改默认值，通常要同时检查 `Assets/StreamingAssets/server_config.json` 和 `ServerConfigSO`。
- 视频 UDP 不再由 Unity 侧 `UdpManager` 桥接。当前设计是 `QuestStreamVideoPlayer` 直接绑定视频端口，`NetClient.ReloadUdpManagers()` 只负责 data/audio UDP，并把视频端口配置推给 `QuestStreamVideoPlayer.ConfigureNetwork(...)`。
- `QuestStreamVideoPlayer` 和 `VideoColorCalibrator` 默认都使用 `ProductionFixed` 模式。很多 Inspector 值会在 `ApplyProductionDefaults()` 中被代码覆盖。
  - 想改生产默认行为，优先改代码里的 `ApplyProductionDefaults()`
  - 只有明确要临时调试时才切到 `DebugInspector`
- `DecoderFlavor` 由 `DataManager.videoDecodeMode` 和 `UIManager` 切换控制。改解码器枚举或行为时，必须同步检查 C#、Java 解码器类和 UI 切换逻辑。
- Java/Native HardwareBuffer 通路依赖 `libunity_vulkan_hwbuffer.so`。改原生插件接口时，要同步检查：
  - `Assets/Scripts/Video/QuestVulkanExt.cs`
  - `Assets/Plugins/Android/src/main/java/com/example/questdecoder/HardwareBufferNativeBridge.java`
  - `Assets/Plugins/Android/src/main/java/com/example/questdecoder/VulkanHardwareBufferBridge.java`
  - `VulkanExternalTexture/cpp/VulkanExternalTexture.cpp`
- `URDFJointMapper` 依赖固定的 20 个关节名和顺序。改 joint 协议时，必须一起检查 `RobotJointStateData`、`RobotJointController`、`URDFJointMapper`。

## 修改约定
- 优先改源码，不要改 Unity 自动生成文件。
  - 不要手改 `Assembly-CSharp.csproj`、`Assembly-CSharp-Editor.csproj`、`*.csproj`、`TempAssembly.dll`
- 修改代码时保存为 UTF-8 编码，避免中文乱码。
- 除非用户明确要求，不要编辑这些目录或把它们当成问题根因：
  - `Library/`
  - `Temp/`
  - `Logs/`
  - `obj/`
  - `UserSettings/`
  - `HybridCLRData/`
  - `VulkanExternalTexture/Build/`
  - `Assets/Plugins/Android/.gradle/`
  - `Assets/Plugins/Android/.idea/`
  - `Assets/_Recovery/`
  - `Assets/*rar`
- 场景和 prefab 是大 YAML 文件。能在代码层解决时，优先改代码；确实要改场景时，只做定点改动，不要顺手重排或大面积重序列化。
- 这个项目已有明显的性能敏感路径。改网络、音频、视频代码时：
  - 继续复用 `ByteArrayPool`
  - 谨慎引入字符串分配、LINQ、每帧 new
  - 保持线程退出路径和 `OnDisable` / `OnDestroy` / `OnApplicationPause` / `OnApplicationQuit` 对称
- 运行时/Editor 日志优先沿用现有模式：很多文件使用 `Debug = AppLog`。新代码除非有明确原因，优先跟随这个约定。
- 平台分支要非常谨慎。项目里大量使用 `#if IS_ANDROID` 和 `#if !IS_ANDROID`，而 Player Settings 里的 define 也有非标准写法。新增条件编译前，先确认当前实际符号，不要想当然。
- 当前 `ProjectSettings/ProjectSettings.asset` 里的 Android define 明确包含 `!IS_ANDROID`，不要凭经验推断 Android 构建一定会走 `#if IS_ANDROID` 分支。
- 不要删除、重建或随意移动 `Assets/**` 下已有资源的 `.meta` 文件；Unity 通过 GUID 绑定 scene、prefab、材质、脚本和资源引用。
- 改已挂到 scene/prefab 上的 `MonoBehaviour` / `ScriptableObject` 类型名、命名空间或 `[SerializeField]` / `public` 字段名时，先确认序列化迁移策略；必要时使用 `FormerlySerializedAs`，不要把这类修改当成普通重构。
- 供应商内容与项目代码分开看待：
  - `Assets/Packet/com.unity.robotics.urdf-importer/**`
  - `Assets/Samples/**`
  - `Assets/StarterSamples/**`
  - `Assets/TextMesh Pro/**`
  - 除非任务明确要求，否则不要把第三方样例或包代码当成首选修改点。

## 与原生视频链路相关的特殊规则
- 如果你改 `QuestStreamVideoPlayer` 的网络端口、心跳、解码模式或 HardwareBuffer 路径，必须同时检查 Java 解码器是否还兼容。
- 如果你改 `QuestVulkanExt.cs` 的 P/Invoke 签名，必须保证 native 导出符号名保持一致，否则运行时只会在 Quest 上失败。
- 不要在 `VulkanHardwareBufferBridge.java` 里再加一份 `System.loadLibrary("unity_vulkan_hwbuffer")`。当前设计假设 Unity 原生插件系统负责加载它。
- 原生插件的构建产物目标路径应保持为：
  - `Assets/Plugins/Android/arm64-v8a/libunity_vulkan_hwbuffer.so`

## 与 UI / 输入相关的特殊规则
- `UIManager.cs` 很大，承担了登录、机器人列表、TTS、Action、视频面板、调试状态、移动端输入等多种职责。小改动优先局部收敛，不要轻易做大重构。
- `GestureAndControllerModeManager.cs` 与 `DataManager`、`UIManager`、`OVRInputController` 强耦合。改手势/透视/模式切换时，至少一起看这几个文件。
- `RecordingManager` 只有在服务端状态是 `AudioMode.DIRECT` 时才开始录音。改音频模式逻辑时，要确认不会误触发录音生命周期。

## 验证方式
- C# 编译检查可尝试：
  - `dotnet build .\\Assembly-CSharp.csproj -nologo`
- 但这一步依赖 Unity 生成的 `.csproj`、`Library/ScriptAssemblies` 和本机 Unity/VS 安装路径；如果它无明确编译报错却退出失败，不要去“修”生成的 `.csproj`，先说明这是环境/生成物依赖问题。
- `dotnet build` 只覆盖 C# 编译；它发现不了 Missing Script、序列化字段丢失、scene/prefab 引用断裂、`.meta` GUID 变化或插件导入设置错误。
- 如果任务改了脚本类型名、命名空间、序列化字段、scene/prefab、`.meta` 或 Android 插件导入设置，必须额外做 Unity 资源级一致性检查，不要只用编译通过作为结论。
- 原生 Vulkan 插件构建入口是：
  - `.\VulkanExternalTexture\build_questvulkanext.bat`
- 改 Android Java 插件后，优先做一致性检查：
  - Java 方法名/参数
  - C# `AndroidJavaObject.Call(...)` 调用点
  - C# 与 native 的桥接签名
- 如果需要看版本控制状态，优先使用：
  - `cm status --short --private`
- 不要假设 `git status`、`git diff` 或 Git 分支信息可用。

## 现成工具
- `Assets/Editor/OpenPersistentDataPathEditor.cs`
  - 方便直接打开 `Application.persistentDataPath`
- `Assets/Editor/PluginPlatformFixer.cs`
  - 处理插件平台兼容设置
- `Assets/Editor/FixMaterialKeywords.cs`
- `Assets/Editor/ImprovedMaterialFixer.cs`
  - 处理 URP/材质关键字问题
- `Assets/Editor/SetAllArticulationImmovable.cs`
  - 批量修正 `ArticulationBody.immovable`

## 默认工作策略
- 先判断任务属于哪条主线：
  - 网络/端口/心跳
  - Quest 视频解码/Vulkan
  - 音频采集或播放
  - UI/手势/模式切换
  - 机器人关节/URDF
- 只读关键路径后再动手，不要被 `Library`、备份场景、恢复文件和第三方样例分散注意力。
- 如果任务会改场景、Java、C#、C++ 中的多个层级，优先把接口边界梳理清楚，再同步修改，避免只改一层导致 Quest 端运行时崩溃。
