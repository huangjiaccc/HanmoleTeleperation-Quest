using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = AppLog;

public class DataManager : MonoBehaviour
{
    public static DataManager Instance;
    private const string VideoDecoderPrefKey = "VideoDecoderFlavor";
    [Header("Debug Settings")]
    [Tooltip("控制是否输出 Unity 的 Debug 日志")]
    public bool enableDebugLogs = true;

    [HideInInspector]
    public RobotState oldrobotState;
    [HideInInspector]
    public RobotState currobotState;
    [HideInInspector]
    public ACTION ACTION = new ACTION();
    [HideInInspector]
    public TTS TTS = new TTS();
    private HelmeData helmeData;
    private Transform headTrans;
    [HideInInspector]
    public Transform leftHandTrans;
    [HideInInspector]
    public Transform rightHandTrans;

    private Vector3 bodyOrigin;
    //[SerializeField]
    //[Tooltip("Offset from head anchor to pelvis (tracking space, meters)")]
    private Vector3 pelvisOffsetFromHead = new Vector3(0f, 0f, 0f);
    private int tracking;
    //=============================
    // 线程运行控制
    //=============================
    private Thread sendThread;
    private volatile bool running = false;     // 当前线程是否运行中
    private volatile bool shouldRun = false;   // 控制线程退出的标志位
    private readonly object dataLock = new object();
    private readonly AutoResetEvent sendThreadWakeSignal = new(false);

    //=============================
    // Unity 主线程采集的数据
    //=============================
    private Vector3 latestHeadPos;
    private Quaternion latestHeadRot;

    private Vector3 latestLeftPos;
    private Quaternion latestLeftRot;

    private Vector3 latestRightPos;
    private Quaternion latestRightRot;
    [HideInInspector]
    public float[] latestJoyL = new float[2];
    [HideInInspector]
    public float[] latestJoyR = new float[2];
    [HideInInspector]
    public float[] leftFingerPos = new float[6];
    [HideInInspector]
    public float[] rightFingerPos = new float[6];

    private string deviceId;

    private long initialUtcMs;    // 初始绝对时间戳（毫秒）
    private double startTicks;    // Stopwatch 起点
    private double tickToMs;      // tick → ms 转换系数


    private Vector3 _lastLeftPosition;
    private Quaternion _lastLeftRotation = Quaternion.identity;
    private Vector3 _lastRightPosition;
    private Quaternion _lastRightRotation = Quaternion.identity;
    private bool _wasLeftConnected;
    private bool _wasRightConnected;

    private readonly PartialCommand _sendPartialCommand = new PartialCommand();
    private static readonly UTF8Encoding SendJsonEncoding = new UTF8Encoding(false);
    [HideInInspector]
    public long lastTimestamp { get; private set; } // 最终稳定时间戳
    [HideInInspector]
    public float LeftHandAngle;
    [HideInInspector]
    public float RightHandAngle;
    //[HideInInspector]
    //public float robotheight;
    //[HideInInspector]
    //public int velocity;
    [HideInInspector]
    public int lastFrameID;

    [HideInInspector]
    public RobotStateMessage robotStateMessage;

    [HideInInspector]
    public TaskStateMessage TaskStateMessage;

    [HideInInspector]
    public RobotJointStateMessage robotJointStateMessage;
    private readonly Dictionary<string, float> _urdfJointPositions = new(StringComparer.Ordinal);
    [HideInInspector]
    public DecoderFlavor videoDecodeMode = DecoderFlavor.AV1;

    [HideInInspector]

    public bool AudioHaveChange = false;
    [HideInInspector]

    public bool VideoHaveChange = false;
    [HideInInspector] public bool RobotHaveChange = false;

    [HideInInspector] public bool G1HaveChange = false;
    [HideInInspector] public bool G2HaveChange = false;
    [HideInInspector] public bool G3HaveChange = false;

     private void Awake()
    {
        Instance = this;
        videoDecodeMode = LoadSavedVideoDecodeMode(DecoderFlavor.AV1);
        UnityEngine.Debug.unityLogger.logEnabled = enableDebugLogs;
        AppLog.SetAll(enableDebugLogs);
    }

    public bool SetVideoDecodeMode(DecoderFlavor flavor, bool persist = true)
    {
        if (videoDecodeMode == flavor)
        {
            return false;
        }

        videoDecodeMode = flavor;
        if (persist)
        {
            SaveVideoDecodeMode(flavor);
        }

        return true;
    }

    public static DecoderFlavor GetSavedVideoDecodeMode(DecoderFlavor fallback = DecoderFlavor.AV1)
    {
        return LoadSavedVideoDecodeMode(fallback);
    }

