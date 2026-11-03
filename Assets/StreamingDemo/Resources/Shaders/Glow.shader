// -----------------------------------------------------------------------------
// StreamingDemo/Glow
//
// Transparent "hologram" shader used for everything that represents STATE
// rather than CONTENT: unloaded outlines, queued/loading placeholders, load
// radius rings, in-flight I/O lines, trails.
// * Additive by default (SrcAlpha One) so overlapping highlights get brighter.
// * Optional fresnel rim (_Rim) and moving scanlines (_Scan) for the
//   "this is being streamed in" look.
// * Vertex colour * _BaseColor, so LineRenderer / TrailRenderer gradients work.
// -----------------------------------------------------------------------------
Shader "StreamingDemo/Glow"
{
    Properties
    {
        _BaseColor ("Colour", Color) = (1,1,1,0.5)
        _Rim ("Fresnel rim", Float) = 0
        _Scan ("Scanline amount", Float) = 0
        _Pulse ("Pulse speed (0 = off)", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5   // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 1   // One (additive)
        [Enum(UnityEngine.Rendering.CullMode)]  _Cull ("Cull", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }

        Pass
        {
            Name "Glow"
            Tags { "LightMode"="UniversalForward" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half  _Rim;
                half  _Scan;
                half  _Pulse;
                half  _SrcBlend;
                half  _DstBlend;
                half  _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                half4  color      : COLOR;
                half   fog        : TEXCOORD2;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS   = TransformObjectToWorldNormal(v.normalOS);
                o.color      = v.color;
                o.fog        = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                half4 col = i.color * _BaseColor;

                // Safe normalise: LineRenderers have no normals.
                float3 n = i.normalWS * rsqrt(max(dot(i.normalWS, i.normalWS), 1e-6));
                float3 v = normalize(GetWorldSpaceViewDir(i.positionWS));
                half rim = pow(1.0 - saturate(abs(dot(n, v))), 2.0) * _Rim;

                half scan = (sin(i.positionWS.y * 6.0 - _Time.y * 5.0) * 0.5 + 0.5) * _Scan;
                half pulse = _Pulse > 0 ? (0.65 + 0.35 * sin(_Time.y * _Pulse)) : 1.0;

                col.a = saturate(col.a * (1.0 + rim + scan) * pulse);

                // Fade towards black (= invisible when additive) in fog.
                col.rgb = MixFogColor(col.rgb, half3(0,0,0), i.fog);
                return col;
            }
            ENDHLSL
        }
    }
}
