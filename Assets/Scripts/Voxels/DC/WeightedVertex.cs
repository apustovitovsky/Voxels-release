using System.Runtime.InteropServices;
using Tuntenfisch.Voxels.Materials;
using Unity.Mathematics;

namespace Tuntenfisch.Voxels.DC
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct WeightedVertex
    {
        public static int SizeInBytes => s_sizeInBytes;

        private static readonly int s_sizeInBytes = Marshal.SizeOf<WeightedVertex>();

        public float3 Position;
        public uint2 PackedNormal;
        public uint2 MaterialWeights;

        public GPUVertex ToGPUVertex()
        {
            return new GPUVertex(Position, PackedNormal, (MaterialIndex)GetDominantMaterialIndex(MaterialWeights));
        }

        private static uint GetDominantMaterialIndex(uint2 materialWeights)
        {
            uint dominantMaterialIndex = 0;
            uint dominantWeight = GetMaterialWeight(materialWeights, 0);

            for (uint materialIndex = 1; materialIndex < 8; materialIndex++)
            {
                uint weight = GetMaterialWeight(materialWeights, materialIndex);

                if (weight > dominantWeight)
                {
                    dominantMaterialIndex = materialIndex;
                    dominantWeight = weight;
                }
            }

            return dominantMaterialIndex;
        }

        private static uint GetMaterialWeight(uint2 materialWeights, uint materialIndex)
        {
            uint packedWeights = materialIndex < 4 ? materialWeights.x : materialWeights.y;
            uint slot = materialIndex & 3;

            return (packedWeights >> ((int)slot * 8)) & 0xFF;
        }
    }
}
