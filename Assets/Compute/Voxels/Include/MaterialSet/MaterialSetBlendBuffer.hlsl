#ifndef TUNTENFISCH_VOXELS_MATERIAL_SET_BLEND_BUFFER
#define TUNTENFISCH_VOXELS_MATERIAL_SET_BLEND_BUFFER

#include "Assets/Compute/Voxels/Include/MaterialSet/MaterialSet.hlsl"

static const uint materialSetBlendCandidateCount = 8u;
static uint materialSetBlendCandidateIds[materialSetBlendCandidateCount];
static uint materialSetBlendCandidateScores[materialSetBlendCandidateCount];

struct MaterialSetBlendBuffer
{
    uint candidateCount;

    void Clear()
    {
        for (uint candidateIndex = 0u; candidateIndex < materialSetBlendCandidateCount; candidateIndex++)
        {
            materialSetBlendCandidateIds[candidateIndex] = materialSetInvalidMaterialId;
            materialSetBlendCandidateScores[candidateIndex] = 0u;
        }

        candidateCount = 0u;
    }

    static MaterialSetBlendBuffer Create()
    {
        MaterialSetBlendBuffer buffer;
        buffer.Clear();

        return buffer;
    }

    uint FindCandidateIndex(uint materialId)
    {
        for (uint candidateIndex = 0u; candidateIndex < materialSetBlendCandidateCount; candidateIndex++)
        {
            if (candidateIndex >= candidateCount)
            {
                break;
            }

            if (materialSetBlendCandidateIds[candidateIndex] == materialId)
            {
                return candidateIndex;
            }
        }

        return materialSetBlendCandidateCount;
    }

    void AccumulateCandidate(uint materialId, uint contribution)
    {
        if (contribution == 0u || materialId == materialSetInvalidMaterialId)
        {
            return;
        }

        uint candidateIndex = FindCandidateIndex(materialId);

        if (candidateIndex < materialSetBlendCandidateCount)
        {
            uint oldScore = materialSetBlendCandidateScores[candidateIndex];
            uint newScore = oldScore + contribution;

            if (newScore < oldScore)
            {
                newScore = 0xFFFFFFFFu;
            }

            materialSetBlendCandidateScores[candidateIndex] = newScore;

            return;
        }

        if (candidateCount >= materialSetBlendCandidateCount)
        {
            return;
        }

        materialSetBlendCandidateIds[candidateCount] = materialId;
        materialSetBlendCandidateScores[candidateCount] = contribution;
        candidateCount++;
    }

    void AccumulateWeighted(uint2 materialSet, uint weight)
    {
        uint4 materialIds = MaterialSetUnpackIds(materialSet);
        uint4 weights = MaterialSetUnpackWeights(materialSet);

        for (uint slotIndex = 0u; slotIndex < 4u; slotIndex++)
        {
            AccumulateCandidate(materialIds[slotIndex], weights[slotIndex] * weight);
        }
    }

    void Accumulate(uint2 materialSetA, uint2 materialSetB, uint alpha)
    {
        alpha = min(alpha, materialSetWeightScale);

        AccumulateWeighted(materialSetA, materialSetWeightScale - alpha);
        AccumulateWeighted(materialSetB, alpha);
    }

    void SelectCandidates(out uint4 selectedIds, out uint4 selectedScores)
    {
        selectedIds = materialSetInvalidMaterialIds;
        selectedScores = 0u;

        uint scores[materialSetBlendCandidateCount];

        for (uint candidateIndex = 0u; candidateIndex < materialSetBlendCandidateCount; candidateIndex++)
        {
            scores[candidateIndex] = materialSetBlendCandidateScores[candidateIndex];
        }

        for (uint topIndex = 0u; topIndex < 4u; topIndex++)
        {
            bool found = false;
            uint bestCandidateIndex = 0u;
            uint bestMaterialId = materialSetInvalidMaterialId;
            uint bestScore = 0u;

            for (uint candidateIndex = 0u; candidateIndex < materialSetBlendCandidateCount; candidateIndex++)
            {
                if (candidateIndex >= candidateCount)
                {
                    break;
                }

                uint score = scores[candidateIndex];

                if (score == 0u)
                {
                    continue;
                }

                uint materialId = materialSetBlendCandidateIds[candidateIndex];

                if (!found || MaterialSetIsBetter(score, materialId, bestScore, bestMaterialId))
                {
                    found = true;
                    bestCandidateIndex = candidateIndex;
                    bestMaterialId = materialId;
                    bestScore = score;
                }
            }

            if (!found)
            {
                break;
            }

            selectedIds = MaterialSetSetComponent(selectedIds, topIndex, bestMaterialId);
            selectedScores = MaterialSetSetComponent(selectedScores, topIndex, bestScore);
            scores[bestCandidateIndex] = 0u;
        }
    }

    uint2 Compute()
    {
        uint4 selectedIds;
        uint4 selectedScores;
        SelectCandidates(selectedIds, selectedScores);

        uint scoreSum = selectedScores.x + selectedScores.y + selectedScores.z + selectedScores.w;

        if (scoreSum == 0u)
        {
            return MaterialSetCreateSingle(0u);
        }

        uint4 weights = MaterialSetDiv255(selectedScores);
        MaterialSetCorrectWeightSum(weights);

        return MaterialSetPack(selectedIds, weights);
    }
};

#endif
