using System;
using Mirror;
using UnityEngine;

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

        public override void Awake()
        {
            maxConnections = Mathf.Clamp(maxConnections, 1, MaxPlayers);
            base.Awake();
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
