#ifndef TUNTENFISCH_VOXELS_VERTEX
#define TUNTENFISCH_VOXELS_VERTEX

#include "Assets/Compute/Include/Packing.hlsl"
#include "Assets/Compute/Voxels/Include/MaterialWeights.hlsl"

struct Vertex
{
    float3 position;
    uint2 halfPrecisionNormal;
    uint2 materialWeights;

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
        return float3(UnpackFloats(halfPrecisionNormal.x), UnpackFloats(halfPrecisionNormal.y).x);
    }

    void SetNormal(float3 newNormal)
    {
        halfPrecisionNormal = uint2(PackFloats(newNormal.xy), PackFloats(float2(newNormal.z, 0.0f)));
    }

    uint2 GetMaterialWeights()
    {
        return materialWeights;
    }

    void SetMaterialWeights(uint2 newMaterialWeights)
    {
        materialWeights = newMaterialWeights;
    }

    uint GetMaterialIndex()
    {
        return GetDominantMaterialIndex(materialWeights);
    }

    void SetMaterialIndex(uint newMaterialIndex)
    {
        materialWeights = CreateSingleMaterialWeights(newMaterialIndex);
    }

    static Vertex Create()
    {
        return Vertex::Create(float3(0.0f, 0.0f, 0.0f), float3(0.0f, 0.0f, 0.0f), CreateSingleMaterialWeights(0));
    }

    static Vertex Create(float3 position, float3 normal, uint2 materialWeights)
    {
        Vertex vertex;
        vertex.position = position;
        vertex.halfPrecisionNormal = uint2(PackFloats(normal.xy), PackFloats(float2(normal.z, 0.0f)));
        vertex.materialWeights = materialWeights;

        return vertex;
    }

    static Vertex Create(float3 position, float3 normal, uint materialIndex)
    {
        return Vertex::Create(position, normal, CreateSingleMaterialWeights(materialIndex));
    }
};

#endif
