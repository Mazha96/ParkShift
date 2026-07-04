// ParkShift stylized dusk/space skybox: three-band vertical gradient, cheap
// procedural star sprinkle in the upper hemisphere, and a flat low-poly-style
// "planet" disc with a soft halo near the horizon. Built-in render pipeline.
// Assigned by SceneBuilder to Assets/Materials/Skybox.mat (Skybox/Procedural is
// the fallback if this shader is missing).
Shader "ParkShift/GradientSkybox"
{
    Properties
    {
        _TopColor ("Top Color", Color) = (0.045, 0.04, 0.15, 1)
        _HorizonColor ("Horizon Color", Color) = (0.96, 0.47, 0.32, 1)
        _BottomColor ("Bottom Color", Color) = (0.19, 0.13, 0.26, 1)
        _HorizonHeight ("Horizon Height", Range(-0.5, 0.5)) = 0.0
        _HorizonFalloff ("Horizon Falloff", Range(0.4, 8)) = 1.5
        _StarDensity ("Star Density", Range(0, 1)) = 0.5
        _StarBrightness ("Star Brightness", Range(0, 4)) = 1.8
        _PlanetDir ("Planet Direction", Vector) = (-0.45, 0.24, 0.86, 0)
        _PlanetColor ("Planet Color", Color) = (0.99, 0.66, 0.7, 1)
        _PlanetCos ("Planet Size (cos of angular radius)", Range(0.99, 0.99995)) = 0.9975
        _HaloColor ("Planet Halo Color", Color) = (0.75, 0.35, 0.65, 1)
        _RingColor ("Planet Ring Color", Color) = (0.85, 0.78, 0.62, 1)
        _RingTilt ("Ring Tilt Axis (world dir)", Vector) = (0.35, 0.9, 0.2, 0)
        _RingInner ("Ring Inner Radius (x planet radii)", Range(1.05, 3)) = 1.35
        _RingOuter ("Ring Outer Radius (x planet radii)", Range(1.1, 4)) = 2.1
        _RingIntensity ("Ring Intensity", Range(0, 2)) = 0
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            half4 _TopColor;
            half4 _HorizonColor;
            half4 _BottomColor;
            float _HorizonHeight;
            float _HorizonFalloff;
            float _StarDensity;
            float _StarBrightness;
            float4 _PlanetDir;
            half4 _PlanetColor;
            float _PlanetCos;
            half4 _HaloColor;
            half4 _RingColor;
            float4 _RingTilt;
            float _RingInner;
            float _RingOuter;
            float _RingIntensity;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz; // skybox mesh verts double as view directions
                return o;
            }

            // Compact 3D->1D hash (Dave Hoskins style), good enough for star cells.
            float hash13(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(i.dir);
                float h = dir.y - _HorizonHeight;

                // ---- Three-band vertical gradient ----
                float up = saturate(h * _HorizonFalloff);
                float dn = saturate(-h * _HorizonFalloff * 1.8);
                float3 col = lerp(_HorizonColor.rgb, _TopColor.rgb, sqrt(up));
                col = lerp(col, _BottomColor.rgb, sqrt(dn));

                // ---- Stars: one hashed dot per sparse cell, upper sky only ----
                float skyFade = saturate((h - 0.06) * 2.2);
                if (skyFade > 0.001 && _StarDensity > 0.001)
                {
                    float3 sp = dir * 42.0;
                    float3 cell = floor(sp);
                    float rnd = hash13(cell);
                    if (rnd > 1.0 - _StarDensity * 0.06)
                    {
                        float3 off = float3(hash13(cell + 7.13),
                                            hash13(cell + 13.7),
                                            hash13(cell + 29.31));
                        off = off * 0.7 + 0.15; // keep the star inside its cell
                        float d = length(frac(sp) - off);
                        float star = saturate(1.0 - d * 4.0);
                        star = star * star * star * star;
                        col += star * _StarBrightness * skyFade
                             * (0.55 + 0.45 * hash13(cell + 3.7));
                    }
                }

                // ---- Planet disc + halo ----
                float3 pdir = normalize(_PlanetDir.xyz);
                float c = dot(dir, pdir);
                float edge = 1.0 - _PlanetCos;
                float disc = smoothstep(_PlanetCos, _PlanetCos + edge * 0.12, c);
                float halo = saturate((c - (_PlanetCos - edge * 6.0)) / (edge * 6.0));
                halo = halo * halo * halo;
                col += _HaloColor.rgb * halo * 0.55 * (1.0 - disc);
                // slight vertical shade so the disc doesn't read as a sticker
                float shade = 0.8 + 0.35 * saturate((dir.y - pdir.y) * 14.0 + 0.5);
                col = lerp(col, _PlanetColor.rgb * shade, disc);

                // ---- Planet ring: thin tilted ellipse band around the disc ----
                // Small-angle band in a basis around the planet direction: u/v are the view
                // offsets along two tangents, squashing v turns the circle into a tilted
                // ellipse. Drawn after the disc so the near half (v < 0) passes in front;
                // the far half is masked where the disc covers it.
                if (_RingIntensity > 0.001 && c > 0.9)
                {
                    float angRad = acos(_PlanetCos);           // planet angular radius
                    float3 t1 = normalize(cross(normalize(_RingTilt.xyz), pdir));
                    float3 t2 = cross(pdir, t1);
                    float3 offs = dir - pdir * c;
                    float u = dot(offs, t1);
                    float v = dot(offs, t2);
                    float rr = sqrt(u * u + v * v * 9.0) / max(angRad, 1e-4);
                    float band = smoothstep(_RingInner - 0.10, _RingInner, rr)
                               * (1.0 - smoothstep(_RingOuter, _RingOuter + 0.10, rr));
                    band *= 1.0 - disc * step(0.0, v);         // far half hides behind the planet
                    band *= 0.65 + 0.35 * saturate(rr - _RingInner); // soft inner-to-outer fade
                    col += _RingColor.rgb * band * _RingIntensity;
                }

                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
