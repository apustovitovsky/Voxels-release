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
            new(VertexAttribute.TexCoord0, VertexAttributeFormat.UInt32, 1),
            new(VertexAttribute.TexCoord2, VertexAttributeFormat.UInt32, 2)
        };

        public readonly float3 Position => m_position;
        public readonly half4 Normal => m_normal;
        public readonly MaterialIndex MaterialIndex => m_materialIndex;

        private float3 m_position;
        private half4 m_normal;
        private readonly MaterialIndex m_materialIndex;
        private uint2 m_packedMaterialWeights;
    }
}