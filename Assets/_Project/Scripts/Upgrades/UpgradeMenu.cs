using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Vineyard.Building;
using Vineyard.Player;
using Vineyard.UI;
using Vineyard.Winery;

namespace Vineyard.Upgrades
{
    /// <summary>
    /// Upgrade panel of the local player (added at runtime by <see cref="PlayerInteractor"/>): a flat pop-up window
    /// in the middle of the screen, clicked with the mouse.
    ///
    ///  [U] while aiming at a fermenter  that machine: live state, what the upgrade does, current → next values
    ///  [U] anywhere else                 team upgrades (speed, jump, bucket)
    ///  [U] / [Esc] / ✕                   close
    ///
    /// The window is a list of <see cref="Row"/>s built every frame from live data, so new kinds of upgrades
    /// (or other things) are added by appending rows in <see cref="BuildRows"/>.
    /// </summary>
    public class UpgradeMenu : MonoBehaviour
    {
        /// <summary>One entry of the panel.</summary>
        class Row
        {
            public string title, description;
            public Texture2D icon;
            public int level, maxLevel;
            public float cost;
            public readonly List<UpgradeStat> stats = new List<UpgradeStat>();
            public Action buy;
        }

        public bool Open { get; private set; }

        PlayerInteractor interactor;
        PlayerBuilder builder;
        PlayerMotor motor;
        WineryStation station;               // null: team panel
        readonly List<Row> rows = new List<Row>();
        Vector2 scroll;
        float openAnim, pendingUntil;
        string message;
        bool messageOk;
        float messageTime = -99f;

        // ---- style ----
        GUIStyle sFootMsg, sTitle, sHeading, sBody, sStat, sStatNext, sButton, sButtonLabel, sClose, sSmall, sHint, sMoney, sIcon;
        Texture2D texButton, texButtonHover, texButtonDown, texButtonOff, texClose, texCloseHover;
        static readonly Color WindowBg = new Color(0.11f, 0.09f, 0.075f, 0.97f);
        static readonly Color HeaderBg = new Color(0.16f, 0.12f, 0.09f, 1f);
        static readonly Color RowBg = new Color(1f, 1f, 1f, 0.04f);
        static readonly Color Line = new Color(1f, 0.86f, 0.55f, 0.18f);
        static readonly Color Gold = new Color(1f, 0.86f, 0.55f);
        static readonly Color Text = new Color(0.93f, 0.9f, 0.84f);
        static readonly Color Muted = new Color(0.72f, 0.68f, 0.6f);
        static readonly Color Good = new Color(0.62f, 0.92f, 0.5f);
        static readonly Color Bad = new Color(1f, 0.5f, 0.42f);

        void Awake()
        {
            interactor = GetComponent<PlayerInteractor>();
            builder = GetComponent<PlayerBuilder>();
            motor = GetComponent<PlayerMotor>();
            if (interactor != null) interactor.Feedback += OnFeedback;
        }

        void OnDestroy()
        {
            if (interactor != null) interactor.Feedback -= OnFeedback;
            if (Open) UiFocus.Modal = false;
            foreach (var t in new[] { texButton, texButtonHover, texButtonDown, texButtonOff, texClose, texCloseHover })
                if (t != null) Destroy(t);
        }

        void OnDisable() { if (Open) SetOpen(false, null); }

        /// <summary>The host's answer to a purchase made in this panel.</summary>
        void OnFeedback(string msg, bool ok)
        {
            if (Time.unscaledTime > pendingUntil) return;
            pendingUntil = 0f;
            message = msg; messageOk = ok; messageTime = Time.unscaledTime;
        }

        // ------------------------------------------------------------------ open / close

        void Update()
        {
            if (interactor == null || !interactor.isLocalPlayer) return;
            var kb = Keyboard.current;
            bool locked = Cursor.lockState == CursorLockMode.Locked;

            if (Open)
            {
                if (kb != null && (kb.uKey.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame)) SetOpen(false, null);
                // The machine was removed / despawned while its panel was open.
                else if (station is object && station == null) SetOpen(false, null);
                return;
            }
            if (locked && kb != null && kb.uKey.wasPressedThisFrame)
            {
                var aimed = interactor.AimedUpgradeable as WineryStation;
                SetOpen(true, aimed);
            }
        }

