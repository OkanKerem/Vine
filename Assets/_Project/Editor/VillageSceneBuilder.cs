using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mirror;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Vineyard.Networking;
using Vineyard.UI;

namespace Vineyard.EditorTools
{
    /// <summary>
    /// Tools > Vineyard > Build Village Scene
    ///
    /// Builds Assets/_Project/Scenes/Village.unity from the project's existing assets:
    ///   OccaSoftware Low Poly Fantasy Village (houses, bridge, cliffs, mountains, rocks, trees, flowers,
    ///   path stones, fences, lanterns, benches, crates, boat), GiantWineTank.fbx, Grapevine 1.fbx,
    ///   GrapeBunch.fbx, SmallGrapePress.fbx, SmallFermentationStation.fbx and the existing NetworkManager prefab.
    ///
    /// Layout (metres, +X = east, +Z = north):
    ///   Village square    centre (0, 4), irregular cobbled plaza, giant bottle in the middle
    ///                     (funnel faces south to the production courtyard, tap faces north to the tavern)
    ///   Production yard   centre (-6, -14): winery building, press, fermenter, 2 reserved expansion slots
    ///   Vineyard          x -24..-4, z -39..-24.5: 5 north-south rows x 4 vines, gate toward the press
    ///   Residential       streets north / north-west / west, east hamlet across the stream (stone bridge)
    ///   Countryside       stream + pond (south-east), hills, forests, cliffs, distant mountains
    ///
    /// Re-running regenerates the scene and everything under Assets/_Project/Generated/Village.
    /// A verification report is written to Logs/VillageBuildReport.txt.
    /// </summary>
    public static partial class VillageSceneBuilder
    {
        // ------------------------------------------------------------------ paths
        const string ScenePath = "Assets/_Project/Scenes/Village.unity";
        const string GenDir = "Assets/_Project/Generated/Village";
        const string NetworkManagerPrefabPath = "Assets/_Project/Prefabs/NetworkManager.prefab";
        const string PlayerPrefabPath = "Assets/_Project/Prefabs/NetworkPlayer.prefab";
        const string MultiplayerScenePath = "Assets/_Project/Scenes/MultiplayerTest.unity";
        const string Pack = "Assets/OccaSoftware/Low Poly Fantasy Village/Prefabs/";
        const string PackMaterial = "Assets/OccaSoftware/Low Poly Fantasy Village/Materials/Color.mat";
        const string TankPath = "Assets/GiantWineTank.fbx";
        const string VinePath = "Assets/Grapevine.fbx";
        const string BunchPath = "Assets/GrapeBunch.fbx";
        const string PressPath = "Assets/SmallGrapePress.fbx";
        const string FermenterPath = "Assets/SmallFermentationStation.fbx";
        const string ReportPath = "Logs/VillageBuildReport.txt";

        // ------------------------------------------------------------------ terrain
        const float TerrainSize = 256f;      // centred on the origin
        const float TerrainBaseY = -6f;      // world y of height 0
        const float TerrainHeight = 60f;
        const int HeightRes = 513;           // 0.5 m
        const int FieldRes = 1025;           // 0.25 m distance fields
        const int AlphaRes = 1024;
        const int DetailRes = 512;

        static readonly Rect PlayBounds = Rect.MinMaxRect(-53f, -47f, 53f, 49f);

        // ------------------------------------------------------------------ layout
        static readonly Vector2 SquareC = new Vector2(0f, 4f);
        static float PlazaRadius(float ang) => 12.5f + 1.5f * Mathf.Sin(2f * ang + 0.5f) + 1.0f * Mathf.Sin(3f * ang + 2.1f);

        static readonly Vector2 CourtC = new Vector2(-6f, -14f);
        const float CourtRx = 11f, CourtRz = 7.5f;
        static readonly Vector2 PressPos = new Vector2(-8f, -19f);
        static readonly Vector2 FermenterPos = new Vector2(-1f, -13f);
        static readonly Vector2[] ExpansionSlots = { new Vector2(-8.5f, -9.5f), new Vector2(3.5f, -18.5f) };

        static readonly Rect VineyardRect = Rect.MinMaxRect(-24f, -39f, -4f, -24.5f);
        static readonly float[] RowX = { -20.8f, -17.4f, -14.0f, -10.6f, -7.2f };
        static readonly float[] VineZ = { -28.2f, -30.32f, -32.44f, -34.56f };   // 2.12 m pitch for the 2.10 m vine
        const float VineyardGateX0 = -11.8f, VineyardGateX1 = -5.9f;    // north gate (toward the press)
        const float VineyardWestGapZ0 = -30.5f, VineyardWestGapZ1 = -26.8f;

        static readonly Vector2 PondC = new Vector2(36f, -36f);
        const float PondExtra = 5f;
        static readonly Vector2[] StreamCtrl =
        {
            new Vector2(44f, 128f), new Vector2(40f, 100f), new Vector2(35f, 75f), new Vector2(31f, 55f),
            new Vector2(29f, 40f), new Vector2(27.5f, 24f), new Vector2(29.4f, 6.5f), new Vector2(28f, -8f),
            new Vector2(30.5f, -22f), new Vector2(36f, -36f), new Vector2(43f, -50f), new Vector2(52f, -72f),
            new Vector2(64f, -100f), new Vector2(78f, -130f),
        };
        static readonly Vector2 BridgeHint = new Vector2(29.4f, 6.5f);

        class Road
        {
            public string name; public Vector2[] ctrl; public float width; public bool canCobble; public bool lanterns = true;
            public List<Vector2> pts;
        }

