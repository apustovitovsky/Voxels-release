using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using Tuntenfisch.Generics;
using Tuntenfisch.Generics.Pool;
using Tuntenfisch.Voxels.Materials;
using Unity.Burst;
using Unity.Collections;
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
        private bool m_logLeafDiagnostics = false;
        [SerializeField]
        private float m_parentErrorThreshold = 0.01f;
        [SerializeField]
        [Range(-1.0f, 1.0f)]
        private float m_normalDotThreshold = 0.92f;
        [SerializeField]
        private bool m_splitOnMaterialChange = true;

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
            task.TargetLOD = targetLOD;
            task.Callback = callback ?? throw new ArgumentNullException(nameof(callback));

            // AdaptiveBurst still ignores worldPosition; it meshes local chunk-space voxel data.
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
            private NativeList<OctreeBuildNode> m_buildStack;
            private NativeList<GPUVertex> m_vertices;
            private NativeList<int> m_indices;
            private AsyncGPUReadbackRequest m_readbackRequest;
            private JobHandle m_jobHandle;
            private int m_targetLOD;
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
                m_targetLOD = task.TargetLOD;
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

                    if (m_parent.m_logLeafDiagnostics)
                    {
                        QefDiagnostics qefDiagnostics = m_qefDiagnostics[0];
                        Debug.Log($"AdaptiveBurst leaves: total={leafBuildDiagnostics.LeafCount}, size1={leafBuildDiagnostics.Size1LeafCount}, size2={leafBuildDiagnostics.Size2LeafCount}, size4={leafBuildDiagnostics.Size4LeafCount}, size8={leafBuildDiagnostics.Size8LeafCount}, size16plus={leafBuildDiagnostics.Size16LeafCount}, overlaps={leafBuildDiagnostics.OverlapCount}, unassigned={leafBuildDiagnostics.UnassignedCount}, parentInactiveKept={leafBuildDiagnostics.ParentInactiveKept}, splitParentRejected={leafBuildDiagnostics.SplitByParentRejected}, splitParentError={leafBuildDiagnostics.SplitByParentError}, splitNormal={leafBuildDiagnostics.SplitByNormal}, splitMaterial={leafBuildDiagnostics.SplitByMaterial}.");
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
                int interiorSize = numberOfCellsAlongAxis - 2;
                int maxDepth = ComputePowerOfTwoExponent(interiorSize);
                int minCellSize = 1 << math.clamp(m_targetLOD, 0, maxDepth);

                JobHandle buildLeavesHandle = new BuildLeavesJob
                {
                    Voxels = m_voxels,
                    Leaves = m_leaves,
                    BuildStack = m_buildStack,
                    LeafCount = m_leafCount,
                    Diagnostics = m_leafBuildDiagnostics,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    RootMinCell = new int3(1, 1, 1),
                    RootCellSize = interiorSize,
                    MinCellSize = minCellSize,
                    VoxelSpacing = m_parent.m_voxelConfig.VoxelVolumeConfig.VoxelSpacing,
                    ParentErrorThreshold = m_parent.m_parentErrorThreshold,
                    NormalDotThreshold = m_parent.m_normalDotThreshold,
                    SplitOnMaterialChange = m_parent.m_splitOnMaterialChange
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
                m_buildStack = new NativeList<OctreeBuildNode>(cellCount, Allocator.Persistent);
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

                if (m_buildStack.IsCreated)
                {
                    m_buildStack.Dispose();
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

            private static int ComputePowerOfTwoExponent(int value)
            {
                int exponent = 0;
                while (value > 1)
                {
                    value >>= 1;
                    exponent++;
                }

                return exponent;
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

        internal struct OctreeBuildNode
        {
            public int3 MinCell;
            public int Size;
        }

        internal struct LeafVertexData
        {
            public GPUVertex Vertex;
            public float Error;
            public QefPlacementResult PlacementResult;
            public bool Active;
        }

        internal struct LeafGeometry
        {
            public bool Active;
            public float3 LocalPosition;
            public float3 VolumePosition;
            public float3 Normal;
            public MaterialIndex MaterialIndex;
            public float Error;
            public int IntersectionCount;
            public QefPlacementResult PlacementResult;
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
            public int Size4LeafCount;
            public int Size8LeafCount;
            public int Size16LeafCount;
            public int OverlapCount;
            public int UnassignedCount;
            public int ParentInactiveKept;
            public int SplitByParentRejected;
            public int SplitByParentError;
            public int SplitByNormal;
            public int SplitByMaterial;
        }

        [BurstCompile]
        internal struct BuildLeavesJob : IJob
        {
            [ReadOnly]
            public NativeArray<PackedVoxel> Voxels;
            public NativeArray<AdaptiveLeafCell> Leaves;
            public NativeList<OctreeBuildNode> BuildStack;
            public NativeArray<int> LeafCount;
            public NativeArray<LeafBuildDiagnostics> Diagnostics;
            public int NumberOfVoxelsAlongAxis;
            public int NumberOfCellsAlongAxis;
            public int3 RootMinCell;
            public int RootCellSize;
            public int MinCellSize;
            public float VoxelSpacing;
            public float ParentErrorThreshold;
            public float NormalDotThreshold;
            public bool SplitOnMaterialChange;

            public void Execute()
            {
                int leafCount = 0;
                LeafBuildDiagnostics diagnostics = default;
                BuildStack.Clear();

                for (int z = 0; z < NumberOfCellsAlongAxis; z++)
                {
                    for (int y = 0; y < NumberOfCellsAlongAxis; y++)
                    {
                        for (int x = 0; x < NumberOfCellsAlongAxis; x++)
                        {
                            int3 minCell = new int3(x, y, z);
                            if (!IsOuterDenseCell(minCell))
                            {
                                continue;
                            }

                            EmitLeaf(minCell, 1, ref leafCount, ref diagnostics);
                        }
                    }
                }

                if (RootCellSize > 0)
                {
                    BuildStack.Add(new OctreeBuildNode
                    {
                        MinCell = RootMinCell,
                        Size = RootCellSize
                    });
                }

                while (BuildStack.Length > 0)
                {
                    int lastIndex = BuildStack.Length - 1;
                    OctreeBuildNode node = BuildStack[lastIndex];
                    BuildStack.RemoveAt(lastIndex);

                    if (node.Size <= MinCellSize)
                    {
                        EmitLeaf(node.MinCell, node.Size, ref leafCount, ref diagnostics);

                        continue;
                    }

                    if (!ShouldSplitNode(node.MinCell, node.Size, ref diagnostics))
                    {
                        EmitLeaf(node.MinCell, node.Size, ref leafCount, ref diagnostics);

                        continue;
                    }

                    int childSize = node.Size >> 1;
                    for (int z = 1; z >= 0; z--)
                    {
                        for (int y = 1; y >= 0; y--)
                        {
                            for (int x = 1; x >= 0; x--)
                            {
                                BuildStack.Add(new OctreeBuildNode
                                {
                                    MinCell = node.MinCell + new int3(x, y, z) * childSize,
                                    Size = childSize
                                });
                            }
                        }
                    }
                }

                LeafCount[0] = leafCount;
                diagnostics.LeafCount = leafCount;
                Diagnostics[0] = diagnostics;
            }

            private bool ShouldSplitNode(int3 minCell, int size, ref LeafBuildDiagnostics diagnostics)
            {
                LeafGeometry parent = EvaluateLeafGeometry(Voxels, NumberOfVoxelsAlongAxis, minCell, size, VoxelSpacing);
                int childSize = size >> 1;
                if (!parent.Active)
                {
                    if (HasAnyActiveChild(minCell, childSize))
                    {
                        diagnostics.SplitByParentRejected++;

                        return true;
                    }

                    diagnostics.ParentInactiveKept++;

                    return false;
                }

                if (parent.PlacementResult != QefPlacementResult.Accepted)
                {
                    diagnostics.SplitByParentRejected++;

                    return true;
                }

                int activeChildCount = 0;
                float minNormalDot = 1.0f;
                bool hasMaterial = false;
                bool sameMaterial = true;
                MaterialIndex firstMaterial = default;

                for (int z = 0; z < 2; z++)
                {
                    for (int y = 0; y < 2; y++)
                    {
                        for (int x = 0; x < 2; x++)
                        {
                            LeafGeometry child = EvaluateLeafGeometry(Voxels, NumberOfVoxelsAlongAxis, minCell + new int3(x, y, z) * childSize, childSize, VoxelSpacing);
                            if (!child.Active)
                            {
                                continue;
                            }

                            activeChildCount++;
                            minNormalDot = math.min(minNormalDot, math.abs(math.dot(parent.Normal, child.Normal)));

                            if (!hasMaterial)
                            {
                                firstMaterial = child.MaterialIndex;
                                hasMaterial = true;
                            }
                            else if (child.MaterialIndex != firstMaterial)
                            {
                                sameMaterial = false;
                            }
                        }
                    }
                }

                if (activeChildCount == 0)
                {
                    return false;
                }

                float normalizedParentError = parent.Error / math.max(1, parent.IntersectionCount);

                if (normalizedParentError > ParentErrorThreshold)
                {
                    diagnostics.SplitByParentError++;

                    return true;
                }

                // Keep ratio diagnostics available for later tuning, but do not split nearly planar
                // regions from tiny child errors while the size-1/2 heuristic is being validated.

                if (minNormalDot < NormalDotThreshold)
                {
                    diagnostics.SplitByNormal++;

                    return true;
                }

                if (SplitOnMaterialChange && !sameMaterial)
                {
                    diagnostics.SplitByMaterial++;

                    return true;
                }

                return false;
            }

            private bool HasAnyActiveChild(int3 minCell, int childSize)
            {
                for (int z = 0; z < 2; z++)
                {
                    for (int y = 0; y < 2; y++)
                    {
                        for (int x = 0; x < 2; x++)
                        {
                            if (EvaluateLeafGeometry(Voxels, NumberOfVoxelsAlongAxis, minCell + new int3(x, y, z) * childSize, childSize, VoxelSpacing).Active)
                            {
                                return true;
                            }
                        }
                    }
                }

                return false;
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

                RecordLeafSize(size, ref diagnostics);

                leafCount++;
            }

            private static void RecordLeafSize(int size, ref LeafBuildDiagnostics diagnostics)
            {
                switch (size)
                {
                    case 1: diagnostics.Size1LeafCount++; break;
                    case 2: diagnostics.Size2LeafCount++; break;
                    case 4: diagnostics.Size4LeafCount++; break;
                    case 8: diagnostics.Size8LeafCount++; break;
                    default: diagnostics.Size16LeafCount++; break;
                }
            }

            private bool IsOuterDenseCell(int3 cell)
            {
                return cell.x == 0 || cell.y == 0 || cell.z == 0 ||
                    cell.x == NumberOfCellsAlongAxis - 1 ||
                    cell.y == NumberOfCellsAlongAxis - 1 ||
                    cell.z == NumberOfCellsAlongAxis - 1;
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
                LeafGeometry geometry = EvaluateLeafGeometry(Voxels, NumberOfVoxelsAlongAxis, leaf.MinCell, leaf.Size, VoxelSpacing);
                if (!geometry.Active)
                {
                    LeafVertexData[index] = default;

                    return;
                }

                LeafVertexData[index] = new LeafVertexData
                {
                    Vertex = new GPUVertex(geometry.VolumePosition, geometry.Normal, geometry.MaterialIndex),
                    Error = geometry.Error,
                    PlacementResult = geometry.PlacementResult,
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

                FixedList32Bytes<int> boundary = default;
                bool useI1First = axis == 1 ? flip : !flip;
                boundary.Add(i0);
                boundary.Add(useI1First ? i1 : i2);
                boundary.Add(i3);
                boundary.Add(useI1First ? i2 : i1);

                FixedList32Bytes<int> unique = default;
                for (int index = 0; index < boundary.Length; index++)
                {
                    AddUnique(ref unique, boundary[index]);
                }

                if (unique.Length < 3)
                {
                    return;
                }

                if (unique.Length == 3)
                {
                    EmitTriangle(unique[0], unique[1], unique[2]);
                    return;
                }

                EmitTriangle(unique[0], unique[1], unique[2]);
                EmitTriangle(unique[0], unique[2], unique[3]);
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

        internal static LeafGeometry EvaluateLeafGeometry(
            NativeArray<PackedVoxel> voxels,
            int numberOfVoxelsAlongAxis,
            int3 minCell,
            int size,
            float voxelSpacing)
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
                return default;
            }

            float3 averagePosition = positionSum / numberOfIntersections;
            qef.TrySolveInsideUnitCell(averagePosition, out float3 localPosition, out float error, out QefPlacementResult placementResult);

            return new LeafGeometry
            {
                Active = true,
                LocalPosition = localPosition,
                VolumePosition = voxelSpacing * (minCell + localPosition * size - 0.5f * (numberOfVoxelsAlongAxis - 1.0f)),
                Normal = math.normalizesafe(normalSum),
                MaterialIndex = materialCounts.GetDominant(),
                Error = error,
                IntersectionCount = numberOfIntersections,
                PlacementResult = placementResult
            };
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
            public int TargetLOD { get; set; }
            public OnMeshGenerated Callback { get; set; }

            public void OnAcquire() { }

            public void OnRelease()
            {
                VoxelVolumeBuffer = null;
                TargetLOD = 0;
                Callback = null;
                Canceled = false;
            }

            public void Cancel() => Canceled = true;
        }
    }
}
