using System;
using System.Linq;
using Mirror;
using UnityEngine;
using Vineyard.Building;
using Vineyard.Upgrades;
using Vineyard.Winery;

namespace Vineyard.Networking
{
    /// <summary>
    /// Project NetworkManager. Transport-agnostic: it only talks to Mirror's
    /// abstractions (NetworkManager / Transport), so KCP can later be swapped
    /// for FizzySteamworks without touching gameplay code.
    /// </summary>
    public class GameNetworkManager : NetworkManager
    {
        public const int MaxPlayers = 4;

        public static GameNetworkManager Instance => singleton as GameNetworkManager;

        /// <summary>Last human-readable connection problem (shown by the connection UI).</summary>
        public string LastError { get; private set; }

        /// <summary>Raised on the client when the connection to the host ends.</summary>
        public event Action ClientDisconnected;

        public override void OnValidate()
        {
            base.OnValidate();
            maxConnections = Mathf.Clamp(maxConnections, 1, MaxPlayers);
        }

        [Header("Winery")]
        [Tooltip("Empty spare buckets the host places next to each machine output (press, fermenter) at start.")]
        public int spareBucketsPerMachine = 1;
        [Tooltip("Building hammers the host lays on the ground next to the first spawn point at start.")]
        public int hammers = 1;

        public override void Awake()
        {
            maxConnections = Mathf.Clamp(maxConnections, 1, MaxPlayers);
            // Ground buckets are spawned at runtime: host and clients must both know the prefab.
            var wb = WorldBucket.Prefab;
            if (wb != null && !spawnPrefabs.Contains(wb)) spawnPrefabs.Add(wb);
            var tu = TeamUpgrades.Prefab;
            if (tu != null && !spawnPrefabs.Contains(tu)) spawnPrefabs.Add(tu);
            var wh = WorldHammer.Prefab;
            if (wh != null && !spawnPrefabs.Contains(wh)) spawnPrefabs.Add(wh);
            // Buildable stations (grape field, press, fermenter) are spawned by the host's build system.
            var catalog = BuildCatalog.Instance;
            if (catalog != null)
                foreach (var e in catalog.entries)
                    if (e != null && e.prefab != null && !spawnPrefabs.Contains(e.prefab)) spawnPrefabs.Add(e.prefab);
            base.Awake();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            TeamUpgrades.ServerSpawn();
            SpawnSpareBuckets();
            SpawnHammers();
        }

        /// <summary>Hammers lie next to the spawn: a player puts the bucket down (Q) and picks one up to build.</summary>
        void SpawnHammers()
        {
            if (hammers <= 0 || WorldHammer.Prefab == null) return;
            Vector3 centre = startPositions.Count > 0 && startPositions[0] != null ? startPositions[0].position : Vector3.zero;
            Vector3 fwd = startPositions.Count > 0 && startPositions[0] != null ? startPositions[0].forward : Vector3.forward;
            for (int k = 0; k < hammers; k++)
            {
                Vector3 spot = WorldBucket.FindGroundSpot(centre, Quaternion.Euler(0f, 35f + k * 30f, 0f) * fwd, 1.6f + k * 0.4f, 16);
                WorldHammer.ServerSpawn(spot, UnityEngine.Random.Range(0f, 360f));
            }
        }

        /// <summary>Spare buckets let a player with full hands put theirs down and still empty a full machine.</summary>
        void SpawnSpareBuckets()
        {
            if (spareBucketsPerMachine <= 0 || WorldBucket.Prefab == null) return;
            var outputs = FindObjectsByType<WineryInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(i => i.part == InteractionPart.PressOutput || i.part == InteractionPart.FermenterOutput)
                .GroupBy(i => i.station)
                .Select(g => g.First());
            foreach (var output in outputs)
            {
                Bounds b = output.colliders != null && output.colliders.Length > 0 && output.colliders[0]
                    ? output.colliders[0].bounds : new Bounds(output.transform.position, Vector3.one * 0.5f);
                // Away from the machine body, on the side the output faces.
                Vector3 machine = output.station != null && output.station.interactables != null
                    ? output.station.interactables.Where(i => i && i.part != output.part).Select(i => i.transform.position).DefaultIfEmpty(b.center).Aggregate(Vector3.zero, (a, p) => a + p)
                    : b.center;
                int count = output.station != null && output.station.interactables != null ? output.station.interactables.Count(i => i && i.part != output.part) : 0;
                if (count > 0) machine /= count;
                Vector3 dir = b.center - machine;
                for (int k = 0; k < spareBucketsPerMachine; k++)
                {
                    Vector3 spot = WorldBucket.FindGroundSpot(b.center, Quaternion.Euler(0f, 50f + k * 40f, 0f) * dir, 0.9f + k * 0.5f, 16);
                    WorldBucket.ServerSpawn(spot, UnityEngine.Random.Range(0f, 360f), ResourceType.Empty, 0, 10);
                }
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            LastError = null;
        }

        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            // Mirror already rejects connections beyond maxConnections at the
            // transport level; this is a second, explicit guard.
            if (NetworkServer.connections.Count > maxConnections)
            {
                Debug.LogWarning($"[Net] Rejecting connection {conn.connectionId}: server full ({maxConnections}).");
                conn.Disconnect();
                return;
            }
            base.OnServerConnect(conn);
        }

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            base.OnServerAddPlayer(conn);
            Debug.Log($"[Net] Player added for connection {conn.connectionId}. Players: {NetworkServer.connections.Count}/{maxConnections}");
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            Debug.Log($"[Net] Connection {conn.connectionId} disconnected.");
            base.OnServerDisconnect(conn);
        }

        public override void OnClientError(TransportError error, string reason)
        {
            base.OnClientError(error, reason);
            LastError = $"{error}: {reason}";
            Debug.LogWarning($"[Net] Client error {LastError}");
        }

        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();
            ClientDisconnected?.Invoke();
        }

        /// <summary>Stops whatever mode we are in (host, client, or server).</summary>
        public void Disconnect()
        {
            if (NetworkServer.active && NetworkClient.isConnected) StopHost();
            else if (NetworkClient.active) StopClient();
            else if (NetworkServer.active) StopServer();
        }
    }
}
