using System.Collections.Generic;
using System.IO;
using Mirror;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Vineyard.Networking;
using Vineyard.Player;
using Vineyard.UI;

namespace Vineyard.EditorTools
{
    /// <summary>
    /// Tools > Vineyard > Build Multiplayer Test Scene
    /// Generates the player prefab, materials and the test scene (ground, obstacles,
    /// spawn points, NetworkManager + KCP transport + connection UI).
    /// Re-running it overwrites the generated assets.
    /// KCP is referenced only here (scene setup), never in gameplay code.
    /// </summary>
    public static class MultiplayerSceneBuilder
    {
        const string Root = "Assets/_Project";
        const string PrefabDir = Root + "/Prefabs";
        const string MaterialDir = Root + "/Materials";
        const string SceneDir = Root + "/Scenes";
        const string PlayerPrefabPath = PrefabDir + "/NetworkPlayer.prefab";
        const string ScenePath = SceneDir + "/MultiplayerTest.unity";

        [MenuItem("Tools/Vineyard/Build Multiplayer Test Scene")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(MaterialDir);
            Directory.CreateDirectory(SceneDir);
            AssetDatabase.Refresh();

            var groundMat = CreateMaterial("Ground", new Color(0.35f, 0.45f, 0.3f));
            var obstacleMat = CreateMaterial("Obstacle", new Color(0.6f, 0.55f, 0.5f));
            var playerMat = CreateMaterial("Player", Color.white);
            var visorMat = CreateMaterial("Visor", new Color(0.1f, 0.1f, 0.12f));

            GameObject playerPrefab = BuildPlayerPrefab(playerMat, visorMat);
            BuildScene(playerPrefab, groundMat, obstacleMat);

            Debug.Log($"[Vineyard] Built {ScenePath} and {PlayerPrefabPath}. Press Play and click 'Host Game'.");
        }

        // ---------------------------------------------------------------- materials

