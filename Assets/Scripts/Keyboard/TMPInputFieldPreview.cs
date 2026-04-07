using System.Collections.Generic;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
public class TMPInputFieldPreview : MonoBehaviour
{

    [SerializeField]

    public TMP_InputField TMP_TestInputField;
    public TMP_InputField TMP_IpInputField;
    private FloatingKeyboardPreview _FloatingKeyboardPreview;
    // Start is called once before the first execution of Update after the MonoBehaviour is created

#if IS_ANDROID
    void Start()
    {
        
        _FloatingKeyboardPreview = GetComponent<FloatingKeyboardPreview>();
        if (TMP_TestInputField != null) 
        {
            TMP_TestInputField.onSelect.AddListener(_ => { _FloatingKeyboardPreview.OnInputSelected(TMP_TestInputField); });
        }
        if (TMP_IpInputField != null) 
        {
            TMP_IpInputField.onSelect.AddListener(_ => { _FloatingKeyboardPreview.OnInputSelected(TMP_IpInputField); });
        }
    }

    // Update is called once per frame
    void Update()
    {
        
    }
#endif
}
