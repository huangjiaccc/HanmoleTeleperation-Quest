using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class CurvedUiRaycaster : BaseRaycaster
{
    private static readonly List<RaycastResult> ScratchResults = new List<RaycastResult>(32);
    private static readonly BindingFlags RayFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [SerializeField] private Camera raycastCamera;
    [SerializeField] private Collider targetCollider;
    [SerializeField] private GraphicRaycaster targetRaycaster;
    [SerializeField] private Camera uiCamera;
    [SerializeField] private float maxDistance = 30f;

    public override Camera eventCamera => raycastCamera;

    public void SetTargets(Collider collider, GraphicRaycaster raycaster, Camera canvasCamera)
    {
        targetCollider = collider;
        targetRaycaster = raycaster;
        uiCamera = canvasCamera;
        if (raycastCamera == null)
        {
            raycastCamera = Camera.main != null ? Camera.main : canvasCamera;
        }
    }

    public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
    {
        if (targetCollider == null || targetRaycaster == null || uiCamera == null)
        {
            return;
        }

        if (!TryGetWorldRay(eventData, out Ray ray))
        {
            return;
        }

        if (!targetCollider.Raycast(ray, out RaycastHit hit, maxDistance))
        {
            return;
        }

        Vector2 uv = hit.textureCoord;
        Vector2 screenPos = new Vector2(uv.x * uiCamera.pixelWidth, uv.y * uiCamera.pixelHeight);

        Vector2 savedPos = eventData.position;
        eventData.position = screenPos;

        ScratchResults.Clear();
        targetRaycaster.Raycast(eventData, ScratchResults);
        foreach (var result in ScratchResults)
        {
            RaycastResult adjusted = result;
            adjusted.worldPosition = hit.point;
            adjusted.worldNormal = hit.normal;
            adjusted.distance = hit.distance;
            adjusted.screenPosition = screenPos;
            resultAppendList.Add(adjusted);
        }

        eventData.position = savedPos;
    }

    private bool TryGetWorldRay(PointerEventData eventData, out Ray ray)
    {
        ray = default;
        if (eventData == null)
        {
            return false;
        }

        PropertyInfo rayProp = eventData.GetType().GetProperty("worldSpaceRay", RayFlags);
        if (rayProp != null && rayProp.PropertyType == typeof(Ray))
        {
            try
            {
                ray = (Ray)rayProp.GetValue(eventData);
                return true;
            }
            catch { }
        }

        if (raycastCamera != null)
        {
            ray = raycastCamera.ScreenPointToRay(eventData.position);
            return true;
        }

        return false;
    }
}
