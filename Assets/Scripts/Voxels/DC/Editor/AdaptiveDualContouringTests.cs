using NUnit.Framework;
using Tuntenfisch.Voxels.Materials;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Tuntenfisch.Voxels.DC.Editor
{
    public class AdaptiveDualContouringTests
    {
        [Test]
        public void PackedVoxel_DecodesValueMaterialAndSolidContract()
        {
            PackedVoxel voxel = new PackedVoxel
            {
                PackedValueAndMaterialIndex = math.f32tof16(0.25f) | ((uint)MaterialIndex.Grass << 16)
            };

            Assert.That(voxel.Value, Is.EqualTo(0.25f).Within(0.001f));
            Assert.That(voxel.MaterialIndex, Is.EqualTo(MaterialIndex.Grass));
            Assert.That(voxel.IsSolid, Is.True);

            voxel.PackedValueAndMaterialIndex = math.f32tof16(-0.25f);

            Assert.That(voxel.IsSolid, Is.False);
        }

        [TestCase(0.0f, 0.0f, 0.0f, 0.0f, 1.0f)]
        [TestCase(1.0f, 0.0f, 1.0f, 0.0f, 0.0f)]
        [TestCase(-1.0f, 0.0f, -1.0f, 0.0f, 0.0f)]
        [TestCase(0.0f, 1.0f, 0.0f, 1.0f, 0.0f)]
        [TestCase(0.0f, -1.0f, 0.0f, -1.0f, 0.0f)]
        [TestCase(1.0f, 1.0f, 0.0f, 0.0f, -1.0f)]
        [TestCase(0.333333f, 0.333333f, 0.57735f, 0.57735f, 0.57735f)]
        public void PackedVoxel_DecodesOctahedralGradient(float encodedX, float encodedY, float expectedX, float expectedY, float expectedZ)
        {
            PackedVoxel voxel = new PackedVoxel
            {
                PackedGradient = math.f32tof16(new float2(encodedX, encodedY)).x |
                    (math.f32tof16(new float2(encodedX, encodedY)).y << 16)
            };

            Assert.That(voxel.Gradient.x, Is.EqualTo(expectedX).Within(0.001f));
            Assert.That(voxel.Gradient.y, Is.EqualTo(expectedY).Within(0.001f));
            Assert.That(voxel.Gradient.z, Is.EqualTo(expectedZ).Within(0.001f));
        }

        [Test]
        public void QefData_SolvesIntersectionOfAxisPlanes()
        {
            QefData qef = default;
            qef.Add(new float3(0.25f, 0.0f, 0.0f), new float3(1.0f, 0.0f, 0.0f));
            qef.Add(new float3(0.0f, 0.5f, 0.0f), new float3(0.0f, 1.0f, 0.0f));
            qef.Add(new float3(0.0f, 0.0f, 0.75f), new float3(0.0f, 0.0f, 1.0f));

            bool accepted = qef.TrySolveInsideUnitCell(float3.zero, out float3 position, out float error, out QefPlacementResult result);

            Assert.That(accepted, Is.True);
            Assert.That(result, Is.EqualTo(QefPlacementResult.Accepted));
            Assert.That(position.x, Is.EqualTo(0.25f).Within(0.001f));
            Assert.That(position.y, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(position.z, Is.EqualTo(0.75f).Within(0.001f));
            Assert.That(error, Is.EqualTo(0.0f).Within(0.001f));
        }

        [Test]
        public void QefData_BiasStabilizesPlanarInput()
        {
            QefData qef = default;
            qef.Add(new float3(0.25f, 0.0f, 0.0f), new float3(1.0f, 0.0f, 0.0f));
            qef.Add(new float3(0.25f, 1.0f, 0.0f), new float3(1.0f, 0.0f, 0.0f));
            float3 fallback = new float3(0.25f, 0.5f, 0.5f);

            bool accepted = qef.TrySolveInsideUnitCell(fallback, out float3 position, out _, out QefPlacementResult result);

            Assert.That(accepted, Is.True);
            Assert.That(result, Is.EqualTo(QefPlacementResult.Accepted));
            Assert.That(position.x, Is.EqualTo(0.25f).Within(0.001f));
            Assert.That(position.y, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(position.z, Is.EqualTo(0.5f).Within(0.001f));
        }

        [Test]
        public void QefData_UsesFallbackForOutsideCellCandidate()
        {
            QefData qef = default;
            qef.Add(new float3(2.0f, 0.0f, 0.0f), new float3(1.0f, 0.0f, 0.0f));
            qef.Add(new float3(0.0f, 0.5f, 0.0f), new float3(0.0f, 1.0f, 0.0f));
            qef.Add(new float3(0.0f, 0.0f, 0.5f), new float3(0.0f, 0.0f, 1.0f));
            float3 fallback = new float3(0.5f);

            bool accepted = qef.TrySolveInsideUnitCell(fallback, out float3 position, out _, out QefPlacementResult result);

            Assert.That(accepted, Is.False);
            Assert.That(result, Is.EqualTo(QefPlacementResult.OutsideFallback));
            Assert.That(position, Is.EqualTo(fallback));
        }

        [Test]
        public void QefData_UsesFallbackForNonFiniteInput()
        {
            QefData qef = default;
            qef.Add(new float3(float.NaN, 0.0f, 0.0f), new float3(1.0f, 0.0f, 0.0f));
            qef.Add(new float3(0.0f, 0.5f, 0.0f), new float3(0.0f, 1.0f, 0.0f));
            qef.Add(new float3(0.0f, 0.0f, 0.5f), new float3(0.0f, 0.0f, 1.0f));
            float3 fallback = new float3(0.5f);

            bool accepted = qef.TrySolveInsideUnitCell(fallback, out float3 position, out _, out QefPlacementResult result);

            Assert.That(accepted, Is.False);
            Assert.That(result, Is.EqualTo(QefPlacementResult.SingularFallback));
            Assert.That(position, Is.EqualTo(fallback));
        }

        [Test]
        public void MaterialCounts_PreservesFirstEncounteredMaterialOnTie()
        {
            AdaptiveDualContouring.MaterialCounts counts = default;
            counts.Add(MaterialIndex.Rock);
            counts.Add(MaterialIndex.Dirt);

            Assert.That(counts.GetDominant(), Is.EqualTo(MaterialIndex.Rock));
        }

        [Test]
        public void BuildLeavesJob_ToyPatternProducesMixedLeavesAndBoundarySizeOne()
        {
            const int numberOfVoxelsAlongAxis = 8;
            const int numberOfCellsAlongAxis = numberOfVoxelsAlongAxis - 1;
            const int voxelCount = numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis;
            const int cellCount = numberOfCellsAlongAxis * numberOfCellsAlongAxis * numberOfCellsAlongAxis;

            NativeArray<PackedVoxel> voxels = new NativeArray<PackedVoxel>(voxelCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.AdaptiveLeafCell> leaves = new NativeArray<AdaptiveDualContouring.AdaptiveLeafCell>(cellCount, Allocator.TempJob);
            NativeArray<int> leafCount = new NativeArray<int>(1, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.LeafBuildDiagnostics> diagnostics = new NativeArray<AdaptiveDualContouring.LeafBuildDiagnostics>(1, Allocator.TempJob);

            try
            {
                new AdaptiveDualContouring.BuildLeavesJob
                {
                    Voxels = voxels,
                    Leaves = leaves,
                    LeafCount = leafCount,
                    Diagnostics = diagnostics,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    BuildMode = AdaptiveDualContouring.AdaptiveLeafBuildMode.ToyPattern,
                    Size2LeafErrorThreshold = 0.01f
                }.Schedule().Complete();

                Assert.That(leafCount[0], Is.LessThanOrEqualTo(cellCount));
                Assert.That(diagnostics[0].Size1LeafCount, Is.GreaterThan(0));
                Assert.That(diagnostics[0].Size2LeafCount, Is.GreaterThan(0));

                for (int leafIndex = 0; leafIndex < leafCount[0]; leafIndex++)
                {
                    AdaptiveDualContouring.AdaptiveLeafCell leaf = leaves[leafIndex];

                    if (AdaptiveDualContouring.TouchesBoundary(leaf.MinCell, leaf.Size, numberOfCellsAlongAxis))
                    {
                        Assert.That(leaf.Size, Is.EqualTo(1));
                    }
                }
            }
            finally
            {
                diagnostics.Dispose();
                leafCount.Dispose();
                leaves.Dispose();
                voxels.Dispose();
            }
        }

        [Test]
        public void FillDenseCellToLeaf_CoversAllDenseCellsWithoutOverlap()
        {
            const int numberOfVoxelsAlongAxis = 8;
            const int numberOfCellsAlongAxis = numberOfVoxelsAlongAxis - 1;
            const int voxelCount = numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis;
            const int cellCount = numberOfCellsAlongAxis * numberOfCellsAlongAxis * numberOfCellsAlongAxis;

            NativeArray<PackedVoxel> voxels = new NativeArray<PackedVoxel>(voxelCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.AdaptiveLeafCell> leaves = new NativeArray<AdaptiveDualContouring.AdaptiveLeafCell>(cellCount, Allocator.TempJob);
            NativeArray<int> leafCount = new NativeArray<int>(1, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.LeafBuildDiagnostics> diagnostics = new NativeArray<AdaptiveDualContouring.LeafBuildDiagnostics>(1, Allocator.TempJob);
            NativeArray<int> denseCellToLeaf = new NativeArray<int>(cellCount, Allocator.TempJob);

            try
            {
                JobHandle buildHandle = new AdaptiveDualContouring.BuildLeavesJob
                {
                    Voxels = voxels,
                    Leaves = leaves,
                    LeafCount = leafCount,
                    Diagnostics = diagnostics,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    BuildMode = AdaptiveDualContouring.AdaptiveLeafBuildMode.ToyPattern,
                    Size2LeafErrorThreshold = 0.01f
                }.Schedule();

                new AdaptiveDualContouring.FillDenseCellToLeafJob
                {
                    Leaves = leaves,
                    LeafCount = leafCount,
                    DenseCellToLeaf = denseCellToLeaf,
                    Diagnostics = diagnostics,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis
                }.Schedule(buildHandle).Complete();

                Assert.That(diagnostics[0].OverlapCount, Is.EqualTo(0));
                Assert.That(diagnostics[0].UnassignedCount, Is.EqualTo(0));

                for (int index = 0; index < denseCellToLeaf.Length; index++)
                {
                    Assert.That(denseCellToLeaf[index], Is.InRange(0, leafCount[0] - 1));
                }
            }
            finally
            {
                denseCellToLeaf.Dispose();
                diagnostics.Dispose();
                leafCount.Dispose();
                leaves.Dispose();
                voxels.Dispose();
            }
        }

        [Test]
        public void GenerateLeafVerticesJob_SizeTwoLeafScalesVolumePosition()
        {
            const int numberOfVoxelsAlongAxis = 5;
            const int voxelCount = numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis;

            NativeArray<PackedVoxel> voxels = new NativeArray<PackedVoxel>(voxelCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.AdaptiveLeafCell> leaves = new NativeArray<AdaptiveDualContouring.AdaptiveLeafCell>(1, Allocator.TempJob);
            NativeArray<int> leafCount = new NativeArray<int>(1, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.LeafVertexData> leafVertexData = new NativeArray<AdaptiveDualContouring.LeafVertexData>(1, Allocator.TempJob);

            try
            {
                FillPlaneVoxels(voxels, numberOfVoxelsAlongAxis, 0, false, 1.5f);
                leaves[0] = new AdaptiveDualContouring.AdaptiveLeafCell
                {
                    MinCell = new int3(1, 1, 1),
                    Size = 2,
                    VertexIndex = -1
                };
                leafCount[0] = 1;

                new AdaptiveDualContouring.GenerateLeafVerticesJob
                {
                    Voxels = voxels,
                    Leaves = leaves,
                    LeafCount = leafCount,
                    LeafVertexData = leafVertexData,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    VoxelSpacing = 1.0f
                }.Schedule(1, 1).Complete();

                Assert.That(leafVertexData[0].Active, Is.True);
                Assert.That(leafVertexData[0].Vertex.Position.x, Is.EqualTo(-0.5f).Within(0.001f));
                Assert.That(leafVertexData[0].Vertex.Position.y, Is.EqualTo(0.0f).Within(0.001f));
                Assert.That(leafVertexData[0].Vertex.Position.z, Is.EqualTo(0.0f).Within(0.001f));
            }
            finally
            {
                leafVertexData.Dispose();
                leafCount.Dispose();
                leaves.Dispose();
                voxels.Dispose();
            }
        }

        [TestCase(0, false, new int[] { 0, 3, 2, 0, 1, 3 })]
        [TestCase(0, true, new int[] { 0, 2, 3, 0, 3, 1 })]
        [TestCase(1, false, new int[] { 0, 3, 1, 0, 2, 3 })]
        [TestCase(1, true, new int[] { 0, 1, 3, 0, 3, 2 })]
        [TestCase(2, false, new int[] { 0, 3, 2, 0, 1, 3 })]
        [TestCase(2, true, new int[] { 0, 2, 3, 0, 3, 1 })]
        public void AdaptivePipeline_PreservesDenseQuadWindingForBoundaryOnlyChunk(int axis, bool reverse, int[] expectedIndices)
        {
            const int numberOfVoxelsAlongAxis = 3;
            const int numberOfCellsAlongAxis = numberOfVoxelsAlongAxis - 1;
            const int voxelCount = numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis;
            const int cellCount = numberOfCellsAlongAxis * numberOfCellsAlongAxis * numberOfCellsAlongAxis;

            NativeArray<PackedVoxel> voxels = new NativeArray<PackedVoxel>(voxelCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.AdaptiveLeafCell> leaves = new NativeArray<AdaptiveDualContouring.AdaptiveLeafCell>(cellCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.LeafVertexData> leafVertexData = new NativeArray<AdaptiveDualContouring.LeafVertexData>(cellCount, Allocator.TempJob);
            NativeArray<int> leafCount = new NativeArray<int>(1, Allocator.TempJob);
            NativeArray<int> denseCellToLeaf = new NativeArray<int>(cellCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.QefDiagnostics> qefDiagnostics = new NativeArray<AdaptiveDualContouring.QefDiagnostics>(1, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.LeafBuildDiagnostics> leafBuildDiagnostics = new NativeArray<AdaptiveDualContouring.LeafBuildDiagnostics>(1, Allocator.TempJob);
            NativeList<GPUVertex> vertices = new NativeList<GPUVertex>(cellCount, Allocator.TempJob);
            NativeList<int> indices = new NativeList<int>(18, Allocator.TempJob);

            try
            {
                FillPlaneVoxels(voxels, numberOfVoxelsAlongAxis, axis, reverse, 0.5f);

                JobHandle buildHandle = new AdaptiveDualContouring.BuildLeavesJob
                {
                    Voxels = voxels,
                    Leaves = leaves,
                    LeafCount = leafCount,
                    Diagnostics = leafBuildDiagnostics,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    BuildMode = AdaptiveDualContouring.AdaptiveLeafBuildMode.ToyPattern,
                    Size2LeafErrorThreshold = 0.01f
                }.Schedule();
                JobHandle fillHandle = new AdaptiveDualContouring.FillDenseCellToLeafJob
                {
                    Leaves = leaves,
                    LeafCount = leafCount,
                    DenseCellToLeaf = denseCellToLeaf,
                    Diagnostics = leafBuildDiagnostics,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis
                }.Schedule(buildHandle);
                JobHandle leafVerticesHandle = new AdaptiveDualContouring.GenerateLeafVerticesJob
                {
                    Voxels = voxels,
                    Leaves = leaves,
                    LeafCount = leafCount,
                    LeafVertexData = leafVertexData,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    VoxelSpacing = 1.0f
                }.Schedule(cellCount, 1, fillHandle);
                JobHandle compactHandle = new AdaptiveDualContouring.CompactLeafVerticesJob
                {
                    Leaves = leaves,
                    LeafCount = leafCount,
                    LeafVertexData = leafVertexData,
                    Vertices = vertices,
                    QefDiagnostics = qefDiagnostics
                }.Schedule(leafVerticesHandle);
                new AdaptiveDualContouring.AdaptiveEdgeTriangulationJob
                {
                    Voxels = voxels,
                    Leaves = leaves,
                    DenseCellToLeaf = denseCellToLeaf,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    Indices = indices
                }.Schedule(compactHandle).Complete();

                Assert.That(leafBuildDiagnostics[0].Size2LeafCount, Is.EqualTo(0));
                Assert.That(vertices.Length, Is.EqualTo(4));
                Assert.That(indices.Length, Is.EqualTo(6));
                Assert.That(indices.AsArray().ToArray(), Is.EqualTo(expectedIndices));
                Assert.That(qefDiagnostics[0].Total, Is.EqualTo(4));
            }
            finally
            {
                indices.Dispose();
                vertices.Dispose();
                leafBuildDiagnostics.Dispose();
                qefDiagnostics.Dispose();
                denseCellToLeaf.Dispose();
                leafCount.Dispose();
                leafVertexData.Dispose();
                leaves.Dispose();
                voxels.Dispose();
            }
        }

        [Test]
        public void AdaptiveTriangulation_EmitsSingleTriangleForThreeUniqueVertices()
        {
            const int numberOfVoxelsAlongAxis = 3;
            const int numberOfCellsAlongAxis = numberOfVoxelsAlongAxis - 1;
            const int voxelCount = numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis;
            const int cellCount = numberOfCellsAlongAxis * numberOfCellsAlongAxis * numberOfCellsAlongAxis;

            NativeArray<PackedVoxel> voxels = new NativeArray<PackedVoxel>(voxelCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.AdaptiveLeafCell> leaves = new NativeArray<AdaptiveDualContouring.AdaptiveLeafCell>(3, Allocator.TempJob);
            NativeArray<int> denseCellToLeaf = new NativeArray<int>(cellCount, Allocator.TempJob);
            NativeList<int> indices = new NativeList<int>(18, Allocator.TempJob);

            try
            {
                FillPlaneVoxels(voxels, numberOfVoxelsAlongAxis, 0, false, 0.5f);

                for (int index = 0; index < denseCellToLeaf.Length; index++)
                {
                    denseCellToLeaf[index] = -1;
                }

                leaves[0] = new AdaptiveDualContouring.AdaptiveLeafCell { VertexIndex = 0, Active = true };
                leaves[1] = new AdaptiveDualContouring.AdaptiveLeafCell { VertexIndex = 1, Active = true };
                leaves[2] = new AdaptiveDualContouring.AdaptiveLeafCell { VertexIndex = 2, Active = true };

                denseCellToLeaf[AdaptiveDualContouring.FlattenIndex(new int3(0, 0, 0), numberOfCellsAlongAxis)] = 0;
                denseCellToLeaf[AdaptiveDualContouring.FlattenIndex(new int3(0, 1, 0), numberOfCellsAlongAxis)] = 1;
                denseCellToLeaf[AdaptiveDualContouring.FlattenIndex(new int3(0, 0, 1), numberOfCellsAlongAxis)] = 2;
                denseCellToLeaf[AdaptiveDualContouring.FlattenIndex(new int3(0, 1, 1), numberOfCellsAlongAxis)] = 2;

                new AdaptiveDualContouring.AdaptiveEdgeTriangulationJob
                {
                    Voxels = voxels,
                    Leaves = leaves,
                    DenseCellToLeaf = denseCellToLeaf,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    Indices = indices
                }.Schedule().Complete();

                Assert.That(indices.Length, Is.EqualTo(3));
                Assert.That(indices.AsArray().ToArray(), Is.EqualTo(new[] { 0, 1, 2 }));
            }
            finally
            {
                indices.Dispose();
                denseCellToLeaf.Dispose();
                leaves.Dispose();
                voxels.Dispose();
            }
        }

        [Test]
        public void AdaptiveTriangulation_UsesOrderedUniqueVerticesForTransitionPatch()
        {
            const int numberOfVoxelsAlongAxis = 3;
            const int numberOfCellsAlongAxis = numberOfVoxelsAlongAxis - 1;
            const int voxelCount = numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis;
            const int cellCount = numberOfCellsAlongAxis * numberOfCellsAlongAxis * numberOfCellsAlongAxis;

            NativeArray<PackedVoxel> voxels = new NativeArray<PackedVoxel>(voxelCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.AdaptiveLeafCell> leaves = new NativeArray<AdaptiveDualContouring.AdaptiveLeafCell>(3, Allocator.TempJob);
            NativeArray<int> denseCellToLeaf = new NativeArray<int>(cellCount, Allocator.TempJob);
            NativeList<int> indices = new NativeList<int>(18, Allocator.TempJob);

            try
            {
                FillPlaneVoxels(voxels, numberOfVoxelsAlongAxis, 0, false, 0.5f);

                for (int index = 0; index < denseCellToLeaf.Length; index++)
                {
                    denseCellToLeaf[index] = -1;
                }

                leaves[0] = new AdaptiveDualContouring.AdaptiveLeafCell { VertexIndex = 0, Active = true };
                leaves[1] = new AdaptiveDualContouring.AdaptiveLeafCell { VertexIndex = 1, Active = true };
                leaves[2] = new AdaptiveDualContouring.AdaptiveLeafCell { VertexIndex = 2, Active = true };

                denseCellToLeaf[AdaptiveDualContouring.FlattenIndex(new int3(0, 0, 0), numberOfCellsAlongAxis)] = 0;
                denseCellToLeaf[AdaptiveDualContouring.FlattenIndex(new int3(0, 1, 0), numberOfCellsAlongAxis)] = 1;
                denseCellToLeaf[AdaptiveDualContouring.FlattenIndex(new int3(0, 0, 1), numberOfCellsAlongAxis)] = 1;
                denseCellToLeaf[AdaptiveDualContouring.FlattenIndex(new int3(0, 1, 1), numberOfCellsAlongAxis)] = 2;

                new AdaptiveDualContouring.AdaptiveEdgeTriangulationJob
                {
                    Voxels = voxels,
                    Leaves = leaves,
                    DenseCellToLeaf = denseCellToLeaf,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    Indices = indices
                }.Schedule().Complete();

                Assert.That(indices.Length, Is.EqualTo(3));
                Assert.That(indices.AsArray().ToArray(), Is.EqualTo(new[] { 0, 1, 2 }));
            }
            finally
            {
                indices.Dispose();
                denseCellToLeaf.Dispose();
                leaves.Dispose();
                voxels.Dispose();
            }
        }

        [Test]
        public void AdaptiveTriangulation_SkipsPatchWithFewerThanThreeUniqueVertices()
        {
            const int numberOfVoxelsAlongAxis = 3;
            const int numberOfCellsAlongAxis = numberOfVoxelsAlongAxis - 1;
            const int voxelCount = numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis;
            const int cellCount = numberOfCellsAlongAxis * numberOfCellsAlongAxis * numberOfCellsAlongAxis;

            NativeArray<PackedVoxel> voxels = new NativeArray<PackedVoxel>(voxelCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.AdaptiveLeafCell> leaves = new NativeArray<AdaptiveDualContouring.AdaptiveLeafCell>(2, Allocator.TempJob);
            NativeArray<int> denseCellToLeaf = new NativeArray<int>(cellCount, Allocator.TempJob);
            NativeList<int> indices = new NativeList<int>(18, Allocator.TempJob);

            try
            {
                FillPlaneVoxels(voxels, numberOfVoxelsAlongAxis, 0, false, 0.5f);

                for (int index = 0; index < denseCellToLeaf.Length; index++)
                {
                    denseCellToLeaf[index] = -1;
                }

                leaves[0] = new AdaptiveDualContouring.AdaptiveLeafCell { VertexIndex = 0, Active = true };
                leaves[1] = new AdaptiveDualContouring.AdaptiveLeafCell { VertexIndex = 1, Active = true };

                denseCellToLeaf[AdaptiveDualContouring.FlattenIndex(new int3(0, 0, 0), numberOfCellsAlongAxis)] = 0;
                denseCellToLeaf[AdaptiveDualContouring.FlattenIndex(new int3(0, 1, 0), numberOfCellsAlongAxis)] = 0;
                denseCellToLeaf[AdaptiveDualContouring.FlattenIndex(new int3(0, 0, 1), numberOfCellsAlongAxis)] = 1;
                denseCellToLeaf[AdaptiveDualContouring.FlattenIndex(new int3(0, 1, 1), numberOfCellsAlongAxis)] = 1;

                new AdaptiveDualContouring.AdaptiveEdgeTriangulationJob
                {
                    Voxels = voxels,
                    Leaves = leaves,
                    DenseCellToLeaf = denseCellToLeaf,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    Indices = indices
                }.Schedule().Complete();

                Assert.That(indices.Length, Is.EqualTo(0));
            }
            finally
            {
                indices.Dispose();
                denseCellToLeaf.Dispose();
                leaves.Dispose();
                voxels.Dispose();
            }
        }

        [Test]
        public void AdaptivePipeline_ToyPatternProducesMixedLeafTopology()
        {
            const int numberOfVoxelsAlongAxis = 8;
            const int numberOfCellsAlongAxis = numberOfVoxelsAlongAxis - 1;
            const int voxelCount = numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis;
            const int cellCount = numberOfCellsAlongAxis * numberOfCellsAlongAxis * numberOfCellsAlongAxis;

            NativeArray<PackedVoxel> voxels = new NativeArray<PackedVoxel>(voxelCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.AdaptiveLeafCell> leaves = new NativeArray<AdaptiveDualContouring.AdaptiveLeafCell>(cellCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.LeafVertexData> leafVertexData = new NativeArray<AdaptiveDualContouring.LeafVertexData>(cellCount, Allocator.TempJob);
            NativeArray<int> leafCount = new NativeArray<int>(1, Allocator.TempJob);
            NativeArray<int> denseCellToLeaf = new NativeArray<int>(cellCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.QefDiagnostics> qefDiagnostics = new NativeArray<AdaptiveDualContouring.QefDiagnostics>(1, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.LeafBuildDiagnostics> leafBuildDiagnostics = new NativeArray<AdaptiveDualContouring.LeafBuildDiagnostics>(1, Allocator.TempJob);
            NativeList<GPUVertex> vertices = new NativeList<GPUVertex>(cellCount, Allocator.TempJob);
            NativeList<int> indices = new NativeList<int>(cellCount * 6, Allocator.TempJob);

            try
            {
                FillPlaneVoxels(voxels, numberOfVoxelsAlongAxis, 0, false, 2.5f);

                JobHandle buildHandle = new AdaptiveDualContouring.BuildLeavesJob
                {
                    Voxels = voxels,
                    Leaves = leaves,
                    LeafCount = leafCount,
                    Diagnostics = leafBuildDiagnostics,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    BuildMode = AdaptiveDualContouring.AdaptiveLeafBuildMode.ToyPattern,
                    Size2LeafErrorThreshold = 0.01f
                }.Schedule();
                JobHandle fillHandle = new AdaptiveDualContouring.FillDenseCellToLeafJob
                {
                    Leaves = leaves,
                    LeafCount = leafCount,
                    DenseCellToLeaf = denseCellToLeaf,
                    Diagnostics = leafBuildDiagnostics,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis
                }.Schedule(buildHandle);
                JobHandle leafVerticesHandle = new AdaptiveDualContouring.GenerateLeafVerticesJob
                {
                    Voxels = voxels,
                    Leaves = leaves,
                    LeafCount = leafCount,
                    LeafVertexData = leafVertexData,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    VoxelSpacing = 1.0f
                }.Schedule(cellCount, 32, fillHandle);
                JobHandle compactHandle = new AdaptiveDualContouring.CompactLeafVerticesJob
                {
                    Leaves = leaves,
                    LeafCount = leafCount,
                    LeafVertexData = leafVertexData,
                    Vertices = vertices,
                    QefDiagnostics = qefDiagnostics
                }.Schedule(leafVerticesHandle);
                new AdaptiveDualContouring.AdaptiveEdgeTriangulationJob
                {
                    Voxels = voxels,
                    Leaves = leaves,
                    DenseCellToLeaf = denseCellToLeaf,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    Indices = indices
                }.Schedule(compactHandle).Complete();

                Assert.That(leafBuildDiagnostics[0].Size1LeafCount, Is.GreaterThan(0));
                Assert.That(leafBuildDiagnostics[0].Size2LeafCount, Is.GreaterThan(0));
                Assert.That(leafBuildDiagnostics[0].OverlapCount, Is.EqualTo(0));
                Assert.That(leafBuildDiagnostics[0].UnassignedCount, Is.EqualTo(0));
                Assert.That(vertices.Length, Is.GreaterThan(0));
                Assert.That(indices.Length, Is.GreaterThan(0));
            }
            finally
            {
                indices.Dispose();
                vertices.Dispose();
                leafBuildDiagnostics.Dispose();
                qefDiagnostics.Dispose();
                denseCellToLeaf.Dispose();
                leafCount.Dispose();
                leafVertexData.Dispose();
                leaves.Dispose();
                voxels.Dispose();
            }
        }

        private static void FillPlaneVoxels(NativeArray<PackedVoxel> voxels, int numberOfVoxelsAlongAxis, int axis, bool reverse, float isoCrossingCoordinate)
        {
            for (int z = 0; z < numberOfVoxelsAlongAxis; z++)
            {
                for (int y = 0; y < numberOfVoxelsAlongAxis; y++)
                {
                    for (int x = 0; x < numberOfVoxelsAlongAxis; x++)
                    {
                        float coordinate = axis == 0 ? x : axis == 1 ? y : z;
                        float value = reverse ? isoCrossingCoordinate - coordinate : coordinate - isoCrossingCoordinate;
                        voxels[x + numberOfVoxelsAlongAxis * (y + numberOfVoxelsAlongAxis * z)] = PackVoxel(value, MaterialIndex.Rock, new float2(1.0f, 0.0f));
                    }
                }
            }
        }

        private static PackedVoxel PackVoxel(float value, MaterialIndex materialIndex, float2 encodedGradient)
        {
            uint2 gradient = math.f32tof16(encodedGradient);

            return new PackedVoxel
            {
                PackedValueAndMaterialIndex = math.f32tof16(value) | ((uint)materialIndex << 16),
                PackedGradient = gradient.x | gradient.y << 16
            };
        }
    }
}
