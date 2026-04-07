Shader "UI/RoundedRect_MaskBased"
{
    Properties
    {
        _MainTex   ("Mask (Alpha)", 2D) = "white" {}
        _FillColor ("Fill Color", Color) = (0,0.6,1,1)
        _BorderColor ("Border Color", Color) = (1,1,1,1)
        _BorderThreshold ("Border Threshold", Range(0,1)) = 0.5
    }

    SubShader
    {
        Tags 
        { 
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                float4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                float4 color : COLOR;
            };

            sampler2D _MainTex;
            fixed4 _FillColor;
            fixed4 _BorderColor;
            float  _BorderThreshold;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.uv    = v.uv;
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed mask = tex2D(_MainTex, i.uv).a;

                fixed borderMask = step(_BorderThreshold, mask);

                fixed4 col = lerp(_FillColor, _BorderColor, borderMask);

                col.a = mask;
                col *= i.color;

                return col;
            }
            ENDCG
        }
    }
}
