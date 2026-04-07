using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

public class VirtualKeyboardStableTrigger : MonoBehaviour, IPointerClickHandler, ISelectHandler
{
    [Header("虚拟键盘配置")]
#if !IS_ANDROID
    public OVRVirtualKeyboard virtualKeyboard;
    public OVRVirtualKeyboard.KeyboardPosition keyboardPosition = OVRVirtualKeyboard.KeyboardPosition.Far;
    private OVRVirtualKeyboardInputFieldTextHandlerExtral textHandler;
    private static VirtualKeyboardStableTrigger activeTrigger;
#endif
    public TMP_InputField targetInputField;
    public float keyboardShowDelay = 0.1f;

    private bool isKeyboardReady = false;
    private bool isAdjustingPosition = false;
    private Coroutine hideCoroutine;
    private Coroutine showCoroutine;

    void Start()
    {
        Log("VirtualKeyboardStableTrigger 开始初始化");

        if (targetInputField == null)
        {
            targetInputField = GetComponent<TMP_InputField>();
        }

        if (targetInputField == null)
        {
            LogError("未找到目标 InputField");
            return;
        }

        targetInputField.readOnly = true;

#if !IS_ANDROID
        EnsureKeyboardReference();
        InitializeTextHandler();

        if (virtualKeyboard == null)
        {
            LogError("未找到 OVRVirtualKeyboard");
            return;
        }

        if (virtualKeyboard.gameObject.activeInHierarchy)
        {
            virtualKeyboard.gameObject.SetActive(false);
        }

        virtualKeyboard.CommitTextEvent.AddListener(OnKeyboardCommitText);
        virtualKeyboard.BackspaceEvent.AddListener(OnKeyboardBackspace);
        virtualKeyboard.EnterEvent.AddListener(OnKeyboardEnter);
        virtualKeyboard.UseSuggestedLocation(keyboardPosition);
#endif

        isKeyboardReady = true;
        Log("虚拟键盘触发器初始化完成");
    }

#if !IS_ANDROID
    private void EnsureKeyboardReference()
    {
        if (virtualKeyboard == null)
        {
            virtualKeyboard = FindObjectOfType<OVRVirtualKeyboard>();
        }
    }

    private void InitializeTextHandler()
    {
        if (targetInputField == null)
        {
            return;
        }

        textHandler = GetComponent<OVRVirtualKeyboardInputFieldTextHandlerExtral>();
        if (textHandler == null)
        {
            textHandler = gameObject.AddComponent<OVRVirtualKeyboardInputFieldTextHandlerExtral>();
        }

        textHandler.InputField = targetInputField;
    }

    private bool BindCurrentInputFieldToKeyboard()
    {
        EnsureKeyboardReference();

        if (targetInputField == null)
        {
            targetInputField = GetComponent<TMP_InputField>();
        }

        if (targetInputField == null)
        {
            LogError("绑定失败：targetInputField 为空");
            return false;
        }

        if (textHandler == null)
        {
            InitializeTextHandler();
        }

        if (virtualKeyboard == null || textHandler == null)
        {
            LogError("绑定失败：virtualKeyboard 或 textHandler 为空");
            return false;
        }

        activeTrigger = this;
        textHandler.InputField = targetInputField;
        virtualKeyboard.TextHandler = textHandler;
        targetInputField.ActivateInputField();
        textHandler.MoveTextEnd();
        return true;
    }

    private bool IsKeyboardVisible()
    {
        return virtualKeyboard != null && virtualKeyboard.gameObject.activeInHierarchy;
    }

    private bool IsActiveTrigger()
    {
        return activeTrigger == this;
    }

    private TMP_InputField GetCurrentInputField()
    {
        return textHandler != null ? textHandler.InputField : targetInputField;
    }
#endif

    public void OnPointerClick(PointerEventData eventData)
    {
        HandleInputSelected();
    }

    public void OnSelect(BaseEventData eventData)
    {
        HandleInputSelected();
    }

    private void HandleInputSelected()
    {
        if (!isKeyboardReady)
        {
            return;
        }

#if !IS_ANDROID
        if (!BindCurrentInputFieldToKeyboard())
        {
            return;
        }

        if (IsKeyboardVisible())
        {
            Log($"切换当前输入框: {targetInputField.gameObject.name}");
            return;
        }
#endif

        ShowVirtualKeyboard();
    }

