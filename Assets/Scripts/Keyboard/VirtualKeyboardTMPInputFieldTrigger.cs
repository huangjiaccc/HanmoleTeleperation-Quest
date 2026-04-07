using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

[DisallowMultipleComponent]
[RequireComponent(typeof(TMP_InputField))]
public sealed class VirtualKeyboardTMPInputFieldTrigger : OVRVirtualKeyboard.AbstractTextHandler, IPointerClickHandler, ISelectHandler
{
    [Header("Virtual Keyboard")]
    [SerializeField] private OVRVirtualKeyboard virtualKeyboard;
    [SerializeField] private OVRVirtualKeyboard.KeyboardPosition keyboardPosition = OVRVirtualKeyboard.KeyboardPosition.Far;
    [SerializeField] private TMP_InputField targetInputField;
    [SerializeField] private float keyboardShowDelay = 0.1f;

    private static VirtualKeyboardTMPInputFieldTrigger activeTrigger;

    private Coroutine hideCoroutine;
    private Coroutine showCoroutine;
    private bool isKeyboardReady;
    private bool isFieldListenerRegistered;
    private bool isKeyboardListenerRegistered;

    public override Action<string> OnTextChanged { get; set; }

    public override string Text => targetInputField ? targetInputField.text : string.Empty;

    public override bool SubmitOnEnter =>
        targetInputField && targetInputField.lineType != TMP_InputField.LineType.MultiLineNewline;

    public override bool IsFocused => targetInputField && targetInputField.isFocused;

    private void Reset()
    {
        targetInputField = GetComponent<TMP_InputField>();
        keyboardShowDelay = 0.1f;
    }

    private void Awake()
    {
        ResolveInputField();
    }

    private void OnEnable()
    {
        ResolveInputField();
        RegisterInputFieldListener();
        EnsureKeyboardReference();
        RegisterKeyboardListeners();
    }

    private void Start()
    {
        if (targetInputField == null)
        {
            LogError("TMP_InputField is missing.");
            return;
        }

        targetInputField.readOnly = true;
        isKeyboardReady = true;

        if (virtualKeyboard == null)
        {
            LogError("OVRVirtualKeyboard is missing.");
            return;
        }

        if (virtualKeyboard.gameObject.activeInHierarchy)
        {
            virtualKeyboard.gameObject.SetActive(false);
        }

        virtualKeyboard.UseSuggestedLocation(keyboardPosition);
    }

    private void OnDisable()
    {
        if (activeTrigger == this)
        {
            HideVirtualKeyboardInternal(clearActiveTrigger: true);
        }

        UnregisterInputFieldListener();
        UnregisterKeyboardListeners();
    }

    private void OnDestroy()
    {
        if (activeTrigger == this)
        {
            activeTrigger = null;
        }
    }

    public override void Submit()
    {
        if (!targetInputField)
        {
            return;
        }

        targetInputField.onSubmit?.Invoke(targetInputField.text);
        targetInputField.onEndEdit?.Invoke(targetInputField.text);
    }

    public override void AppendText(string s)
    {
        if (!targetInputField)
        {
            return;
        }

        targetInputField.text += s;
        MoveTextEnd();
    }

    public override void ApplyBackspace()
    {
        if (!targetInputField || string.IsNullOrEmpty(targetInputField.text))
        {
            return;
        }

        targetInputField.text = targetInputField.text.Substring(0, targetInputField.text.Length - 1);
        MoveTextEnd();
    }

    public override void MoveTextEnd()
    {
        if (!targetInputField)
        {
            return;
        }

        targetInputField.MoveTextEnd(false);
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        HandleInputSelected();
    }

    public void OnSelect(BaseEventData eventData)
    {
        HandleInputSelected();
    }

    public void HideVirtualKeyboard()
    {
        if (!IsActiveTrigger() && activeTrigger != null)
        {
            return;
        }

        HideVirtualKeyboardInternal(clearActiveTrigger: true);
    }

    public void ScheduleHideKeyboard()
    {
        if (hideCoroutine != null)
        {
            StopCoroutine(hideCoroutine);
        }

        hideCoroutine = StartCoroutine(HideKeyboardWithDelay());
    }

    private void Update()
    {
        if (!IsKeyboardVisible() || !IsActiveTrigger())
        {
            return;
        }

        if (OVRInput.GetDown(OVRInput.Button.Two))
        {
            Log("Hide keyboard from controller input.");
            HideVirtualKeyboard();
        }
    }

    private void ResolveInputField()
    {
        if (targetInputField == null)
        {
            targetInputField = GetComponent<TMP_InputField>();
        }
    }

    private void EnsureKeyboardReference()
    {
        if (virtualKeyboard == null)
        {
            virtualKeyboard = FindObjectOfType<OVRVirtualKeyboard>();
        }
    }

