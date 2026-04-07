using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Debug = AppLog;

public class UIManager : MonoBehaviour
{
    public static UIManager Instance;
    private readonly Dictionary<string, Button> buttonDict = new Dictionary<string, Button>();
    private readonly Dictionary<string, string> lastRobotSnapshot = new Dictionary<string, string>();
    private readonly Dictionary<Button, Coroutine> holdCoroutines = new Dictionary<Button, Coroutine>();
    public GameObject loginPanel;
    public GameObject selectPanel;
    public GameObject robotSelect_prefab;
    public GameObject robotSelectContent;

    [Header("loginpanel")]
    public Button openserverlist_btn;
    public GameObject serverlist_page;
    public TMP_InputField ip_inputField;
    public Button server1_btn;
    public Button server2_btn;
    public Button server3_btn;
    public Button loginBtn;
    public Toggle islantoggle;
    public Toggle videoav1_tog;
    public Toggle videoh264_tog;
    public Toggle videohevc_tog;
    public GameObject MainCanvas_obj;
    private bool suppressVideoDecoderToggleCallbacks;

    [Header("Debug Panel")]
    [SerializeField, Range(0.05f, 1f)] private float debugStateUpdateInterval = 0.2f;
    private float nextDebugStateUpdateTime;
    private RectTransform _batteryPercentRect;
    private Image _batteryPercentImage;
    private bool _batteryBaseHeightCached;
    private float _batteryBaseHeight;
    private Color _batteryColorHigh;
    private Color _batteryColorMid;
    private Color _batteryColorLow;


    [Header("视频的轮盘状态选择")]
    [SerializeField] private List<Transform> turntableSegments = new List<Transform>(); // 子物体列表（顺时针/逆时针顺序均可）
    [SerializeField] private float turntableHighlightScale = 1.05f;
    [SerializeField] private float turntableNormalScale = 1f;
    [SerializeField] private float joystickDeadzone = 0.2f;
    public GameObject turntableObj;
    private int currentTurntableIndex = -1;
    private int oldTurntableIndex = -1;
    private int turntableTouchCount;
    int clickindex = 0;
    private float timer = 0f;


    [Header("SettingsPanel 额外按钮")]
    public Button VideoParametersBtn;
    public Button NavigationBtn;
    public Button PortSettingsBtn;
    public GameObject Quest_settingPanel;
    public Button Quest_settingBtn;


    [Header("NavigationPanel")]
    public GameObject NavigationPanel;
    public Toggle NavigationMode_Tog;
    private TextMeshProUGUI NavigationModeText;

    public Button NavigationPlayBtn;
    public Button NavigationStopBtn;
    public Button NavigationPauseBtn;
    public Button NavigationInitBtn;
    public Button NavigationCloseBtn;
    public GameObject NavigationContent;
    private List<Button> NavigationBtnList = new List<Button>();


    [Header("IP Config Change Panel")]
    public GameObject PortConfigPanel;
    [SerializeField] private TMP_InputField localDataPortInput;
    [SerializeField] private TMP_InputField remoteDataPortInput;
    [SerializeField] private TMP_InputField localVideoPortInput;
    [SerializeField] private TMP_InputField remoteVideoPortInput;
    [SerializeField] private TMP_InputField localAudioPortInput;
    [SerializeField] private TMP_InputField remoteAudioPortInput;
    [SerializeField] private Button portSubmitBtn;
    [SerializeField] private Button portCloseBtn;

    [Header("Quest VideoParametersPanel")]
    public GameObject VideoParametersPanel;
    public Button VideoParametersCloseBtn;
    public Slider Quest_volumeSlider;

    [Header("视频相关面板")]
    private const string LRSliderPrefKey = "UIManager.LRSlider";
    private const string UDSliderPrefKey = "UIManager.UDSlider";
    private const string ScaleSliderPrefKey = "UIManager.ScaleSlider";
    [SerializeField] private GameObject VideoPanel;
    private Material quardvideoMaterial;
    private Renderer quardvideoRender;
    private static readonly int PlaneWidth = Shader.PropertyToID("_PlaneWidth");
    #region extral video quard
    public Toggle videoChange_btn;
    public TextMeshProUGUI videoChange_text;
    public Slider LR_slider;
    private TextMeshProUGUI LR_value;
    public Slider UD_slider;
    private TextMeshProUGUI UD_value;
    public Slider Scale_slider;
    private TextMeshProUGUI Scale_value;
    #endregion


    [Header("Quest的leftcam按钮")]
    public Button Quest_MOVE_IDX_LEFT_X_ADDBtn;
    public Button Quest_MOVE_IDX_LEFT_X_REDUCEBtn;
    public Button Quest_MOVE_IDX_LEFT_Y_ADDBtn;
    public Button Quest_MOVE_IDX_LEFT_Y_REDUCEBtn;
    public Button Quest_SET_LEFT_ROTATE_ANGLE_ADDBtn;
    public Button Quest_SET_LEFT_ROTATE_ANGLE_REDUCEBtn;

    [Header("Quest的rightcam按钮")]
    public Button Quest_MOVE_IDX_RIGHT_X_ADDBtn;
    public Button Quest_MOVE_IDX_RIGHT_X_REDUCEBtn;
    public Button Quest_MOVE_IDX_RIGHT_Y_ADDBtn;
    public Button Quest_MOVE_IDX_RIGHT_Y_REDUCEBtn;
    public Button Quest_SET_RIGHT_ROTATE_ANGLEADDBtn;
    public Button Quest_SET_RIGHT_ROTATE_ANGLEREDUCEBtn;

    [Header("Quest的TTS UI")]
    public Toggle Quest_TTSToggle;
    public GameObject Quest_TTSListObj;
    public GameObject Quest_TTSListContent;
    public Button Quest_TTSCmdPlayButton;
    public Button Quest_TTSCmdPauseButton;
    public Button Quest_TTSCmdStopButton;

    [Header("Quest的Action UI")]
    public Toggle Quest_ActionToggle;
    public GameObject Quest_ActionListObj;
    public GameObject Quest_ActionListContent;
    public Button Quest_ActionCmdPlayButton;
    public Button Quest_ActionCmdPauseButton;
    public Button Quest_ActionCmdStopButton;
    private bool loginFlowInProgress;


    [Header("安卓 Input")]

    public Toggle NavigationTog;
    public Toggle PortConfigTog;
    public Toggle ControlTog;
    public GameObject ControlPage;

    [Header("Settings UI")]
    public Toggle settingTog;
    public GameObject settingPanel;
    public Slider leftSlider;
    private TextMeshProUGUI leftSliderText;
    public Slider rightSlider;
    private TextMeshProUGUI rightSliderText;
    public FixedJoystick leftJoy;
    public FixedJoystick rightJoy;
    public Slider volumeSlider;
    private TextMeshProUGUI volumeSliderText;
    public Slider heightSlider;
    private TextMeshProUGUI heightSliderText;


    [Header("Robot Command Buttons")]
    public Button robotModeBtn;
    public Button robotAudioBtn;
    public Button robotVideoBtn;


    [Header("TTS UI")]
    public Toggle TTSToggle;
    public GameObject TTSListObj;
    public GameObject TTSListContent;
    public Button TTSCmdPlayButton;
    public Button TTSCmdPauseButton;
    public Button TTSCmdStopButton;
    private string TTSCmdText;
    private List<Button> TTSBtnList = new List<Button>();

    [Header("Action UI")]
    public Toggle ActionToggle;
    public GameObject ActionListObj;
    public GameObject ActionListContent;
    public Button ActionCmdPlayButton;
    public Button ActionCmdPauseButton;
    public Button ActionCmdStopButton;
    private string ActionCmdText;
    private List<Button> ActionBtnList = new List<Button>();

    private string NavigationCmdText;
    [HideInInspector]
    public string NavigationCmd;

    public Triggers uiinput_triggers = new Triggers();
    public Buttons uiinput_buttons = new Buttons();

    private List<string> defaultTTSOptions = new List<string> ();
    private List<string> defaultActionOptions = new List<string>();
    private List<string> defaultNavigationOptions = new List<string>();

    private bool ttsListNeedsRebuild = true;
    private bool actionListNeedsRebuild = true;
    private bool navigationListNeedsRebuild = true;
    private bool haveLogin =false;
    private bool robotListInitialized;
    //public Button PassthroughBtn;

    private Color ModeBtnDefaultColor;              //#1A2236;
    private Color ModeBtnClickColor;                //#004E63;
    private Color GroupBtnDefaultColor;         //#0D1622
    private Color StartBtnClickColor;           //#00FF17;
    private Color PauseBtnClickColor;           //#FFB020;
    private Color StopBtnClickColor;            //#FF0E00;
    private Color DefaultPrefabTextColor; //#2DE1EB
    private Color ClickPrefabTextColor;   //#FFFFFF
    private Color DefaultPrefabBtnColor; //#132E3F
    private Color ClickPrefabBtnColor;   //#00CFEA

    [Header("leftcam按钮")]
    public Button MOVE_IDX_LEFT_X_ADDBtn;
    public Button MOVE_IDX_LEFT_X_REDUCEBtn;
    public Button MOVE_IDX_LEFT_Y_ADDBtn;
    public Button MOVE_IDX_LEFT_Y_REDUCEBtn;
    public Button SET_LEFT_ROTATE_ANGLE_ADDBtn;
    public Button SET_LEFT_ROTATE_ANGLE_REDUCEBtn;