    private static DecoderFlavor LoadSavedVideoDecodeMode(DecoderFlavor fallback)
    {
        if (!PlayerPrefs.HasKey(VideoDecoderPrefKey))
        {
            return fallback;
        }

        int raw = PlayerPrefs.GetInt(VideoDecoderPrefKey, (int)fallback);
        if (!Enum.IsDefined(typeof(DecoderFlavor), raw))
        {
            return fallback;
        }

        return (DecoderFlavor)raw;
    }

    private static void SaveVideoDecodeMode(DecoderFlavor flavor)
    {
        PlayerPrefs.SetInt(VideoDecoderPrefKey, (int)flavor);
        PlayerPrefs.Save();
    }

    private void Start()
    {
        currobotState = new RobotState();
        oldrobotState = new RobotState();
        helmeData = new HelmeData();

#if !IS_ANDROID
        leftBoneMap = new Dictionary<OVRSkeleton.BoneId, Transform>();
        rightBoneMap = new Dictionary<OVRSkeleton.BoneId, Transform>();
        ovrRig = GameObject.Find("OVRCameraRig")?.GetComponent<OVRCameraRig>();
        if (OVRManager.instance != null)
        {
            OVRManager.instance.isInsightPassthroughEnabled = false;
        }

        if (leftSkeleton != null)
        {
            foreach (var b in leftSkeleton.Bones)
            {
                leftBoneMap[b.Id] = b.Transform;
            }
        }

        if (rightSkeleton != null)
        {
            foreach (var b in rightSkeleton.Bones)
            {
                rightBoneMap[b.Id] = b.Transform;
            }
        }

        if (ovrRig != null)
        {
            headTrans = ovrRig.centerEyeAnchor;
        }

        leftHandTrans = GetXRPalm(leftSkeleton);
        rightHandTrans = GetXRPalm(rightSkeleton);
#endif

        InitHelmeDataArrays();
        StartCoroutine(WaitTimeGetBodyPos());

        Debug.Log($"GetAndroidId:{DeviceIdUtil.GetAndroidId()}+++deviceId{SystemInfo.deviceUniqueIdentifier}");


        //xrFingerBones[0][3]  xrFingerBones[3][3] xrFingerBones[4][3]
        //j0 = rightBoneMap[xrFingerBones[0][3]]; j1 = rightBoneMap[xrFingerBones[3][3]]
        //j0 = rightBoneMap[xrFingerBones[0][3]]; j1 = rightBoneMap[xrFingerBones[4][3]]
        //j0 = leftBoneMap[xrFingerBones[0][3]]; j1 = leftBoneMap[xrFingerBones[3][3]]



    }

    //---------------------------------------------
    //   Unity 主线程采集输入（60 FPS）
    //---------------------------------------------
    float testtime;
    private void Update()
    {
        lock (dataLock)
        {
#if IS_ANDROID
            SampleAndroidInput();
#else
            SampleQuestInput();
             
            rightXRHand_ThumbTip = rightBoneMap[xrFingerBones[0][3]];
            rightXRHand_RingTip = rightBoneMap[xrFingerBones[3][3]];
            rightXRHand_LittleTip = rightBoneMap[xrFingerBones[4][3]];
            leftXRHand_ThumbTip = leftBoneMap[xrFingerBones[0][3]];
            leftXRHand_RingTip = leftBoneMap[xrFingerBones[3][3]];

#endif
            if (deviceId == null)
            {
                deviceId = SystemInfo.deviceUniqueIdentifier;
            }
            //if (AudioStreamPlayer.Instance != null)
            //{
            //    audioVolume = AudioStreamPlayer.Instance.volumeSlider.value;
            //}

        }
    }

    public void UpdateRobotState(RobotStateMessage message)
    {
#if !IS_ANDROID
        if (message.data.audio_mode != robotStateMessage.data.audio_mode ||
            message.data.robot_mode != robotStateMessage.data.robot_mode || message.data.hand_mode != robotStateMessage.data.hand_mode)
        {
            OVRInputController.instance.TriggerHapticFeedback();
            if(message.data.video_mode == (int)VideoMode.PERSPECTIVE) 
            {
                UIManager.Instance.SetVideoPanel(false);
                GestureAndControllerInputModeManager._instance.SetPassthroughEnabled(true);
            }
            else 
            {
                UIManager.Instance.SetVideoPanel(true);
                GestureAndControllerInputModeManager._instance.SetPassthroughEnabled(false);
            }

        }


        if (message.data.video_mode != robotStateMessage.data.video_mode)
        {
            OVRInputController.instance.TriggerHapticFeedback();
            if(message.data.video_mode == (int)VideoMode.PERSPECTIVE) 
            {
                UIManager.Instance.SetVideoPanel(false);
                GestureAndControllerInputModeManager._instance.SetPassthroughEnabled(true);
            }
            else 
            {
                UIManager.Instance.SetVideoPanel(true);
                GestureAndControllerInputModeManager._instance.SetPassthroughEnabled(false);
            }
            Debug.Log("latestRobotStateData.video_mode:" + robotStateMessage.data.video_mode);
        }


#endif
        if (message != null)
        {
            robotStateMessage = message;
            string notification = robotStateMessage.data.notifications?.GetLatestMessage();
            if (!string.IsNullOrWhiteSpace(notification))
            {
                DebugTextManager.Instance?.PushNotification(notification);
            }
        }
    }

