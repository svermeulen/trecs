Shader "Trecs/InstancedIndirectUnlit"
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct InstanceData
            {
                float4 posScale;  // xyz = position, w = scale
                float4 rotation;  // quaternion xyzw
                float4 color;     // rgba per-instance color
            };

            StructuredBuffer<InstanceData> _InstanceData;

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

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
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;

                InstanceData data = _InstanceData[input.instanceID];
                float s = data.posScale.w;
                float4 q = data.rotation;

                float3 scaled = input.positionOS * s;
                float3 rotated = RotateByQuat(scaled, q);
                float3 positionWS = rotated + data.posScale.xyz;

                output.positionCS = TransformWorldToHClip(positionWS);
                output.color = half4(data.color) * _BaseColor;

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct InstanceData
            {
                float4 posScale;
                float4 rotation;
                float4 color;
            };

            StructuredBuffer<InstanceData> _InstanceData;

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
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vertDepth(Attributes input)
            {
                Varyings output;
                InstanceData data = _InstanceData[input.instanceID];
                float3 scaled = input.positionOS * data.posScale.w;
                float3 rotated = RotateByQuat(scaled, data.rotation);
                float3 positionWS = rotated + data.posScale.xyz;
                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            half4 fragDepth(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
