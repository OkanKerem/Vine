using Mirror;
using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// Fermentation machine: GRAPE_JUICE -> WINE (1:1), no minigame.
    /// States: Empty -> Loaded (lid opens, juice pours, lid closes) -> Fermenting (needle sweeps) -> WineReady.
    /// Juice can be added while the machine is empty, pouring, or holding finished wine (if the output can
    /// take the next batch); never while fermenting. Finished wine is never discarded.
    /// </summary>
    public class FermentationMachineController : WineryStation
    {
        public enum FermentState : byte { Empty, Loaded, Fermenting, WineReady }

        [Header("Tuning (upgrade-ready)")]
        [SyncVar] public int inputCapacity = 20;
        [SyncVar] public int outputCapacity = 20;
        [SyncVar] public float fermentDuration = 10f;
        [Tooltip("Lid open + pour + lid close (s). Fermentation starts right after.")]
        [SyncVar] public float pourDuration = 1.8f;

        [Header("Moving parts (from the SmallFermentationStation model)")]
        public Transform lid;              // Fermenter_InputLid
        public Transform lidHingeHint;     // optional: explicit hinge position
        public Transform needle;           // Fermenter_ProgressNeedle
        public Transform dial;             // Fermenter_ProgressDial
        public Transform liquid;           // Fermenter_Liquid (visible through the inspection glass)
        public Renderer liquidRenderer;
        public ParticleSystem pourStream;
        [Tooltip("Play the station's own stream above the opening. Off: the player's bucket pours the juice in.")]
        public bool useStationPourStream = false;
        public ParticleSystem bubbles;
        [Tooltip("Bubble effect played while fermenting (NamuFX Bubbles_Vertical_Loop). Spawned where 'bubbles' sits and replaces it.")]
        public GameObject bubblesPrefab;
        [Tooltip("Size of the bubble prefab relative to its authored size.")]
        public float bubblesScale = 0.05f;

        [Header("Animation")]
        [Tooltip("Lid angle (degrees about the hinge) when closed, relative to the authored (open) pose.")]
        public float lidClosedAngle = 92f;
        [Tooltip("Lid angle when fully open during pouring.")]
        public float lidOpenAngle = -8f;
        [Tooltip("Needle sweep from 0% to 100% (degrees, negative = clockwise seen from the front).")]
        public float needleSweep = -270f;
        public Color juiceColor = new Color(0.52f, 0.16f, 0.55f, 1f);
        public Color wineColor = new Color(0.46f, 0.02f, 0.07f, 1f);

        [SyncVar] int juice;
        [SyncVar] int wine;
        [SyncVar] FermentState state;
        [SyncVar] double pourStart = -999;
        [SyncVar] double fermentStart;

        public int Juice => juice;
        public int Wine => wine;
        public FermentState State => state;

        Transform lidPivot, needlePivot, liquidPivot;
        Quaternion lidRest, needleRest;
        Vector3 liquidScale;
        float shownLevel;
        bool built;
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock block;

        void Awake() => Build();

        void Build()
        {
            if (built) return;
            built = true;
            if (lid)
            {
                // Hinge: the lower back edge of the authored (standing open) lid.
                var b = RendererBounds(lid);
                Vector3 hinge = lidHingeHint ? lidHingeHint.position : new Vector3(b.center.x, b.min.y + 0.03f, b.center.z);
                lidPivot = MakePivot(lid, hinge, "LidHinge (runtime)");
                lidRest = lidPivot.localRotation;
            }
            if (needle)
            {
                var c = dial ? RendererBounds(dial).center : RendererBounds(needle).center;
                needlePivot = MakePivot(needle, c, "NeedlePivot (runtime)");
                needleRest = needlePivot.localRotation;
            }
            if (liquid)
            {
                var lb = RendererBounds(liquid);
                liquidPivot = MakePivot(liquid, new Vector3(lb.center.x, lb.min.y, lb.center.z), "LiquidPivot (runtime)");
                liquidScale = liquidPivot.localScale;
            }
            if (bubblesPrefab && bubbles)
            {
                // The prefab takes the place of the generated emitter (which stays as the anchor, switched off).
                var fx = FxUtil.Spawn(bubblesPrefab, bubbles.transform.position, bubbles.transform.parent, bubblesScale);
                var ps = fx ? fx.GetComponent<ParticleSystem>() : null;
                if (ps)
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    bubbles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    bubbles.gameObject.SetActive(false);
                    bubbles = ps;
                }
            }
        }

        // ------------------------------------------------------------------ host logic

        void Update()
        {
            if (isServer)
            {
                if (state == FermentState.Loaded && Now >= fermentStart) state = FermentState.Fermenting;
                if (state == FermentState.Fermenting && Now >= fermentStart + fermentDuration)
                {
                    wine += juice;                  // output space was reserved when the juice went in
                    juice = 0;
                    state = FermentState.WineReady;
                }
            }
            UpdateVisuals();
        }

        int Acceptable => Mathf.Max(0, Mathf.Min(inputCapacity - juice, outputCapacity - wine - juice));

        float Progress => state == FermentState.Fermenting ? Mathf.Clamp01((float)((Now - fermentStart) / fermentDuration))
                        : state == FermentState.WineReady ? 1f : 0f;

        string Status
        {
            get
            {
                switch (state)
                {
                    case FermentState.Loaded: return $"Filling · {juice} L juice";
                    case FermentState.Fermenting: return $"Fermenting {juice} L — {Mathf.RoundToInt(Progress * 100)}%";
                    case FermentState.WineReady: return $"Wine ready · {wine} L";
                    default: return $"Empty · capacity {inputCapacity} L";
                }
            }
        }

        public override InteractionPrompt GetPrompt(WineryInteractable target, PlayerBucket bucket)
        {
            string title = "Fermentation Machine";
            float prog = state == FermentState.Fermenting || state == FermentState.WineReady ? Progress : -1f;
            if (target.part == InteractionPart.FermenterInput)
            {
                if (state == FermentState.Fermenting) return Blocked(title, Status, $"Fermenting — {Mathf.RoundToInt(Progress * 100)}%", prog);
                if (bucket.Type != ResourceType.GrapeJuice)
                {
                    if (state == FermentState.WineReady && wine > 0) return Blocked(title, Status, "Wine ready — use the tap", prog);
                    return Blocked(title, Status, bucket.Type == ResourceType.Empty ? BringHint(bucket, "grape juice") : HoldsOther(bucket), prog);
                }
                if (Acceptable <= 0) return Blocked(title, Status, wine > 0 ? "Machine Full — collect the wine" : "Machine Full", prog);
                return Action(title, Status, $"Add Grape Juice — {juice}/{inputCapacity} L", prog);
            }
            if (target.part == InteractionPart.FermenterOutput)
            {
                if (wine <= 0) return Blocked(title, Status, state == FermentState.Fermenting ? $"Fermenting — {Mathf.RoundToInt(Progress * 100)}%" : "Not Ready", prog);
                string why = CannotReceive(bucket, ResourceType.Wine);
                if (why != null) return Blocked(title, Status, why, prog);
                return Action(title, Status, $"Collect Wine — {wine} L", prog);
            }
            return Blocked(title, Status, "");
        }

        [Server]
        public override string ServerInteract(WineryInteractable target, PlayerBucket bucket, out bool success, out string floatText)
        {
            success = false; floatText = null;
            if (target.part == InteractionPart.FermenterInput)
            {
                if (state == FermentState.Fermenting) return "Processing...";
                if (bucket.Type != ResourceType.GrapeJuice) return bucket.Type == ResourceType.Empty ? "Your bucket is empty" : "Wrong Resource Type";
                int n = bucket.ServerRemove(ResourceType.GrapeJuice, Acceptable);   // bucket only changes if the tank accepts
                if (n <= 0) return "Machine Full";
                juice += n;
                pourStart = Now;
                fermentStart = Now + pourDuration;  // starts automatically once the lid has closed
                state = FermentState.Loaded;
                success = true; floatText = $"-{n} L Juice";
                return bucket.Amount > 0 ? $"Poured {n} L — tank is full" : $"Poured {n} L — fermentation starting";
            }
            if (target.part == InteractionPart.FermenterOutput)
            {
                if (wine <= 0) return state == FermentState.Fermenting ? "Processing..." : "Not Ready";
                if (!bucket.CanHold(ResourceType.Wine)) return "Wrong Resource Type";
                int n = bucket.ServerAdd(ResourceType.Wine, wine);
                if (n <= 0) return "Bucket Full";
                wine -= n;
                if (wine == 0 && state == FermentState.WineReady) state = juice > 0 ? FermentState.Loaded : FermentState.Empty;
                success = true; floatText = $"+{n} L Wine";
                return wine > 0 ? $"Collected {n} L — {wine} L left" : $"Collected {n} L of wine";
            }
            return null;
        }

        /// <summary>The bucket pours into the input opening on top (whichever input part was clicked).</summary>
        public override Vector3 PourPoint(WineryInteractable target) =>
            target != null && target.part == InteractionPart.FermenterInput ? PourPointOf("Fermenter_InputOpening", target) : base.PourPoint(target);

        // ------------------------------------------------------------------ visuals (every client)

        void UpdateVisuals()
        {
            Build();
            float tp = (float)(Now - pourStart);
            float open = 0f;
            float openT = 0.35f, closeT = 0.4f;
            if (tp >= 0f && tp < pourDuration)
            {
                if (tp < openT) open = Ease(tp / openT);
                else if (tp > pourDuration - closeT) open = Ease((pourDuration - tp) / closeT);
                else open = 1f;
            }
            if (lidPivot) lidPivot.localRotation = lidRest * Quaternion.Euler(Mathf.Lerp(lidClosedAngle, lidOpenAngle, open), 0f, 0f);

            bool pouring = useStationPourStream && tp > openT && tp < pourDuration - closeT;
            if (pourStream)
            {
                if (pouring && !pourStream.isEmitting) pourStream.Play();
                else if (!pouring && pourStream.isEmitting) pourStream.Stop();
            }
            if (bubbles)
            {
                bool fermenting = state == FermentState.Fermenting;
                if (fermenting && !bubbles.isEmitting) bubbles.Play();
                else if (!fermenting && bubbles.isEmitting) bubbles.Stop();
            }

            if (needlePivot) needlePivot.localRotation = needleRest * Quaternion.Euler(0f, 0f, needleSweep * Progress);

            float level = (juice + wine) / (float)Mathf.Max(1, Mathf.Max(inputCapacity, outputCapacity));
            if (VisualsHeld && level > shownLevel) level = shownLevel;   // juice not poured in yet
            shownLevel = Mathf.MoveTowards(shownLevel, level, Time.deltaTime * 0.8f);
            if (liquidPivot)
            {
                liquidPivot.gameObject.SetActive(shownLevel > 0.01f);
                liquidPivot.localScale = new Vector3(liquidScale.x, liquidScale.y * Mathf.Max(0.02f, shownLevel), liquidScale.z);
                if (liquidRenderer)
                {
                    block ??= new MaterialPropertyBlock();
                    float w = state == FermentState.WineReady ? 1f : state == FermentState.Fermenting ? Progress : 0f;
                    block.SetColor(BaseColor, Color.Lerp(juiceColor, wineColor, w));
                    liquidRenderer.SetPropertyBlock(block);
                }
            }
        }
    }
}
