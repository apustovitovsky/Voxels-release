#ifndef TUNTENFISCH_VOXELS_CSG
#define TUNTENFISCH_VOXELS_CSG

// Parts below are based on articles by Inigo Quilez (https://www.iquilezles.org/).
#include "Assets/Compute/Include/Enumeration.hlsl"
#include "Assets/Compute/Voxels/Include/Voxel2.hlsl"
ENUM CSGOperatorIndex
{
    static const uint Union = 0;
    static const uint Intersection = 1;
    static const uint Difference = 2;
    static const uint SmoothUnion = 3;
    static const uint SmoothIntersection = 4;
    static const uint SmoothDifference = 5;
};

ENUM CSGPrimitiveType
{
    static const uint Sphere = 0;
    static const uint Cuboid = 1;
};

void BuildMaterialSetFromWeights(float materialSetWeightSums[16], out uint4 materialSetIndices, out float4 materialSetWeights)
{
    materialSetIndices = 0;
    materialSetWeights = 0.0f;

    [unroll]
    for (uint materialIndex = 0; materialIndex < 16; materialIndex++)
    {
        float weight = materialSetWeightSums[materialIndex];

        if (weight > materialSetWeights.x)
        {
            materialSetWeights.w = materialSetWeights.z;
            materialSetIndices.w = materialSetIndices.z;
            materialSetWeights.z = materialSetWeights.y;
            materialSetIndices.z = materialSetIndices.y;
            materialSetWeights.y = materialSetWeights.x;
            materialSetIndices.y = materialSetIndices.x;
            materialSetWeights.x = weight;
            materialSetIndices.x = materialIndex;
        }
        else if (weight > materialSetWeights.y)
        {
            materialSetWeights.w = materialSetWeights.z;
            materialSetIndices.w = materialSetIndices.z;
            materialSetWeights.z = materialSetWeights.y;
            materialSetIndices.z = materialSetIndices.y;
            materialSetWeights.y = weight;
            materialSetIndices.y = materialIndex;
        }
        else if (weight > materialSetWeights.z)
        {
            materialSetWeights.w = materialSetWeights.z;
            materialSetIndices.w = materialSetIndices.z;
            materialSetWeights.z = weight;
            materialSetIndices.z = materialIndex;
        }
        else if (weight > materialSetWeights.w)
        {
            materialSetWeights.w = weight;
            materialSetIndices.w = materialIndex;
        }
    }

    float sum = materialSetWeights.x + materialSetWeights.y + materialSetWeights.z + materialSetWeights.w;

    if (sum > 0.0001f)
    {
        materialSetWeights /= sum;
    }
    else
    {
        materialSetIndices = uint4(materialSetIndices.x, 0, 0, 0);
        materialSetWeights = float4(1, 0, 0, 0);
    }
}

Voxel BlendMaterialSets(Voxel lhs, Voxel rhs, float lhsWeight, float rhsWeight)
{
    float materialSetWeightSums[16] = { 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f };

    [unroll]
    for (uint index = 0; index < 4; index++)
    {
        materialSetWeightSums[lhs.materialSetIndices[index]] += lhsWeight * lhs.materialSetWeights[index];
        materialSetWeightSums[rhs.materialSetIndices[index]] += rhsWeight * rhs.materialSetWeights[index];
    }

    uint4 materialSetIndices;
    float4 materialSetWeights;
    BuildMaterialSetFromWeights(materialSetWeightSums, materialSetIndices, materialSetWeights);

    Voxel voxel = Voxel::Create();
    voxel.SetMaterialSet(materialSetIndices, materialSetWeights);

    return voxel;
}

Voxel Union(Voxel lhs, Voxel rhs)
{
    Voxel voxel = Voxel::Create();

    if (lhs.GetValue() < rhs.GetValue())
    {
        voxel = lhs;
    }
    else
    {
        voxel = rhs;
    }

    return voxel;
}

Voxel Intersection(Voxel lhs, Voxel rhs)
{
    lhs.valueAndGradient *= -1.0f;
    rhs.valueAndGradient *= -1.0f;

    Voxel voxel = Union(lhs, rhs);
    voxel.valueAndGradient *= -1.0f;

    return voxel;
}

Voxel Difference(Voxel lhs, Voxel rhs)
{
    rhs.materialSetIndices = lhs.materialSetIndices;
    rhs.materialSetWeights = lhs.materialSetWeights;
    rhs.valueAndGradient *= -1.0f;

    return Intersection(lhs, rhs);
}

