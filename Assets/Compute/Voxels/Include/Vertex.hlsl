#ifndef TUNTENFISCH_VOXELS_VERTEX
#define TUNTENFISCH_VOXELS_VERTEX

#include "Assets/Compute/Include/Packing2.hlsl"
#include "Assets/Compute/Include/MaterialWeights.hlsl"

struct Vertex
{
    float3 position;
    uint2 halfPrecisionNormal;
    uint materialIndex;

    // weights 0..3 in x
    // weights 4..7 in y
    uint2 packedMaterialWeights;

    float3 GetPosition()
    {
        return position;
    }

    void SetPosition(float3 newPosition)
    {
        position = newPosition;
    }

    float3 GetNormal()
    {
        return float3(
            UnpackFloats(halfPrecisionNormal.x),
            UnpackFloats(halfPrecisionNormal.y).x
        );
    }

    void SetNormal(float3 newNormal)
    {
        halfPrecisionNormal = uint2(
            PackFloats(newNormal.xy),
            PackFloats(float2(newNormal.z, 0.0f))
        );
    }

    uint GetMaterialIndex()
    {
        return materialIndex;
    }

    void SetMaterialIndex(uint newMaterialIndex)
    {
        materialIndex = newMaterialIndex;
    }

    float4 GetMaterialWeights0()
    {
        return UnpackBytes01(packedMaterialWeights.x);
    }

    float4 GetMaterialWeights1()
    {
        return UnpackBytes01(packedMaterialWeights.y);
    }

    void SetMaterialWeights(float4 weights0, float4 weights1)
    {
        NormalizeMaterialWeights(weights0, weights1);

        packedMaterialWeights = uint2(
            PackBytes01(weights0),
            PackBytes01(weights1)
        );
    }

    static Vertex Create(
        float3 position = 0.0f,
        float3 normal = 0.0f,
        uint materialIndex = 0,
        float4 materialWeights0 = 0.0f,
        float4 materialWeights1 = 0.0f)
    {
        Vertex vertex;
        vertex.position = position;
        vertex.halfPrecisionNormal = uint2(
            PackFloats(normal.xy),
            PackFloats(float2(normal.z, 0.0f))
        );
        vertex.materialIndex = materialIndex;
        vertex.SetMaterialWeights(materialWeights0, materialWeights1);

        return vertex;
    }
};

#endif