        void SetOpen(bool on, WineryStation target)
        {
            Open = on;
            UiFocus.Modal = on;
            station = on ? target : null;
            if (on)
            {
                openAnim = 0f;
                scroll = Vector2.zero;
                message = null;
                if (builder != null) builder.ExitBuildMode();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        // ------------------------------------------------------------------ content

        static bool CanAfford(float cost)
        {
            var bank = GiantWineBottleController.Instance;
            return bank != null && bank.Money + 1e-6 >= cost;
        }

        string Title => station is IUpgradeable up ? up.UpgradeTitle : "Team Upgrades";

        string Subtitle => station is IUpgradeable up ? up.UpgradeStatus : "Bought with the team money — every player gets them.";

        /// <summary>Everything the panel lists right now. Add new rows here.</summary>
        void BuildRows()
        {
            rows.Clear();
            if (station is IUpgradeable up)
            {
                var r = new Row
                {
                    title = "Machine level", description = up.UpgradeDescription,
                    level = up.UpgradeLevel, maxLevel = up.MaxUpgradeLevel, cost = up.NextUpgradeCost,
                    buy = () => interactor.RequestUpgradeStation(station),
                };
                r.stats.AddRange(up.UpgradeStats);
                rows.Add(r);
                return;
            }

            var team = TeamUpgrades.Instance;
            if (team == null) return;
            foreach (TeamUpgrade u in new[] { TeamUpgrade.Speed, TeamUpgrade.Jump, TeamUpgrade.Bucket })
            {
                var track = team.Get(u);
                bool max = team.IsMax(u);
                var r = new Row
                {
                    title = string.IsNullOrEmpty(track.label) ? u.ToString() : track.label,
                    description = team.Description(u), icon = track.icon,
                    level = team.Level(u), maxLevel = TeamUpgrades.MaxLevel, cost = team.NextCost(u),
                };
                var captured = u;
                r.buy = () => interactor.RequestTeamUpgrade(captured);
                float now = team.Value(u), next = team.NextValue(u);
                switch (u)
                {
                    case TeamUpgrade.Speed:
                        float walk = motor ? motor.walkSpeed : 5f, sprint = motor ? motor.sprintSpeed : 8.5f;
                        r.stats.Add(new UpgradeStat("Walk", $"{walk * now:0.0} m/s", max ? null : $"{walk * next:0.0} m/s"));
                        r.stats.Add(new UpgradeStat("Sprint", $"{sprint * now:0.0} m/s", max ? null : $"{sprint * next:0.0} m/s"));
                        break;
                    case TeamUpgrade.Jump:
                        float jh = motor ? motor.jumpHeight : 1.2f;
                        r.stats.Add(new UpgradeStat("Jump height", $"{jh * now:0.0} m", max ? null : $"{jh * next:0.0} m"));
                        break;
                    default:
                        r.stats.Add(new UpgradeStat("Bucket size", $"{Mathf.RoundToInt(now)} L", max ? null : $"{Mathf.RoundToInt(next)} L"));
                        break;
                }
                rows.Add(r);
            }
        }

        // ------------------------------------------------------------------ drawing

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        void Styles(float ui)
        {
            if (sTitle == null)
            {
                texButton = Solid(new Color(0.55f, 0.36f, 0.12f));
                texButtonHover = Solid(new Color(0.68f, 0.46f, 0.16f));
                texButtonDown = Solid(new Color(0.45f, 0.29f, 0.1f));
                texButtonOff = Solid(new Color(1f, 1f, 1f, 0.07f));
                texClose = Solid(new Color(1f, 1f, 1f, 0.06f));
                texCloseHover = Solid(new Color(0.7f, 0.2f, 0.15f));
                sTitle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                sHeading = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                sBody = new GUIStyle(GUI.skin.label) { wordWrap = true };
                sStat = new GUIStyle(GUI.skin.label);
                sStatNext = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                sSmall = new GUIStyle(GUI.skin.label) { wordWrap = true };
                sMoney = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
                sIcon = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                sButton = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, border = new RectOffset(0, 0, 0, 0) };
                sButton.normal.background = texButton; sButton.hover.background = texButtonHover; sButton.active.background = texButtonDown;
                sButton.normal.textColor = sButton.hover.textColor = sButton.active.textColor = Color.white;
                sButtonLabel = new GUIStyle(sButton);
                sButtonLabel.normal.background = sButtonLabel.hover.background = sButtonLabel.active.background = null;
                sHint = new GUIStyle(sSmall) { wordWrap = false, alignment = TextAnchor.MiddleRight };
                sFootMsg = new GUIStyle(sSmall) { wordWrap = false, alignment = TextAnchor.MiddleLeft };
                sClose = new GUIStyle(sButton);
                sClose.normal.background = texClose; sClose.hover.background = texCloseHover; sClose.active.background = texCloseHover;
                sClose.normal.textColor = Muted;
            }
            sTitle.fontSize = Mathf.RoundToInt(22 * ui);
            sHeading.fontSize = Mathf.RoundToInt(17 * ui);
            sBody.fontSize = Mathf.RoundToInt(13 * ui);
            sStat.fontSize = sStatNext.fontSize = Mathf.RoundToInt(14 * ui);
            sSmall.fontSize = sHint.fontSize = sFootMsg.fontSize = Mathf.RoundToInt(12 * ui);
            sMoney.fontSize = Mathf.RoundToInt(18 * ui);
            sIcon.fontSize = Mathf.RoundToInt(26 * ui);
            sButton.fontSize = sButtonLabel.fontSize = Mathf.RoundToInt(15 * ui);
            sClose.fontSize = Mathf.RoundToInt(16 * ui);
        }

        static void Box(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }

        static void Label(Rect r, string text, GUIStyle style, Color color)
        {
            var o = style.normal.textColor;
            style.normal.textColor = color;
            GUI.Label(r, text, style);
            style.normal.textColor = o;
        }

        void OnGUI()
        {
            if (!Open || interactor == null || !interactor.isLocalPlayer) return;
            float ui = Mathf.Clamp(Screen.height / 1080f, 0.75f, 1.6f);
            Styles(ui);
            if (Event.current.type == EventType.Layout) BuildRows();

            if (Event.current.type == EventType.Repaint) openAnim = Mathf.MoveTowards(openAnim, 1f, Time.unscaledDeltaTime * 8f);
            float a = Mathf.SmoothStep(0f, 1f, openAnim);
            var oldColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, a);

            float w = Screen.width, h = Screen.height;
            Box(new Rect(0, 0, w, h), new Color(0f, 0f, 0f, 0.35f * a));   // dim the game behind the panel

            float pad = 18f * ui, headerH = 58f * ui, subH = 30f * ui, footerH = 34f * ui;
            float rowH = 132f * ui, rowGap = 8f * ui;
            float contentH = rows.Count * rowH + Mathf.Max(0, rows.Count - 1) * rowGap;
            float winW = Mathf.Min(720f * ui, w - 40f);
            float winH = Mathf.Min(headerH + subH + pad * 2 + contentH + footerH, h - 60f);
            Rect win = new Rect((w - winW) / 2f, (h - winH) / 2f + (1f - a) * 20f * ui, winW, winH);

            Box(win, WindowBg);
            Box(new Rect(win.x, win.y, win.width, headerH), HeaderBg);
            Box(new Rect(win.x, win.y + headerH - 2f, win.width, 2f), Gold);

            // Header: title, team money, close.
            Label(new Rect(win.x + pad, win.y, win.width * 0.6f, headerH), Title, sTitle, Text);
            float closeS = 34f * ui;
            var bank = GiantWineBottleController.Instance;
            Label(new Rect(win.x, win.y, win.width - pad - closeS - 14f * ui, headerH),
                  bank != null ? $"{GiantWineBottleController.FormatMoney(bank.Money)} money" : "", sMoney, Gold);
            if (GUI.Button(new Rect(win.xMax - pad - closeS, win.y + (headerH - closeS) / 2f, closeS, closeS), "X", sClose))
            {
                GUI.color = oldColor;
                SetOpen(false, null);
                return;
            }

            // Subtitle: live machine state / what the page is.
            Label(new Rect(win.x + pad, win.y + headerH + 4f * ui, win.width - pad * 2, subH), Subtitle, sSmall, Muted);

            // Rows (scrolls when the list grows).
            Rect view = new Rect(win.x + pad, win.y + headerH + subH + pad * 0.5f, win.width - pad * 2, win.height - headerH - subH - footerH - pad);
            bool scrolls = contentH > view.height;
            Rect inner = new Rect(0, 0, view.width - (scrolls ? 16f : 0f), contentH);
            scroll = GUI.BeginScrollView(view, scroll, inner, false, false);
            for (int i = 0; i < rows.Count; i++)
                DrawRow(rows[i], new Rect(0, i * (rowH + rowGap), inner.width, rowH), ui);
            GUI.EndScrollView();

            // Footer: result of the last purchase, close hint.
            Rect foot = new Rect(win.x + pad, win.yMax - footerH, win.width - pad * 2, footerH);
            Box(new Rect(win.x, foot.y - 1f, win.width, 1f), Line);
            float mAge = Time.unscaledTime - messageTime;
            if (message != null && mAge < 3f) Label(foot, message, sFootMsg, messageOk ? Good : Bad);
            Label(foot, "[U] / [Esc] close", sHint, Muted);

            GUI.color = oldColor;
        }

