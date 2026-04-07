#if UNITY_EDITOR
using Unity.VisualScripting;
using UnityEditor;
#endif
using UnityEngine;
[DefaultExecutionOrder(1000)]
public class GestureAndControllerInputModeManager : MonoBehaviour
{
    public static GestureAndControllerInputModeManager _instance;
    // ========== 手势相关 ==========
    private OVRHand leftHand;
    private OVRSkeleton leftskeleton;

    private OVRHand rightHand;
    private OVRSkeleton rightskeleton;

    // ========== 控制器相关 ==========
    //private bool controllerModeActivated = false;

    private bool pendingEnablePassthrough;
    private Camera[] _ovrRigCameras;
    private OVRPassthroughLayer _passthroughLayer;

    // ========== 模式状态 ==========
    private AudioMode RobotAudioMode;
    private AudioMode PreviousAudioMode = AudioMode.SLEEP;
    private float audioholdTime = 0f;

    private VideoMode RobotVideoMode;
    private VideoMode PreviousVideoMode = VideoMode.PERSPECTIVE;
    private float videoholdTime = 0f;

    private RobotMode RobotStateMode;
    private RobotMode PreviousRobotMode = RobotMode.HOME;
    private float robotholdTime = 0f;


    bool isleft = true;
    int menuCount;
    float menuholdtime;

#if !IS_ANDROID

    private void Awake()
    {
        _instance = this;
        leftHand = DataManager.Instance.leftHand;
        rightHand = DataManager.Instance.rightHand;
        leftskeleton = DataManager.Instance.leftSkeleton;
        rightskeleton = DataManager.Instance.rightSkeleton;
        var rig = GameObject.Find("OVRCameraRig");
        _ovrRigCameras = rig != null ? rig.GetComponentsInChildren<Camera>(true) : null;
        _passthroughLayer = FindAnyObjectByType<OVRPassthroughLayer>();
    }

    private void Start()
    {
#if !UNITY_EDITOR
        SetPassthroughEnabled(true);
#endif
    }

    void Update()
    {
        if (!leftHand.IsTracked || !rightHand.IsTracked)
        {
            HandleControllerInput(); // 手柄控制
            DataManager.Instance.DefaultFingerCurls();

        }
        else 
        {
            HandleHandGestures();    // 手势控制
            DataManager.Instance.GetFingerCurls();
        }
        TryCompletePendingPassthroughEnable();
    }

    private void LateUpdate()
    {

    }


    // ==========================================================
    //                  手柄按键模式（原逻辑）
    // ==========================================================

    void HandleControllerInput()
    {

        
        if (OVRInputController.instance.menuButtonDown)
        {
            menuCount++;
            bool enablePassthrough = (menuCount % 2) != 0;
            UIManager.Instance.settingTog.isOn = enablePassthrough;
        }

        //if (OVRInputController.instance.menuButton)
        //{
        //    menuholdtime += Time.deltaTime;
        //    if(menuholdtime >= 1) 
        //    {
        //        menuCount = 1;
        //        UIManager.Instance.settingTog.isOn = true;
        //    }
        //    Vector3 desiredPosition = DataManager.Instance.leftHandTrans.TransformPoint(new Vector3(0, 0, 3));
        //    UIManager.Instance.settingPanel.transform.position = desiredPosition;
        //}
        //else 
        //{
        //menuholdtime = 0;
        //}



        // 3️⃣ 仅同时按下 LHT + RHT 且不含 IndexTrigger → 持续 1 秒触发模式切换

        //if (OVRInputController.instance.RobotStateChangeClick())
        //{
        //    robotholdTime += Time.deltaTime;
        //    if(robotholdTime >= 1) 
        //    {
        //        if (!DataManager.Instance.RobotHaveChange)
        //        {
        //            SwitchRobotMode();
        //        }
        //        else
        //        {
        //            DataManager.Instance.currobotState.SET_ROBOT_MODE = (int)RobotStateMode;
        //        }
        //    }
        //}
        //else if (OVRInputController.instance.AudioChangeClick())
        //{
        //    audioholdTime += Time.deltaTime;
        //    if (audioholdTime >= 1)
        //    {
        //        if (!DataManager.Instance.AudioHaveChange)
        //        {
        //            SwitchVoiceMode();
        //        }
        //        else
        //        {
        //            DataManager.Instance.currobotState.SET_AUDIO_MODE = (int)RobotAudioMode;
        //        }
        //    }
        //}
        //else if (OVRInputController.instance.VideoChangeClick())
        //{
        //    videoholdTime += Time.deltaTime;
        //    if (videoholdTime >= 1)
        //    {
        //        if (!DataManager.Instance.VideoHaveChange)
        //        {
        //            SwitchVideoMode();
        //        }
        //        else
        //        {
        //            DataManager.Instance.currobotState.SET_VIDEO_MODE = (int)RobotVideoMode;
        //        }
        //    }
        //}
        //else
        //{
        //    robotholdTime = 0;
        //    videoholdTime = 0;
        //    audioholdTime = 0;
        //    DataManager.Instance.AudioHaveChange = false;
        //    DataManager.Instance.RobotHaveChange = false;
        //    DataManager.Instance.VideoHaveChange = false;
        //}

        // 4️⃣ 单独按 A/B 键调整速度（不受扳机干扰）
        //if (OVRInputController.instance.AButtonDown && OVRInputController.instance.NoPressTriggers())
        //{
        //    if (DataManager.Instance.velocity < 30)
        //        DataManager.Instance.velocity += 5;
        //}

        //if (OVRInputController.instance.BButtonDown && OVRInputController.instance.NoPressTriggers())
        //{
        //    if (DataManager.Instance.velocity > 10)
        //        DataManager.Instance.velocity -= 5;
        //}

    }

