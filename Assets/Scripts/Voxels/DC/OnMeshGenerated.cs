using Unity.Collections;
using UnityEngine;

namespace Tuntenfisch.Voxels.DC
{
    // TODO: This result still mixes CPU mesh data and GPU resources as an incremental transport step.
    // Consider splitting it into mesh data and render-resource data if the payload grows further.
    public delegate void OnMeshGenerated(MeshGenerationResult result);
}
