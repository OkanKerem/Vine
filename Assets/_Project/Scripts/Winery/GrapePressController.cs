using Mirror;
using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// Manual grape press: GRAPES -> GRAPE_JUICE (1:1).
    /// States: Empty -> Loaded (grapes in) -> Pressing (handle clicked, ~3 s) -> JuiceReady (juice waiting).
    /// Grapes can always be topped up while juice waits. If a press produces more juice than the output
    /// bucket holds, the excess overflows onto the ground (lost) — the press never blocks the player.
    ///
    /// Animation is evaluated from the replicated start time, so every client — including late joiners —
    /// sees the same point of the cycle. Moving parts are wrapped in runtime pivots; the scene file and
    /// mesh transforms are never changed.
    /// </summary>
    public class GrapePressController : WineryStation
    {
        public enum PressState : byte { Empty, Loaded, Pressing, JuiceReady }

        [Header("Tuning (upgrade-ready)")]
        [SyncVar] public int inputCapacity = 20;
        [SyncVar] public int outputCapacity = 20;
        [SyncVar] public float pressDuration = 3f;

        [Header("Moving parts (from the SmallGrapePress model)")]
        public Transform handle;          // Press_Handle
        public Transform screw;           // Press_Screw
        public Transform plate;           // Press_Plate
        public Transform grapes;          // Press_Grapes (shown / compressed)
        public Transform juiceLiquid;     // liquid disc inside Press_Bucket
        public Renderer juiceRenderer;
        public ParticleSystem juiceStream; // drips from the spout while pressing

        [Header("Animation")]
        [Tooltip("How far the screw, handle and plate travel down (m).")]
        public float plateTravel = 0.32f;
        [Tooltip("Handle turns during the downward stroke.")]
        public float handleTurns = 2.5f;
        [Tooltip("Fraction of the cycle spent pressing down (the rest returns the plate).")]
        [Range(0.3f, 0.9f)] public float downFraction = 0.68f;
        [Tooltip("Grape pile height when fully compressed (fraction).")]
        [Range(0.1f, 1f)] public float compressedHeight = 0.3f;
        public float juiceMaxHeight = 0.12f;
        [Tooltip("How long the overflow spill effect runs (s).")]
        public float spillSeconds = 1.6f;

        [SyncVar] int storedGrapes;
        [SyncVar] int juice;
        [SyncVar] PressState state;
        [SyncVar] double pressStart;
        [SyncVar] double spillStart = -999;
        [SyncVar] int lastSpill;

        public int StoredGrapes => storedGrapes;
        public int Juice => juice;
        public PressState State => state;
        /// <summary>Litres lost in the most recent overflow (0 if none yet).</summary>
        public int LastSpill => lastSpill;

        Transform screwPivot, platePivot, grapePivot;
        Vector3 screwPivotRest, platePivotRest;
        Quaternion screwPivotRot;
        Vector3 grapeScale;
        float shownJuice, shownGrapes;
        bool pivotsBuilt;
        ParticleSystem spillFx;

        void Awake() => BuildPivots();

        void BuildPivots()
        {
            if (pivotsBuilt || !screw || !handle || !plate) return;
            pivotsBuilt = true;
            // Screw axis: centre of the screw mesh; handle and screw turn together around it and travel down.
            var sb = RendererBounds(screw);
            screwPivot = MakePivot(screw, new Vector3(sb.center.x, sb.max.y, sb.center.z), "ScrewPivot (runtime)");
            handle.SetParent(screwPivot, true);
            screwPivotRest = screwPivot.localPosition;
            screwPivotRot = screwPivot.localRotation;
            var pb = RendererBounds(plate);
            platePivot = MakePivot(plate, pb.center, "PlatePivot (runtime)");
            platePivotRest = platePivot.localPosition;
            if (grapes)
            {
                var gb = RendererBounds(grapes);
                grapePivot = MakePivot(grapes, new Vector3(gb.center.x, gb.min.y, gb.center.z), "GrapePivot (runtime)");
                grapeScale = grapePivot.localScale;
            }
        }

        // ------------------------------------------------------------------ host logic

        void Update()
        {
            if (isServer && state == PressState.Pressing && Now >= pressStart + pressDuration)
            {
                int total = juice + storedGrapes;  // 1 grape = 1 juice
                juice = Mathf.Min(outputCapacity, total);
                int spilled = total - juice;       // overflow: the output bucket runs over
                if (spilled > 0) { lastSpill = spilled; spillStart = Now; }
                storedGrapes = 0;
                state = PressState.JuiceReady;    // leave Pressing first, then derive the resting state
                ServerRefreshState();
            }
            UpdateVisuals();
        }

        [Server]
        void ServerRefreshState()
        {
            if (state == PressState.Pressing) return;
            state = storedGrapes > 0 ? PressState.Loaded : juice > 0 ? PressState.JuiceReady : PressState.Empty;
        }

        /// <summary>Grapes the press can still accept. Output space is not reserved: excess juice overflows.</summary>
        int Acceptable => Mathf.Max(0, inputCapacity - storedGrapes);

        /// <summary>Litres that would spill if <paramref name="extraGrapes"/> more were pressed now.</summary>
        int WouldSpill(int extraGrapes) => Mathf.Max(0, juice + storedGrapes + extraGrapes - outputCapacity);

        float Progress => state == PressState.Pressing ? Mathf.Clamp01((float)((Now - pressStart) / pressDuration)) : 0f;

        string Status => state == PressState.Pressing
            ? $"Pressing {storedGrapes} grapes · {Mathf.RoundToInt(Progress * 100)}%"
            : $"Grapes {storedGrapes}/{inputCapacity} · Juice {juice} L";

        public override InteractionPrompt GetPrompt(WineryInteractable target, PlayerBucket bucket)
        {
            string title = "Grape Press";
            float prog = state == PressState.Pressing ? Progress : -1f;
            switch (target.part)
            {
                case InteractionPart.PressInput:
                    if (state == PressState.Pressing) return Blocked(title, Status, "Processing...", prog);
                    if (bucket.Type != ResourceType.Grapes)
                        return Blocked(title, Status, bucket.Type == ResourceType.Empty ? BringHint(bucket, "grapes") : HoldsOther(bucket), prog);
                    if (Acceptable <= 0) return Blocked(title, Status, "Press Full — pull the handle", prog);
                    int adding = Mathf.Min(Acceptable, bucket.Amount);
                    int spill = WouldSpill(adding);
                    return Action(title, Status, spill > 0 ? $"Add Grapes — {spill} L will overflow" : $"Add Grapes — {storedGrapes}/{inputCapacity}", prog);

                case InteractionPart.PressHandle:
                    if (state == PressState.Pressing) return Blocked(title, Status, "Processing...", Progress);
                    if (storedGrapes <= 0) return Blocked(title, Status, "Not Ready — add grapes first");
                    int lost = WouldSpill(0);
                    return Action(title, Status, lost > 0 ? $"Start Press — {lost} L will overflow" : $"Start Press — {storedGrapes} grapes");

                case InteractionPart.PressOutput:
                    if (juice <= 0) return Blocked(title, Status, state == PressState.Pressing ? "Processing..." : "Not Ready — no juice yet", prog);
                    string why = CannotReceive(bucket, ResourceType.GrapeJuice);
                    if (why != null) return Blocked(title, Status, why, prog);
                    return Action(title, Status, $"Collect Grape Juice — {juice} L", prog);
            }
            return Blocked(title, Status, "");
        }

        [Server]
        public override string ServerInteract(WineryInteractable target, PlayerBucket bucket, out bool success, out string floatText)
        {
            success = false; floatText = null;
            switch (target.part)
            {
                case InteractionPart.PressInput:
                {
                    if (state == PressState.Pressing) return "Processing...";
                    if (bucket.Type != ResourceType.Grapes) return bucket.Type == ResourceType.Empty ? "Your bucket is empty" : "Wrong Resource Type";
                    int n = bucket.ServerRemove(ResourceType.Grapes, Acceptable);
                    if (n <= 0) return "Press Full";
                    storedGrapes += n;
                    ServerRefreshState();
                    success = true; floatText = $"-{n} Grapes";
                    return bucket.Amount > 0 ? $"Added {n} grapes — press is full" : $"Added {n} grapes";
                }
                case InteractionPart.PressHandle:
                {
                    if (state == PressState.Pressing) return "Processing...";      // locked during a cycle
                    if (storedGrapes <= 0) return "Not Ready — add grapes first";
                    state = PressState.Pressing;
                    pressStart = Now;
                    success = true;
                    return $"Pressing {storedGrapes} grapes...";
                }
                case InteractionPart.PressOutput:
                {
                    if (juice <= 0) return state == PressState.Pressing ? "Processing..." : "Not Ready";
                    if (!bucket.CanHold(ResourceType.GrapeJuice)) return "Wrong Resource Type";
                    int n = bucket.ServerAdd(ResourceType.GrapeJuice, juice);
                    if (n <= 0) return "Bucket Full";
                    juice -= n;
                    ServerRefreshState();
                    success = true; floatText = $"+{n} L Juice";
                    return juice > 0 ? $"Collected {n} L — {juice} L left in the press" : $"Collected {n} L of grape juice";
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ visuals (every client)

        /// <summary>Runtime copy of the drip emitter, spraying juice over the output bucket's rim.</summary>
        void BuildSpillFx()
        {
            if (spillFx || !juiceStream || !juiceLiquid) return;
            var go = Instantiate(juiceStream.gameObject, juiceLiquid.parent);
            go.name = "Winery_JuiceSpill (runtime)";
            float r = Mathf.Max(0.05f, Mathf.Abs(juiceLiquid.lossyScale.x) * 0.5f);
            go.transform.position = juiceLiquid.position + Vector3.up * (juiceMaxHeight + 0.02f);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // circle lies flat: particles leave the rim outward
            spillFx = go.GetComponent<ParticleSystem>();
            spillFx.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = spillFx.main; main.startSpeed = new ParticleSystem.MinMaxCurve(0.35f, 0.7f); main.gravityModifier = 1.2f; main.startLifetime = 0.7f;
            var sh = spillFx.shape; sh.shapeType = ParticleSystemShapeType.Circle; sh.radius = r; sh.radiusThickness = 0f; sh.arc = 360f;
            var em = spillFx.emission; em.rateOverTime = 120f;
        }

        void UpdateVisuals()
        {
            BuildPivots();
            BuildSpillFx();
            if (spillFx)
            {
                bool spilling = Now - spillStart >= 0 && Now - spillStart < spillSeconds;
                if (spilling && !spillFx.isEmitting) spillFx.Play();
                else if (!spilling && spillFx.isEmitting) spillFx.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
            float t = Progress;
            // Down stroke (eased), then the plate returns.
            float travel = state != PressState.Pressing ? 0f
                : t < downFraction ? Ease(t / downFraction)
                : 1f - Ease((t - downFraction) / (1f - downFraction));

            if (screwPivot)
            {
                screwPivot.localPosition = screwPivotRest + Vector3.down * (travel * plateTravel);
                // Screwing in turns one way, unscrewing turns back: rotation follows the travel.
                screwPivot.localRotation = screwPivotRot * Quaternion.Euler(0f, travel * handleTurns * 360f, 0f);
            }
            if (platePivot) platePivot.localPosition = platePivotRest + Vector3.down * (travel * plateTravel);

            // Grape pile: height follows stored grapes, squashed while the plate is down, gone once juiced.
            float pileTarget = storedGrapes > 0 ? Mathf.Lerp(0.35f, 1f, storedGrapes / (float)Mathf.Max(1, inputCapacity)) : 0f;
            if (state == PressState.Pressing)
                pileTarget *= t < downFraction ? Mathf.Lerp(1f, compressedHeight, Ease(t / downFraction)) : compressedHeight * (1f - Ease((t - downFraction) / (1f - downFraction)));
            shownGrapes = state == PressState.Pressing ? pileTarget : Mathf.MoveTowards(shownGrapes, pileTarget, Time.deltaTime * 2f);
            if (grapePivot)
            {
                grapePivot.gameObject.SetActive(shownGrapes > 0.01f);
                grapePivot.localScale = new Vector3(grapeScale.x, grapeScale.y * Mathf.Max(0.01f, shownGrapes), grapeScale.z);
            }

            // Juice collecting in the output bucket.
            float juiceTarget = juice / (float)Mathf.Max(1, outputCapacity);
            if (state == PressState.Pressing) juiceTarget += storedGrapes / (float)Mathf.Max(1, outputCapacity) * Mathf.Clamp01((t - 0.15f) / (downFraction - 0.15f)) * 0.98f;
            shownJuice = Mathf.MoveTowards(shownJuice, Mathf.Clamp01(juiceTarget), Time.deltaTime * 1.5f);
            if (juiceLiquid)
            {
                juiceLiquid.gameObject.SetActive(shownJuice > 0.01f);
                var s = juiceLiquid.localScale; s.y = Mathf.Max(0.001f, shownJuice * juiceMaxHeight * 0.5f); juiceLiquid.localScale = s;
            }
            if (juiceStream)
            {
                bool flowing = state == PressState.Pressing && t > 0.15f && t < downFraction + 0.05f;
                if (flowing && !juiceStream.isEmitting) juiceStream.Play();
                else if (!flowing && juiceStream.isEmitting) juiceStream.Stop();
            }
        }
    }
}
