using UnityEngine;
#if !IS_ANDROID
using UnityEngine.XR;

public class OVRInputController : MonoBehaviour
{
    public static OVRInputController instance;

    [HideInInspector]
    public bool LHT;
    [HideInInspector]
    public bool RHT;
    [HideInInspector]
    public bool LIT;
    [HideInInspector]
    public bool RIT;
    [HideInInspector]
    public bool AButton;
    [HideInInspector]
    public bool BButton;
    [HideInInspector]
    public bool XButton;
    [HideInInspector]
    public bool YButton;
    [HideInInspector]
    public bool LeftJoyButton;
    [HideInInspector]
    public bool RightJoyButton;
    [HideInInspector]
    public bool menuButtonDown;
    [HideInInspector]
    public bool menuButton;
    [HideInInspector]
    public bool AButtonDown;
    [HideInInspector]
    public bool BButtonDown;
    [HideInInspector]
    public bool RightJoyDown;
    [HideInInspector]
    public bool LeftJoyDown;
    [HideInInspector]
    public float[] LeftJoy = new float[2] { 0, 0 };
    [HideInInspector]
    public float[] RightJoy = new float[2] { 0, 0 };

    [HideInInspector]
    public Triggers triggers = new Triggers();
    [HideInInspector]

    public Triggers gris = new Triggers();
    [HideInInspector]

    public Buttons buttons = new Buttons();

    private void Awake()
    {
        instance = this;
    }
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {

        LIT = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
        LHT = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);

        RIT = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
        RHT = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);

        AButton = OVRInput.Get(OVRInput.Button.One);
        BButton = OVRInput.Get(OVRInput.Button.Two);
        XButton = OVRInput.Get(OVRInput.Button.Three);
        YButton = OVRInput.Get(OVRInput.Button.Four);

        menuButtonDown = OVRInput.GetDown(OVRInput.Button.Start);
        menuButton = OVRInput.Get(OVRInput.Button.Start);

        AButtonDown = OVRInput.GetDown(OVRInput.Button.One);
        BButtonDown = OVRInput.GetDown(OVRInput.Button.Two);

        LeftJoy[0] = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch).x;
        LeftJoy[1] = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch).y;
        RightJoy[0] = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).x;
        RightJoy[1] = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).y;

        RightJoyDown = OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch);
        RightJoyButton = OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch);

        LeftJoyDown = OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch);
        LeftJoyButton = OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch);


        triggers.l = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
        triggers.r = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);

        gris.l = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.LTouch);
        gris.r = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch);

        buttons.a = AButton ? 1 : 0;
        buttons.b = BButton ? 1 : 0;
        buttons.x = XButton ? 1 : 0;
        buttons.y = YButton ? 1 : 0;
        buttons.leftjoy = LeftJoyButton ? 1 : 0;
        buttons.rightjoy = RightJoyButton ? 1 : 0;

        DataManager.Instance.latestJoyL[0] = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch).x;
        DataManager.Instance.latestJoyL[1] = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch).y;
        DataManager.Instance.latestJoyR[0] = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).x;
        DataManager.Instance.latestJoyR[1] = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).y;

    }



    // ==========================================================
    //                           Õð¶¯·´À¡
    // ==========================================================

    public void TriggerHapticFeedback()
    {
        InputDevices.GetDeviceAtXRNode(XRNode.LeftHand).SendHapticImpulse(0, 0.5f, 0.2f);
        InputDevices.GetDeviceAtXRNode(XRNode.RightHand).SendHapticImpulse(0, 0.5f, 0.2f);
    }


    public bool IsPressHandTriggers()
    {
        return LHT && RHT && !(LIT || RIT);
    }

    public bool NoPressTriggers() 
    {
        return !(LHT || RHT || LIT || RIT);
    }

    public bool IsPressFourTriggers()
    {
        return RHT&&LHT&&LIT&&RIT;
    }


    private float lhtPressTime = 0f;
    private float rhtPressTime = 0f;

    private bool IsLITLongPressed(float requiredTime)
    {
        lhtPressTime += Time.deltaTime;
        return lhtPressTime >= requiredTime;
    }

    private bool IsBITLongPressed(float requiredTime)
    {
        rhtPressTime += Time.deltaTime;
        return rhtPressTime >= requiredTime;
    }


    private float allTriggersPressTime = 0f;
    private bool AreAllTriggersLongPressed(float requiredTime)
    {
        allTriggersPressTime += Time.deltaTime;
        return allTriggersPressTime >= requiredTime;
    }

    private void ResetAllTriggersTimer()
    {
        allTriggersPressTime = 0f;
    }

    public bool RobotStateChangeClick()
    {
        return XButton && YButton && AButton && !BButton;
    }

    public bool AudioChangeClick()
    {
        return XButton && YButton && BButton && !AButton;
    }

    public bool VideoChangeClick()
    {
        return !YButton && BButton && AButton && XButton;
    }
}
#endif