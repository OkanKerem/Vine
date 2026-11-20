using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// Shows what is inside a bucket model: a rising liquid surface (juice / wine) or a growing pile of grape
    /// bunches. Purely visual — it only reads the replicated <see cref="PlayerBucket"/> state.
    /// Used both for the first-person view bucket and the third-person bucket other players see.
    /// </summary>
    public class BucketVisual : MonoBehaviour
    {
        public PlayerBucket bucket;

        [Header("Contents (children of this bucket)")]
        public Transform liquid;             // cylinder; its local Y scale / position are driven
        public Renderer liquidRenderer;
        public Transform grapeBed;           // flattened purple base under the bunches
        public Transform[] grapeBunches;     // revealed one by one as the amount grows

        [Header("Inner geometry (local to this transform)")]
        public float floorY = 0.02f;
        public float rimY = 0.24f;
        public float liquidMaxFill = 0.88f;  // keep the surface just below the rim

        [Header("Colours")]
        public Color juiceColor = new Color(0.52f, 0.16f, 0.55f, 1f);
        public Color wineColor = new Color(0.46f, 0.02f, 0.07f, 1f);

        [Header("Motion")]
        public bool viewModel;               // first-person bucket: bob while walking
        public float bobAmount = 0.012f;

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock block;
        float shownFill;
        int lastAmount = -1;
        ResourceType lastType, shownType;
        float bounce;
        Vector3 restLocalPos, lastWorldPos;
        Vector3[] bunchScale;
        float bobPhase;

        void Awake()
        {
            restLocalPos = transform.localPosition;
            lastWorldPos = transform.position;
            if (grapeBunches != null)
            {
                bunchScale = new Vector3[grapeBunches.Length];
                for (int i = 0; i < grapeBunches.Length; i++) bunchScale[i] = grapeBunches[i] ? grapeBunches[i].localScale : Vector3.one;
            }
            if (!bucket) bucket = GetComponentInParent<PlayerBucket>();
        }

        void LateUpdate()
        {
            if (!bucket) return;
            var actual = bucket.Type;
            float target = bucket.Fill;
            if (bucket.Amount != lastAmount || actual != lastType)
            {
                if (lastAmount >= 0) bounce = 1f;
                // A different resource starts from the bottom; emptying keeps the old type until it has drained.
                if (actual != ResourceType.Empty && actual != shownType) { shownFill = 0f; shownType = actual; }
                lastAmount = bucket.Amount;
                lastType = actual;
            }
            shownFill = Mathf.MoveTowards(shownFill, target, Time.deltaTime * 1.6f);
            if (actual == ResourceType.Empty && shownFill <= 0.001f) shownType = ResourceType.Empty;
            var type = shownType;

            bool isLiquid = type.IsLiquid();
            bool isGrapes = type == ResourceType.Grapes;

            // Liquid surface.
            if (liquid)
            {
                bool show = isLiquid && shownFill > 0.01f;
                liquid.gameObject.SetActive(show);
                if (show)
                {
                    float h = Mathf.Lerp(0.01f, (rimY - floorY) * liquidMaxFill, shownFill);
                    var s = liquid.localScale; s.y = h * 0.5f; liquid.localScale = s;   // Unity cylinder is 2 units tall
                    var p = liquid.localPosition; p.y = floorY + h * 0.5f; liquid.localPosition = p;
                    if (liquidRenderer)
                    {
                        block ??= new MaterialPropertyBlock();
                        block.SetColor(BaseColor, type == ResourceType.Wine ? wineColor : juiceColor);
                        liquidRenderer.SetPropertyBlock(block);
                    }
                }
            }

            // Grape pile: base rises, bunches appear one by one.
            float grapeFill = isGrapes ? shownFill : 0f;
            if (grapeBed)
            {
                grapeBed.gameObject.SetActive(grapeFill > 0.01f);
                var p = grapeBed.localPosition;
                p.y = Mathf.Lerp(floorY, floorY + (rimY - floorY) * 0.55f, grapeFill);
                grapeBed.localPosition = p;
            }
            if (grapeBunches != null)
                for (int i = 0; i < grapeBunches.Length; i++)
                {
                    if (!grapeBunches[i]) continue;
                    float threshold = (i + 0.5f) / grapeBunches.Length;
                    float k = Mathf.Clamp01((grapeFill - threshold) * grapeBunches.Length + 1f);
                    grapeBunches[i].gameObject.SetActive(k > 0.02f);
                    grapeBunches[i].localScale = bunchScale[i] * Mathf.SmoothStep(0f, 1f, k);
                }

            // A little bounce when the contents change; bob while walking for the first-person bucket.
            bounce = Mathf.MoveTowards(bounce, 0f, Time.deltaTime * 3f);
            Vector3 offset = Vector3.up * (Mathf.Sin(bounce * Mathf.PI * 2f) * 0.02f * bounce);
            if (viewModel)
            {
                Vector3 v = (transform.position - lastWorldPos) / Mathf.Max(Time.deltaTime, 1e-4f);
                v.y = 0f;
                float speed = Mathf.Clamp01(v.magnitude / 5f);
                bobPhase += Time.deltaTime * (6f + speed * 5f);
                offset += new Vector3(Mathf.Cos(bobPhase * 0.5f) * bobAmount, Mathf.Abs(Mathf.Sin(bobPhase * 0.5f)) * bobAmount, 0f) * speed;
                if (liquid && liquid.gameObject.activeSelf)
                    liquid.localRotation = Quaternion.Euler(Mathf.Sin(bobPhase) * 3f * speed, 0f, Mathf.Cos(bobPhase * 0.7f) * 3f * speed);
            }
            transform.localPosition = restLocalPos + offset;
            lastWorldPos = transform.position;
        }
    }
}
