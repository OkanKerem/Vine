using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vineyard.Winery
{
    /// <summary>
    /// Purely visual: a copy of a grape bunch that arcs from where it was to a (possibly moving) target, spinning
    /// a little, then disappears. Used for vine -> bucket and bucket -> press. Never networked: every client
    /// spawns its own copies from the same replicated event.
    /// </summary>
    public class FlyingBunch : MonoBehaviour
    {
        Vector3 from, last;
        Func<Vector3> to;
        float start, duration, arc, endScale;
        Vector3 scale0, spinAxis;
        Quaternion rot0;
        Action onArrive;

        /// <param name="source">Bunch to copy (its current world pose and size are kept).</param>
        /// <param name="target">Evaluated every frame, so the bunch follows a walking player's bucket.</param>
        /// <param name="delay">Seconds the copy waits at the start before flying (staggers several bunches).</param>
        /// <param name="endScale">Size at arrival relative to the start (shrinks into the bucket / hopper).</param>
        /// <param name="onArrive">Called once when the copy reaches the target (also if it is destroyed early).</param>
        public static FlyingBunch Launch(Transform source, Func<Vector3> target, float delay, float duration, float arc, float endScale, Action onArrive = null)
        {
            if (!source) return null;
            var go = Instantiate(source.gameObject);
            go.name = "FlyingBunch (runtime)";
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.forceRenderingOff = false;
            }
            go.transform.SetPositionAndRotation(source.position, source.rotation);
            go.transform.localScale = source.lossyScale;
            go.SetActive(true);

            var f = go.AddComponent<FlyingBunch>();
            f.from = f.last = source.position;
            f.to = target;
            f.start = Time.time + Mathf.Max(0f, delay);
            f.duration = Mathf.Max(0.05f, duration);
            f.arc = arc;
            f.endScale = endScale;
            f.scale0 = go.transform.localScale;
            f.rot0 = go.transform.rotation;
            f.spinAxis = UnityEngine.Random.onUnitSphere;
            f.onArrive = onArrive;
            return f;
        }

        void Update()
        {
            float t = (Time.time - start) / duration;
            if (t < 0f) return;                                     // waiting for its turn
            if (to != null) last = to();
            if (t >= 1f) { Destroy(gameObject); return; }   // OnDestroy reports the arrival

            // Quadratic Bezier with the control point lifted above the middle: a short toss.
            Vector3 mid = (from + last) * 0.5f + Vector3.up * arc;
            float u = 1f - t;
            transform.position = u * u * from + 2f * u * t * mid + t * t * last;
            transform.rotation = Quaternion.AngleAxis(t * 160f, spinAxis) * rot0;
            transform.localScale = scale0 * Mathf.Lerp(1f, endScale, t * t);
        }

        void OnDestroy()
        {
            var cb = onArrive;
            onArrive = null;
            cb?.Invoke();
        }
    }
}
