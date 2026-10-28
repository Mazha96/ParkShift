Shader "ParkShift/CartoonSurface"
{
    Properties
    {
        _Color ("Paint", Color) = (1,1,1,1)
        _MainTex ("Paint texture", 2D) = "white" {}
        [HDR] _EmissionColor ("Emission", Color) = (0,0,0,0)
        _ShadowTint ("Shadow ink tint", Color) = (0.53,0.48,0.72,1)
        _Glossiness ("Smoothness", Range(0,1)) = 0
        _Metallic ("Metallic", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Toon fullforwardshadows addshadow
        #pragma target 3.0
        sampler2D _MainTex;
        fixed4 _Color, _EmissionColor, _ShadowTint;
        struct Input { float2 uv_MainTex; };
        half4 LightingToon(SurfaceOutput s, half3 lightDir, half atten)
        {
            half ndl = dot(s.Normal, lightDir);
            // Two clean painted light bands with a narrow anti-aliased transition.
            half band = smoothstep(0.08, 0.14, ndl);
            half3 paint = lerp(_ShadowTint.rgb * 0.45, half3(1,1,1), band);
            return half4(s.Albedo * _LightColor0.rgb * paint * atten, s.Alpha);
        }
        void surf(Input IN, inout SurfaceOutput o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            // Preserve color in deep shade and retain every existing interactive glow.
            o.Emission = c.rgb * 0.12 + _EmissionColor.rgb * 0.65;
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "Diffuse"
}
