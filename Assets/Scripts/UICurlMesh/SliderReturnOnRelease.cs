using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class SliderReturnOnRelease : MonoBehaviour,
    IPointerDownHandler, IPointerUpHandler
{
    public Slider slider;

    private float originValue;

    void Awake()
    {
        if (!slider) slider = GetComponent<Slider>();
        slider.value = 0;
        originValue = slider.value;   // 记录原点
    }

    // 按下：正常拖动
    public void OnPointerDown(PointerEventData eventData)
    {
        // 不需要做任何事
    }

    // 松开：回到原点
    public void OnPointerUp(PointerEventData eventData)
    {
        slider.value = originValue;
        UIManager.Instance.uiinput_buttons.x = 0;
    }
}
