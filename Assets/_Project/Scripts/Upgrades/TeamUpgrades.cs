using System;
using Mirror;
using UnityEngine;
using Vineyard.Winery;

namespace Vineyard.Upgrades
{
    public enum TeamUpgrade : byte { Speed = 0, Jump = 1, Bucket = 2 }

    /// <summary>
    /// Team-wide upgrades bought from the shared money (U menu): walk / sprint speed, jump height, bucket size.
    /// One networked object spawned by the host at start (Resources/TeamUpgrades); levels are SyncVars, so the
    /// host's movement simulation and every client's prediction use the same values.
    /// Prices and values are tuned on the prefab (Tools > Vineyard > Setup Build System creates it once).
    /// </summary>
    public class TeamUpgrades : NetworkBehaviour
    {
        public const string ResourcePath = "TeamUpgrades";
        public const int MaxLevel = 3;

        [Serializable]
        public class Track
        {
            public string label;
            [TextArea] [Tooltip("What the upgrade does (shown in the upgrade panel).")]
            public string description;
            [Tooltip("Optional icon for the U menu (transparent PNG). Without one the label is shown.")]
            public Texture2D icon;
            [Tooltip("Price of level 1, 2, 3.")]
            public float[] cost = new float[MaxLevel];
            [Tooltip("Value at level 0, 1, 2, 3.")]
            public float[] value = new float[MaxLevel + 1];
        }

        [Tooltip("Multiplier on walk and sprint speed.")]
        public Track speed = new Track { label = "Speed", description = DefaultSpeed, cost = new[] { 1f, 2f, 4f }, value = new[] { 1f, 1.15f, 1.3f, 1.5f } };
        [Tooltip("Multiplier on jump height.")]
        public Track jump = new Track { label = "Jump", description = DefaultJump, cost = new[] { 1f, 2f, 4f }, value = new[] { 1f, 1.35f, 1.7f, 2.1f } };
        [Tooltip("Bucket capacity in litres / grapes (every player's bucket and every bucket on the ground).")]
        public Track bucket = new Track { label = "Bucket", description = DefaultBucket, cost = new[] { 1.5f, 3f, 5f }, value = new[] { 10f, 15f, 20f, 30f } };

        const string DefaultSpeed = "Everyone on the team walks and sprints faster.";
        const string DefaultJump = "Everyone on the team jumps higher.";
        const string DefaultBucket = "Every bucket holds more grapes, juice and wine — fewer trips. Buckets on the ground grow too, nothing inside is lost.";

        /// <summary>The track's description, or the built-in one if the prefab has none yet.</summary>
        public string Description(TeamUpgrade u)
        {
            var d = Get(u).description;
            if (!string.IsNullOrWhiteSpace(d)) return d;
            return u == TeamUpgrade.Speed ? DefaultSpeed : u == TeamUpgrade.Jump ? DefaultJump : DefaultBucket;
        }

        /// <summary>Value at the next level (same as now when maxed).</summary>
        public float NextValue(TeamUpgrade u) => ValueAt(Get(u), Mathf.Min(Level(u) + 1, MaxLevel));

        [SyncVar] int speedLevel;
        [SyncVar] int jumpLevel;
        [SyncVar] int bucketLevel;

        public static TeamUpgrades Instance { get; private set; }

        static GameObject prefab;
        public static GameObject Prefab => prefab != null ? prefab : prefab = Resources.Load<GameObject>(ResourcePath);

        void OnEnable() => Instance = this;
        void OnDisable() { if (Instance == this) Instance = null; }

        public Track Get(TeamUpgrade u) => u == TeamUpgrade.Speed ? speed : u == TeamUpgrade.Jump ? jump : bucket;

        public int Level(TeamUpgrade u) => u == TeamUpgrade.Speed ? speedLevel : u == TeamUpgrade.Jump ? jumpLevel : bucketLevel;

        public bool IsMax(TeamUpgrade u) => Level(u) >= MaxLevel;

        public float NextCost(TeamUpgrade u)
        {
            var t = Get(u);
            int l = Level(u);
            return l < MaxLevel && l < t.cost.Length ? t.cost[l] : 0f;
        }

        public float Value(TeamUpgrade u) => ValueAt(Get(u), Level(u));

        static float ValueAt(Track t, int level) =>
            t.value == null || t.value.Length == 0 ? 1f : t.value[Mathf.Clamp(level, 0, t.value.Length - 1)];

        // ---- read by gameplay (static so callers don't need to null-check) ----

        public static float SpeedMultiplier => Instance != null ? Instance.Value(TeamUpgrade.Speed) : 1f;
        public static float JumpMultiplier => Instance != null ? Instance.Value(TeamUpgrade.Jump) : 1f;
        /// <summary>Team bucket capacity, never below <paramref name="fallback"/>.</summary>
        public static int BucketCapacity(int fallback) =>
            Instance != null ? Mathf.Max(fallback, Mathf.RoundToInt(Instance.Value(TeamUpgrade.Bucket))) : fallback;

        // ------------------------------------------------------------------ host

        /// <summary>Pays for and applies the next level. Returns the message for the buyer.</summary>
        [Server]
        public string ServerBuy(TeamUpgrade u, out bool success, out float paid)
        {
            success = false; paid = 0f;
            if (IsMax(u)) return "Max level";
            var bank = GiantWineBottleController.Instance;
            float cost = NextCost(u);
            if (bank == null || !bank.ServerTrySpend(cost)) return "Not enough money";
            switch (u)
            {
                case TeamUpgrade.Speed: speedLevel++; break;
                case TeamUpgrade.Jump: jumpLevel++; break;
                default: bucketLevel++; ServerApplyBucketCapacity(); break;
            }
            success = true; paid = cost;
            return $"{Get(u).label} level {Level(u)}";
        }

        /// <summary>Raises every bucket (carried or on the ground) to the team capacity. Contents are kept.</summary>
        [Server]
        public void ServerApplyBucketCapacity()
        {
            int cap = BucketCapacity(1);
            foreach (var pb in FindObjectsByType<PlayerBucket>(FindObjectsSortMode.None))
                if (pb.Capacity < cap) pb.ServerSetCapacity(cap);
            foreach (var wb in FindObjectsByType<WorldBucket>(FindObjectsSortMode.None))
                wb.ServerRaiseCapacity(cap);
        }

        [Server]
        public static void ServerSpawn()
        {
            if (Instance != null) return;
            if (Prefab == null) { Debug.LogError($"[Upgrades] Resources/{ResourcePath} prefab missing — run Tools > Vineyard > Setup Build System."); return; }
            NetworkServer.Spawn(Instantiate(Prefab));
        }
    }
}
