#ifndef TUNTENFISCH_VOXELS_VOXEL
#define TUNTENFISCH_VOXELS_VOXEL

#include "Assets/Compute/Include/Packing.hlsl"
#include "Assets/Compute/Voxels/Include/Material.hlsl"
#include "Assets/Compute/Voxels/Include/MaterialWeights.hlsl"

struct Voxel
{
    float4 valueAndGradient;
    uint2 materialWeights;

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

    uint2 GetMaterialWeights()
    {
        return materialWeights;
    }

    void SetMaterialWeights(uint2 newMaterialWeights)
    {
        materialWeights = newMaterialWeights;
    }

    static Voxel Create()
    {
        return Voxel::Create(float4(0.0f, 0.0f, 0.0f, 0.0f), CreateSingleMaterialWeights(0));
    }

    static Voxel Create(float4 valueAndGradient, uint2 materialWeights)
    {
        Voxel voxel;
        voxel.valueAndGradient = valueAndGradient;
        voxel.materialWeights = materialWeights;

        return voxel;
    }

    static Voxel Create(float4 valueAndGradient, uint materialIndex)
    {
        Voxel voxel;
        voxel.valueAndGradient = valueAndGradient;
        voxel.materialWeights = CreateSingleMaterialWeights(materialIndex);

        return voxel;
    }
};

struct PackedVoxel
{
    uint2 packedValueAndGradient;
    uint2 packedMaterialWeights;

    static PackedVoxel Create(uint2 packedValueAndGradient, uint2 packedMaterialWeights)
    {
        PackedVoxel packedVoxel;
        packedVoxel.packedValueAndGradient = packedValueAndGradient;
        packedVoxel.packedMaterialWeights = packedMaterialWeights;

        return packedVoxel;
    }
};

PackedVoxel PackVoxel(Voxel voxel)
{
    // Technically value can be any arbitrarily large float so using only 16 bits for
    // precision wouldn't be great. But since we only actually do something with a
    // voxel's value if it's close to 0 (that means the voxel is near the isosurface)
    // it shouldn't pose a problem.
    uint packedValue = f32tof16(voxel.GetValue());
    uint packedGradient = PackFloats(PackNormalOctQuadEncode(normalize(voxel.GetGradient())));
    uint2 packedValueAndGradient = uint2(packedValue, packedGradient);
    uint2 packedMaterialWeights = voxel.GetMaterialWeights();

    return PackedVoxel::Create(packedValueAndGradient, packedMaterialWeights);
}

Voxel UnpackVoxel(PackedVoxel voxel)
{
    float value = f16tof32(voxel.packedValueAndGradient.x);
    float3 gradient = UnpackNormalOctQuadEncode(UnpackFloats(voxel.packedValueAndGradient.y));

    return Voxel::Create(float4(value, gradient), voxel.packedMaterialWeights);
}

#endif