    private void RegisterInputFieldListener()
    {
        if (targetInputField == null || isFieldListenerRegistered)
        {
            return;
        }

        targetInputField.onValueChanged.AddListener(OnInputValueChanged);
        isFieldListenerRegistered = true;
    }

    private void UnregisterInputFieldListener()
    {
        if (targetInputField == null || !isFieldListenerRegistered)
        {
            return;
        }

        targetInputField.onValueChanged.RemoveListener(OnInputValueChanged);
        isFieldListenerRegistered = false;
    }

    private void RegisterKeyboardListeners()
    {
        if (virtualKeyboard == null || isKeyboardListenerRegistered)
        {
            return;
        }

        virtualKeyboard.CommitTextEvent.AddListener(OnKeyboardCommitText);
        virtualKeyboard.BackspaceEvent.AddListener(OnKeyboardBackspace);
        virtualKeyboard.EnterEvent.AddListener(OnKeyboardEnter);
        isKeyboardListenerRegistered = true;
    }

    private void UnregisterKeyboardListeners()
    {
        if (virtualKeyboard == null || !isKeyboardListenerRegistered)
        {
            return;
        }

        virtualKeyboard.CommitTextEvent.RemoveListener(OnKeyboardCommitText);
        virtualKeyboard.BackspaceEvent.RemoveListener(OnKeyboardBackspace);
        virtualKeyboard.EnterEvent.RemoveListener(OnKeyboardEnter);
        isKeyboardListenerRegistered = false;
    }

    private void HandleInputSelected()
    {
        if (!isKeyboardReady)
        {
            return;
        }

        if (!BindToKeyboard())
        {
            return;
        }

        if (IsKeyboardVisible())
        {
            Log($"Switch active TMP input: {targetInputField.gameObject.name}");
            return;
        }

        ShowVirtualKeyboard();
    }

    private bool BindToKeyboard()
    {
        ResolveInputField();
        EnsureKeyboardReference();
        RegisterKeyboardListeners();

        if (targetInputField == null)
        {
            LogError("Bind failed: TMP_InputField is missing.");
            return false;
        }

        if (virtualKeyboard == null)
        {
            LogError("Bind failed: OVRVirtualKeyboard is missing.");
            return false;
        }

        activeTrigger = this;
        virtualKeyboard.TextHandler = this;
        targetInputField.ActivateInputField();
        MoveTextEnd();
        return true;
    }

    private void ShowVirtualKeyboard()
    {
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

        if (!BindToKeyboard())
        {
            showCoroutine = null;
            yield break;
        }

        if (!virtualKeyboard.gameObject.activeInHierarchy)
        {
            virtualKeyboard.gameObject.SetActive(true);
        }

        virtualKeyboard.UseSuggestedLocation(keyboardPosition);
        virtualKeyboard.InputEnabled = true;
        Log("Virtual keyboard shown for TMP input.");
        showCoroutine = null;
    }

    private IEnumerator HideKeyboardWithDelay()
    {
        yield return new WaitForSeconds(0.1f);
        HideVirtualKeyboard();
        hideCoroutine = null;
    }

    private void HideVirtualKeyboardInternal(bool clearActiveTrigger)
    {
        if (showCoroutine != null)
        {
            StopCoroutine(showCoroutine);
            showCoroutine = null;
        }

        if (hideCoroutine != null)
        {
            StopCoroutine(hideCoroutine);
            hideCoroutine = null;
        }

        if (virtualKeyboard != null && virtualKeyboard.gameObject.activeInHierarchy)
        {
            virtualKeyboard.gameObject.SetActive(false);
            Log("Virtual keyboard hidden.");
        }

        if (clearActiveTrigger && activeTrigger == this)
        {
            activeTrigger = null;
        }
    }

    private bool IsKeyboardVisible()
    {
        return virtualKeyboard != null && virtualKeyboard.gameObject.activeInHierarchy;
    }

    private bool IsActiveTrigger()
    {
        return activeTrigger == this;
    }

    private void OnInputValueChanged(string value)
    {
        OnTextChanged?.Invoke(value);
    }

    private void OnKeyboardCommitText(string text)
    {
        if (!IsActiveTrigger())
        {
            return;
        }

        Log($"Keyboard commit: {text}");
    }

    private void OnKeyboardBackspace()
    {
        if (!IsActiveTrigger())
        {
            return;
        }

        Log("Keyboard backspace.");
    }

    private void OnKeyboardEnter()
    {
        if (!IsActiveTrigger())
        {
            return;
        }

        Log("Keyboard enter.");

        if (targetInputField != null &&
            targetInputField.lineType != TMP_InputField.LineType.MultiLineNewline)
        {
            HideVirtualKeyboard();
        }
    }

    private void Log(string message)
    {
        Debug.Log($"[VirtualKeyboardTMP] {message}", this);
    }

    private void LogError(string message)
    {
        Debug.LogError($"[VirtualKeyboardTMP] {message}", this);
    }
}