        static readonly Road[] Roads =
        {
            new Road { name = "Street_North",          width = 3.6f, canCobble = true,  ctrl = new[] { V(2f, 14f), V(5f, 20f), V(7.5f, 26f), V(7f, 32f), V(4f, 37f) } },
            new Road { name = "Street_NorthWest",      width = 3.4f, canCobble = true,  ctrl = new[] { V(-8.5f, 12.5f), V(-16f, 16.5f), V(-23f, 22f), V(-28f, 29f), V(-31f, 37f) } },
            new Road { name = "Road_East",             width = 4.0f, canCobble = true,  ctrl = new[] { V(11f, 5f), V(20f, 6f), V(29.4f, 6.5f), V(38f, 8f), V(46f, 9.5f), V(58f, 11f) } },
            new Road { name = "Road_West",             width = 4.0f, canCobble = true,  ctrl = new[] { V(-12f, 5f), V(-20f, 7f), V(-30f, 9.5f), V(-40f, 12.5f), V(-50f, 15f), V(-62f, 17f) } },
            new Road { name = "Lane_SquareToYard",     width = 5.0f, canCobble = true,  lanterns = false, ctrl = new[] { V(-0.5f, -6f), V(-2.5f, -10.5f) } },
            new Road { name = "Lane_YardToVineyard",   width = 3.2f, canCobble = false, lanterns = false, ctrl = new[] { V(-8.5f, -20.5f), V(-8.9f, -24.5f), V(-8.9f, -26.2f) } },
            new Road { name = "Lane_VineyardHeadland", width = 2.4f, canCobble = false, lanterns = false, ctrl = new[] { V(-4.6f, -25.9f), V(-14f, -25.9f), V(-23.4f, -25.9f) } },
            new Road { name = "Lane_Farm",             width = 3.0f, canCobble = false, ctrl = new[] { V(-30f, 9.5f), V(-31.5f, -3f), V(-31.2f, -14f), V(-30f, -23f), V(-26.5f, -28.6f), V(-23.4f, -28.6f) } },
            new Road { name = "Lane_EastHamlet",       width = 3.0f, canCobble = false, ctrl = new[] { V(38f, 8f), V(38.5f, -2f), V(39f, -14f), V(40.5f, -24f) } },
            new Road { name = "Lane_NorthEast",        width = 2.8f, canCobble = false, ctrl = new[] { V(7.5f, 26f), V(12.5f, 28.5f), V(16.5f, 30.5f) } },
            new Road { name = "Path_StreamWalk",       width = 2.0f, canCobble = false, ctrl = new[] { V(22.5f, 5.5f), V(22.5f, -6f), V(23.3f, -17f), V(26.5f, -26.5f), V(29.5f, -30.5f) } },
        };

        struct HouseDef
        {
            public string id, prefab, role; public Vector2 pos; public Vector2 faceTo; public float scale; public bool mirror; public float fixedYaw;
            public bool garden;
        }

        static readonly HouseDef[] HouseDefs =
        {
            new HouseDef { id = "Tavern",          prefab = "House_3", pos = V(-6f, 25f),   faceTo = V(-3f, 10f),  scale = 1.0f,  role = "VillageCenter" },
            new HouseDef { id = "House_NorthEast", prefab = "House_2", pos = V(17f, 21f),   faceTo = V(6f, 12f),   scale = 0.85f, garden = true },
            new HouseDef { id = "House_SouthEast", prefab = "House_1", pos = V(12.5f, -13.5f), faceTo = V(4f, -4f),  scale = 0.85f, mirror = true },
            new HouseDef { id = "House_NorthWest", prefab = "House_3", pos = V(-35.5f, 21f), faceTo = V(-24f, 20f), scale = 0.85f, mirror = true },
            new HouseDef { id = "House_West",      prefab = "House_1", pos = V(-41.5f, -3f), faceTo = V(-34f, 8f),  scale = 0.85f },
            new HouseDef { id = "House_FarWest",   prefab = "House_2", pos = V(-45.5f, 33.5f), faceTo = V(-36f, 26f), scale = 0.8f, mirror = true },
            new HouseDef { id = "House_Hilltop",   prefab = "House_1", pos = V(-3f, 41.5f), faceTo = V(5f, 35f),   scale = 0.8f, mirror = true },
            new HouseDef { id = "House_NorthLane", prefab = "House_3", pos = V(18f, 39.5f), faceTo = V(10f, 30f),  scale = 0.8f },
            new HouseDef { id = "Hamlet_Mill",     prefab = "House_3", pos = V(43f, 20f),   faceTo = V(32f, 8f),   scale = 0.85f, garden = true },
            new HouseDef { id = "Hamlet_Cottage",  prefab = "House_2", pos = V(47f, -9.5f), faceTo = V(38f, -5f),  scale = 0.75f },
            new HouseDef { id = "Farmhouse",       prefab = "House_2", pos = V(-38f, -20f), faceTo = V(-28f, -20f), scale = 0.8f },
            new HouseDef { id = "WineryHall",      prefab = "House_1", pos = V(-20.5f, -15f), faceTo = V(0f, -15f), scale = 0.95f, role = "Production" },
        };

        static readonly Vector2[] SpawnCandidates = { V(9f, 1.5f), V(10.3f, 4.6f), V(9.2f, 7.8f), V(6.8f, 10.2f) };

        static Vector2 V(float x, float z) => new Vector2(x, z);

        // ------------------------------------------------------------------ build state
        static StringBuilder report;
        static int warnings;
        static Terrain terrain;
        static System.Random rng;
        static readonly Dictionary<string, GameObject> prefabCache = new Dictionary<string, GameObject>();
        static readonly List<string> missing = new List<string>();
        static readonly HashSet<string> usedAssets = new HashSet<string>();

        // ================================================================== entry

        [MenuItem("Tools/Vineyard/Build Village Scene")]
        public static void BuildMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Village", "Exit Play Mode first.", "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Build();
        }

