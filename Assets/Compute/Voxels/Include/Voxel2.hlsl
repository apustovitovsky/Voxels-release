#ifndef TUNTENFISCH_VOXELS_VOXEL
#define TUNTENFISCH_VOXELS_VOXEL

#include "Assets/Compute/Include/Packing2.hlsl"
#include "Assets/Compute/Voxels/Include/Material.hlsl"

struct Voxel
{
    float4 valueAndGradient;
    uint materialIndex;
    float4 materialWeights0;
    float4 materialWeights1;

    float GetValue()
    {
        return valueAndGradient.x;
    }

    void SetValue(float newValue)
    {
        valueAndGradient.x = newValue;
    }

    float3 GetGradient()
    {
        return valueAndGradient.yzw;
    }

    void SetGradient(float3 newGradient)
    {
        valueAndGradient.yzw = newGradient;
    }

    uint IsSolid()
    {
        return valueAndGradient.x >= 0.0;
    }

    float4 GetMaterialWeights0()
    {
        return materialWeights0;
    }

    float4 GetMaterialWeights1()
    {
        return materialWeights1;
    }

    void SetMaterialWeights(float4 weights0, float4 weights1)
    {
        materialWeights0 = weights0;
        materialWeights1 = weights1;
    }

    static Voxel Create(
        float4 valueAndGradient = 0.0f,
        uint materialIndex = 0,
        float4 materialWeights0 = 0.0f,
        float4 materialWeights1 = 0.0f)
    {
        Voxel voxel;
        voxel.valueAndGradient = valueAndGradient;
        voxel.materialIndex = materialIndex;
        voxel.materialWeights0 = materialWeights0;
        voxel.materialWeights1 = materialWeights1;

        return voxel;
    }
};

struct PackedVoxel
{
    uint packedValueAndMaterialIndex;
    uint packedGradient;
    uint2 packedMaterialWeights;

    static PackedVoxel Create(
        uint packedValueAndMaterialIndex,
        uint packedGradient,
        uint2 packedMaterialWeights)
    {
        PackedVoxel packedVoxel;
        packedVoxel.packedValueAndMaterialIndex = packedValueAndMaterialIndex;
        packedVoxel.packedGradient = packedGradient;
        packedVoxel.packedMaterialWeights = packedMaterialWeights;

        return packedVoxel;
    }
};

PackedVoxel PackVoxel(Voxel voxel)
{
    uint packedValueAndMaterialIndex = f32tof16(voxel.GetValue()) | voxel.materialIndex << 16;

    uint packedGradient = PackFloats(
        PackNormalOctQuadEncode(normalize(voxel.GetGradient())));

    uint2 packedWeights = uint2(
        PackBytes01(voxel.materialWeights0),
        PackBytes01(voxel.materialWeights1)
    );

    return PackedVoxel::Create(
        packedValueAndMaterialIndex,
        packedGradient,
        packedWeights);
}

Voxel UnpackVoxel(PackedVoxel packedVoxel)
{
    float value = f16tof32(packedVoxel.packedValueAndMaterialIndex);
    float3 gradient = UnpackNormalOctQuadEncode(UnpackFloats(packedVoxel.packedGradient));
    uint materialIndex = packedVoxel.packedValueAndMaterialIndex >> 16;
    float4 materialWeights0 = UnpackBytes01(packedVoxel.packedMaterialWeights.x);
    float4 materialWeights1 = UnpackBytes01(packedVoxel.packedMaterialWeights.y);

    return Voxel::Create(
        float4(value, gradient),
        materialIndex,
        materialWeights0,
        materialWeights1);
}

#endif