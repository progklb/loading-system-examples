// -----------------------------------------------------------------------------
// StreamingDemo/GridFloor
//
// An infinite-looking "blueprint" floor that draws a world-space grid.
// The grid size is set per demo mode so the floor literally shows the
// streaming granularity (Skyrim cells, Minecraft chunks, World Partition
// cells...). Lines are anti-aliased with fwidth().
// -----------------------------------------------------------------------------
Shader "StreamingDemo/GridFloor"
{
    Properties
    {
        _BaseColor ("Floor", Color) = (0.05,0.07,0.11,1)
        _LineColor ("Line", Color) = (0.2,0.35,0.55,1)
        _GridSize ("Grid size (world units)", Float) = 32
        _MajorEvery ("Major line every N cells", Float) = 4
        _LineWidth ("Line width (pixels)", Float) = 1.2
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry-10" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            half4 _LineColor;
            float _GridSize;
            float _MajorEvery;
            float _LineWidth;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "Floor"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; half fog : TEXCOORD1; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            float GridLine (float2 coord, float width)
            {
                float2 d = fwidth(coord);
                float2 g = abs(frac(coord - 0.5) - 0.5) / max(d, 1e-5);
                return 1.0 - saturate(min(g.x, g.y) - width * 0.5 + 0.5);
            }

            half4 frag (Varyings i) : SV_Target
            {
                float2 p = i.positionWS.xz / _GridSize;
                float minor = GridLine(p, _LineWidth);
                float major = GridLine(p / max(_MajorEvery, 1), _LineWidth * 1.8);

                half3 col = _BaseColor.rgb;
                col = lerp(col, _LineColor.rgb, minor * 0.55);
                col = lerp(col, _LineColor.rgb * 1.6, major * 0.8);
                col = MixFog(col, i.fog);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct Attributes { float4 positionOS : POSITION; };
            float4 vert (Attributes v) : SV_POSITION { return TransformObjectToHClip(v.positionOS.xyz); }
            half4 frag () : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct Attributes { float4 positionOS : POSITION; };
            float4 vert (Attributes v) : SV_POSITION { return TransformObjectToHClip(v.positionOS.xyz); }
            half4 frag () : SV_Target { return half4(0, 1, 0, 0); }
            ENDHLSL
        }
    }
}