        /// <summary>Non-interactive entry point (also used by automation).</summary>
        public static string Build()
        {
            report = new StringBuilder();
            warnings = 0;
            rng = new System.Random(20261120);
            prefabCache.Clear(); missing.Clear(); usedAssets.Clear();
            blockers.Clear(); lanternSpots.Clear(); localBoundsCache.Clear();
            bridgeInfo = null; terrain = null; allHouses = new List<HouseInfo>();
            Log($"Village build {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                Progress("Preparing assets", 0.02f);
                EnsureFolder(GenDir);
                BackupExistingScene();
                GameObject nmPrefab = GetOrCreateNetworkManagerPrefab();
                AssetDatabase.DeleteAsset(GenDir + "/StationPad.mat"); // obsolete placeholder material

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                // ---------------- hierarchy
                var root = new GameObject("Village").transform;
                var env = Child(root, "Environment");
                H.Terrain = Child(env, "Terrain");
                H.Mountains = Child(env, "Mountains");
                H.Cliffs = Child(env, "Cliffs");
                H.Rocks = Child(env, "Rocks");
                H.Vegetation = Child(env, "Vegetation");
                H.Trees = Child(H.Vegetation, "Trees");
                H.Flowers = Child(H.Vegetation, "Flowers");
                H.Water = Child(env, "Water");
                H.Bridges = Child(env, "Bridges");
                H.Bounds = Child(env, "MapBounds");

                var center = Child(root, "VillageCenter");
                H.Square = Child(center, "VillageSquare");
                H.CenterBuildings = Child(center, "Buildings");
                H.CenterDecor = Child(center, "Decorations");

                var res = Child(root, "ResidentialArea");
                H.ResBuildings = Child(res, "Buildings");
                H.Roads = Child(res, "Roads");
                H.ResFences = Child(res, "Fences");
                H.ResDecor = Child(res, "Decorations");

                var vin = Child(root, "Vineyard");
                H.GrapeRows = Child(vin, "GrapeRows");
                H.Soil = Child(vin, "Soil");
                H.VinFences = Child(vin, "Fences");
                H.VinDecor = Child(vin, "Decorations");

                var prod = Child(root, "ProductionArea");
                H.Press = Child(prod, "CrushingPress");
                H.Fermenter = Child(prod, "FermentationMachine");
                H.Storage = Child(prod, "Storage");
                H.ProdDecor = Child(prod, "Decorations");
                H.ProdBuildings = Child(prod, "Buildings");
                H.Expansion = Child(prod, "ExpansionSlots");

                var lighting = Child(root, "Lighting");
                var spawns = Child(root, "PlayerSpawns");
                H.Networking = Child(root, "Networking");

                // ---------------- layout + terrain
                Progress("Layout", 0.08f);
                PrepareRoads();
                PrepareStream();
                var houses = PrepareHouses();          // instantiated now so the terrain can be shaped around them
                allHouses = houses;
                var bridge = PrepareBridge();          // ditto (bank heights)
                bridgeInfo = bridge;

                Progress("Terrain heights", 0.15f);
                BuildDistanceFields();
                BuildTerrain(houses, bridge);

                Progress("Buildings", 0.35f);
                SettleHouses(houses);

                Progress("Giant wine bottle", 0.4f);
                var bottle = BuildBottle(center, out Vector3 inputAccess, out Vector3 outputAccess);

                Progress("Village square", 0.45f);
                BuildSquare(houses);

                Progress("Production area", 0.5f);
                var stations = BuildProduction();

                Progress("Vineyard", 0.55f);
                var aisles = BuildVineyard();

                Progress("Streets & houses", 0.6f);
                DressRoads(houses);
                DressHouses(houses);
                DressFields();
                RetainingStones(houses);

                Progress("Water & bridge", 0.65f);
                BuildWater();
                DressStream(bridge);

                Progress("Nature", 0.7f);
                BuildNature(houses);
                BuildMountains();
                BuildMapBounds();

                Progress("Lighting", 0.78f);
                BuildLighting(lighting);
                BuildMenuCamera(root);

                var spawnPoints = BuildSpawns(spawns);

                if (nmPrefab != null)
                {
                    // Must stay a root object: NetworkManager uses DontDestroyOnLoad.
                    var nm = (GameObject)PrefabUtility.InstantiatePrefab(nmPrefab, scene);
                    nm.name = "NetworkManager";
                    var note = new GameObject("NetworkManager is a scene-root object (DontDestroyOnLoad)");
                    note.transform.SetParent(H.Networking, false);
                    Log($"NetworkManager: prefab instance of {NetworkManagerPrefabPath} at scene root.");
                }

                Progress("Static flags", 0.82f);
                foreach (var t in new[] { env, H.Square, H.CenterBuildings, H.CenterDecor, res, H.Soil, H.VinFences, H.VinDecor,
                                          H.Storage, H.ProdDecor, H.ProdBuildings, H.Expansion })
                    SetStaticRecursive(t.gameObject);

                Progress("Verification + NavMesh", 0.86f);
                Physics.SyncTransforms();
                Verify(root, houses, spawnPoints, inputAccess, outputAccess, stations, aisles, bridge);

                // Production loop (stations, interactables, effects) — kept in WinerySetup so it can also run on its own.
                Log("Winery gameplay: " + WinerySetup.SetupScene());

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AddSceneToBuildSettingsFirst(ScenePath);
                Log($"Saved {ScenePath} (build index 0).");
                LogAssetsUsed(root);
            }
            catch (System.Exception e)
            {
                Warn("BUILD FAILED: " + e);
                Debug.LogException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Log($"Done in {sw.Elapsed.TotalSeconds:F1} s with {warnings} warning(s).");
                Directory.CreateDirectory("Logs");
                File.WriteAllText(ReportPath, report.ToString());
                Debug.Log($"[Village] Build finished with {warnings} warning(s). Report: {Path.GetFullPath(ReportPath)}");
            }
            return $"{warnings} warnings";
        }

        static class H
        {
            public static Transform Terrain, Mountains, Cliffs, Rocks, Vegetation, Trees, Flowers, Water, Bridges, Bounds;
            public static Transform Square, CenterBuildings, CenterDecor;
            public static Transform ResBuildings, Roads, ResFences, ResDecor;
            public static Transform GrapeRows, Soil, VinFences, VinDecor;
            public static Transform Press, Fermenter, Storage, ProdDecor, ProdBuildings, Expansion;
            public static Transform Networking;
        }

        static void Progress(string what, float t) => EditorUtility.DisplayProgressBar("Village", what, t);

        static void BackupExistingScene()
        {
            if (!File.Exists(ScenePath)) return;
            string dir = "Assets/_Project/Scenes/Backups";
            EnsureFolder(dir);
            string dst = $"{dir}/Village_BeforeRebuild_{System.DateTime.Now:yyyyMMdd_HHmmss}.unity";
            AssetDatabase.CopyAsset(ScenePath, dst);
            Log($"Backup of the previous scene file: {dst}");
        }

        // ================================================================== networking

        static GameObject GetOrCreateNetworkManagerPrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(NetworkManagerPrefabPath);
            if (existing != null && existing.GetComponent<NetworkManager>() != null)
            {
                Log("Reusing existing NetworkManager prefab (not modified).");
                return existing;
            }

            GameObject source = null;
            Scene mpScene = default;
            bool openedMp = false;
            if (File.Exists(MultiplayerScenePath))
            {
                mpScene = SceneManager.GetSceneByPath(MultiplayerScenePath);
                if (!mpScene.isLoaded)
                {
                    mpScene = EditorSceneManager.OpenScene(MultiplayerScenePath, OpenSceneMode.Additive);
                    openedMp = true;
                }
                source = mpScene.GetRootGameObjects().FirstOrDefault(g => g.GetComponent<NetworkManager>() != null);
            }

