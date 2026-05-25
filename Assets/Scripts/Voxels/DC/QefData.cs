using Unity.Mathematics;

namespace Tuntenfisch.Voxels.DC
{
    public enum QefPlacementResult : byte
    {
        None = 0,
        Accepted = 1,
        OutsideFallback = 2,
        SingularFallback = 3
    }

    public struct QefData
    {
        public float3x3 Ata;
        public float3 Atb;
        public float Btb;
        public int Count;

        public void Add(float3 position, float3 normal)
        {
            float distance = math.dot(normal, position);

            Ata += new float3x3(normal * normal.x, normal * normal.y, normal * normal.z);
            Atb += normal * distance;
            Btb += distance * distance;
            Count++;
        }

        public readonly bool TrySolveInsideUnitCell(float3 fallback, out float3 position, out float error, out QefPlacementResult result)
        {
            const float bias = 1e-4f;
            const float regularizedDeterminantEpsilon = 1e-12f;

            float3x3 ata = Ata;
            float3 atb = Atb;
            ata.c0.x += bias;
            ata.c1.y += bias;
            ata.c2.z += bias;
            atb += bias * fallback;

            float determinant = math.determinant(ata);

            if (math.isfinite(determinant) && math.abs(determinant) > regularizedDeterminantEpsilon)
            {
                float3 candidate = math.mul(math.inverse(ata), atb);

                if (math.all(math.isfinite(candidate)))
                {
                    if (math.all(candidate >= float3.zero) && math.all(candidate <= new float3(1.0f)))
                    {
                        position = candidate;
                        error = EvaluateError(position);
                        result = QefPlacementResult.Accepted;

                        return true;
                    }

                    position = fallback;
                    error = EvaluateError(position);
                    result = QefPlacementResult.OutsideFallback;

                    return false;
                }
            }

            position = fallback;
            error = EvaluateError(position);
            result = QefPlacementResult.SingularFallback;

            return false;
        }

        public readonly float EvaluateError(float3 position)
        {
            float error = math.dot(position, math.mul(Ata, position)) - 2.0f * math.dot(position, Atb) + Btb;

            return math.max(error, 0.0f);
        }
    }
}
