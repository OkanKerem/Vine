using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// Marks a clickable part of a station (a vine, the press handle, the bottle funnel...).
    /// Lives on the visual object that owns the collider; the networked state lives on <see cref="station"/>.
    /// </summary>
    public class WineryInteractable : MonoBehaviour
    {
        public WineryStation station;
        public InteractionPart part;
        [Tooltip("Index inside the station (e.g. which grapevine).")]
        public int index;
        [Tooltip("Renderers tinted while the local player aims at this part.")]
        public Renderer[] highlightRenderers;
        [Tooltip("Colliders used for the host's distance check (auto-filled if empty).")]
        public Collider[] colliders;

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock block;
        bool highlighted;

        void Awake()
        {
            if (colliders == null || colliders.Length == 0) colliders = GetComponentsInChildren<Collider>(true);
        }

        /// <summary>World point used for floating feedback text.</summary>
        public Vector3 FeedbackPoint
        {
            get
            {
                if (colliders != null && colliders.Length > 0 && colliders[0]) return colliders[0].bounds.center + Vector3.up * 0.4f;
                return transform.position + Vector3.up;
            }
        }

        /// <summary>Distance from a point to this part (axis-aligned bounds, works for any collider type).</summary>
        public float DistanceFrom(Vector3 p)
        {
            float best = float.MaxValue;
            if (colliders != null)
                foreach (var c in colliders)
                    if (c) best = Mathf.Min(best, Vector3.Distance(p, c.bounds.ClosestPoint(p)));
            if (best == float.MaxValue) best = Vector3.Distance(p, transform.position);
            return best;
        }

        public void SetHighlighted(bool on)
        {
            if (highlighted == on || highlightRenderers == null) return;
            highlighted = on;
            block ??= new MaterialPropertyBlock();
            foreach (var r in highlightRenderers)
            {
                if (!r) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (!on || mats[i] == null || !mats[i].HasProperty(BaseColor))
                    {
                        r.SetPropertyBlock(null, i);
                        continue;
                    }
                    Color c = mats[i].GetColor(BaseColor);
                    Color lit = Color.Lerp(c * 1.3f, new Color(1f, 0.95f, 0.8f, c.a), 0.12f);
                    lit.a = c.a;
                    block.Clear();
                    block.SetColor(BaseColor, lit);
                    r.SetPropertyBlock(block, i);
                }
            }
        }

        void OnDisable() => SetHighlighted(false);
    }
}
