using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// One grapevine: its tuning values (read by the host) and its visuals.
    /// The grape count itself is host-authoritative and lives in <see cref="VineyardController"/>.
    /// Bunches are the GrapeBunch instances under the model's GrapeSpawn points. One grape = one bunch:
    /// harvested bunches fly into the harvesting player's bucket, and they grow back one by one as the host
    /// regrows grapes.
    /// </summary>
    public class GrapevineController : MonoBehaviour
    {
        [Header("Tuning (upgrade-ready)")]
        [Min(1)] public int maxGrapes = 5;
        [Tooltip("One grape per visible bunch: maxGrapes follows the number of bunches on the model.")]
        public bool oneGrapePerBunch = true;
        [Tooltip("Seconds to regrow one grape unit.")]
        [Min(0.1f)] public float regrowSeconds = 20f;
        [Tooltip("Minimum seconds between two harvests of this vine (harvest speed).")]
        [Min(0f)] public float harvestCooldown = 0.25f;

        [Header("Visuals")]
        public Transform[] bunches;
        public Transform foliage;            // wobbles when harvested

        [Header("Harvest flight (vine -> bucket)")]
        public float flightSeconds = 0.32f;
        public float flightStagger = 0.04f;
        public float flightArc = 0.4f;

        Vector3[] bunchScale;
        float[] bunchShown;
        bool[] bunchApplied;                 // transform already matches bunchShown (skip redundant writes)
        int[] order;
        float wobble;
        bool foliageAtRest;
        Quaternion foliageRest;
        // Just-harvested bunches must not grow back while the replicated count is still on its way.
        int capAvailable;
        float capUntil;

        void Awake()
        {
            if (bunches == null) bunches = new Transform[0];
            if (oneGrapePerBunch && bunches.Length > 0) maxGrapes = bunches.Length;   // runs before the host reads it
            bunchScale = new Vector3[bunches.Length];
            bunchShown = new float[bunches.Length];
            bunchApplied = new bool[bunches.Length];
            order = new int[bunches.Length];
            for (int i = 0; i < bunches.Length; i++)
            {
                bunchScale[i] = bunches[i] ? bunches[i].localScale : Vector3.one;
                bunchShown[i] = 1f;
                order[i] = i;
            }
            // Deterministic per-vine order so bunches disappear in a natural, non-sequential pattern.
            var r = new System.Random(name.GetHashCode());
            for (int i = order.Length - 1; i > 0; i--) { int j = r.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
            if (foliage) foliageRest = foliage.localRotation;
        }

        /// <summary>How many bunches stand for <paramref name="available"/> grapes (any grapes left = at least one bunch).</summary>
        int Visible(int available)
        {
            int n = bunches.Length;
            return available <= 0 || n == 0 ? 0 : Mathf.Clamp(Mathf.CeilToInt(available / (float)maxGrapes * n), 1, n);
        }

        /// <summary>
        /// The replicated count, but never more than what this client already saw picked (the new count may still
        /// be on its way after a harvest, or after a locally predicted one).
        /// </summary>
        public int Shown(int available) => Time.time < capUntil ? Mathf.Min(available, capAvailable) : available;

        /// <summary>Called every frame by the vineyard with the replicated grape count.</summary>
        public void ShowGrapes(int available)
        {
            int n = bunches.Length;
            if (n == 0) return;
            available = Shown(available);
            int visible = Visible(available);
            for (int k = 0; k < n; k++)
            {
                int i = order[k];
                if (!bunches[i]) continue;
                float target = k < visible ? 1f : 0f;
                float speed = target > bunchShown[i] ? 1.2f : 4f;       // grow slowly, pick quickly
                float s = Mathf.MoveTowards(bunchShown[i], target, Time.deltaTime * speed);
                // Settled bunches (the usual case) need no transform write: avoids ~100 transform changes per frame.
                if (s == bunchShown[i] && bunchApplied[i]) continue;
                bunchShown[i] = s;
                bunchApplied[i] = true;
                // Overshoot a little when growing back ("pop").
                float pop = s < 1f && target > 0f ? 1f + Mathf.Sin(s * Mathf.PI) * 0.15f : 1f;
                bunches[i].localScale = bunchScale[i] * (Mathf.SmoothStep(0f, 1f, s) * pop);
                bool on = s > 0.01f;
                if (bunches[i].gameObject.activeSelf != on) bunches[i].gameObject.SetActive(on);
            }

            if (foliage && !(wobble <= 0f && foliageAtRest))
            {
                wobble = Mathf.MoveTowards(wobble, 0f, Time.deltaTime * 1.8f);
                foliageAtRest = wobble <= 0f;
                float a = Mathf.Sin(wobble * 18f) * 4f * wobble;
                foliage.localRotation = foliageRest * Quaternion.Euler(a * 0.4f, 0f, a);
            }
        }

        public void PlayHarvestFeedback() { wobble = 1f; foliageAtRest = false; }

        /// <summary>
        /// Harvest went from <paramref name="before"/> to <paramref name="after"/> grapes: the picked bunches leave
        /// the vine and fly into <paramref name="into"/>'s bucket (the bucket fills when they land).
        /// </summary>
        public void PlayHarvest(int before, int after, PlayerBucket into)
        {
            PlayHarvestFeedback();
            capAvailable = after;
            capUntil = Time.time + 2f;
            var vis = into ? into.ActiveVisual : null;

            var leaving = new System.Collections.Generic.List<Transform>();
            for (int k = Visible(after); k < Visible(before) && k < order.Length; k++)
            {
                int i = order[k];
                var b = bunches[i];
                if (!b || !b.gameObject.activeInHierarchy || bunchShown[i] < 0.3f) continue;
                leaving.Add(b);
                // The original leaves the vine at once (the copy flies); it regrows later through ShowGrapes.
                bunchShown[i] = 0f;
                bunchApplied[i] = true;
            }

            int units = before - after;
            if (vis && leaving.Count > 0)
            {
                // The bucket grows by each bunch's share of the grapes as that bunch lands.
                vis.ExpectIncoming(units, flightSeconds + flightStagger * leaving.Count + 1f);
                for (int j = 0; j < leaving.Count; j++)
                {
                    int share = units / leaving.Count + (j < units % leaving.Count ? 1 : 0);
                    Vector3 last = vis.MouthWorld;
                    FlyingBunch.Launch(leaving[j], () => vis ? (last = vis.MouthWorld) : last, j * flightStagger, flightSeconds, flightArc, 0.3f,
                                       () => { if (vis) vis.Landed(share); });
                }
            }
            foreach (var b in leaving) b.gameObject.SetActive(false);   // after the copies were made from them
        }
    }
}
