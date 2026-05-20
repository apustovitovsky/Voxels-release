using Unity.Mathematics;

namespace Tuntenfisch.Voxels.Materials
{
    public readonly struct MaterialSample
    {
        public uint2 MaterialWeights { get; }
        public MaterialIndex DominantMaterialIndex => (MaterialIndex)GetDominantMaterialIndex(MaterialWeights);

        public MaterialSample(uint2 materialWeights)
        {
            MaterialWeights = materialWeights;
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
