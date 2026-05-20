using Unity.Collections;
using UnityEngine;

namespace Tuntenfisch.Voxels.DC
{
    // TODO: Replace this callback payload with a dedicated mesh generation result object.
    // It currently mixes CPU mesh data and GPU resources as an incremental transport step.
    public delegate void OnMeshGenerated(
        NativeArray<GPUVertex> vertices,
        int vertexCount,
        int vertexStartIndex,
        NativeArray<int> indices,
        int indexCount,
        int indexStartIndex,
        ComputeBuffer generatedSurfaceMaterials,
        int surfaceMaterialCount
    );
}