    private void ShowVirtualKeyboard()
    {
        Log("调起虚拟键盘");

        if (hideCoroutine != null)
        {
            StopCoroutine(hideCoroutine);
            hideCoroutine = null;
        }

        if (showCoroutine != null)
        {
            StopCoroutine(showCoroutine);
        }

        showCoroutine = StartCoroutine(ShowVirtualKeyboardWithDelay());
    }

    private IEnumerator ShowVirtualKeyboardWithDelay()
    {
        yield return new WaitForSeconds(keyboardShowDelay);
#if !IS_ANDROID
        if (virtualKeyboard != null)
        {
            if (!BindCurrentInputFieldToKeyboard())
            {
                yield break;
            }

            if (!virtualKeyboard.gameObject.activeInHierarchy)
            {
                virtualKeyboard.gameObject.SetActive(true);
            }

            virtualKeyboard.UseSuggestedLocation(keyboardPosition);
            virtualKeyboard.InputEnabled = true;

            Log("虚拟键盘已显示");
        }
#endif
        showCoroutine = null;
    }

    private void OnKeyboardCommitText(string text)
    {
#if !IS_ANDROID
        if (!IsActiveTrigger())
        {
            return;
        }
#endif
        Log($"键盘提交文本: {text}");
    }

    private void OnKeyboardBackspace()
    {
#if !IS_ANDROID
        if (!IsActiveTrigger())
        {
            return;
        }
#endif
        Log("键盘按下退格");
    }

    private void OnKeyboardEnter()
    {
#if !IS_ANDROID
        if (!IsActiveTrigger())
        {
            return;
        }
#endif
        Log("键盘按下回车");

        TMP_InputField currentField = targetInputField;
#if !IS_ANDROID
        currentField = GetCurrentInputField();
#endif

        if (currentField != null && currentField.lineType != TMP_InputField.LineType.MultiLineNewline)
        {
            HideVirtualKeyboard();
        }
    }

    public void HideVirtualKeyboard()
    {
#if !IS_ANDROID
        if (!IsActiveTrigger() && activeTrigger != null)
        {
            return;
        }

        if (virtualKeyboard != null && virtualKeyboard.gameObject.activeInHierarchy)
        {
            virtualKeyboard.gameObject.SetActive(false);
            isAdjustingPosition = false;
            Log("虚拟键盘已隐藏");
        }

        if (activeTrigger == this)
        {
            activeTrigger = null;
        }
#endif
    }

    public void ScheduleHideKeyboard()
    {
        if (hideCoroutine != null)
        {
            StopCoroutine(hideCoroutine);
        }
        hideCoroutine = StartCoroutine(HideKeyboardWithDelay());
    }

    private IEnumerator HideKeyboardWithDelay()
    {
        yield return new WaitForSeconds(0.1f);
        HideVirtualKeyboard();
        hideCoroutine = null;
    }

    void Update()
    {
        CheckForExternalClicks();
    }

    private void CheckForExternalClicks()
    {
#if !IS_ANDROID
        if (!IsKeyboardVisible() || !IsActiveTrigger() || isAdjustingPosition)
        {
            return;
        }
#endif

        CheckControllerInput();
    }

    private void CheckControllerInput()
    {
#if !IS_ANDROID
        if (!IsActiveTrigger())
        {
            return;
        }

        if (OVRInput.GetDown(OVRInput.Button.Two) && !isAdjustingPosition)
        {
            Log("控制器B按钮按下，隐藏键盘");
            HideVirtualKeyboard();
        }
#endif
    }

    private void Log(string message)
    {
        Debug.Log($"[VirtualKeyboard] {message}", this);
    }

    private void LogError(string message)
    {
        Debug.LogError($"[VirtualKeyboard] {message}", this);
    }

    void OnDestroy()
    {
#if !IS_ANDROID
        if (virtualKeyboard != null)
        {
            virtualKeyboard.CommitTextEvent.RemoveListener(OnKeyboardCommitText);
            virtualKeyboard.BackspaceEvent.RemoveListener(OnKeyboardBackspace);
            virtualKeyboard.EnterEvent.RemoveListener(OnKeyboardEnter);
        }

        if (activeTrigger == this)
        {
            activeTrigger = null;
        }
#endif
    }
}
