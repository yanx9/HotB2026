// Chmura punktów ze skanu. Każdy punkt to czworokąt zwrócony do kamery; pojawia się, gdy
// dotrze do niego fala skanu (odległość / prędkość fali), błyska i powoli gaśnie.
// uv: narożnik czworokąta (-1..1), uv2: (czas skanu, odległość od miejsca skanu, czas gaśnięcia).
Shader "Mrok/PointCloud"
{
    Properties
    {
        _Size ("Rozmiar punktu (m)", Float) = 0.028
        _MinPixels ("Najmniejszy rozmiar (px)", Float) = 1.5
        _WaveSpeed ("Predkosc fali (m/s)", Float) = 28
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend One One
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 corner : TEXCOORD0;
                float3 scan : TEXCOORD1;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
                float4 color : COLOR;
            };

            CBUFFER_START(UnityPerMaterial)
                float _Size;
                float _MinPixels;
                float _WaveSpeed;
            CBUFFER_END

            float _Now;         // ustawiane globalnie przez grę
            float _Tremble;     // napięcie gracza: o ile metrów drżą punkty
            float _LifeScale;   // napięcie skraca pamięć obrazu, spokój ją wydłuża (1 = bez zmian)

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float age = _Now - v.scan.x - v.scan.y / _WaveSpeed;
                float fade = saturate(1.0 - age / max(v.scan.z * (_LifeScale > 0.0 ? _LifeScale : 1.0), 0.01));
                // każdy punkt migocze we własnym rytmie, żeby stopklatka nie była martwa
                float seed = frac(sin(dot(v.positionOS.xyz, float3(12.9898, 78.233, 37.719))) * 43758.5453);
                // pod presją obraz drży: każdy punkt we własną stronę
                v.positionOS.xyz += _Tremble * float3(sin(_Now * 17.0 + seed * 40.0), sin(_Now * 23.0 + seed * 71.0), sin(_Now * 19.0 + seed * 13.0));
                float shimmer = 0.82 + 0.18 * sin(_Now * (1.5 + 2.0 * seed) + seed * 6.2832);
                float brightness = fade * fade * shimmer + 2.5 * exp(-max(age, 0.0) * 5.0);

                float4 center = TransformObjectToHClip(v.positionOS.xyz);
                // rozmiar liczony z prawdziwej odległości, nie z głębi: inaczej punkty pod nogami,
                // tuż przy płaszczyźnie kamery, rozdymają się na pół ekranu
                float distance = max(0.05, length(TransformWorldToView(TransformObjectToWorld(v.positionOS.xyz))));
                float2 world = _Size * float2(UNITY_MATRIX_P._m00, abs(UNITY_MATRIX_P._m11)) * center.w / distance;
                float2 pixels = _MinPixels * 2.0 * center.w / _ScreenParams.xy;
                center.xy += v.corner * max(world, pixels);

                // punkt, do którego fala jeszcze nie doszła albo który już zgasł, wypada poza ekran
                o.positionCS = (age < 0.0 || fade <= 0.0) ? float4(2.0, 2.0, 2.0, 1.0) : center;
                o.corner = v.corner;
                o.color = v.color * brightness;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float r = dot(i.corner, i.corner);
                clip(1.0 - r);
                return half4(i.color.rgb * (1.0 - 0.5 * r), 1.0);
            }
            ENDHLSL
        }
    }
}
