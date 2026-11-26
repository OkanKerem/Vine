using System.Collections.Generic;
using UnityEngine;

namespace Vineyard.Building
{
    /// <summary>
    /// Shape of a buildable prefab (meshes for the preview, bounds for the footprint) and the placement rules.
    /// The same <see cref="Check"/> runs on the client (preview colour) and on the host (authoritative), so a
    /// white preview is what the host accepts.
    /// </summary>
    public static class BuildPlacement
    {
        /// <summary>Footprint is shrunk a little so neighbours can stand close together.</summary>
        const float FootprintShrink = 0.92f;
        /// <summary>The overlap box starts this far above the ground (small bumps / roots don't block).</summary>
        const float GroundClearance = 0.12f;
        /// <summary>Max height difference between the footprint's corners (m).</summary>
        public const float MaxGroundStep = 0.55f;

        public struct Part
        {
            public Mesh mesh;
            public Matrix4x4 local;   // mesh -> prefab root space
        }

        public class Shape
        {
            public readonly List<Part> parts = new List<Part>();
            /// <summary>Visual bounds in prefab root space (the root sits on the ground).</summary>
            public Bounds bounds;
            /// <summary>
            /// What must be free: the solid colliders of the prefab (falls back to the visual bounds). Flat decoration
            /// such as the soil patch has no collider, so vines can be planted in rows like in the village.
            /// </summary>
            public Bounds footprint;
            /// <summary>Horizontal radius of the footprint (for range checks).</summary>
            public float Radius => new Vector2(bounds.extents.x, bounds.extents.z).magnitude;
        }

        static readonly Dictionary<GameObject, Shape> shapes = new Dictionary<GameObject, Shape>();
        static readonly Collider[] overlapBuffer = new Collider[32];

        /// <summary>Visible meshes and bounds of <paramref name="prefab"/> (cached; read straight from the asset, nothing is instantiated).</summary>
        public static Shape ShapeOf(GameObject prefab)
        {
            if (prefab == null) return null;
            if (shapes.TryGetValue(prefab, out var s)) return s;
            s = new Shape();
            var root = prefab.transform;
            Matrix4x4 toRoot = root.worldToLocalMatrix;
            bool any = false;
            foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!r.enabled || !ActiveUnder(r.transform, root)) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var local = toRoot * r.transform.localToWorldMatrix;
                s.parts.Add(new Part { mesh = mf.sharedMesh, local = local });
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = local.MultiplyPoint3x4(corner);
                    if (!any) { s.bounds = new Bounds(p, Vector3.zero); any = true; }
                    else s.bounds.Encapsulate(p);
                }
            }
            if (!any) s.bounds = new Bounds(Vector3.up * 0.5f, Vector3.one);

            bool anyCol = false;
            foreach (var box in prefab.GetComponentsInChildren<BoxCollider>(true))
            {
                if (box.isTrigger || !box.enabled || !ActiveUnder(box.transform, root)) continue;
                var local = toRoot * box.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var corner = box.center + Vector3.Scale(box.size * 0.5f, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = local.MultiplyPoint3x4(corner);
                    if (!anyCol) { s.footprint = new Bounds(p, Vector3.zero); anyCol = true; }
                    else s.footprint.Encapsulate(p);
                }
            }
            if (!anyCol) s.footprint = s.bounds;
            shapes[prefab] = s;
            return s;
        }

        static bool ActiveUnder(Transform t, Transform root)
        {
            for (; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return false;
                if (t == root) return true;
            }
            return true;
        }

        /// <summary>Ground height under (x, z): the terrain if there is one there, otherwise the first solid surface below.</summary>
        public static bool TryGroundHeight(Vector3 p, out float y)
        {
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 o = t.GetPosition(), size = t.terrainData.size;
                if (p.x < o.x || p.z < o.z || p.x > o.x + size.x || p.z > o.z + size.z) continue;
                y = t.SampleHeight(p) + o.y;
                return true;
            }
            if (Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out var hit, 30f, ~0, QueryTriggerInteraction.Ignore))
            {
                y = hit.point.y;
                return true;
            }
            y = p.y;
            return false;
        }

        /// <summary>
        /// Can <paramref name="shape"/> stand at <paramref name="pos"/> (ground point under its bottom centre)
        /// turned by <paramref name="rot"/>? Returns null if yes, otherwise the reason shown to the player.
        /// </summary>
        public static string Check(Shape shape, Vector3 pos, Quaternion rot)
        {
            var b = shape.footprint;

            // Ground under the footprint must be roughly flat.
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < 5; i++)
            {
                Vector3 local = i == 4 ? new Vector3(b.center.x, 0f, b.center.z)
                                       : new Vector3((i & 1) == 0 ? b.min.x : b.max.x, 0f, (i & 2) == 0 ? b.min.z : b.max.z);
                if (!TryGroundHeight(pos + rot * local, out float gy)) return "No ground here";
                min = Mathf.Min(min, gy); max = Mathf.Max(max, gy);
            }
            if (max - min > MaxGroundStep) return "Ground too steep";

            // Nothing solid inside the footprint (terrain itself and trigger volumes don't count).
            Vector3 half = new Vector3(b.extents.x * FootprintShrink, Mathf.Max(0.05f, (b.size.y - GroundClearance) * 0.5f), b.extents.z * FootprintShrink);
            Vector3 centre = new Vector3(b.center.x, b.min.y + GroundClearance + half.y, b.center.z);
            int n = Physics.OverlapBoxNonAlloc(pos + rot * centre, half, overlapBuffer, rot, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (!(overlapBuffer[i] is TerrainCollider)) return "Blocked — something is in the way";
            return null;
        }
    }
}
