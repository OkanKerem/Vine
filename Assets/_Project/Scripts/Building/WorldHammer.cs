using Mirror;
using UnityEngine;
using Vineyard.Winery;

namespace Vineyard.Building
{
    /// <summary>
    /// The building hammer lying on the ground. Hands hold one thing at a time (bucket or hammer):
    /// clicking it with empty hands picks it up, clicking it while carrying a bucket swaps the two
    /// (the bucket stays where the hammer was). [Q] puts the held item down again.
    /// Spawned by the host from Resources/WorldHammer (registered in <see cref="Vineyard.Networking.GameNetworkManager"/>).
    /// </summary>
    public class WorldHammer : WineryStation
    {
        public const string ResourcePath = "WorldHammer";

        static GameObject prefab;
        public static GameObject Prefab => prefab != null ? prefab : prefab = Resources.Load<GameObject>(ResourcePath);

        public override InteractionPrompt GetPrompt(WineryInteractable target, PlayerBucket bucket)
        {
            const string title = "Hammer";
            if (bucket == null) return Blocked(title, "", "");
            if (bucket.IsBusy) return Blocked(title, "Builds machines and vines", "Hands busy");
            return Action(title, "Builds machines and vines", bucket.HasBucket ? "Swap bucket for hammer" : "Pick up");
        }

        [Server]
        public override string ServerInteract(WineryInteractable target, PlayerBucket bucket, out bool success, out string floatText)
        {
            success = false; floatText = null;
            if (target.part != InteractionPart.HammerPickup || bucket == null || bucket.IsBusy) return null;
            var builder = bucket.GetComponent<PlayerBuilder>();
            if (builder == null || builder.HasHammer) return null;

            if (bucket.HasBucket)
            {
                // Single slot: the carried bucket goes down where the hammer was.
                if (!bucket.ServerTakeBucket(out var t, out int n, out int cap)) return null;
                WorldBucket.ServerSpawn(transform.position, transform.eulerAngles.y, t, n, cap);
            }
            builder.ServerGiveHammer();
            NetworkServer.Destroy(gameObject);
            success = true;
            return "Picked up the hammer";
        }

        /// <summary>Host: lay a hammer on the ground.</summary>
        [Server]
        public static WorldHammer ServerSpawn(Vector3 position, float yaw)
        {
            if (Prefab == null) { Debug.LogError($"[Build] Resources/{ResourcePath} prefab missing — run Tools > Vineyard > Setup Build System."); return null; }
            var go = Instantiate(Prefab, position, Quaternion.Euler(0f, yaw, 0f));
            NetworkServer.Spawn(go);
            return go.GetComponent<WorldHammer>();
        }
    }
}
