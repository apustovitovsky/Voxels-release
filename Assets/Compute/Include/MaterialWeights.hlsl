#ifndef TUNTENFISCH_VOXELS_MATERIAL_WEIGHTS
#define TUNTENFISCH_VOXELS_MATERIAL_WEIGHTS

void NormalizeMaterialWeights(inout float4 weights0, inout float4 weights1)
{
    weights0 = max(weights0, 0.0f);
    weights1 = max(weights1, 0.0f);

    float sum =
        weights0.x + weights0.y + weights0.z + weights0.w +
        weights1.x + weights1.y + weights1.z + weights1.w;

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
}

void CreateOneHotMaterialWeights(
    uint materialIndex,
    out float4 weights0,
    out float4 weights1)
{
    uint slot = materialIndex & 7u;

    weights0 = float4(
        slot == 0u ? 1.0f : 0.0f,
        slot == 1u ? 1.0f : 0.0f,
        slot == 2u ? 1.0f : 0.0f,
        slot == 3u ? 1.0f : 0.0f
    );

    weights1 = float4(
        slot == 4u ? 1.0f : 0.0f,
        slot == 5u ? 1.0f : 0.0f,
        slot == 6u ? 1.0f : 0.0f,
        slot == 7u ? 1.0f : 0.0f
    );
}

// void CreateOneHotMaterialWeights(
//     uint materialIndex,
//     out float4 weights0,
//     out float4 weights1)
// {
//     float slot = (float)(materialIndex & 7u);

//     weights0 = 1.0f - saturate(abs(slot - float4(0, 1, 2, 3)));
//     weights1 = 1.0f - saturate(abs(slot - float4(4, 5, 6, 7)));
// }

#endif