Shader "Unlit/Multiview Stereo Masked"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _SrcRectLeft("SrcRectLeft", Vector) = (0,0,1,1)
        _SrcRectRight("SrcRectRight", Vector) = (0,0,1,1)
        _FlipX ("Flip X", Float) = 0
        _FlipY ("Flip Y", Float) = 0
        _StartAngle ("Start Angle", Range(0,1)) = 0
        _VisibleWidth ("Visible Diameter", Range(0,1)) = 0.52
        _MaskRadiusY ("Mask Fill (Top/Bottom)", Range(0,1)) = 0.51
        _MaskRadiusX ("Mask Fill (Left/Right)", Range(0,1)) = 0.92
        _FillAmount ("Fill Amount", Range(0,1)) = 1
        _FillFeather ("Fill Feather", Range(0,0.2)) = 0.02
        _SeamPadding ("Seam Padding (Pixels)", Range(0,3)) = 0
        _RevealAxis ("Reveal Axis", Vector) = (0,1,0,0)
        _MaskLeft ("Mask Left Offset", Range(0,0.5)) = 0
        _MaskRight ("Mask Right Offset", Range(0,0.5)) = 0
        _MaskBottom ("Mask Bottom Offset", Range(0,0.5)) = 0
        _MaskTop ("Mask Top Offset", Range(0,0.5)) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        Cull Front

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float2 pos : TEXCOORD1;
                float3 dir : TEXCOORD2;
                float4 rect : TEXCOORD3;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;

            float4 _SrcRectLeft;
            float4 _SrcRectRight;
            float _FlipX;
            float _FlipY;
            float _StartAngle;
            float _VisibleWidth;
            float _MaskRadiusY;
            float _MaskRadiusX;
            float _FillAmount;
            float _FillFeather;
            float _SeamPadding;
            float4 _RevealAxis;
            float _MaskLeft;
            float _MaskRight;
            float _MaskBottom;
            float _MaskTop;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);

                float4 srcRect = lerp(_SrcRectLeft, _SrcRectRight, unity_StereoEyeIndex);

                float2 baseUv = TRANSFORM_TEX(v.uv, _MainTex);
                if (_FlipX > 0.5)
                {
                    baseUv.x = 1.0 - baseUv.x;
                }
                if (_FlipY > 0.5)
                {
                    baseUv.y = 1.0 - baseUv.y;
                }

                o.pos = baseUv;
                o.uv = (baseUv * srcRect.zw) + srcRect.xy;
                o.dir = v.vertex.xyz;
                o.rect = srcRect;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 baseUv = i.pos;

                float4 srcRect = i.rect;
                float t = saturate(_VisibleWidth) * 0.5;
                float f = saturate(_FillAmount);
                float3 axis = _RevealAxis.xyz;
                float axisLen = max(length(axis), 1e-5);
                axis /= axisLen;
                float3 dir = normalize(i.dir);
                float dotAxis = dot(dir, axis);
                float threshold = lerp(1.0, -1.0, t);
                clip(dotAxis - threshold);

                float3 ref = (abs(axis.y) < 0.999) ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
                float3 axisX = normalize(cross(ref, axis));
                float3 axisZ = cross(axis, axisX);
                float angle = _StartAngle * (2.0 * UNITY_PI);
                float sinAngle = sin(angle);
                float cosAngle = cos(angle);
                float3 axisXr = axisX * cosAngle + axisZ * sinAngle;
                float3 axisZr = -axisX * sinAngle + axisZ * cosAngle;

                float3 p = dir - (axis * dotAxis);
                float rMax = sqrt(max(1.0 - (threshold * threshold), 1e-5));
                float2 disk = float2(dot(p, axisXr), dot(p, axisZr)) / rMax;

                float dx2 = disk.x * disk.x;
                float dy2 = disk.y * disk.y;
                float sum = dx2 + dy2;
                float diff = dx2 - dy2;
                float sqrtTerm = sqrt(max(4.0 - 4.0 * sum + diff * diff, 0.0));
                float sTerm = 2.0 - sqrtTerm;
                float sx = sqrt(max((sTerm + diff) * 0.5, 0.0));
                float sy = sqrt(max((sTerm - diff) * 0.5, 0.0));
                sx = (disk.x < 0.0) ? -sx : sx;
                sy = (disk.y < 0.0) ? -sy : sy;
                float2 square01 = float2(sx, sy) * 0.5 + 0.5;

                float visibleHeight = saturate(_MaskRadiusY);
                float halfBlack = (1.0 - visibleHeight) * 0.5;
                float bottom = saturate(halfBlack + _MaskBottom);
                float top = saturate(1.0 - halfBlack - _MaskTop);
                float bottomClamped = min(bottom, top);
                float topClamped = max(bottom, top);
                float invHeight = max(topClamped - bottomClamped, 1e-5);
                float maskedY = saturate((square01.y - bottomClamped) / invHeight);

                float visibleWidth = saturate(_MaskRadiusX);
                float halfBlackX = (1.0 - visibleWidth) * 0.5;
                float left = saturate(halfBlackX + _MaskLeft);
                float right = saturate(1.0 - halfBlackX - _MaskRight);
                float leftClamped = min(left, right);
                float rightClamped = max(left, right);
                float invWidth = max(rightClamped - leftClamped, 1e-5);
                float maskedX = saturate((square01.x - leftClamped) / invWidth);

                float2 aaEdge = fwidth(square01);
                float edgeX = smoothstep(leftClamped, leftClamped + aaEdge.x, square01.x)
                            * (1.0 - smoothstep(rightClamped - aaEdge.x, rightClamped, square01.x));
                float edgeY = smoothstep(bottomClamped, bottomClamped + aaEdge.y, square01.y)
                            * (1.0 - smoothstep(topClamped - aaEdge.y, topClamped, square01.y));
                float edgeMask = edgeX * edgeY;
                if (edgeMask <= 0.0)
                {
                    return float4(0,0,0,1);
                }

                float fillStart = 1.0 - f;
                float feather = min(max(_FillFeather, 1e-5), max(f * 0.5, 1e-5));
                float aa = fwidth(maskedY);
                float fillMask = smoothstep(fillStart - feather - aa, fillStart + feather + aa, maskedY);

                float denom = max(f, 1e-5);
                float vSample = saturate((maskedY - fillStart) / denom);

                if (_FlipX > 0.5)
                {
                    maskedX = 1.0 - maskedX;
                }
                if (_FlipY > 0.5)
                {
                    vSample = 1.0 - vSample;
                }

                float2 sampleUv = float2(saturate(maskedX), vSample);
                sampleUv = (sampleUv * _MainTex_ST.xy) + _MainTex_ST.zw;

                float2 pad = _MainTex_TexelSize.xy * _SeamPadding;
                float2 rectMin = srcRect.xy + pad;
                float2 rectSize = max(srcRect.zw - (pad * 2.0), _MainTex_TexelSize.xy);
                float2 uv = (sampleUv * rectSize) + rectMin;
                fixed4 col = tex2D(_MainTex, uv);
                col.rgb *= (fillMask * edgeMask);
                return col;
            }
            ENDCG
        }
    }
    Fallback "Unlit/Texture"
}