    public void UpdateTaskState(TaskStateMessage message)
    {
        if (message != null) 
        {
            TaskStateMessage = message;
        }
    }

    public void UpdateRobotJointState(RobotJointStateMessage message)
    {
        if (message != null) 
        {
            robotJointStateMessage = message;
            bool appliedByName = robotJointStateMessage.data != null &&
                                 robotJointStateMessage.data.TryFillUrdfJointPositions(_urdfJointPositions) &&
                                 RobotJointController.instance != null;

            if (appliedByName)
            {
                RobotJointController.instance.ApplyNamedJoints(_urdfJointPositions);
                Debug.Log($"latestRobotJointStatePositions mapped={_urdfJointPositions.Count} ts={robotJointStateMessage.data.ts}");
                return;
            }

            float[] controllerJointRadians = robotJointStateMessage.data != null
                ? robotJointStateMessage.data.GetControllerJointRadians()
                : null;

            if (controllerJointRadians == null || controllerJointRadians.Length < 20 || RobotJointController.instance == null)
            {
                int rawRadianCount = robotJointStateMessage.data?.GetRawJointRadiansCount() ?? 0;
                int namedPositionCount = _urdfJointPositions.Count;
                Debug.LogWarning($"robot_joint_state parse failed or incomplete. controllerJointRadians={(controllerJointRadians?.Length ?? 0)} rawRadianCount={rawRadianCount} namedPositionCount={namedPositionCount}");
                return;
            }

            RobotJointController.instance.ApplyJoints(controllerJointRadians);
            Debug.Log($"latestRobotJointStateRadians count={controllerJointRadians.Length} ts={robotJointStateMessage.data.ts}");
        }
    }
    private IEnumerator WaitTimeGetBodyPos()
    {
        yield return new WaitForSeconds(0.5f);
        InitBodyOrigin();
    }

    // 初始化所有数组（避免GC）
    private void InitHelmeDataArrays()
    {
        helmeData.pos_head = new float[3];
        helmeData.quat_head = new float[4];

        helmeData.pos_left_hand = new float[6];
        helmeData.pos_left_arm = new float[3];
        helmeData.quat_left_arm = new float[4];

        helmeData.pos_right_hand = new float[6];
        helmeData.pos_right_arm = new float[3];
        helmeData.quat_right_arm = new float[4];

        helmeData.joystick_left = new float[2];
        helmeData.joystick_right = new float[2];
        helmeData.trigger = new Triggers();
        helmeData.button = new Buttons();

        //robotheight = 0;
        //velocity = 20;
        LeftHandAngle = 1;
        RightHandAngle = 1;

        // 初始化单调时钟
        // 1. 获取初始 UTC 时间戳（用于和服务器对齐）
        initialUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 2. 初始化单调时钟（不会漂移）
        startTicks = Stopwatch.GetTimestamp();
        tickToMs = 1000.0 / Stopwatch.Frequency;
    }


    //---------------------------------------------
    //   手动选择机器人 → 开启线程
    //---------------------------------------------
    public void OnSelectRobot(string targetdeviceId)
    {
        NetClient.instance._heartbeatMessage.target_device_id = targetdeviceId;
        StartSending();
    }

    //---------------------------------------------
    //   启动发送线程
    //---------------------------------------------
    public void StartSending()
    {
        if (running)
        {
            Debug.Log("  线程已经启动");
            return;
        }

        Debug.Log("  启动100Hz发送线程");

        shouldRun = true;
        sendThread = new Thread(ThreadSendLoop);
        sendThread.IsBackground = true;
        sendThread.Start();

    }

