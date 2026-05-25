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
        private bool m_logQefDiagnostics = false;

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

            // Dense Burst DC deliberately ignores targetLOD and worldPosition in this milestone.
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
            private NativeArray<CellVertex> m_vertexPerCell;
            private NativeArray<int> m_cellToVertexIndex;
            private NativeArray<QefDiagnostics> m_qefDiagnostics;
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

                    // Keep worker-owned voxel storage independent from readback request lifetime.
                    NativeArray<PackedVoxel>.Copy(m_readbackRequest.GetData<PackedVoxel>(), m_voxels);
                    ScheduleMeshJobs();
                    m_status = WorkerStatus.WaitingForJobs;
                }

                if (m_status == WorkerStatus.WaitingForJobs && m_jobHandle.IsCompleted)
                {
                    m_jobHandle.Complete();
                    m_jobsScheduled = false;

                    if (m_parent.m_logQefDiagnostics)
                    {
                        QefDiagnostics diagnostics = m_qefDiagnostics[0];
                        Debug.Log($"AdaptiveBurst QEF: total={diagnostics.Total}, accepted={diagnostics.Accepted}, outsideFallback={diagnostics.OutsideFallback}, singularFallback={diagnostics.SingularFallback}.");
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

                JobHandle cellVerticesHandle = new GenerateCellVerticesJob
                {
                    Voxels = m_voxels,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    VoxelSpacing = m_parent.m_voxelConfig.VoxelVolumeConfig.VoxelSpacing,
                    VertexPerCell = m_vertexPerCell
                }.Schedule(m_vertexPerCell.Length, 64);

                JobHandle compactVerticesHandle = new CompactVerticesJob
                {
                    VertexPerCell = m_vertexPerCell,
                    CellToVertexIndex = m_cellToVertexIndex,
                    Vertices = m_vertices,
                    Diagnostics = m_qefDiagnostics
                }.Schedule(cellVerticesHandle);

                m_jobHandle = new GenerateTrianglesJob
                {
                    Voxels = m_voxels,
                    CellToVertexIndex = m_cellToVertexIndex,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    Indices = m_indices
                }.Schedule(compactVerticesHandle);
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
                m_vertexPerCell = new NativeArray<CellVertex>(cellCount, Allocator.Persistent);
                m_cellToVertexIndex = new NativeArray<int>(cellCount, Allocator.Persistent);
                m_qefDiagnostics = new NativeArray<QefDiagnostics>(1, Allocator.Persistent);
                m_vertices = new NativeList<GPUVertex>(cellCount, Allocator.Persistent);
                m_indices = new NativeList<int>(maxIndexCount, Allocator.Persistent);
            }

            private void ReleaseBuffers()
            {
                if (m_voxels.IsCreated)
                {
                    m_voxels.Dispose();
                }

                if (m_vertexPerCell.IsCreated)
                {
                    m_vertexPerCell.Dispose();
                }

                if (m_cellToVertexIndex.IsCreated)
                {
                    m_cellToVertexIndex.Dispose();
                }

                if (m_qefDiagnostics.IsCreated)
                {
                    m_qefDiagnostics.Dispose();
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

        internal struct CellVertex
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

        [BurstCompile]
        internal struct GenerateCellVerticesJob : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<PackedVoxel> Voxels;
            public int NumberOfVoxelsAlongAxis;
            public int NumberOfCellsAlongAxis;
            public float VoxelSpacing;
            [WriteOnly]
            public NativeArray<CellVertex> VertexPerCell;

            public void Execute(int index)
            {
                int3 coordinate = CalculateCoordinate(index, NumberOfCellsAlongAxis);
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
                    PackedVoxel sampleA = GetVoxel(coordinate + firstCorner);
                    PackedVoxel sampleB = GetVoxel(coordinate + secondCorner);

                    if (sampleA.IsSolid == sampleB.IsSolid)
                    {
                        continue;
                    }

                    float interpolant = -sampleA.Value / (sampleB.Value - sampleA.Value);
                    float3 position = math.lerp(firstCorner, secondCorner, interpolant);
                    float3 intersectionNormal = math.normalizesafe(math.lerp(sampleA.Gradient, sampleB.Gradient, interpolant));
                    MaterialIndex materialIndex = !sampleA.IsSolid ? sampleA.MaterialIndex : sampleB.MaterialIndex;

                    qef.Add(position, intersectionNormal);
                    positionSum += position;
                    normalSum += intersectionNormal;
                    materialCounts.Add(materialIndex);
                    numberOfIntersections++;
                }

                if (numberOfIntersections == 0)
                {
                    VertexPerCell[index] = default;

                    return;
                }

                float3 averagePosition = positionSum / numberOfIntersections;
                qef.TrySolveInsideUnitCell(averagePosition, out float3 localPosition, out float error, out QefPlacementResult placementResult);
                float3 volumePosition = VoxelSpacing * (localPosition + coordinate - 0.5f * (NumberOfVoxelsAlongAxis - 1.0f));
                float3 vertexNormal = math.normalizesafe(normalSum);

                VertexPerCell[index] = new CellVertex
                {
                    Vertex = new GPUVertex(volumePosition, vertexNormal, materialCounts.GetDominant()),
                    Error = error,
                    PlacementResult = placementResult,
                    Active = true
                };
            }

            private PackedVoxel GetVoxel(int3 coordinate)
            {
                return Voxels[coordinate.x + NumberOfVoxelsAlongAxis * (coordinate.y + NumberOfVoxelsAlongAxis * coordinate.z)];
            }
        }

        [BurstCompile]
        internal struct CompactVerticesJob : IJob
        {
            [ReadOnly]
            public NativeArray<CellVertex> VertexPerCell;
            public NativeArray<int> CellToVertexIndex;
            public NativeList<GPUVertex> Vertices;
            public NativeArray<QefDiagnostics> Diagnostics;

            public void Execute()
            {
                QefDiagnostics diagnostics = default;

                for (int index = 0; index < VertexPerCell.Length; index++)
                {
                    CellVertex cellVertex = VertexPerCell[index];

                    if (!cellVertex.Active)
                    {
                        CellToVertexIndex[index] = -1;

                        continue;
                    }

                    CellToVertexIndex[index] = Vertices.Length;
                    Vertices.AddNoResize(cellVertex.Vertex);
                    diagnostics.Add(cellVertex.PlacementResult);
                }

                Diagnostics[0] = diagnostics;
            }
        }

        [BurstCompile]
        internal struct GenerateTrianglesJob : IJob
        {
            [ReadOnly]
            public NativeArray<PackedVoxel> Voxels;
            [ReadOnly]
            public NativeArray<int> CellToVertexIndex;
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

                int i0 = GetCellVertexIndex(edge + new int3(0, -1, -1));
                int i1 = GetCellVertexIndex(edge + new int3(0, 0, -1));
                int i2 = GetCellVertexIndex(edge + new int3(0, -1, 0));
                int i3 = GetCellVertexIndex(edge);

                if (!IsValidQuad(i0, i1, i2, i3))
                {
                    return;
                }

                if (sampleB.Value < 0.0f)
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

            private void EmitPatchForYEdge(int3 edge)
            {
                PackedVoxel sampleA = GetVoxel(edge);
                PackedVoxel sampleB = GetVoxel(edge + new int3(0, 1, 0));

                if (sampleA.IsSolid == sampleB.IsSolid)
                {
                    return;
                }

                int i0 = GetCellVertexIndex(edge + new int3(-1, 0, -1));
                int i1 = GetCellVertexIndex(edge + new int3(0, 0, -1));
                int i2 = GetCellVertexIndex(edge + new int3(-1, 0, 0));
                int i3 = GetCellVertexIndex(edge);

                if (!IsValidQuad(i0, i1, i2, i3))
                {
                    return;
                }

                if (sampleB.Value < 0.0f)
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

            private void EmitPatchForZEdge(int3 edge)
            {
                PackedVoxel sampleA = GetVoxel(edge);
                PackedVoxel sampleB = GetVoxel(edge + new int3(0, 0, 1));

                if (sampleA.IsSolid == sampleB.IsSolid)
                {
                    return;
                }

                int i0 = GetCellVertexIndex(edge + new int3(-1, -1, 0));
                int i1 = GetCellVertexIndex(edge + new int3(0, -1, 0));
                int i2 = GetCellVertexIndex(edge + new int3(-1, 0, 0));
                int i3 = GetCellVertexIndex(edge);

                if (!IsValidQuad(i0, i1, i2, i3))
                {
                    return;
                }

                if (sampleB.Value < 0.0f)
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

            private static bool IsValidQuad(int i0, int i1, int i2, int i3)
            {
                return i0 >= 0 && i1 >= 0 && i2 >= 0 && i3 >= 0 &&
                    i0 != i1 && i0 != i2 && i0 != i3 &&
                    i1 != i2 && i1 != i3 && i2 != i3;
            }

            private void EmitTriangle(int first, int second, int third)
            {
                Indices.AddNoResize(first);
                Indices.AddNoResize(second);
                Indices.AddNoResize(third);
            }

            private int GetCellVertexIndex(int3 coordinate)
            {
                return CellToVertexIndex[coordinate.x + NumberOfCellsAlongAxis * (coordinate.y + NumberOfCellsAlongAxis * coordinate.z)];
            }

            private PackedVoxel GetVoxel(int3 coordinate)
            {
                return Voxels[coordinate.x + NumberOfVoxelsAlongAxis * (coordinate.y + NumberOfVoxelsAlongAxis * coordinate.z)];
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

        private static int3 CalculateCoordinate(int index, int axisLength)
        {
            int x = index % axisLength;
            int y = index / axisLength % axisLength;
            int z = index / (axisLength * axisLength);

            return new int3(x, y, z);
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
