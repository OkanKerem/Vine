using System.Collections.Generic;
using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// Compact gameplay HUD for the local player (IMGUI, like the existing connection panel):
    /// crosshair, interaction prompt with status / progress, bucket contents, feedback messages and
    /// floating "+8 Grapes" texts. Draws nothing for remote players.
    /// </summary>
    [RequireComponent(typeof(PlayerInteractor))]
    public class WineryHUD : MonoBehaviour
    {
        PlayerInteractor interactor;
        PlayerBucket bucket;
        Vineyard.Building.PlayerBuilder builder;

        struct Floater { public Vector3 pos; public string text; public float time; }
        readonly List<Floater> floaters = new List<Floater>();

        GUIStyle title, body, small, center, right, floatStyle;
        Texture2D white;
        float bucketPulse, moneyPulse;
        int lastAmount = -1;
        double lastMoney = -1;
        Camera playerCam;
        readonly OnboardingHints hints = new OnboardingHints();
        GUIStyle hintStyle, hintLabel;

        static readonly Color Panel = new Color(0.08f, 0.06f, 0.05f, 0.62f);
        static readonly Color Gold = new Color(1f, 0.86f, 0.55f);

        void Awake()
        {
            interactor = GetComponent<PlayerInteractor>();
            bucket = GetComponent<PlayerBucket>();
            builder = GetComponent<Vineyard.Building.PlayerBuilder>();
            interactor.FloatingText += (p, t) => floaters.Add(new Floater { pos = p, text = t, time = Time.unscaledTime });
        }

        void Update()
        {
            if (interactor != null && interactor.isLocalPlayer) hints.Tick(interactor, bucket);
        }

        static Color ColorOf(ResourceType t)
        {
            switch (t)
            {
                case ResourceType.Grapes: return new Color(0.72f, 0.45f, 0.95f);
                case ResourceType.GrapeJuice: return new Color(0.85f, 0.45f, 0.85f);
                case ResourceType.Wine: return new Color(0.95f, 0.35f, 0.4f);
                default: return new Color(0.8f, 0.8f, 0.8f);
            }
        }

        void Styles()
        {
            if (white != null) return;
            white = Texture2D.whiteTexture;
            title = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = true };
            body = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter, richText = true };
            small = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, richText = true };
            center = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleLeft, richText = true };
            right = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleRight, richText = true };
            floatStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.UpperLeft, wordWrap = true, richText = false };
            hintLabel = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
        }

        void Box(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, white); GUI.color = old; }

        void Shadowed(Rect r, string text, GUIStyle style, Color color)
        {
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, color.a * 0.75f);
            GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, style);
            GUI.color = color;
            GUI.Label(r, text, style);
            GUI.color = old;
        }

        void Bar(Rect r, float t, Color fill)
        {
            Box(r, new Color(0f, 0f, 0f, 0.55f));
            Box(new Rect(r.x + 2, r.y + 2, (r.width - 4) * Mathf.Clamp01(t), r.height - 4), fill);
        }

        void OnGUI()
        {
            if (interactor == null || !interactor.isLocalPlayer) return;
            Styles();
            float w = Screen.width, h = Screen.height;
            var prompt = interactor.CurrentPrompt;
            bool hasTarget = interactor.Current != null;

            // Crosshair (grows and turns gold when something is clickable).
            float cs = hasTarget ? (prompt.actionable ? 9f : 7f) : 4f;
            Color cc = hasTarget ? (prompt.actionable ? Gold : new Color(1f, 1f, 1f, 0.8f)) : new Color(1f, 1f, 1f, 0.7f);
            Box(new Rect(w / 2 - cs / 2 - 1, h / 2 - 1.5f, cs + 2, 3), new Color(0, 0, 0, 0.5f));
            Box(new Rect(w / 2 - 1.5f, h / 2 - cs / 2 - 1, 3, cs + 2), new Color(0, 0, 0, 0.5f));
            Box(new Rect(w / 2 - cs / 2, h / 2 - 0.5f, cs, 1), cc);
            Box(new Rect(w / 2 - 0.5f, h / 2 - cs / 2, 1, cs), cc);

            // Interaction prompt under the crosshair.
            if (hasTarget)
            {
                float pw = 380f, y = h / 2 + 34f;
                bool bar = prompt.progress >= 0f;
                float ph = bar ? 104f : 86f;
                Box(new Rect(w / 2 - pw / 2, y, pw, ph), Panel);
                Shadowed(new Rect(w / 2 - pw / 2, y + 4, pw, 26), prompt.title, title, Color.white);
                string action = prompt.actionable ? $"<color=#FFD98A>[LMB]</color>  {prompt.action}" : $"<color=#FF9A80>{prompt.blockReason}</color>";
                Shadowed(new Rect(w / 2 - pw / 2, y + 30, pw, 24), action, body, Color.white);
                Shadowed(new Rect(w / 2 - pw / 2, y + 54, pw, 22), prompt.status, small, new Color(0.9f, 0.88f, 0.82f));
                if (bar) Bar(new Rect(w / 2 - pw / 2 + 20, y + 80, pw - 40, 14), prompt.progress, new Color(0.62f, 0.15f, 0.3f));

                // Upgradeable machine: [U]  ● ● ○  price (or MAX).
                var up = interactor.AimedUpgradeable;
                if (up != null)
                {
                    int max = up.MaxUpgradeLevel, lvl = up.UpgradeLevel;
                    Rect ur = new Rect(w / 2 - 110, y + ph + 6, 220, 30);
                    Box(ur, Panel);
                    bool maxed = lvl >= max;
                    var bottleNow = GiantWineBottleController.Instance;
                    bool afford = maxed || (bottleNow != null && bottleNow.Money + 1e-6 >= up.NextUpgradeCost);
                    Shadowed(new Rect(ur.x + 10, ur.y, 40, ur.height), maxed ? "" : "<color=#FFD98A>[U]</color>", center, Color.white);
                    float dot = 11f, gap = 7f, dx = ur.x + 62;
                    for (int d = 0; d < max; d++)
                    {
                        Rect dr = new Rect(dx + d * (dot + gap), ur.y + ur.height / 2 - dot / 2, dot, dot);
                        Box(dr, new Color(0f, 0f, 0f, 0.5f));
                        if (d < lvl) Box(new Rect(dr.x + 2, dr.y + 2, dr.width - 4, dr.height - 4), Gold);
                    }
                    Shadowed(new Rect(ur.x, ur.y, ur.width - 12, ur.height),
                             maxed ? "<b>MAX</b>" : $"<b>{GiantWineBottleController.FormatMoney(up.NextUpgradeCost)}</b>", right,
                             maxed ? new Color(0.75f, 1f, 0.6f) : afford ? Gold : new Color(1f, 0.55f, 0.45f));
                }
            }

            // Bucket (bottom-left).
            if (bucket != null)
            {
                if (bucket.Amount != lastAmount) { if (lastAmount >= 0) bucketPulse = 1f; lastAmount = bucket.Amount; }
                bucketPulse = Mathf.MoveTowards(bucketPulse, 0f, Time.unscaledDeltaTime * 2.5f);
                var t = bucket.Type;
                Rect r = new Rect(20, h - 118, 230, 98);
                Box(r, Panel);
                Box(new Rect(r.x, r.y, 4, r.height), ColorOf(t));
                bool hammer = builder != null && builder.HasHammer;
                Shadowed(new Rect(r.x + 14, r.y + 6, 200, 20), hammer ? "<b>HAND</b>" : "<b>BUCKET</b>", center, new Color(0.85f, 0.82f, 0.75f));
                if (hammer)
                {
                    Shadowed(new Rect(r.x + 14, r.y + 6, r.width - 28, 20), "<color=#FFD98A>[Q]</color> Put down", right, new Color(0.85f, 0.82f, 0.75f));
                    Shadowed(new Rect(r.x + 14, r.y + 28, 210, 24), "<size=18><b>HAMMER</b></size>", center, Gold);
                    Shadowed(new Rect(r.x + 14, r.y + 54, 210, 36), builder.BuildMode ? "<color=#FFD98A>[B]</color> Exit build mode" : "<color=#FFD98A>[B]</color> Build mode", center, new Color(0.85f, 0.82f, 0.75f));
                }
                else if (bucket.HasBucket && !bucket.IsPouring)
                    Shadowed(new Rect(r.x + 14, r.y + 6, r.width - 28, 20),
                             (bucket.Amount > 0 ? "<color=#FFD98A>[G]</color> Empty   " : "") + "<color=#FFD98A>[Q]</color> Put down", right, new Color(0.85f, 0.82f, 0.75f));
                if (hammer) { }
                else if (!bucket.HasBucket)
                {
                    Shadowed(new Rect(r.x + 14, r.y + 28, 210, 24), "<size=18><b>HANDS FREE</b></size>", center, new Color(0.8f, 0.8f, 0.8f));
                    Shadowed(new Rect(r.x + 14, r.y + 54, 210, 36), "Click a bucket or hammer", center, new Color(0.85f, 0.82f, 0.75f));
                }
                else
                {
                    string label = t == ResourceType.Empty ? "EMPTY" : t.Label().ToUpperInvariant();
                    Shadowed(new Rect(r.x + 14, r.y + 28, 210, 24), $"<size={18 + Mathf.RoundToInt(bucketPulse * 4)}><b>{label}</b></size>", center, ColorOf(t));
                    Shadowed(new Rect(r.x + 14, r.y + 52, 210, 20), $"{bucket.Amount} / {bucket.Capacity}{(t.IsLiquid() ? " L" : "")}", center, Color.white);
                    Bar(new Rect(r.x + 14, r.y + 76, r.width - 28, 12), bucket.Fill, ColorOf(t));
                }
            }

            // Team money (top-right).
            var bottle = GiantWineBottleController.Instance;
            if (bottle != null)
            {
                double m = bottle.Money;
                if (m != lastMoney) { if (lastMoney >= 0) moneyPulse = 1f; lastMoney = m; }
                moneyPulse = Mathf.MoveTowards(moneyPulse, 0f, Time.unscaledDeltaTime * 2f);
                Rect r = new Rect(w - 190, 20, 170, 56);
                Box(r, Panel);
                Box(new Rect(r.xMax - 4, r.y, 4, r.height), Gold);
                Shadowed(new Rect(r.x + 14, r.y + 4, 150, 18), "<b>MONEY</b>", center, new Color(0.85f, 0.82f, 0.75f));
                Shadowed(new Rect(r.x + 14, r.y + 22, 150, 30),
                         $"<size={20 + Mathf.RoundToInt(moneyPulse * 5)}><b>{GiantWineBottleController.FormatMoney(m)}</b></size>", center, Gold);
            }

            float now = Time.unscaledTime;

            // One-time tip (top-centre): fades and slides in, holds, fades out.
            if (hints.Visible)
            {
                float age = now - hints.ShownAt;
                float a = Mathf.Clamp01(age / 0.25f) * Mathf.Clamp01((OnboardingHints.Duration - age) / 0.6f);
                float slide = (1f - Mathf.Clamp01(age / 0.25f)) * -10f;
                float tw = Mathf.Min(520f, w - 40f), pad = 16f;
                float textH = hintStyle.CalcHeight(new GUIContent(hints.Text), tw - pad * 2 - 4);
                Rect r = new Rect(w / 2 - tw / 2, 28f + slide, tw, textH + 40f);
                Box(r, new Color(Panel.r, Panel.g, Panel.b, Panel.a * a));
                Box(new Rect(r.x, r.y, 4, r.height), new Color(Gold.r, Gold.g, Gold.b, a));
                Shadowed(new Rect(r.x + pad + 4, r.y + 8, tw - pad * 2, 16), "TIP", hintLabel, new Color(Gold.r, Gold.g, Gold.b, a));
                Shadowed(new Rect(r.x + pad + 4, r.y + 26, tw - pad * 2 - 4, textH), hints.Text, hintStyle, new Color(1f, 0.97f, 0.9f, a));
                // Thin timer line so the player knows it will go away by itself.
                float left = Mathf.Clamp01(1f - age / OnboardingHints.Duration);
                Box(new Rect(r.x + 4, r.yMax - 2, (r.width - 4) * left, 2), new Color(Gold.r, Gold.g, Gold.b, 0.5f * a));
            }

            // Floating texts rising from the station.
            var cam = Camera.current != null ? Camera.current : Camera.main;
            floaters.RemoveAll(f => now - f.time > 1.6f);
            if (playerCam == null) playerCam = GetComponentInChildren<Camera>();   // cached: OnGUI runs several times per frame
            if (playerCam != null && playerCam.enabled) cam = playerCam;
            if (cam != null)
                foreach (var f in floaters)
                {
                    float age = now - f.time;
                    Vector3 sp = cam.WorldToScreenPoint(f.pos + Vector3.up * age * 0.6f);
                    if (sp.z <= 0) continue;
                    Color c = f.text.StartsWith("-") ? new Color(1f, 0.85f, 0.6f) : new Color(0.8f, 1f, 0.6f);
                    c.a = Mathf.Clamp01((1.6f - age) / 0.5f);
                    Shadowed(new Rect(sp.x - 120, h - sp.y - 16, 240, 32), f.text, floatStyle, c);
                }
        }
    }
}
