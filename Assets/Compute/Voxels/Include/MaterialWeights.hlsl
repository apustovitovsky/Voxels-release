#ifndef TUNTENFISCH_VOXELS_MATERIAL_WEIGHTS
#define TUNTENFISCH_VOXELS_MATERIAL_WEIGHTS

#include "Assets/Compute/Voxels/Include/TriangleMaterialSet.hlsl"
#include "Assets/Compute/Voxels/Include/MaterialSet/MaterialSet.hlsl"

static const uint numberOfGlobalMaterialSlots = 8;

uint4 UnpackMaterialWeights0(uint2 materialWeights)
{
    return UnpackBytes(materialWeights.x);
}

uint4 UnpackMaterialWeights1(uint2 materialWeights)
{
    return UnpackBytes(materialWeights.y);
}

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

uint2 MaterialWeightsToMaterialSet(uint2 materialWeights)
{
    uint4 score0 = UnpackMaterialWeights0(materialWeights);
    uint4 score1 = UnpackMaterialWeights1(materialWeights);
    uint4 selectedIds = materialSetInvalidMaterialIds;
    uint4 selectedWeights = 0u;

    for (uint selectedIndex = 0u; selectedIndex < 4u; selectedIndex++)
    {
        bool found = false;
        uint bestMaterialId = materialSetInvalidMaterialId;
        uint bestWeight = 0u;

        for (uint materialId = 0u; materialId < numberOfGlobalMaterialSlots; materialId++)
        {
            uint weight = materialId < 4u
                ? GetUint4Component(score0, materialId)
                : GetUint4Component(score1, materialId - 4u);

            if (weight == 0u)
            {
                continue;
            }

            if (!found || MaterialSetIsBetter(weight, materialId, bestWeight, bestMaterialId))
            {
                found = true;
                bestMaterialId = materialId;
                bestWeight = weight;
            }
        }

        if (!found)
        {
            break;
        }

        selectedIds = MaterialSetSetComponent(selectedIds, selectedIndex, bestMaterialId);
        selectedWeights = MaterialSetSetComponent(selectedWeights, selectedIndex, bestWeight);

        if (bestMaterialId < 4u)
        {
            score0 = SetUint4Component(score0, bestMaterialId, 0u);
        }
        else
        {
            score1 = SetUint4Component(score1, bestMaterialId - 4u, 0u);
        }
    }

    uint selectedWeightSum = selectedWeights.x + selectedWeights.y + selectedWeights.z + selectedWeights.w;

    if (selectedWeightSum == 0u)
    {
        return MaterialSetCreateSingle(0u);
    }

    return MaterialSetPack(selectedIds, selectedWeights);
}

uint PackMaterialWeights4(uint4 weights)
{
    return PackBytes(weights);
}

uint SumMaterialWeightScores(uint4 score0, uint4 score1)
{
    return
        score0.x + score0.y + score0.z + score0.w +
        score1.x + score1.y + score1.z + score1.w;
}

