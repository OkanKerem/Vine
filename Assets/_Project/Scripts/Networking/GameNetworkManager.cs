using System;
using System.Linq;
using Mirror;
using UnityEngine;
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

        public override void Awake()
        {
            maxConnections = Mathf.Clamp(maxConnections, 1, MaxPlayers);
            // Ground buckets are spawned at runtime: host and clients must both know the prefab.
            var wb = WorldBucket.Prefab;
            if (wb != null && !spawnPrefabs.Contains(wb)) spawnPrefabs.Add(wb);
            base.Awake();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            SpawnSpareBuckets();
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
