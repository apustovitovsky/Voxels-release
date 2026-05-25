using Tuntenfisch.Voxels.Materials;
using Unity.Mathematics;

namespace Tuntenfisch.Voxels.DC
{
    public struct PackedVoxel
    {
        public uint PackedValueAndMaterialIndex;
        public uint PackedGradient;

        public readonly float Value => math.f16tof32(PackedValueAndMaterialIndex & 0xffffu);
        public readonly MaterialIndex MaterialIndex => (MaterialIndex)(PackedValueAndMaterialIndex >> 16);
        public readonly bool IsSolid => Value >= 0.0f;

        public readonly float3 Gradient
        {
            get
            {
                float2 encoded = math.f16tof32(new uint2(PackedGradient & 0xffffu, PackedGradient >> 16));
                float3 normal = new float3(encoded, 1.0f - math.abs(encoded.x) - math.abs(encoded.y));
                float t = math.max(-normal.z, 0.0f);
                normal.xy += new float2(normal.x >= 0.0f ? -t : t, normal.y >= 0.0f ? -t : t);

                return math.normalizesafe(normal);
            }
        }
    }
}
