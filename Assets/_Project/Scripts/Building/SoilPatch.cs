using UnityEngine;

namespace Vineyard.Building
{
    /// <summary>
    /// Tilled soil under a built grapevine — the same soil texture the village vineyard rows have painted into the
    /// terrain. The prefab holds a flat grid (that is what the build preview shows); once placed, every vertex is
    /// dropped onto the ground just above the terrain, so the patch follows bumps and slopes like paint would.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    public class SoilPatch : MonoBehaviour
    {
        [Tooltip("Height above the ground (m), keeps the patch from flickering into the terrain.")]
        public float lift = 0.025f;

        Mesh conformed;

        void Start()
        {
            var mf = GetComponent<MeshFilter>();
            if (mf.sharedMesh == null) return;
            conformed = Instantiate(mf.sharedMesh);
            conformed.name = mf.sharedMesh.name + " (ground)";
            var v = conformed.vertices;
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 w = transform.TransformPoint(v[i]);
                if (!BuildPlacement.TryGroundHeight(w, out float gy)) continue;
                w.y = gy + lift;
                v[i] = transform.InverseTransformPoint(w);
            }
            conformed.vertices = v;
            conformed.RecalculateNormals();
            conformed.RecalculateBounds();
            mf.sharedMesh = conformed;
        }

        void OnDestroy()
        {
            if (conformed != null) Destroy(conformed);
        }
    }
}
