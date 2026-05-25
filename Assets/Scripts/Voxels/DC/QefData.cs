using Unity.Mathematics;

namespace Tuntenfisch.Voxels.DC
{
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

        public readonly float3 Solve(float3 fallback, out float error)
        {
            const float determinantEpsilon = 1e-6f;

            float determinant = math.determinant(Ata);
            float3 position = fallback;

            if (math.isfinite(determinant) && math.abs(determinant) > determinantEpsilon)
            {
                float3 candidate = math.mul(math.inverse(Ata), Atb);

                if (math.all(math.isfinite(candidate)))
                {
                    position = candidate;
                }
            }

            error = EvaluateError(position);

            return position;
        }

        public readonly float EvaluateError(float3 position)
        {
            float error = math.dot(position, math.mul(Ata, position)) - 2.0f * math.dot(position, Atb) + Btb;

            return math.max(error, 0.0f);
        }
    }
}
