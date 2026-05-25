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

        [TestCase(0, false, new int[] { 0, 3, 2, 0, 1, 3 })]
        [TestCase(0, true, new int[] { 0, 2, 3, 0, 3, 1 })]
        [TestCase(1, false, new int[] { 0, 3, 1, 0, 2, 3 })]
        [TestCase(1, true, new int[] { 0, 1, 3, 0, 3, 2 })]
        [TestCase(2, false, new int[] { 0, 3, 2, 0, 1, 3 })]
        [TestCase(2, true, new int[] { 0, 2, 3, 0, 3, 1 })]
        public void DensePipeline_GeneratesExpectedQuadWindingForAxisPlane(int axis, bool reverse, int[] expectedIndices)
        {
            const int numberOfVoxelsAlongAxis = 3;
            const int numberOfCellsAlongAxis = numberOfVoxelsAlongAxis - 1;
            const int voxelCount = numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis;
            const int cellCount = numberOfCellsAlongAxis * numberOfCellsAlongAxis * numberOfCellsAlongAxis;

            NativeArray<PackedVoxel> voxels = new NativeArray<PackedVoxel>(voxelCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.CellVertex> vertexPerCell = new NativeArray<AdaptiveDualContouring.CellVertex>(cellCount, Allocator.TempJob);
            NativeArray<int> cellToVertexIndex = new NativeArray<int>(cellCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.QefDiagnostics> diagnostics = new NativeArray<AdaptiveDualContouring.QefDiagnostics>(1, Allocator.TempJob);
            NativeList<GPUVertex> vertices = new NativeList<GPUVertex>(cellCount, Allocator.TempJob);
            NativeList<int> indices = new NativeList<int>(18, Allocator.TempJob);

            try
            {
                for (int z = 0; z < numberOfVoxelsAlongAxis; z++)
                {
                    for (int y = 0; y < numberOfVoxelsAlongAxis; y++)
                    {
                        for (int x = 0; x < numberOfVoxelsAlongAxis; x++)
                        {
                            float coordinate = axis == 0 ? x : axis == 1 ? y : z;
                            float value = reverse ? 0.5f - coordinate : coordinate - 0.5f;
                            voxels[x + numberOfVoxelsAlongAxis * (y + numberOfVoxelsAlongAxis * z)] = PackVoxel(value, MaterialIndex.Rock, new float2(1.0f, 0.0f));
                        }
                    }
                }

                JobHandle cellHandle = new AdaptiveDualContouring.GenerateCellVerticesJob
                {
                    Voxels = voxels,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    VoxelSpacing = 1.0f,
                    VertexPerCell = vertexPerCell
                }.Schedule(cellCount, 1);
                JobHandle compactHandle = new AdaptiveDualContouring.CompactVerticesJob
                {
                    VertexPerCell = vertexPerCell,
                    CellToVertexIndex = cellToVertexIndex,
                    Vertices = vertices,
                    Diagnostics = diagnostics
                }.Schedule(cellHandle);
                new AdaptiveDualContouring.GenerateTrianglesJob
                {
                    Voxels = voxels,
                    CellToVertexIndex = cellToVertexIndex,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    Indices = indices
                }.Schedule(compactHandle).Complete();

                Assert.That(vertices.Length, Is.EqualTo(4));
                Assert.That(indices.Length, Is.EqualTo(6));
                Assert.That(indices.AsArray().ToArray(), Is.EqualTo(expectedIndices));
                Assert.That(diagnostics[0].Total, Is.EqualTo(4));

                for (int index = 0; index < indices.Length; index++)
                {
                    Assert.That(indices[index], Is.InRange(0, vertices.Length - 1));
                }
            }
            finally
            {
                indices.Dispose();
                vertices.Dispose();
                diagnostics.Dispose();
                cellToVertexIndex.Dispose();
                vertexPerCell.Dispose();
                voxels.Dispose();
            }
        }

        [Test]
        public void DenseTriangulation_SkipsPatchWithDuplicateVertexIndices()
        {
            const int numberOfVoxelsAlongAxis = 3;
            const int numberOfCellsAlongAxis = numberOfVoxelsAlongAxis - 1;
            const int voxelCount = numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis;
            const int cellCount = numberOfCellsAlongAxis * numberOfCellsAlongAxis * numberOfCellsAlongAxis;

            NativeArray<PackedVoxel> voxels = new NativeArray<PackedVoxel>(voxelCount, Allocator.TempJob);
            NativeArray<int> cellToVertexIndex = new NativeArray<int>(cellCount, Allocator.TempJob);
            NativeList<int> indices = new NativeList<int>(18, Allocator.TempJob);

            try
            {
                for (int z = 0; z < numberOfVoxelsAlongAxis; z++)
                {
                    for (int y = 0; y < numberOfVoxelsAlongAxis; y++)
                    {
                        for (int x = 0; x < numberOfVoxelsAlongAxis; x++)
                        {
                            voxels[x + numberOfVoxelsAlongAxis * (y + numberOfVoxelsAlongAxis * z)] = PackVoxel(x - 0.5f, MaterialIndex.Rock, new float2(1.0f, 0.0f));
                        }
                    }
                }

                for (int index = 0; index < cellCount; index++)
                {
                    cellToVertexIndex[index] = -1;
                }

                cellToVertexIndex[0] = 0;
                cellToVertexIndex[2] = 1;
                cellToVertexIndex[4] = 2;
                cellToVertexIndex[6] = 2;

                new AdaptiveDualContouring.GenerateTrianglesJob
                {
                    Voxels = voxels,
                    CellToVertexIndex = cellToVertexIndex,
                    NumberOfVoxelsAlongAxis = numberOfVoxelsAlongAxis,
                    NumberOfCellsAlongAxis = numberOfCellsAlongAxis,
                    Indices = indices
                }.Schedule().Complete();

                Assert.That(indices.Length, Is.EqualTo(0));
            }
            finally
            {
                indices.Dispose();
                cellToVertexIndex.Dispose();
                voxels.Dispose();
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
