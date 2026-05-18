Shader "Voxels/Voxel"
{
    Properties
    {
        _CoordinateScaling ("Coordinate Scaling", Float) = 0.15
        _BlendOffset ("Blend Offset", Range(0, 0.33)) = 0.2
        _BlendExponent ("Blend Exponent", Range(0.0, 8.0)) = 2.0
        _BlendHeightStrength ("Blend Height Strength", Range(0.01, 0.99)) = 0.5
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
        half _CoordinateScaling;
        half _BlendOffset;
        half _BlendExponent;
        half _BlendHeightStrength;
        CBUFFER_END

        ENDHLSL

        Pass
        {

            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM

            #pragma require geometry

            // Material Keywords
            #pragma shader_feature_local_fragment _SPECULAR_SETUP

            // URP Keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK

            // Unity Keywords
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fog

            #pragma vertex LitPassVertex
            #pragma geometry LitPassGeometry
            #pragma fragment LitPassFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"


            struct VertexPassInput
            {
                float4 positionOS : POSITION;
                float4 normalOS : NORMAL;
                uint materialIndex : TEXCOORD0;
                float2 lightmapUV : TEXCOORD1;

                // Temporary contract:
                // packedMaterialWeights.x stores weights0 for slots 0..3
                // packedMaterialWeights.y stores weights1 for slots 4..7, currently ignored by the shader
                uint2 packedMaterialWeights : TEXCOORD2;
            };

            #include "Assets/Compute/Include/Packing2.hlsl"

            float4 UnpackMaterialWeights(uint2 packedWeights)
            {
                return UnpackBytes01(packedWeights.x);
            }

            struct GeometryPassInput
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                uint materialIndex : TEXCOORD2;
                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 3);

                #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                    half4 fogFactorAndVertexLight : TEXCOORD4;
                #else
                    half fogFactor : TEXCOORD4;
                #endif

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    float4 shadowCoord : TEXCOORD5;
                #endif

                // Веса, пришедшие из mesh vertex buffer.
                half4 materialWeights : TEXCOORD6;
            };

            struct FragmentPassInput
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;

                // Старые индексы трёх вершин треугольника.
                uint3 materialIndices : TEXCOORD2;

                // Старые barycentric-веса трёх вершин треугольника.
                half3 triangleMaterialWeights : TEXCOORD3;

                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 4);

                #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                    half4 fogFactorAndVertexLight : TEXCOORD5;
                #else
                    half fogFactor : TEXCOORD5;
                #endif

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    float4 shadowCoord : TEXCOORD6;
                #endif

                // Averaged material weights for slots 0..3 only.
                // Slots 4..7 are currently ignored in the rendering path.
                nointerpolation half4 fixedMaterialWeights : TEXCOORD7;
            };

            float cosOfHalfSharpFeatureAngle;
            TEXTURE2D_ARRAY(materialAlbedoTextures);
            TEXTURE2D_ARRAY(materialNormalTextures);
            TEXTURE2D_ARRAY(materialMOHSTextures);
            SAMPLER(sampler_linear_repeat);

            #include "Assets/Shaders/Voxels/Include/Triplanar.hlsl"

            GeometryPassInput LitPassVertex(VertexPassInput input)
            {
                GeometryPassInput output;

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS.xyz);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;

                half3 vertexLight = VertexLighting(positionInputs.positionWS, normalInputs.normalWS);
                half fogFactor = ComputeFogFactor(positionInputs.positionCS.z);

                output.normalWS = NormalizeNormalPerVertex(normalInputs.normalWS);
                output.materialIndex = input.materialIndex;

                output.materialWeights = UnpackMaterialWeights(input.packedMaterialWeights);

                OUTPUT_LIGHTMAP_UV(input.lightmapUV, unity_LightmapST, output.lightmapUV);
                OUTPUT_SH(output.normalWS.xyz, output.vertexSH);

                #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
                #else
                    output.fogFactor = fogFactor;
                #endif

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    output.shadowCoord = GetShadowCoord(positionInputs);
                #endif

                return output;
            }

            [maxvertexcount(3)]
            void LitPassGeometry(triangle GeometryPassInput inputs[3], inout TriangleStream<FragmentPassInput> outputStream)
            {
                const half3x3 half3x3Identity = half3x3
                (
                    1.0h, 0.0h, 0.0h,
                    0.0h, 1.0h, 0.0h,
                    0.0h, 0.0h, 1.0h
                );

                uint3 materialIndices = float3(inputs[0].materialIndex, inputs[1].materialIndex, inputs[2].materialIndex);
                float3 faceNormalWS = normalize(cross(inputs[1].positionWS - inputs[0].positionWS, inputs[2].positionWS - inputs[0].positionWS));

                half4 triangleFixedMaterialWeights = (inputs[0].materialWeights + inputs[1].materialWeights + inputs[2].materialWeights) / 3.0h;

                for (uint index = 0; index < 3; index++)
                {
                    GeometryPassInput input = inputs[index];
                    input.normalWS = dot(input.normalWS, faceNormalWS) <= cosOfHalfSharpFeatureAngle ? faceNormalWS : input.normalWS;

                    FragmentPassInput output;
                    output.positionCS = input.positionCS;
                    output.positionWS = input.positionWS;
                    output.normalWS = input.normalWS;
                    output.materialIndices = materialIndices;
                    // output.materialWeights = half3x3Identity[index];
                    output.triangleMaterialWeights = half3x3Identity[index];
                    output.fixedMaterialWeights = triangleFixedMaterialWeights;

                    #if defined(LIGHTMAP_ON)
                        output.lightmapUV = input.lightmapUV;
                    #else
                        output.vertexSH = input.vertexSH;
                    #endif

                    #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                        output.fogFactorAndVertexLight = input.fogFactorAndVertexLight;
                    #else
                        output.fogFactor = input.fogFactor;
                    #endif

                    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                        output.shadowCoord = input.shadowCoord;
                    #endif

                    outputStream.Append(output);
                }
                outputStream.RestartStrip();
            }

            float3 GetMaterialBlendWeights(FragmentPassInput input, half3 heights)
            {
                // float3 materialWeights = abs(input.materialWeights);
                float3 materialWeights = abs(input.triangleMaterialWeights);

                materialWeights = saturate(materialWeights - _BlendOffset);
                materialWeights *= abs(lerp(1.0h, heights, _BlendHeightStrength));
                materialWeights = pow(materialWeights, _BlendExponent);
                materialWeights /= dot(materialWeights, 1.0h);

                return materialWeights;
            }

            // SurfaceData CreateSurfaceData(FragmentPassInput input)
            // {
            //     SurfaceData surfaceData = (SurfaceData)0;
            //     TriplanarData triplanarDatas[3];

            //     for (uint index = 0; index < 3; index++)
            //     {
            //         triplanarDatas[index] = ApplyTriplanarTexturing
            //         (
            //             input.positionWS,
            //             input.normalWS,
            //             materialAlbedoTextures,
            //             materialNormalTextures,
            //             materialMOHSTextures,
            //             sampler_linear_repeat,
            //             input.materialIndices[index]
            //         );
            //     }

            //     half3 heights = half3(triplanarDatas[0].height, triplanarDatas[1].height, triplanarDatas[2].height);
            //     float3 materialWeights = GetMaterialBlendWeights(input, heights);

            //     for (index = 0; index < 3; index++)
            //     {
            //         surfaceData.albedo += materialWeights[index] * triplanarDatas[index].albedo.rgb;
            //         surfaceData.alpha += materialWeights[index] * triplanarDatas[index].albedo.a;
            //         // Use SurfaceData's normalTS field to store our normalWS.
            //         surfaceData.normalTS += materialWeights[index] * triplanarDatas[index].normalWS;
            //         surfaceData.metallic += materialWeights[index] * triplanarDatas[index].metallic;
            //         surfaceData.occlusion += materialWeights[index] * triplanarDatas[index].occlusion;
            //         surfaceData.smoothness += materialWeights[index] * triplanarDatas[index].smoothness;
            //     }

            //     return surfaceData;
            // }

            SurfaceData CreateSurfaceData(FragmentPassInput input)
            {
                SurfaceData surfaceData = (SurfaceData)0;

                float4 w = input.fixedMaterialWeights;
                float sum = w.x + w.y + w.z + w.w;

                if (sum > 0.0001f)
                {
                    w /= sum;
                }
                else
                {
                    w = float4(1, 0, 0, 0);
                }

                // Temporary slot mapping for weights0:
                // slot0 -> texture layer 0
                // slot1 -> texture layer 3
                // slot2 -> texture layer 1
                // slot3 -> texture layer 4

                TriplanarData slot0 = ApplyTriplanarTexturing
                (
                    input.positionWS,
                    input.normalWS,
                    materialAlbedoTextures,
                    materialNormalTextures,
                    materialMOHSTextures,
                    sampler_linear_repeat,
                    0
                );

                TriplanarData slot1 = ApplyTriplanarTexturing
                (
                    input.positionWS,
                    input.normalWS,
                    materialAlbedoTextures,
                    materialNormalTextures,
                    materialMOHSTextures,
                    sampler_linear_repeat,
                    1
                );

                TriplanarData slot2 = ApplyTriplanarTexturing
                (
                    input.positionWS,
                    input.normalWS,
                    materialAlbedoTextures,
                    materialNormalTextures,
                    materialMOHSTextures,
                    sampler_linear_repeat,
                    2
                );

                TriplanarData slot3 = ApplyTriplanarTexturing
                (
                    input.positionWS,
                    input.normalWS,
                    materialAlbedoTextures,
                    materialNormalTextures,
                    materialMOHSTextures,
                    sampler_linear_repeat,
                    3
                );

                surfaceData.albedo =
                    w.x * slot0.albedo.rgb +
                    w.y * slot1.albedo.rgb +
                    w.z * slot2.albedo.rgb +
                    w.w * slot3.albedo.rgb;

                surfaceData.alpha =
                    w.x * slot0.albedo.a +
                    w.y * slot1.albedo.a +
                    w.z * slot2.albedo.a +
                    w.w * slot3.albedo.a;

                // Use SurfaceData.normalTS to store world-space normal.
                surfaceData.normalTS =
                    w.x * slot0.normalWS +
                    w.y * slot1.normalWS +
                    w.z * slot2.normalWS +
                    w.w * slot3.normalWS;

                surfaceData.metallic =
                    w.x * slot0.metallic +
                    w.y * slot1.metallic +
                    w.z * slot2.metallic +
                    w.w * slot3.metallic;

                surfaceData.occlusion =
                    w.x * slot0.occlusion +
                    w.y * slot1.occlusion +
                    w.z * slot2.occlusion +
                    w.w * slot3.occlusion;

                surfaceData.smoothness =
                    w.x * slot0.smoothness +
                    w.y * slot1.smoothness +
                    w.z * slot2.smoothness +
                    w.w * slot3.smoothness;

                return surfaceData;
            }

            InputData CreateInputData(FragmentPassInput input, half3 normalWS)
            {
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = NormalizeNormalPerPixel(normalWS);
                inputData.viewDirectionWS = SafeNormalize(GetWorldSpaceNormalizeViewDir(inputData.positionWS));

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    inputData.shadowCoord = input.shadowCoord;
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
                #else
                    inputData.shadowCoord = float4(0.0f, 0.0f, 0.0f, 0.0f);
                #endif

                #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                    inputData.fogCoord = input.fogFactorAndVertexLight.x;
                    inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
                #else
                    inputData.fogCoord = input.fogFactor;
                    inputData.vertexLighting = half3(0.0h, 0.0h, 0.0h);
                #endif

                inputData.bakedGI = SAMPLE_GI(input.lightmapUV, input.vertexSH, inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = SAMPLE_SHADOWMASK(input.lightmapUV);

                return inputData;
            }

            half4 LitPassFragment(FragmentPassInput input) : SV_Target
            {
                SurfaceData surfaceData = CreateSurfaceData(input);
                InputData inputData = CreateInputData(input, surfaceData.normalTS);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);

                return color;
            }

            // half4 LitPassFragment(FragmentPassInput input) : SV_Target
            // {
            //     float4 w = input.fixedMaterialWeights;
            //     float sum = w.x + w.y + w.z + w.w;

            //     if (sum > 0.0001f)
            //     {
            //         w /= sum;
            //     }

            //     half3 debugColor = half3(w.x, w.y, w.z) + w.w.xxx;
            //     return half4(debugColor, 1.0h);
            // }

            ENDHLSL

        }

        Pass
        {

            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            HLSLPROGRAM

            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #include "Assets/Shaders/Voxels/Include/ShadowCasterPass.hlsl"

            ENDHLSL

        }

        Pass
        {

            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            HLSLPROGRAM

            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #include "Assets/Shaders/Voxels/Include/DepthOnlyPass.hlsl"

            ENDHLSL

        }

        Pass
        {

            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            HLSLPROGRAM

            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment

            #include "Assets/Shaders/Voxels/Include/DepthNormalsPass.hlsl"

            ENDHLSL

        }
    }
}