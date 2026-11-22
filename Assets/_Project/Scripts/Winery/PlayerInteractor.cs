using System;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using Vineyard.Player;

namespace Vineyard.Winery
{
    /// <summary>
    /// First-person interaction: centre-screen raycast, highlight + prompt, left click sends a request to the host.
    ///
    /// Flow: local click -> CmdInteract (station, part, index) -> host checks distance, rate limit and the
    /// station's own rules -> station applies the transfer on the host -> SyncVars replicate to everyone ->
    /// TargetFeedback tells the clicking player what happened.
    /// The host cannot re-raycast (camera pitch is local-only), so it validates distance to the part instead.
    /// </summary>
    [RequireComponent(typeof(PlayerBucket))]
    public class PlayerInteractor : NetworkBehaviour
    {
        [Header("Interaction")]
        [Tooltip("Max distance from the camera to the aimed part (m).")]
        public float maxDistance = 3f;
        [Tooltip("Extra distance the host tolerates (latency, eye-height difference).")]
        public float serverTolerance = 1.25f;
        [Tooltip("Ignore Raycast (layer 2) is excluded so big outer frame colliders don't block inner parts.")]
        public LayerMask raycastMask = ~(1 << 2);
        [Tooltip("Minimum time between two requests from one player (host side, seconds).")]
        public float serverMinInterval = 0.15f;

        public static PlayerInteractor Local { get; private set; }

        public WineryInteractable Current { get; private set; }
        public InteractionPrompt CurrentPrompt { get; private set; }

        /// <summary>(message, success) — raised on the owning client.</summary>
        public event Action<string, bool> Feedback;
        /// <summary>(world position, text) — floating "+8 Grapes".</summary>
        public event Action<Vector3, string> FloatingText;

        NetworkPlayer player;
        PlayerBucket bucket;
        bool wasLocked;
        float nextClickTime;
        double lastServerRequest;

        public PlayerBucket Bucket => bucket;

        void Awake()
        {
            player = GetComponent<NetworkPlayer>();
            bucket = GetComponent<PlayerBucket>();
        }

        public override void OnStartLocalPlayer() => Local = this;

        public override void OnStopLocalPlayer()
        {
            if (Local == this) Local = null;
            SetCurrent(null);
        }

        void Update()
        {
            if (!isLocalPlayer) return;
            Scan();

            bool locked = Cursor.lockState == CursorLockMode.Locked;
            var mouse = Mouse.current;
            // 'wasLocked' skips the click that only re-captured the cursor.
            if (locked && wasLocked && mouse != null && mouse.leftButton.wasPressedThisFrame && Time.unscaledTime >= nextClickTime)
            {
                nextClickTime = Time.unscaledTime + 0.2f;
                TryInteract();
            }
            var kb = Keyboard.current;
            if (locked && kb != null && kb.gKey.wasPressedThisFrame) RequestEmptyBucket();
            if (locked && kb != null && kb.bKey.wasPressedThisFrame) RequestDropBucket();
            wasLocked = locked;
        }

        void Scan()
        {
            WineryInteractable hitTarget = null;
            var cam = player != null ? player.playerCamera : null;
            if (cam != null && cam.enabled)
            {
                Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                if (Physics.Raycast(ray, out RaycastHit hit, maxDistance, raycastMask, QueryTriggerInteraction.Collide))
                {
                    var it = hit.collider.GetComponentInParent<WineryInteractable>();
                    if (it != null && it.station != null && it.station.isActiveAndEnabled && it.station.netId != 0)
                        hitTarget = it;
                }
            }
            SetCurrent(hitTarget);
            CurrentPrompt = Current != null ? Current.station.GetPrompt(Current, bucket) : default;
        }

        void SetCurrent(WineryInteractable it)
        {
            if (Current == it) return;
            if (Current) Current.SetHighlighted(false);
            Current = it;
            if (Current) Current.SetHighlighted(true);
        }

        /// <summary>Interact with whatever is aimed at (also used by automated tests).</summary>
        public void TryInteract()
        {
            if (Current == null || bucket.IsPouring) return; // clicking empty space / scenery does nothing
            var prompt = Current.station.GetPrompt(Current, bucket);
            if (!prompt.actionable)
            {
                Feedback?.Invoke(prompt.blockReason, false); // explain locally, no request
                return;
            }
            if (bucket.IsTransferring && UsesBucket(Current.part)) return;   // still pouring into the last machine
            Current.station.ClientPredict(Current, bucket);
            CmdInteract(Current.station.netIdentity, Current.part, Current.index);
        }

