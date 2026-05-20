using System;
using System.Collections.Generic;
using Tuntenfisch.Extensions;
using Tuntenfisch.Generics;
using Tuntenfisch.Generics.Pool;
using Tuntenfisch.Voxels;
using Tuntenfisch.Voxels.CSG;
using Tuntenfisch.Voxels.DC;
using Tuntenfisch.Voxels.Materials;
using Tuntenfisch.Voxels.Volume;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tuntenfisch.World
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public class Chunk : MonoBehaviour, IPoolable
    {
        private int m_currentLOD;
        private int m_targetLOD;
        private int m_vertexCount;
        private int m_indexCount;
        private int m_triangleCount;
        private Mesh m_mesh;
        private MeshFilter m_meshFilter;
        private MeshRenderer m_meshRenderer;
        private MeshCollider m_meshCollider;
        private MaterialPropertyBlock m_materialPropertyBlock;
        private OnMeshGenerated m_onMeshGeneratedDelegate;

        private ComputeBuffer m_surfaceMaterialBuffer;
        private ComputeBuffer m_emptySurfaceMaterialBuffer;
        private ComputeBuffer m_voxelVolumeBuffer;
        private IRequest m_request;
        private JobHandle m_bakeJobHandle;
        private List<GPUVoxelVolumeCSGOperation> m_voxelVolumeCSGOperations;
        private ChunkFlags m_flags;

        private void Awake()
        {
            WorldManager.VoxelConfig.MaterialConfig.OnDirtied += ApplyRenderMaterial;
            InitializeMeshComponents();
            ApplyRenderMaterial();
            m_voxelVolumeCSGOperations = new List<GPUVoxelVolumeCSGOperation>();
        }

        private void Update()
        {
            if (m_flags == 0)
            {
                return;
            }

            if ((m_flags & ChunkFlags.VoxelVolumeRegenerationRequested) == ChunkFlags.VoxelVolumeRegenerationRequested)
            {
                m_flags &= ~ChunkFlags.VoxelVolumeRegenerationRequested;
                WorldManager.VoxelVolume.GenerateVoxelVolume(m_voxelVolumeBuffer, transform.position);
            }

            if ((m_flags & ChunkFlags.CSGOperationPerformed) == ChunkFlags.CSGOperationPerformed)
            {
                m_flags &= ~ChunkFlags.CSGOperationPerformed;
                WorldManager.VoxelVolume.ApplyVoxelVolumeCSGOperations(m_voxelVolumeBuffer, transform.position, m_voxelVolumeCSGOperations);
                m_voxelVolumeCSGOperations.Clear();
            }

            if ((m_flags & ChunkFlags.MeshRegenerationRequested) == ChunkFlags.MeshRegenerationRequested && (m_flags & ChunkFlags.IsBakingMesh) != ChunkFlags.IsBakingMesh && m_request == null)
            {
                m_flags &= ~ChunkFlags.MeshRegenerationRequested;
                m_request = WorldManager.DualContouring.RequestMeshAsync
                (
                    m_voxelVolumeBuffer,
                    m_currentLOD,
                    m_targetLOD,
                    m_vertexCount,
                    m_indexCount,
                    transform.position,
                    m_onMeshGeneratedDelegate
                );
            }

            if ((m_flags & ChunkFlags.IsBakingMesh) == ChunkFlags.IsBakingMesh && m_bakeJobHandle.IsCompleted)
            {
                m_flags &= ~ChunkFlags.IsBakingMesh;
                m_meshCollider.sharedMesh = null;
                m_meshCollider.sharedMesh = m_mesh;
            }
        }

        private void OnDestroy()
        {
            WorldManager.VoxelConfig.MaterialConfig.OnDirtied -= ApplyRenderMaterial;
            ReleaseBuffers();
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(transform.position, WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions);
        }

        public void OnAcquire()
        {
            m_currentLOD = m_targetLOD = m_vertexCount = m_indexCount = m_triangleCount = -1;

            if (m_meshRenderer == null || m_materialPropertyBlock == null)
            {
                InitializeMeshComponents();
            }

            CreateBuffers();
            BindSurfaceMaterialBuffer(m_emptySurfaceMaterialBuffer);
            gameObject.SetActive(true);
        }

        public void OnRelease()
        {
            m_meshFilter.sharedMesh = null;
            m_meshCollider.sharedMesh = null;
            m_request?.Cancel();
            m_request = null;
            m_voxelVolumeCSGOperations.Clear();
            m_flags = 0;
            gameObject.SetActive(false);
        }

        public bool GetMaterialFromRaycastHit(RaycastHit hit, out MaterialIndex materialIndex)
        {
            materialIndex = default;

            if (hit.triangleIndex >= m_triangleCount)
            {
                return false;
            }

            using Mesh.MeshDataArray meshDataArray = Mesh.AcquireReadOnlyMeshData(m_mesh);
            {
                Mesh.MeshData meshData = meshDataArray[0];
                NativeArray<int> triangles = meshData.GetIndexData<int>();
                NativeArray<GPUVertex> vertices = meshData.GetVertexData<GPUVertex>();

                float shortestDistanceSquared = float.MaxValue;

                for (int index = 0; index < 3; index++)
                {
                    GPUVertex vertex = vertices[triangles[3 * hit.triangleIndex + index]];
                    float distanceSquared = math.lengthsq(hit.transform.TransformPoint(vertex.Position) - hit.point);

                    if (distanceSquared < shortestDistanceSquared)
                    {
                        shortestDistanceSquared = distanceSquared;
                        materialIndex = vertex.MaterialIndex;
                    }
                }
            }

            return true;
        }

        private void CreateBuffers()
        {
            if (m_voxelVolumeBuffer?.count != WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount)
            {
                m_voxelVolumeBuffer?.Release();
                m_voxelVolumeBuffer = new ComputeBuffer(WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount, 2 * sizeof(uint));
            }

            CreateEmptySurfaceMaterialBuffer();
        }

        private void ReleaseBuffers()
        {
            if (m_voxelVolumeBuffer != null)
            {
                m_voxelVolumeBuffer.Release();
                m_voxelVolumeBuffer = null;
            }

            if (m_surfaceMaterialBuffer != null)
            {
                m_surfaceMaterialBuffer.Release();
                m_surfaceMaterialBuffer = null;
            }

            if (m_emptySurfaceMaterialBuffer != null)
            {
                m_emptySurfaceMaterialBuffer.Release();
                m_emptySurfaceMaterialBuffer = null;
            }
        }

        public void RegenerateVoxelVolume() => m_flags |= ChunkFlags.VoxelVolumeRegenerationRequested;

        public void RegenerateMesh(int lod = -1)
        {
            if (lod != -1 && lod != m_targetLOD)
            {
                m_targetLOD = lod;
                m_flags |= ChunkFlags.MeshRegenerationRequested;
            }
            else if (lod == -1)
            {
                m_flags |= ChunkFlags.MeshRegenerationRequested;
            }
        }

        public void ApplyCSGPrimitiveOperation(GPUCSGOperator csgOperator, GPUCSGPrimitive csgPrimitive, MaterialIndex materialIndex, Matrix4x4 worldToObjectMatrix)
        {
            m_voxelVolumeCSGOperations.Add(new GPUVoxelVolumeCSGOperation(csgOperator, csgPrimitive, materialIndex, worldToObjectMatrix));
            m_flags |= ChunkFlags.CSGOperationPerformed | ChunkFlags.MeshRegenerationRequested;
        }

        private void OnMeshGenerated(
            NativeArray<GPUVertex> vertices,
            int vertexCount,
            int vertexStartIndex,
            NativeArray<int> indices,
            int indexCount,
            int indexStartIndex,
            ComputeBuffer generatedSurfaceMaterials,
            int surfaceMaterialCount
        )
        {
            m_request = null;
            m_currentLOD = m_targetLOD;
            m_vertexCount = vertexCount;
            m_indexCount = indexCount;
            m_triangleCount = surfaceMaterialCount;

            if (vertexCount == 0 || indexCount == 0)
            {
                m_meshFilter.sharedMesh = null;
                m_meshCollider.sharedMesh = null;
                BindSurfaceMaterialBuffer(m_emptySurfaceMaterialBuffer);

                return;
            }

            m_mesh.SetVertexBufferParams(vertexCount, GPUVertex.Attributes);
            m_mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
#if !UNITY_EDITOR
            MeshUpdateFlags flags = MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontValidateIndices;
            m_mesh.SetVertexBufferData(vertices, vertexStartIndex, 0, vertexCount, 0, flags);
            m_mesh.SetIndexBufferData(indices, indexStartIndex, 0, indexCount, flags);
            m_mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount), flags);
            m_mesh.RecalculateBounds(flags);
