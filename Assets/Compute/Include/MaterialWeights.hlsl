#ifndef TUNTENFISCH_VOXELS_MATERIAL_WEIGHTS
#define TUNTENFISCH_VOXELS_MATERIAL_WEIGHTS

void NormalizeMaterialWeights(
    inout float4 weights0,
    inout float4 weights1)
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
        weights1 = 0.0f;
    }
}

void NormalizeMaterialWeights(
    inout float4 weights0,
    inout float4 weights1,
    inout float4 weights2,
    inout float4 weights3)
{
    weights0 = max(weights0, 0.0f);
    weights1 = max(weights1, 0.0f);
    weights2 = max(weights2, 0.0f);
    weights3 = max(weights3, 0.0f);

    float sum =
        weights0.x + weights0.y + weights0.z + weights0.w +
        weights1.x + weights1.y + weights1.z + weights1.w +
        weights2.x + weights2.y + weights2.z + weights2.w +
        weights3.x + weights3.y + weights3.z + weights3.w;

    if (sum > 0.0001f)
    {
        weights0 /= sum;
        weights1 /= sum;
        weights2 /= sum;
        weights3 /= sum;
    }
    else
    {
        weights0 = float4(1, 0, 0, 0);
        weights1 = 0.0f;
        weights2 = 0.0f;
        weights3 = 0.0f;
    }
}

#endif
