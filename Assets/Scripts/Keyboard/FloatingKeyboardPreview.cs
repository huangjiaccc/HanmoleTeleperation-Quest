using UnityEngine;
using TMPro;

public class FloatingKeyboardPreview : MonoBehaviour
{
    [Header("悬浮预览条")]
    [SerializeField]
    private RectTransform previewBar;

    [Header("文字显示")]
    public TextMeshProUGUI previewText;

#if IS_ANDROID
    private TMP_InputField curField;
    private bool wasVisible = false;


    private void Start()
    {
        previewBar.gameObject.SetActive(false);
    }
    void Update()
    {
        if (TouchScreenKeyboard.visible)
        {
            wasVisible = true;

            // 显示条
            if (!previewBar.gameObject.activeSelf)
                previewBar.gameObject.SetActive(true);

            previewBar.anchoredPosition =
                new Vector2(0, 0);

            // 同步文本
            if (curField != null)
                previewText.text = curField.text;
        }
        else
        {
            if (wasVisible)
            {
                wasVisible = false;
                previewBar.gameObject.SetActive(false);
            }
        }
    }

    /// 激活（供 InputField 的 OnSelect 调用）
    public void OnInputSelected(TMP_InputField field)
    {
        curField = field;
    }
#endif
}
