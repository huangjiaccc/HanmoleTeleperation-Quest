using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

[DisallowMultipleComponent]
public class OVRVirtualKeyboardInputFieldTextHandlerExtral :
#if !IS_ANDROID
    OVRVirtualKeyboard.AbstractTextHandler
#elif IS_ANDROID
    MonoBehaviour
#endif
{
    [SerializeField]
    private List<TMP_InputField> inputFields = new();
    private readonly HashSet<TMP_InputField> _registeredFields = new();
    private TMP_InputField _activeField;

    public override Action<string> OnTextChanged { get; set; }

    public override string Text => _activeField ? _activeField.text : string.Empty;

    public override bool SubmitOnEnter =>
        _activeField && _activeField.lineType != TMP_InputField.LineType.MultiLineNewline;

    public override bool IsFocused => _activeField && _activeField.isFocused;
#if !IS_ANDROID

    public TMP_InputField InputField
    {
        get => _activeField;
        set => SetActiveField(value);
    }

    private void Awake()
    {
        DiscoverInputFields();
    }

    private void OnEnable()
    {
        DiscoverInputFields();
    }

    private void OnDisable()
    {
        if (_activeField)
        {
            _activeField.onValueChanged.RemoveListener(ProxyOnValueChanged);
        }
    }

    public void AssignInputFields(IEnumerable<TMP_InputField> fields)
    {
        inputFields.Clear();
        if (fields != null)
        {
            foreach (var field in fields)
            {
                if (field != null && !inputFields.Contains(field))
                {
                    inputFields.Add(field);
                }
            }
        }

        DiscoverInputFields();
    }

    public override void Submit()
    {
        if (_activeField)
        {
            _activeField.onEndEdit.Invoke(_activeField.text);
        }
    }

    public override void AppendText(string s)
    {
        if (!_activeField)
        {
            return;
        }

        _activeField.text += s;
        MoveTextEnd();
    }

    public override void ApplyBackspace()
    {
        if (!_activeField || string.IsNullOrEmpty(_activeField.text))
        {
            return;
        }

        _activeField.text = _activeField.text.Substring(0, _activeField.text.Length - 1);
        MoveTextEnd();
    }

    public override void MoveTextEnd()
    {
        if (_activeField)
        {
            _activeField.MoveTextEnd(false);
        }
    }

    private void DiscoverInputFields()
    {
        _registeredFields.Clear();

        foreach (var field in inputFields)
        {
            RegisterField(field);
        }

        if (_registeredFields.Count == 0)
        {
            RegisterField(GetComponent<TMP_InputField>());
        }

        if (_registeredFields.Count == 0)
        {
            foreach (var field in GetComponentsInChildren<TMP_InputField>(true))
            {
                RegisterField(field);
            }
        }

        if (_activeField == null)
        {
            SetActiveField(_registeredFields.FirstOrDefault());
        }
    }

    private void RegisterField(TMP_InputField field)
    {
        if (field == null || !_registeredFields.Add(field))
        {
            return;
        }

        field.readOnly = true;

        var listener = field.gameObject.GetComponent<InputFieldListener>();
        if (listener == null)
        {
            listener = field.gameObject.AddComponent<InputFieldListener>();
        }
        listener.Initialize(this, field);
    }

    private void SetActiveField(TMP_InputField field)
    {
        if (_activeField == field)
        {
            return;
        }

        if (_activeField)
        {
            _activeField.onValueChanged.RemoveListener(ProxyOnValueChanged);
        }

        _activeField = field;

        if (_activeField)
        {
            if (!_registeredFields.Contains(_activeField))
            {
                RegisterField(_activeField);
            }

            _activeField.onValueChanged.AddListener(ProxyOnValueChanged);
            OnTextChanged?.Invoke(Text);
        }
    }

    private void OnInputFieldSelected(TMP_InputField field)
    {
        SetActiveField(field);
        if (!IsFocused && _activeField)
        {
            _activeField.ActivateInputField();
        }
    }

    private void ProxyOnValueChanged(string arg0)
    {
        OnTextChanged?.Invoke(arg0);
    }

    private class InputFieldListener : MonoBehaviour, IPointerClickHandler, ISelectHandler
    {
        private OVRVirtualKeyboardInputFieldTextHandlerExtral _handler;
        private TMP_InputField _field;

        public void Initialize(OVRVirtualKeyboardInputFieldTextHandlerExtral handler, TMP_InputField field)
        {
            _handler = handler;
            _field = field;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            _handler?.OnInputFieldSelected(_field);
        }

        public void OnSelect(BaseEventData eventData)
        {
            _handler?.OnInputFieldSelected(_field);
        }
    }
#endif
}
