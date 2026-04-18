// Used on platforms without compute shader / structured buffer support
// (notably WebGL 2). Renders via standard GPU instancing with per-instance
// properties supplied through MaterialPropertyBlock.SetVectorArray.
Shader "Trecs/InstancedUnlitFallback"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options maxcount:250

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _PosScale)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Rotation)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            float3 RotateByQuat(float3 v, float4 q)
            {
                float3 u = q.xyz;
                float s = q.w;
                return 2.0 * dot(u, v) * u
                     + (s * s - dot(u, u)) * v
                     + 2.0 * s * cross(u, v);
            }

            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float4 posScale = UNITY_ACCESS_INSTANCED_PROP(Props, _PosScale);
                float4 rotation = UNITY_ACCESS_INSTANCED_PROP(Props, _Rotation);
                float4 col = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);

                float3 scaled = input.positionOS * posScale.w;
                float3 rotated = RotateByQuat(scaled, rotation);
                float3 positionWS = rotated + posScale.xyz;

                output.positionCS = TransformWorldToHClip(positionWS);
                output.color = half4(col) * _BaseColor;

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return input.color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma multi_compile_instancing
            #pragma instancing_options maxcount:250

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _PosScale)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Rotation)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            float3 RotateByQuat(float3 v, float4 q)
            {
                float3 u = q.xyz;
                float s = q.w;
                return 2.0 * dot(u, v) * u
                     + (s * s - dot(u, u)) * v
                     + 2.0 * s * cross(u, v);
            }

            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vertDepth(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float4 posScale = UNITY_ACCESS_INSTANCED_PROP(Props, _PosScale);
                float4 rotation = UNITY_ACCESS_INSTANCED_PROP(Props, _Rotation);

                float3 scaled = input.positionOS * posScale.w;
                float3 rotated = RotateByQuat(scaled, rotation);
                float3 positionWS = rotated + posScale.xyz;

                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            half4 fragDepth(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return 0;
            }
            ENDHLSL
        }
    }
}