    //---------------------------------------------
    //   100Hz 发送线程（独立，不受Unity FPS影响）
    //---------------------------------------------
    private void ThreadSendLoop()
    {
        running = true;
        using var jsonStream = new MemoryStream(2048);

        try
        {
            while (shouldRun)
            {
                lock (dataLock)
                {
                    // 复制全部姿态与输入
                    helmeData.pos_head[0] = latestHeadPos.x;
                    helmeData.pos_head[1] = latestHeadPos.y;
                    helmeData.pos_head[2] = latestHeadPos.z;

                    helmeData.quat_head[0] = latestHeadRot.x;
                    helmeData.quat_head[1] = latestHeadRot.y;
                    helmeData.quat_head[2] = latestHeadRot.z;
                    helmeData.quat_head[3] = latestHeadRot.w;

                    CopyFloatArray(leftFingerPos, helmeData.pos_left_hand);
                    helmeData.pos_left_arm[0] = latestLeftPos.x;
                    helmeData.pos_left_arm[1] = latestLeftPos.y;
                    helmeData.pos_left_arm[2] = latestLeftPos.z;

                    helmeData.quat_left_arm[0] = latestLeftRot.x;
                    helmeData.quat_left_arm[1] = latestLeftRot.y;
                    helmeData.quat_left_arm[2] = latestLeftRot.z;
                    helmeData.quat_left_arm[3] = latestLeftRot.w;

                    CopyFloatArray(rightFingerPos, helmeData.pos_right_hand);
                    helmeData.pos_right_arm[0] = latestRightPos.x;
                    helmeData.pos_right_arm[1] = latestRightPos.y;
                    helmeData.pos_right_arm[2] = latestRightPos.z;

                    helmeData.quat_right_arm[0] = latestRightRot.x;
                    helmeData.quat_right_arm[1] = latestRightRot.y;
                    helmeData.quat_right_arm[2] = latestRightRot.z;
                    helmeData.quat_right_arm[3] = latestRightRot.w;

                    helmeData.joystick_left[0] = latestJoyL[0];
                    helmeData.joystick_left[1] = latestJoyL[1];

                    helmeData.joystick_right[0] = latestJoyR[0];
                    helmeData.joystick_right[1] = latestJoyR[1];
                    helmeData.tracking = tracking;
                    helmeData.video_last_received_id = lastFrameID;
                }

                helmeData.ts = lastTimestamp;
                helmeData.device_id = deviceId;

#if !IS_ANDROID
                helmeData.grip ??= new Triggers();
                CopyTriggers(OVRInputController.instance.triggers, helmeData.trigger);
                CopyButtons(OVRInputController.instance.buttons, helmeData.button);
                CopyTriggers(OVRInputController.instance.gris, helmeData.grip);
#else
                CopyTriggers(UIManager.Instance.uiinput_triggers, helmeData.trigger);
                CopyButtons(UIManager.Instance.uiinput_buttons, helmeData.button);
                helmeData.grip = null;
#endif
                currobotState.TTS = TTS;
                currobotState.ACTION = ACTION;
                PartialCommand partial = CommandDiff.BuildPartial(oldrobotState, currobotState, _sendPartialCommand);
                helmeData.commands = CommandDiff.IsEmpty(partial) ? null : partial;

                try
                {
                    WriteHelmeDataJson(jsonStream, helmeData);
                    var client = NetClient.instance;
                    client?.udpDataManager?.Send(jsonStream.GetBuffer(), null, (int)jsonStream.Length);
                    //Debug.Log(JsonUtility.ToJson(helmeData));       //输出调试
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[DataManager] Send thread exception: {ex.Message}");
                }
                finally
                {
                    ResetCommandTriggers();
                    oldrobotState.CopyFrom(currobotState);
                }

                sendThreadWakeSignal.WaitOne(10); // 固定 100Hz，并允许 stop 时立即唤醒
            }
        }
        catch (ThreadInterruptedException)
        {
        }
        finally
        {
            running = false;
        }
    }

    private static void CopyFloatArray(float[] source, float[] destination)
    {
        if (source == null || destination == null)
        {
            return;
        }

        int copyLength = Math.Min(source.Length, destination.Length);
        Array.Copy(source, destination, copyLength);
    }

    private static void CopyTriggers(Triggers source, Triggers destination)
    {
        if (source == null || destination == null)
        {
            return;
        }

        destination.r = source.r;
        destination.l = source.l;
    }

    private static void CopyButtons(Buttons source, Buttons destination)
    {
        if (source == null || destination == null)
        {
            return;
        }

        destination.x = source.x;
        destination.y = source.y;
        destination.a = source.a;
        destination.b = source.b;
        destination.leftjoy = source.leftjoy;
        destination.rightjoy = source.rightjoy;
    }

