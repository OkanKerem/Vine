using Mirror;
using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// The player's only inventory: one bucket holding a single resource type.
    /// State is owned by the host and replicated to every client (so other players see your bucket).
    /// Capacity is a SyncVar so a future upgrade can raise it at runtime with <see cref="ServerSetCapacity"/>.
    /// </summary>
    public class PlayerBucket : NetworkBehaviour
    {
        [SyncVar] ResourceType type = ResourceType.Empty;
        [SyncVar] int amount;
        [SyncVar] [SerializeField] int capacity = 10;

        public ResourceType Type => amount > 0 ? type : ResourceType.Empty;
        public int Amount => amount;
        public int Capacity => capacity;
        public int Free => Mathf.Max(0, capacity - amount);
        public float Fill => capacity > 0 ? amount / (float)capacity : 0f;

        [Header("Presentation")]
        [Tooltip("First-person bucket under the camera (enabled for the local player only).")]
        public GameObject viewBucket;
        [Tooltip("Bucket others see in the character's hand (shadow-only for the local player).")]
        public Renderer[] worldBucketRenderers;

        public static PlayerBucket Local { get; private set; }

        public override void OnStartLocalPlayer()
        {
            Local = this;
            if (viewBucket) viewBucket.SetActive(true);
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
        public bool CanHold(ResourceType t) => amount == 0 || type == t;

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
            if (amount <= 0 || type != t || max <= 0) return 0;
            int n = Mathf.Min(max, amount);
            amount -= n;
            if (amount == 0) type = ResourceType.Empty;
            return n;
        }

        /// <summary>Upgrade hook. Never drops below what is currently carried.</summary>
        [Server]
        public void ServerSetCapacity(int newCapacity) => capacity = Mathf.Max(1, Mathf.Max(newCapacity, amount));
    }
}
