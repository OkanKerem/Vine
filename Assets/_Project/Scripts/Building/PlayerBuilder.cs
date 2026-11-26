using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Vineyard.Player;
using Vineyard.Winery;

namespace Vineyard.Building
{
    /// <summary>
    /// Hammer + build mode for the first-person player.
    ///
    /// Hands hold a single item (bucket OR hammer). The hammer lies on the ground as a <see cref="WorldHammer"/>:
    /// click it to pick it up (a carried bucket is swapped onto the ground), [Q] puts it down again.
    ///
    ///  [B]          enter / leave build mode — only while holding the hammer
    ///  Mouse wheel  pick what to build (icon bar at the bottom), [1]-[9] also work
    ///  [R]          turn 45° (Shift+R the other way), hold [Q]/[E] to turn smoothly
    ///  [LMB]        build at the preview (paid from the team money)
    ///
    /// The preview follows a centre-screen raycast: transparent white where the build fits, transparent red where
    /// it does not. The host re-checks place, range and money before spawning (<see cref="CmdBuild"/>).
    /// </summary>
    [RequireComponent(typeof(PlayerBucket))]
    public class PlayerBuilder : NetworkBehaviour
    {
        [Header("Hammer model")]
        [Tooltip("First-person hammer under the camera (local player only).")]
        public GameObject hammerView;
        [Tooltip("Hammer in the character's hand that other players see.")]
        public GameObject hammerWorld;

        [Header("Placement")]
        [Tooltip("How far the build ray reaches from the camera (m).")]
        public float maxBuildDistance = 12f;
        [Tooltip("Extra distance the host tolerates (latency).")]
        public float serverTolerance = 2f;
        [Tooltip("Degrees per [R] press.")]
        public float rotateStep = 45f;
        [Tooltip("Degrees per second while [Q]/[E] is held.")]
        public float rotateSpeed = 120f;
        [Tooltip("Minimum time between two requests from one player (host side, s).")]
        public float serverMinInterval = 0.3f;

        [SyncVar] bool hasHammer;

        /// <summary>The hammer is in hand (replicated: everyone sees it, the host checks it).</summary>
        public bool HasHammer => hasHammer;
        public bool BuildMode { get; private set; }
        public int Selected { get; private set; }

        NetworkPlayer player;
        PlayerBucket bucket;
        PlayerInteractor interactor;

        // ---- local preview state ----
        float yaw;
        bool yawInitialised;
        bool hasPreview, previewValid;
        Vector3 previewPos;
        Quaternion previewRot = Quaternion.identity;
        bool wasLocked;
        float nextBuildTime;
        readonly RaycastHit[] rayHits = new RaycastHit[16];
        Material fallbackValid, fallbackInvalid;

        // ---- hammer animation ----
        float swing;
        Vector3 viewRestPos;
        Quaternion viewRestRot;
        bool viewRestCached;

        // ---- server ----
        double lastServerRequest;

        // ---- icon bar animation ----
        float barSlot;          // smoothly follows Selected
        float barOpen;          // 0..1 slide-in
        float pop;              // punch after a build / selection change
        float deny;             // shake after a refused click
        Texture2D glow;

        void Awake()
        {
            player = GetComponent<NetworkPlayer>();
            bucket = GetComponent<PlayerBucket>();
            interactor = GetComponent<PlayerInteractor>();
            if (hammerView) hammerView.SetActive(false);
            if (hammerWorld) hammerWorld.SetActive(false);
        }

        public override void OnStartLocalPlayer()
        {
            if (hammerWorld)
                foreach (var r in hammerWorld.GetComponentsInChildren<Renderer>(true))
                    r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;   // the local player sees the first-person one
        }

        public override void OnStopLocalPlayer()
        {
            BuildMode = false;
            if (hammerView) hammerView.SetActive(false);
        }

        // ================================================================== single slot (host)

        [Server] public void ServerGiveHammer() => hasHammer = true;

        [Server]
        public bool ServerTakeHammer()
        {
            if (!hasHammer) return false;
            hasHammer = false;
            return true;
        }

        /// <summary>Q while holding the hammer: put it on the ground in front of the player.</summary>
        public void RequestDropHammer()
        {
            if (hasHammer && !BuildMode) CmdDropHammer();
        }

