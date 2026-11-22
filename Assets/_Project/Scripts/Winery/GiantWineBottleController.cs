using Mirror;
using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// The giant bottle in the square: final storage for wine. Players pour wine into the funnel; the host
    /// stores it and every client raises the bottle's WineLiquid mesh to the same level.
    /// The output tap is reserved for future villager consumption (shows a hint only).
    /// Every litre poured in earns the team <see cref="moneyPerLitre"/> (shared, host-authoritative).
    /// </summary>
    public class GiantWineBottleController : WineryStation
    {
        [Header("Tuning (upgrade-ready)")]
        [SyncVar] public int capacity = 100;
        [Tooltip("Money the team earns for every litre of wine poured into the bottle.")]
        [SyncVar] public float moneyPerLitre = 0.1f;

        [Header("Visuals (from the GiantWineTank model)")]
        public Transform wineLiquid;          // WineLiquid mesh
        [Tooltip("Liquid surface height above the liquid's bottom when the bottle is 100% full (m). Keeps the wine inside the straight part of the glass.")]
        public float fullHeight = 3.9f;
        [Tooltip("Height at the first litre: the bottom of the liquid sits below the wooden deck, so this keeps 1 L visible (m).")]
        public float minHeight = 0.38f;
        public ParticleSystem pourStream;
        [Tooltip("Play the station's own stream above the funnel. Off: the player's bucket pours the wine in.")]
        public bool useStationPourStream = false;
        public float levelSmoothing = 0.6f;
        [Tooltip("Glass body: its shadow would darken the wine inside, so it is rendered without casting shadows.")]
        public Renderer glass;
        public Color liquidColor = new Color(0.58f, 0.05f, 0.13f, 1f);

        [SyncVar] int wine;
        [SyncVar] double lastPourTime = -999;
        [SyncVar] double money;

        public int Wine => wine;
        /// <summary>Team money earned from wine poured into the bottle.</summary>
        public double Money => money;
        /// <summary>The bottle in the scene (the HUD reads the team money from it).</summary>
        public static GiantWineBottleController Instance { get; private set; }

        void OnEnable() => Instance = this;
        void OnDisable() { if (Instance == this) Instance = null; }

        public static string FormatMoney(double m) => m.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        public float Fill => capacity > 0 ? wine / (float)capacity : 0f;

        Transform liquidPivot;
        float originalHeight = 1f;
        Vector3 liquidScale;
        float shownFill;
        bool built, firstFrame = true;

        void Awake() => Build();

        void Build()
        {
            if (built || !wineLiquid) return;
            built = true;
            var b = RendererBounds(wineLiquid);
            originalHeight = Mathf.Max(0.01f, b.size.y);
            liquidPivot = MakePivot(wineLiquid, new Vector3(b.center.x, b.min.y, b.center.z), "LiquidPivot (runtime)");
            liquidScale = liquidPivot.localScale;
            if (glass) glass.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var lr = wineLiquid.GetComponent<Renderer>();
            if (lr)
            {
                lr.receiveShadows = false;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var block = new MaterialPropertyBlock();
                block.SetColor("_BaseColor", liquidColor);
                lr.SetPropertyBlock(block);
            }
        }

        void Update()
        {
            Build();
            // Late joiners snap to the current level; afterwards the level eases toward the target.
            float fill = !firstFrame && VisualsHeld && Fill > shownFill ? shownFill : Fill;   // wine not poured in yet
            shownFill = firstFrame ? fill : Mathf.MoveTowards(shownFill, fill, Time.deltaTime * levelSmoothing * Mathf.Max(0.05f, Mathf.Abs(fill - shownFill) + 0.05f));
            firstFrame = false;
            if (liquidPivot)
            {
                bool any = shownFill > 0.0005f;
                liquidPivot.gameObject.SetActive(any);
                float h = Mathf.Lerp(minHeight, fullHeight, shownFill);
                liquidPivot.localScale = new Vector3(liquidScale.x, liquidScale.y * (h / originalHeight), liquidScale.z);
            }
            if (pourStream)
            {
                bool pouring = useStationPourStream && Now - lastPourTime < 1.2;
                if (pouring && !pourStream.isEmitting) pourStream.Play();
                else if (!pouring && pourStream.isEmitting) pourStream.Stop();
            }
        }

        /// <summary>The bucket pours into the copper funnel (whichever funnel part was clicked).</summary>
        public override Vector3 PourPoint(WineryInteractable target) =>
            target != null && target.part == InteractionPart.BottleFunnel ? PourPointOf("WineInput_Funnel", target) : base.PourPoint(target);

        string Status =>$"{wine} / {capacity} L · {Mathf.RoundToInt(Fill * 100)}% FULL";

        public override InteractionPrompt GetPrompt(WineryInteractable target, PlayerBucket bucket)
        {
            const string title = "Giant Wine Bottle";
            switch (target.part)
            {
                case InteractionPart.BottleFunnel:
                    if (wine >= capacity) return Blocked(title, Status, "Bottle Full", Fill);
                    if (bucket.Type != ResourceType.Wine)
                        return Blocked(title, Status, bucket.Type == ResourceType.Empty ? BringHint(bucket, "wine") : HoldsOther(bucket), Fill);
                    int pour = Mathf.Min(bucket.Amount, capacity - wine);
                    return Action(title, Status, $"Pour Wine Into Bottle — {pour} L (+{FormatMoney(pour * moneyPerLitre)} money)", Fill);
                case InteractionPart.BottleTap:
                    return Blocked("Wine Tap", Status, "For the villagers — coming soon", Fill);
                default:
                    return Blocked(title, Status, bucket.Type == ResourceType.Wine ? "Pour at the copper funnel" : "Fill me with wine!", Fill);
            }
        }

        [Server]
        public override string ServerInteract(WineryInteractable target, PlayerBucket bucket, out bool success, out string floatText)
        {
            success = false; floatText = null;
            if (target.part != InteractionPart.BottleFunnel) return null;
            if (wine >= capacity) return "Bottle Full";
            if (bucket.Type != ResourceType.Wine) return bucket.Type == ResourceType.Empty ? "Your bucket is empty" : "Wrong Resource Type";
            int n = bucket.ServerRemove(ResourceType.Wine, capacity - wine);
            if (n <= 0) return "Bottle Full";
            wine += n;
            lastPourTime = Now;
            double earned = n * (double)moneyPerLitre;
            money += earned;
            success = true; floatText = $"+{FormatMoney(earned)} Money";
            return wine >= capacity ? $"Poured {n} L (+{FormatMoney(earned)} money) — the bottle is FULL!"
                                    : $"Poured {n} L (+{FormatMoney(earned)} money) — bottle {Mathf.RoundToInt(Fill * 100)}% full";
        }

        /// <summary>Upgrade hook: bigger bottle.</summary>
        [Server] public void ServerSetCapacity(int c) => capacity = Mathf.Max(wine, Mathf.Max(1, c));
    }
}