    private void Tick(float deltaTime,Transform target,Transform curtrans)
    {

    }

    // ==========================================================
    //                  手势控制（你设计的3种）
    // ==========================================================

    public void SetPassthroughEnabled(bool enable)
    {
        if (OVRManager.instance == null)
        {
            Debug.LogWarning("[Passthrough] OVRManager.instance is null, cannot toggle passthrough.");
            return;
        }

        // Underlay passthrough 依赖 EyeFov layer 的 alpha 混合。
        // 如果系统处于 premultiplied alpha 模式，而相机 clearColor 是“白色 + alpha=0”，会导致背景发白、透视看不见。
        // 这里强制使用非预乘 alpha，并把相机 clearColor 置为黑色透明，避免白底遮挡透视。
        ConfigureEyeBufferForPassthrough(enable);

        if (!enable)
        {
            pendingEnablePassthrough = false;
            OVRManager.instance.isInsightPassthroughEnabled = false;
            if (_passthroughLayer != null)
            {
                _passthroughLayer.hidden = true;
            }
            //UIManager.Instance?.SetVideoRenderVisible(true);
            return;
        }

        Debug.Log($"[Passthrough] supported={OVRManager.IsInsightPassthroughSupported()}, initState={OVRPlugin.GetInsightPassthroughInitializationState()}, premultEyeFov={OVRManager.eyeFovPremultipliedAlphaModeEnabled}");

        // Passthrough 需要 Horizon OS 的 HEADSET_CAMERA 权限；未授权时先请求，再在授权后真正开启。
        if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess))
        {
            pendingEnablePassthrough = true;
            Debug.Log("[Passthrough] Requesting PassthroughCameraAccess permission...");
            OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.PassthroughCameraAccess });
            return;
        }

        pendingEnablePassthrough = false;
        OVRManager.instance.isInsightPassthroughEnabled = true;
        if (_passthroughLayer != null)
        {
            _passthroughLayer.hidden = false;
        }
        //UIManager.Instance?.SetVideoRenderVisible(false);
    }

    private void TryCompletePendingPassthroughEnable()
    {
        if (!pendingEnablePassthrough)
        {
            return;
        }

        if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess))
        {
            return;
        }

        pendingEnablePassthrough = false;

        if (OVRManager.instance != null)
        {
            OVRManager.instance.isInsightPassthroughEnabled = true;
        }

        if (_passthroughLayer != null)
        {
            _passthroughLayer.hidden = false;
        }
        //UIManager.Instance?.SetVideoRenderVisible(false);
        Debug.Log("[Passthrough] Permission granted, passthrough enabled.");
    }


    private void ConfigureEyeBufferForPassthrough(bool passthroughEnabled)
    {
        // 这里用 try/catch 防止某些平台/版本 API 不可用导致直接崩溃。
        try
        {
            if (OVRManager.eyeFovPremultipliedAlphaModeEnabled)
            {
                OVRManager.eyeFovPremultipliedAlphaModeEnabled = false;
                Debug.Log("[Passthrough] Disabled EyeFov premultiplied alpha mode for underlay blending.");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Passthrough] Failed to set eyeFovPremultipliedAlphaModeEnabled: {e.Message}");
        }

        if (_ovrRigCameras == null || _ovrRigCameras.Length == 0)
        {
            return;
        }

        // 统一把 OVRCameraRig 的相机背景清为黑色透明，避免“透明但带白色 RGB”在某些混合路径下发白。
        var clear = passthroughEnabled ? new Color(0f, 0f, 0f, 0f) : new Color(1f, 1f, 1f, 1f);
        foreach (var cam in _ovrRigCameras)
        {
            if (cam == null) continue;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = clear;
        }
    }

    void HandleHandGestures()
    {
        //xrFingerBones[0][3]  xrFingerBones[3][3] xrFingerBones[4][3]
        //j0 = rightBoneMap[xrFingerBones[0][3]]; j1 = rightBoneMap[xrFingerBones[3][3]]
        //j0 = rightBoneMap[xrFingerBones[0][3]]; j1 = rightBoneMap[xrFingerBones[4][3]]
        //j0 = leftBoneMap[xrFingerBones[0][3]]; j1 = leftBoneMap[xrFingerBones[3][3]]

        //bool g1 = IsGesture2();
        //bool g2 = IsGesture2();
        //bool g3 = IsGesture3();


        bool g1 = IsPinching(DataManager.Instance.rightXRHand_ThumbTip, DataManager.Instance.rightXRHand_RingTip, 0.02f);
        bool g2 = IsPinching(DataManager.Instance.rightXRHand_ThumbTip, DataManager.Instance.rightXRHand_LittleTip, 0.02f);
        bool g3 = IsPinching(DataManager.Instance.leftXRHand_ThumbTip, DataManager.Instance.leftXRHand_RingTip, 0.02f);

        if (g1)
        {
            robotholdTime += Time.deltaTime;
            if (robotholdTime >= 1)
            {
                if (!DataManager.Instance.RobotHaveChange)
                {
                    SwitchRobotMode();
                }
                else
                {
                    DataManager.Instance.currobotState.SET_ROBOT_MODE = (int)RobotStateMode;
                }
            }
        }
        else if (g2)
        {
            audioholdTime += Time.deltaTime;
            if (audioholdTime >= 1)
            {
                if (!DataManager.Instance.AudioHaveChange)
                {
                    SwitchVoiceMode();
                }
                else
                {
                    DataManager.Instance.currobotState.SET_AUDIO_MODE = (int)RobotAudioMode;
                }
            }
        }
        else if (g3)
        {
            videoholdTime += Time.deltaTime;
            if (videoholdTime >= 1)
            {
                if (!DataManager.Instance.VideoHaveChange)
                {
                    SwitchVideoMode();
                }
                else
                {
                    DataManager.Instance.currobotState.SET_VIDEO_MODE = (int)RobotVideoMode;
                }
            }
        }
        else
        {
            robotholdTime = 0;
            audioholdTime = 0;
            videoholdTime = 0;
            DataManager.Instance.RobotHaveChange = false;
            DataManager.Instance.AudioHaveChange = false;
            DataManager.Instance.VideoHaveChange = false;
        }



        //bool g1 = IsPinching(DataManager.Instance.rightXRHand_ThumbTip, DataManager.Instance.rightXRHand_RingTip, 0.25f);
        //bool g2 = IsPinching(DataManager.Instance.rightXRHand_ThumbTip, DataManager.Instance.rightXRHand_LittleTip, 0.25f);
        //bool g3 = IsPinching(DataManager.Instance.leftXRHand_ThumbTip, DataManager.Instance.leftXRHand_RingTip, 0.25f);


        //// 三个姿势全部没触发 → 重置计时
        //if (!g1 && !g2 && !g3)
        //{
        //    g1StartTime = g2StartTime = g3StartTime = -1f;
        //    prevGesture1 = prevGesture2 = prevGesture3 = false;
        //    return;
        //}

        //float timeNow = Time.time;

        //// ---------- 手势1 ---------- 
        //if (g1)
        //{
        //    if (g1StartTime < 0f) g1StartTime = timeNow; // 第一次发现手势
        //    if (!prevGesture1 && (timeNow - g1StartTime >= gestureHoldTime))
        //    {
        //        prevGesture1 = true;
        //        SwitchRobotMode();
        //        g1StartTime = -1f; // 防止重复触发
        //        return;
        //    }
        //}
        //else
        //{
        //    g1StartTime = -1f;
        //    prevGesture1 = false;
        //}

        //// ---------- 手势2 ----------
        //if (g2)
        //{
        //    if (g2StartTime < 0f) g2StartTime = timeNow;
        //    if (!prevGesture2 && (timeNow - g2StartTime >= gestureHoldTime))
        //    {
        //        prevGesture2 = true;
        //        SwitchVoiceMode();
        //        g2StartTime = -1f;
        //        return;
        //    }
        //}
        //else
        //{
        //    g2StartTime = -1f;
        //    prevGesture2 = false;
        //}

        //// ---------- 手势3 ----------
        //if (g3)
        //{
        //    if (g3StartTime < 0f) g3StartTime = timeNow;
        //    if (!prevGesture3 && (timeNow - g3StartTime >= gestureHoldTime))
        //    {
        //        prevGesture3 = true;
        //        SwitchVideoMode();
        //        g3StartTime = -1f;
        //        return;
        //    }
        //}
        //else
        //{
        //    g3StartTime = -1f;
        //    prevGesture3 = false;
        //}
    }

    // ==========================================================
    //                           手势判定
    // ==========================================================

    bool IsRightTwo()
    {
        return DataManager.Instance.rightFingerPos[2] < 0.5f &&
            DataManager.Instance.rightFingerPos[3] < 0.5f &&
            DataManager.Instance.rightFingerPos[4] > 0.6f &&
            DataManager.Instance.rightFingerPos[5] > 0.6f;
    }

    bool IsFist(bool isleft)        //是否弯曲
    {
        if (isleft)
        {
            return DataManager.Instance.leftFingerPos[2] > 0.7f &&
                DataManager.Instance.leftFingerPos[3] > 0.8f &&
                DataManager.Instance.leftFingerPos[4] > 0.8f &&
                DataManager.Instance.leftFingerPos[5] > 0.8f;
        }
        else
        {
            return DataManager.Instance.rightFingerPos[2] > 0.7f &&
                DataManager.Instance.rightFingerPos[3] > 0.8f &&
                DataManager.Instance.rightFingerPos[4] > 0.8f &&
                DataManager.Instance.rightFingerPos[5] > 0.8f;
        }
    }
    bool IsOpen(bool isleft)        //打开
    {
        if (isleft)
        {
            return DataManager.Instance.leftFingerPos[2] < 0.15f &&
                DataManager.Instance.leftFingerPos[3] < 0.15f &&
                DataManager.Instance.leftFingerPos[4] < 0.15f &&
                DataManager.Instance.leftFingerPos[5] < 0.15f;
        }
        else
        {
            return DataManager.Instance.rightFingerPos[2] < 0.15f &&
                DataManager.Instance.rightFingerPos[3] < 0.15f &&
                DataManager.Instance.rightFingerPos[4] < 0.15f &&
                DataManager.Instance.rightFingerPos[5] < 0.15f;
        }
    }


    //xrFingerBones[0][3]  xrFingerBones[3][3] xrFingerBones[4][3]
    //j0 = rightBoneMap[xrFingerBones[0][3]]; j1 = rightBoneMap[xrFingerBones[3][3]]
    //j0 = rightBoneMap[xrFingerBones[0][3]]; j1 = rightBoneMap[xrFingerBones[4][3]]
    //j0 = leftBoneMap[xrFingerBones[0][3]]; j1 = leftBoneMap[xrFingerBones[3][3]]

    bool IsPinching(Transform a, Transform b, float threshold)
    {
        return Vector3.Distance(a.position, b.position) < threshold;
    }



    /// <summary>
    /// 判断手掌朝向（只返回朝上、朝下、其他）
    /// </summary>
    public int CheckPalmIsUp(OVRSkeleton skeleton)
    {
        if (skeleton == null || !skeleton.IsInitialized)
            return 0;

        // 直接使用手掌骨骼的法线朝向
        foreach (var bone in skeleton.Bones)
        {
            if (bone.Id == OVRSkeleton.BoneId.XRHand_Palm)
            {
                Vector3 palmNormal = bone.Transform.up;
                float dot = Vector3.Dot(palmNormal, Vector3.up);

                if (dot > 0.5f)
                    return -1;
                else if (dot < -0.5f)
                    return 1;
                else
                    return 0;
            }
        }
        return 0;

    }


    bool IsGesture1()       //双手握拳并手掌朝上   切换机器人模式
    {
        return IsFist(isleft) && IsFist(!isleft) && CheckPalmIsUp(leftskeleton) == 1 && CheckPalmIsUp(rightskeleton) == 1;
    }

    bool IsGesture2()       //双手握拳并手掌朝下       切换声音模式
    {
        return (IsFist(isleft) && CheckPalmIsUp(leftskeleton) == -1 && IsFist(!isleft) && CheckPalmIsUp(rightskeleton) == -1);
    }

    bool IsGesture3()     //双手捏合并手掌朝上         切换视频模式
    {
        bool leftPinching = leftHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool rightPinching = rightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        return (leftPinching && CheckPalmIsUp(leftskeleton) == 1 && rightPinching && CheckPalmIsUp(rightskeleton) == 1);
    }

    // ==========================================================
    //                       模式切换函数
    // ==========================================================

    private void SwitchRobotMode()
    {
        if (DataManager.Instance.RobotHaveChange) {return; }
        switch (RobotStateMode)
        {
            case RobotMode.HOME:
                RobotStateMode = RobotMode.ASYNC;
                OVRReset.Instance.AlignChildToWorld();
                DataManager.Instance.RefreshBodyOriginFromHead();
                PreviousRobotMode = RobotMode.HOME;
                break;
            case RobotMode.ASYNC:
                // 判断上一次是否是 Home 来决定下一步
                if (PreviousRobotMode == RobotMode.HOME)
                {
                    RobotStateMode = RobotMode.SYNC;
                    OVRReset.Instance.AlignChildToWorld();
                    DataManager.Instance.RefreshBodyOriginFromHead();
                }
                else
                {
                    RobotStateMode = RobotMode.HOME;
                }
                PreviousRobotMode = RobotMode.ASYNC;
                break;
            case RobotMode.SYNC:
                RobotStateMode = RobotMode.ASYNC;
                OVRReset.Instance.AlignChildToWorld();
                DataManager.Instance.RefreshBodyOriginFromHead();
                PreviousRobotMode = RobotMode.SYNC;
                break;
        }

        // 保存上一次模式
        //OVRInputController.instance.TriggerHapticFeedback();
        DataManager.Instance.currobotState.SET_ROBOT_MODE = (int)RobotStateMode;
        DataManager.Instance.RobotHaveChange = true;
    }


    private void SwitchVoiceMode()
    {
        if(DataManager.Instance.AudioHaveChange) { return; }
        switch (RobotAudioMode)
        {
            case AudioMode.SLEEP:
                RobotAudioMode = AudioMode.DIRECT;
                PreviousAudioMode = AudioMode.SLEEP;
                break;
            case AudioMode.DIRECT:
                // 判断上一次是否是 Home 来决定下一步
                if (PreviousAudioMode == AudioMode.SLEEP)
                {
                    RobotAudioMode = AudioMode.REALTIME;
                }
                else 
                {
                    RobotAudioMode = AudioMode.SLEEP;
                }
                PreviousAudioMode = AudioMode.DIRECT;

                break;
            case AudioMode.REALTIME:
                RobotAudioMode = AudioMode.DIRECT;
                PreviousAudioMode = AudioMode.REALTIME;
                break;
        }
        //OVRInputController.instance.TriggerHapticFeedback();
        DataManager.Instance.currobotState.SET_AUDIO_MODE = (int)RobotAudioMode;
        DataManager.Instance.AudioHaveChange = true;
    }

    private void SwitchVideoMode()
    {
        if(DataManager.Instance.VideoHaveChange) { return; }
        switch (RobotVideoMode)
        {
            case VideoMode.PERSPECTIVE:
                RobotVideoMode = VideoMode.FRONT;
                PreviousVideoMode = VideoMode.PERSPECTIVE;
                UIManager.Instance.SetVideoPanel(true);
                SetPassthroughEnabled(false);

                break;
            case VideoMode.FRONT:
                // 判断上一次是否是 Home 来决定下一步
                if (PreviousVideoMode == VideoMode.PERSPECTIVE) 
                {
                    RobotVideoMode = VideoMode.REALSENSE;
                    UIManager.Instance.SetVideoPanel(true);
                    SetPassthroughEnabled(false);

                }
                else 
                {
                    RobotVideoMode = VideoMode.PERSPECTIVE;
                    UIManager.Instance.SetVideoPanel(false);
                    SetPassthroughEnabled(true);
                }
                PreviousVideoMode = VideoMode.FRONT;
                break;
            case VideoMode.REALSENSE:
                RobotVideoMode = VideoMode.FRONT;
                UIManager.Instance.SetVideoPanel(true);
                PreviousVideoMode = VideoMode.REALSENSE;
                SetPassthroughEnabled(false);

                break;
        }

        //OVRInputController.instance.TriggerHapticFeedback();
        DataManager.Instance.currobotState.SET_VIDEO_MODE = (int)RobotVideoMode;
        DataManager.Instance.VideoHaveChange = true;
    }
#endif
}
