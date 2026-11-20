using Mirror;
using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// Manual grape press: GRAPES -> GRAPE_JUICE (1:1).
    /// States: Empty -> Loaded (grapes in) -> Pressing (handle clicked, ~3 s) -> JuiceReady (juice waiting).
    /// Grapes can be topped up while juice waits, as long as the output can hold the next batch.
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

        [SyncVar] int storedGrapes;
        [SyncVar] int juice;
        [SyncVar] PressState state;
        [SyncVar] double pressStart;

        public int StoredGrapes => storedGrapes;
        public int Juice => juice;
        public PressState State => state;

        Transform screwPivot, platePivot, grapePivot;
        Vector3 screwPivotRest, platePivotRest;
        Quaternion screwPivotRot;
        Vector3 grapeScale;
        float shownJuice, shownGrapes;
        bool pivotsBuilt;

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
                juice += storedGrapes;            // 1 grape = 1 juice; output space was reserved when loading
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

        /// <summary>Grapes the press can still accept (input space and output space for the next batch).</summary>
        int Acceptable => Mathf.Max(0, Mathf.Min(inputCapacity - storedGrapes, outputCapacity - juice - storedGrapes));

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
                        return Blocked(title, Status, bucket.Type == ResourceType.Empty ? "Bring grapes in your bucket" : "Wrong Resource Type", prog);
                    if (Acceptable <= 0) return Blocked(title, Status, juice > 0 ? "Machine Full — collect the juice" : "Machine Full", prog);
                    return Action(title, Status, $"Add Grapes — {storedGrapes}/{inputCapacity}", prog);

                case InteractionPart.PressHandle:
                    if (state == PressState.Pressing) return Blocked(title, Status, "Processing...", Progress);
                    if (storedGrapes <= 0) return Blocked(title, Status, "Not Ready — add grapes first");
                    return Action(title, Status, $"Start Press — {storedGrapes} grapes");

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
                    if (n <= 0) return "Machine Full";
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

        void UpdateVisuals()
        {
            BuildPivots();
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
