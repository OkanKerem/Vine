using Mirror;
using UnityEngine;
using Vineyard.Networking;

namespace Vineyard.UI
{
    /// <summary>
    /// Minimal development connection panel (IMGUI, so it works with the
    /// Input System-only project setting and needs no Canvas/EventSystem).
    /// Uses only NetworkManager / Transport abstractions — no KCP specifics.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public class ConnectionUI : MonoBehaviour
    {
        const string PrefKey = "vineyard.lastAddress";

        public Vector2 offset = new Vector2(10, 10);
        public float width = 260f;

        NetworkManager manager;
        string address = "localhost";
        string portText;
        static Rect panelRect;

        void Awake()
        {
            manager = GetComponent<NetworkManager>();
            address = PlayerPrefs.GetString(PrefKey, manager.networkAddress);
            if (manager.transport is PortTransport pt) portText = pt.Port.ToString();
        }

        /// <summary>Lets gameplay ignore clicks on the panel (e.g. when re-locking the cursor).</summary>
        public static bool IsPointerOverUI(Vector2 screenPos)
        {
            // Input System uses bottom-left origin; IMGUI uses top-left.
            var guiPos = new Vector2(screenPos.x, Screen.height - screenPos.y);
            return panelRect.Contains(guiPos);
        }

        void OnGUI()
        {
            bool connected = NetworkClient.isConnected || NetworkServer.active;
            float height = connected ? 110f : (NetworkClient.isConnecting ? 90f : 185f);
            panelRect = new Rect(offset.x, offset.y, width, height);

            GUILayout.BeginArea(panelRect, GUI.skin.box);

            if (NetworkClient.isConnecting)
            {
                GUILayout.Label($"Connecting to {manager.networkAddress}...");
                if (GUILayout.Button("Cancel")) manager.StopClient();
            }
            else if (!connected)
            {
                DrawOffline();
            }
            else
            {
                DrawOnline();
            }

            GUILayout.EndArea();
        }

        void DrawOffline()
        {
            GUILayout.Label("Vineyard — Multiplayer (dev)");

            GUILayout.BeginHorizontal();
            GUILayout.Label("IP", GUILayout.Width(40));
            address = GUILayout.TextField(address ?? "");
            GUILayout.EndHorizontal();

            if (portText != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Port", GUILayout.Width(40));
                portText = GUILayout.TextField(portText);
                GUILayout.EndHorizontal();
            }

            if (GUILayout.Button("Host Game"))
            {
                ApplyPort();
                manager.StartHost();
            }

            if (GUILayout.Button("Join Game"))
            {
                string a = string.IsNullOrWhiteSpace(address) ? "localhost" : address.Trim();
                manager.networkAddress = a;
                PlayerPrefs.SetString(PrefKey, a);
                ApplyPort();
                manager.StartClient();
            }

            var gnm = manager as GameNetworkManager;
            if (gnm != null && !string.IsNullOrEmpty(gnm.LastError))
                GUILayout.Label($"<color=orange>{gnm.LastError}</color>", RichLabel);
        }

        void DrawOnline()
        {
            string mode = NetworkServer.active && NetworkClient.isConnected ? "Host"
                        : NetworkServer.active ? "Server" : "Client";
            GUILayout.Label($"{mode} — {Transport.active?.GetType().Name}");

            if (NetworkServer.active)
                GUILayout.Label($"Players: {NetworkServer.connections.Count}/{manager.maxConnections}");
            else
                GUILayout.Label($"Connected to {manager.networkAddress}  RTT {Mathf.RoundToInt((float)(NetworkTime.rtt * 1000))} ms");

            if (GUILayout.Button(mode == "Host" ? "Stop Host" : "Disconnect"))
            {
                if (manager is GameNetworkManager g) g.Disconnect();
                else if (NetworkServer.active) manager.StopHost();
                else manager.StopClient();
            }

            if (Cursor.lockState != CursorLockMode.Locked)
                GUILayout.Label("Click in the game view to capture the mouse.");
        }

        void ApplyPort()
        {
            if (portText != null && manager.transport is PortTransport pt && ushort.TryParse(portText, out ushort port))
                pt.Port = port;
        }

        static GUIStyle richLabel;
        static GUIStyle RichLabel => richLabel ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
    }
}
