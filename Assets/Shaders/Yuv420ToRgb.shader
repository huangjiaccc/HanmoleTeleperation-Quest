Shader "Hidden/Yuv420ToRgb"
{
    Properties
    {
        _TexY ("Y", 2D) = "white" {}
        _TexU ("U", 2D) = "white" {}
        _TexV ("V", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZWrite Off Cull Off ZTest Always
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _TexY;
            sampler2D _TexU;
            sampler2D _TexV;

            struct app { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(app v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float y = tex2D(_TexY, uv).r; // R8 channel (Y)
                float u = tex2D(_TexU, uv * 0.5).r - 0.5; // U (subsampled)
                float v = tex2D(_TexV, uv * 0.5).r - 0.5; // V (subsampled)

                // YUV to RGB conversion
                float R = y + 1.402 * v;
                float G = y - 0.344136 * u - 0.714136 * v;
                float B = y + 1.772 * u;

                return fixed4(R, G, B, 1.0);
            }
            ENDCG
        }
    }
}