    private static void WriteHelmeDataJson(MemoryStream stream, HelmeData data)
    {
        stream.Position = 0;
        stream.SetLength(0);

        using var textWriter = new StreamWriter(stream, SendJsonEncoding, 1024, true);
        using var jsonWriter = new JsonTextWriter(textWriter)
        {
            Formatting = Formatting.None,
            CloseOutput = false
        };

        jsonWriter.WriteStartObject();

        WriteStringProperty(jsonWriter, nameof(HelmeData.device_id), data.device_id);
        jsonWriter.WritePropertyName(nameof(HelmeData.ts));
        jsonWriter.WriteValue(data.ts);
        jsonWriter.WritePropertyName(nameof(HelmeData.video_last_received_id));
        jsonWriter.WriteValue(data.video_last_received_id);

        WriteFloatArrayProperty(jsonWriter, nameof(HelmeData.pos_head), data.pos_head);
        WriteFloatArrayProperty(jsonWriter, nameof(HelmeData.quat_head), data.quat_head);
        WriteFloatArrayProperty(jsonWriter, nameof(HelmeData.pos_left_hand), data.pos_left_hand);
        WriteFloatArrayProperty(jsonWriter, nameof(HelmeData.pos_left_arm), data.pos_left_arm);
        WriteFloatArrayProperty(jsonWriter, nameof(HelmeData.quat_left_arm), data.quat_left_arm);
        WriteFloatArrayProperty(jsonWriter, nameof(HelmeData.pos_right_hand), data.pos_right_hand);
        WriteFloatArrayProperty(jsonWriter, nameof(HelmeData.pos_right_arm), data.pos_right_arm);
        WriteFloatArrayProperty(jsonWriter, nameof(HelmeData.quat_right_arm), data.quat_right_arm);
        WriteFloatArrayProperty(jsonWriter, nameof(HelmeData.joystick_left), data.joystick_left);
        WriteFloatArrayProperty(jsonWriter, nameof(HelmeData.joystick_right), data.joystick_right);

        if (data.commands != null)
        {
            WritePartialCommandProperty(jsonWriter, nameof(HelmeData.commands), data.commands);
        }

        WriteTrackingProperty(jsonWriter, nameof(HelmeData.tracking), data.tracking);
        WriteTriggersProperty(jsonWriter, nameof(HelmeData.trigger), data.trigger);
        WriteTriggersProperty(jsonWriter, nameof(HelmeData.grip), data.grip);
        WriteButtonsProperty(jsonWriter, nameof(HelmeData.button), data.button);

        jsonWriter.WriteEndObject();
        jsonWriter.Flush();
        textWriter.Flush();
    }

    private static void WriteStringProperty(JsonTextWriter writer, string propertyName, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteValue(value);
    }

    private static void WriteFloatArrayProperty(JsonTextWriter writer, string propertyName, float[] values)
    {
        if (values == null)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        for (int i = 0; i < values.Length; i++)
        {
            writer.WriteValue(values[i]);
        }
        writer.WriteEndArray();
    }

    private static void WriteTrackingProperty(JsonTextWriter writer, string propertyName, int value)
    {

        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WritePropertyName(nameof(value));
        writer.WriteValue(value);
        writer.WriteEndObject();
    }

    private static void WriteTriggersProperty(JsonTextWriter writer, string propertyName, Triggers value)
    {
        if (value == null)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WritePropertyName(nameof(Triggers.r));
        writer.WriteValue(value.r);
        writer.WritePropertyName(nameof(Triggers.l));
        writer.WriteValue(value.l);
        writer.WriteEndObject();
    }

    private static void WriteButtonsProperty(JsonTextWriter writer, string propertyName, Buttons value)
    {
        if (value == null)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WritePropertyName(nameof(Buttons.x));
        writer.WriteValue(value.x);
        writer.WritePropertyName(nameof(Buttons.y));
        writer.WriteValue(value.y);
        writer.WritePropertyName(nameof(Buttons.a));
        writer.WriteValue(value.a);
        writer.WritePropertyName(nameof(Buttons.b));
        writer.WriteValue(value.b);
        writer.WritePropertyName(nameof(Buttons.leftjoy));
        writer.WriteValue(value.leftjoy);
        writer.WritePropertyName(nameof(Buttons.rightjoy));
        writer.WriteValue(value.rightjoy);
        writer.WriteEndObject();
    }

    private static void WritePartialCommandProperty(JsonTextWriter writer, string propertyName, PartialCommand value)
    {
        if (value == null)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();

        WriteNullableIntProperty(writer, nameof(PartialCommand.SET_ROBOT_MODE), value.SET_ROBOT_MODE);
        WriteNullableIntProperty(writer, nameof(PartialCommand.SET_VIDEO_MODE), value.SET_VIDEO_MODE);
        WriteNullableIntProperty(writer, nameof(PartialCommand.SET_AUDIO_MODE), value.SET_AUDIO_MODE);
        WriteNullableIntProperty(writer, nameof(PartialCommand.TASK), value.TASK);
        WriteNullableIntProperty(writer, nameof(PartialCommand.STEP), value.STEP);
        WriteNullableFloatProperty(writer, nameof(PartialCommand.SET_AUDIO_VOLUME), value.SET_AUDIO_VOLUME);
        WriteNullableFloatProperty(writer, nameof(PartialCommand.MOVE_IDX_LEFT_X), value.MOVE_IDX_LEFT_X);
        WriteNullableFloatProperty(writer, nameof(PartialCommand.MOVE_IDX_LEFT_Y), value.MOVE_IDX_LEFT_Y);
        WriteNullableFloatProperty(writer, nameof(PartialCommand.MOVE_IDX_RIGHT_X), value.MOVE_IDX_RIGHT_X);
        WriteNullableFloatProperty(writer, nameof(PartialCommand.MOVE_IDX_RIGHT_Y), value.MOVE_IDX_RIGHT_Y);
        WriteNullableFloatProperty(writer, nameof(PartialCommand.SET_LEFT_ROTATE_ANGLE), value.SET_LEFT_ROTATE_ANGLE);
        WriteNullableFloatProperty(writer, nameof(PartialCommand.SET_RIGHT_ROTATE_ANGLE), value.SET_RIGHT_ROTATE_ANGLE);
        WriteActionProperty(writer, nameof(PartialCommand.ACTION), value.ACTION);
        WriteTtsProperty(writer, nameof(PartialCommand.TTS), value.TTS);

        writer.WriteEndObject();
    }

