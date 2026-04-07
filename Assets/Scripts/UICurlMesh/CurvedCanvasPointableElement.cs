#if !IS_ANDROID
using Oculus.Interaction;
using UnityEngine;

[DisallowMultipleComponent]
public class CurvedCanvasPointableElement : PointableElement
{
    [SerializeField] private Transform surfaceTransform;
    [SerializeField] private Transform canvasTransform;
    [SerializeField] private float radius = 1000f;
    [SerializeField] private bool invert = true;

    public void Configure(Transform surface, Transform canvas, float curveRadius, bool curveInvert, PointableCanvas forwardElement)
    {
        surfaceTransform = surface;
        canvasTransform = canvas;
        radius = curveRadius;
        invert = curveInvert;

        if (forwardElement != null)
        {
            InjectOptionalForwardElement(forwardElement);
        }
    }

    public override void ProcessPointerEvent(PointerEvent evt)
    {
        if (surfaceTransform == null || canvasTransform == null)
        {
            base.ProcessPointerEvent(evt);
            return;
        }

        Vector3 transformedPosition = CurvedToCanvasWorld(evt.Pose.position);
        Pose transformedPose = new Pose(transformedPosition, evt.Pose.rotation);
        base.ProcessPointerEvent(new PointerEvent(evt.Identifier, evt.Type, transformedPose, evt.Data));
    }

    private Vector3 CurvedToCanvasWorld(Vector3 worldPoint)
    {
        Vector3 local = surfaceTransform.InverseTransformPoint(worldPoint);
        float safeRadius = Mathf.Max(0.01f, radius);
        float sign = invert ? -1f : 1f;

        float sin = Mathf.Clamp(local.x / safeRadius, -1f, 1f);
        float cos = 1f - (local.z / (safeRadius * sign));
        cos = Mathf.Clamp(cos, -1f, 1f);

        float angle = Mathf.Atan2(sin, cos);
        float flatX = angle * safeRadius;
        float flatY = local.y;

        Vector3 canvasLocal = new Vector3(flatX, flatY, 0f);
        return canvasTransform.TransformPoint(canvasLocal);
    }
}
#endif