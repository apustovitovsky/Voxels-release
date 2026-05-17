using Unity.Burst;
using Unity.Jobs;
using UnityEngine;

namespace Tuntenfisch.Generics
{
    [BurstCompile]
    public readonly struct BakeJob : IJob
    {
        private readonly EntityId m_meshID;
        private readonly bool m_convex;

        public BakeJob(EntityId entityId, bool convex = false)
        {
            m_meshID = entityId;
            m_convex = convex;
        }

        public void Execute()
        {
            Physics.BakeMesh(m_meshID, m_convex);
        }
    }
}