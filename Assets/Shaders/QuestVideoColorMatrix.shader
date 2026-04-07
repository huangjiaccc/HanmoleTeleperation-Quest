Shader "Quest3VideoPlayer/QuestVideoColorMatrix"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _CM0 ("Color Matrix Row 0", Vector) = (1,0,0,0)
        _CM1 ("Color Matrix Row 1", Vector) = (0,1,0,0)
        _CM2 ("Color Matrix Row 2", Vector) = (0,0,1,0)
        _LeftRect ("Left Rect", Vector) = (0, 0, 0.5, 1)
        _RightRect ("Right Rect", Vector) = (0.5, 0, 0.5, 1)
        _SeamCenter ("Seam Center", Range(0, 1)) = 0.5
        _BlendWidth ("Blend Width", Range(0, 0.5)) = 0.05
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _CM0;
            float4 _CM1;
            float4 _CM2;
            float4 _LeftRect;
            float4 _RightRect;
            float _SeamCenter;
            float _BlendWidth;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float seam = saturate(_SeamCenter);
                float bw = max(_BlendWidth, 0.0);
                float half = bw * 0.5;
                float t = (bw <= 0.0001) ? step(seam, uv.x) : smoothstep(seam - half, seam + half, uv.x);

                float seamDenom = max(seam, 1e-4);
                float rightDenom = max(1.0 - seam, 1e-4);
                float uLeft = saturate(uv.x / seamDenom);
                float uRight = saturate((uv.x - seam) / rightDenom);
                float2 srcLeft = _LeftRect.xy + float2(uLeft * _LeftRect.z, uv.y * _LeftRect.w);
                float2 srcRight = _RightRect.xy + float2(uRight * _RightRect.z, uv.y * _RightRect.w);

                fixed4 left = tex2D(_MainTex, srcLeft);
                fixed4 right = tex2D(_MainTex, srcRight);
                fixed4 c = lerp(left, right, t);
                float4 v = float4(c.rgb, 1.0);
                float3 outRgb;
                outRgb.r = dot(_CM0, v);
                outRgb.g = dot(_CM1, v);
                outRgb.b = dot(_CM2, v);
                return fixed4(saturate(outRgb), c.a);
            }
            ENDHLSL
        }
    }
}
