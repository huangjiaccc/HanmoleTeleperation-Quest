Shader "Unlit/HalfSphere_SBS_Inside"
{
    Properties
    {
        _MainTex ("SBS Texture", 2D) = "white" {}
        _Radius ("Sphere Radius", Float) = 0.5
        _PlaneWidth ("Plane Width", Float) = 1
    }

    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Transparent" }

        Cull Front                // 摄像机在球内
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ UNITY_SINGLE_PASS_STEREO
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Radius;
            float _PlaneWidth;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 objPos : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.objPos = v.vertex.xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // ===== 真正的几何半球裁剪 =====
                // y > 0 的那半球完全透明
                if (i.objPos.y > 0)
                    return float4(0,0,0,0);

                // ===== 以下只作用在下半球 =====
                float3 S = normalize(i.objPos) * _Radius;
                float3 northPole = float3(0, _Radius, 0);
                float3 dir = normalize(S - northPole);

                float t = (-2.0 * _Radius) / dir.y;
                float3 planePos = northPole + dir * t;

                float planeHeight = _PlaneWidth * 9.0 / 16.0;

                float2 uv;
                uv.x = planePos.x / _PlaneWidth + 0.5;
                uv.y = planePos.z / planeHeight + 0.5;

                // Mirror the projected image horizontally before SBS eye split.
                uv.x = 1.0 - uv.x;

                // 半球内但贴纸外 → 黑色填充
                if (uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1)
                    return float4(0,0,0,1);

                // ===== SBS 分眼 =====
                if (unity_StereoEyeIndex == 0)
                    uv.x *= 0.5;
                else
                    uv.x = uv.x * 0.5 + 0.5;

                fixed4 col = tex2D(_MainTex, uv);
                col.a = 1;
                return col;
            }
            ENDCG
        }
    }
}
