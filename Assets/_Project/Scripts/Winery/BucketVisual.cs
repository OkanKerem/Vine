using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// Shows what is inside a bucket model: a rising liquid surface (juice / wine) or a growing pile of grape
    /// bunches. Purely visual — it only reads the replicated <see cref="PlayerBucket"/> state.
    /// Used both for the first-person view bucket and the third-person bucket other players see.
    ///
    /// Animations (all driven by synced state, so every player sees the same):
    ///  * G: tip forward and pour the content on the ground.
    ///  * Juice / wine into a machine: the bucket reaches over the opening / funnel, tips and pours a stream,
    ///    then comes back.
    ///  * Grapes into the press: the bucket stays in the hand; the bunches fly out of it into the hopper.
    ///  * Harvest: the bucket grows one step as each flying bunch lands in it (<see cref="ExpectIncoming"/>).
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

        [Header("Pour into a machine")]
        [Tooltip("Tip angle over the machine opening (degrees).")]
        public float transferAngle = 105f;
        [Tooltip("Height of the bucket's front rim above the opening while pouring (m).")]
        public float transferHover = 0.2f;
        [Tooltip("The bucket never travels further than this from the hand (m).")]
        public float transferMaxReach = 2.5f;
        [Tooltip("Flight time of a grape bunch from the bucket into the hopper (s).")]
        public float grapeFlightSeconds = 0.5f;
        [Tooltip("Delay between two bunches leaving the bucket for the hopper (s).")]
        public float grapeFlightStagger = 0.06f;
        [Tooltip("Height of the arc the bunches fly into the hopper (m).")]
        public float grapeFlightArc = 0.5f;

        // Transfer phases (fraction of PlayerBucket.TransferLength).
        const float ReachIn = 0.25f, TipStart = 0.22f, TipFull = 0.40f, TipEnd = 0.70f, TipBack = 0.82f, ReachOut = 0.78f;
        const float LiquidFlowAt = 0.33f;

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock block;
        float shownFill;
        int lastAmount = -1;
        ResourceType lastType, shownType;
        float bounce;
        // Harvest: units still flying toward this bucket. The shown amount grows as each bunch lands.
        int inFlight, flightBase;
        float flightExpire;
        double seenTransfer = double.NaN, launchedTransfer = double.NaN;
        Vector3 restLocalPos, lastWorldPos;
        Quaternion restLocalRot;
        Vector2 liquidXZ = Vector2.one;
        ParticleSystem pourFx;
        ParticleSystemRenderer pourFxRenderer;
        Material pourFxMat;
        LineRenderer stream;
        Material streamMat;
        float streamHead, streamTail;
        bool streamOn, streamInto;
        const int StreamPoints = 16;
        const float StreamFallSeconds = 0.14f;   // time for the stream's front / top to fall all the way
        const float StreamWidth = 0.035f;        // at the rim, before the bucket's scale
        Vector3[] bunchScale;
        float bobPhase;

        /// <summary>World point just inside the bucket opening (target for bunches flying in).</summary>
        public Vector3 MouthWorld => transform.TransformPoint(new Vector3(0f, Mathf.Lerp(floorY, rimY, 0.75f), 0f));

        /// <summary>Front rim in local space (where the content leaves when tipped).</summary>
        Vector3 FrontRimLocal => pourFx ? pourFx.transform.localPosition : new Vector3(0f, rimY, 0.1f);

        /// <summary>Amount the bucket shows: the synced amount minus what is still in the air.</summary>
        int VisibleAmount => src == null ? 0 : inFlight > 0 ? Mathf.Min(Mathf.Max(src.Amount - inFlight, flightBase), src.Amount) : src.Amount;

        /// <summary>
        /// <paramref name="units"/> are flying toward this bucket: they only show once <see cref="Landed"/> reports
        /// them. Works whether the synced amount has already arrived or not (locally predicted harvest).
        /// </summary>
        public void ExpectIncoming(int units, float maxSeconds)
        {
            if (units <= 0) return;
            flightBase = VisibleAmount;
            inFlight += units;
            flightExpire = Mathf.Max(flightExpire, Time.time + maxSeconds);
        }

        /// <summary>A flying bunch carrying <paramref name="units"/> just dropped into the bucket.</summary>
        public void Landed(int units)
        {
            if (inFlight <= 0) return;
            units = Mathf.Min(units, inFlight);
            inFlight -= units;
            flightBase += units;
            bounce = 1f;
        }

        /// <summary>True on the bucket model this client actually looks at (spawns effects only once).</summary>
        bool IsSeenVisual => bucket == null || bucket.ActiveVisual == this;

        void Awake()
        {
            restLocalPos = transform.localPosition;
            restLocalRot = transform.localRotation;
            if (liquid) liquidXZ = new Vector2(liquid.localScale.x, liquid.localScale.z);
            lastWorldPos = transform.parent ? transform.parent.position : transform.position;
            if (grapeBunches != null)
            {
                bunchScale = new Vector3[grapeBunches.Length];
                for (int i = 0; i < grapeBunches.Length; i++) bunchScale[i] = grapeBunches[i] ? grapeBunches[i].localScale : Vector3.one;
            }
            if (!bucket) bucket = GetComponentInParent<PlayerBucket>();
            src = bucket ? bucket : GetComponentInParent<IBucketContents>();
            BuildPourFx();
        }

        /// <summary>Runtime emitter on the front rim (grape blobs tumbling out) and the liquid stream.</summary>
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
            BuildStream(src);
        }

        void OnDestroy()
        {
            if (pourFxMat) Destroy(pourFxMat);
            if (streamMat) Destroy(streamMat);
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

        /// <summary>How far the bucket has travelled toward the machine (0 = in the hand, 1 = over the opening).</summary>
        static float TransferReach(float t)
        {
            if (t < 0f) return 0f;
            if (t < ReachIn) return Ease(t / ReachIn);
            if (t < ReachOut) return 1f;
            return 1f - Ease((t - ReachOut) / (1f - ReachOut));
        }

        /// <summary>Tip over the opening (0..1).</summary>
        static float TransferTip(float t)
        {
            if (t < TipStart) return 0f;
            if (t < TipFull) return Ease((t - TipStart) / (TipFull - TipStart));
            if (t < TipEnd) return 1f;
            if (t < TipBack) return 1f - Ease((t - TipEnd) / (TipBack - TipEnd));
            return 0f;
        }

        static float Ease(float t) => t * t * (3f - 2f * t);

        /// <summary>Bunch i is shown (0..1) at this grape fill level.</summary>
        float BunchShown(int i, float grapeFill) =>
            grapeFill > 0.01f ? Mathf.Clamp01((grapeFill - (i + 0.5f) / grapeBunches.Length) * grapeBunches.Length + 1f) : 0f;

        void LateUpdate()
        {
            if (src == null) return;
            var actual = src.Type;
            if (inFlight > 0 && (Time.time > flightExpire || src.Amount < flightBase)) inFlight = 0;   // lost / poured out
            bool incoming = inFlight > 0;
            float target = src.Fill;
            if (src.Amount != lastAmount || actual != lastType)
            {
                if (lastAmount >= 0 && !incoming) bounce = 1f;      // harvested bunches bounce as they land instead
                // A different resource starts from the bottom; emptying keeps the old type until it has drained.
                if (actual != ResourceType.Empty && actual != shownType) { shownFill = 0f; shownType = actual; }
                lastAmount = src.Amount;
                lastType = actual;
            }
            if (incoming && src.Amount > 0) target = src.Fill * VisibleAmount / src.Amount;

            float pourT = src.PourProgress;
            bool pouring = pourT >= 0f;
            if (pouring && shownType == ResourceType.Empty && src.PourType != ResourceType.Empty)
            {
                shownType = src.PourType;   // joined mid-pour or state arrived before the first frame
                shownFill = Mathf.Max(shownFill, 0.5f);
            }

            // Pouring into a machine (carried buckets only).
            float trT = bucket ? bucket.TransferProgress : -1f;
            bool transferring = trT >= 0f;
            if (transferring && bucket.TransferStart != seenTransfer) OnTransferStart(trT);
            bool grapeTransfer = transferring && bucket.TransferType == ResourceType.Grapes;   // not poured: bunches fly

            // While tipped the content drains fast; otherwise the usual fill / drain speed.
            float drainSpeed = pouring ? (pourT > 0.2f ? 2.0f : 0f)
                             : transferring ? (shownType != ResourceType.Grapes && trT > LiquidFlowAt ? 2.4f : 0f)   // grapes leave at launch
                             : 1.6f;
            if (target > shownFill) drainSpeed = 1.6f;
            shownFill = Mathf.MoveTowards(shownFill, target, Time.deltaTime * drainSpeed);

            // Grapes into the press: right away, the bunches above the new level leave the bucket and fly into the hopper.
            if (grapeTransfer && bucket.TransferStart != launchedTransfer && shownType == ResourceType.Grapes)
            {
                launchedTransfer = bucket.TransferStart;
                LaunchGrapesInto(bucket.TransferPoint, src.Fill);
                shownFill = Mathf.Min(shownFill, src.Fill);
            }
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
                    float k = BunchShown(i, grapeFill);
                    grapeBunches[i].gameObject.SetActive(k > 0.02f);
                    grapeBunches[i].localScale = bunchScale[i] * Mathf.SmoothStep(0f, 1f, k);
                }

            // A little bounce when the contents change; bob while walking for the first-person bucket.
            bounce = Mathf.MoveTowards(bounce, 0f, Time.deltaTime * 3f);
            Vector3 offset = Vector3.up * (Mathf.Sin(bounce * Mathf.PI * 2f) * 0.02f * bounce);
            if (viewModel)
            {
                Vector3 v = (transform.parent ? transform.parent.position - lastWorldPos : Vector3.zero) / Mathf.Max(Time.deltaTime, 1e-4f);
                v.y = 0f;
                float speed = Mathf.Clamp01(v.magnitude / 5f);
                bobPhase += Time.deltaTime * (6f + speed * 5f);
                offset += new Vector3(Mathf.Cos(bobPhase * 0.5f) * bobAmount, Mathf.Abs(Mathf.Sin(bobPhase * 0.5f)) * bobAmount, 0f) * speed;
                if (liquid && liquid.gameObject.activeSelf)
                    liquid.localRotation = Quaternion.Euler(Mathf.Sin(bobPhase) * 3f * speed, 0f, Mathf.Cos(bobPhase * 0.7f) * 3f * speed);
            }
            lastWorldPos = transform.parent ? transform.parent.position : transform.position;

            // Tip-out: rotate forward around the front rim so the content falls in front of the player.
            float tip = TipCurve(pourT);
            Quaternion tipRot = Quaternion.Euler(pourAngle * tip, 0f, 0f);
            Vector3 ls = transform.localScale;
            Vector3 hinge = Vector3.Scale(new Vector3(0f, rimY, FrontRimLocal.z), ls);
            Vector3 hingeShift = restLocalRot * (hinge - tipRot * hinge);
            transform.localRotation = restLocalRot * tipRot;
            transform.localPosition = restLocalPos + offset + hingeShift + restLocalRot * (pourReach * tip);

            // Pour into a machine: reach over the opening (world space), tip toward it, come back.
            float reach = transferring && !grapeTransfer ? TransferReach(trT) : 0f;
            float ttip = transferring && !grapeTransfer ? TransferTip(trT) : 0f;
            if (reach > 0f)
            {
                Vector3 restPos = transform.position;
                Quaternion restRot = transform.rotation;
                Vector3 to = bucket.TransferPoint;
                Vector3 flat = to - restPos; flat.y = 0f;
                if (flat.sqrMagnitude < 1e-4f) flat = transform.parent ? transform.parent.forward : Vector3.forward;
                // Face the opening with the front rim, then tip forward around it.
                Quaternion face = Quaternion.LookRotation(flat.normalized, Vector3.up) * Quaternion.Euler(transferAngle * ttip, 0f, 0f);
                Vector3 rim = face * Vector3.Scale(FrontRimLocal, transform.lossyScale);
                Vector3 goal = to + Vector3.up * transferHover - rim;
                Vector3 travel = Vector3.ClampMagnitude(goal - restPos, transferMaxReach);
                transform.SetPositionAndRotation(restPos + travel * reach, Quaternion.Slerp(restRot, face, reach));
            }

            if (pourFx)
            {
                bool liquidTransfer = transferring && isLiquid && ttip > 0.55f && shownFill > src.Fill + 0.005f;
                bool emit = IsSeenVisual && ((pouring && tip > 0.55f && shownFill > 0.02f) || liquidTransfer);
                // Juice / wine run out as one continuous stream; only grapes tumble out as separate blobs.
                bool blobs = emit && !isLiquid;
                if (blobs && !pourFx.isEmitting)
                {
                    pourFxMat.SetColor(BaseColor, GrapeColor);
                    var main = pourFx.main;
                    float k = transform.lossyScale.x;
                    main.startSize = new ParticleSystem.MinMaxCurve(0.05f * k, 0.08f * k);
                    main.startLifetime = 0.9f;
                    var em = pourFx.emission; em.rateOverTime = 45f;
                    pourFx.Play();
                }
                else if (!blobs && pourFx.isEmitting) pourFx.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                UpdateStream(emit && isLiquid, liquidTransfer, type == ResourceType.Wine ? wineColor : juiceColor);
            }
        }

        // ------------------------------------------------------------------ liquid stream

        /// <summary>One tapered ribbon from the front rim down to the opening / ground, drawn every frame.</summary>
        void BuildStream(Material src)
        {
            var go = new GameObject("PourStream (runtime)");
            go.transform.SetParent(transform, false);
            stream = go.AddComponent<LineRenderer>();
            stream.useWorldSpace = true;
            stream.positionCount = StreamPoints;
            stream.numCapVertices = 4;
            stream.numCornerVertices = 2;
            stream.alignment = LineAlignment.View;
            stream.generateLightingData = true;
            stream.textureMode = LineTextureMode.Stretch;
            // Thick where it leaves the rim, thinning as it falls and speeds up.
            stream.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.2f, 0.75f), new Keyframe(1f, 0.5f));
            stream.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            stream.receiveShadows = false;
            streamMat = new Material(src) { name = "PourStream (runtime)" };
            stream.sharedMaterial = streamMat;
            stream.enabled = false;
        }

        /// <summary>
        /// The stream's front falls from the rim when pouring starts (head 0 -> 1); when it stops, its top lets go
        /// of the rim and falls after it (tail -> head), so it never just pops in or out.
        /// </summary>
        void UpdateStream(bool flowing, bool intoMachine, Color color)
        {
            if (!stream) return;
            float dt = Time.deltaTime;
            if (flowing)
            {
                if (!streamOn)
                {
                    streamOn = true;
                    streamHead = streamTail = 0f;
                    streamMat.SetColor(BaseColor, color);
                }
                streamTail = 0f;
                streamHead = Mathf.MoveTowards(streamHead, 1f, dt / StreamFallSeconds);
            }
            else if (streamOn)
            {
                streamHead = Mathf.MoveTowards(streamHead, 1f, dt / StreamFallSeconds);
                streamTail = Mathf.MoveTowards(streamTail, streamHead, dt / StreamFallSeconds);
                if (streamTail >= 0.999f) streamOn = false;
            }
            stream.enabled = streamOn;
            if (!streamOn) return;
            if (flowing) streamInto = intoMachine;

            Vector3 p0 = pourFx.transform.position;
            Vector3 dir = p0 - transform.position; dir.y = 0f;
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : transform.forward;
            Vector3 p2 = streamInto && bucket ? bucket.TransferPoint + Vector3.down * 0.05f : GroundBelow(p0 + dir * 0.12f);
            Vector3 flat = p2 - p0; flat.y = 0f;
            // Leaves the rim horizontally, then curves down (straight down if the end is right below).
            Vector3 c = p0 + dir * (flat.magnitude * 0.9f);
            for (int i = 0; i < StreamPoints; i++)
            {
                float t = Mathf.Lerp(streamTail, streamHead, i / (float)(StreamPoints - 1));
                float u = 1f - t;
                stream.SetPosition(i, u * u * p0 + 2f * u * t * c + t * t * p2);
            }
            stream.widthMultiplier = StreamWidth * transform.lossyScale.x;
        }

        /// <summary>Where a stream poured at <paramref name="from"/> lands (ignores this bucket and its carrier).</summary>
        Vector3 GroundBelow(Vector3 from)
        {
            var hits = Physics.RaycastAll(from, Vector3.down, 4f, ~(1 << 2), QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Vector3 point = from + Vector3.down * 1.5f;
            var root = transform.root;
            foreach (var h in hits)
                if (h.distance < best && h.collider.transform.root != root) { best = h.distance; point = h.point; }
            return point;
        }

        /// <summary>A new pour into a machine began: the machine waits with its level until the content arrives.</summary>
        void OnTransferStart(float progressNow)
        {
            seenTransfer = bucket.TransferStart;
            if (!IsSeenVisual) return;
            var station = bucket.TransferStation;
            if (station == null) return;
            float dur = bucket.TransferLength;
            int bunches = grapeBunches != null ? grapeBunches.Length : 0;
            float arrive = bucket.TransferType == ResourceType.Grapes
                ? grapeFlightSeconds + grapeFlightStagger * Mathf.Max(0, bunches - 1) + 0.05f
                : LiquidFlowAt * dur + 0.15f;
            station.HoldVisuals(Time.time + Mathf.Max(0f, arrive - progressNow * dur));
        }

        /// <summary>Copies of the bunches that are above <paramref name="newFill"/> fly from the bucket to <paramref name="point"/>.</summary>
        void LaunchGrapesInto(Vector3 point, float newFill)
        {
            if (grapeBunches == null || !IsSeenVisual) return;
            float delay = 0f;
            Vector3 into = point + Vector3.down * 0.08f;
            for (int i = grapeBunches.Length - 1; i >= 0; i--)
            {
                var b = grapeBunches[i];
                if (!b || !b.gameObject.activeInHierarchy) continue;
                if (BunchShown(i, newFill) > 0.02f) continue;          // stays in the bucket (partial pour)
                FlyingBunch.Launch(b, () => into, delay, grapeFlightSeconds, grapeFlightArc, 0.8f);
                delay += grapeFlightStagger;
            }
        }
    }
}
