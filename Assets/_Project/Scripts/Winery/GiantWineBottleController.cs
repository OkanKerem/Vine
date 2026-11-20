using Mirror;
using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// The giant bottle in the square: final storage for wine. Players pour wine into the funnel; the host
    /// stores it and every client raises the bottle's WineLiquid mesh to the same level.
    /// The output tap is reserved for future villager consumption (shows a hint only).
    /// </summary>
    public class GiantWineBottleController : WineryStation
    {
        [Header("Tuning (upgrade-ready)")]
        [SyncVar] public int capacity = 100;

        [Header("Visuals (from the GiantWineTank model)")]
        public Transform wineLiquid;          // WineLiquid mesh
        [Tooltip("Liquid surface height above the liquid's bottom when the bottle is 100% full (m). Keeps the wine inside the straight part of the glass.")]
        public float fullHeight = 3.9f;
        [Tooltip("Height at the first litre: the bottom of the liquid sits below the wooden deck, so this keeps 1 L visible (m).")]
        public float minHeight = 0.38f;
        public ParticleSystem pourStream;
        public float levelSmoothing = 0.6f;
        [Tooltip("Glass body: its shadow would darken the wine inside, so it is rendered without casting shadows.")]
        public Renderer glass;
        public Color liquidColor = new Color(0.58f, 0.05f, 0.13f, 1f);

        [SyncVar] int wine;
        [SyncVar] double lastPourTime = -999;

        public int Wine => wine;
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
            shownFill = firstFrame ? Fill : Mathf.MoveTowards(shownFill, Fill, Time.deltaTime * levelSmoothing * Mathf.Max(0.05f, Mathf.Abs(Fill - shownFill) + 0.05f));
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
                bool pouring = Now - lastPourTime < 1.2;
                if (pouring && !pourStream.isEmitting) pourStream.Play();
                else if (!pouring && pourStream.isEmitting) pourStream.Stop();
            }
        }

        string Status => $"{wine} / {capacity} L · {Mathf.RoundToInt(Fill * 100)}% FULL";

        public override InteractionPrompt GetPrompt(WineryInteractable target, PlayerBucket bucket)
        {
            const string title = "Giant Wine Bottle";
            switch (target.part)
            {
                case InteractionPart.BottleFunnel:
                    if (wine >= capacity) return Blocked(title, Status, "Bottle Full", Fill);
                    if (bucket.Type != ResourceType.Wine)
                        return Blocked(title, Status, bucket.Type == ResourceType.Empty ? BringHint(bucket, "wine") : HoldsOther(bucket), Fill);
                    return Action(title, Status, $"Pour Wine Into Bottle — {bucket.Amount} L", Fill);
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
            success = true; floatText = $"+{n} L Wine";
            return wine >= capacity ? $"Poured {n} L — the bottle is FULL!" : $"Poured {n} L — bottle {Mathf.RoundToInt(Fill * 100)}% full";
        }

        /// <summary>Upgrade hook: bigger bottle.</summary>
        [Server] public void ServerSetCapacity(int c) => capacity = Mathf.Max(wine, Mathf.Max(1, c));
    }
}
