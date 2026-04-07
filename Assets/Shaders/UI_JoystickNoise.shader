Shader "UI/JoystickBreathNoise_Safe"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _NoiseTex ("Noise", 2D) = "white" {}
        _NoiseScale ("Noise Scale", Range(0.1, 5)) = 1
        _MoveSpeed ("Move Speed", Range(0, 5)) = 1
        _Intensity ("Intensity", Range(0, 1)) = 0.2
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "RenderType"="Transparent"
            "IgnoreProjector"="True"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        Lighting Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex   : POSITION;
                float2 uv       : TEXCOORD0;
                float4 color    : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv     : TEXCOORD0;
                float4 color  : COLOR;
            };

            sampler2D _MainTex;
            sampler2D _NoiseTex;
            float4 _MainTex_ST;
            float4 _Color;

            float _MoveSpeed;
            float _NoiseScale;
            float _Intensity;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv) * i.color;

                float2 noiseUV = i.uv * _NoiseScale;
                noiseUV += _Time.y * _MoveSpeed;

                float noise = tex2D(_NoiseTex, noiseUV).r;

                col.rgb += noise * _Intensity;

                return col;
            }
            ENDCG
        }
    }
}