    private static void WriteNullableIntProperty(JsonTextWriter writer, string propertyName, int? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteValue(value.Value);
    }

    private static void WriteNullableFloatProperty(JsonTextWriter writer, string propertyName, float? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteValue(value.Value);
    }

    private static void WriteActionProperty(JsonTextWriter writer, string propertyName, global::ACTION value)
    {
        if (value == null || (string.IsNullOrEmpty(value.cmd) && string.IsNullOrEmpty(value.name)))
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        WriteStringProperty(writer, nameof(global::ACTION.cmd), value.cmd);
        WriteStringProperty(writer, nameof(global::ACTION.name), value.name);
        writer.WriteEndObject();
    }

    private static void WriteTtsProperty(JsonTextWriter writer, string propertyName, global::TTS value)
    {
        if (value == null || (string.IsNullOrEmpty(value.cmd) && string.IsNullOrEmpty(value.text)))
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        WriteStringProperty(writer, nameof(global::TTS.cmd), value.cmd);
        WriteStringProperty(writer, nameof(global::TTS.text), value.text);
        writer.WriteEndObject();
    }

    private float angle;
    private Vector3 pDir;
    private Vector3 cDir;
    // 计算单个关节的弯曲度（0~1）
    private float CalcCurl(Transform parent, Transform child, float maxAngle = 90f)
    {
        pDir = parent.rotation * Vector3.forward;
        cDir = child.rotation * Vector3.forward;
        angle = Vector3.Angle(pDir, cDir);
        return Mathf.InverseLerp(0f, maxAngle, angle);
    }

    private void SampleAndroidInput()
    {
        double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * tickToMs;
        lastTimestamp = initialUtcMs + (long)elapsedMs;
        latestHeadPos = Vector3.zero;
        latestHeadRot = Quaternion.identity;
        latestLeftPos = Vector3.zero;
        latestLeftRot = Quaternion.identity;
        latestRightPos = Vector3.zero;
        latestRightRot = Quaternion.identity;
        //latestJoyL[0] = latestJoyL[1] = 0f;
        //latestJoyR[0] = latestJoyR[1] = 0f;
        Array.Clear(leftFingerPos, 0, leftFingerPos.Length);
        Array.Clear(rightFingerPos, 0, rightFingerPos.Length);
    }

#if !IS_ANDROID

    private OVRCameraRig ovrRig;
    [Header("左手 (手势模式)")]
    public OVRHand leftHand;
    public OVRSkeleton leftSkeleton;

    [Header("右手 (手势模式)")]
    public OVRHand rightHand;
    public OVRSkeleton rightSkeleton;

    private Dictionary<OVRSkeleton.BoneId, Transform> leftBoneMap;
    private Dictionary<OVRSkeleton.BoneId, Transform> rightBoneMap;

    [HideInInspector]
    public Transform rightXRHand_ThumbTip;
    [HideInInspector]
    public Transform rightXRHand_RingTip;
    [HideInInspector]
    public Transform rightXRHand_LittleTip;
    [HideInInspector]
    public Transform leftXRHand_ThumbTip;
    [HideInInspector]
    public Transform leftXRHand_RingTip;


