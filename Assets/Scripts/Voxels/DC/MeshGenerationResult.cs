using Unity.Collections;
using UnityEngine;

namespace Tuntenfisch.Voxels.DC
{
    // Non-owning transport wrapper. The contained buffers/arrays are owned by DualContouring.Worker.
    public readonly struct MeshGenerationResult
    {
        public NativeArray<GPUVertex> Vertices { get; }
        public int VertexCount { get; }
        public int VertexStartIndex { get; }
        public NativeArray<int> Indices { get; }
        public int IndexCount { get; }
        public int IndexStartIndex { get; }
        public ComputeBuffer GeneratedTriangleMaterialSets { get; }
        public int TriangleMaterialSetCount { get; }
        public int TriangleCount => TriangleMaterialSetCount;

        public MeshGenerationResult(
            NativeArray<GPUVertex> vertices,
            int vertexCount,
            int vertexStartIndex,
            NativeArray<int> indices,
            int indexCount,
            int indexStartIndex,
            ComputeBuffer generatedTriangleMaterialSets,
            int triangleMaterialSetCount)
        {
            Vertices = vertices;
            VertexCount = vertexCount;
            VertexStartIndex = vertexStartIndex;
            Indices = indices;
            IndexCount = indexCount;
            IndexStartIndex = indexStartIndex;
            GeneratedTriangleMaterialSets = generatedTriangleMaterialSets;
            TriangleMaterialSetCount = triangleMaterialSetCount;
        }
    }
}
