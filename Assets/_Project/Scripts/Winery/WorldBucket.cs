using Mirror;
using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// A bucket standing on the ground. Hands hold one thing at a time (bucket or hammer); this lets players put it down (Q),
    /// grab another one (spare buckets near the machines), and come back for it later.
    /// Clicking it with empty hands picks it up; clicking it while carrying a bucket swaps the two.
    /// Spawned by the host from Resources/WorldBucket (registered in <see cref="Vineyard.Networking.GameNetworkManager"/>).
    /// </summary>
    public class WorldBucket : WineryStation, IBucketContents
    {
        public const string ResourcePath = "WorldBucket";

        [SyncVar] ResourceType type;
        [SyncVar] int amount;
        [SyncVar] int capacity = 10;

        public ResourceType Type => amount > 0 ? type : ResourceType.Empty;
        public int Amount => amount;
        public int Capacity => capacity;
        public float Fill => capacity > 0 ? amount / (float)capacity : 0f;
        public float PourProgress => -1f;
        public ResourceType PourType => ResourceType.Empty;

        static GameObject prefab;
        public static GameObject Prefab => prefab != null ? prefab : prefab = Resources.Load<GameObject>(ResourcePath);

        string Contents => amount > 0 ? $"{type.Label()} · {amount}/{capacity}{(type.IsLiquid() ? " L" : "")}" : "Empty";

        public override InteractionPrompt GetPrompt(WineryInteractable target, PlayerBucket bucket)
        {
            if (bucket == null) return Blocked("Bucket", Contents, "");
            if (bucket.IsPouring) return Blocked("Bucket", Contents, "Pouring...");
            var builder = bucket.GetComponent<Vineyard.Building.PlayerBuilder>();
            if (builder != null && builder.HasHammer) return Action("Bucket", Contents, "Swap hammer for bucket");
            return Action("Bucket", Contents, bucket.HasBucket ? "Swap buckets" : "Pick up");
        }

        [Server]
        public override string ServerInteract(WineryInteractable target, PlayerBucket bucket, out bool success, out string floatText)
        {
            success = false; floatText = null;
            if (target.part != InteractionPart.BucketPickup || bucket == null || bucket.IsPouring) return null;

            // Single slot: a held hammer goes down where this bucket stood.
            var builder = bucket.GetComponent<Vineyard.Building.PlayerBuilder>();
            if (builder != null && builder.HasHammer)
            {
                if (bucket.HasBucket || !builder.ServerTakeHammer()) return null;
                Vineyard.Building.WorldHammer.ServerSpawn(transform.position, transform.eulerAngles.y);
                bucket.ServerGiveBucket(type, amount, capacity);
                NetworkServer.Destroy(gameObject);
                success = true;
                return "Picked up the bucket";
            }

            if (bucket.HasBucket)
            {
                if (!bucket.ServerTakeBucket(out var t, out int n, out int cap)) return null;
                bucket.ServerGiveBucket(type, amount, capacity);
                type = n > 0 ? t : ResourceType.Empty; amount = n; capacity = cap;
                success = true;
                return "Swapped buckets";
            }

            bucket.ServerGiveBucket(type, amount, capacity);
            NetworkServer.Destroy(gameObject);
            success = true;
            return "Picked up the bucket";
        }

        /// <summary>Host: bucket upgrade — never shrinks below the current size.</summary>
        [Server] public void ServerRaiseCapacity(int c) => capacity = Mathf.Max(capacity, c);

        /// <summary>Host: place a bucket on the ground with the given contents.</summary>
        [Server]
        public static WorldBucket ServerSpawn(Vector3 position, float yaw, ResourceType t, int n, int cap)
        {
            if (Prefab == null) { Debug.LogError($"[Winery] Resources/{ResourcePath} prefab missing — run Tools > Vineyard > Setup Winery Gameplay."); return null; }
            var go = Instantiate(Prefab, position, Quaternion.Euler(0f, yaw, 0f));
            var wb = go.GetComponent<WorldBucket>();
            wb.capacity = Vineyard.Upgrades.TeamUpgrades.BucketCapacity(Mathf.Max(1, cap));
            wb.amount = Mathf.Clamp(n, 0, wb.capacity);
            wb.type = wb.amount > 0 ? t : ResourceType.Empty;
            NetworkServer.Spawn(go);
            return wb;
        }

        /// <summary>
        /// Finds a free spot on the ground around <paramref name="center"/>: first ring point whose ground is
        /// walkable and has nothing solid where the bucket would stand. Falls back to <paramref name="center"/>.
        /// </summary>
        public static Vector3 FindGroundSpot(Vector3 center, Vector3 preferredDir, float radius, int tries = 12)
        {
            preferredDir.y = 0f;
            if (preferredDir.sqrMagnitude < 1e-4f) preferredDir = Vector3.forward;
            preferredDir.Normalize();
            const int mask = ~(1 << 2);
            for (int i = 0; i < tries; i++)
            {
                // 0, +30, -30, +60, -60 ... degrees away from the preferred direction.
                float ang = ((i + 1) / 2) * (360f / tries) * (i % 2 == 0 ? 1f : -1f);
                Vector3 p = center + Quaternion.Euler(0f, ang, 0f) * preferredDir * radius;
                if (!Physics.Raycast(p + Vector3.up * 2.5f, Vector3.down, out RaycastHit hit, 6f, mask, QueryTriggerInteraction.Ignore)) continue;
                if (hit.normal.y < 0.75f) continue;
                if (hit.collider is CharacterController) continue;
                Vector3 ground = hit.point;
                if (Physics.CheckSphere(ground + Vector3.up * 0.3f, 0.22f, mask, QueryTriggerInteraction.Ignore)) continue;
                return ground;
            }
            return center;
        }
    }
}