Voxel SmoothUnion(Voxel lhs, Voxel rhs, float smoothing)
{
    if (smoothing <= 0.0001f)
    {
        return Union(lhs, rhs);
    }

    float lhsValue = lhs.GetValue();
    float rhsValue = rhs.GetValue();

    float invSmoothing = rcp(smoothing);

    float h = max(smoothing - abs(lhsValue - rhsValue), 0.0f);
    float m = 0.25f * h * h * invSmoothing;
    float n = 0.50f * h * invSmoothing;

    bool lhsWins = lhsValue < rhsValue;

    float gradientBlend = lhsWins ? n : 1.0f - n;
    float materialBlend = saturate(0.5f + 0.5f * (rhsValue - lhsValue) * invSmoothing);

    Voxel voxel = BlendMaterialSets(lhs, rhs, materialBlend, 1.0f - materialBlend);

    voxel.valueAndGradient = float4(
        min(lhsValue, rhsValue) - m,
        lerp(lhs.GetGradient(), rhs.GetGradient(), gradientBlend)
    );

    return voxel;
}

Voxel SmoothIntersection(Voxel lhs, Voxel rhs, float smoothing)
{
    if (smoothing <= 0.0001f)
    {
        return Intersection(lhs, rhs);
    }

    lhs.valueAndGradient *= -1.0f;
    rhs.valueAndGradient *= -1.0f;

    Voxel voxel = SmoothUnion(lhs, rhs, smoothing);
    voxel.valueAndGradient *= -1.0f;

    return voxel;
}

Voxel SmoothDifference(Voxel lhs, Voxel rhs, float smoothing)
{
    if (smoothing <= 0.0001f)
    {
        return Difference(lhs, rhs);
    }

    rhs.materialSetIndices = lhs.materialSetIndices;
    rhs.materialSetWeights = lhs.materialSetWeights;
    rhs.valueAndGradient *= -1.0f;

    return SmoothIntersection(lhs, rhs, smoothing);
}

struct CSGOperator
{
    uint operatorIndex;
    float smoothing;
};

Voxel ApplyCSGOperator(Voxel lhs, Voxel rhs, CSGOperator csgOperator)
{
    [branch]
    switch(csgOperator.operatorIndex)
    {
        case CSGOperatorIndex::Union:
            return Union(lhs, rhs);

        case CSGOperatorIndex::Intersection:
            return Intersection(lhs, rhs);

        case CSGOperatorIndex::Difference:
            return Difference(lhs, rhs);

        case CSGOperatorIndex::SmoothUnion:
            return SmoothUnion(lhs, rhs, csgOperator.smoothing);

        case CSGOperatorIndex::SmoothIntersection:
            return SmoothIntersection(lhs, rhs, csgOperator.smoothing);

        default:
            return SmoothDifference(lhs, rhs, csgOperator.smoothing);
    }
}

// When combining a primitive (effectively an SDF) with other SDFs, the primitive
// will perturb the shape of nearby SDFs and hence alter the generated mesh's
// geometry. But the mesh's geometry is even affected when the the isosurface the
// primitive is describing isn't intersecting any of the other isosurfaces at all.
//
// This is due to small values, i.e. values close to 0, near the primitive's isosurface
// "winning" when the primitive is combined with other SDFs. Multiplying the
// value of the primitive by a "large" factor effectively narrows the interval
// of small values the primitive's SDFs has and therefore limits the region in which
// it affects other SDFs when combined. The isosurface, which is generated at the 0
// transition of the SDF isn't affect.
//
// At least that's my theory behind it...
static const float csgPrimitiveValueMultiplier = 7.5f;

struct CSGPrimitive
{
    uint primitiveType;
};

float4 EvaluateCSGSphere(float3 position)
{
    float4 valueAndGradient;
    valueAndGradient.x = length(position) - 0.5f;
    valueAndGradient.x *= csgPrimitiveValueMultiplier;
    valueAndGradient.yzw = position;

    return valueAndGradient;
}

float4 EvaluateCSGCuboid(float3 position)
{
    float4 valueAndGradient;
    float3 d = abs(position) - 0.5f;
    float3 smoothing = sign(position);
    float g = max(d.x, max(d.y, d.z));

    valueAndGradient.x = length(max(d, 0.0f)) + min(max(d.x, max(d.y, d.z)), 0.0f);
    valueAndGradient.x *= csgPrimitiveValueMultiplier;
    valueAndGradient.yzw = smoothing * (g > 0.0f ? normalize(max(d, 0.0f)) : step(d.yzx, d.xyz) * step(d.zxy, d.xyz));

    return valueAndGradient;
}

float4 EvaluateCSGPrimitive(float3 position, CSGPrimitive primitive)
{
    [branch]
    switch(primitive.primitiveType)
    {
        case CSGPrimitiveType::Sphere:
            return EvaluateCSGSphere(position);

        default:
            return EvaluateCSGCuboid(position);
    }
}

#endif
