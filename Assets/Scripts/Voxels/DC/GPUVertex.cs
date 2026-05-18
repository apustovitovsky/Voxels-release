using System.Runtime.InteropServices;
using Tuntenfisch.Voxels.Materials;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace Tuntenfisch.Voxels.DC
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUVertex
    {
        public static int SizeInBytes => s_sizeInBytes;
        public static VertexAttributeDescriptor[] Attributes => s_attributes;


        private static readonly int s_sizeInBytes = Marshal.SizeOf<GPUVertex>();
        private static readonly VertexAttributeDescriptor[] s_attributes =
        {
            new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new(VertexAttribute.Normal, VertexAttributeFormat.Float16, 4),
            new(VertexAttribute.TexCoord2, VertexAttributeFormat.UInt32, 2)
        };

        public readonly float3 Position => m_position;
        public readonly half4 Normal => m_normal;
        public readonly MaterialIndex MaterialIndex => GetDominantMaterialIndex();

        private float3 m_position;
        private half4 m_normal;
        private readonly uint2 m_packedMaterialSet;

        private readonly MaterialIndex GetDominantMaterialIndex()
        {
            uint4 materialIndices = new(
                m_packedMaterialSet.x & 0xFu,
                (m_packedMaterialSet.x >> 4) & 0xFu,
                (m_packedMaterialSet.x >> 8) & 0xFu,
                (m_packedMaterialSet.x >> 12) & 0xFu
            );

            uint4 materialWeights = new(
                m_packedMaterialSet.y & 0xFFu,
                (m_packedMaterialSet.y >> 8) & 0xFFu,
                (m_packedMaterialSet.y >> 16) & 0xFFu,
                (m_packedMaterialSet.y >> 24) & 0xFFu
            );

            uint dominantWeight = materialWeights.x;
            uint dominantIndex = materialIndices.x;

            if (materialWeights.y > dominantWeight)
            {
                dominantWeight = materialWeights.y;
                dominantIndex = materialIndices.y;
            }

            if (materialWeights.z > dominantWeight)
            {
                dominantWeight = materialWeights.z;
                dominantIndex = materialIndices.z;
            }

            if (materialWeights.w > dominantWeight)
            {
                dominantIndex = materialIndices.w;
            }

            return (MaterialIndex)dominantIndex;
        }
    }
}