    [Header("rightcam按钮")]
    public Button MOVE_IDX_RIGHT_X_ADDBtn;
    public Button MOVE_IDX_RIGHT_X_REDUCEBtn;
    public Button MOVE_IDX_RIGHT_Y_ADDBtn;
    public Button MOVE_IDX_RIGHT_Y_REDUCEBtn;
    public Button SET_RIGHT_ROTATE_ANGLEADDBtn;
    public Button SET_RIGHT_ROTATE_ANGLEREDUCEBtn;

    [Header("ServerStatePanel")]
    #region 额外的状态
    public GameObject ServerStatePanel;
    public TextMeshProUGUI _debugwifirtt;
    public TextMeshProUGUI _debugvrrtt;
    public TextMeshProUGUI _debugbattery;
    public TextMeshProUGUI _debugvideortt;

    public TextMeshProUGUI _debugServerRobot;
    public TextMeshProUGUI _debugServerAudio;
    public TextMeshProUGUI _debugServerVideo;
    public TextMeshProUGUI _debugServerHand;

    public GameObject _chargeobj;
    public Transform _percenttrans;
    #endregion


    [Header("机器人模型切换")]
    public Toggle RobotModelChangeTog;
    private TextMeshProUGUI RobotModelText;
    public GameObject RobotV1;
    public GameObject RobotV2;

    private void Awake()
    {
        Instance = this;
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            SaveSliderSettings();
        }
    }

    private void OnApplicationQuit()
    {
        SaveSliderSettings();
    }

    private void OnDestroy()
    {
        SaveSliderSettings();

        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void Start()
    {

#if !IS_ANDROID
        MOVE_IDX_LEFT_X_ADDBtn = Quest_MOVE_IDX_LEFT_X_ADDBtn;
        MOVE_IDX_LEFT_X_REDUCEBtn = Quest_MOVE_IDX_LEFT_X_REDUCEBtn;
        MOVE_IDX_LEFT_Y_ADDBtn = Quest_MOVE_IDX_LEFT_Y_ADDBtn;
        MOVE_IDX_LEFT_Y_REDUCEBtn = Quest_MOVE_IDX_LEFT_Y_REDUCEBtn;
        SET_LEFT_ROTATE_ANGLE_ADDBtn = Quest_SET_LEFT_ROTATE_ANGLE_ADDBtn;
        SET_LEFT_ROTATE_ANGLE_REDUCEBtn = Quest_SET_LEFT_ROTATE_ANGLE_REDUCEBtn;

        MOVE_IDX_RIGHT_X_ADDBtn = Quest_MOVE_IDX_RIGHT_X_ADDBtn;
        MOVE_IDX_RIGHT_X_REDUCEBtn = Quest_MOVE_IDX_RIGHT_X_REDUCEBtn;
        MOVE_IDX_RIGHT_Y_ADDBtn = Quest_MOVE_IDX_RIGHT_Y_ADDBtn;
        MOVE_IDX_RIGHT_Y_REDUCEBtn = Quest_MOVE_IDX_RIGHT_Y_REDUCEBtn;
        SET_RIGHT_ROTATE_ANGLEADDBtn = Quest_SET_RIGHT_ROTATE_ANGLEADDBtn;
        SET_RIGHT_ROTATE_ANGLEREDUCEBtn = Quest_SET_RIGHT_ROTATE_ANGLEREDUCEBtn;

        TTSToggle = Quest_TTSToggle;
        TTSListObj = Quest_TTSListObj;
        TTSListContent = Quest_TTSListContent;
        TTSCmdPlayButton = Quest_TTSCmdPlayButton;
        TTSCmdPauseButton = Quest_TTSCmdPauseButton;
        TTSCmdStopButton = Quest_TTSCmdStopButton;

        ActionToggle = Quest_ActionToggle;
        ActionListObj = Quest_ActionListObj;
        ActionListContent = Quest_ActionListContent;
        ActionCmdPlayButton = Quest_ActionCmdPlayButton;
        ActionCmdPauseButton = Quest_ActionCmdPauseButton;
        ActionCmdStopButton = Quest_ActionCmdStopButton;


        settingPanel = Quest_settingPanel;
        volumeSlider = Quest_volumeSlider;
        if (Quest_settingBtn != null)
        {
            AddPointerClickEvent(Quest_settingBtn,
                () =>{
                    Debug.Log("ison == fales");
                    settingTog.isOn = false;
                });
        }

#else
                settingTog.gameObject.SetActive(false);

#endif

        if (loginBtn != null)
        {
            AddPointerClickEvent(loginBtn, LoginBtnClick);
        }
        if (openserverlist_btn != null)
        {
            AddPointerClickEvent(openserverlist_btn, OnServerListBtnClick);
        }
        if (server1_btn != null)
        {
            AddPointerClickEvent(server1_btn, () => { OnSetServer(server1_btn); });
        }
        if (server2_btn != null)
        {
            AddPointerClickEvent(server2_btn, () => { OnSetServer(server2_btn); });
        }
        if (server3_btn != null)
        {
            AddPointerClickEvent(server3_btn, () => { OnSetServer(server3_btn); });
        }
        if (ip_inputField != null)
        {
            ip_inputField.text = RuntimeServerConfig.serverIp;
        }

        if (islantoggle != null)
        {
            islantoggle.onValueChanged.AddListener((bool ison) =>
            {
                bool changed = RuntimeServerConfig.curislan != ison;
                if (ison)
                {
                    RuntimeServerConfig.curislan = true;
                    islantoggle.transform.Find("Label").GetComponent<Text>().text = "本地连接";
                }
                else
                {
                    RuntimeServerConfig.curislan = false;
                    islantoggle.transform.Find("Label").GetComponent<Text>().text = "远程连接";
                }

                if (changed)
                {
                    RuntimeServerConfig.Save(false);
                }
            });
            islantoggle.isOn = RuntimeServerConfig.curislan;
        }

        if (videoav1_tog != null)
        {
            videoav1_tog.onValueChanged.AddListener((bool ison) =>
            {
                OnVideoDecoderToggleChanged(DecoderFlavor.AV1, ison);
            });
        }


        if (videoh264_tog != null)
        {
            videoh264_tog.onValueChanged.AddListener((bool ison) =>
            {
                OnVideoDecoderToggleChanged(DecoderFlavor.H264, ison);
            });
        }

        if (videohevc_tog != null)
        {
            videohevc_tog.onValueChanged.AddListener((bool ison) =>
            {
                OnVideoDecoderToggleChanged(DecoderFlavor.HEVC, ison);
            });
        }

        DecoderFlavor initialDecoderFlavor = DataManager.Instance != null
            ? DataManager.Instance.videoDecodeMode
            : DataManager.GetSavedVideoDecodeMode(DecoderFlavor.AV1);
        SetVideoDecoderToggleState(initialDecoderFlavor);
        ApplyVideoDecoderSelection();

        if (loginPanel != null) loginPanel.SetActive(true);
        if (selectPanel != null) selectPanel.SetActive(false);
        if (settingPanel != null) settingPanel.SetActive(false);
        if (ServerStatePanel != null) ServerStatePanel.SetActive(false);

#if !IS_ANDROID


        if (LR_slider != null)
        {
            LR_slider.minValue = -10f;
            LR_slider.maxValue = 10f;
            float initialValue = GetSavedSliderValue(LRSliderPrefKey, 0f, LR_slider);
            LR_value = LR_slider.transform.Find("value").GetComponent<TextMeshProUGUI>();
            LR_slider.onValueChanged.AddListener((float value) =>
            {
                LR_value.text = $"{value:F2}";
                SaveSliderValue(LRSliderPrefKey, value);
            });
            LR_slider.value = initialValue;
        }

        if (UD_slider != null)
        {
            UD_slider.minValue = -20f;
            UD_slider.maxValue = 20f;
            float initialValue = GetSavedSliderValue(UDSliderPrefKey, 0f, UD_slider);
            UD_value = UD_slider.transform.Find("value").GetComponent<TextMeshProUGUI>();
            UD_slider.onValueChanged.AddListener((float value) =>
            {
                UD_value.text = $"{value:F2}";
                SaveSliderValue(UDSliderPrefKey, value);
            });
            UD_slider.value = initialValue;
        }

        if (Scale_slider != null)
        {
            if (quardvideoRender == null && VideoPanel != null)
            {
                quardvideoRender = VideoPanel.GetComponentInChildren<Renderer>();
            }
            if (quardvideoRender != null)
            {
                quardvideoMaterial = quardvideoRender.material;
            }

            Scale_slider.minValue = 0;
            Scale_slider.maxValue = 10;
            float initialValue = 6.92f;
            if (quardvideoMaterial != null)
            {
                initialValue = Mathf.Clamp(quardvideoMaterial.GetFloat(PlaneWidth), Scale_slider.minValue, Scale_slider.maxValue);
            }
            initialValue = GetSavedSliderValue(ScaleSliderPrefKey, initialValue, Scale_slider);
            if (quardvideoMaterial != null)
            {
                quardvideoMaterial.SetFloat(PlaneWidth, initialValue);
            }
            Scale_value = Scale_slider.transform.Find("value").GetComponent<TextMeshProUGUI>();
            Scale_slider.onValueChanged.AddListener((float value) =>
            {
                Scale_value.text = $"{value:F2}";
                if (quardvideoMaterial != null)
                {
                    quardvideoMaterial.SetFloat(PlaneWidth, value);
                }
                SaveSliderValue(ScaleSliderPrefKey, value);
            });
            Scale_slider.value = initialValue;

        }

#endif



        if (MOVE_IDX_LEFT_X_ADDBtn != null)
        {
            AddPointerHoldEvent(
                MOVE_IDX_LEFT_X_ADDBtn,
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_LEFT_X = 1f; },
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_LEFT_X = 0f; });
        }

        if (MOVE_IDX_LEFT_X_REDUCEBtn != null)
        {
            AddPointerHoldEvent(
                MOVE_IDX_LEFT_X_REDUCEBtn,
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_LEFT_X = -1f; },
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_LEFT_X = 0f; });
        }


        if (MOVE_IDX_LEFT_Y_ADDBtn != null)
        {
            AddPointerHoldEvent(
                MOVE_IDX_LEFT_Y_ADDBtn,
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_LEFT_Y = 1f; },
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_LEFT_Y = 0f; });
        }

        if (MOVE_IDX_LEFT_Y_REDUCEBtn != null)
        {
            AddPointerHoldEvent(
                MOVE_IDX_LEFT_Y_REDUCEBtn,
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_LEFT_Y = -1f; },
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_LEFT_Y = 0f; });
        }


        if (MOVE_IDX_RIGHT_X_ADDBtn != null)
        {
            AddPointerHoldEvent(
                MOVE_IDX_RIGHT_X_ADDBtn,
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_RIGHT_X = 1f; },
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_RIGHT_X = 0f; });
        }

        if (MOVE_IDX_RIGHT_X_REDUCEBtn != null)
        {
            AddPointerHoldEvent(
                MOVE_IDX_RIGHT_X_REDUCEBtn,
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_RIGHT_X = -1f; },
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_RIGHT_X = 0f; });
        }


        if (MOVE_IDX_RIGHT_Y_ADDBtn != null)
        {
            AddPointerHoldEvent(
                MOVE_IDX_RIGHT_Y_ADDBtn,
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_RIGHT_Y = 1f; },
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_RIGHT_Y = 0f; });
        }

        if (MOVE_IDX_RIGHT_Y_REDUCEBtn != null)
        {
            AddPointerHoldEvent(
                MOVE_IDX_RIGHT_Y_REDUCEBtn,
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_RIGHT_Y = -1f; },
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.MOVE_IDX_RIGHT_Y = 0f; });
        }


        if (SET_LEFT_ROTATE_ANGLE_ADDBtn != null)
        {
            AddPointerHoldEvent(
                SET_LEFT_ROTATE_ANGLE_ADDBtn,
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.SET_LEFT_ROTATE_ANGLE = 1f; },
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.SET_LEFT_ROTATE_ANGLE = 0f; });
        }

        if (SET_LEFT_ROTATE_ANGLE_REDUCEBtn != null)
        {
            AddPointerHoldEvent(
                SET_LEFT_ROTATE_ANGLE_REDUCEBtn,
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.SET_LEFT_ROTATE_ANGLE = -1f; },
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.SET_LEFT_ROTATE_ANGLE = 0f; });
        }


        if (SET_RIGHT_ROTATE_ANGLEADDBtn != null)
        {
            AddPointerHoldEvent(
                SET_RIGHT_ROTATE_ANGLEADDBtn,
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.SET_RIGHT_ROTATE_ANGLE = 1f; },
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.SET_RIGHT_ROTATE_ANGLE = 0f; });
        }

        if (SET_RIGHT_ROTATE_ANGLEREDUCEBtn != null)
        {
            AddPointerHoldEvent(
                SET_RIGHT_ROTATE_ANGLEREDUCEBtn,
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.SET_RIGHT_ROTATE_ANGLE = -1f; },
                () => { if (DataManager.Instance != null && DataManager.Instance.currobotState != null) DataManager.Instance.currobotState.SET_RIGHT_ROTATE_ANGLE = 0f; });
        }

        if(leftSlider != null) 
        {
            leftSlider.minValue = 0;
            leftSlider.maxValue = 1;
            leftSliderText = leftSlider.transform.Find("value").GetComponent<TextMeshProUGUI>();
        }

        if (rightSlider != null)
        {
            rightSlider.minValue = 0;
            rightSlider.maxValue = 1;
            rightSliderText = rightSlider.transform.Find("value").GetComponent<TextMeshProUGUI>();
        }

        if (volumeSlider != null)
        {
            volumeSlider.minValue = 0;
            volumeSlider.maxValue = 1;
            volumeSliderText = volumeSlider.transform.Find("value").GetComponent<TextMeshProUGUI>();
        }

        if (heightSlider != null)
        {
            heightSlider.minValue = -1;
            heightSlider.maxValue = 1;
            heightSliderText = heightSlider.transform.Find("value").GetComponent<TextMeshProUGUI>();
        }

        if (RobotModelChangeTog != null) 
        {
            RobotModelText = RobotModelChangeTog.GetComponentInChildren<TextMeshProUGUI>();
            RobotModelChangeTog.onValueChanged.AddListener((bool ison) =>
            {
                if (ison) 
                {
                    RobotV2.gameObject.SetActive(false);
                    RobotV1.gameObject.SetActive(true);
                    RobotModelText.text = "机器人V1";
                }
                else 
                {
                    RobotV1.gameObject.SetActive(false);
                    RobotV2.gameObject.SetActive(true);
                    RobotModelText.text = "机器人V2";
                }
            });
            RobotModelChangeTog.isOn = true;

        }

        RegisterRobotCommandButtons();
        SetupTTSUI();
        SetupActionUI();
        SetupNavigationUI();
        SetupSettingsToggle();
        SetupHandInputUI();
        SetupIpConfigChangePanel();
        SetupPanelButtons();
        SetAndriodSettingPanel();

        SetupVolumeInput();
        SetupHeightInput();

        GetColor();
        SetVideoPanel(false);
    }

    private static float GetSavedSliderValue(string key, float fallbackValue, Slider slider)
    {
        if (slider == null)
        {
            return fallbackValue;
        }

        float value = PlayerPrefs.GetFloat(key, fallbackValue);
        return Mathf.Clamp(value, slider.minValue, slider.maxValue);
    }

    private static void SaveSliderValue(string key, float value)
    {
        PlayerPrefs.SetFloat(key, value);
    }

    private void SaveSliderSettings()
    {
        if (LR_slider != null)
        {
            SaveSliderValue(LRSliderPrefKey, LR_slider.value);
        }

        if (UD_slider != null)
        {
            SaveSliderValue(UDSliderPrefKey, UD_slider.value);
        }

        if (Scale_slider != null)
        {
            SaveSliderValue(ScaleSliderPrefKey, Scale_slider.value);
        }

        PlayerPrefs.Save();
    }


    private void Update()
    {

#if IS_ANDROID
        UpdateJoystickInputs();
#endif
        if (selectPanel != null && selectPanel.activeInHierarchy)
        {
            timer += Time.deltaTime;
            if (timer >= 10f)
            {
                timer = 0f;
                // 在这里执行你希望每 5 秒触发的逻辑
                NetClient.instance.StartRequest();
            }
        }

        if (Time.unscaledTime >= nextDebugStateUpdateTime)
        {
            nextDebugStateUpdateTime = Time.unscaledTime + debugStateUpdateInterval;
            DebugState();
        }
    }
    public void SetVideoPanel(bool isopen)
    {
        //videoPanel.SetActive(isopen);
        //quardvideoPanel.SetActive(isopen);
        VideoPanel.SetActive(isopen);
    }