        static Material CreateMaterial(string name, Color color)
        {
            string path = $"{MaterialDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ---------------------------------------------------------------- player

        static GameObject BuildPlayerPrefab(Material bodyMat, Material visorMat)
        {
            var root = new GameObject("NetworkPlayer");
            // Pivot at the feet.
            var cc = root.AddComponent<CharacterController>();
            cc.height = 2f;
            cc.radius = 0.4f;
            cc.center = new Vector3(0f, 1f, 0f);
            cc.slopeLimit = 45f;
            cc.stepOffset = 0.35f;
            cc.skinWidth = 0.04f;
            cc.minMoveDistance = 0f;

            root.AddComponent<NetworkIdentity>();
            root.AddComponent<PlayerMotor>();
            var player = root.AddComponent<NetworkPlayer>();

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 1f, 0f);
            body.transform.localScale = new Vector3(0.8f, 1f, 0.8f);
            var bodyR = body.GetComponent<MeshRenderer>();
            bodyR.sharedMaterial = bodyMat;

            var visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visor.name = "Visor";
            Object.DestroyImmediate(visor.GetComponent<Collider>());
            visor.transform.SetParent(root.transform, false);
            visor.transform.localPosition = new Vector3(0f, 1.55f, 0.33f);
            visor.transform.localScale = new Vector3(0.5f, 0.15f, 0.15f);
            var visorR = visor.GetComponent<MeshRenderer>();
            visorR.sharedMaterial = visorMat;

            var camRoot = new GameObject("CameraRoot");
            camRoot.transform.SetParent(root.transform, false);
            camRoot.transform.localPosition = new Vector3(0f, 1.65f, 0f);

            var camGo = new GameObject("PlayerCamera");
            camGo.transform.SetParent(camRoot.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.fieldOfView = 75f;
            cam.enabled = false;
            var listener = camGo.AddComponent<AudioListener>();
            listener.enabled = false;
            // URP adds UniversalAdditionalCameraData automatically when needed.

            player.cameraRoot = camRoot.transform;
            player.playerCamera = cam;
            player.audioListener = listener;
            player.bodyRenderers = new Renderer[] { bodyR, visorR };

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(PlayerPrefabPath, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath) ?? prefab;
        }

        // ---------------------------------------------------------------- scene

        static void BuildScene(GameObject playerPrefab, Material groundMat, Material obstacleMat)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // Menu camera (disabled by the local player when it spawns).
            var menuCam = Camera.main;
            if (menuCam != null)
            {
                menuCam.transform.position = new Vector3(0f, 18f, -22f);
                menuCam.transform.rotation = Quaternion.Euler(38f, 0f, 0f);
            }

            // Ground: 60 x 60 m.
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(6f, 1f, 6f);
            ground.GetComponent<Renderer>().sharedMaterial = groundMat;
            GameObjectUtility.SetStaticEditorFlags(ground, StaticEditorFlags.BatchingStatic);

            var obstacles = new GameObject("Obstacles").transform;
            Box(obstacles, obstacleMat, "Crate A", new Vector3(4f, 0.5f, 6f), new Vector3(1f, 1f, 1f));
            Box(obstacles, obstacleMat, "Crate B", new Vector3(5.2f, 0.75f, 6.3f), new Vector3(1.5f, 1.5f, 1.5f));
            Box(obstacles, obstacleMat, "Wall", new Vector3(-6f, 1.5f, 8f), new Vector3(8f, 3f, 0.5f));
            Box(obstacles, obstacleMat, "Pillar", new Vector3(0f, 2f, 12f), new Vector3(1.5f, 4f, 1.5f));
            Box(obstacles, obstacleMat, "Platform", new Vector3(10f, 1f, 14f), new Vector3(5f, 2f, 5f));
            Box(obstacles, obstacleMat, "Ramp", new Vector3(10f, 0.9f, 9.2f), new Vector3(3f, 0.3f, 5f), new Vector3(-22f, 0f, 0f));
            for (int i = 0; i < 4; i++) // steps
                Box(obstacles, obstacleMat, $"Step {i}", new Vector3(-10f, 0.15f + i * 0.3f, 2f + i * 0.8f), new Vector3(3f, 0.3f * (i + 1), 0.8f), Vector3.zero, alignBottom: true);

            // Spawn points.
            var spawns = new GameObject("SpawnPoints").transform;
            Vector3[] spawnPos = { new Vector3(-3f, 0f, -3f), new Vector3(3f, 0f, -3f), new Vector3(-3f, 0f, -7f), new Vector3(3f, 0f, -7f) };
            for (int i = 0; i < spawnPos.Length; i++)
            {
                var sp = new GameObject($"Spawn {i + 1}");
                sp.transform.SetParent(spawns, false);
                sp.transform.position = spawnPos[i];
                sp.transform.rotation = Quaternion.identity; // facing +Z (toward obstacles)
                sp.AddComponent<NetworkStartPosition>();
            }

            // Network manager.
            var nmGo = new GameObject("NetworkManager");
            var manager = nmGo.AddComponent<GameNetworkManager>();
            var transport = nmGo.AddComponent<kcp2k.KcpTransport>();
            nmGo.AddComponent<ConnectionUI>();
            manager.transport = transport;
            manager.playerPrefab = playerPrefab;
            manager.autoCreatePlayer = true;
            manager.playerSpawnMethod = PlayerSpawnMethod.RoundRobin;
            manager.maxConnections = GameNetworkManager.MaxPlayers;
            manager.networkAddress = "localhost";
            EditorUtility.SetDirty(manager);
            EditorUtility.SetDirty(transport);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettingsFirst(ScenePath);
        }

        static void Box(Transform parent, Material mat, string name, Vector3 pos, Vector3 size,
                        Vector3 euler = default, bool alignBottom = false)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            if (alignBottom) pos.y = size.y * 0.5f;
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(euler);
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.On;
        }

        static void AddSceneToBuildSettingsFirst(string path)
        {
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            list.RemoveAll(s => s.path == path);
            list.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = list.ToArray();
        }
    }
}
