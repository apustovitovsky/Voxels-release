using System.Runtime.InteropServices;
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
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float16, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.UInt32, 2)
        };

        public readonly float3 Position => m_position;
        public half4 Normal => new PackedNormalUnion { Packed = m_packedNormal }.Normal;
        public readonly uint2 MaterialWeights => m_materialWeights;

        private float3 m_position;
        private uint2 m_packedNormal;
        private uint2 m_materialWeights;

        [StructLayout(LayoutKind.Explicit)]
        private struct PackedNormalUnion
        {
            [FieldOffset(0)]
            public uint2 Packed;

            [FieldOffset(0)]
            public half4 Normal;
        }
    }
}
