#ifndef TUNTENFISCH_VOXELS_MATERIAL_SET_MERGE
#define TUNTENFISCH_VOXELS_MATERIAL_SET_MERGE

#include "Assets/Compute/Voxels/Include/MaterialSet/MaterialSet.hlsl"

static const uint mergeBufferLength = 16u;

struct MergeCandidate
{
    uint materialId;
    uint score;

    static MergeCandidate Create(uint materialId, uint score)
    {
        MergeCandidate candidate;
        candidate.materialId = materialId;
        candidate.score = score;

        return candidate;
    }

    void Add(uint value)
    {
        uint newScore = score + value;
        score = newScore < score ? 0xFFFFFFFFu : newScore;
    }
};

static uint mergeCandidateCount = 0u;
static MergeCandidate mergeCandidates[mergeBufferLength];

void MaterialSetMergeReset()
{
    [unroll(mergeBufferLength)]
    for (uint candidateIndex = 0u; candidateIndex < mergeBufferLength; candidateIndex++)
    {
        mergeCandidates[candidateIndex] = MergeCandidate::Create(materialSetInvalidMaterialId, 0u);
    }

    mergeCandidateCount = 0u;
}

bool MaterialSetMergeTryFindCandidateIndex(uint materialId, out uint candidateIndex)
{
    [unroll(mergeBufferLength)]
    for (candidateIndex = 0u; candidateIndex < mergeBufferLength; candidateIndex++)
    {
        if (candidateIndex >= mergeCandidateCount)
        {
            break;
        }

        if (mergeCandidates[candidateIndex].materialId == materialId)
        {
            return true;
        }
    }

    candidateIndex = 0u;
    return false;
}

uint MaterialSetMergeFindWeakestCandidateIndex()
{
    uint weakestIndex = 0u;
    MergeCandidate weakestCandidate = mergeCandidates[0u];

    [unroll(mergeBufferLength)]
    for (uint candidateIndex = 1u; candidateIndex < mergeBufferLength; candidateIndex++)
    {
        MergeCandidate candidate = mergeCandidates[candidateIndex];

        if (MaterialSetIsWeaker(
            candidate.score,
            candidate.materialId,
            weakestCandidate.score,
            weakestCandidate.materialId))
        {
            weakestIndex = candidateIndex;
            weakestCandidate = candidate;
        }
    }

    return weakestIndex;
}

void MaterialSetMergeAccumulateCandidate(uint materialId, uint contribution)
{
    if (contribution == 0u || materialId == materialSetInvalidMaterialId)
    {
        return;
    }

    uint candidateIndex;

    if (MaterialSetMergeTryFindCandidateIndex(materialId, candidateIndex))
    {
        mergeCandidates[candidateIndex].Add(contribution);
        return;
    }

    if (mergeCandidateCount < mergeBufferLength)
    {
        mergeCandidates[mergeCandidateCount] = MergeCandidate::Create(materialId, contribution);
        mergeCandidateCount++;

        return;
    }

    uint weakestIndex = MaterialSetMergeFindWeakestCandidateIndex();
    MergeCandidate weakestCandidate = mergeCandidates[weakestIndex];

    if (!MaterialSetIsBetter(
        contribution,
        materialId,
        weakestCandidate.score,
        weakestCandidate.materialId))
    {
        return;
    }

    mergeCandidates[weakestIndex] = MergeCandidate::Create(materialId, contribution);
}

void MaterialSetMergeAccumulate(uint2 materialSet)
{
    uint4 materialIds = MaterialSetUnpackIds(materialSet);
    uint4 weights = MaterialSetUnpackWeights(materialSet);

    for (uint slotIndex = 0u; slotIndex < 4u; slotIndex++)
    {
        MaterialSetMergeAccumulateCandidate(materialIds[slotIndex], weights[slotIndex]);
    }
}

bool MaterialSetMergeIsCandidateSelected(uint candidateIndex, uint4 selectedCandidateIndices, uint selectedCount)
{
    return (selectedCount > 0u && candidateIndex == selectedCandidateIndices.x)
        || (selectedCount > 1u && candidateIndex == selectedCandidateIndices.y)
        || (selectedCount > 2u && candidateIndex == selectedCandidateIndices.z)
        || (selectedCount > 3u && candidateIndex == selectedCandidateIndices.w);
}

void MaterialSetMergeSelectCandidates(out uint4 selectedIds, out uint4 selectedScores)
{
    selectedIds = materialSetInvalidMaterialIds;
    selectedScores = 0u;

    uint4 selectedCandidateIndices = 0u;

    for (uint selectedIndex = 0u; selectedIndex < 4u; selectedIndex++)
    {
        bool found = false;
        uint bestCandidateIndex = 0u;
        MergeCandidate bestCandidate = MergeCandidate::Create(materialSetInvalidMaterialId, 0u);

        for (uint candidateIndex = 0u; candidateIndex < mergeBufferLength; candidateIndex++)
        {
            if (candidateIndex >= mergeCandidateCount)
            {
                break;
            }

            if (MaterialSetMergeIsCandidateSelected(candidateIndex, selectedCandidateIndices, selectedIndex))
            {
                continue;
            }

            MergeCandidate candidate = mergeCandidates[candidateIndex];

            if (candidate.score == 0u)
            {
                continue;
            }

            if (!found || MaterialSetIsBetter(
                candidate.score,
                candidate.materialId,
                bestCandidate.score,
                bestCandidate.materialId))
            {
                found = true;
                bestCandidateIndex = candidateIndex;
                bestCandidate = candidate;
            }
        }

        if (!found)
        {
            break;
        }

        selectedIds = MaterialSetSetComponent(selectedIds, selectedIndex, bestCandidate.materialId);
        selectedScores = MaterialSetSetComponent(selectedScores, selectedIndex, bestCandidate.score);
        selectedCandidateIndices = MaterialSetSetComponent(selectedCandidateIndices, selectedIndex, bestCandidateIndex);
    }
}

uint MaterialSetMergeComputeIds()
{
    uint4 selectedIds;
    uint4 selectedScores;
    MaterialSetMergeSelectCandidates(selectedIds, selectedScores);

    return MaterialSetPackBytes(selectedIds);
}

uint2 MaterialSetMergeCompute()
{
    uint4 selectedIds;
    uint4 selectedScores;
    MaterialSetMergeSelectCandidates(selectedIds, selectedScores);

    uint scoreSum = selectedScores.x + selectedScores.y + selectedScores.z + selectedScores.w;

    if (scoreSum == 0u)
    {
        return MaterialSetCreateSingle(0u);
    }

    uint4 weights = MaterialSetNormalizeWeightsByScoreSum(selectedScores);
    MaterialSetCorrectWeightSum(weights);

    return MaterialSetPack(selectedIds, weights);
}

uint MaterialSetMergeComputeIds3(uint2 set0, uint2 set1, uint2 set2)
{
    MaterialSetMergeReset();
    MaterialSetMergeAccumulate(set0);
    MaterialSetMergeAccumulate(set1);
    MaterialSetMergeAccumulate(set2);

    return MaterialSetMergeComputeIds();
}

uint2 MaterialSetMergeCompute3(uint2 set0, uint2 set1, uint2 set2)
{
    MaterialSetMergeReset();
    MaterialSetMergeAccumulate(set0);
    MaterialSetMergeAccumulate(set1);
    MaterialSetMergeAccumulate(set2);

    return MaterialSetMergeCompute();
}

#endif
