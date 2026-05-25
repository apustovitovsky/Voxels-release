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

        [Test]
        public void PackedVoxel_DecodesOctahedralGradient()
        {
            PackedVoxel voxel = new PackedVoxel
            {
                PackedGradient = math.f32tof16(new float2(0.0f, 0.0f)).x |
                    (math.f32tof16(new float2(0.0f, 0.0f)).y << 16)
            };

            Assert.That(voxel.Gradient.x, Is.EqualTo(0.0f).Within(0.001f));
            Assert.That(voxel.Gradient.y, Is.EqualTo(0.0f).Within(0.001f));
            Assert.That(voxel.Gradient.z, Is.EqualTo(1.0f).Within(0.001f));
        }

        [Test]
        public void QefData_SolvesIntersectionOfAxisPlanes()
        {
            QefData qef = default;
            qef.Add(new float3(0.25f, 0.0f, 0.0f), new float3(1.0f, 0.0f, 0.0f));
            qef.Add(new float3(0.0f, 0.5f, 0.0f), new float3(0.0f, 1.0f, 0.0f));
            qef.Add(new float3(0.0f, 0.0f, 0.75f), new float3(0.0f, 0.0f, 1.0f));

            float3 position = qef.Solve(float3.zero, out float error);

            Assert.That(position.x, Is.EqualTo(0.25f).Within(0.001f));
            Assert.That(position.y, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(position.z, Is.EqualTo(0.75f).Within(0.001f));
            Assert.That(error, Is.EqualTo(0.0f).Within(0.001f));
        }

        [Test]
        public void QefData_UsesFallbackForSingularInput()
        {
            QefData qef = default;
            qef.Add(new float3(0.25f, 0.0f, 0.0f), new float3(1.0f, 0.0f, 0.0f));
            qef.Add(new float3(0.25f, 1.0f, 0.0f), new float3(1.0f, 0.0f, 0.0f));
            float3 fallback = new float3(0.25f, 0.5f, 0.5f);

            float3 position = qef.Solve(fallback, out _);

            Assert.That(position, Is.EqualTo(fallback));
        }

        [Test]
        public void DensePipeline_GeneratesDeterministicQuadForPlane()
        {
            const int numberOfVoxelsAlongAxis = 3;
            const int numberOfCellsAlongAxis = numberOfVoxelsAlongAxis - 1;
            const int voxelCount = numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis * numberOfVoxelsAlongAxis;
            const int cellCount = numberOfCellsAlongAxis * numberOfCellsAlongAxis * numberOfCellsAlongAxis;

            NativeArray<PackedVoxel> voxels = new NativeArray<PackedVoxel>(voxelCount, Allocator.TempJob);
            NativeArray<AdaptiveDualContouring.CellVertex> vertexPerCell = new NativeArray<AdaptiveDualContouring.CellVertex>(cellCount, Allocator.TempJob);
            NativeArray<int> cellToVertexIndex = new NativeArray<int>(cellCount, Allocator.TempJob);
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
                            voxels[x + numberOfVoxelsAlongAxis * (y + numberOfVoxelsAlongAxis * z)] = PackVoxel(x - 0.5f, MaterialIndex.Rock, new float2(1.0f, 0.0f));
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
                    Vertices = vertices
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

                for (int index = 0; index < indices.Length; index++)
                {
                    Assert.That(indices[index], Is.InRange(0, vertices.Length - 1));
                }
            }
            finally
            {
                indices.Dispose();
                vertices.Dispose();
                cellToVertexIndex.Dispose();
                vertexPerCell.Dispose();
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