    // XRHand 系列骨骼（新版）
    private readonly OVRSkeleton.BoneId[][] xrFingerBones = new OVRSkeleton.BoneId[][]
    {
        // Thumb
        new OVRSkeleton.BoneId[]
        {
            OVRSkeleton.BoneId.XRHand_ThumbMetacarpal,
            OVRSkeleton.BoneId.XRHand_ThumbProximal,
            OVRSkeleton.BoneId.XRHand_ThumbDistal,
            OVRSkeleton.BoneId.XRHand_ThumbTip
        },

        // Index
        new OVRSkeleton.BoneId[]
        {
            OVRSkeleton.BoneId.XRHand_IndexMetacarpal,
            OVRSkeleton.BoneId.XRHand_IndexProximal,
            OVRSkeleton.BoneId.XRHand_IndexIntermediate,
            OVRSkeleton.BoneId.XRHand_IndexTip
        },

        // Middle
        new OVRSkeleton.BoneId[]
        {
            OVRSkeleton.BoneId.XRHand_MiddleMetacarpal,
            OVRSkeleton.BoneId.XRHand_MiddleProximal,
            OVRSkeleton.BoneId.XRHand_MiddleIntermediate,            
            OVRSkeleton.BoneId.XRHand_MiddleTip

        },

        // Ring
        new OVRSkeleton.BoneId[]
        {
            OVRSkeleton.BoneId.XRHand_RingMetacarpal,
            OVRSkeleton.BoneId.XRHand_RingProximal,
            OVRSkeleton.BoneId.XRHand_RingIntermediate,           
            OVRSkeleton.BoneId.XRHand_RingTip
        },

        // Little
        new OVRSkeleton.BoneId[]
        {
            OVRSkeleton.BoneId.XRHand_LittleMetacarpal,
            OVRSkeleton.BoneId.XRHand_LittleProximal,
            OVRSkeleton.BoneId.XRHand_LittleIntermediate,            
            OVRSkeleton.BoneId.XRHand_LittleTip

        }
    };

    private Transform GetXRPalm(OVRSkeleton skeleton)
    {
        foreach (var bone in skeleton.Bones)
        {
            if (bone.Id == OVRSkeleton.BoneId.XRHand_Palm)
            {
                return bone.Transform;
            }
        }
        return null;
    }

    private bool IsLeftHandTracking(OVRInput.Controller controller)
    {
        return controller == OVRInput.Controller.Hands ||
               controller == OVRInput.Controller.LHand;
    }
    private bool IsRightHandTracking(OVRInput.Controller controller)
    {
        return controller == OVRInput.Controller.Hands ||
               controller == OVRInput.Controller.RHand;
    }

    private bool IsHandTracking(OVRInput.Controller controller)
    {
        return controller == OVRInput.Controller.Hands ||
               controller == OVRInput.Controller.LHand ||
               controller == OVRInput.Controller.RHand;
    }

    private bool IsOnlyHandsTracking(OVRInput.Controller controller)
    {
        return IsLeftHandTracking(controller) && IsRightHandTracking(controller);
    }


    private void CacheControllerPose(
        OVRInput.Controller controller,
        bool isConnected,
        ref bool wasConnected,
        ref Vector3 lastPosition,
        ref Quaternion lastRotation)
    {
        if (isConnected)
        {
            // 更新为最新姿态，只要控制器在线就刷新
            lastPosition = OVRInput.GetLocalControllerPosition(controller);
            lastRotation = OVRInput.GetLocalControllerRotation(controller);
        }
        else if (wasConnected && !isConnected)
        {
            // 丢失控制器时保持上一帧缓存，不做额外处理
        }

        wasConnected = isConnected;
    }

    public void DefaultFingerCurls()
    {
        for (int i = 0; i < leftFingerPos.Length; i++)
        {
            leftFingerPos[i] = 0;
        }

        for (int i = 0; i < rightFingerPos.Length; i++)
        {
            rightFingerPos[i] = 0;
        }
    }


    private Transform j0;
    private Transform j1;
    private Transform j2;


    // 获取五指三关节的 curl（[finger][joint]）
    public void GetFingerCurls()
    {
        if (leftBoneMap.Count <= 0) { return; }
        for (int f = 0; f < 5; f++)
        {
            j0 = leftBoneMap[xrFingerBones[f][0]];
            j1 = leftBoneMap[xrFingerBones[f][1]];
            j2 = leftBoneMap[xrFingerBones[f][2]];
            if (f == 0)
            {
                leftFingerPos[f] = CalcCurl(j0, j1);
                leftFingerPos[f + 1] = CalcCurl(j1, j2);
            }
            else
            {
                leftFingerPos[f + 1] = CalcCurl(j0, j1);
            }
        }

        for (int f = 0; f < 5; f++)
        {
            j0 = rightBoneMap[xrFingerBones[f][0]];
            j1 = rightBoneMap[xrFingerBones[f][1]];
            j2 = rightBoneMap[xrFingerBones[f][2]];
            if (f == 0)
            {
                rightFingerPos[f] = CalcCurl(j0, j1);
                rightFingerPos[f + 1] = CalcCurl(j1, j2);
            }
            else
            {
                rightFingerPos[f + 1] = CalcCurl(j0, j1);
            }
        }
    }

    private void SampleQuestInput()
    {
        var activeController = OVRInput.GetActiveController();
        tracking = IsOnlyHandsTracking(activeController) ? 1 : 0;
        _lastLeftPosition = latestLeftPos;
        _lastLeftRotation = latestLeftRot;
        _lastRightPosition = latestRightPos;
        _lastRightRotation = latestRightRot;

        bool leftControlConnected = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
        bool rightControlConnected = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);

