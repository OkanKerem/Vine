using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Vineyard.Player
{
    /// <summary>
    /// Server-authoritative first-person player.
    ///
    /// Flow (per FixedUpdate tick):
    ///  * Owning remote client: samples input -> predicts locally -> sends CmdInput to host.
    ///  * Host: simulates every player from their queued inputs (its own player directly
    ///    from local input), then broadcasts authoritative state with RpcState.
    ///  * Owning client: on RpcState, drops acknowledged inputs; if the server position
    ///    disagrees with what it predicted, rewinds to the server state and replays.
    ///  * Other clients: buffer RpcState snapshots and interpolate slightly in the past.
    ///
    /// Look yaw is client-driven (applied immediately for responsiveness); pitch is local only.
    /// Only Mirror abstractions are used here, no transport-specific code.
    /// </summary>
    [RequireComponent(typeof(PlayerMotor))]
    public class NetworkPlayer : NetworkBehaviour
    {
        [Header("References")]
        public Transform cameraRoot;          // child holding the Camera (pitch is applied here)
        public Camera playerCamera;
        public AudioListener audioListener;
        public Renderer[] bodyRenderers;      // hidden (shadows only) for the local player

        [Header("Look")]
        public float mouseSensitivity = 0.1f; // degrees per pixel of mouse delta
        public float minPitch = -85f;
        public float maxPitch = 85f;

        [Header("Networking")]
        [Tooltip("Broadcast state every N fixed ticks (1 = every tick).")]
        [Range(1, 5)] public int stateSendEveryTicks = 2;
        [Tooltip("Max inputs the host processes per player per tick (limits speed hacks, allows catch-up).")]
        [Range(1, 5)] public int maxInputsPerTick = 2;
        [Tooltip("Inputs beyond this are dropped on the host (oldest first).")]
        public int maxQueuedInputs = 30;
        [Tooltip("Position error (m) above which the owner rewinds and replays.")]
        public float reconcileThreshold = 0.05f;
        [Tooltip("How far in the past remote players are rendered (seconds).")]
        public float interpolationDelay = 0.1f;

        [SyncVar(hook = nameof(OnColorChanged))]
        int colorIndex;

        static readonly Color[] PlayerColors =
        {
            new Color(0.25f, 0.55f, 1f),  // blue
            new Color(1f, 0.35f, 0.3f),   // red
            new Color(0.35f, 0.85f, 0.4f),// green
            new Color(1f, 0.8f, 0.2f),    // yellow
        };

        PlayerMotor motor;

        // ---- owner (local) state ----
        float yaw, pitch;
        bool jumpLatched;
        uint nextSeq = 1;
        uint lastAckSeq;
        struct PendingInput { public PlayerInputCmd cmd; public Vector3 predictedPos; }
        readonly List<PendingInput> pending = new List<PendingInput>();
        Camera sceneCamera;

        // ---- server state ----
        readonly Queue<PlayerInputCmd> serverInputs = new Queue<PlayerInputCmd>();
        uint lastProcessedSeq;
        float serverYaw;
        int tickCounter;

        // ---- remote proxy state ----
        struct Snapshot { public double time; public Vector3 pos; public float yaw; }
        readonly List<Snapshot> snapshots = new List<Snapshot>();

        public static NetworkPlayer Local { get; private set; }

        bool IsHostOwner => isServer && isLocalPlayer;
        bool IsRemoteOwner => isLocalPlayer && !isServer;
        bool IsProxy => !isServer && !isLocalPlayer;

        void Awake()
        {
            motor = GetComponent<PlayerMotor>();
            SetCameraActive(false);
        }

        // ================= Lifecycle =================

        public override void OnStartServer()
        {
            serverYaw = transform.eulerAngles.y;
            // Pick the lowest colour not used by another player.
            var used = new HashSet<int>();
            foreach (var conn in NetworkServer.connections.Values)
                if (conn.identity != null && conn.identity != netIdentity &&
                    conn.identity.TryGetComponent(out NetworkPlayer other))
                    used.Add(other.colorIndex);
            int idx = 0;
            while (used.Contains(idx) && idx < PlayerColors.Length - 1) idx++;
            colorIndex = idx;
        }

        public override void OnStartClient()
        {
            ApplyColor(colorIndex);
        }

        public override void OnStartLocalPlayer()
        {
            Local = this;
            yaw = transform.eulerAngles.y;
            pitch = 0f;

            sceneCamera = Camera.main;
            if (sceneCamera != null && sceneCamera != playerCamera)
                sceneCamera.gameObject.SetActive(false);

            SetCameraActive(true);
            foreach (var r in bodyRenderers)
                if (r) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;

            SetCursorLocked(true);
        }

        public override void OnStopLocalPlayer()
        {
            if (Local == this) Local = null;
            SetCameraActive(false);
            if (sceneCamera != null) sceneCamera.gameObject.SetActive(true);
            SetCursorLocked(false);
        }

        void SetCameraActive(bool on)
        {
            if (playerCamera) playerCamera.enabled = on;
            if (audioListener) audioListener.enabled = on;
        }

        // ================= Input / look (per frame) =================

        void Update()
        {
            if (isLocalPlayer)
            {
                HandleCursor();
                HandleLook();
                if (InputEnabled && Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
                    jumpLatched = true;
            }
            else if (IsProxy)
            {
                InterpolateProxy();
            }
        }

        static bool InputEnabled => Cursor.lockState == CursorLockMode.Locked;

        void HandleCursor()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
                SetCursorLocked(false);
            else if (!InputEnabled && mouse != null && mouse.leftButton.wasPressedThisFrame &&
                     !Vineyard.UI.ConnectionUI.IsPointerOverUI(mouse.position.ReadValue()))
                SetCursorLocked(true);
        }

        static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        void HandleLook()
        {
            if (InputEnabled && Mouse.current != null)
            {
                Vector2 delta = Mouse.current.delta.ReadValue() * mouseSensitivity;
                yaw = Mathf.Repeat(yaw + delta.x, 360f);
                pitch = Mathf.Clamp(pitch - delta.y, minPitch, maxPitch);
            }
            // Local look is applied immediately; never overwritten by the network.
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (cameraRoot) cameraRoot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        PlayerInputCmd SampleInput()
        {
            var cmd = new PlayerInputCmd { seq = nextSeq++, yaw = yaw };
            var kb = Keyboard.current;
            if (InputEnabled && kb != null)
            {
                cmd.moveX = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
                cmd.moveY = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
                cmd.sprint = kb.leftShiftKey.isPressed;
                cmd.jump = jumpLatched;
            }
            jumpLatched = false;
            return cmd;
        }

        // ================= Simulation (fixed tick) =================

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;

            if (IsHostOwner)
            {
                // Host's own player: authoritative, no prediction needed.
                var cmd = SampleInput();
                motor.Step(cmd, dt);
                lastProcessedSeq = cmd.seq;
                serverYaw = cmd.yaw;
            }
            else if (IsRemoteOwner)
            {
                // Predict locally, remember the result, send input to host.
                var cmd = SampleInput();
                motor.Step(cmd, dt);
                pending.Add(new PendingInput { cmd = cmd, predictedPos = transform.position });
                if (pending.Count > 256) pending.RemoveAt(0); // safety cap
                CmdInput(cmd);
            }

            if (isServer)
            {
                if (!isLocalPlayer) ServerProcessInputs(dt);

                if (++tickCounter >= stateSendEveryTicks)
                {
                    tickCounter = 0;
                    RpcState(NetworkTime.time, transform.position, serverYaw, motor.VerticalVelocity, lastProcessedSeq);
                }
            }
        }

        // ================= Server =================

        [Command]
        void CmdInput(PlayerInputCmd cmd)
        {
            // Basic validation: ignore old / malformed input.
            if (cmd.seq <= lastProcessedSeq) return;
            if (float.IsNaN(cmd.moveX) || float.IsNaN(cmd.moveY) || float.IsNaN(cmd.yaw) ||
                float.IsInfinity(cmd.yaw)) return;
            cmd.moveX = Mathf.Clamp(cmd.moveX, -1f, 1f);
            cmd.moveY = Mathf.Clamp(cmd.moveY, -1f, 1f);
            cmd.yaw = Mathf.Repeat(cmd.yaw, 360f);

            serverInputs.Enqueue(cmd);
            while (serverInputs.Count > maxQueuedInputs) serverInputs.Dequeue();
        }

        [Server]
        void ServerProcessInputs(float dt)
        {
            int processed = 0;
            while (serverInputs.Count > 0 && processed < maxInputsPerTick)
            {
                var cmd = serverInputs.Dequeue();
                if (cmd.seq <= lastProcessedSeq) continue;
                motor.Step(cmd, dt);
                lastProcessedSeq = cmd.seq;
                serverYaw = cmd.yaw;
                processed++;
            }
            transform.rotation = Quaternion.Euler(0f, serverYaw, 0f);
        }

        // ================= Client =================

        [ClientRpc(channel = Channels.Unreliable)]
        void RpcState(double serverTime, Vector3 position, float stateYaw, float verticalVelocity, uint ackSeq)
        {
            if (isServer) return; // host already has the authoritative state

            if (isLocalPlayer) Reconcile(position, verticalVelocity, ackSeq);
            else AddSnapshot(serverTime, position, stateYaw);
        }

        void Reconcile(Vector3 serverPos, float serverVelY, uint ackSeq)
        {
            // Ignore duplicate / out-of-order (unreliable channel) state.
            if (ackSeq == 0 || ackSeq <= lastAckSeq) return;
            lastAckSeq = ackSeq;

            // Find what we predicted for the acknowledged input.
            int ackIndex = -1;
            for (int i = 0; i < pending.Count; i++)
                if (pending[i].cmd.seq == ackSeq) { ackIndex = i; break; }

            if (ackIndex < 0)
            {
                // Not in the pending list (e.g. trimmed by the safety cap): trust the server and snap.
                pending.Clear();
                motor.Teleport(serverPos);
                motor.VerticalVelocity = serverVelY;
                return;
            }

            Vector3 predicted = pending[ackIndex].predictedPos;
            pending.RemoveRange(0, ackIndex + 1);

            if ((predicted - serverPos).sqrMagnitude <= reconcileThreshold * reconcileThreshold)
                return; // prediction was right

            // Misprediction: rewind to server state and replay unacknowledged inputs.
            motor.Teleport(serverPos);
            motor.VerticalVelocity = serverVelY;
            float dt = Time.fixedDeltaTime;
            for (int i = 0; i < pending.Count; i++)
            {
                motor.Step(pending[i].cmd, dt);
                var p = pending[i];
                p.predictedPos = transform.position;
                pending[i] = p;
            }
        }

        void AddSnapshot(double time, Vector3 pos, float snapYaw)
        {
            if (snapshots.Count > 0 && time <= snapshots[snapshots.Count - 1].time) return; // out of order
            snapshots.Add(new Snapshot { time = time, pos = pos, yaw = snapYaw });
            if (snapshots.Count > 32) snapshots.RemoveAt(0);
        }

        void InterpolateProxy()
        {
            if (snapshots.Count == 0) return;
            double renderTime = NetworkTime.time - interpolationDelay;

            // Drop snapshots we have fully passed (keep one older than renderTime).
            while (snapshots.Count >= 2 && snapshots[1].time <= renderTime) snapshots.RemoveAt(0);

            Snapshot a = snapshots[0];
            if (snapshots.Count == 1 || renderTime <= a.time)
            {
                transform.SetPositionAndRotation(a.pos, Quaternion.Euler(0f, a.yaw, 0f));
                return;
            }
            Snapshot b = snapshots[1];
            float t = (float)((renderTime - a.time) / (b.time - a.time));
            transform.SetPositionAndRotation(
                Vector3.Lerp(a.pos, b.pos, t),
                Quaternion.Euler(0f, Mathf.LerpAngle(a.yaw, b.yaw, t), 0f));
        }

        // ================= Cosmetics =================

        void OnColorChanged(int _, int newIndex) => ApplyColor(newIndex);

        void ApplyColor(int index)
        {
            Color c = PlayerColors[Mathf.Clamp(index, 0, PlayerColors.Length - 1)];
            var block = new MaterialPropertyBlock();
            foreach (var r in bodyRenderers)
            {
                if (!r) continue;
                r.GetPropertyBlock(block);
                block.SetColor("_BaseColor", c); // URP
                block.SetColor("_Color", c);     // Built-in fallback
                r.SetPropertyBlock(block);
            }
        }
    }
}
