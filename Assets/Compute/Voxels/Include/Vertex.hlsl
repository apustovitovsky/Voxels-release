#ifndef TUNTENFISCH_VOXELS_VERTEX
#define TUNTENFISCH_VOXELS_VERTEX

#include "Assets/Compute/Include/Packing2.hlsl"

struct Vertex
{
    float3 position;
    uint2 halfPrecisionNormal;
    uint2 packedMaterialSet;

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

    void GetMaterialSet(out uint4 materialSetIndices, out float4 materialSetWeights)
    {
        UnpackTop4MaterialWeights8Bit(packedMaterialSet, materialSetIndices, materialSetWeights);
    }

    void SetMaterialSet(uint4 materialSetIndices, float4 materialSetWeights)
    {
        packedMaterialSet = PackTop4MaterialWeights8Bit(materialSetIndices, materialSetWeights);
    }

    static Vertex Create(
        float3 position = 0.0f,
        float3 normal = 0.0f,
        uint4 materialSetIndices = uint4(0, 0, 0, 0),
        float4 materialSetWeights = float4(1, 0, 0, 0))
    {
        Vertex vertex;
        vertex.position = position;
        vertex.halfPrecisionNormal = uint2(
            PackFloats(normal.xy),
            PackFloats(float2(normal.z, 0.0f))
        );
        vertex.SetMaterialSet(materialSetIndices, materialSetWeights);

        return vertex;
    }

};

#endif