        void DrawRow(Row r, Rect rect, float ui)
        {
            Box(rect, RowBg);
            float pad = 12f * ui;
            bool max = r.level >= r.maxLevel;
            bool afford = max || CanAfford(r.cost);

            // Icon (or the first letter until an icon is assigned).
            float iconS = rect.height - pad * 2;
            Rect icon = new Rect(rect.x + pad, rect.y + pad, iconS * 0.8f, iconS);
            Box(icon, new Color(0f, 0f, 0f, 0.25f));
            if (r.icon != null) GUI.DrawTexture(icon, r.icon, ScaleMode.ScaleToFit, true);
            else Label(icon, string.IsNullOrEmpty(r.title) ? "?" : r.title.Substring(0, 1).ToUpperInvariant(), sIcon, Gold);

            // Buy button on the right.
            float btnW = 150f * ui, btnH = 44f * ui;
            Rect btn = new Rect(rect.xMax - pad - btnW, rect.y + (rect.height - btnH) / 2f, btnW, btnH);

            float x = icon.xMax + pad * 1.4f;
            float textW = btn.x - pad - x;

            // Title + level dots.
            Label(new Rect(x, rect.y + pad * 0.6f, textW, 24f * ui), r.title, sHeading, Text);
            var titleSize = sHeading.CalcSize(new GUIContent(r.title));
            float dot = 10f * ui, gap = 6f * ui, dx = x + titleSize.x + 12f * ui, dy = rect.y + pad * 0.6f + 12f * ui - dot / 2f;
            for (int d = 0; d < r.maxLevel; d++)
            {
                Rect dr = new Rect(dx + d * (dot + gap), dy, dot, dot);
                Box(dr, new Color(0f, 0f, 0f, 0.45f));
                if (d < r.level) Box(new Rect(dr.x + 2, dr.y + 2, dr.width - 4, dr.height - 4), Gold);
            }
            Label(new Rect(dx + r.maxLevel * (dot + gap) + 4f * ui, rect.y + pad * 0.6f, 120f * ui, 24f * ui),
                  max ? "MAX" : $"Lv {r.level}", sSmall, max ? Good : Muted);

            // What it does.
            float descH = 36f * ui;
            Label(new Rect(x, rect.y + pad * 0.6f + 26f * ui, textW, descH), r.description, sBody, Muted);

            // Current → next values.
            float sy = rect.y + pad * 0.6f + 26f * ui + descH + 2f * ui, lineH = 19f * ui;
            float labelW = Mathf.Min(120f * ui, textW * 0.38f);
            foreach (var s in r.stats)
            {
                if (sy + lineH > rect.yMax - 2f) break;
                Label(new Rect(x, sy, labelW, lineH), s.label, sStat, Muted);
                Label(new Rect(x + labelW, sy, textW - labelW, lineH), s.now, sStat, Text);
                if (s.next != null)
                {
                    float nw = sStat.CalcSize(new GUIContent(s.now)).x;
                    Label(new Rect(x + labelW + nw + 6f * ui, sy, textW, lineH), $"→  {s.next}", sStatNext, Good);
                }
                sy += lineH;
            }

            // Button: price, MAX, or not enough money.
            if (max)
            {
                Box(btn, new Color(1f, 1f, 1f, 0.05f));
                Label(btn, "MAX LEVEL", sButtonLabel, Good);
            }
            else if (!afford)
            {
                Box(btn, new Color(1f, 1f, 1f, 0.05f));
                Label(btn, $"Need {GiantWineBottleController.FormatMoney(r.cost)}", sButtonLabel, Bad);
            }
            else if (GUI.Button(btn, $"Upgrade   {GiantWineBottleController.FormatMoney(r.cost)}", sButton))
            {
                pendingUntil = Time.unscaledTime + 2f;
                r.buy?.Invoke();
            }
        }
    }
}
