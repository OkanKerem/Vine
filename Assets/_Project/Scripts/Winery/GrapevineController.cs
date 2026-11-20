using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// One grapevine: its tuning values (read by the host) and its visuals.
    /// The grape count itself is host-authoritative and lives in <see cref="VineyardController"/>.
    /// Bunches are the GrapeBunch instances under the model's GrapeSpawn points: they shrink away when
    /// harvested and grow back one by one as the host regrows grapes.
    /// </summary>
    public class GrapevineController : MonoBehaviour
    {
        [Header("Tuning (upgrade-ready)")]
        [Min(1)] public int maxGrapes = 8;
        [Tooltip("Seconds to regrow one grape unit.")]
        [Min(0.1f)] public float regrowSeconds = 20f;
        [Tooltip("Minimum seconds between two harvests of this vine (harvest speed).")]
        [Min(0f)] public float harvestCooldown = 0.25f;

        [Header("Visuals")]
        public Transform[] bunches;
        public Transform foliage;            // wobbles when harvested

        Vector3[] bunchScale;
        float[] bunchShown;
        int[] order;
        float wobble;
        Quaternion foliageRest;

        void Awake()
        {
            if (bunches == null) bunches = new Transform[0];
            bunchScale = new Vector3[bunches.Length];
            bunchShown = new float[bunches.Length];
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

        /// <summary>Called every frame by the vineyard with the replicated grape count.</summary>
        public void ShowGrapes(int available)
        {
            int n = bunches.Length;
            if (n == 0) return;
            // How many bunches stand for 'available' grapes (any grapes left = at least one bunch).
            int visible = available <= 0 ? 0 : Mathf.Clamp(Mathf.CeilToInt(available / (float)maxGrapes * n), 1, n);
            for (int k = 0; k < n; k++)
            {
                int i = order[k];
                if (!bunches[i]) continue;
                float target = k < visible ? 1f : 0f;
                float speed = target > bunchShown[i] ? 1.2f : 4f;       // grow slowly, pick quickly
                bunchShown[i] = Mathf.MoveTowards(bunchShown[i], target, Time.deltaTime * speed);
                float s = bunchShown[i];
                // Overshoot a little when growing back ("pop").
                float pop = s < 1f && target > 0f ? 1f + Mathf.Sin(s * Mathf.PI) * 0.15f : 1f;
                bunches[i].localScale = bunchScale[i] * (Mathf.SmoothStep(0f, 1f, s) * pop);
                bool on = s > 0.01f;
                if (bunches[i].gameObject.activeSelf != on) bunches[i].gameObject.SetActive(on);
            }

            if (foliage)
            {
                wobble = Mathf.MoveTowards(wobble, 0f, Time.deltaTime * 1.8f);
                float a = Mathf.Sin(wobble * 18f) * 4f * wobble;
                foliage.localRotation = foliageRest * Quaternion.Euler(a * 0.4f, 0f, a);
            }
        }

        public void PlayHarvestFeedback() => wobble = 1f;
    }
}
