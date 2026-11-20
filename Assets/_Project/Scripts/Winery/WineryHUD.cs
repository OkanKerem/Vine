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
        public float messageSeconds = 2.2f;

        PlayerInteractor interactor;
        PlayerBucket bucket;

        struct Message { public string text; public bool good; public float time; }
        struct Floater { public Vector3 pos; public string text; public float time; }
        readonly List<Message> messages = new List<Message>();
        readonly List<Floater> floaters = new List<Floater>();

        GUIStyle title, body, small, center, floatStyle, msgStyle;
        Texture2D white;
        float bucketPulse;
        int lastAmount = -1;

        static readonly Color Panel = new Color(0.08f, 0.06f, 0.05f, 0.62f);
        static readonly Color Gold = new Color(1f, 0.86f, 0.55f);

        void Awake()
        {
            interactor = GetComponent<PlayerInteractor>();
            bucket = GetComponent<PlayerBucket>();
            interactor.Feedback += (m, ok) => { messages.Add(new Message { text = m, good = ok, time = Time.unscaledTime }); if (messages.Count > 3) messages.RemoveAt(0); };
            interactor.FloatingText += (p, t) => floaters.Add(new Floater { pos = p, text = t, time = Time.unscaledTime });
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
            floatStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            msgStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = true };
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
                Shadowed(new Rect(r.x + 14, r.y + 6, 200, 20), "<b>BUCKET</b>", center, new Color(0.85f, 0.82f, 0.75f));
                string label = t == ResourceType.Empty ? "EMPTY" : t.Label().ToUpperInvariant();
                Shadowed(new Rect(r.x + 14, r.y + 28, 210, 24), $"<size={18 + Mathf.RoundToInt(bucketPulse * 4)}><b>{label}</b></size>", center, ColorOf(t));
                Shadowed(new Rect(r.x + 14, r.y + 52, 210, 20), $"{bucket.Amount} / {bucket.Capacity}{(t.IsLiquid() ? " L" : "")}", center, Color.white);
                Bar(new Rect(r.x + 14, r.y + 76, r.width - 28, 12), bucket.Fill, ColorOf(t));
            }

            // Feedback messages (above the bucket line, centre).
            float now = Time.unscaledTime;
            messages.RemoveAll(m => now - m.time > messageSeconds);
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[messages.Count - 1 - i];
                float a = Mathf.Clamp01((messageSeconds - (now - m.time)) / 0.4f);
                Color c = m.good ? new Color(0.75f, 1f, 0.7f, a) : new Color(1f, 0.6f, 0.5f, a);
                Shadowed(new Rect(w / 2 - 300, h - 150 - i * 28, 600, 28), m.text, msgStyle, c);
            }

            // Floating texts rising from the station.
            var cam = Camera.current != null ? Camera.current : Camera.main;
            floaters.RemoveAll(f => now - f.time > 1.6f);
            var playerCam = GetComponentInChildren<Camera>();
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
