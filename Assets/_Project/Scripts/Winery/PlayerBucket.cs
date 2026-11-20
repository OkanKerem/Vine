using Mirror;
using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// The player's only inventory: one bucket holding a single resource type.
    /// State is owned by the host and replicated to every client (so other players see your bucket).
    /// Capacity is a SyncVar so a future upgrade can raise it at runtime with <see cref="ServerSetCapacity"/>.
    /// </summary>
    public class PlayerBucket : NetworkBehaviour, IBucketContents
    {
        /// <summary>False after the bucket was put down: the player's hands are free until they pick one up.</summary>
        [SyncVar] bool hasBucket = true;
        [SyncVar] ResourceType type = ResourceType.Empty;
        [SyncVar] int amount;
        [SyncVar] [SerializeField] int capacity = 10;

        public bool HasBucket => hasBucket;
        public ResourceType Type => hasBucket && amount > 0 ? type : ResourceType.Empty;
        public int Amount => hasBucket ? amount : 0;
        public int Capacity => capacity;
        public int Free => hasBucket ? Mathf.Max(0, capacity - amount) : 0;
        public float Fill => hasBucket && capacity > 0 ? amount / (float)capacity : 0f;

        [Header("Presentation")]
        [Tooltip("First-person bucket under the camera (enabled for the local player only).")]
        public GameObject viewBucket;
        [Tooltip("Bucket others see in the character's hand (shadow-only for the local player).")]
        public Renderer[] worldBucketRenderers;

        public static PlayerBucket Local { get; private set; }

        void Update()
        {
            // Hide the bucket models while the hands are empty (BucketVisual keeps animating its children).
            if (isLocalPlayer && viewBucket && viewBucket.activeSelf != hasBucket) viewBucket.SetActive(hasBucket);
            if (worldBucketRenderers != null && worldVisible != hasBucket)
            {
                worldVisible = hasBucket;
                foreach (var r in worldBucketRenderers) if (r) r.forceRenderingOff = !hasBucket;
            }
        }
        bool worldVisible = true;

        public override void OnStartLocalPlayer()
        {
            Local = this;
            if (viewBucket) viewBucket.SetActive(hasBucket);
            if (worldBucketRenderers != null)
                foreach (var r in worldBucketRenderers)
                    if (r) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        }

        public override void OnStopLocalPlayer()
        {
            if (Local == this) Local = null;
            if (viewBucket) viewBucket.SetActive(false);
        }

        /// <summary>True if <paramref name="t"/> can be added without mixing resources.</summary>
        public bool CanHold(ResourceType t) => hasBucket && (amount == 0 || type == t);

        /// <summary>Adds up to <paramref name="max"/> units. Returns how many were added.</summary>
        [Server]
        public int ServerAdd(ResourceType t, int max)
        {
            if (t == ResourceType.Empty || max <= 0 || !CanHold(t)) return 0;
            int n = Mathf.Min(max, Free);
            if (n <= 0) return 0;
            type = t;
            amount += n;
            return n;
        }

        /// <summary>Removes up to <paramref name="max"/> units of <paramref name="t"/>. Returns how many were removed.</summary>
        [Server]
        public int ServerRemove(ResourceType t, int max)
        {
            if (!hasBucket || amount <= 0 || type != t || max <= 0) return 0;
            int n = Mathf.Min(max, amount);
            amount -= n;
            if (amount == 0) type = ResourceType.Empty;
            return n;
        }

        // ------------------------------------------------------------------ emptying (pour out on the ground)

        [Tooltip("Length of the tip-out animation (s). Interactions are blocked meanwhile.")]
        public float pourDuration = 1.1f;

        /// <summary>Replicated so every client plays the same tip-out animation.</summary>
        [SyncVar] double pourStart = -999;
        [SyncVar] ResourceType pourType;

        public ResourceType PourType => pourType;
        /// <summary>0..1 through the tip-out animation, or -1 when not pouring.</summary>
        public float PourProgress
        {
            get
            {
                double t = (NetworkTime.time - pourStart) / pourDuration;
                return t >= 0 && t < 1 ? (float)t : -1f;
            }
        }
        public bool IsPouring => PourProgress >= 0f;

        /// <summary>Dumps the whole content. Returns false if there was nothing to pour or a pour is running.</summary>
        [Server]
        public bool ServerEmpty()
        {
            if (!hasBucket || amount <= 0 || IsPouring) return false;
            pourType = type;
            pourStart = NetworkTime.time;
            amount = 0;
            type = ResourceType.Empty;
            return true;
        }

        // ------------------------------------------------------------------ putting down / picking up

        /// <summary>Hands the bucket over (contents and capacity) and leaves the player empty-handed.</summary>
        [Server]
        public bool ServerTakeBucket(out ResourceType t, out int n, out int cap)
        {
            t = Type; n = amount; cap = capacity;
            if (!hasBucket || IsPouring) return false;
            hasBucket = false;
            amount = 0;
            type = ResourceType.Empty;
            return true;
        }

        /// <summary>The player now carries a bucket with these contents.</summary>
        [Server]
        public void ServerGiveBucket(ResourceType t, int n, int cap)
        {
            capacity = Mathf.Max(1, cap);
            amount = Mathf.Clamp(n, 0, capacity);
            type = amount > 0 ? t : ResourceType.Empty;
            hasBucket = true;
        }

        /// <summary>Upgrade hook. Never drops below what is currently carried.</summary>
        [Server]
        public void ServerSetCapacity(int newCapacity) => capacity = Mathf.Max(1, Mathf.Max(newCapacity, amount));
    }
}