        /// <summary>Send a request for a specific part without aiming (automated tests / future gamepad assist).</summary>
        public void RequestInteract(WineryInteractable target)
        {
            if (target != null && target.station != null) CmdInteract(target.station.netIdentity, target.part, target.index);
        }

        // ------------------------------------------------------------------ host

        [Command]
        void CmdInteract(NetworkIdentity stationIdentity, InteractionPart part, int index)
        {
            if (NetworkTime.time - lastServerRequest < serverMinInterval) return;   // spam / duplicate clicks
            lastServerRequest = NetworkTime.time;
            if (bucket.IsPouring) return;                                            // hands busy tipping the bucket
            if (bucket.IsTransferring && UsesBucket(part)) return;                   // still pouring into a machine

            if (stationIdentity == null || !stationIdentity.TryGetComponent(out WineryStation station)) return;
            var target = station.Find(part, index);
            if (target == null) return;

            Vector3 eye = transform.position + Vector3.up * 1.65f;
            if (station.DistanceTo(part, index, eye) > maxDistance + serverTolerance)
            {
                TargetFeedback("Out of Range", false, null, Vector3.zero);
                return;
            }

            ResourceType typeBefore = bucket.Type;
            int amountBefore = bucket.Amount;
            string msg = station.ServerInteract(target, bucket, out bool ok, out string floatText);
            // Content went from the bucket into the machine: everyone sees the bucket reach over and pour it in.
            if (ok && bucket.HasBucket && bucket.Amount < amountBefore)
                bucket.ServerStartTransfer(typeBefore, station.PourPoint(target), station.netIdentity);
            TargetFeedback(msg, ok, floatText, target.FeedbackPoint);
        }

        /// <summary>Parts that move content in or out of the bucket (the press handle does not).</summary>
        static bool UsesBucket(InteractionPart part) =>
            part != InteractionPart.PressHandle && part != InteractionPart.BottleBody && part != InteractionPart.BottleTap;

        /// <summary>G: pour the bucket out (also used by automated tests).</summary>
        public void RequestEmptyBucket()
        {
            if (bucket.HasBucket && bucket.Amount > 0 && !bucket.IsBusy) CmdEmptyBucket();
        }

        /// <summary>B: put the bucket down in front of the player (also used by automated tests).</summary>
        public void RequestDropBucket()
        {
            if (bucket.HasBucket && !bucket.IsBusy) CmdDropBucket();
        }

        [Command]
        void CmdDropBucket()
        {
            if (NetworkTime.time - lastServerRequest < serverMinInterval) return;
            lastServerRequest = NetworkTime.time;
            if (!bucket.HasBucket || bucket.IsBusy || WorldBucket.Prefab == null) return;
            Vector3 fwd = transform.forward;
            Vector3 spot = WorldBucket.FindGroundSpot(transform.position, fwd, 0.85f);
            if (!bucket.ServerTakeBucket(out var t, out int n, out int cap)) return;
            WorldBucket.ServerSpawn(spot, transform.eulerAngles.y + 180f, t, n, cap);
            TargetFeedback("Put the bucket down", true, null, Vector3.zero);
        }

        /// <summary>Pour the whole bucket out on the ground (G). The content is lost.</summary>
        [Command]
        void CmdEmptyBucket()
        {
            if (NetworkTime.time - lastServerRequest < serverMinInterval) return;
            lastServerRequest = NetworkTime.time;
            ResourceType t = bucket.Type;
            int n = bucket.Amount;
            if (bucket.ServerEmpty())
                TargetFeedback($"Poured out {t.Amount(n)}", true, null, Vector3.zero);
        }

        [TargetRpc]
        void TargetFeedback(string message, bool success, string floatText, Vector3 position)
        {
            if (!string.IsNullOrEmpty(message)) Feedback?.Invoke(message, success);
            if (!string.IsNullOrEmpty(floatText)) FloatingText?.Invoke(position, floatText);
        }
    }
}