#if !IS_ANDROID

    private void LateUpdate()
    {
        //UpdateTurntableSelection();
    }



    private void UpdateTurntableSelection()
    {
        if (turntableSegments == null || turntableSegments.Count == 0) return;

        if ( VideoPanel.activeInHierarchy) 
        {
            if (OVRInputController.instance.RightJoyDown) 
            {
                turntableTouchCount++;
            }

            if(turntableTouchCount % 2 == 1) 
            {
                turntableObj.SetActive(true);
            }
            else 
            {
                if (oldTurntableIndex != -1) 
                {
                    DataManager.Instance.currobotState.TASK = oldTurntableIndex;
                }
                //DebugTextManager.Instance._debugText2.text = "oldTurntableIndex:" + oldTurntableIndex;
                turntableObj.SetActive(false);
                return;
            }


            //Vector2 stick = GetRightStick();
            Vector2 stick = new Vector2(OVRInputController.instance.RightJoy[0],
                OVRInputController.instance.RightJoy[1]);
            if (stick.magnitude < joystickDeadzone)
            {
                // 回中时恢复默认大小
                if (currentTurntableIndex != -1)
                {
                    SetTurntableIndex(-1);
                }
                return;
            }

            float angle = Mathf.Atan2(stick.y, stick.x) * Mathf.Rad2Deg; // -180~180
            if (angle < 0) angle += 360f; // 0~360

            int idx = -1;
            if (angle < 18f || angle >= 306f) idx = 0;           // -36~36（包含 324~360）
            else if (angle < 90f) idx = 1;                      // 36~108
            else if (angle < 162f) idx = 2;                      // 108~180
            else if (angle < 234f) idx = 3;                      // 180~252
            else if (angle < 306f) idx = 4;                      // 252~334

            if (idx != currentTurntableIndex)
            {
                SetTurntableIndex(idx);
            }

        }
        // 读取右手摇杆（显式指定右手控制器，避免未连接时始终为0）
    }
