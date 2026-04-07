/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[ExecuteAlways]
[RequireComponent(typeof(Graphic))]
public class UICurveMeshEffect : BaseMeshEffect
{
    public enum HorizontalUvMode
    {
        LinearArc = 0,
        PerspectiveCompensated = 1,
    }

    [Min(0.01f)]
    public float Radius = 1200f;
    public bool Invert = false;
    [Range(1, 128)]
    public int HorizontalSegments = 32;
    public HorizontalUvMode UvMode = HorizontalUvMode.LinearArc;
    [Range(45f, 89f)]
    public float PerspectiveEdgeClampDegrees = 85f;

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive() || Radius <= 0.01f)
        {
            return;
        }
        var sourceVerts = new List<UIVertex>();
        vh.GetUIVertexStream(sourceVerts);

        if (sourceVerts.Count == 0)
        {
            return;
        }

        // Build a segmented quad so the curve is visible even for RawImage (which is a single quad by default).
        var rect = graphic.rectTransform.rect;
        float xMin = rect.xMin;
        float xMax = rect.xMax;
        float yMin = rect.yMin;
        float yMax = rect.yMax;
        float centerX = rect.center.x;
        float sign = Invert ? -1f : 1f;

        Vector2 uvMin = sourceVerts[0].uv0;
        Vector2 uvMax = sourceVerts[0].uv0;
        for (int i = 1; i < sourceVerts.Count; i++)
        {
            var uv = sourceVerts[i].uv0;
            uvMin = Vector2.Min(uvMin, uv);
            uvMax = Vector2.Max(uvMax, uv);
        }

        int segments = Mathf.Max(1, HorizontalSegments);
        int vertCount = (segments + 1) * 2;

        vh.Clear();

        Color32 color = graphic.color;
        float leftAngle = (xMin - centerX) / Radius;
        float rightAngle = (xMax - centerX) / Radius;
        float clampRad = Mathf.Deg2Rad * Mathf.Clamp(PerspectiveEdgeClampDegrees, 45f, 89f);
        leftAngle = Mathf.Clamp(leftAngle, -clampRad, clampRad);
        rightAngle = Mathf.Clamp(rightAngle, -clampRad, clampRad);
        float leftTan = Mathf.Tan(leftAngle);
        float rightTan = Mathf.Tan(rightAngle);
        float tanSpan = rightTan - leftTan;

        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            float x = Mathf.Lerp(xMin, xMax, t);

            float localX = x - centerX;
            float angle = localX / Radius;
            float curvedX = Mathf.Sin(angle) * Radius + centerX;
            float curvedZ = (Radius * (1f - Mathf.Cos(angle))) * sign;

            float uNormalized = t;
            if (UvMode == HorizontalUvMode.PerspectiveCompensated && Mathf.Abs(tanSpan) > 1e-6f)
            {
                float clampedAngle = Mathf.Clamp(angle, -clampRad, clampRad);
                float tanAngle = Mathf.Tan(clampedAngle);
                uNormalized = Mathf.Clamp01((tanAngle - leftTan) / tanSpan);
            }

            float u = Mathf.Lerp(uvMin.x, uvMax.x, uNormalized);

            var vBottom = UIVertex.simpleVert;
            vBottom.color = color;
            vBottom.position = new Vector3(curvedX, yMin, curvedZ);
            vBottom.uv0 = new Vector2(u, uvMin.y);
            vh.AddVert(vBottom);

            var vTop = UIVertex.simpleVert;
            vTop.color = color;
            vTop.position = new Vector3(curvedX, yMax, curvedZ);
            vTop.uv0 = new Vector2(u, uvMax.y);
            vh.AddVert(vTop);
        }

        for (int i = 0; i < segments; i++)
        {
            int i0 = i * 2;
            int i1 = i0 + 1;
            int i2 = i0 + 2;
            int i3 = i0 + 3;

            vh.AddTriangle(i0, i1, i3);
            vh.AddTriangle(i0, i3, i2);
        }
    }
}
