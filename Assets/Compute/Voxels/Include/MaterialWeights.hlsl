#ifndef TUNTENFISCH_VOXELS_MATERIAL_WEIGHTS
#define TUNTENFISCH_VOXELS_MATERIAL_WEIGHTS

#include "Assets/Compute/Voxels/Include/SurfaceMaterial.hlsl"

static const uint numberOfGlobalMaterialSlots = 8;

uint GetMaterialWeight(uint2 materialWeights, uint materialIndex)
{
    uint packedWeights = materialIndex < 4 ? materialWeights.x : materialWeights.y;
    uint slot = materialIndex & 3;

    return (packedWeights >> (slot * 8)) & 0xFF;
}

uint2 CreateMaterialWeights(uint4 low, uint4 high)
{
    return uint2(PackBytes(low), PackBytes(high));
}

uint4 SetUint4Component(uint4 values, uint index, uint value)
{
    if (index == 0)
    {
        values.x = value;
    }
    else if (index == 1)
    {
        values.y = value;
    }
    else if (index == 2)
    {
        values.z = value;
    }
    else if (index == 3)
    {
        values.w = value;
    }

    return values;
}

uint GetUint4Component(uint4 values, uint index)
{
    if (index == 0)
    {
        return values.x;
    }
    else if (index == 1)
    {
        return values.y;
    }
    else if (index == 2)
    {
        return values.z;
    }

    return values.w;
}

uint2 CreateSingleMaterialWeights(uint materialIndex)
{
    uint4 low = 0;
    uint4 high = 0;

    if (materialIndex < 4)
    {
        low = SetUint4Component(low, materialIndex, 255);
    }
    else if (materialIndex < numberOfGlobalMaterialSlots)
    {
        high = SetUint4Component(high, materialIndex - 4, 255);
    }

    return CreateMaterialWeights(low, high);
}

uint PackMaterialWeights4(uint4 weights)
{
    return PackBytes(weights);
}

uint GetDominantMaterialIndex(uint2 materialWeights)
{
    uint dominantMaterialIndex = 0;
    uint dominantWeight = GetMaterialWeight(materialWeights, 0);

    [unroll]
    for (uint materialIndex = 1; materialIndex < numberOfGlobalMaterialSlots; materialIndex++)
    {
        uint weight = GetMaterialWeight(materialWeights, materialIndex);

        if (weight > dominantWeight)
        {
            dominantMaterialIndex = materialIndex;
            dominantWeight = weight;
        }
    }

    return dominantMaterialIndex;
}

uint BuildTop4MaterialIndices(uint2 weights0, uint2 weights1, uint2 weights2)
{
    uint scores[numberOfGlobalMaterialSlots];

    [unroll]
    for (uint materialIndex = 0; materialIndex < numberOfGlobalMaterialSlots; materialIndex++)
    {
        scores[materialIndex] =
            GetMaterialWeight(weights0, materialIndex) +
            GetMaterialWeight(weights1, materialIndex) +
            GetMaterialWeight(weights2, materialIndex);
    }

    uint4 top4Indices = 0;

    [unroll]
    for (uint topIndex = 0; topIndex < 4; topIndex++)
    {
        bool found = false;
        uint bestMaterialIndex = 0;
        uint bestScore = 0;

        [unroll]
        for (uint candidateMaterialIndex = 0; candidateMaterialIndex < numberOfGlobalMaterialSlots; candidateMaterialIndex++)
        {
            uint score = scores[candidateMaterialIndex];

            if (score == 0)
            {
                continue;
            }

            if (!found || score > bestScore || (score == bestScore && candidateMaterialIndex < bestMaterialIndex))
            {
                found = true;
                bestMaterialIndex = candidateMaterialIndex;
                bestScore = score;
            }
        }

        if (found)
        {
            top4Indices = SetUint4Component(top4Indices, topIndex, bestMaterialIndex);
            scores[bestMaterialIndex] = 0;
        }
        else
        {
            top4Indices = SetUint4Component(top4Indices, topIndex, 0);
        }
    }

    return PackBytes(top4Indices);
}

uint PickWeightsForTop4(uint2 materialWeights, uint top4Indices)
{
    uint4 indices = UnpackBytes(top4Indices);

    return PackMaterialWeights4(uint4
    (
        GetMaterialWeight(materialWeights, indices.x),
        GetMaterialWeight(materialWeights, indices.y),
        GetMaterialWeight(materialWeights, indices.z),
        GetMaterialWeight(materialWeights, indices.w)
    ));
}

#endif