        CacheControllerPose(OVRInput.Controller.LTouch, leftControlConnected, ref _wasLeftConnected,
            ref _lastLeftPosition, ref _lastLeftRotation);
        CacheControllerPose(OVRInput.Controller.RTouch, rightControlConnected, ref _wasRightConnected,
            ref _lastRightPosition, ref _lastRightRotation);

        double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * tickToMs;
        lastTimestamp = initialUtcMs + (long)elapsedMs;

        if (headTrans != null)
        {
            //latestHeadPos = GetBodyRelativePosition(headTrans.position);
            latestHeadPos = headTrans.position;
            latestHeadRot = headTrans.rotation;
        }
        else
        {
            latestHeadPos = Vector3.zero;
            latestHeadRot = Quaternion.identity;
        }

        if (!leftControlConnected && !IsLeftHandTracking(activeController))
        {
            //latestLeftPos = GetBodyRelativePosition(_lastLeftPosition);
            latestLeftPos = _lastLeftPosition;
            latestLeftRot = _lastLeftRotation;
        }
        else if (leftHandTrans != null)
        {
            //latestLeftPos = GetBodyRelativePosition(leftHandTrans.position);
            latestLeftPos = leftHandTrans.position;
            latestLeftRot = leftHandTrans.rotation;
        }

        if (!rightControlConnected && !IsRightHandTracking(activeController))
        {
            //latestRightPos = GetBodyRelativePosition(_lastRightPosition);
            latestRightPos = _lastRightPosition;
            latestRightRot = _lastRightRotation;
        }
        else if (rightHandTrans != null)
        {
            //latestRightPos = GetBodyRelativePosition(rightHandTrans.position);
            latestRightPos = rightHandTrans.position;
            latestRightRot = rightHandTrans.rotation;
        }


        //latestJoyR[0] = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick).x;
        //latestJoyR[1] = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick).y;

    }
#endif


    //---------------------------------------------
    //   数据是否变化
    //---------------------------------------------

    private void InitBodyOrigin()
    {
        RefreshBodyOriginFromHead();
    }

    public void RefreshBodyOriginFromHead()
    {
        if (headTrans == null)
        {
            bodyOrigin = Vector3.zero;
            return;
        }
        bodyOrigin = headTrans.position + pelvisOffsetFromHead;
        Debug.Log("bodyOrigin" + bodyOrigin);
    }

    private Vector3 GetBodyRelativePosition(Vector3 worldPos)
    {
        return worldPos - bodyOrigin;
    }

    private Vector3 BackToRelativePosition(Vector3 bodyPos)
    {
        return bodyPos + bodyOrigin;
    }

    //---------------------------------------------
    //  Unity销毁时必须停止线程（非常重要）
    //---------------------------------------------
    private void OnDestroy()
    {
        StopSendingThread("OnDestroy");
    }

    private void OnApplicationQuit()
    {
        StopSendingThread("OnApplicationQuit");
    }

    private void OnDisable()
    {
        StopSendingThread("OnDisable");
        // Config persistence is handled by UI flows; avoid blocking disable with file IO + UDP rebuild.
    }

    private void OnApplicationPause(bool pause)
    {
        if (pause)
        {
            StopSendingThread("OnApplicationPause");
        }
    }

    private void StopSendingThread(string reason)
    {
        if (!running && sendThread == null)
        {
            return;
        }

        shouldRun = false;
        try { sendThreadWakeSignal.Set(); } catch { }

        if (sendThread != null && sendThread.IsAlive)
        {
            try
            {
                if (!sendThread.Join(80))
                {
                    sendThread.Interrupt();
                    sendThread.Join(20);
                }
            }
            catch { }
        }

        sendThread = null;
        running = false;
        Debug.Log($"[DataManager] Send thread stopped ({reason}).");
    }

    private void ResetCommandTriggers()
    {
        if (ACTION != null)
        {
            ACTION.cmd = string.Empty;
            ACTION.name = string.Empty;
        }

        if (TTS != null)
        {
            TTS.cmd = string.Empty;
            TTS.text = string.Empty;
        }

        currobotState.ACTION = ACTION;
        currobotState.TTS = TTS;

        currobotState.MOVE_IDX_LEFT_X = 0f;
        currobotState.MOVE_IDX_LEFT_Y = 0f;
        currobotState.MOVE_IDX_RIGHT_X = 0f;
        currobotState.MOVE_IDX_RIGHT_Y = 0f;
        currobotState.SET_LEFT_ROTATE_ANGLE = 0f;
        currobotState.SET_RIGHT_ROTATE_ANGLE = 0f;
    }
}
