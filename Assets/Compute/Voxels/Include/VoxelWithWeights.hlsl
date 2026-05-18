#ifndef TUNTENFISCH_VOXELS_VOXEL
#define TUNTENFISCH_VOXELS_VOXEL

#include "Assets/Compute/Include/Packing.hlsl"
#include "Assets/Compute/Voxels/Include/Material.hlsl"

float4 UnpackWeights4(uint packed)
{
    return float4
    (
        (packed      ) & 255,
        (packed >>  8) & 255,
        (packed >> 16) & 255,
        (packed >> 24) & 255
    ) / 255.0f;
}

uint PackWeights4(float4 weights)
{
    uint4 w = (uint4)round(saturate(weights) * 255.0f);

    return
        (w.x      ) |
        (w.y <<  8) |
        (w.z << 16) |
        (w.w << 24);
}

struct Voxel
{
    float4 valueAndGradient;
    uint materialIndex;
    uint2 packedMaterialWeights;

    // Fixed material weight slots:
    // 0 Dirt
    // 1 Grass
    // 2 Rock
    // 3 Snow
    // 4 Sand
    // 5 Mud
    // 6 Moss
    // 7 Gravel

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
        return UnpackWeights4(packedMaterialWeights.x);
    }

    float4 GetMaterialWeights1()
    {
        return UnpackWeights4(packedMaterialWeights.y);
    }

    void SetMaterialWeights(float4 weights0, float4 weights1)
    {
        float sum = dot(weights0, 1.0f.xxxx) + dot(weights1, 1.0f.xxxx);

        if (sum > 0.0001f)
        {
            weights0 /= sum;
            weights1 /= sum;
        }
        else
        {
            weights0 = float4(1, 0, 0, 0);
            weights1 = float4(0, 0, 0, 0);
        }

        packedMaterialWeights = uint2(PackWeights4(weights0), PackWeights4(weights1));
    }

    static Voxel Create(float4 valueAndGradient = 0.0f, uint materialIndex = 0)
    {
        Voxel voxel;
        voxel.valueAndGradient = valueAndGradient;
        voxel.materialIndex = materialIndex;

        float4 weights0 = float4(0, 0, 0, 0);
        float4 weights1 = float4(0, 0, 0, 0);

        if (materialIndex == MaterialIndex::Dirt) weights0.x = 1;
        else if (materialIndex == MaterialIndex::Grass) weights0.y = 1;
        else if (materialIndex == MaterialIndex::Rock) weights0.z = 1;
        else if (materialIndex == MaterialIndex::Snow) weights0.w = 1;
        else if (materialIndex == MaterialIndex::Sand) weights1.x = 1;
        else if (materialIndex == 5) weights1.y = 1;
        else if (materialIndex == 6) weights1.z = 1;
        else if (materialIndex == 7) weights1.w = 1;
        else weights0.x = 1;

        voxel.SetMaterialWeights(weights0, weights1);

        return voxel;
    }
};

struct PackedVoxel
{
    uint packedValueAndMaterialIndex;
    uint packedGradient;
    uint2 packedMaterialWeights;

    static PackedVoxel Create(uint packedValueAndMaterialIndex, uint packedGradient, uint2 packedMaterialWeights)
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
    // Technically value can be any arbitrarily large float so using only 16 bits for
    // precision wouldn't be great. But since we only actually do something with a
    // voxel's value if it's close to 0 (that means the voxel is near the isosurface)
    // it shouldn't pose a problem.
    uint packedValueAndMaterialIndex = f32tof16(voxel.GetValue()) | voxel.materialIndex << 16;
    uint packedGradient = PackFloats(PackNormalOctQuadEncode(normalize(voxel.GetGradient())));

    return PackedVoxel::Create(packedValueAndMaterialIndex, packedGradient, voxel.packedMaterialWeights);
}

Voxel UnpackVoxel(PackedVoxel voxel)
{
    float value = f16tof32(voxel.packedValueAndMaterialIndex);
    float3 gradient = UnpackNormalOctQuadEncode(UnpackFloats(voxel.packedGradient));
    uint materialIndex = voxel.packedValueAndMaterialIndex >> 16;
    Voxel unpackedVoxel = Voxel::Create(float4(value, gradient), materialIndex);
    unpackedVoxel.packedMaterialWeights = voxel.packedMaterialWeights;

    return unpackedVoxel;
}

#endif