#endif
    private void SetTurntableIndex(int idx)
    {
        currentTurntableIndex = idx;
        for (int i = 0; i < turntableSegments.Count; i++)
        {
            var t = turntableSegments[i];
            if (t == null) continue;
            float scale = (i == idx && idx >= 0) ? turntableHighlightScale : turntableNormalScale;
            t.localScale = Vector3.one * scale;
        }
        if(currentTurntableIndex != -1) 
        {
            oldTurntableIndex = currentTurntableIndex + 1 ;
        }
    }

    private void AddPointerClickEvent(Button button, UnityEngine.Events.UnityAction call)
    {
        if (button == null) return;
        EventTrigger trigger = GetOrCreateEventTrigger(button);
        AddEventTrigger(trigger, EventTriggerType.PointerClick, _ => call());
        AddEventTrigger(trigger, EventTriggerType.Submit, _ => call());
    }

    private void RemovePointerClickEvent(Button button)
    {
        EventTrigger trigger = button.GetComponent<EventTrigger>();
        if (trigger != null)
        {
            Destroy(trigger);
        }
    }

    private void AddPointerHoldEvent(
        Button button,
        Action onHold,
        Action onRelease = null,
        float initialDelaySeconds = 0.2f,
        float repeatIntervalSeconds = 0.05f)
    {
        if (button == null)
        {
            return;
        }

        EventTrigger trigger = GetOrCreateEventTrigger(button);

        AddEventTrigger(trigger, EventTriggerType.PointerDown, _ =>
        {
            StartHoldCoroutine(button, onHold, initialDelaySeconds, repeatIntervalSeconds);
        });
        AddEventTrigger(trigger, EventTriggerType.PointerUp, _ => StopHoldCoroutine(button, onRelease));
        AddEventTrigger(trigger, EventTriggerType.PointerExit, _ => StopHoldCoroutine(button, onRelease));
        AddEventTrigger(trigger, EventTriggerType.Cancel, _ => StopHoldCoroutine(button, onRelease));
    }

    private EventTrigger GetOrCreateEventTrigger(Button button)
    {
        EventTrigger trigger = button.GetComponent<EventTrigger>();
        if (trigger == null)
        {
            trigger = button.gameObject.AddComponent<EventTrigger>();
        }
        if (trigger.triggers == null)
        {
            trigger.triggers = new List<EventTrigger.Entry>();
        }
        return trigger;
    }

    private void AddEventTrigger(EventTrigger trigger, EventTriggerType type, Action<BaseEventData> callback)
    {
        EventTrigger.Entry entry = new EventTrigger.Entry();
        entry.eventID = type;
        entry.callback.AddListener(data => callback(data));
        trigger.triggers.Add(entry);
    }

    private void StartHoldCoroutine(Button button, Action onHold, float initialDelaySeconds, float repeatIntervalSeconds)
    {
        if (button == null || onHold == null)
        {
            return;
        }

        StopHoldCoroutine(button, null);
        holdCoroutines[button] = StartCoroutine(HoldRepeatRoutine(onHold, initialDelaySeconds, repeatIntervalSeconds));
    }

    private void StopHoldCoroutine(Button button, Action onRelease)
    {
        if (button == null)
        {
            return;
        }

        if (holdCoroutines.TryGetValue(button, out Coroutine routine) && routine != null)
        {
            StopCoroutine(routine);
        }

        holdCoroutines.Remove(button);
        onRelease?.Invoke();
    }

    private IEnumerator HoldRepeatRoutine(Action onHold, float initialDelaySeconds, float repeatIntervalSeconds)
    {
        onHold?.Invoke();
        if (initialDelaySeconds > 0f)
        {
            yield return new WaitForSeconds(initialDelaySeconds);
        }

        while (true)
        {
            onHold?.Invoke();
            if (repeatIntervalSeconds > 0f)
            {
                yield return new WaitForSeconds(repeatIntervalSeconds);
            }
            else
            {
                yield return null;
            }
        }
    }

    void OnSetServer(Button trans)
    {
        if (ip_inputField!=null)
        {
            ip_inputField.text = trans.transform.GetComponentInChildren<TextMeshProUGUI>().text;
        }

        LoginBtnClick();
    }


    Vector3 openlistangle = new Vector3(0, 0, -180);
    Vector3 closelistangle = new Vector3(0, 0, -270);
    void OnServerListBtnClick()
    {
        clickindex++;
        if (clickindex % 2 == 0)
        {
            serverlist_page.gameObject.SetActive(false);
            openserverlist_btn.transform.localEulerAngles = closelistangle;
        }
        else
        {
            serverlist_page.gameObject.SetActive(true);
            openserverlist_btn.transform.localEulerAngles = openlistangle;
        }
    }


    public void LoginBtnClick()
    {
        if (loginFlowInProgress)
        {
            return;
        }

        ApplyVideoDecoderSelection();
#if !IS_ANDROID
        if (ip_inputField != null) 
        {
            if (ip_inputField.text != null)
            {
                bool configChanged = RuntimeServerConfig.serverIp != ip_inputField.text;
                if (RuntimeServerConfig.serverIp != ip_inputField.text)
                {
                    RuntimeServerConfig.serverIp = ip_inputField.text;
                }

                if (configChanged)
                {
                    RuntimeServerConfig.Save(false);
                }

                if (RuntimeServerConfig.curislan)
                {
                    loginPanel.SetActive(false);
#if !IS_ANDROID
                    MainCanvas_obj.SetActive(false);
#endif
                    if (settingTog != null)
                    {
                        settingTog.gameObject.SetActive(true);
                        settingTog.isOn = true;
                    }
                    ServerStatePanel.SetActive(true);
                }

                StartCoroutine(CompleteLoginNextFrame(RuntimeServerConfig.curislan, configChanged));
                haveLogin = true;
            }
        }
#endif

#if IS_ANDROID
        if (ip_inputField != null)
        {
            if (ip_inputField.text != null)
            {
                bool configChanged = RuntimeServerConfig.serverIp != ip_inputField.text;
                if (RuntimeServerConfig.serverIp != ip_inputField.text)
                {
                    RuntimeServerConfig.serverIp = ip_inputField.text;
                }

                if (configChanged)
                {
                    RuntimeServerConfig.Save(false);
                }

                if (RuntimeServerConfig.curislan)
                {
                    loginPanel.SetActive(false);
                    settingPanel.gameObject.SetActive(true);
                    ControlTog.isOn = true;
                }

                StartCoroutine(CompleteLoginNextFrame(RuntimeServerConfig.curislan, configChanged));
                haveLogin = true;
            }
        }
#endif


    }

    private IEnumerator CompleteLoginNextFrame(bool isLanMode, bool reloadNetworking)
    {
        loginFlowInProgress = true;
        if (loginBtn != null)
        {
            loginBtn.interactable = false;
        }

        // Let the UI state settle before heavier work like socket rebuilds or decoder restarts.
        yield return null;

        try
        {
            if (reloadNetworking && NetClient.instance != null)
            {
                NetClient.instance.ReloadUdpManagers();
            }

            if (!isLanMode)
            {
                NetClient.instance?.StartRequest();
            }
            else
            {
                DataManager.Instance?.StartSending();
            }
        }
        finally
        {
            loginFlowInProgress = false;
            if (loginBtn != null)
            {
                loginBtn.interactable = true;
            }
        }
    }

    private void ApplyVideoDecoderSelection()
    {
        if (videoav1_tog != null && videoav1_tog.isOn)
        {
            ApplyVideoDecoderFlavor(DecoderFlavor.AV1, restartVideoStream: false);
            return;
        }

        if (videoh264_tog != null && videoh264_tog.isOn)
        {
            ApplyVideoDecoderFlavor(DecoderFlavor.H264, restartVideoStream: false);
            return;
        }

        if (videohevc_tog != null && videohevc_tog.isOn)
        {
            ApplyVideoDecoderFlavor(DecoderFlavor.HEVC, restartVideoStream: false);
            return;
        }
    }

    private void OnVideoDecoderToggleChanged(DecoderFlavor flavor, bool isOn)
    {
        if (suppressVideoDecoderToggleCallbacks || !isOn)
        {
            return;
        }

        SetVideoDecoderToggleState(flavor);
        ApplyVideoDecoderFlavor(flavor, restartVideoStream: true);
    }

    private void SetVideoDecoderToggleState(DecoderFlavor flavor)
    {
        suppressVideoDecoderToggleCallbacks = true;
        try
        {
            videoav1_tog?.SetIsOnWithoutNotify(flavor == DecoderFlavor.AV1);
            videoh264_tog?.SetIsOnWithoutNotify(flavor == DecoderFlavor.H264);
            videohevc_tog?.SetIsOnWithoutNotify(flavor == DecoderFlavor.HEVC);
        }
        finally
        {
            suppressVideoDecoderToggleCallbacks = false;
        }
    }

    private void ApplyVideoDecoderFlavor(DecoderFlavor flavor, bool restartVideoStream)
    {
        if (DataManager.Instance == null)
        {
            return;
        }

        bool changed = DataManager.Instance.SetVideoDecodeMode(flavor, persist: true);

        if (!restartVideoStream || !changed)
        {
            return;
        }

#if UNITY_2023_1_OR_NEWER
        var player = UnityEngine.Object.FindFirstObjectByType<Quest3VideoPlayer.QuestStreamVideoPlayer>();
#else
        var player = FindObjectOfType<Quest3VideoPlayer.QuestStreamVideoPlayer>();
#endif
        player?.RestartStreamIfRunning();
    }

    public void CreateRobotList(ServerRobotList list)
    {
        if (!IsRobotListChanged(list))
        {
            return;
        }

        loginPanel?.SetActive(false);
        selectPanel?.SetActive(true);
        robotListInitialized = true;

        if (list?.list == null || list.list.Length == 0)
        {
            ClearRobotButtons();
            SnapshotRobotList(list);
            return;
        }

        HashSet<string> incomingIds = new HashSet<string>();
        foreach (var item in list.list)
        {
            if (string.IsNullOrEmpty(item.device_id))
            {
                continue;
            }

            incomingIds.Add(item.device_id);
            if (!buttonDict.ContainsKey(item.device_id))
            {
                CreateRobotPrefab(item.name, item.device_id);
            }
            else
            {
                UpdateButtonIfChanged(item.name, item.device_id);
            }
        }

        RemoveMissingRobots(incomingIds);
        SnapshotRobotList(list);
    }

    private void CreateRobotPrefab(string name, string device_id)
    {
        GameObject temp = Instantiate(robotSelect_prefab);
        temp.gameObject.SetActive(true);
        temp.transform.SetParent(robotSelectContent.transform);
        temp.transform.localScale = Vector3.one;
        temp.transform.localPosition = Vector3.zero;
        temp.transform.localEulerAngles = Vector3.zero;
        temp.name = device_id;
        temp.transform.Find("Name").GetComponent<Text>().text = name;
        string tempid = device_id;
        var button = temp.GetComponent<Button>();
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(() => SelectRobot(tempid));
        buttonDict[device_id] = button;
    }

    void UpdateButtonIfChanged(string name, string device_id)
    {
        Button btn = buttonDict[device_id];
        RemovePointerClickEvent(btn);
        Text text = btn.GetComponentInChildren<Text>();
        // **名称变化：更新UI**
        if (text.text != name)
        {
            text.text = name;
        }

        string tempid = device_id;
        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(() => SelectRobot(tempid));
    }

    public void SelectRobot(string device_id)
    {
        selectPanel.SetActive(false);
        settingPanel.SetActive(true);
        
#if !IS_ANDROID
        MainCanvas_obj.SetActive(false);
        ServerStatePanel.SetActive(true);
#endif
        DataManager.Instance.OnSelectRobot(device_id);
    }

    private void RegisterRobotCommandButtons()
    {

        if (robotModeBtn != null)
        {
            robotModeBtn.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(robotModeBtn));
            });

            AddPointerHoldEvent(
                robotModeBtn,
                () => { uiinput_buttons.x = 1; uiinput_buttons.y = 1; uiinput_buttons.a = 1; },
                () => { uiinput_buttons.x = 0; uiinput_buttons.y = 0; uiinput_buttons.a = 0; }
                );
        }

       
        if (robotAudioBtn != null)
        {
            robotAudioBtn.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(robotAudioBtn));
            });

            AddPointerHoldEvent(
                robotAudioBtn,
                () => { uiinput_buttons.x = 1; uiinput_buttons.y = 1; uiinput_buttons.b = 1; },
                () => { uiinput_buttons.x = 0; uiinput_buttons.y = 0; uiinput_buttons.b = 0; }
                );
        }

        if (robotVideoBtn != null)
        {
            robotVideoBtn.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(robotVideoBtn));
            });

            AddPointerHoldEvent(
                robotVideoBtn,
                () => { uiinput_buttons.a = 1; uiinput_buttons.b = 1; uiinput_buttons.x = 1; },
                () => { uiinput_buttons.a = 0; uiinput_buttons.b = 0; uiinput_buttons.x = 0; }
                );
        }
    }

    private void SetupTTSUI()
    {
        //TTSCmdBtnList.Clear();
        if (TTSToggle != null)
        {
            TTSToggle.onValueChanged.AddListener(OnTTSToggleChanged);
            OnTTSToggleChanged(TTSToggle.isOn);
        }

        if (TTSCmdPlayButton != null) 
        {
            TTSCmdPlayButton.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(TTSCmdPlayButton));
                SetTTSCommand("play", TTSCmdText);
            });
        }

        if (TTSCmdPauseButton != null) 
        {
            TTSCmdPauseButton.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(TTSCmdPauseButton));
                SetTTSCommand("interrupt");
            });
        }

        if (TTSCmdStopButton!=null)
        {
            TTSCmdStopButton.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(TTSCmdStopButton));
                SetTTSCommand("stop");
            });
        }
    }

    private IEnumerator ChangeBtnColor(Button btn) 
    {
        Color CurColor = new Color();
        Color TargetColor = new Color();
        if(btn == robotModeBtn || btn == robotAudioBtn || btn == robotVideoBtn) 
        {
            CurColor = ModeBtnDefaultColor;
            TargetColor = ModeBtnClickColor;
        }
        else if( btn == TTSCmdPlayButton || btn == ActionCmdPlayButton || btn == NavigationPlayBtn || btn == NavigationInitBtn) 
        {
            CurColor = GroupBtnDefaultColor;
            TargetColor = StartBtnClickColor;
        }
        else if( btn == TTSCmdPauseButton || btn == ActionCmdPauseButton || btn == NavigationPauseBtn) 
        {
            CurColor = GroupBtnDefaultColor;
            TargetColor = PauseBtnClickColor ;
        }
        else if (btn == TTSCmdStopButton || btn == ActionCmdStopButton || btn == NavigationStopBtn)
        {
            CurColor = GroupBtnDefaultColor;
            TargetColor = StopBtnClickColor;
        }

        btn.transform.GetComponent<Image>().color = TargetColor;
        yield return new WaitForSeconds(0.2f);
        btn.transform.GetComponent<Image>().color = CurColor;

    }



    private void OnTTSToggleChanged(bool isOn)
    {
        if (TTSListObj != null) TTSListObj.SetActive(isOn);
        if (isOn)
        {
            BuildTTSList();
        }
    }

    private void BuildTTSList()
    {
        if (!ttsListNeedsRebuild || TTSListContent == null || robotSelect_prefab == null) return;
        ClearListContent(TTSListContent.transform);
        TTSBtnList.Clear();
        if (defaultTTSOptions == null)
        {
            ttsListNeedsRebuild = false;
            return;
        }
        foreach (var text in defaultTTSOptions)
        {
            if (string.IsNullOrEmpty(text)) continue;
            var item = Instantiate(robotSelect_prefab, TTSListContent.transform);
            item.SetActive(true);
            item.transform.localScale = Vector3.one;
            item.transform.localPosition = Vector3.zero;
            item.transform.localEulerAngles = Vector3.zero;
            item.name = $"TTS_{text}";
            var label = item.transform.Find("Name")?.GetComponent<Text>();
            if (label != null)
            {
                label.text = text;
            }
            var button = item.GetComponent<Button>();
            if (button != null)
            {
                TTSBtnList.Add(button);
                string optionText = text;
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() =>
                {
                    if (DataManager.Instance != null && DataManager.Instance.TTS != null)
                    {
                        TTSToggle.isOn = false;
                        TTSToggle.GetComponentInChildren<TextMeshProUGUI>().text = optionText;
                        TTSCmdText = optionText;
                    }
                    HighlightButton(button, TTSBtnList);
                });
            }
        }
        ResetButtonBGs(TTSBtnList);
        ttsListNeedsRebuild = false;
    }

    private void SetupActionUI()
    {
        if (ActionToggle != null)
        {
            ActionToggle.onValueChanged.AddListener(OnActionToggleChanged);
            OnActionToggleChanged(ActionToggle.isOn);
        }

        if (ActionCmdPlayButton != null) 
        {
            ActionCmdPlayButton.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(ActionCmdPlayButton));
                SetActionCommand("play", ActionCmdText);
            });
        }

        if (ActionCmdPauseButton!=null)
        {
            ActionCmdPauseButton.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(ActionCmdPauseButton));
                SetActionCommand("interrupt");
            });
        }

        if (ActionCmdStopButton != null) 
        {
            ActionCmdStopButton.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(ActionCmdStopButton));
                SetActionCommand("stop");
            });
        }

    }

    private void OnActionToggleChanged(bool isOn)
    {
        if (ActionListObj != null) ActionListObj.SetActive(isOn);
        if (isOn)
        {
            BuildActionList();
        }
    }

    private void BuildActionList()
    {
        if (!actionListNeedsRebuild || ActionListContent == null || robotSelect_prefab == null) return;
        ClearListContent(ActionListContent.transform);
        ActionBtnList.Clear();
        if (defaultActionOptions == null)
        {
            actionListNeedsRebuild = false;
            return;
        }
        foreach (var option in defaultActionOptions)
        {
            if (string.IsNullOrEmpty(option)) continue;
            var item = Instantiate(robotSelect_prefab, ActionListContent.transform);
            item.SetActive(true);
            item.transform.localScale = Vector3.one;
            item.transform.localPosition = Vector3.zero;
            item.transform.localEulerAngles = Vector3.zero;
            item.name = $"Action_{option}";
            var label = item.transform.Find("Name")?.GetComponent<Text>();

            if (label != null)
            {
                label.text = option;
            }
            var button = item.GetComponent<Button>();
            if (button != null)
            {
                string actionName = option;
                ActionBtnList.Add(button);
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() =>
                {
                    if (DataManager.Instance != null && DataManager.Instance.ACTION != null)
                    {
                        ActionToggle.isOn = false;
                        ActionToggle.GetComponentInChildren<TextMeshProUGUI>().text = actionName;
                        ActionCmdText = actionName;
                    }
                    HighlightButton(button, ActionBtnList);
                });
            }
        }
        ResetButtonBGs(ActionBtnList);
        actionListNeedsRebuild = false;
    }

    private void SetupNavigationUI()
    {
        //if (NavigationMode_Tog != null)
        //{
        //    NavigationMode_Tog.onValueChanged.AddListener(OnNavigationToggleChanged);
        //    OnNavigationToggleChanged(NavigationMode_Tog.isOn);
        //}
        if (NavigationMode_Tog != null)
        {
            NavigationModeText = NavigationMode_Tog.GetComponentInChildren<TextMeshProUGUI>();
            
            NavigationMode_Tog.onValueChanged.AddListener((bool ison) => 
            {
                if (ison) 
                {
                    NavigationModeText.text = "导航模式";
                }
                else 
                {
                    NavigationModeText.text = "建图模式";

                }
            });
            NavigationModeText.text = "导航模式";
            NavigationMode_Tog.isOn = true;
        }



        if (NavigationPlayBtn != null)
        {
            NavigationPlayBtn.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(NavigationPlayBtn));
                SetNavigationCommand("play", NavigationCmdText);
            });
        }

        if (NavigationPauseBtn != null)
        {
            NavigationPauseBtn.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(NavigationPauseBtn));
                SetNavigationCommand("interrupt");
            });
        }

        if (NavigationStopBtn != null)
        {
            NavigationStopBtn.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(NavigationStopBtn));
                SetNavigationCommand("stop");
            });
        }

        if (NavigationInitBtn != null)
        {
            NavigationInitBtn.onClick.AddListener(() =>
            {
                StartCoroutine(ChangeBtnColor(NavigationInitBtn));
                SetNavigationCommand("init", NavigationCmdText);
            });
        }
    }

    private void OnNavigationToggleChanged(bool isOn)
    {
        if (NavigationContent != null) NavigationContent.SetActive(isOn);
        if (isOn)
        {
            BuildNavigationList();
        }
    }

    private void BuildNavigationList()
    {
        if (!navigationListNeedsRebuild || NavigationContent == null || robotSelect_prefab == null) return;
        ClearListContent(NavigationContent.transform);
        NavigationBtnList.Clear();
        if (defaultNavigationOptions == null)
        {
            navigationListNeedsRebuild = false;
            return;
        }
        foreach (var option in defaultNavigationOptions)
        {
            if (string.IsNullOrEmpty(option)) continue;
            var item = Instantiate(robotSelect_prefab, NavigationContent.transform);
            item.SetActive(true);
            item.transform.localScale = Vector3.one;
            item.transform.localPosition = Vector3.zero;
            item.transform.localEulerAngles = Vector3.zero;
            item.name = $"Navigation_{option}";
            var label = item.transform.Find("Name")?.GetComponent<Text>();
            if (label != null)
            {
                label.text = option;
            }
            var button = item.GetComponent<Button>();
            if (button != null)
            {
                string navName = option;
                NavigationBtnList.Add(button);
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() =>
                {
                    if (NavigationMode_Tog != null)
                    {
                        NavigationMode_Tog.isOn = false;
                        var toggleLabel = NavigationMode_Tog.GetComponentInChildren<TextMeshProUGUI>();
                        if (toggleLabel != null)
                        {
                            toggleLabel.text = navName;
                        }
                        NavigationCmdText = navName;
                    }
                    HighlightButton(button, NavigationBtnList);
                });
            }
        }
        ResetButtonBGs(NavigationBtnList);
        navigationListNeedsRebuild = false;
    }

    public void UpdateTTSOptions(List<string> newOptions)
    {
        if (newOptions == null) return;
        var normalizedOptions = NormalizeOptions(newOptions);
        if (!HasListChanged(defaultTTSOptions, normalizedOptions)) return;
        defaultTTSOptions = normalizedOptions;
        ttsListNeedsRebuild = true;
        BuildTTSList();
    }

    public void UpdateActionOptions(List<string> newOptions)
    {
        if (newOptions == null) return;
        var normalizedOptions = NormalizeOptions(newOptions);
        if (!HasListChanged(defaultActionOptions, normalizedOptions)) return;
        defaultActionOptions = normalizedOptions;
        actionListNeedsRebuild = true;
        BuildActionList();
    }

    public void UpdateNavigationOptions(List<string> newOptions)
    {
        if (newOptions == null) return;
        var normalizedOptions = NormalizeOptions(newOptions);
        if (!HasListChanged(defaultNavigationOptions, normalizedOptions)) return;
        defaultNavigationOptions = normalizedOptions;
        navigationListNeedsRebuild = true;
        BuildNavigationList();
    }

    private static List<string> NormalizeOptions(List<string> source)
    {
        List<string> result = new List<string>();
        if (source == null) return result;
        foreach (var option in source)
        {
            if (!string.IsNullOrEmpty(option))
            {
                result.Add(option);
            }
        }
        return result;
    }

    private bool HasListChanged(List<string> current, List<string> incoming)
    {
        if (incoming == null) return false;
        if (current == null) return incoming.Count > 0;
        if (current.Count != incoming.Count) return true;
        for (int i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i], incoming[i], StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private void SetupSettingsToggle()
    {
        if (settingTog == null) return;
        settingTog.onValueChanged.AddListener(isOn =>
        {
            if (settingPanel != null)
            {
                settingPanel.SetActive(isOn);
            }
            if (!haveLogin) 
            {
                loginPanel.SetActive(!isOn);
            }
        });
    }

    private void SetupPanelButtons()
    {
        if (VideoParametersBtn != null)
        {
            AddPointerClickEvent(VideoParametersBtn, () => SetUIPanelActive(VideoParametersPanel, true));
        }
        if (VideoParametersCloseBtn != null)
        {
            AddPointerClickEvent(VideoParametersCloseBtn, () => SetUIPanelActive(VideoParametersPanel, false));
        }

        if (PortSettingsBtn != null)
        {
            AddPointerClickEvent(PortSettingsBtn, OpenPortConfigPanel);
        }
        if (portCloseBtn != null)
        {
            AddPointerClickEvent(portCloseBtn, ClosePortConfigPanel);
        }

        if (NavigationBtn != null)
        {
            AddPointerClickEvent(NavigationBtn, OpenPathfindingPanel);
        }
        if (NavigationCloseBtn != null)
        {
            AddPointerClickEvent(NavigationCloseBtn, ClosePathfindingPanel);
        }
    }

    private void SetAndriodSettingPanel() 
    {
        if (ControlTog != null) 
        {
            ControlTog.onValueChanged.AddListener((bool ison) =>
            {
                ControlPage.SetActive(ison);
            });
        }

        if(NavigationTog != null) 
        {
            NavigationTog.onValueChanged.AddListener((bool ison) =>
            {
                if (ison)
                {
                    OpenPathfindingPanel();
                }
                else
                {
                    ClosePathfindingPanel();
                }
            });
        }

        if (PortConfigTog != null) 
        {
            PortConfigTog.onValueChanged.AddListener((bool ison) =>
            {
                if (ison)
                {
                    OpenPortConfigPanel();
                }
                else
                {
                    ClosePortConfigPanel();
                }
            });
        }
    }


    private void SetUIPanelActive(GameObject panel, bool isActive)
    {
        if (panel != null)
        {
            panel.SetActive(isActive);
        }
    }

    private void OpenPortConfigPanel()
    {
        SetUIPanelActive(PortConfigPanel, true);
        RefreshIpConfigInputs();
    }

    private void ClosePortConfigPanel()
    {
        SetUIPanelActive(PortConfigPanel, false);
    }

    private void OpenPathfindingPanel()
    {
        SetUIPanelActive(NavigationPanel, true);

        BuildNavigationList();

    }

    private void ClosePathfindingPanel()
    {
        SetUIPanelActive(NavigationPanel, false);
        if (NavigationMode_Tog != null)
        {
            NavigationMode_Tog.isOn = false;
        }
    }

    private void SetupIpConfigChangePanel()
    {
        if (PortConfigPanel == null)
        {
            return;
        }

        if (portSubmitBtn != null)
        {
            AddPointerClickEvent(portSubmitBtn, OnPortSubmitClicked);
        }

    }


    private void RefreshIpConfigInputs()
    {
        if (localDataPortInput != null)
        {
            localDataPortInput.text = RuntimeServerConfig.curislan ? 
                RuntimeServerConfig.serverDataPort.ToString() : RuntimeServerConfig.clientDataPort.ToString();
        }
        if (remoteDataPortInput != null)
        {
            remoteDataPortInput.text = RuntimeServerConfig.curislan ?
                 RuntimeServerConfig.clientDataPort.ToString(): RuntimeServerConfig.serverDataPort.ToString();
        }

        if (localVideoPortInput != null)
        {
            localVideoPortInput.text = RuntimeServerConfig.curislan ?
                RuntimeServerConfig.serverVideoPort.ToString() : RuntimeServerConfig.clientVideoPort.ToString();
        }

        if (remoteVideoPortInput != null)
        {
            remoteVideoPortInput.text = RuntimeServerConfig.curislan ?
                RuntimeServerConfig.clientVideoPort.ToString() : RuntimeServerConfig.serverVideoPort.ToString();
        }

        if (localAudioPortInput != null)
        {
            localAudioPortInput.text = RuntimeServerConfig.curislan ?
                RuntimeServerConfig.serverAudioPort.ToString() : RuntimeServerConfig.clientAudioPort.ToString();
        }

        if (remoteAudioPortInput != null)
        {
            remoteAudioPortInput.text = RuntimeServerConfig.curislan ?
                RuntimeServerConfig.clientAudioPort.ToString() : RuntimeServerConfig.serverAudioPort.ToString();
        }
    }

    private void OnPortSubmitClicked()
    {
        bool changed = false;

        int currentLocalDataPort = RuntimeServerConfig.curislan ? RuntimeServerConfig.serverDataPort : RuntimeServerConfig.clientDataPort;
        int currentRemoteDataPort = RuntimeServerConfig.curislan ? RuntimeServerConfig.clientDataPort : RuntimeServerConfig.serverDataPort;
        int currentLocalVideoPort = RuntimeServerConfig.curislan ? RuntimeServerConfig.serverVideoPort : RuntimeServerConfig.clientVideoPort;
        int currentRemoteVideoPort = RuntimeServerConfig.curislan ? RuntimeServerConfig.clientVideoPort : RuntimeServerConfig.serverVideoPort;
        int currentLocalAudioPort = RuntimeServerConfig.curislan ? RuntimeServerConfig.serverAudioPort : RuntimeServerConfig.clientAudioPort;
        int currentRemoteAudioPort = RuntimeServerConfig.curislan ? RuntimeServerConfig.clientAudioPort : RuntimeServerConfig.serverAudioPort;

        if (TryReadPort(localDataPortInput, currentLocalDataPort, out int localDataPort))
        {
            if (RuntimeServerConfig.curislan)
            {
                if (localDataPort != RuntimeServerConfig.serverDataPort)
                {
                    RuntimeServerConfig.serverDataPort = localDataPort;
                    changed = true;
                }
            }
            else if (localDataPort != RuntimeServerConfig.clientDataPort)
            {
                RuntimeServerConfig.clientDataPort = localDataPort;
                changed = true;
            }
        }

        if (TryReadPort(remoteDataPortInput, currentRemoteDataPort, out int remoteDataPort))
        {
            if (RuntimeServerConfig.curislan)
            {
                if (remoteDataPort != RuntimeServerConfig.clientDataPort)
                {
                    RuntimeServerConfig.clientDataPort = remoteDataPort;
                    changed = true;
                }
            }
            else if (remoteDataPort != RuntimeServerConfig.serverDataPort)
            {
                RuntimeServerConfig.serverDataPort = remoteDataPort;
                changed = true;
            }
        }

        if (TryReadPort(localVideoPortInput, currentLocalVideoPort, out int localVideoPort))
        {
            if (RuntimeServerConfig.curislan)
            {
                if (localVideoPort != RuntimeServerConfig.serverVideoPort)
                {
                    RuntimeServerConfig.serverVideoPort = localVideoPort;
                    changed = true;
                }
            }
            else if (localVideoPort != RuntimeServerConfig.clientVideoPort)
            {
                RuntimeServerConfig.clientVideoPort = localVideoPort;
                changed = true;
            }
        }

        if (TryReadPort(remoteVideoPortInput, currentRemoteVideoPort, out int remoteVideoPort))
        {
            if (RuntimeServerConfig.curislan)
            {
                if (remoteVideoPort != RuntimeServerConfig.clientVideoPort)
                {
                    RuntimeServerConfig.clientVideoPort = remoteVideoPort;
                    changed = true;
                }
            }
            else if (remoteVideoPort != RuntimeServerConfig.serverVideoPort)
            {
                RuntimeServerConfig.serverVideoPort = remoteVideoPort;
                changed = true;
            }
        }

        if (TryReadPort(localAudioPortInput, currentLocalAudioPort, out int localAudioPort))
        {
            if (RuntimeServerConfig.curislan)
            {
                if (localAudioPort != RuntimeServerConfig.serverAudioPort)
                {
                    RuntimeServerConfig.serverAudioPort = localAudioPort;
                    changed = true;
                }
            }
            else if (localAudioPort != RuntimeServerConfig.clientAudioPort)
            {
                RuntimeServerConfig.clientAudioPort = localAudioPort;
                changed = true;
            }
        }

        if (TryReadPort(remoteAudioPortInput, currentRemoteAudioPort, out int remoteAudioPort))
        {
            if (RuntimeServerConfig.curislan)
            {
                if (remoteAudioPort != RuntimeServerConfig.clientAudioPort)
                {
                    RuntimeServerConfig.clientAudioPort = remoteAudioPort;
                    changed = true;
                }
            }
            else if (remoteAudioPort != RuntimeServerConfig.serverAudioPort)
            {
                RuntimeServerConfig.serverAudioPort = remoteAudioPort;
                changed = true;
            }
        }

        if (changed)
        {
            RuntimeServerConfig.Save();
        }

        RefreshIpConfigInputs();
    }

    private static bool TryReadPort(TMP_InputField input, int fallback, out int value)
    {
        value = fallback;
        if (input == null)
        {
            return false;
        }

        string text = input.text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!int.TryParse(text, out int parsed) || parsed < 1 || parsed > 65535)
        {
            Debug.LogWarning($"[UIManager] Invalid port '{text}' for {input.gameObject.name}. Keeping {fallback}.");
            return false;
        }

        value = parsed;
        return true;
    }

    private void SetupVolumeInput() 
    {
        if (volumeSlider != null)
        {
            volumeSlider.onValueChanged.AddListener(OnVolumeSliderChanged);
            OnVolumeSliderChanged(volumeSlider.value);
        }
    }

    void OnVolumeSliderChanged(float value) 
    {
        if (DataManager.Instance != null)
        {
            DataManager.Instance.currobotState.SET_AUDIO_VOLUME = value;
            volumeSliderText.text = value.ToString("F2");
           
        }
    }

    private void SetupHeightInput() 
    {
        if (heightSlider != null)
        {
            heightSlider.onValueChanged.AddListener(OnHeightSliderChanged);
            OnHeightSliderChanged(heightSlider.value);
        }
    }

    void OnHeightSliderChanged(float value) 
    {
        if (DataManager.Instance != null)
        {
            heightSliderText.text = value.ToString("F2");
        }
    }

    private void SetupHandInputUI()
    {
        if (leftSlider != null)
        {
            leftSlider.onValueChanged.AddListener(OnLeftSliderChanged);
            leftSlider.value = 0;
            OnLeftSliderChanged(leftSlider.value);
        }

        if (rightSlider != null)
        {
            rightSlider.onValueChanged.AddListener(OnRightSliderChanged);
            rightSlider.value = 0;
            OnRightSliderChanged(rightSlider.value);
        }
    }


    private void SetTTSCommand(string cmd,string? name = null)
    {
        if (DataManager.Instance != null && DataManager.Instance.TTS != null)
        {
            DataManager.Instance.TTS.cmd = cmd;
        }
        if (name != null) 
        {
            DataManager.Instance.TTS.text = TTSCmdText;
        }
    }
    

    private void SetActionCommand(string cmd, string? name = null)
    {
        if (DataManager.Instance != null && DataManager.Instance.ACTION != null)
        {
            DataManager.Instance.ACTION.cmd = cmd;
        }
        if (name != null)
        {
            DataManager.Instance.ACTION.name = ActionCmdText;
        }
    }

    private void SetNavigationCommand(string cmd, string? name = null)
    {
        if (string.IsNullOrEmpty(cmd))
        {
            return;
        }
        NavigationCmd = cmd;
        if (name != null)
        {
            NavigationCmdText = name;
        }
    }

    private void OnLeftSliderChanged(float value)
    {
        if (DataManager.Instance != null)
        {
            DataManager.Instance.LeftHandAngle = value;
            uiinput_triggers.l = value;
            leftSliderText.text = value.ToString("F2");
        }
    }

    private void OnRightSliderChanged(float value)
    {
        if (DataManager.Instance != null)
        {
            DataManager.Instance.RightHandAngle = value;
            uiinput_triggers.r = value;
            rightSliderText.text = value.ToString("F2");
        }
    }

    private void UpdateJoystickInputs()
    {
        if (DataManager.Instance == null)
        {
            return;
        }

        if (leftJoy != null )
        {
            DataManager.Instance.latestJoyL[0] = leftJoy.Horizontal;
            DataManager.Instance.latestJoyL[1] = leftJoy.Vertical;

            if (leftJoy.Horizontal >0.05f || leftJoy.Horizontal < -0.05f || leftJoy.Vertical > 0.05f || leftJoy.Vertical < -0.05f)
            {
                uiinput_buttons.leftjoy = 1;
            }
            else 
            {
                uiinput_buttons.leftjoy = 0;
            }
        }

        if (heightSlider != null)
        {
            if (heightSlider.value >= 0.05f||heightSlider.value <= -0.05f)
            {

                DataManager.Instance.latestJoyR[1] = heightSlider.value;
                uiinput_buttons.x = 1;
                DataManager.Instance.latestJoyR[0] = rightJoy.Horizontal;
            }
            else
            {

                if (rightJoy != null)
                {
                    if (rightJoy.Horizontal > 0.05f || rightJoy.Horizontal < -0.05f || rightJoy.Vertical > 0.05f || rightJoy.Vertical < -0.05f)
                    {
                        uiinput_buttons.rightjoy = 1;
                    }
                    else
                    {
                        uiinput_buttons.rightjoy = 0;
                    }
                    DataManager.Instance.latestJoyR[0] = rightJoy.Horizontal;
                    DataManager.Instance.latestJoyR[1] = rightJoy.Vertical;
                }
            }
        }

    }

    private bool IsRobotListChanged(ServerRobotList list)
    {
        if (list?.list == null || list.list.Length == 0)
        {
            return !robotListInitialized || lastRobotSnapshot.Count != 0;
        }

        HashSet<string> seenIds = new HashSet<string>();
        foreach (var robot in list.list)
        {
            if (robot == null || string.IsNullOrEmpty(robot.device_id))
            {
                continue;
            }

            if (!seenIds.Add(robot.device_id))
            {
                continue;
            }

            string incomingName = robot.name ?? string.Empty;
            if (!lastRobotSnapshot.TryGetValue(robot.device_id, out var cachedName))
            {
                return true;
            }

            if (!string.Equals(cachedName, incomingName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return seenIds.Count != lastRobotSnapshot.Count;
    }

    private void SnapshotRobotList(ServerRobotList list)
    {
        lastRobotSnapshot.Clear();
        if (list?.list == null)
        {
            return;
        }

        foreach (var robot in list.list)
        {
            if (robot == null || string.IsNullOrEmpty(robot.device_id))
            {
                continue;
            }

            string normalizedName = robot.name ?? string.Empty;
            if (!lastRobotSnapshot.ContainsKey(robot.device_id))
            {
                lastRobotSnapshot.Add(robot.device_id, normalizedName);
            }
            else
            {
                lastRobotSnapshot[robot.device_id] = normalizedName;
            }
        }
    }

    private void RemoveMissingRobots(HashSet<string> incomingIds)
    {
        if (incomingIds == null || incomingIds.Count == 0)
        {
            ClearRobotButtons();
            return;
        }

        List<string> toRemove = null;
        foreach (var pair in buttonDict)
        {
            if (!incomingIds.Contains(pair.Key))
            {
                if (pair.Value != null)
                {
                    Destroy(pair.Value.gameObject);
                }
                toRemove ??= new List<string>();
                toRemove.Add(pair.Key);
            }
        }

        if (toRemove != null)
        {
            foreach (var id in toRemove)
            {
                buttonDict.Remove(id);
            }
        }
    }

    private void ClearRobotButtons()
    {
        foreach (var pair in buttonDict)
        {
            if (pair.Value != null)
            {
                Destroy(pair.Value.gameObject);
            }
        }

        buttonDict.Clear();
    }

    private void ClearListContent(Transform parent)
    {
        if (parent == null) return;
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Destroy(parent.GetChild(i).gameObject);
        }
    }

    private void HighlightButton(Button targetButton, List<Button> buttonGroup)
    {
        if (targetButton == null || buttonGroup == null) return;
        foreach (var btn in buttonGroup)
        {
            SetButtonBGState(btn, btn == targetButton);
        }
    }

    private void ResetButtonBGs(List<Button> buttons)
    {
        if (buttons == null) return;
        foreach (var btn in buttons)
        {
            SetButtonBGState(btn, false);
        }
    }

    private void SetButtonBGState(Button button, bool isActive)
    {
        if (button == null) return;
        Text nametext = button.transform.Find("Name").GetComponent<Text>();
        
        //if (bg != null)
        //{
        //    bg.gameObject.SetActive(isActive);
        //}
        if (isActive) 
        {
            if (nametext != null) 
            {
                nametext.color = ClickPrefabTextColor;
            }
            button.transform.GetComponent<Image>().color = ClickPrefabBtnColor;
        }
        else 
        {
            if (nametext != null)
            {
                nametext.color = DefaultPrefabTextColor; ;
            }
            button.transform.GetComponent<Image>().color = DefaultPrefabBtnColor;

        }
    }

    void DebugState()
    {

        var stateData = DataManager.Instance != null ? DataManager.Instance.robotStateMessage : null;
        if (stateData == null)
        {
            return;
        }

        string ServeraudioMode = FormatEnumValue<AudioMode>(stateData.data.audio_mode);
        string ServerrobotMode = FormatEnumValue<RobotMode>(stateData.data.robot_mode);
        string ServervideoMode = FormatEnumValue<VideoMode>(stateData.data.video_mode);
        string Serverhandmode = FormatEnumValue<HandMode>(stateData.data.hand_mode);
        string Serverheight = stateData.data.robot_height.HasValue ? stateData.data.robot_height.Value.ToString("F3") : "null";


        if (_debugServerAudio != null)
        {
            _debugServerAudio.text = "AudioMode:" + ServeraudioMode;

        }
        if (_debugServerHand != null)
        {
            _debugServerHand.text = "HandMode:" + Serverhandmode;

        }
        if (_debugServerRobot != null)
        {
            _debugServerRobot.text = "RobotMode:" + ServerrobotMode;

        }
        if (_debugServerVideo != null)
        {
            _debugServerVideo.text = "VideoMode:" + ServervideoMode;
        }


        DebugTextManager.Instance._debugServertext.text =
            $"Server: Robot Mode = {ServerrobotMode} ; Audio Mode = {ServeraudioMode} ;  Video Mode = {ServervideoMode} ; HandMode = {Serverhandmode} ; Height = {Serverheight:F2}";

        if (_debugvrrtt != null)
        {
            long? nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long? vrrttMs = nowMs - stateData.data.vr_ts;
            //1770379837174
            //1770379924123
            if (vrrttMs < 0)
            {
                vrrttMs = 0;
            }
            _debugvrrtt.text = vrrttMs.ToString();
        }

        if (_debugwifirtt != null)
        {
            _debugwifirtt.text = stateData.data.wifi.ToString() + "db";
        }

        if (_debugvideortt != null)
        {
            _debugvideortt.text = stateData.data.video_rtt_ms.ToString() + "ms";
        }

        if (_debugbattery != null)
        {
            _debugbattery.text = $"{stateData.data.battery_percent}%";
        }

        if (_percenttrans != null)
        {
            if (_batteryPercentRect == null)
            {
                _batteryPercentRect = _percenttrans as RectTransform;
            }
            if (_batteryPercentImage == null)
            {
                _batteryPercentImage = _percenttrans.GetComponent<Image>();
            }

            if (_batteryPercentRect != null)
            {
                if (!_batteryBaseHeightCached)
                {
                    _batteryBaseHeight = _batteryPercentRect.rect.height;
                    if (_batteryBaseHeight <= 0f)
                    {
                        _batteryBaseHeight = _batteryPercentRect.sizeDelta.y;
                    }
                    if (_batteryBaseHeight <= 0f)
                    {
                        _batteryBaseHeight = 1f;
                    }
                    _batteryBaseHeightCached = true;
                }

                if (stateData.data.battery_percent != null) 
                {
                    float percent = Mathf.Clamp((int)stateData.data.battery_percent, 0, 100) / 100f;
                    Vector2 size = _batteryPercentRect.sizeDelta;
                    size.y = _batteryBaseHeight * percent;
                    _batteryPercentRect.sizeDelta = size;
                }
            }

            if (_batteryPercentImage != null)
            {
                if (stateData.data.battery_percent > 60)
                {
                    _batteryPercentImage.color = _batteryColorHigh;
                }
                else if (stateData.data.battery_percent > 25)
                {
                    _batteryPercentImage.color = _batteryColorMid;
                }
                else
                {
                    _batteryPercentImage.color = _batteryColorLow;
                }
            }
        }

        if (_chargeobj != null)
        {
            _chargeobj.SetActive(string.Equals(stateData.data.battery_status, "charging", StringComparison.OrdinalIgnoreCase));
        }

    }


    private static string FormatEnumValue<T>(int value) where T : struct
    {
        Type enumType = typeof(T);
        if (enumType.IsEnum && Enum.IsDefined(enumType, value))
        {
            return Enum.GetName(enumType, value);
        }

        return $"Unknown({value})";
    }

    /// <summary>
    /// 将16进制的颜色转换成0-1
    /// </summary>
    private void GetColor()
    {
        ModeBtnDefaultColor = HexToColor("#1A2236");
        ModeBtnClickColor = HexToColor("#004E63");
        GroupBtnDefaultColor = HexToColor("#0D1622");
        StartBtnClickColor = HexToColor("#00FF17");
        PauseBtnClickColor = HexToColor("#FFB020");
        StopBtnClickColor = HexToColor("#FF0E00");

        DefaultPrefabTextColor = HexToColor("#2DE1EB");
        ClickPrefabTextColor = HexToColor("#FFFFFF");

        DefaultPrefabBtnColor = HexToColor("#132E3F");
        ClickPrefabBtnColor = HexToColor("#00CFEA");
        _batteryColorHigh = HexToColor("#16F913");
        _batteryColorMid = HexToColor("#F9C613");
        _batteryColorLow = HexToColor("#F91B13");

    }



    public static Color HexToColor(string hex)
    {
        // 移除#号（兼容不带#的输入）
        hex = hex.TrimStart('#');

        // 解析RGB分量（十六进制转十进制）
        int r = int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
        int g = int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
        int b = int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);

        // 转换为0-1的浮点数并返回Color
        return new Color(r / 255f, g / 255f, b / 255f, 1.0f);
    }


}

