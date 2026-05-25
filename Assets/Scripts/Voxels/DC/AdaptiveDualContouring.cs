using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using Tuntenfisch.Generics;
using Tuntenfisch.Generics.Pool;
using Tuntenfisch.Voxels.Materials;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tuntenfisch.Voxels.DC
{
    [RequireComponent(typeof(VoxelConfig))]
    public sealed class AdaptiveDualContouring : MonoBehaviour
    {
        [Range(1, 16)]
        [SerializeField]
        private int m_numberOfWorkers = 2;
        [Min(0)]
        [SerializeField]
        private int m_initialTaskPoolPopulation = 0;
        [SerializeField]
        private bool m_logQefDiagnostics = false;
        [SerializeField]
        private AdaptiveLeafBuildMode m_leafBuildMode = AdaptiveLeafBuildMode.ToyPattern;
        [Min(0.0f)]
        [SerializeField]
        private float m_size2LeafErrorThreshold = 0.01f;

        private VoxelConfig m_voxelConfig;
        private Queue<Task> m_tasks;
        private Stack<Worker> m_availableWorkers;
        private List<Worker> m_workers;
        private Tuntenfisch.Generics.Pool.ObjectPool<Task> m_taskPool;

        private void Awake()
        {
            m_voxelConfig = GetComponent<VoxelConfig>();
            m_tasks = new Queue<Task>();
            m_availableWorkers = new Stack<Worker>();
            m_workers = new List<Worker>();
            m_taskPool = new Tuntenfisch.Generics.Pool.ObjectPool<Task>(() => new Task(), m_initialTaskPoolPopulation);

            CreateWorkers();
        }

        private void LateUpdate()
        {
            while (m_tasks.Count > 0 && m_availableWorkers.Count > 0)
            {
                DispatchWorker(m_tasks.Dequeue());
            }
        }

        private void OnDestroy()
        {
            foreach (Worker worker in m_workers)
            {
                worker.Dispose();
            }

            while (m_tasks.Count > 0)
            {
                m_taskPool.Release(m_tasks.Dequeue());
            }
        }

        public IRequest RequestMeshAsync(ComputeBuffer voxelVolumeBuffer, int targetLOD, float3 worldPosition, OnMeshGenerated callback)
        {
            Task task = m_taskPool.Acquire();
            task.VoxelVolumeBuffer = voxelVolumeBuffer ?? throw new ArgumentNullException(nameof(voxelVolumeBuffer));
            task.Callback = callback ?? throw new ArgumentNullException(nameof(callback));

            // AdaptiveBurst still ignores targetLOD and worldPosition in Milestone 2.
            if (m_availableWorkers.Count > 0)
            {
                DispatchWorker(task);
            }
            else
            {
                m_tasks.Enqueue(task);
            }

            return task;
        }

        private void CreateWorkers()
        {
            for (int index = 0; index < m_numberOfWorkers; index++)
            {
                Worker worker = new Worker(this);
                m_workers.Add(worker);
                m_availableWorkers.Push(worker);
            }
        }

        private void DispatchWorker(Task task)
        {
            if (task.Canceled)
            {
                m_taskPool.Release(task);

                return;
            }

            DispatchWorkerUniTask(task).Forget();
        }

        private async UniTaskVoid DispatchWorkerUniTask(Task task)
        {
            Worker worker = m_availableWorkers.Pop();
            worker.GenerateMeshAsync(task);

            do
            {
                await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());
            }
            while (worker.Process() != WorkerStatus.Done);

            if (!task.Canceled)
            {
                task.Callback(worker.Vertices, worker.Vertices.Length, 0, worker.Indices, worker.Indices.Length, 0);
            }

            m_taskPool.Release(task);
            m_availableWorkers.Push(worker);
        }

        internal enum AdaptiveLeafBuildMode : byte
        {
            ToyPattern = 0,
            QefError = 1
        }

        private sealed class Worker : IDisposable
        {
            public NativeArray<GPUVertex> Vertices => m_vertices.AsArray();
            public NativeArray<int> Indices => m_indices.AsArray();

            private readonly AdaptiveDualContouring m_parent;

            private NativeArray<PackedVoxel> m_voxels;
            private NativeArray<AdaptiveLeafCell> m_leaves;
            private NativeArray<LeafVertexData> m_leafVertexData;
            private NativeArray<int> m_denseCellToLeaf;
            private NativeArray<int> m_leafCount;
            private NativeArray<QefDiagnostics> m_qefDiagnostics;
            private NativeArray<LeafBuildDiagnostics> m_leafBuildDiagnostics;
            private NativeList<GPUVertex> m_vertices;
            private NativeList<int> m_indices;
            private AsyncGPUReadbackRequest m_readbackRequest;
            private JobHandle m_jobHandle;
            private WorkerStatus m_status;
            private bool m_readbackStarted;
            private bool m_jobsScheduled;
            private bool m_disposed;

            public Worker(AdaptiveDualContouring parent)
            {
                m_parent = parent;
                m_parent.m_voxelConfig.VoxelVolumeConfig.OnDirtied += CreateBuffers;
                CreateBuffers();
            }

            public void GenerateMeshAsync(Task task)
            {
                m_vertices.Clear();
                m_indices.Clear();
                m_readbackRequest = AsyncGPUReadback.Request(task.VoxelVolumeBuffer);
                m_readbackStarted = true;
                m_jobsScheduled = false;
                m_status = WorkerStatus.WaitingForGPUReadback;
            }

            public WorkerStatus Process()
            {
                if (m_status == WorkerStatus.WaitingForGPUReadback)
                {
                    if (!m_readbackRequest.done)
                    {
                        return m_status;
                    }

                    m_readbackRequest.WaitForCompletion();
                    m_readbackStarted = false;

                    if (m_readbackRequest.hasError)
                    {
                        Debug.LogWarning("GPU voxel volume readback error detected; returning an empty AdaptiveBurst mesh.");
                        m_status = WorkerStatus.Done;

                        return m_status;
                    }

                    NativeArray<PackedVoxel>.Copy(m_readbackRequest.GetData<PackedVoxel>(), m_voxels);
                    ScheduleMeshJobs();
                    m_status = WorkerStatus.WaitingForJobs;
                }

                if (m_status == WorkerStatus.WaitingForJobs && m_jobHandle.IsCompleted)
                {
                    m_jobHandle.Complete();
                    m_jobsScheduled = false;

                    LeafBuildDiagnostics leafBuildDiagnostics = m_leafBuildDiagnostics[0];
                    if (leafBuildDiagnostics.OverlapCount > 0 || leafBuildDiagnostics.UnassignedCount > 0)
                    {
                        Debug.LogWarning($"AdaptiveBurst leaf coverage issue: overlaps={leafBuildDiagnostics.OverlapCount}, unassigned={leafBuildDiagnostics.UnassignedCount}.");
                    }

                    if (m_parent.m_logQefDiagnostics)
                    {
                        QefDiagnostics qefDiagnostics = m_qefDiagnostics[0];
                        Debug.Log($"AdaptiveBurst leaves: total={leafBuildDiagnostics.LeafCount}, size1={leafBuildDiagnostics.Size1LeafCount}, size2={leafBuildDiagnostics.Size2LeafCount}, overlaps={leafBuildDiagnostics.OverlapCount}, unassigned={leafBuildDiagnostics.UnassignedCount}.");
                        Debug.Log($"AdaptiveBurst QEF: total={qefDiagnostics.Total}, accepted={qefDiagnostics.Accepted}, outsideFallback={qefDiagnostics.OutsideFallback}, singularFallback={qefDiagnostics.SingularFallback}.");
                    }

                    m_status = WorkerStatus.Done;
                }

                return m_status;
            }

            public void Dispose()
            {
                if (m_disposed)
                {
                    return;
                }

                if (m_readbackStarted)
                {
                    m_readbackRequest.WaitForCompletion();
                }

                if (m_jobsScheduled)
                {
                    m_jobHandle.Complete();
                }

                ReleaseBuffers();
                m_parent.m_voxelConfig.VoxelVolumeConfig.OnDirtied -= CreateBuffers;
                m_disposed = true;
            }

            private void ScheduleMeshJobs()
            {
                int numberOfCellsAlongAxis = m_parent.m_voxelConfig.VoxelVolumeConfig.NumberOfCellsAlongAxis;
                int numberOfVoxelsAlongAxis = m_parent.m_voxelConfig.VoxelVolumeConfig.NumberOfVoxelsAlongAxis;

                JobHandle buildLeavesHandle = new BuildLeavesJob
                {
                    Voxels = m_voxels,
                    Leaves = m_leaves,
                    LeafCount = m_leafCount,
                    Diagnostics = m_leafBuildDiagnostics,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    BuildMode = m_parent.m_leafBuildMode,
                    Size2LeafErrorThreshold = m_parent.m_size2LeafErrorThreshold
                }.Schedule();

                JobHandle fillDenseCellToLeafHandle = new FillDenseCellToLeafJob
                {
                    Leaves = m_leaves,
                    LeafCount = m_leafCount,
                    DenseCellToLeaf = m_denseCellToLeaf,
                    Diagnostics = m_leafBuildDiagnostics,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis
                }.Schedule(buildLeavesHandle);

                JobHandle leafVerticesHandle = new GenerateLeafVerticesJob
                {
                    Voxels = m_voxels,
                    Leaves = m_leaves,
                    LeafCount = m_leafCount,
                    LeafVertexData = m_leafVertexData,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    VoxelSpacing = m_parent.m_voxelConfig.VoxelVolumeConfig.VoxelSpacing
                }.Schedule(m_leaves.Length, 64, fillDenseCellToLeafHandle);

                JobHandle compactLeafVerticesHandle = new CompactLeafVerticesJob
                {
                    Leaves = m_leaves,
                    LeafCount = m_leafCount,
                    LeafVertexData = m_leafVertexData,
                    Vertices = m_vertices,
                    QefDiagnostics = m_qefDiagnostics
                }.Schedule(leafVerticesHandle);

                m_jobHandle = new AdaptiveEdgeTriangulationJob
                {
                    Voxels = m_voxels,
                    Leaves = m_leaves,
                    DenseCellToLeaf = m_denseCellToLeaf,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    Indices = m_indices
                }.Schedule(compactLeafVerticesHandle);
                m_jobsScheduled = true;
            }

            private void CreateBuffers()
            {
                if (m_jobsScheduled)
                {
                    m_jobHandle.Complete();
                    m_jobsScheduled = false;
                }

                if (m_readbackStarted)
                {
                    m_readbackRequest.WaitForCompletion();
                    m_readbackStarted = false;
                }

                ReleaseBuffers();

                int voxelCount = m_parent.m_voxelConfig.VoxelVolumeConfig.VoxelCount;
                int cellCount = m_parent.m_voxelConfig.VoxelVolumeConfig.CellCount;
                int numberOfCellsAlongAxis = m_parent.m_voxelConfig.VoxelVolumeConfig.NumberOfCellsAlongAxis;
                int maxIndexCount = 18 * numberOfCellsAlongAxis * (numberOfCellsAlongAxis - 1) * (numberOfCellsAlongAxis - 1);

                m_voxels = new NativeArray<PackedVoxel>(voxelCount, Allocator.Persistent);
                m_leaves = new NativeArray<AdaptiveLeafCell>(cellCount, Allocator.Persistent);
                m_leafVertexData = new NativeArray<LeafVertexData>(cellCount, Allocator.Persistent);
                m_denseCellToLeaf = new NativeArray<int>(cellCount, Allocator.Persistent);
                m_leafCount = new NativeArray<int>(1, Allocator.Persistent);
                m_qefDiagnostics = new NativeArray<QefDiagnostics>(1, Allocator.Persistent);
                m_leafBuildDiagnostics = new NativeArray<LeafBuildDiagnostics>(1, Allocator.Persistent);
                m_vertices = new NativeList<GPUVertex>(cellCount, Allocator.Persistent);
                m_indices = new NativeList<int>(maxIndexCount, Allocator.Persistent);
            }

            private void ReleaseBuffers()
            {
                if (m_voxels.IsCreated)
                {
                    m_voxels.Dispose();
                }

                if (m_leaves.IsCreated)
                {
                    m_leaves.Dispose();
                }

                if (m_leafVertexData.IsCreated)
                {
                    m_leafVertexData.Dispose();
                }

                if (m_denseCellToLeaf.IsCreated)
                {
                    m_denseCellToLeaf.Dispose();
                }

                if (m_leafCount.IsCreated)
                {
                    m_leafCount.Dispose();
                }

                if (m_qefDiagnostics.IsCreated)
                {
                    m_qefDiagnostics.Dispose();
                }

                if (m_leafBuildDiagnostics.IsCreated)
                {
                    m_leafBuildDiagnostics.Dispose();
                }

                if (m_vertices.IsCreated)
                {
                    m_vertices.Dispose();
                }

                if (m_indices.IsCreated)
                {
                    m_indices.Dispose();
                }
            }
        }

        private enum WorkerStatus
        {
            WaitingForGPUReadback,
            WaitingForJobs,
            Done
        }

        internal struct AdaptiveLeafCell
        {
            public int3 MinCell;
            public int Size;
            public int VertexIndex;
            public float Error;
            public bool Active;
        }

        internal struct LeafVertexData
        {
            public GPUVertex Vertex;
            public float Error;
            public QefPlacementResult PlacementResult;
            public bool Active;
        }

        internal struct QefDiagnostics
        {
            public int Total;
            public int Accepted;
            public int OutsideFallback;
            public int SingularFallback;

            public void Add(QefPlacementResult result)
            {
                Total++;

                switch (result)
                {
                    case QefPlacementResult.Accepted:
                        Accepted++;
                        break;
                    case QefPlacementResult.OutsideFallback:
                        OutsideFallback++;
                        break;
                    case QefPlacementResult.SingularFallback:
                        SingularFallback++;
                        break;
                }
            }
        }

        internal struct LeafBuildDiagnostics
        {
            public int LeafCount;
            public int Size1LeafCount;
            public int Size2LeafCount;
            public int OverlapCount;
            public int UnassignedCount;
        }

        [BurstCompile]
        internal struct BuildLeavesJob : IJob
        {
            [ReadOnly]
            public NativeArray<PackedVoxel> Voxels;
            public NativeArray<AdaptiveLeafCell> Leaves;
            public NativeArray<int> LeafCount;
            public NativeArray<LeafBuildDiagnostics> Diagnostics;
            public int NumberOfVoxelsAlongAxis;
            public int NumberOfCellsAlongAxis;
            public AdaptiveLeafBuildMode BuildMode;
            public float Size2LeafErrorThreshold;

            public void Execute()
            {
                int leafCount = 0;
                LeafBuildDiagnostics diagnostics = default;

                for (int z = 0; z < NumberOfCellsAlongAxis; z += 2)
                {
                    for (int y = 0; y < NumberOfCellsAlongAxis; y += 2)
                    {
                        for (int x = 0; x < NumberOfCellsAlongAxis; x += 2)
                        {
                            int3 minCell = new int3(x, y, z);
                            int3 blockDimensions = new int3(
                                math.min(2, NumberOfCellsAlongAxis - x),
                                math.min(2, NumberOfCellsAlongAxis - y),
                                math.min(2, NumberOfCellsAlongAxis - z));

                            if (math.any(blockDimensions < 2) || TouchesBoundary(minCell, 2, NumberOfCellsAlongAxis))
                            {
                                EmitSizeOneLeaves(minCell, blockDimensions, ref leafCount, ref diagnostics);

                                continue;
                            }

                            bool shouldCoarsen = BuildMode == AdaptiveLeafBuildMode.ToyPattern
                                ? ShouldUseToyCoarsePattern(minCell)
                                : ShouldUseQefCoarseLeaf(minCell);

                            if (shouldCoarsen)
                            {
                                EmitLeaf(minCell, 2, ref leafCount, ref diagnostics);
                            }
                            else
                            {
                                EmitSizeOneLeaves(minCell, new int3(2, 2, 2), ref leafCount, ref diagnostics);
                            }
                        }
                    }
                }

                LeafCount[0] = leafCount;
                diagnostics.LeafCount = leafCount;
                Diagnostics[0] = diagnostics;
            }

            private bool ShouldUseQefCoarseLeaf(int3 minCell)
            {
                return TryEvaluateLeafGeometry(Voxels, NumberOfVoxelsAlongAxis, minCell, 2, out _, out _, out _, out float error, out QefPlacementResult placementResult) &&
                    placementResult == QefPlacementResult.Accepted &&
                    error < Size2LeafErrorThreshold;
            }

            private static bool ShouldUseToyCoarsePattern(int3 minCell)
            {
                return (((minCell.x >> 1) + (minCell.y >> 1) + (minCell.z >> 1)) & 1) == 0;
            }

            private void EmitSizeOneLeaves(int3 minCell, int3 blockDimensions, ref int leafCount, ref LeafBuildDiagnostics diagnostics)
            {
                for (int z = 0; z < blockDimensions.z; z++)
                {
                    for (int y = 0; y < blockDimensions.y; y++)
                    {
                        for (int x = 0; x < blockDimensions.x; x++)
                        {
                            EmitLeaf(minCell + new int3(x, y, z), 1, ref leafCount, ref diagnostics);
                        }
                    }
                }
            }

            private void EmitLeaf(int3 minCell, int size, ref int leafCount, ref LeafBuildDiagnostics diagnostics)
            {
                Leaves[leafCount] = new AdaptiveLeafCell
                {
                    MinCell = minCell,
                    Size = size,
                    VertexIndex = -1,
                    Error = 0.0f,
                    Active = false
                };

                if (size == 1)
                {
                    diagnostics.Size1LeafCount++;
                }
                else
                {
                    diagnostics.Size2LeafCount++;
                }

                leafCount++;
            }
        }

        [BurstCompile]
        internal struct FillDenseCellToLeafJob : IJob
        {
            [ReadOnly]
            public NativeArray<AdaptiveLeafCell> Leaves;
            [ReadOnly]
            public NativeArray<int> LeafCount;
            public NativeArray<int> DenseCellToLeaf;
            public NativeArray<LeafBuildDiagnostics> Diagnostics;
            public int NumberOfCellsAlongAxis;

            public void Execute()
            {
                LeafBuildDiagnostics diagnostics = Diagnostics[0];

                for (int index = 0; index < DenseCellToLeaf.Length; index++)
                {
                    DenseCellToLeaf[index] = -1;
                }

                for (int leafIndex = 0; leafIndex < LeafCount[0]; leafIndex++)
                {
                    AdaptiveLeafCell leaf = Leaves[leafIndex];

                    for (int z = 0; z < leaf.Size; z++)
                    {
                        for (int y = 0; y < leaf.Size; y++)
                        {
                            for (int x = 0; x < leaf.Size; x++)
                            {
                                int denseCellIndex = FlattenIndex(leaf.MinCell + new int3(x, y, z), NumberOfCellsAlongAxis);

                                if (DenseCellToLeaf[denseCellIndex] != -1)
                                {
                                    diagnostics.OverlapCount++;
                                }
                                else
                                {
                                    DenseCellToLeaf[denseCellIndex] = leafIndex;
                                }
                            }
                        }
                    }
                }

                for (int index = 0; index < DenseCellToLeaf.Length; index++)
                {
                    if (DenseCellToLeaf[index] == -1)
                    {
                        diagnostics.UnassignedCount++;
                    }
                }

                Diagnostics[0] = diagnostics;
            }
        }

        [BurstCompile]
        internal struct GenerateLeafVerticesJob : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<PackedVoxel> Voxels;
            [ReadOnly]
            public NativeArray<AdaptiveLeafCell> Leaves;
            [ReadOnly]
            public NativeArray<int> LeafCount;
            public NativeArray<LeafVertexData> LeafVertexData;
            public int NumberOfVoxelsAlongAxis;
            public float VoxelSpacing;

            public void Execute(int index)
            {
                if (index >= LeafCount[0])
                {
                    LeafVertexData[index] = default;

                    return;
                }

                AdaptiveLeafCell leaf = Leaves[index];
                if (!TryEvaluateLeafGeometry(Voxels, NumberOfVoxelsAlongAxis, leaf.MinCell, leaf.Size, out float3 localPosition, out float3 vertexNormal, out MaterialIndex materialIndex, out float error, out QefPlacementResult placementResult))
                {
                    LeafVertexData[index] = default;

                    return;
                }

                float3 volumePosition = VoxelSpacing * (leaf.MinCell + localPosition * leaf.Size - 0.5f * (NumberOfVoxelsAlongAxis - 1.0f));
                LeafVertexData[index] = new LeafVertexData
                {
                    Vertex = new GPUVertex(volumePosition, vertexNormal, materialIndex),
                    Error = error,
                    PlacementResult = placementResult,
                    Active = true
                };
            }
        }

        [BurstCompile]
        internal struct CompactLeafVerticesJob : IJob
        {
            public NativeArray<AdaptiveLeafCell> Leaves;
            [ReadOnly]
            public NativeArray<int> LeafCount;
            [ReadOnly]
            public NativeArray<LeafVertexData> LeafVertexData;
            public NativeList<GPUVertex> Vertices;
            public NativeArray<QefDiagnostics> QefDiagnostics;

            public void Execute()
            {
                QefDiagnostics diagnostics = default;

                for (int leafIndex = 0; leafIndex < LeafCount[0]; leafIndex++)
                {
                    AdaptiveLeafCell leaf = Leaves[leafIndex];
                    LeafVertexData leafVertexData = LeafVertexData[leafIndex];

                    if (!leafVertexData.Active)
                    {
                        leaf.VertexIndex = -1;
                        leaf.Error = 0.0f;
                        leaf.Active = false;
                        Leaves[leafIndex] = leaf;

                        continue;
                    }

                    leaf.VertexIndex = Vertices.Length;
                    leaf.Error = leafVertexData.Error;
                    leaf.Active = true;
                    Leaves[leafIndex] = leaf;
                    Vertices.AddNoResize(leafVertexData.Vertex);
                    diagnostics.Add(leafVertexData.PlacementResult);
                }

                QefDiagnostics[0] = diagnostics;
            }
        }

        [BurstCompile]
        internal struct AdaptiveEdgeTriangulationJob : IJob
        {
            [ReadOnly]
            public NativeArray<PackedVoxel> Voxels;
            [ReadOnly]
            public NativeArray<AdaptiveLeafCell> Leaves;
            [ReadOnly]
            public NativeArray<int> DenseCellToLeaf;
            public int NumberOfVoxelsAlongAxis;
            public int NumberOfCellsAlongAxis;
            public NativeList<int> Indices;

            public void Execute()
            {
                ProcessXEdges();
                ProcessYEdges();
                ProcessZEdges();
            }

            private void ProcessXEdges()
            {
                for (int z = 1; z < NumberOfCellsAlongAxis; z++)
                {
                    for (int y = 1; y < NumberOfCellsAlongAxis; y++)
                    {
                        for (int x = 0; x < NumberOfCellsAlongAxis; x++)
                        {
                            EmitPatchForXEdge(new int3(x, y, z));
                        }
                    }
                }
            }

            private void ProcessYEdges()
            {
                for (int z = 1; z < NumberOfCellsAlongAxis; z++)
                {
                    for (int y = 0; y < NumberOfCellsAlongAxis; y++)
                    {
                        for (int x = 1; x < NumberOfCellsAlongAxis; x++)
                        {
                            EmitPatchForYEdge(new int3(x, y, z));
                        }
                    }
                }
            }

            private void ProcessZEdges()
            {
                for (int z = 0; z < NumberOfCellsAlongAxis; z++)
                {
                    for (int y = 1; y < NumberOfCellsAlongAxis; y++)
                    {
                        for (int x = 1; x < NumberOfCellsAlongAxis; x++)
                        {
                            EmitPatchForZEdge(new int3(x, y, z));
                        }
                    }
                }
            }

            private void EmitPatchForXEdge(int3 edge)
            {
                PackedVoxel sampleA = GetVoxel(edge);
                PackedVoxel sampleB = GetVoxel(edge + new int3(1, 0, 0));

                if (sampleA.IsSolid == sampleB.IsSolid)
                {
                    return;
                }

                EmitPatch(
                    GetLeafVertexIndex(edge + new int3(0, -1, -1)),
                    GetLeafVertexIndex(edge + new int3(0, 0, -1)),
                    GetLeafVertexIndex(edge + new int3(0, -1, 0)),
                    GetLeafVertexIndex(edge),
                    sampleB.Value < 0.0f,
                    0);
            }

            private void EmitPatchForYEdge(int3 edge)
            {
                PackedVoxel sampleA = GetVoxel(edge);
                PackedVoxel sampleB = GetVoxel(edge + new int3(0, 1, 0));

                if (sampleA.IsSolid == sampleB.IsSolid)
                {
                    return;
                }

                EmitPatch(
                    GetLeafVertexIndex(edge + new int3(-1, 0, -1)),
                    GetLeafVertexIndex(edge + new int3(0, 0, -1)),
                    GetLeafVertexIndex(edge + new int3(-1, 0, 0)),
                    GetLeafVertexIndex(edge),
                    sampleB.Value < 0.0f,
                    1);
            }

            private void EmitPatchForZEdge(int3 edge)
            {
                PackedVoxel sampleA = GetVoxel(edge);
                PackedVoxel sampleB = GetVoxel(edge + new int3(0, 0, 1));

                if (sampleA.IsSolid == sampleB.IsSolid)
                {
                    return;
                }

                EmitPatch(
                    GetLeafVertexIndex(edge + new int3(-1, -1, 0)),
                    GetLeafVertexIndex(edge + new int3(0, -1, 0)),
                    GetLeafVertexIndex(edge + new int3(-1, 0, 0)),
                    GetLeafVertexIndex(edge),
                    sampleB.Value < 0.0f,
                    2);
            }

            private void EmitPatch(int i0, int i1, int i2, int i3, bool flip, int axis)
            {
                if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0)
                {
                    return;
                }

                FixedList32Bytes<int> unique = default;
                AddUnique(ref unique, i0);
                AddUnique(ref unique, i1);
                AddUnique(ref unique, i2);
                AddUnique(ref unique, i3);

                if (unique.Length < 3)
                {
                    return;
                }

                if (unique.Length == 3)
                {
                    FixedList32Bytes<int> boundaryUnique = default;
                    bool useForwardBoundaryOrder = axis == 1 ? flip : !flip;

                    AddUnique(ref boundaryUnique, i0);
                    AddUnique(ref boundaryUnique, useForwardBoundaryOrder ? i1 : i2);
                    AddUnique(ref boundaryUnique, i3);
                    AddUnique(ref boundaryUnique, useForwardBoundaryOrder ? i2 : i1);
                    EmitTransitionTriangle(boundaryUnique[0], boundaryUnique[1], boundaryUnique[2]);

                    return;
                }

                EmitQuad(unique[0], unique[1], unique[2], unique[3], flip, axis);
            }

            private void EmitTransitionTriangle(int i0, int i1, int i2)
            {
                EmitTriangle(i0, i1, i2);
            }

            private void EmitQuad(int i0, int i1, int i2, int i3, bool flip, int axis)
            {
                if (axis == 1)
                {
                    if (flip)
                    {
                        EmitTriangle(i0, i1, i3);
                        EmitTriangle(i0, i3, i2);
                    }
                    else
                    {
                        EmitTriangle(i0, i3, i1);
                        EmitTriangle(i0, i2, i3);
                    }
                }
                else if (flip)
                {
                    EmitTriangle(i0, i2, i3);
                    EmitTriangle(i0, i3, i1);
                }
                else
                {
                    EmitTriangle(i0, i3, i2);
                    EmitTriangle(i0, i1, i3);
                }
            }

            private static void AddUnique(ref FixedList32Bytes<int> unique, int value)
            {
                for (int index = 0; index < unique.Length; index++)
                {
                    if (unique[index] == value)
                    {
                        return;
                    }
                }

                unique.Add(value);
            }

            private void EmitTriangle(int first, int second, int third)
            {
                if (first == second || first == third || second == third)
                {
                    return;
                }

                Indices.AddNoResize(first);
                Indices.AddNoResize(second);
                Indices.AddNoResize(third);
            }

            private int GetLeafVertexIndex(int3 denseCellCoordinate)
            {
                int leafIndex = DenseCellToLeaf[FlattenIndex(denseCellCoordinate, NumberOfCellsAlongAxis)];
                if (leafIndex < 0)
                {
                    return -1;
                }

                return Leaves[leafIndex].VertexIndex;
            }

            private PackedVoxel GetVoxel(int3 coordinate)
            {
                return AdaptiveDualContouring.GetVoxel(Voxels, NumberOfVoxelsAlongAxis, coordinate);
            }
        }

        internal struct MaterialCounts
        {
            private int m_dirt;
            private int m_rock;
            private int m_sand;
            private int m_grass;
            private int m_snow;
            private int m_dirtOrder;
            private int m_rockOrder;
            private int m_sandOrder;
            private int m_grassOrder;
            private int m_snowOrder;
            private int m_nextOrder;
            private MaterialIndex m_first;
            private bool m_hasFirst;

            public void Add(MaterialIndex materialIndex)
            {
                if (!m_hasFirst)
                {
                    m_first = materialIndex;
                    m_hasFirst = true;
                }

                switch (materialIndex)
                {
                    case MaterialIndex.Dirt:
                        if (m_dirt == 0) m_dirtOrder = m_nextOrder++;
                        m_dirt++;
                        break;
                    case MaterialIndex.Rock:
                        if (m_rock == 0) m_rockOrder = m_nextOrder++;
                        m_rock++;
                        break;
                    case MaterialIndex.Sand:
                        if (m_sand == 0) m_sandOrder = m_nextOrder++;
                        m_sand++;
                        break;
                    case MaterialIndex.Grass:
                        if (m_grass == 0) m_grassOrder = m_nextOrder++;
                        m_grass++;
                        break;
                    case MaterialIndex.Snow:
                        if (m_snow == 0) m_snowOrder = m_nextOrder++;
                        m_snow++;
                        break;
                }
            }

            public readonly MaterialIndex GetDominant()
            {
                MaterialIndex result = m_first;
                int count = GetCount(result);
                int order = GetOrder(result);

                SetIfDominant(MaterialIndex.Dirt, m_dirt, m_dirtOrder, ref result, ref count, ref order);
                SetIfDominant(MaterialIndex.Rock, m_rock, m_rockOrder, ref result, ref count, ref order);
                SetIfDominant(MaterialIndex.Sand, m_sand, m_sandOrder, ref result, ref count, ref order);
                SetIfDominant(MaterialIndex.Grass, m_grass, m_grassOrder, ref result, ref count, ref order);
                SetIfDominant(MaterialIndex.Snow, m_snow, m_snowOrder, ref result, ref count, ref order);

                return result;
            }

            private readonly int GetCount(MaterialIndex materialIndex)
            {
                switch (materialIndex)
                {
                    case MaterialIndex.Dirt: return m_dirt;
                    case MaterialIndex.Rock: return m_rock;
                    case MaterialIndex.Sand: return m_sand;
                    case MaterialIndex.Grass: return m_grass;
                    case MaterialIndex.Snow: return m_snow;
                    default: return 0;
                }
            }

            private readonly int GetOrder(MaterialIndex materialIndex)
            {
                switch (materialIndex)
                {
                    case MaterialIndex.Dirt: return m_dirtOrder;
                    case MaterialIndex.Rock: return m_rockOrder;
                    case MaterialIndex.Sand: return m_sandOrder;
                    case MaterialIndex.Grass: return m_grassOrder;
                    case MaterialIndex.Snow: return m_snowOrder;
                    default: return 0;
                }
            }

            private static void SetIfDominant(MaterialIndex candidate, int candidateCount, int candidateOrder, ref MaterialIndex result, ref int count, ref int order)
            {
                if (candidateCount > count || candidateCount == count && candidateCount > 0 && candidateOrder < order)
                {
                    result = candidate;
                    count = candidateCount;
                    order = candidateOrder;
                }
            }
        }

        internal static bool TryEvaluateLeafGeometry(
            NativeArray<PackedVoxel> voxels,
            int numberOfVoxelsAlongAxis,
            int3 minCell,
            int size,
            out float3 localPosition,
            out float3 vertexNormal,
            out MaterialIndex materialIndex,
            out float error,
            out QefPlacementResult placementResult)
        {
            QefData qef = default;
            float3 positionSum = float3.zero;
            float3 normalSum = float3.zero;
            MaterialCounts materialCounts = default;
            int numberOfIntersections = 0;

            for (int edgeIndex = 0; edgeIndex < 12; edgeIndex++)
            {
                GetCellEdge(edgeIndex, out int firstCornerIndex, out int secondCornerIndex);
                int3 firstCorner = GetCellCorner(firstCornerIndex);
                int3 secondCorner = GetCellCorner(secondCornerIndex);
                PackedVoxel sampleA = GetVoxel(voxels, numberOfVoxelsAlongAxis, minCell + firstCorner * size);
                PackedVoxel sampleB = GetVoxel(voxels, numberOfVoxelsAlongAxis, minCell + secondCorner * size);

                if (sampleA.IsSolid == sampleB.IsSolid)
                {
                    continue;
                }

                float interpolant = -sampleA.Value / (sampleB.Value - sampleA.Value);
                float3 position = math.lerp((float3)firstCorner, (float3)secondCorner, interpolant);
                float3 intersectionNormal = math.normalizesafe(math.lerp(sampleA.Gradient, sampleB.Gradient, interpolant));
                MaterialIndex intersectionMaterialIndex = !sampleA.IsSolid ? sampleA.MaterialIndex : sampleB.MaterialIndex;

                qef.Add(position, intersectionNormal);
                positionSum += position;
                normalSum += intersectionNormal;
                materialCounts.Add(intersectionMaterialIndex);
                numberOfIntersections++;
            }

            if (numberOfIntersections == 0)
            {
                localPosition = float3.zero;
                vertexNormal = float3.zero;
                materialIndex = default;
                error = 0.0f;
                placementResult = QefPlacementResult.None;

                return false;
            }

            float3 averagePosition = positionSum / numberOfIntersections;
            qef.TrySolveInsideUnitCell(averagePosition, out localPosition, out error, out placementResult);
            vertexNormal = math.normalizesafe(normalSum);
            materialIndex = materialCounts.GetDominant();

            return true;
        }

        internal static bool TouchesBoundary(int3 minCell, int size, int numberOfCellsAlongAxis)
        {
            int3 maxCell = minCell + (size - 1);

            return minCell.x == 0 || minCell.y == 0 || minCell.z == 0 ||
                maxCell.x == numberOfCellsAlongAxis - 1 ||
                maxCell.y == numberOfCellsAlongAxis - 1 ||
                maxCell.z == numberOfCellsAlongAxis - 1;
        }

        internal static int FlattenIndex(int3 coordinate, int axisLength)
        {
            return coordinate.x + axisLength * (coordinate.y + axisLength * coordinate.z);
        }

        private static PackedVoxel GetVoxel(NativeArray<PackedVoxel> voxels, int numberOfVoxelsAlongAxis, int3 coordinate)
        {
            return voxels[FlattenIndex(coordinate, numberOfVoxelsAlongAxis)];
        }

        private static int3 GetCellCorner(int index)
        {
            switch (index)
            {
                case 0: return new int3(0, 0, 0);
                case 1: return new int3(0, 1, 0);
                case 2: return new int3(1, 1, 0);
                case 3: return new int3(1, 0, 0);
                case 4: return new int3(0, 0, 1);
                case 5: return new int3(0, 1, 1);
                case 6: return new int3(1, 1, 1);
                default: return new int3(1, 0, 1);
            }
        }

        private static void GetCellEdge(int index, out int firstCornerIndex, out int secondCornerIndex)
        {
            switch (index)
            {
                case 0: firstCornerIndex = 0; secondCornerIndex = 3; break;
                case 1: firstCornerIndex = 3; secondCornerIndex = 7; break;
                case 2: firstCornerIndex = 7; secondCornerIndex = 4; break;
                case 3: firstCornerIndex = 4; secondCornerIndex = 0; break;
                case 4: firstCornerIndex = 1; secondCornerIndex = 2; break;
                case 5: firstCornerIndex = 2; secondCornerIndex = 6; break;
                case 6: firstCornerIndex = 5; secondCornerIndex = 6; break;
                case 7: firstCornerIndex = 5; secondCornerIndex = 1; break;
                case 8: firstCornerIndex = 0; secondCornerIndex = 1; break;
                case 9: firstCornerIndex = 3; secondCornerIndex = 2; break;
                case 10: firstCornerIndex = 7; secondCornerIndex = 6; break;
                default: firstCornerIndex = 4; secondCornerIndex = 5; break;
            }
        }

        private sealed class Task : IPoolable, IRequest
        {
            public bool Canceled { get; private set; }
            public ComputeBuffer VoxelVolumeBuffer { get; set; }
            public OnMeshGenerated Callback { get; set; }

            public void OnAcquire() { }

            public void OnRelease()
            {
                VoxelVolumeBuffer = null;
                Callback = null;
                Canceled = false;
            }

            public void Cancel() => Canceled = true;
        }
    }
}
