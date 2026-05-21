#ifndef TUNTENFISCH_VOXELS_TRIPLANAR
#define TUNTENFISCH_VOXELS_TRIPLANAR

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

struct TriplanarData
{
    half4 albedo;
    half3 normalWS;
    half metallic;
    half occlusion;
    half height;
    half smoothness;
};

struct TriplanarUVs
{
    float2 x;
    float2 y;
    float2 z;
};

uint HashUInt(uint n)
{
    n ^= n >> 16;
    n *= 0x7feb352du;
    n ^= n >> 15;
    n *= 0x846ca68bu;
    n ^= n >> 16;

    return n;
}

float Hash01(uint n)
{
    return (HashUInt(n) & 0x00FFFFFFu) / 16777215.0f;
}

float Hash21(uint2 n)
{
    return Hash01(n.x * 0x9E3779B9u ^ n.y * 0x85EBCA6Bu);
}

float2 GetPlanarHashOffset(float2 uv)
{
    float2 cell = floor(uv * _TextureCellFrequency);
    int2 signedCell = int2(cell);
    uint2 cellKey = asuint(signedCell);

    return float2
    (
        Hash21(cellKey ^ uint2(0x68E31DA4u, 0xB5297A4Du)),
        Hash21(cellKey ^ uint2(0x1B56C4E9u, 0x94D049BBu))
    ) * 2.0f - 1.0f;
}

TriplanarUVs GetTriplanarUVs(float3 positionWS, half3 normalWS)
{
    TriplanarUVs uvs;

    positionWS *= _CoordinateScaling;

    uvs.x = positionWS.zy;
    uvs.y = positionWS.xz;
    uvs.z = positionWS.xy;

    uvs.x += GetPlanarHashOffset(uvs.x) * _TextureCellOffsetStrength;
    uvs.y += GetPlanarHashOffset(uvs.y) * _TextureCellOffsetStrength;
    uvs.z += GetPlanarHashOffset(uvs.z) * _TextureCellOffsetStrength;

    half3 signs = sign(normalWS);

    // Account for mirrored mapping.
    uvs.x.x *= signs.x;
    uvs.y.x *= signs.y;
    uvs.z.x *= -signs.z;

    // Offset coordinates to prevent sudden repetitions.
    uvs.x.y += 0.5h;
    uvs.z.x += 0.5h;

    return uvs;
}

half3 GetTriplanarWeights(half3 normalWS, half3 heights)
{
    half3 triplanarWeights = abs(normalWS);
    triplanarWeights = saturate(triplanarWeights - _BlendOffset);
    triplanarWeights *= abs(lerp(1.0h, heights, _BlendHeightStrength));
    triplanarWeights = pow(triplanarWeights, _BlendExponent);
    triplanarWeights /= dot(triplanarWeights, 1.0h);

    return triplanarWeights;
}

half3 BlendTriplanarNormal(half3 normalTS, half3 normalWS)
{
    return half3(normalTS.xy + normalWS.xy, normalTS.z * normalWS.z);
}

TriplanarData ApplyTriplanarTexturing
(
    float3 positionWS,
    half3 normalWS,
    TEXTURE2D_ARRAY(albedoTextures),
    TEXTURE2D_ARRAY(normalTextures),
    TEXTURE2D_ARRAY(mohsTextures),
    SAMPLER(samplerName),
    uint index
)
{
    TriplanarData triplanarData = (TriplanarData)0;
    TriplanarUVs uvs = GetTriplanarUVs(positionWS, normalWS);

    // Sample MOHS first to get the height information.
    half4 mohsX = SAMPLE_TEXTURE2D_ARRAY(mohsTextures, samplerName, uvs.x, index);
    half4 mohsY = SAMPLE_TEXTURE2D_ARRAY(mohsTextures, samplerName, uvs.y, index);
    half4 mohsZ = SAMPLE_TEXTURE2D_ARRAY(mohsTextures, samplerName, uvs.z, index);

    // Now that we have the height information we can get the triplanar weights
    // for combining the three projections.
    half3 triplanarWeights = GetTriplanarWeights(normalWS, half3(mohsX.z, mohsY.z, mohsZ.z));

    half4 mohs = triplanarWeights.x * mohsX + triplanarWeights.y * mohsY + triplanarWeights.z * mohsZ;

    triplanarData.metallic = mohs.x;
    triplanarData.occlusion = mohs.y;
    triplanarData.height = mohs.z;
    triplanarData.smoothness = mohs.w;

    // Sample normals.
    half3 normalXTS = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(normalTextures, samplerName, uvs.x, index));
    half3 normalYTS = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(normalTextures, samplerName, uvs.y, index));
    half3 normalZTS = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(normalTextures, samplerName, uvs.z, index));

    half3 signs = sign(normalWS);

    // Account for mirrored mapping.
    normalXTS.x *= signs.x;
    normalYTS.x *= signs.y;
    normalZTS.x *= -signs.z;

    half3 normalXWS = BlendTriplanarNormal(normalXTS, normalWS.zyx).zyx;
    half3 normalYWS = BlendTriplanarNormal(normalYTS, normalWS.xzy).xzy;
    half3 normalZWS = BlendTriplanarNormal(normalZTS, normalWS);

    triplanarData.normalWS = triplanarWeights.x * normalXWS + triplanarWeights.y * normalYWS + triplanarWeights.z * normalZWS;
    
    // Sample albedo.
    half4 albedoX = SAMPLE_TEXTURE2D_ARRAY(albedoTextures, samplerName, uvs.x, index);
    half4 albedoY = SAMPLE_TEXTURE2D_ARRAY(albedoTextures, samplerName, uvs.y, index);
    half4 alebdoZ = SAMPLE_TEXTURE2D_ARRAY(albedoTextures, samplerName, uvs.z, index);

    triplanarData.albedo = triplanarWeights.x * albedoX + triplanarWeights.y * albedoY + triplanarWeights.z * alebdoZ;

    return triplanarData;
}

#endif