        [Command]
        void CmdDropHammer()
        {
            if (NetworkTime.time - lastServerRequest < serverMinInterval) return;
            lastServerRequest = NetworkTime.time;
            if (!hasHammer || WorldHammer.Prefab == null) return;
            Vector3 spot = WorldBucket.FindGroundSpot(transform.position, transform.forward, 0.85f);
            hasHammer = false;
            WorldHammer.ServerSpawn(spot, transform.eulerAngles.y + 90f);
        }

        // ================================================================== per frame

        void Update()
        {
            bool held = hasHammer;
            if (bucket) bucket.HiddenByTool = held;
            if (hammerWorld && hammerWorld.activeSelf != held) hammerWorld.SetActive(held);
            if (!isLocalPlayer) return;

            if (hammerView && hammerView.activeSelf != held) hammerView.SetActive(held);
            if (!held && BuildMode) BuildMode = false;

            HandleInput();
            if (BuildMode) UpdatePreview();
            else hasPreview = false;
            AnimateHammer();
        }

        void LateUpdate()
        {
            if (isLocalPlayer && BuildMode && hasPreview) DrawPreview();
        }

        void HandleInput()
        {
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (locked && kb != null)
            {
                if (kb.bKey.wasPressedThisFrame && hasHammer) SetBuildMode(!BuildMode);
                if (BuildMode)
                {
                    bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
                    if (kb.rKey.wasPressedThisFrame) yaw += shift ? -rotateStep : rotateStep;
                    float turn = (kb.eKey.isPressed ? 1f : 0f) - (kb.qKey.isPressed ? 1f : 0f);
                    yaw = Mathf.Repeat(yaw + turn * rotateSpeed * Time.deltaTime, 360f);
                    int count = BuildCatalog.Instance ? BuildCatalog.Instance.entries.Count : 0;
                    for (int i = 0; i < Mathf.Min(count, 9); i++)
                        if (kb[Key.Digit1 + i].wasPressedThisFrame) Select(i);
                }
            }
            if (BuildMode && locked && mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll > 0.01f) Select(Selected - 1);
                else if (scroll < -0.01f) Select(Selected + 1);
                // 'wasLocked' skips the click that only re-captured the cursor.
                if (wasLocked && mouse.leftButton.wasPressedThisFrame) TryBuild();
            }
            wasLocked = locked;
        }

        /// <summary>Leave build mode (the U menu opening, for example).</summary>
        public void ExitBuildMode() => BuildMode = false;

        void SetBuildMode(bool on)
        {
            BuildMode = on;
            if (!on) return;
            barOpen = 0f;
            barSlot = Selected;
            if (!yawInitialised)
            {
                // First time: the build faces the player.
                yaw = Mathf.Repeat(Mathf.Round((transform.eulerAngles.y + 180f) / rotateStep) * rotateStep, 360f);
                yawInitialised = true;
            }
        }

        void Select(int index)
        {
            var cat = BuildCatalog.Instance;
            int n = cat != null ? cat.entries.Count : 0;
            if (n == 0) return;
            int next = (index % n + n) % n;     // wraps around both ways
            if (next != Selected) pop = 0.6f;
            Selected = next;
        }

        // ================================================================== preview

        void UpdatePreview()
        {
            hasPreview = false; previewValid = false;
            var entry = BuildCatalog.Instance ? BuildCatalog.Instance.Get(Selected) : null;
            var cam = player ? player.playerCamera : null;
            if (entry == null || entry.prefab == null || cam == null || !cam.enabled) return;

            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (!RaycastWorld(ray, out var hit)) return;
            if (!BuildPlacement.TryGroundHeight(hit.point, out float gy)) return;

            previewPos = new Vector3(hit.point.x, gy, hit.point.z);
            previewRot = Quaternion.Euler(0f, yaw, 0f);
            hasPreview = true;
            previewValid = Validate(entry, previewPos, previewRot, 0f) == null;
        }

        /// <summary>Closest solid hit along the ray, skipping this player's own colliders.</summary>
        bool RaycastWorld(Ray ray, out RaycastHit best)
        {
            best = default;
            int n = Physics.RaycastNonAlloc(ray, rayHits, maxBuildDistance, ~0, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = rayHits[i];
                if (h.collider.transform.IsChildOf(transform) || h.distance >= bestDist) continue;
                best = h; bestDist = h.distance;
            }
            return bestDist < float.MaxValue;
        }

        static bool CanAfford(BuildCatalog.Entry e)
        {
            var bank = GiantWineBottleController.Instance;
            return bank != null && bank.Money + 1e-6 >= e.cost;
        }

