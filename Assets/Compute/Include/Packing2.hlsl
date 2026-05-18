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

float4 UnpackBytes01(uint packedBytes)
{
    return float4
    (
        (packedBytes      ) & 255u,
        (packedBytes >>  8) & 255u,
        (packedBytes >> 16) & 255u,
        (packedBytes >> 24) & 255u
    ) / 255.0f;
}

uint PackBytes01(float4 values)
{
    uint4 bytes = (uint4)round(saturate(values) * 255.0f);

    return
        (bytes.x      ) |
        (bytes.y <<  8) |
        (bytes.z << 16) |
        (bytes.w << 24);
}

#endif