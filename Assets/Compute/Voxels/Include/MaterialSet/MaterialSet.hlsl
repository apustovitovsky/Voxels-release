#ifndef TUNTENFISCH_VOXELS_MATERIAL_SET
#define TUNTENFISCH_VOXELS_MATERIAL_SET

static const uint materialSetInvalidMaterialId = 0xFFu;
static const uint4 materialSetInvalidMaterialIds = uint4(0xFFu, 0xFFu, 0xFFu, 0xFFu);
static const uint materialSetWeightScale = 255u;

uint MaterialSetPackBytes(uint4 values)
{
    return (values.x & 0xFFu)
        | ((values.y & 0xFFu) << 8u)
        | ((values.z & 0xFFu) << 16u)
        | ((values.w & 0xFFu) << 24u);
}

uint4 MaterialSetUnpackBytes(uint value)
{
    return uint4
    (
        value & 0xFFu,
        (value >> 8u) & 0xFFu,
        (value >> 16u) & 0xFFu,
        (value >> 24u) & 0xFFu
    );
}

uint4 MaterialSetSetComponent(uint4 values, uint index, uint value)
{
    uint4 mask = uint4(index == 0u, index == 1u, index == 2u, index == 3u);
    return values * (1u - mask) + value * mask;
}

uint4 MaterialSetUnpackIds(uint2 materialSet)
{
    return MaterialSetUnpackBytes(materialSet.x);
}

uint4 MaterialSetUnpackWeights(uint2 materialSet)
{
    return MaterialSetUnpackBytes(materialSet.y);
}

uint2 MaterialSetPack(uint4 materialIds, uint4 weights)
{
    return uint2(MaterialSetPackBytes(materialIds), MaterialSetPackBytes(weights));
}

uint2 MaterialSetCreateSingle(uint materialId)
{
    return MaterialSetPack(
        uint4(materialId, materialSetInvalidMaterialId, materialSetInvalidMaterialId, materialSetInvalidMaterialId),
        uint4(materialSetWeightScale, 0u, 0u, 0u));
}

void MaterialSetCorrectWeightSum(inout uint4 weights)
{
    uint sum = weights.x + weights.y + weights.z + weights.w;

    if (sum < materialSetWeightScale)
    {
        weights.x += materialSetWeightScale - sum;
        return;
    }

    if (sum > materialSetWeightScale)
    {
        uint excess = sum - materialSetWeightScale;

        uint reduction = min(weights.w, excess);
        weights.w -= reduction;
        excess -= reduction;

        reduction = min(weights.z, excess);
        weights.z -= reduction;
        excess -= reduction;

        reduction = min(weights.y, excess);
        weights.y -= reduction;
        excess -= reduction;

        weights.x -= min(weights.x, excess);
    }
}

uint4 MaterialSetNormalizeWeightsByScoreSum(uint4 scores)
{
    uint scoreSum = scores.x + scores.y + scores.z + scores.w;

    if (scoreSum == 0u)
    {
        return uint4(materialSetWeightScale, 0u, 0u, 0u);
    }

    uint positiveCount = (scores.x > 0u ? 1u : 0u)
        + (scores.y > 0u ? 1u : 0u)
        + (scores.z > 0u ? 1u : 0u)
        + (scores.w > 0u ? 1u : 0u);
    uint remainingWeight = materialSetWeightScale - positiveCount;

    uint4 weights = uint4
    (
        scores.x > 0u ? 1u : 0u,
        scores.y > 0u ? 1u : 0u,
        scores.z > 0u ? 1u : 0u,
        scores.w > 0u ? 1u : 0u
    );

    weights += (scores * remainingWeight) / scoreSum;

    return weights;
}

uint4 MaterialSetDiv255(uint4 value)
{
    return (value + 128u + ((value + 128u) >> 8u)) >> 8u;
}

bool MaterialSetIsBetter(uint scoreA, uint materialIdA, uint scoreB, uint materialIdB)
{
    return scoreA > scoreB || (scoreA == scoreB && materialIdA < materialIdB);
}

bool MaterialSetIsWeaker(uint scoreA, uint materialIdA, uint scoreB, uint materialIdB)
{
    return scoreA < scoreB || (scoreA == scoreB && materialIdA > materialIdB);
}

bool MaterialSetTryFindSelectedIndex(uint4 selectedIds, uint materialId, out uint selectedIndex)
{
    selectedIndex = selectedIds.x == materialId ? 0u
        : selectedIds.y == materialId ? 1u
        : selectedIds.z == materialId ? 2u
        : selectedIds.w == materialId ? 3u
        : 4u;

    return selectedIndex < 4u;
}

uint MaterialSetPickWeightsForIds(uint2 materialSet, uint4 selectedIds)
{
    uint4 sourceIds = MaterialSetUnpackIds(materialSet);
    uint4 sourceWeights = MaterialSetUnpackWeights(materialSet);

    uint4 selectedWeights = 0u;

    for (uint sourceIndex = 0u; sourceIndex < 4u; sourceIndex++)
    {
        uint sourceId = sourceIds[sourceIndex];
        uint sourceWeight = sourceWeights[sourceIndex];

        if (sourceId == materialSetInvalidMaterialId || sourceWeight == 0u)
        {
            continue;
        }

        uint selectedIndex;

        if (MaterialSetTryFindSelectedIndex(selectedIds, sourceId, selectedIndex))
        {
            selectedWeights = MaterialSetSetComponent(selectedWeights, selectedIndex, sourceWeight);
        }
    }

    return MaterialSetPackBytes(selectedWeights);
}

uint3 MaterialSetPickWeightsForIds3(uint2 materialSet0, uint2 materialSet1, uint2 materialSet2, uint packedSelectedIds)
{
    uint4 selectedIds = MaterialSetUnpackBytes(packedSelectedIds);

    return uint3(
        MaterialSetPickWeightsForIds(materialSet0, selectedIds),
        MaterialSetPickWeightsForIds(materialSet1, selectedIds),
        MaterialSetPickWeightsForIds(materialSet2, selectedIds));
}

#endif