        /// <summary>Shared client / host rule set. Null = can build, otherwise why not (logged on the host).</summary>
        string Validate(BuildCatalog.Entry entry, Vector3 pos, Quaternion rot, float extraRange)
        {
            var shape = BuildPlacement.ShapeOf(entry.prefab);
            Vector3 d = pos - transform.position; d.y = 0f;
            if (d.magnitude > maxBuildDistance + extraRange) return "too far";
            string why = BuildPlacement.Check(shape, pos, rot);
            if (why != null) return why;
            if (!CanAfford(entry)) return "not enough money";
            return null;
        }

        void DrawPreview()
        {
            var cat = BuildCatalog.Instance;
            var entry = cat ? cat.Get(Selected) : null;
            if (entry == null || entry.prefab == null) return;
            var mat = previewValid ? (cat.validMaterial ? cat.validMaterial : Fallback(true))
                                   : (cat.invalidMaterial ? cat.invalidMaterial : Fallback(false));
            if (mat == null) return;
            var rp = new RenderParams(mat) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false, layer = 0 };
            Matrix4x4 world = Matrix4x4.TRS(previewPos, previewRot, Vector3.one);
            foreach (var part in BuildPlacement.ShapeOf(entry.prefab).parts)
                for (int sub = 0; sub < part.mesh.subMeshCount; sub++)
                    Graphics.RenderMesh(rp, part.mesh, sub, world * part.local);
        }

        /// <summary>Used only if the catalog's preview materials are missing.</summary>
        Material Fallback(bool valid)
        {
            ref Material m = ref valid ? ref fallbackValid : ref fallbackInvalid;
            if (m == null)
            {
                var shader = Shader.Find("Sprites/Default");
                if (shader == null) return null;
                m = new Material(shader) { color = valid ? new Color(1f, 1f, 1f, 0.35f) : new Color(1f, 0.15f, 0.1f, 0.45f) };
            }
            return m;
        }

        // ================================================================== building

        void TryBuild()
        {
            if (Time.unscaledTime < nextBuildTime) return;
            if (!hasPreview || !previewValid) { deny = 1f; return; }
            nextBuildTime = Time.unscaledTime + 0.35f;
            swing = 1f;
            CmdBuild(Selected, previewPos, yaw);
        }

        [Command]
        void CmdBuild(int index, Vector3 pos, float yawDeg)
        {
            if (NetworkTime.time - lastServerRequest < serverMinInterval) return;
            lastServerRequest = NetworkTime.time;
            if (!hasHammer) { TargetBuildResult(false, null, Vector3.zero); return; }
            var cat = BuildCatalog.Instance;
            var entry = cat ? cat.Get(index) : null;
            if (entry == null || entry.prefab == null) return;
            if (float.IsNaN(pos.x) || float.IsNaN(pos.z) || float.IsInfinity(pos.x) || float.IsInfinity(pos.z) ||
                float.IsNaN(yawDeg) || float.IsInfinity(yawDeg)) return;

            // The host finds the ground itself: only x/z and the rotation come from the client.
            if (!BuildPlacement.TryGroundHeight(pos, out float gy)) { TargetBuildResult(false, null, Vector3.zero); return; }
            pos.y = gy;
            var rot = Quaternion.Euler(0f, Mathf.Repeat(yawDeg, 360f), 0f);
            var shape = BuildPlacement.ShapeOf(entry.prefab);
            string why = Validate(entry, pos, rot, serverTolerance + shape.Radius);
            if (why != null || !GiantWineBottleController.Instance.ServerTrySpend(entry.cost))
            {
                Debug.Log($"[Build] Refused {entry.displayName} for player {netId}: {why ?? "not enough money"}");
                TargetBuildResult(false, null, Vector3.zero);
                return;
            }

            var go = Instantiate(entry.prefab, pos, rot);
            go.name = $"{entry.prefab.name} (built)";
            NetworkServer.Spawn(go);
            TargetBuildResult(true, $"-{GiantWineBottleController.FormatMoney(entry.cost)}", pos + Vector3.up * (shape.bounds.max.y + 0.3f));
        }

        [TargetRpc]
        void TargetBuildResult(bool ok, string floatText, Vector3 at)
        {
            if (ok) pop = 1f; else deny = 1f;
            if (!string.IsNullOrEmpty(floatText) && interactor) interactor.ShowFloatingText(at, floatText);
        }

        // ================================================================== hammer animation

        void AnimateHammer()
        {
            if (!hammerView) return;
            var t = hammerView.transform;
            if (!viewRestCached) { viewRestPos = t.localPosition; viewRestRot = t.localRotation; viewRestCached = true; }
            swing = Mathf.MoveTowards(swing, 0f, Time.deltaTime * 3.2f);
            // Quick strike down, slower return.
            float k = swing > 0.75f ? Mathf.Sin((1f - swing) / 0.25f * Mathf.PI * 0.5f) : Mathf.SmoothStep(0f, 1f, swing / 0.75f);
            t.localRotation = viewRestRot * Quaternion.Euler(55f * k, 0f, -10f * k);
            t.localPosition = viewRestPos + new Vector3(0f, -0.05f * k, 0.06f * k);
        }

        // ================================================================== icon bar (no text)

        /// <summary>Soft round glow drawn behind the selected icon.</summary>
        Texture2D Glow()
        {
            if (glow != null) return glow;
            const int s = 64;
            glow = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            var px = new Color[s * s];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(s / 2f, s / 2f)) / (s / 2f);
                    float a = Mathf.Clamp01(1f - d);
                    px[y * s + x] = new Color(1f, 1f, 1f, a * a);
                }
            glow.SetPixels(px);
            glow.Apply();
            return glow;
        }

        void OnGUI()
        {
            if (!isLocalPlayer || !BuildMode || Event.current.type != EventType.Repaint) return;
            var cat = BuildCatalog.Instance;
            int n = cat != null ? cat.entries.Count : 0;
            if (n == 0) return;

            float dt = Time.unscaledDeltaTime, now = Time.unscaledTime;
            barSlot = Mathf.Lerp(barSlot, Selected, 1f - Mathf.Exp(-dt * 14f));
            barOpen = Mathf.MoveTowards(barOpen, 1f, dt * 4f);
            pop = Mathf.MoveTowards(pop, 0f, dt * 3f);
            deny = Mathf.MoveTowards(deny, 0f, dt * 3f);

            float w = Screen.width, h = Screen.height;
            float ui = Mathf.Clamp(h / 1080f, 0.7f, 1.6f);
            float spacing = 118f * ui, baseSize = 74f * ui, bigSize = 118f * ui;
            float open = Mathf.SmoothStep(0f, 1f, barOpen);
            float centreY = h - (96f * ui) + (1f - open) * 140f * ui;
            float shake = Mathf.Sin(now * 60f) * 9f * ui * deny;

            // Carousel: the selected icon slides to the centre, the others fade out to the sides.
            var old = GUI.color;
            var oldMatrix = GUI.matrix;
            for (int i = 0; i < n; i++)
            {
                var e = cat.entries[i];
                if (e == null || e.icon == null) continue;
                float off = i - barSlot;
                float near = Mathf.Clamp01(1f - Mathf.Abs(off));                // 1 at the centre
                float size = Mathf.Lerp(baseSize, bigSize, near);
                bool sel = i == Selected;
                if (sel) size *= 1f + 0.18f * Mathf.Sin(pop * Mathf.PI);           // punch
                float bob = sel ? Mathf.Sin(now * 3.2f) * 4f * ui : 0f;
                float x = w / 2f + off * spacing + (sel ? shake : 0f);
                float y = centreY + bob - near * 10f * ui;
                float alpha = Mathf.Clamp01(1f - (Mathf.Abs(off) - 1.2f) * 0.6f) * open;
                bool afford = CanAfford(e);

                if (near > 0.01f)
                {
                    GUI.color = new Color(1f, 0.86f, 0.55f, 0.45f * near * alpha);
                    float g = size * 1.45f;
                    GUI.DrawTexture(new Rect(x - g / 2f, y - g / 2f, g, g), Glow());
                }
                Color tint = afford ? Color.white : new Color(1f, 0.42f, 0.38f);
                if (sel && deny > 0f) tint = Color.Lerp(tint, new Color(1f, 0.3f, 0.25f), deny);
                tint.a = alpha * (afford ? 1f : 0.75f) * Mathf.Lerp(0.6f, 1f, near);
                GUI.color = tint;

                // Selected icon sways gently.
                GUI.matrix = oldMatrix;
                if (sel) GUIUtility.RotateAroundPivot(Mathf.Sin(now * 2.1f) * 4f, new Vector2(x, y));
                GUI.DrawTexture(new Rect(x - size / 2f, y - size / 2f, size, size), e.icon, ScaleMode.ScaleToFit, true);
                GUI.matrix = oldMatrix;
            }
            GUI.color = old;
        }

        void OnDestroy()
        {
            if (glow != null) Destroy(glow);
        }
    }
}
