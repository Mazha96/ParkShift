Shader "Hidden/ParkShift/CartoonInk"
{
    Properties { _MainTex ("Scene", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex, _CameraDepthNormalsTexture;
            float4 _MainTex_TexelSize;
            float _Thickness;
            fixed4 _InkColor;
            void SampleGeometry(float2 uv, out float depth, out float3 normal)
            {
                DecodeDepthNormal(tex2D(_CameraDepthNormalsTexture, uv), depth, normal);
            }
            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                float2 uv = i.uv;
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0) uv.y = 1 - uv.y;
                #endif
                float d; float3 n;
                SampleGeometry(uv, d, n);
                float edge = 0;
                float2 pixel = abs(_MainTex_TexelSize.xy) * _Thickness;
                const float2 offsets[4] = {float2(1,0),float2(-1,0),float2(0,1),float2(0,-1)};
                [unroll] for (int k=0; k<4; k++)
                {
                    float otherDepth; float3 otherNormal;
                    SampleGeometry(uv + pixel * offsets[k], otherDepth, otherNormal);
                    float depthEdge = smoothstep(0.012, 0.035, abs(d-otherDepth)/max(d,0.001));
                    float normalEdge = smoothstep(0.22,0.38,1-dot(n,otherNormal));
                    // No ink within the sky; silhouettes still use the depth edge.
                    normalEdge *= (1-step(0.999,d)) * (1-step(0.999,otherDepth));
                    edge = max(edge,max(depthEdge,normalEdge));
                }
                col.rgb = lerp(col.rgb, _InkColor.rgb, edge * _InkColor.a);
                return col;
            }
            ENDCG
        }
    }
    Fallback Off
}