#else
            m_mesh.SetVertexBufferData(vertices, vertexStartIndex, 0, vertexCount);
            m_mesh.SetIndexBufferData(indices, indexStartIndex, 0, indexCount);
            m_mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount));
            m_mesh.RecalculateBounds(MeshUpdateFlags.DontValidateIndices);
#endif
            m_meshFilter.sharedMesh = null;
            m_meshFilter.sharedMesh = m_mesh;
            CopySurfaceMaterials(generatedSurfaceMaterials, surfaceMaterialCount);

            m_bakeJobHandle = new BakeJob(m_mesh.GetEntityId()).Schedule();
            m_flags |= ChunkFlags.IsBakingMesh;
        }

        private void InitializeMeshComponents()
        {
            if (m_mesh != null && m_meshFilter != null && m_meshRenderer != null && m_meshCollider != null && m_materialPropertyBlock != null)
            {
                return;
            }

            m_mesh = new Mesh();
            m_mesh.MarkDynamic();
            m_meshFilter = GetComponent<MeshFilter>();
            m_meshRenderer = GetComponent<MeshRenderer>();
            m_meshCollider = GetComponent<MeshCollider>();
            m_materialPropertyBlock = new MaterialPropertyBlock();
            m_onMeshGeneratedDelegate = OnMeshGenerated;
        }

        private void ApplyRenderMaterial()
        {
            m_meshRenderer.material = WorldManager.VoxelConfig.MaterialConfig.RenderMaterial;
            BindSurfaceMaterialBuffer(m_surfaceMaterialBuffer ?? m_emptySurfaceMaterialBuffer);
        }

        private void CreateEmptySurfaceMaterialBuffer()
        {
            if (m_emptySurfaceMaterialBuffer == null)
            {
                // Fallback buffer used when the chunk has no generated surface materials yet.
                m_emptySurfaceMaterialBuffer = new ComputeBuffer(1, 4 * sizeof(uint));
                m_emptySurfaceMaterialBuffer.SetData(new uint[]
                {
                    0u,
                    255u,
                    255u,
                    255u
                });
            }
        }

        private void EnsureSurfaceMaterialBuffer(int triangleCount)
        {
            int bufferCount = math.max(1, triangleCount);

            if (m_surfaceMaterialBuffer?.count != bufferCount)
            {
                m_surfaceMaterialBuffer?.Release();
                m_surfaceMaterialBuffer = new ComputeBuffer(bufferCount, 4 * sizeof(uint));
            }
        }

        private void CopySurfaceMaterials(ComputeBuffer generatedSurfaceMaterials, int surfaceMaterialCount)
        {
            EnsureSurfaceMaterialBuffer(surfaceMaterialCount);

            ComputeShader copySurfaceMaterialsCompute = WorldManager.VoxelConfig.DualContouringConfig.CopySurfaceMaterialsCompute;
            const int kernelID = 0;

            copySurfaceMaterialsCompute.SetBuffer(kernelID, ComputeShaderProperties.Source, generatedSurfaceMaterials);
            copySurfaceMaterialsCompute.SetBuffer(kernelID, ComputeShaderProperties.Destination, m_surfaceMaterialBuffer);
            copySurfaceMaterialsCompute.SetInt(ComputeShaderProperties.Count, surfaceMaterialCount);
            copySurfaceMaterialsCompute.Dispatch(kernelID, new int3(surfaceMaterialCount, 1, 1));

            BindSurfaceMaterialBuffer(m_surfaceMaterialBuffer);
        }

        private void BindSurfaceMaterialBuffer(ComputeBuffer surfaceMaterialBuffer)
        {
            if (surfaceMaterialBuffer == null || m_meshRenderer == null || m_materialPropertyBlock == null)
            {
                return;
            }

            m_meshRenderer.GetPropertyBlock(m_materialPropertyBlock);
            m_materialPropertyBlock.SetBuffer(ShaderProperties.SurfaceMaterials, surfaceMaterialBuffer);
            m_meshRenderer.SetPropertyBlock(m_materialPropertyBlock);
        }

        [Flags]
        private enum ChunkFlags
        {
            VoxelVolumeRegenerationRequested = 1,
            CSGOperationPerformed = 2,
            MeshRegenerationRequested = 4,
            IsBakingMesh = 8
        }
    }
}
