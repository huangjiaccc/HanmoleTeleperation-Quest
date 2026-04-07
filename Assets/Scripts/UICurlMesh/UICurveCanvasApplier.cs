using UnityEngine;
using UnityEngine.UI;

[ExecuteAlways]
[DisallowMultipleComponent]
public class UICurveCanvasApplier : MonoBehaviour
{
    [Header("Curve Settings")]
    [Min(0.01f)]
    [SerializeField] private float radius = 1633.8f;
    [SerializeField] private bool invert = true;
    [Range(1, 128)]
    [SerializeField] private int horizontalSegments = 64;

    [Header("Apply")]
    [SerializeField] private bool includeInactive = true;
    [SerializeField] private bool applyOnEnable = true;

    private void OnEnable()
    {
        if (applyOnEnable)
        {
            Apply();
        }
    }

    private void OnValidate()
    {
        Apply();
    }

    public void Apply()
    {
        var graphics = GetComponentsInChildren<Graphic>(includeInactive);
        if (graphics == null || graphics.Length == 0)
        {
            return;
        }

        float safeRadius = Mathf.Max(0.01f, radius);
        int safeSegments = Mathf.Clamp(horizontalSegments, 1, 128);

        foreach (var graphic in graphics)
        {
            if (graphic == null)
            {
                continue;
            }

            var effect = graphic.GetComponent<UICurveMeshEffect>();
            if (effect == null)
            {
                effect = graphic.gameObject.AddComponent<UICurveMeshEffect>();
            }

            effect.Radius = safeRadius;
            effect.Invert = invert;
            effect.HorizontalSegments = safeSegments;
            effect.enabled = true;
        }
    }
}
