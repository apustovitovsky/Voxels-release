#ifndef TUNTENFISCH_VOXELS_SURFACE_MATERIAL
#define TUNTENFISCH_VOXELS_SURFACE_MATERIAL

struct SurfaceMaterial
{
    uint indices;
    uint weights0;
    uint weights1;
    uint weights2;
};

uint PackBytes(uint4 unpackedBytes)
{
    return (unpackedBytes.x & 0xFF)
        | ((unpackedBytes.y & 0xFF) << 8)
        | ((unpackedBytes.z & 0xFF) << 16)
        | ((unpackedBytes.w & 0xFF) << 24);
}

uint4 UnpackBytes(uint packedBytes)
{
    return uint4
    (
        packedBytes & 0xFF,
        (packedBytes >> 8) & 0xFF,
        (packedBytes >> 16) & 0xFF,
        (packedBytes >> 24) & 0xFF
    );
}

half4 UnpackWeights(uint packedWeights)
{
    return (half4)UnpackBytes(packedWeights) / 255.0h;
}

#endif
