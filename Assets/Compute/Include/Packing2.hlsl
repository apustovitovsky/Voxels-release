#ifndef TUNTENFISCH_PACKING
#define TUNTENFISCH_PACKING

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

uint PackFloats(float2 unpackedFloats)
{
    uint packedFloats = f32tof16(unpackedFloats.x) | f32tof16(unpackedFloats.y) << 16;

    return packedFloats;
}

float2 UnpackFloats(uint packedFloats)
{
    float2 unpackedFloats = float2
    (
        f16tof32(packedFloats),
        f16tof32(packedFloats >> 16)
    );

    return unpackedFloats;
}

uint2 PackTop4MaterialWeights8Bit(uint4 materialIndices, float4 materialWeights)
{
    materialWeights = max(materialWeights, 0.0f);

    float sum = materialWeights.x + materialWeights.y + materialWeights.z + materialWeights.w;

    if (sum > 0.0001f)
    {
        materialWeights /= sum;
    }
    else
    {
        materialIndices = uint4(materialIndices.x, 0, 0, 0);
        materialWeights = float4(1, 0, 0, 0);
    }

    uint4 q = (uint4)round(saturate(materialWeights) * 255.0f);

    return uint2(
        ((materialIndices.x & 15u)      ) |
        ((materialIndices.y & 15u) <<  4) |
        ((materialIndices.z & 15u) <<  8) |
        ((materialIndices.w & 15u) << 12),
        ((q.x & 255u)      ) |
        ((q.y & 255u) <<  8) |
        ((q.z & 255u) << 16) |
        ((q.w & 255u) << 24)
    );
}

void UnpackTop4MaterialWeights8Bit(
    uint2 packed,
    out uint4 materialIndices,
    out float4 materialWeights)
{
    materialIndices = uint4(
        (packed.x      ) & 15u,
        (packed.x >>  4) & 15u,
        (packed.x >>  8) & 15u,
        (packed.x >> 12) & 15u
    );

    materialWeights = float4(
        (packed.y      ) & 255u,
        (packed.y >>  8) & 255u,
        (packed.y >> 16) & 255u,
        (packed.y >> 24) & 255u
    ) / 255.0f;

    float sum = materialWeights.x + materialWeights.y + materialWeights.z + materialWeights.w;

    if (sum > 0.0001f)
    {
        materialWeights /= sum;
    }
    else
    {
        materialIndices = uint4(materialIndices.x, 0, 0, 0);
        materialWeights = float4(1, 0, 0, 0);
    }
}

#endif
