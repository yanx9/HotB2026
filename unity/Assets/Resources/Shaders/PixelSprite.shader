// Sprite pixel-artowy poruszający się w rytm muzyki.
//  - kołysanie: wiersze pikseli przesuwają się w poziomie o całe piksele, tym mocniej, im wyżej;
//    faza idzie z globalnym _BeatTime, więc wychylenie przypada na uderzenie
//  - piksele z alfą ok. 0.5 w teksturze są "świecące" i rozjaśniają się z _Pulse
//  - _Sat odbarwia uśpione rośliny, _Dither zamienia półprzezroczystość na raster (poświaty)
Shader "Ogrod/PixelSprite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}
        _Sway ("Kolysanie (px)", Float) = 0
        _Phase ("Faza", Float) = 0
        _WaveX ("Fala wzdluz X", Float) = 0
        _Pulse ("Puls", Float) = 0
        _Sat ("Nasycenie", Float) = 1
        _Dither ("Raster", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
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
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float _Sway;
                float _Phase;
                float _WaveX;
                float _Pulse;
                float _Sat;
                float _Dither;
            CBUFFER_END

            // ustawiane globalnie przez GardenGame
            float _BeatTime;
            float _Energy;

            float Bayer4(float2 p)
            {
                const float m[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
                int x = (int)fmod(p.x, 4.0);
                int y = (int)fmod(p.y, 4.0);
                return (m[y * 4 + x] + 0.5) / 16.0;
            }

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                o.color = v.color * unity_SpriteColor;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float h = i.uv.y;
                float wave = sin(_BeatTime * 3.14159265 + _Phase + i.uv.x * _WaveX + h * 1.3);
                float shift = round(_Sway * (0.5 + 0.5 * _Energy) * h * h * wave);
                float texWidth, texHeight;
                _MainTex.GetDimensions(texWidth, texHeight);
                float2 uv = float2(i.uv.x - shift / texWidth, i.uv.y);

                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                if (uv.x < 0.0 || uv.x > 1.0) c.a = 0.0;

                if (_Dither > 0.5)
                {
                    // najrzadszy raster wyglądałby jak siatka kropek, więc go obcinamy
                    float strength = c.a * i.color.a;
                    float on = step(Bayer4(i.positionCS.xy), strength) * step(0.16, strength);
                    return half4(c.rgb * i.color.rgb, on);
                }

                float emissive = (c.a > 0.3 && c.a < 0.7) ? 1.0 : 0.0;
                float alpha = c.a > 0.2 ? 1.0 : 0.0;
                float3 rgb = c.rgb;
                float gray = dot(rgb, float3(0.3, 0.59, 0.11));
                rgb = lerp(gray * float3(0.8, 0.85, 1.0), rgb, _Sat);
                rgb *= 1.0 + emissive * (0.1 + 0.7 * _Pulse);
                return half4(rgb * i.color.rgb, alpha * i.color.a);
            }
            ENDHLSL
        }
    }
}
