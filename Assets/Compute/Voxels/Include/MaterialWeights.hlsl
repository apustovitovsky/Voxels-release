#ifndef TUNTENFISCH_VOXELS_MATERIAL_WEIGHTS
#define TUNTENFISCH_VOXELS_MATERIAL_WEIGHTS

#define MaterialWeights8 uint2

static const uint numberOfGlobalMaterialSlots = 8;

uint GetMaterialWeight8(MaterialWeights8 weights, uint materialIndex)
{
    uint packedWeights = materialIndex < 4 ? weights.x : weights.y;
    uint slot = materialIndex & 3;

    return (packedWeights >> (slot * 8)) & 0xFF;
}

MaterialWeights8 CreateMaterialWeights8(uint4 low, uint4 high)
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

MaterialWeights8 CreateSingleMaterialWeights(uint materialIndex)
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

    return CreateMaterialWeights8(low, high);
}

uint PackMaterialWeights4(uint4 weights)
{
    return PackBytes(weights);
}

uint BuildTop4MaterialIndices(MaterialWeights8 w0, MaterialWeights8 w1, MaterialWeights8 w2)
{
    uint scores[numberOfGlobalMaterialSlots];

    [unroll]
    for (uint materialIndex = 0; materialIndex < numberOfGlobalMaterialSlots; materialIndex++)
    {
        scores[materialIndex] =
            GetMaterialWeight8(w0, materialIndex) +
            GetMaterialWeight8(w1, materialIndex) +
            GetMaterialWeight8(w2, materialIndex);
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

uint PickWeightsForTop4(MaterialWeights8 weights, uint top4Indices)
{
    uint4 indices = UnpackBytes(top4Indices);

    return PackMaterialWeights4(uint4
    (
        GetMaterialWeight8(weights, indices.x),
        GetMaterialWeight8(weights, indices.y),
        GetMaterialWeight8(weights, indices.z),
        GetMaterialWeight8(weights, indices.w)
    ));
}

#endif