            GameObject temp;
            if (source != null)
            {
                temp = Object.Instantiate(source);
                temp.name = "NetworkManager";
                Log("NetworkManager copied from MultiplayerTest.unity (that scene is not modified).");
            }
            else
            {
                Warn("No NetworkManager found in MultiplayerTest.unity, creating one with the default setup.");
                temp = new GameObject("NetworkManager");
                var manager = temp.AddComponent<GameNetworkManager>();
                var transport = temp.AddComponent<kcp2k.KcpTransport>();
                temp.AddComponent<ConnectionUI>();
                manager.transport = transport;
                manager.playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
                manager.playerSpawnMethod = PlayerSpawnMethod.RoundRobin;
                manager.maxConnections = GameNetworkManager.MaxPlayers;
            }
            var prefab = PrefabUtility.SaveAsPrefabAsset(temp, NetworkManagerPrefabPath);
            Object.DestroyImmediate(temp);
            if (openedMp) EditorSceneManager.CloseScene(mpScene, true);
            return prefab;
        }

        // ================================================================== layout preparation

        static List<Vector2> streamPts;     // resampled, north -> south
        static float[] streamRef;           // reference (bank) height along the stream, non-increasing downstream

        static void PrepareRoads()
        {
            foreach (var r in Roads) r.pts = CatmullRom(r.ctrl, 0.75f);
        }

        static void PrepareStream()
        {
            streamPts = CatmullRom(StreamCtrl, 1f);
            int n = streamPts.Count;
            var raw = new float[n];
            for (int i = 0; i < n; i++) raw[i] = BaseHeight(streamPts[i].x, streamPts[i].y);
            streamRef = new float[n];
            for (int i = 0; i < n; i++)
            {
                float s = 0; int c = 0;
                for (int k = -6; k <= 6; k++) { int j = Mathf.Clamp(i + k, 0, n - 1); s += raw[j]; c++; }
                streamRef[i] = s / c;
            }
            for (int i = 1; i < n; i++) streamRef[i] = Mathf.Min(streamRef[i], streamRef[i - 1] - 0.005f);
        }

        // ================================================================== terrain

        static float Smooth01(float a, float b, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, x));

        static float Fbm(float x, float z)
        {
            float v = 0, amp = 0.5f, f = 1f;
            for (int o = 0; o < 4; o++) { v += Mathf.PerlinNoise(x * f + o * 17.3f, z * f + o * 9.1f) * amp; f *= 2.03f; amp *= 0.5f; }
            return v / 0.9375f;
        }

        /// <summary>Natural landform before any flattening / carving (world metres).</summary>
        static float BaseHeight(float x, float z)
        {
            float h = (Mathf.PerlinNoise(x * 0.03f + 11.3f, z * 0.03f + 5.1f) - 0.5f) * 1.3f
                    + (Mathf.PerlinNoise(x * 0.09f + 3.3f, z * 0.09f + 8.8f) - 0.5f) * 0.35f;
            h += Smooth01(12f, 46f, z) * 2.8f;                                        // northern terrace
            h += Smooth01(-12f, -48f, x) * 1.4f;                                      // western rise
            h += Smooth01(-22f, -46f, z) * 2.0f * (1f - Smooth01(5f, 25f, x));       // south-west vineyard slope
            h -= Smooth01(16f, 40f, x) * 0.5f;                                        // eastern meadow toward the stream

            float ang = Mathf.Atan2(z, x);
            float r = Mathf.Sqrt(x * x + z * z);
            float warp = (Mathf.PerlinNoise(Mathf.Cos(ang) * 1.7f + 4f, Mathf.Sin(ang) * 1.7f + 2f) - 0.5f) * 26f;
            float t = Smooth01(52f, 105f, r + warp);
            float open = Mathf.Clamp01(Mathf.Cos(ang + 0.95f));                        // lower toward the south-east valley
            float hills = (5f + 24f * Fbm(x * 0.011f + 20f, z * 0.011f + 40f)) * (1f - 0.6f * open);
            hills += Mathf.Max(0f, Fbm(x * 0.035f + 3f, z * 0.035f + 7f) - 0.5f) * 14f;
            return h + t * hills;
        }

        // distance fields (FieldRes x FieldRes, 0.25 m)
        static float[] fRoadEdge;   // distance to the nearest road edge (negative inside)
        static int[] fRoadId;
        static float[] fStream;     // distance to stream centre line (pond-aware)
        static float[] fStreamRef;  // reference height of the nearest stream point

        static float FieldCell => TerrainSize / (FieldRes - 1);
        static Vector2 FieldToWorld(int x, int z) => new Vector2(-TerrainSize / 2f + x * FieldCell, -TerrainSize / 2f + z * FieldCell);

        static float SampleField(float[] f, float x, float z)
        {
            int ix = Mathf.Clamp(Mathf.RoundToInt((x + TerrainSize / 2f) / FieldCell), 0, FieldRes - 1);
            int iz = Mathf.Clamp(Mathf.RoundToInt((z + TerrainSize / 2f) / FieldCell), 0, FieldRes - 1);
            return f[iz * FieldRes + ix];
        }
        static int SampleFieldId(float x, float z)
        {
            int ix = Mathf.Clamp(Mathf.RoundToInt((x + TerrainSize / 2f) / FieldCell), 0, FieldRes - 1);
            int iz = Mathf.Clamp(Mathf.RoundToInt((z + TerrainSize / 2f) / FieldCell), 0, FieldRes - 1);
            return fRoadId[iz * FieldRes + ix];
        }

        static void BuildDistanceFields()
        {
            int n = FieldRes * FieldRes;
            fRoadEdge = Enumerable.Repeat(999f, n).ToArray();
            fRoadId = Enumerable.Repeat(-1, n).ToArray();
            fStream = Enumerable.Repeat(999f, n).ToArray();
            fStreamRef = new float[n];

            for (int ri = 0; ri < Roads.Length; ri++)
                Stamp(Roads[ri].pts, 10f, Roads[ri].width / 2f, fRoadEdge, null, null, fRoadId, ri);
            Stamp(streamPts, 40f, 0f, fStream, fStreamRef, streamRef, null, 0);

            // Pond: a widening of the stream, shares the reference height of the stream at its centre.
            int pi = NearestIndex(streamPts, PondC);
            float pr = streamRef[pi];
            float R = 26f;
            ForCells(PondC - Vector2.one * R, PondC + Vector2.one * R, (k, p) =>
            {
                float d = Mathf.Max(0f, Vector2.Distance(p, PondC) - PondExtra * (0.85f + 0.3f * Mathf.PerlinNoise(p.x * 0.2f, p.y * 0.2f)));
                if (d < fStream[k]) { fStream[k] = d; fStreamRef[k] = pr; }
            });
        }

        static void Stamp(List<Vector2> pts, float radius, float sub, float[] dist, float[] aux, float[] auxVals, int[] ids, int id)
        {
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                var min = Vector2.Min(a, b) - Vector2.one * radius;
                var max = Vector2.Max(a, b) + Vector2.one * radius;
                ForCells(min, max, (k, p) =>
                {
                    float d = DistToSegment(p, a, b, out float t) - sub;
                    if (d < dist[k])
                    {
                        dist[k] = d;
                        if (aux != null) aux[k] = Mathf.Lerp(auxVals[i], auxVals[i + 1], t);
                        if (ids != null) ids[k] = id;
                    }
                });
            }
        }

        static void ForCells(Vector2 min, Vector2 max, System.Action<int, Vector2> f)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt((min.x + TerrainSize / 2f) / FieldCell), 0, FieldRes - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((max.x + TerrainSize / 2f) / FieldCell), 0, FieldRes - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((min.y + TerrainSize / 2f) / FieldCell), 0, FieldRes - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((max.y + TerrainSize / 2f) / FieldCell), 0, FieldRes - 1);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    f(z * FieldRes + x, FieldToWorld(x, z));
        }

        static float RoadEdge(float x, float z) => SampleField(fRoadEdge, x, z);
        static float StreamDist(float x, float z) => SampleField(fStream, x, z);

        static float PlazaSignedDist(Vector2 p)
        {
            Vector2 d = p - SquareC;
            return d.magnitude - PlazaRadius(Mathf.Atan2(d.y, d.x));
        }
        static float CourtEllipse(Vector2 p)
        {
            Vector2 d = p - CourtC;
            return Mathf.Sqrt((d.x / CourtRx) * (d.x / CourtRx) + (d.y / CourtRz) * (d.y / CourtRz));
        }

        /// <summary>Stream bank profile relative to the reference height: bed, bank, then valley walls.</summary>
        static float StreamProfile(float d)
        {
            if (d < 1.6f) return -1.8f;
            if (d < 4.4f) return -1.8f + 1.8f * Mathf.SmoothStep(0f, 1f, (d - 1.6f) / 2.8f);
            return (d - 4.4f) * 0.5f;
        }

        static float plazaH, courtH;

        static float FinalHeight(float x, float z, List<HouseInfo> houses, BridgeInfo bridge)
        {
            var p = new Vector2(x, z);
            float h = BaseHeight(x, z);

            // Square and production yard: flat, with soft edges.
            float pm = 1f - Smooth01(-0.5f, 6f, PlazaSignedDist(p));
            h = Mathf.Lerp(h, plazaH, pm);
            float cm = 1f - Smooth01(1.0f, 1.6f, CourtEllipse(p));
            h = Mathf.Lerp(h, courtH, cm);

            // Building pads.
            foreach (var hs in houses)
            {
                float d = hs.ObbDistance(p);
                if (d > 6f) continue;
                h = Mathf.Lerp(h, hs.padHeight, 1f - Smooth01(0.6f, 5f, d));
            }

            // Roads: soften bumps a little (follow a smoothed version of the ground).
            float re = RoadEdge(x, z);
            if (re < 1.5f)
            {
                float hs = (BaseHeight(x + 1.5f, z) + BaseHeight(x - 1.5f, z) + BaseHeight(x, z + 1.5f) + BaseHeight(x, z - 1.5f)) * 0.25f;
                float w = (1f - Smooth01(-0.5f, 1.5f, re)) * 0.6f * (1f - pm) * (1f - cm);
                h = Mathf.Lerp(h, hs, w);
            }

            // Bridge abutments: ground meets the deck ends.
            if (bridge != null)
            {
                foreach (var e in bridge.ends)
                {
                    float d = Vector2.Distance(p, e.xz);
                    if (d < 6f) h = Mathf.Lerp(h, e.height, 1f - Smooth01(1.8f, 6f, d));
                }
            }

            // Stream channel and pond.
            float sd = StreamDist(x, z);
            if (sd < 40f)
            {
                float sref = SampleField(fStreamRef, x, z);
                h = Mathf.Min(h, sref + StreamProfile(sd));
            }
            return h;
        }

        static TerrainLayer[] layers;
        const int LGrass = 0, LGrassDark = 1, LMeadow = 2, LDirt = 3, LSoil = 4, LCobble = 5, LGravel = 6;

        static void BuildTerrain(List<HouseInfo> houses, BridgeInfo bridge)
        {
            plazaH = BaseHeight(SquareC.x, SquareC.y);
            courtH = Mathf.Lerp(BaseHeight(CourtC.x, CourtC.y), plazaH, 0.5f);

            layers = BuildTerrainLayers();
            // All texture imports must happen before the terrain is filled: an import reloads the TerrainData asset
            // from disk and silently discards unsaved heights / splat.
            grassTuftTex = MakeGrassTuftTexture(GenDir + "/Textures/GrassTuft.png");

            string dataPath = GenDir + "/VillageTerrainData.asset";
            AssetDatabase.DeleteAsset(dataPath);
            var fresh = new TerrainData { heightmapResolution = HeightRes };
            fresh.size = new Vector3(TerrainSize, TerrainHeight, TerrainSize);
            fresh.alphamapResolution = AlphaRes;
            fresh.baseMapResolution = 1024;
            fresh.terrainLayers = layers;
            // Persist the empty asset first and work on the persisted instance: data written into a TerrainData
            // before / right after CreateAsset was being discarded (flat heights or all-grass splat textures).
            AssetDatabase.CreateAsset(fresh, dataPath);
            AssetDatabase.SaveAssets();
            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(dataPath);

            var go = Terrain.CreateTerrainGameObject(data);
            go.name = "VillageTerrain";
            go.transform.SetParent(H.Terrain, false);
            go.transform.position = new Vector3(-TerrainSize / 2f, TerrainBaseY, -TerrainSize / 2f);
            terrain = go.GetComponent<Terrain>();
            terrain.drawInstanced = true;
            terrain.basemapDistance = 160f;
            terrain.heightmapPixelError = 3f;
            terrain.detailObjectDistance = 75f;
            terrain.detailObjectDensity = 1f;
            terrain.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
            terrain.materialTemplate = TerrainMaterial();

            // Heights.
            var heights = new float[HeightRes, HeightRes];
            var worldH = new float[HeightRes, HeightRes];
            float cell = TerrainSize / (HeightRes - 1);
            for (int z = 0; z < HeightRes; z++)
                for (int x = 0; x < HeightRes; x++)
                {
                    float wx = -TerrainSize / 2f + x * cell, wz = -TerrainSize / 2f + z * cell;
                    float hh = FinalHeight(wx, wz, houses, bridge);
                    worldH[z, x] = hh;
                    heights[z, x] = Mathf.Clamp01((hh - TerrainBaseY) / TerrainHeight);
                }
            terrain.terrainData.SetHeights(0, 0, heights);
            CheckTerrain("after heights", worldH);

            var maps = PaintSplat(houses, worldH);
            terrain.terrainData.SetAlphamaps(0, 0, maps);
            WriteSplatTextures(terrain.terrainData, maps);
            foreach (var at in terrain.terrainData.alphamapTextures)
            {
                bool inAsset = AssetDatabase.Contains(at);
                Log($"Splat texture {at.name}: part of asset={inAsset} ({AssetDatabase.GetAssetPath(at)})");
                if (!inAsset) AssetDatabase.AddObjectToAsset(at, terrain.terrainData);
            }
            CheckTerrain("after splat", worldH);
            PaintDetails(terrain.terrainData, maps);
            CheckTerrain("after details", worldH);
            EditorUtility.SetDirty(terrain.terrainData);
            AssetDatabase.SaveAssets();
            CheckTerrain("after save", worldH);
            Physics.SyncTransforms();
            Log($"Terrain: {TerrainSize} m square, heightmap {HeightRes} (0.5 m), splat {AlphaRes}, {layers.Length} layers, square at y={plazaH:F2}, yard at y={courtH:F2}.");
        }

        static Texture2D grassTuftTex;

        /// <summary>Mirror the weights into the splat textures' CPU pixels so the asset serializes them.</summary>
        static void WriteSplatTextures(TerrainData d, float[,,] maps)
        {
            int res = d.alphamapResolution, L = d.alphamapLayers;
            var tex = d.alphamapTextures;
            for (int ti = 0; ti < tex.Length; ti++)
            {
                var px = new Color[res * res];
                int b = ti * 4;
                for (int z = 0; z < res; z++)
                    for (int x = 0; x < res; x++)
                        px[z * res + x] = new Color(b < L ? maps[z, x, b] : 0f, b + 1 < L ? maps[z, x, b + 1] : 0f,
                                                    b + 2 < L ? maps[z, x, b + 2] : 0f, b + 3 < L ? maps[z, x, b + 3] : 0f);
                tex[ti].SetPixels(px);
                tex[ti].Apply();
                EditorUtility.SetDirty(tex[ti]);
            }
            d.SetBaseMapDirty();
        }

        static void CheckTerrain(string stage, float[,] worldH)
        {
            int i = HeightRes / 2 + 40, j = HeightRes / 2 + 60;   // a point north-east of the square
            float cell = TerrainSize / (HeightRes - 1);
            float x = -TerrainSize / 2f + j * cell, z = -TerrainSize / 2f + i * cell;
            float got = GroundY(x, z), want = worldH[i, j];
            var a = terrain.terrainData.GetAlphamaps(AlphaRes / 2, AlphaRes / 2 + 8, 1, 1);
            var t1 = terrain.terrainData.alphamapTextures.Length > 1 ? terrain.terrainData.alphamapTextures[1].GetPixel(AlphaRes / 2, AlphaRes / 2 + 8) : Color.clear;
            string msg = $"Terrain check {stage}: height at ({x:F0},{z:F0}) {got:F2} (expected {want:F2}); square splat cobble={a[0, 0, LCobble]:F2}, texture={t1.g:F2}";
            if (Mathf.Abs(got - want) > 0.05f) Warn(msg); else Log(msg);
        }

        static Material TerrainMaterial()
        {
            string path = GenDir + "/VillageTerrain.mat";
            var shader = Shader.Find("Universal Render Pipeline/Terrain/Lit");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
            else mat.shader = shader;
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static float[,,] PaintSplat(List<HouseInfo> houses, float[,] worldH)
        {
            int L = layers.Length;
            var maps = new float[AlphaRes, AlphaRes, L];
            var w = new float[L];
            float hCell = TerrainSize / (HeightRes - 1);

            void Over(int layer, float a)
            {
                if (a <= 0f) return;
                a = Mathf.Clamp01(a);
                for (int i = 0; i < L; i++) w[i] *= (1f - a);
                w[layer] += a;
            }

            for (int z = 0; z < AlphaRes; z++)
                for (int x = 0; x < AlphaRes; x++)
                {
                    float wx = -TerrainSize / 2f + x / (float)(AlphaRes - 1) * TerrainSize;
                    float wz = -TerrainSize / 2f + z / (float)(AlphaRes - 1) * TerrainSize;
                    var p = new Vector2(wx, wz);
                    float nA = Mathf.PerlinNoise(wx * 0.045f + 7f, wz * 0.045f + 3f);
                    float nB = Mathf.PerlinNoise(wx * 0.16f + 1.7f, wz * 0.16f + 9.2f);
                    float nC = Mathf.PerlinNoise(wx * 0.7f + 5.5f, wz * 0.7f + 2.4f);
                    float jitter = (nC - 0.5f) * 0.7f + (nB - 0.5f) * 0.7f;

                    for (int i = 0; i < L; i++) w[i] = 0f;
                    float meadow = Mathf.Clamp01((nA - 0.56f) * 3.5f);
                    float dark = Mathf.Clamp01((0.40f - nA) * 3.5f);
                    w[LGrass] = 1f - meadow - dark; w[LMeadow] = meadow; w[LGrassDark] = dark;

                    // Slopes (hills): darker grass, then exposed dirt on steep parts.
                    int hx = Mathf.Clamp(Mathf.RoundToInt((wx + TerrainSize / 2f) / hCell), 1, HeightRes - 2);
                    int hz = Mathf.Clamp(Mathf.RoundToInt((wz + TerrainSize / 2f) / hCell), 1, HeightRes - 2);
                    float gx = (worldH[hz, hx + 1] - worldH[hz, hx - 1]) / (2f * hCell);
                    float gz = (worldH[hz + 1, hx] - worldH[hz - 1, hx]) / (2f * hCell);
                    float slope = Mathf.Sqrt(gx * gx + gz * gz);
                    float r = p.magnitude;
                    Over(LGrassDark, Smooth01(55f, 85f, r) * 0.6f);
                    Over(LGrassDark, Smooth01(0.35f, 0.6f, slope + jitter * 0.1f));
                    Over(LDirt, Smooth01(0.75f, 1.1f, slope + jitter * 0.15f) * 0.8f);

                    // Worn ground around buildings.
                    foreach (var hs in houses)
                    {
                        float d = hs.ObbDistance(p);
                        if (d < 2.5f) Over(LDirt, (1f - Smooth01(0.2f, 1.6f, d + jitter)) * 0.55f);
                    }

                    // Production yard: compacted dirt with gravel patches.
                    float ce = CourtEllipse(p) + jitter * 0.08f;
                    float cmask = 1f - Smooth01(0.92f, 1.08f, ce);
                    Over(LDirt, cmask);
                    Over(LGravel, cmask * Smooth01(0.5f, 0.7f, nB) * 0.6f);

                    // Roads: dirt, cobbled near the square.
                    float re = SampleField(fRoadEdge, wx, wz) + jitter * 0.5f;
                    float road = 1f - Smooth01(-0.45f, 0.45f, re);
                    if (road > 0f)
                    {
                        int id = SampleFieldId(wx, wz);
                        bool canCobble = id >= 0 && Roads[id].canCobble;
                        float cob = canCobble ? 1f - Smooth01(22f, 30f, Vector2.Distance(p, SquareC) + jitter * 5f) : 0f;
                        Over(LDirt, road * (1f - cob));
                        Over(LCobble, road * cob);
                        Over(LGravel, road * (1f - cob) * Smooth01(0.62f, 0.8f, nC) * 0.5f);
                    }

                    // Village square: cobbles with grass growing in at the edges.
                    float ps = PlazaSignedDist(p) + jitter * 0.9f;
                    float plaza = 1f - Smooth01(-0.4f, 0.4f, ps);
                    Over(LCobble, plaza);
                    if (plaza > 0f && ps > -3f) Over(LGrass, plaza * Smooth01(0.66f, 0.8f, nC) * 0.8f);

                    // Planting beds on the square.
                    foreach (var b in SquareBeds)
                    {
                        float d = Vector2.Distance(p, b) - 1.9f + jitter * 0.3f;
                        if (d < 0.5f) Over(LGrass, 1f - Smooth01(-0.2f, 0.4f, d));
                    }

                    // Vineyard: tilled soil under each row, meadow in the aisles.
                    if (p.x > VineyardRect.xMin - 1f && p.x < VineyardRect.xMax + 1f && p.y > VineyardRect.yMin - 1f && p.y < VineyardRect.yMax + 1f)
                    {
                        float inside = (1f - Smooth01(-0.5f, 0.5f, RectDist(VineyardRect, p) + jitter));
                        Over(LMeadow, inside * 0.5f);
                        foreach (float rx in RowX)
                        {
                            float zTop = VineZ[0] + 1.6f, zBot = VineZ[VineZ.Length - 1] - 1.6f;
                            float dx = Mathf.Abs(p.x - rx) - 0.85f;
                            float dz = Mathf.Max(p.y - zTop, zBot - p.y, 0f);
                            float soil = (1f - Smooth01(-0.15f, 0.3f, dx + jitter * 0.25f)) * (1f - Smooth01(0f, 0.8f, dz + jitter * 0.3f));
                            Over(LSoil, soil);
                        }
                    }

                    // Fenced fields: furrows of tilled soil.
                    foreach (var f in Fields)
                    {
                        float fd = RectDist(f.rect, p);
                        if (fd > 1.2f) continue;
                        float inner = f.rect.Contains(p) ? Mathf.Min(p.x - f.rect.xMin, f.rect.xMax - p.x, p.y - f.rect.yMin, f.rect.yMax - p.y) : 0f;
                        float inMask = f.rect.Contains(p) ? Smooth01(0f, 0.6f, inner + jitter * 0.2f) : 0f;
                        Over(LDirt, (1f - Smooth01(0f, 1.2f, fd)) * 0.5f);
                        float c = f.rowsAlongX ? p.y - f.rect.yMin : p.x - f.rect.xMin;
                        float m = Mathf.Repeat(c, 1.5f);
                        Over(LSoil, inMask * (m < 0.85f ? 1f : 0.35f));
                    }

                    // Stream banks: gravel near water, lush grass beyond.
                    float sd = SampleField(fStream, wx, wz) + jitter * 0.6f;
                    Over(LGrassDark, (1f - Smooth01(4.5f, 7.5f, sd)) * 0.8f);
                    Over(LGravel, 1f - Smooth01(3.0f, 4.2f, sd));
                    Over(LDirt, (1f - Smooth01(0.5f, 1.8f, sd)) * 0.5f);

                    float sum = 0f;
                    for (int i = 0; i < L; i++) sum += w[i];
                    for (int i = 0; i < L; i++) maps[z, x, i] = w[i] / Mathf.Max(sum, 1e-4f);
                }
            return maps;
        }

        static void PaintDetails(TerrainData data, float[,,] maps)
        {
            var grassTex = grassTuftTex;
            var protos = new List<DetailPrototype>
            {
                new DetailPrototype
                {
                    prototypeTexture = grassTex, usePrototypeMesh = false, renderMode = DetailRenderMode.Grass,
                    minWidth = 0.4f, maxWidth = 0.7f, minHeight = 0.22f, maxHeight = 0.42f, noiseSpread = 0.3f,
                    healthyColor = new Color(0.62f, 0.78f, 0.38f), dryColor = new Color(0.72f, 0.74f, 0.40f),
                    useInstancing = false, alignToGround = 0.6f,
                },
            };
            string[] flowerNames = { "Flower_1", "Flower_3", "Flower_5" };
            foreach (var fn in flowerNames)
            {
                var fp = LoadPack(fn);
                if (fp == null) continue;
                protos.Add(new DetailPrototype
                {
                    prototype = fp, usePrototypeMesh = true, renderMode = DetailRenderMode.VertexLit, useInstancing = true,
                    minWidth = 0.35f, maxWidth = 0.6f, minHeight = 0.35f, maxHeight = 0.6f, noiseSpread = 0.4f,
                    healthyColor = Color.white, dryColor = Color.white, alignToGround = 0.5f,
                });
            }
            data.detailPrototypes = protos.ToArray();
            data.SetDetailResolution(DetailRes, 32);
            data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);

            var grass = new int[DetailRes, DetailRes];
            var flowers = new int[protos.Count - 1][,];
            for (int i = 0; i < flowers.Length; i++) flowers[i] = new int[DetailRes, DetailRes];
            var drng = new System.Random(77);
            int total = 0, totalF = 0;
            for (int z = 0; z < DetailRes; z++)
                for (int x = 0; x < DetailRes; x++)
                {
                    float wx = -TerrainSize / 2f + (x + 0.5f) / DetailRes * TerrainSize;
                    float wz = -TerrainSize / 2f + (z + 0.5f) / DetailRes * TerrainSize;
                    float r = Mathf.Sqrt(wx * wx + wz * wz);
                    if (r > 105f) continue;
                    int ax = Mathf.Clamp(Mathf.RoundToInt((x + 0.5f) / DetailRes * (AlphaRes - 1)), 0, AlphaRes - 1);
                    int az = Mathf.Clamp(Mathf.RoundToInt((z + 0.5f) / DetailRes * (AlphaRes - 1)), 0, AlphaRes - 1);
                    float green = maps[az, ax, LGrass] + maps[az, ax, LGrassDark] + maps[az, ax, LMeadow];
                    if (green < 0.9f) continue;
                    if (RoadEdge(wx, wz) < 1.0f || StreamDist(wx, wz) < 3.8f || CourtEllipse(new Vector2(wx, wz)) < 1.2f) continue;
                    if (Fields.Any(f => RectDist(f.rect, new Vector2(wx, wz)) < 0.6f)) continue;
                    if (PlazaSignedDist(new Vector2(wx, wz)) < 0.5f && !SquareBeds.Any(b => Vector2.Distance(b, new Vector2(wx, wz)) < 1.7f)) continue;
                    float n = Mathf.PerlinNoise(wx * 0.11f + 30f, wz * 0.11f + 12f);
                    float dens = Smooth01(0.3f, 0.75f, n) * (1f - Smooth01(70f, 105f, r));
                    int c = Mathf.FloorToInt(dens * 3f + (float)drng.NextDouble());
                    grass[z, x] = c; total += c;
                    // Wild flowers in meadow patches.
                    float fm = maps[az, ax, LMeadow];
                    float fn = Mathf.PerlinNoise(wx * 0.07f + 90f, wz * 0.07f + 40f);
                    if (fm > 0.3f && fn > 0.55f && drng.NextDouble() < 0.18f)
                    {
                        flowers[drng.Next(flowers.Length)][z, x] = 1; totalF++;
                    }
                }
            data.SetDetailLayer(0, 0, 0, grass);
            for (int i = 0; i < flowers.Length; i++) data.SetDetailLayer(0, 0, i + 1, flowers[i]);
            Log($"Terrain details: ~{total} grass tufts, ~{totalF} wild flowers (instanced Flower meshes from the pack).");
        }

        // ================================================================== houses (layout)

        class HouseInfo
        {
            public HouseDef def; public GameObject go; public Vector2 center; public float yaw; public Vector2 half; public float padHeight;
            public Vector2 Forward => new Vector2(Mathf.Sin(yaw * Mathf.Deg2Rad), Mathf.Cos(yaw * Mathf.Deg2Rad));
            public Vector2 Right => new Vector2(Forward.y, -Forward.x);
            public Vector2 DoorPoint => center + Forward * (half.y + 0.6f);
            public float ObbDistance(Vector2 p)
            {
                Vector2 d = p - center;
                float lx = Vector2.Dot(d, Right), lz = Vector2.Dot(d, Forward);
                float ex = Mathf.Max(Mathf.Abs(lx) - half.x, 0f), ez = Mathf.Max(Mathf.Abs(lz) - half.y, 0f);
                return Mathf.Sqrt(ex * ex + ez * ez);
            }
        }

        static List<HouseInfo> PrepareHouses()
        {
            var list = new List<HouseInfo>();
            foreach (var d in HouseDefs)
            {
                var prefab = LoadPack(d.prefab);
                if (prefab == null) continue;
                Transform parent = d.role == "VillageCenter" ? H.CenterBuildings : d.role == "Production" ? H.ProdBuildings : H.ResBuildings;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                go.name = $"{d.id} ({d.prefab})";
                Vector2 dir = d.faceTo - d.pos;
                float yaw = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;
                go.transform.localScale = new Vector3(d.mirror ? -d.scale : d.scale, d.scale, d.scale);
                go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                go.transform.position = Vector3.zero;

                // Local-space footprint (rotation-independent), then centre the footprint on the target position.
                var mf = go.GetComponentInChildren<MeshFilter>();
                var mb = mf.sharedMesh.bounds;
                Vector3 lc = Vector3.Scale(mb.center, go.transform.localScale);
                Vector3 ls = Vector3.Scale(mb.size, new Vector3(Mathf.Abs(go.transform.localScale.x), d.scale, d.scale));
                Vector3 worldCenterOffset = go.transform.rotation * lc;
                go.transform.position = new Vector3(d.pos.x - worldCenterOffset.x, 0f, d.pos.y - worldCenterOffset.z);

                var info = new HouseInfo
                {
                    def = d, go = go, center = d.pos, yaw = yaw,
                    half = new Vector2(ls.x / 2f, ls.z / 2f),
                    padHeight = BaseHeight(d.pos.x, d.pos.y),
                };
                list.Add(info);
            }
            // Layout sanity: overlaps with roads / other houses / key areas.
            foreach (var h in list)
            {
                foreach (var o in list)
                    if (o != h && h.ObbDistance(o.center) < Mathf.Min(o.half.x, o.half.y) + 0.5f)
                        Warn($"{h.def.id} overlaps {o.def.id}");
                foreach (var r in Roads)
                    foreach (var p in r.pts)
                        if (h.ObbDistance(p) < r.width / 2f + 0.3f) { Warn($"{h.def.id} touches road {r.name} at {p}"); break; }
                if (PlazaSignedDist(h.center) < 0f) Warn($"{h.def.id} centre is inside the square");
            }
            return list;
        }

        static void SettleHouses(List<HouseInfo> houses)
        {
            foreach (var h in houses)
            {
                // Sit on the lowest point of the footprint so no corner floats; foundations sink slightly.
                float minH = float.MaxValue;
                for (int i = -1; i <= 1; i++)
                    for (int j = -1; j <= 1; j++)
                    {
                        Vector2 p = h.center + h.Right * (h.half.x * i * 0.95f) + h.Forward * (h.half.y * j * 0.95f);
                        minH = Mathf.Min(minH, GroundY(p.x, p.y));
                    }
                var pos = h.go.transform.position;
                h.go.transform.position = new Vector3(pos.x, minH - 0.08f, pos.z);
                Log($"Building {h.def.id}: {h.def.prefab} x{h.def.scale:F2}{(h.def.mirror ? " mirrored" : "")}, centre ({h.center.x:F1}, {h.center.y:F1}), yaw {h.yaw:F0}, footprint {h.half.x * 2:F1} x {h.half.y * 2:F1} m, base y {minH:F2}");
            }
        }

        // ================================================================== bridge (layout)

        class BridgeInfo
        {
            public GameObject go; public Vector2 center; public Vector2 axis; public float halfLength;
            public List<(Vector2 xz, float height)> ends = new List<(Vector2, float)>();
        }

        static BridgeInfo PrepareBridge()
        {
            var prefab = LoadPack("Bridge");
            if (prefab == null) return null;
            int si = NearestIndex(streamPts, BridgeHint);
            Vector2 c = streamPts[si];
            // Road direction at the crossing.
            var road = Roads.First(r => r.name == "Road_East");
            int ri = NearestIndex(road.pts, c);
            Vector2 axis = (road.pts[Mathf.Min(ri + 1, road.pts.Count - 1)] - road.pts[Mathf.Max(ri - 1, 0)]).normalized;
            float yaw = Mathf.Atan2(axis.x, axis.y) * Mathf.Rad2Deg;
            float bankH = streamRef[si];

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, H.Bridges);
            go.name = "StoneBridge_EastRoad";
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f) * prefab.transform.rotation;
            go.transform.position = new Vector3(c.x, bankH + 0.05f, c.y);
            Physics.SyncTransforms();

            var info = new BridgeInfo { go = go, center = c, axis = axis, halfLength = 6.18f };
            foreach (float s in new[] { -1f, 1f })
            {
                Vector2 e = c + axis * s * 5.9f;
                float y = bankH;
                if (Physics.Raycast(new Vector3(e.x, bankH + 10f, e.y), Vector3.down, out var hit, 20f)) y = hit.point.y;
                info.ends.Add((c + axis * s * 6.6f, y - 0.04f));
            }
            Log($"Bridge: across the stream at ({c.x:F1}, {c.y:F1}), yaw {yaw:F0}, bank level {bankH:F2}, deck end heights {info.ends[0].height:F2} / {info.ends[1].height:F2}");
            return info;
        }
    }
}
