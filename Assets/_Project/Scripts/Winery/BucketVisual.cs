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
        [Tooltip("Carried bucket. Leave empty to use the IBucketContents found on a parent (ground bucket).")]
        public PlayerBucket bucket;
        IBucketContents src;

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

        [Header("Pour out (G)")]
        [Tooltip("Max forward tip angle while pouring (degrees).")]
        public float pourAngle = 115f;
        [Tooltip("How far the bucket is pushed forward / up while tipping (local units).")]
        public Vector3 pourReach = new Vector3(0f, 0.06f, 0.12f);

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock block;
        float shownFill;
        int lastAmount = -1;
        ResourceType lastType, shownType;
        float bounce;
        Vector3 restLocalPos, lastWorldPos;
        Quaternion restLocalRot;
        Vector2 liquidXZ = Vector2.one;
        ParticleSystem pourFx;
        ParticleSystemRenderer pourFxRenderer;
        Material pourFxMat;
        Vector3[] bunchScale;
        float bobPhase;

        void Awake()
        {
            restLocalPos = transform.localPosition;
            restLocalRot = transform.localRotation;
            if (liquid) liquidXZ = new Vector2(liquid.localScale.x, liquid.localScale.z);
            lastWorldPos = transform.position;
            if (grapeBunches != null)
            {
                bunchScale = new Vector3[grapeBunches.Length];
                for (int i = 0; i < grapeBunches.Length; i++) bunchScale[i] = grapeBunches[i] ? grapeBunches[i].localScale : Vector3.one;
            }
            if (!bucket) bucket = GetComponentInParent<PlayerBucket>();
            src = bucket ? bucket : GetComponentInParent<IBucketContents>();
            BuildPourFx();
        }

        /// <summary>Runtime emitter on the front rim: liquid drops or grape blobs tumbling to the ground.</summary>
        void BuildPourFx()
        {
            var src = liquidRenderer ? liquidRenderer.sharedMaterial : null;
            if (src == null) return;
            var go = new GameObject("PourFx (runtime)");
            go.transform.SetParent(transform, false);
            float inner = liquid ? liquid.localScale.x * 0.5f : 0.1f;   // read in Awake, before any scaling
            go.transform.localPosition = new Vector3(0f, rimY, inner);
            go.transform.localRotation = Quaternion.identity;
            pourFx = go.AddComponent<ParticleSystem>();
            pourFx.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = pourFx.main;
            main.playOnAwake = false;
            main.loop = true;
            main.startLifetime = 0.9f;
            main.startSpeed = 0.6f;
            main.gravityModifier = 1.4f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;
            var em = pourFx.emission; em.rateOverTime = 90f;
            var sh = pourFx.shape; sh.shapeType = ParticleSystemShapeType.Circle; sh.radius = inner * 0.5f;
            var sol = pourFx.sizeOverLifetime; sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.8f, 0.8f), new Keyframe(1f, 0f)));
            var col = pourFx.collision; col.enabled = true; col.type = ParticleSystemCollisionType.World;
            col.bounce = 0.1f; col.dampen = 0.6f; col.lifetimeLoss = 0.4f; col.collidesWith = ~(1 << 2);
            pourFxRenderer = go.GetComponent<ParticleSystemRenderer>();
            pourFxRenderer.renderMode = ParticleSystemRenderMode.Mesh;
            pourFxRenderer.mesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            pourFxMat = new Material(src) { name = "PourFx (runtime)" };
            pourFxRenderer.sharedMaterial = pourFxMat;
            pourFxRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void OnDestroy()
        {
            if (pourFxMat) Destroy(pourFxMat);
        }

        static readonly Color GrapeColor = new Color(0.36f, 0.12f, 0.48f, 1f);

        /// <summary>Tip angle 0..1 over the pour: tilt out, hold while it empties, come back.</summary>
        static float TipCurve(float t)
        {
            if (t < 0f) return 0f;
            if (t < 0.25f) return Ease(t / 0.25f);
            if (t < 0.7f) return 1f;
            return 1f - Ease((t - 0.7f) / 0.3f);
        }

        static float Ease(float t) => t * t * (3f - 2f * t);

        void LateUpdate()
        {
            if (src == null) return;
            var actual = src.Type;
            float target = src.Fill;
            if (src.Amount != lastAmount || actual != lastType)
            {
                if (lastAmount >= 0) bounce = 1f;
                // A different resource starts from the bottom; emptying keeps the old type until it has drained.
                if (actual != ResourceType.Empty && actual != shownType) { shownFill = 0f; shownType = actual; }
                lastAmount = src.Amount;
                lastType = actual;
            }
            float pourT = src.PourProgress;
            bool pouring = pourT >= 0f;
            if (pouring && shownType == ResourceType.Empty && src.PourType != ResourceType.Empty)
            {
                shownType = src.PourType;   // joined mid-pour or state arrived before the first frame
                shownFill = Mathf.Max(shownFill, 0.5f);
            }
            // While tipped the content drains fast; otherwise the usual fill / drain speed.
            float drainSpeed = pouring ? (pourT > 0.2f ? 2.0f : 0f) : 1.6f;
            shownFill = Mathf.MoveTowards(shownFill, target, Time.deltaTime * drainSpeed);
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
                    // Only a thin surface disc at the current level: a full column would poke through the
                    // tapered wall near the bottom. The disc widens as it rises, following the bucket wall.
                    float h = Mathf.Lerp(0.01f, (rimY - floorY) * liquidMaxFill, shownFill);
                    float widen = Mathf.Lerp(0.82f, 1f, shownFill);
                    liquid.localScale = new Vector3(liquidXZ.x * widen, 0.004f, liquidXZ.y * widen);
                    var p = liquid.localPosition; p.y = floorY + h; liquid.localPosition = p;
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
                    float k = grapeFill > 0.01f ? Mathf.Clamp01((grapeFill - threshold) * grapeBunches.Length + 1f) : 0f;
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
            // Tip-out: rotate forward around the front rim so the content falls in front of the player.
            float tip = TipCurve(pourT);
            Quaternion tipRot = Quaternion.Euler(pourAngle * tip, 0f, 0f);
            Vector3 ls = transform.localScale;
            Vector3 hinge = Vector3.Scale(new Vector3(0f, rimY, pourFx ? pourFx.transform.localPosition.z : 0f), ls);
            Vector3 hingeShift = restLocalRot * (hinge - tipRot * hinge);
            transform.localRotation = restLocalRot * tipRot;
            transform.localPosition = restLocalPos + offset + hingeShift + restLocalRot * (pourReach * tip);
            lastWorldPos = transform.position;

            if (pourFx)
            {
                bool emit = pouring && tip > 0.55f && shownFill > 0.02f;
                if (emit && !pourFx.isEmitting)
                {
                    pourFxMat.SetColor(BaseColor, type == ResourceType.Grapes ? GrapeColor : type == ResourceType.Wine ? wineColor : juiceColor);
                    var main = pourFx.main;
                    bool grapes = type == ResourceType.Grapes;
                    float k = transform.lossyScale.x;
                    main.startSize = grapes ? new ParticleSystem.MinMaxCurve(0.05f * k, 0.08f * k) : new ParticleSystem.MinMaxCurve(0.012f * k, 0.022f * k);
                    var em = pourFx.emission; em.rateOverTime = grapes ? 45f : 220f;
                    pourFx.Play();
                }
                else if (!emit && pourFx.isEmitting) pourFx.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }
    }
}
