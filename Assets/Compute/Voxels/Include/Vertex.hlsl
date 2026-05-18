#ifndef TUNTENFISCH_VOXELS_VERTEX
#define TUNTENFISCH_VOXELS_VERTEX

#include "Assets/Compute/Include/Packing.hlsl"

struct Vertex
{
    float3 position;
    uint2 halfPrecisionNormal;
    uint materialIndex;
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
        return float3(UnpackFloats(halfPrecisionNormal.x), UnpackFloats(halfPrecisionNormal.y).x);
    }

    void SetNormal(float3 newNormal)
    {
        halfPrecisionNormal = uint2(PackFloats(newNormal.xy), PackFloats(float2(newNormal.z, 0.0f)));
    }

    uint GetMaterialIndex()
    {
        return materialIndex;
    }

    void SetMaterialIndex(uint newMaterialIndex)
    {
        materialIndex = newMaterialIndex;
    }

    // half4 GetMaterialWeights()
    // {
    //     return materialWeights;
    // }

    // void SetMaterialWeights(half4 newMaterialWeights)
    // {
    //     materialWeights = newMaterialWeights;
    // }

    float4 GetMaterialWeights()
    {
        uint wx = packedMaterialWeights.x & 0xFFFF;
        uint wy = packedMaterialWeights.x >> 16;
        uint wz = packedMaterialWeights.y & 0xFFFF;
        uint ww = packedMaterialWeights.y >> 16;

        return float4(wx, wy, wz, ww) / 65535.0f;
    }

    void SetMaterialWeights(float4 weights)
    {
        weights = saturate(weights);

        uint4 quantized = (uint4)round(weights * 65535.0f);

        packedMaterialWeights.x = (quantized.x & 0xFFFF) | (quantized.y << 16);
        packedMaterialWeights.y = (quantized.z & 0xFFFF) | (quantized.w << 16);
    }

    // static Vertex Create(
    //     float3 position = 0.0f,
    //     float3 normal = 0.0f,
    //     uint materialIndex = 0,
    //     half4 materialWeights = 0.0f)
    // {
    //     Vertex vertex;
    //     vertex.position = position;
    //     vertex. halfPrecisionNormal = uint2(PackFloats(normal.xy), PackFloats(float2(normal.z, 0.0f)));
    //     vertex.materialIndex = materialIndex;
    //     vertex.materialWeights = materialWeights;
    //     return vertex;
    // }
        static Vertex Create(float3 position = 0.0f, float3 normal = 0.0f, uint materialIndex = 0, float4 materialWeights = 0.0f)
    {
        Vertex vertex;
        vertex.position = position;
        vertex.halfPrecisionNormal = uint2(PackFloats(normal.xy), PackFloats(float2(normal.z, 0.0f)));
        vertex.materialIndex = materialIndex;
        vertex.SetMaterialWeights(materialWeights);
        return vertex;
    }
};

#endif