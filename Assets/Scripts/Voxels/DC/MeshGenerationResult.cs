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
        public ComputeBuffer GeneratedSurfaceMaterials { get; }
        public int SurfaceMaterialCount { get; }
        public int TriangleCount => SurfaceMaterialCount;

        public MeshGenerationResult(
            NativeArray<GPUVertex> vertices,
            int vertexCount,
            int vertexStartIndex,
            NativeArray<int> indices,
            int indexCount,
            int indexStartIndex,
            ComputeBuffer generatedSurfaceMaterials,
            int surfaceMaterialCount)
        {
            Vertices = vertices;
            VertexCount = vertexCount;
            VertexStartIndex = vertexStartIndex;
            Indices = indices;
            IndexCount = indexCount;
            IndexStartIndex = indexStartIndex;
            GeneratedSurfaceMaterials = generatedSurfaceMaterials;
            SurfaceMaterialCount = surfaceMaterialCount;
        }
    }
}
