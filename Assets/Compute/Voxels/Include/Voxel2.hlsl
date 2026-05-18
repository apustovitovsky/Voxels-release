#ifndef TUNTENFISCH_VOXELS_VOXEL
#define TUNTENFISCH_VOXELS_VOXEL

#include "Assets/Compute/Include/Packing2.hlsl"
#include "Assets/Compute/Voxels/Include/Material.hlsl"

struct Voxel
{
    float4 valueAndGradient;
    uint4 materialSetIndices;
    float4 materialSetWeights;

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

    void SetMaterialSet(uint4 newMaterialIndices, float4 newMaterialWeights)
    {
        materialSetIndices = newMaterialIndices;
        materialSetWeights = newMaterialWeights;
    }

    void SetMaterialIndex(uint materialIndex)
    {
        materialSetIndices = uint4(materialIndex & 15u, 0u, 0u, 0u);
        materialSetWeights = float4(1.0f, 0.0f, 0.0f, 0.0f);
    }

    static Voxel Create(
        float4 valueAndGradient = 0.0f,
        uint4 materialSetIndices = uint4(0, 0, 0, 0),
        float4 materialSetWeights = float4(1, 0, 0, 0))
    {
        Voxel voxel;
        voxel.valueAndGradient = valueAndGradient;
        voxel.materialSetIndices = materialSetIndices;
        voxel.materialSetWeights = materialSetWeights;

        return voxel;
    }

    static Voxel Create(
        float4 valueAndGradient,
        uint materialIndex)
    {
        Voxel voxel;
        voxel.valueAndGradient = valueAndGradient;
        voxel.SetMaterialIndex(materialIndex);

        return voxel;
    }
};

struct PackedVoxel
{
    uint2 packedValueAndGradient;
    uint2 packedMaterialSet;

    static PackedVoxel Create(
        uint2 packedValueAndGradient,
        uint2 packedMaterialSet)
    {
        PackedVoxel packedVoxel;
        packedVoxel.packedValueAndGradient = packedValueAndGradient;
        packedVoxel.packedMaterialSet = packedMaterialSet;

        return packedVoxel;
    }
};

PackedVoxel PackVoxel(Voxel voxel)
{
    uint2 packedValueAndGradient = uint2(
        asuint(voxel.GetValue()),
        PackFloats(PackNormalOctQuadEncode(normalize(voxel.GetGradient()))));

    uint2 packedMaterialSet = PackTop4MaterialWeights8Bit(
        voxel.materialSetIndices,
        voxel.materialSetWeights
    );

    return PackedVoxel::Create(
        packedValueAndGradient,
        packedMaterialSet
    );
}

Voxel UnpackVoxel(PackedVoxel packedVoxel)
{
    float value = asfloat(packedVoxel.packedValueAndGradient.x);
    float3 gradient = UnpackNormalOctQuadEncode(UnpackFloats(packedVoxel.packedValueAndGradient.y));

    uint4 materialSetIndices;
    float4 materialSetWeights;

    UnpackTop4MaterialWeights8Bit(
        packedVoxel.packedMaterialSet,
        materialSetIndices,
        materialSetWeights
    );

    return Voxel::Create(
        float4(value, gradient),
        materialSetIndices,
        materialSetWeights
    );
}

#endif
