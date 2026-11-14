// Nocne niebo liczone w całości w szaderze, w siatce 320x180: rastrowany gradient,
// gwiazdy migające w rytm, księżyc z pulsującą poświatą i zorza, która jaśnieje z muzyką
// i przyjmuje kolor nastroju.
Shader "Ogrod/PixelSky"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}
        _Top ("Gora", Color) = (0.04, 0.06, 0.15, 1)
        _Mid ("Srodek", Color) = (0.1, 0.14, 0.32, 1)
        _Horizon ("Horyzont", Color) = (0.29, 0.25, 0.48, 1)
        _Moon ("Ksiezyc", Color) = (0.96, 0.95, 0.85, 1)
        _Aurora ("Zorza", Color) = (0.25, 0.88, 0.69, 1)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Top;
                float4 _Mid;
                float4 _Horizon;
                float4 _Moon;
                float4 _Aurora;
            CBUFFER_END

            // ustawiane globalnie przez GardenGame
            float _BeatTime;
            float _BeatPulse;
            float _Energy;
            float _Mood;
            float4 _MoodColor;

            float Bayer4(float2 p)
            {
                const float m[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
                int x = (int)fmod(p.x, 4.0);
                int y = (int)fmod(p.y, 4.0);
                return (m[y * 4 + x] + 0.5) / 16.0;
            }

            float Hash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 res = float2(320.0, 180.0);
                float2 p = floor(i.uv * res);
                float y = p.y / res.y;
                float d = Bayer4(p);

                // gradient w pasach, przejścia między pasami rastrowane
                float bands = 7.0;
                float g = saturate((y - 0.15) / 0.85);
                float b = saturate(floor(g * bands + d) / bands);
                float3 col = lerp(_Horizon.rgb, _Mid.rgb, saturate(b * 2.0));
                col = lerp(col, _Top.rgb, saturate(b * 2.0 - 1.0));
                col = lerp(col, col * 0.7 + _MoodColor.rgb * 0.3, abs(_Mood) * 0.12);

                // gwiazdy: migają naprzemiennie w rytm, wszystkie rozbłyskują na uderzenie
                float star = Hash(p);
                if (star > 0.988 && y > 0.32)
                {
                    float twinkle = 0.5 + 0.5 * sin(_BeatTime * 3.14159265 + star * 700.0);
                    float size = (star - 0.988) / 0.012;
                    col += (0.3 + 0.7 * twinkle) * lerp(0.35, 1.0, size) * (0.7 + 0.5 * _BeatPulse) * float3(0.9, 0.95, 1.0);
                }

                // zorza: wstęga falująca w tempie muzyki, z pionowymi smugami
                float t = _BeatTime * 0.25;
                float ribbon = 0.68 + 0.05 * sin(p.x * 0.021 + t) + 0.025 * sin(p.x * 0.053 - t * 1.7);
                float a = y < ribbon ? saturate(1.0 - (ribbon - y) / 0.17) : saturate(1.0 - (y - ribbon) / 0.03);
                float streak = 0.6 + 0.4 * sin(p.x * 0.9 + 3.0 * sin(p.x * 0.13 + t * 2.0));
                a *= streak * (0.3 + 0.35 * _Energy + 0.35 * _BeatPulse * (0.3 + 0.7 * _Energy));
                float3 auroraColor = lerp(_Aurora.rgb, _MoodColor.rgb, abs(_Mood));
                col += auroraColor * floor(a * 3.0 + d) / 3.0 * 0.5;

                // księżyc: sierp i rastrowana poświata oddychająca z rytmem
                float2 moon = float2(262.0, 148.0);
                float md = distance(p, moon);
                if (md <= 9.0)
                {
                    col = distance(p, moon + float2(4.0, 2.0)) < 8.0 ? lerp(col, _Moon.rgb, 0.07) : _Moon.rgb;
                }
                else
                {
                    float halo = saturate(1.0 - (md - 9.0) / (11.0 + 6.0 * _BeatPulse));
                    if (halo * 0.7 > d) col = lerp(col, _Moon.rgb, 0.22);
                }

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