void CorrectMaterialWeightSum(inout uint4 weights0, inout uint4 weights1)
{
    uint sum = SumMaterialWeightScores(weights0, weights1);

    if (sum == 255u)
    {
        return;
    }

    if (sum < 255u)
    {
        uint deficit = 255u - sum;

        if (weights0.x > 0) { weights0.x += deficit; return; }
        if (weights0.y > 0) { weights0.y += deficit; return; }
        if (weights0.z > 0) { weights0.z += deficit; return; }
        if (weights0.w > 0) { weights0.w += deficit; return; }

        if (weights1.x > 0) { weights1.x += deficit; return; }
        if (weights1.y > 0) { weights1.y += deficit; return; }
        if (weights1.z > 0) { weights1.z += deficit; return; }
        if (weights1.w > 0) { weights1.w += deficit; return; }

        weights0.x = 255u;

        return;
    }

    uint excess = sum - 255u;

    if (weights0.x >= excess) { weights0.x -= excess; return; }
    if (weights0.y >= excess) { weights0.y -= excess; return; }
    if (weights0.z >= excess) { weights0.z -= excess; return; }
    if (weights0.w >= excess) { weights0.w -= excess; return; }

    if (weights1.x >= excess) { weights1.x -= excess; return; }
    if (weights1.y >= excess) { weights1.y -= excess; return; }
    if (weights1.z >= excess) { weights1.z -= excess; return; }
    if (weights1.w >= excess) { weights1.w -= excess; return; }

    if (weights0.x > 0)
    {
        uint reduction = min(weights0.x, excess);
        weights0.x -= reduction;
        excess -= reduction;
    }

    if (weights0.y > 0 && excess > 0)
    {
        uint reduction = min(weights0.y, excess);
        weights0.y -= reduction;
        excess -= reduction;
    }

    if (weights0.z > 0 && excess > 0)
    {
        uint reduction = min(weights0.z, excess);
        weights0.z -= reduction;
        excess -= reduction;
    }

    if (weights0.w > 0 && excess > 0)
    {
        uint reduction = min(weights0.w, excess);
        weights0.w -= reduction;
        excess -= reduction;
    }

    if (weights1.x > 0 && excess > 0)
    {
        uint reduction = min(weights1.x, excess);
        weights1.x -= reduction;
        excess -= reduction;
    }

    if (weights1.y > 0 && excess > 0)
    {
        uint reduction = min(weights1.y, excess);
        weights1.y -= reduction;
        excess -= reduction;
    }

    if (weights1.z > 0 && excess > 0)
    {
        uint reduction = min(weights1.z, excess);
        weights1.z -= reduction;
        excess -= reduction;
    }

    if (weights1.w > 0 && excess > 0)
    {
        uint reduction = min(weights1.w, excess);
        weights1.w -= reduction;
    }
}

uint4 Div255(uint4 value)
{
    return (value + 128u + ((value + 128u) >> 8)) >> 8;
}

uint2 AverageMaterialWeightScores(uint4 score0, uint4 score1, uint sourceCount)
{
    if (sourceCount == 0)
    {
        return CreateSingleMaterialWeights(0);
    }

    uint4 weights0 = score0 / sourceCount;
    uint4 weights1 = score1 / sourceCount;
    CorrectMaterialWeightSum(weights0, weights1);

    return CreateMaterialWeights(weights0, weights1);
}

uint2 BlendMaterialWeights(uint2 lhs, uint2 rhs, float alpha)
{
    uint alphaByte = (uint)round(saturate(alpha) * 255.0f);
    uint invAlphaByte = 255u - alphaByte;

    uint4 lhs0 = UnpackMaterialWeights0(lhs);
    uint4 lhs1 = UnpackMaterialWeights1(lhs);
    uint4 rhs0 = UnpackMaterialWeights0(rhs);
    uint4 rhs1 = UnpackMaterialWeights1(rhs);

    uint4 blended0 = Div255(lhs0 * invAlphaByte + rhs0 * alphaByte);
    uint4 blended1 = Div255(lhs1 * invAlphaByte + rhs1 * alphaByte);
    CorrectMaterialWeightSum(blended0, blended1);

    return CreateMaterialWeights(blended0, blended1);
}

uint BuildTop4MaterialIndices(uint2 weights0, uint2 weights1, uint2 weights2)
{
    uint4 score0 =
        UnpackMaterialWeights0(weights0) +
        UnpackMaterialWeights0(weights1) +
        UnpackMaterialWeights0(weights2);
    uint4 score1 =
        UnpackMaterialWeights1(weights0) +
        UnpackMaterialWeights1(weights1) +
        UnpackMaterialWeights1(weights2);

    uint4 top4Indices = 0;

    for (uint topIndex = 0; topIndex < 4; topIndex++)
    {
        bool found = false;
        uint bestMaterialIndex = 0;
        uint bestScore = 0;

        for (uint candidateMaterialIndex = 0; candidateMaterialIndex < numberOfGlobalMaterialSlots; candidateMaterialIndex++)
        {
            uint score = candidateMaterialIndex < 4
                ? GetUint4Component(score0, candidateMaterialIndex)
                : GetUint4Component(score1, candidateMaterialIndex - 4);

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

            if (bestMaterialIndex < 4)
            {
                score0 = SetUint4Component(score0, bestMaterialIndex, 0);
            }
            else
            {
                score1 = SetUint4Component(score1, bestMaterialIndex - 4, 0);
            }
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
