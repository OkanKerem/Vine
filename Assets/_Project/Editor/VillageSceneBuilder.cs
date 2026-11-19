using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mirror;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Vineyard.Networking;
using Vineyard.UI;

namespace Vineyard.EditorTools
{
    /// <summary>
    /// Tools > Vineyard > Build Village Scene
    ///
    /// Assembles Assets/_Project/Scenes/Village.unity from existing assets:
    ///  - OccaSoftware Low Poly Fantasy Village (houses, fences, trees, rocks, lanterns, benches...)
    ///  - GiantWineTank.fbx, GrapeVine.fbx
    ///  - The NetworkManager from MultiplayerTest.unity (copied into a reusable prefab)
    /// Ground is a generated Terrain painted with grass / dirt roads / cobbled square.
    ///
    /// Layout (metres, +Z = north). Play area ~80 x 80 centred on the origin:
    ///   Village square  : centre (0, 10), r = 12, giant bottle in the middle, houses around it
    ///   Production area : x -12..12, z -18..-5 (between the square and the vineyard)
    ///   Vineyard        : x -10..10, z -38.5..-19, 4 rows x 4 vines, fenced, north entrance
    ///
    /// Writes a verification report to Logs/VillageBuildReport.txt.
    /// Re-running overwrites the scene and the generated assets (not the imported packs).
    /// </summary>
    public static class VillageSceneBuilder
    {
        // ------------------------------------------------------------------ paths
        const string ScenePath = "Assets/_Project/Scenes/Village.unity";
        const string GenDir = "Assets/_Project/Generated/Village";
        const string NetworkManagerPrefabPath = "Assets/_Project/Prefabs/NetworkManager.prefab";
        const string PlayerPrefabPath = "Assets/_Project/Prefabs/NetworkPlayer.prefab";
        const string MultiplayerScenePath = "Assets/_Project/Scenes/MultiplayerTest.unity";
        const string Pack = "Assets/OccaSoftware/Low Poly Fantasy Village/Prefabs/";
        const string TankPath = "Assets/GiantWineTank.fbx";
        const string VinePath = "Assets/GrapeVine.fbx";
        const string ReportPath = "Logs/VillageBuildReport.txt";

        // ------------------------------------------------------------------ layout
        static readonly Vector3 SquareCenter = new Vector3(0f, 0f, 10f);
        const float SquareRadius = 12f;
        static readonly Rect ProductionRect = Rect.MinMaxRect(-12f, -18f, 12f, -5f);   // x/z
        static readonly Rect VineyardRect = Rect.MinMaxRect(-10f, -38.5f, 10f, -19f);
        const float PlayHalfExtent = 45f;   // invisible walls
        const float TerrainSize = 160f;

        /// <summary>
        /// Yaw added to houses after turning their local +Z toward the square.
        /// If house doors face away from the square, set this to 180 and rebuild.
        /// </summary>
        const float HouseFrontYawOffset = 0f;

        struct Road { public Vector2 a, b; public float width; public string name; }
        static readonly Road[] Roads =
        {
            new Road { name = "Road_Main_SquareToVineyard", a = new Vector2(0f, -1f),  b = new Vector2(0f, -20f),  width = 5f },
            new Road { name = "Road_VineyardHeadland",      a = new Vector2(-9f, -21f), b = new Vector2(9f, -21f),  width = 3f },
            new Road { name = "Road_East",                  a = new Vector2(13f, 8f),   b = new Vector2(48f, 4f),   width = 4f },
            new Road { name = "Road_West",                  a = new Vector2(-13f, 8f),  b = new Vector2(-48f, 6f),  width = 4f },
            new Road { name = "Road_FarmPath_A",            a = new Vector2(-2f, -16.5f), b = new Vector2(-17f, -16.5f), width = 3f },
            new Road { name = "Road_FarmPath_B",            a = new Vector2(-17f, -16.5f), b = new Vector2(-17f, -24f),  width = 3f },
        };
        const float RingRoadInner = SquareRadius, RingRoadOuter = SquareRadius + 3.5f;

        static StringBuilder report;
        static int warnings;

        // ================================================================== entry

        [MenuItem("Tools/Vineyard/Build Village Scene")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Village", "Exit Play Mode first.", "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            report = new StringBuilder();
            warnings = 0;
            Log($"Village build {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            try
            {
                EditorUtility.DisplayProgressBar("Village", "Preparing assets", 0.05f);
                EnsureFolder(GenDir);
                EnsureFolder("Assets/_Project/Scenes");

                // Grab the NetworkManager setup before we replace the open scene.
                GameObject nmPrefab = GetOrCreateNetworkManagerPrefab();

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                var root = new GameObject("Village").transform;
                var env = Child(root, "Environment");
                var town = Child(root, "Town");
                var vineyard = Child(root, "Vineyard");
                var production = Child(root, "ProductionArea");
                var lighting = Child(root, "Lighting");
                var spawns = Child(root, "PlayerSpawns");

                EditorUtility.DisplayProgressBar("Village", "Terrain", 0.15f);
                BuildTerrain(Child(env, "Terrain"));
                BuildRoadMarkers(Child(env, "Roads"));
                BuildMapBounds(Child(env, "MapBounds"));

                EditorUtility.DisplayProgressBar("Village", "Giant wine bottle", 0.3f);
                var bottle = BuildBottle(root, out Vector3 inputAccess, out Vector3 outputAccess);

                EditorUtility.DisplayProgressBar("Village", "Town", 0.4f);
                var buildings = Child(town, "Buildings");
                var square = Child(town, "VillageSquare");
                var placedFootprints = new List<Rect>();
                placedFootprints.Add(FootprintOf(bottle, 2f)); // keep houses away from the bottle
                BuildHouses(buildings, placedFootprints);
                BuildSquareProps(square, placedFootprints);

                EditorUtility.DisplayProgressBar("Village", "Vineyard", 0.55f);
                var aisles = BuildVineyard(Child(vineyard, "GrapeRows"));
                BuildFences(Child(env, "Fences"));

                EditorUtility.DisplayProgressBar("Village", "Production area", 0.6f);
                var stationPoints = BuildProductionArea(production);

                EditorUtility.DisplayProgressBar("Village", "Nature", 0.65f);
                BuildNature(Child(env, "Nature"), placedFootprints);

                EditorUtility.DisplayProgressBar("Village", "Lighting", 0.7f);
                BuildLighting(lighting);
                BuildMenuCamera(root);

                var spawnPoints = BuildSpawns(spawns);

                // Networking (root object: NetworkManager uses DontDestroyOnLoad, which needs a root).
                if (nmPrefab != null)
                {
                    var nm = (GameObject)PrefabUtility.InstantiatePrefab(nmPrefab, scene);
                    nm.name = "NetworkManager";
                    Log($"NetworkManager: prefab instance of {NetworkManagerPrefabPath}");
                }

                // Static flags for environment (not spawns / network / lighting).
                foreach (var t in new[] { env, town, vineyard, production, bottle.transform })
                    SetStaticRecursive(t.gameObject);

                EditorUtility.DisplayProgressBar("Village", "Verification + NavMesh", 0.8f);
                Physics.SyncTransforms();
                Verify(root, spawnPoints, inputAccess, outputAccess, stationPoints, aisles);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AddSceneToBuildSettingsFirst(ScenePath);
                Log($"Saved {ScenePath} (build index 0).");
            }
            catch (System.Exception e)
            {
                Warn("BUILD FAILED: " + e);
                Debug.LogException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Log($"Done with {warnings} warning(s).");
                Directory.CreateDirectory("Logs");
                File.WriteAllText(ReportPath, report.ToString());
                Debug.Log($"[Village] Build finished with {warnings} warning(s). Report: {Path.GetFullPath(ReportPath)}\n{report}");
            }
        }

        // ================================================================== networking

        static GameObject GetOrCreateNetworkManagerPrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(NetworkManagerPrefabPath);
            if (existing != null && existing.GetComponent<NetworkManager>() != null)
            {
                Log("Reusing existing NetworkManager prefab.");
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
                var transport = temp.AddComponent<kcp2k.KcpTransport>(); // transport choice lives only in setup code
                temp.AddComponent<ConnectionUI>();
                manager.transport = transport;
                manager.playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
                manager.playerSpawnMethod = PlayerSpawnMethod.RoundRobin;
                manager.maxConnections = GameNetworkManager.MaxPlayers;
                if (manager.playerPrefab == null) Warn($"Player prefab missing at {PlayerPrefabPath}. Run Tools > Vineyard > Build Multiplayer Test Scene.");
            }

            var prefab = PrefabUtility.SaveAsPrefabAsset(temp, NetworkManagerPrefabPath);
            Object.DestroyImmediate(temp);
            if (openedMp) EditorSceneManager.CloseScene(mpScene, true);
            return prefab;
        }

        // ================================================================== terrain

        static void BuildTerrain(Transform parent)
        {
            string texDir = GenDir + "/Textures";
            EnsureFolder(texDir);
            var grass = MakeTexture(texDir + "/Grass.png", (u, v) =>
            {
                float n = TileNoise(u, v, 6f) * 0.6f + TileNoise(u + 0.37f, v + 0.11f, 23f) * 0.4f;
                return Color.Lerp(new Color(0.42f, 0.62f, 0.25f), new Color(0.52f, 0.70f, 0.30f), n);
            });
            var grassDark = MakeTexture(texDir + "/GrassDark.png", (u, v) =>
            {
                float n = TileNoise(u, v, 8f) * 0.7f + TileNoise(u + 0.5f, v, 31f) * 0.3f;
                return Color.Lerp(new Color(0.30f, 0.48f, 0.20f), new Color(0.40f, 0.56f, 0.22f), n);
            });
            var dirt = MakeTexture(texDir + "/Dirt.png", (u, v) =>
            {
                float n = TileNoise(u, v, 7f) * 0.6f + TileNoise(u + 0.2f, v + 0.7f, 40f) * 0.4f;
                var c = Color.Lerp(new Color(0.55f, 0.42f, 0.28f), new Color(0.66f, 0.52f, 0.36f), n);
                if (TileNoise(u + 0.9f, v + 0.3f, 64f) > 0.78f) c *= 0.82f; // pebbles
                return c;
            });
            var cobble = MakeTexture(texDir + "/Cobble.png", (u, v) =>
            {
                float edge = CellEdge(u, v, 7);
                float n = TileNoise(u, v, 20f);
                var stone = Color.Lerp(new Color(0.62f, 0.58f, 0.52f), new Color(0.74f, 0.70f, 0.63f), n);
                return Color.Lerp(new Color(0.40f, 0.36f, 0.31f), stone, Mathf.SmoothStep(0f, 1f, edge * 6f));
            });

            var layers = new[]
            {
                MakeLayer("Grass", grass, 8f),
                MakeLayer("GrassDark", grassDark, 10f),
                MakeLayer("Dirt", dirt, 5f),
                MakeLayer("Cobble", cobble, 4f),
            };

            string dataPath = GenDir + "/VillageTerrainData.asset";
            AssetDatabase.DeleteAsset(dataPath);
            var data = new TerrainData { heightmapResolution = 257, alphamapResolution = 256 };
            data.size = new Vector3(TerrainSize, 20f, TerrainSize);
            data.terrainLayers = layers;

            // Heights: flat play area, gentle hills outside it as a natural backdrop.
            int hr = data.heightmapResolution;
            var heights = new float[hr, hr];
            for (int z = 0; z < hr; z++)
            for (int x = 0; x < hr; x++)
            {
                Vector2 w = TerrainToWorld(x, z, hr);
                float r = Mathf.Max(Mathf.Abs(w.x), Mathf.Abs(w.y)) * 0.6f + w.magnitude * 0.4f;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(48f, 78f, r));
                float hills = 4f + 8f * Mathf.PerlinNoise(w.x * 0.03f + 10f, w.y * 0.03f + 3f);
                heights[z, x] = t * hills / data.size.y;
            }
            data.SetHeights(0, 0, heights);

            // Splat map.
            int ar = data.alphamapResolution;
            var maps = new float[ar, ar, layers.Length];
            for (int z = 0; z < ar; z++)
            for (int x = 0; x < ar; x++)
            {
                Vector2 w = TerrainToWorld(x, z, ar);
                float jitter = (Mathf.PerlinNoise(w.x * 0.35f, w.y * 0.35f) - 0.5f) * 0.9f;

                float cob = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(SquareRadius - 0.8f, SquareRadius + 0.4f, Vector2.Distance(w, XZ(SquareCenter)) + jitter));

                float d = 0f;
                foreach (var road in Roads)
                {
                    float dist = DistToSegment(w, road.a, road.b) + jitter;
                    d = Mathf.Max(d, 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(road.width * 0.5f - 0.6f, road.width * 0.5f + 0.6f, dist)));
                }
                float sq = Vector2.Distance(w, XZ(SquareCenter)) + jitter;
                if (sq > RingRoadInner - 1f && sq < RingRoadOuter + 1f)
                    d = Mathf.Max(d, 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(RingRoadOuter - 0.6f, RingRoadOuter + 0.6f, sq)));
                d = Mathf.Max(d, RectMask(w, ProductionRect, 1f, jitter));
                d = Mathf.Max(d, RectMask(w, VineyardRect, 1f, jitter) * 0.85f);

                float rest = Mathf.Clamp01(1f - cob);
                float dirtW = Mathf.Min(d, rest);
                rest -= dirtW;
                float variation = Mathf.Clamp01(Mathf.PerlinNoise(w.x * 0.06f + 50f, w.y * 0.06f + 20f) * 1.3f - 0.35f);
                maps[z, x, 0] = rest * (1f - variation);
                maps[z, x, 1] = rest * variation;
                maps[z, x, 2] = dirtW;
                maps[z, x, 3] = cob;
            }
            data.SetAlphamaps(0, 0, maps);
            AssetDatabase.CreateAsset(data, dataPath);

            var terrainGo = Terrain.CreateTerrainGameObject(data);
            terrainGo.name = "VillageTerrain";
            terrainGo.transform.SetParent(parent, false);
            terrainGo.transform.position = new Vector3(-TerrainSize / 2f, 0f, -TerrainSize / 2f);
            var terrain = terrainGo.GetComponent<Terrain>();
            terrain.drawInstanced = true;
            terrain.basemapDistance = 200f;
            Log($"Terrain: {TerrainSize}x{TerrainSize} m, flat play area, hills beyond ~48 m, TerrainCollider present: {terrainGo.GetComponent<TerrainCollider>() != null}");
        }

        static Vector2 TerrainToWorld(int x, int z, int res)
        {
            float fx = (x / (float)(res - 1)) * TerrainSize - TerrainSize / 2f;
            float fz = (z / (float)(res - 1)) * TerrainSize - TerrainSize / 2f;
            return new Vector2(fx, fz);
        }

        static float RectMask(Vector2 p, Rect r, float fade, float jitter)
        {
            float dx = Mathf.Max(r.xMin - p.x, 0f, p.x - r.xMax);
            float dz = Mathf.Max(r.yMin - p.y, 0f, p.y - r.yMax);
            float dist = Mathf.Sqrt(dx * dx + dz * dz) + jitter * 0.5f;
            return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, fade, dist));
        }

        static TerrainLayer MakeLayer(string name, Texture2D tex, float tile)
        {
            string path = $"{GenDir}/Layer_{name}.terrainlayer";
            AssetDatabase.DeleteAsset(path);
            var layer = new TerrainLayer { diffuseTexture = tex, tileSize = new Vector2(tile, tile), smoothness = 0f, metallic = 0f };
            AssetDatabase.CreateAsset(layer, path);
            return layer;
        }

        static Texture2D MakeTexture(string path, System.Func<float, float, Color> fn, int size = 256)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = fn(x / (float)size, y / (float)size);
            tex.SetPixels(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.mipmapEnabled = true;
            importer.sRGBTexture = true;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>Tileable value in [0,1] (blend of four Perlin samples).</summary>
        static float TileNoise(float u, float v, float freq)
        {
            u = Mathf.Repeat(u, 1f); v = Mathf.Repeat(v, 1f);
            float x = u * freq, y = v * freq;
            float a = Mathf.PerlinNoise(x, y), b = Mathf.PerlinNoise(x - freq, y);
            float c = Mathf.PerlinNoise(x, y - freq), d = Mathf.PerlinNoise(x - freq, y - freq);
            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        /// <summary>Tileable Voronoi edge distance (F2 - F1) for cobblestones.</summary>
        static float CellEdge(float u, float v, int cells)
        {
            float f1 = 9f, f2 = 9f;
            int cx = Mathf.FloorToInt(u * cells), cy = Mathf.FloorToInt(v * cells);
            for (int oy = -1; oy <= 1; oy++)
            for (int ox = -1; ox <= 1; ox++)
            {
                int gx = cx + ox, gy = cy + oy;
                int hx = ((gx % cells) + cells) % cells, hy = ((gy % cells) + cells) % cells;
                var rng = new System.Random(hx * 73856093 ^ hy * 19349663);
                float px = (gx + 0.15f + 0.7f * (float)rng.NextDouble()) / cells;
                float py = (gy + 0.15f + 0.7f * (float)rng.NextDouble()) / cells;
                float d = Vector2.Distance(new Vector2(u, v), new Vector2(px, py)) * cells;
                if (d < f1) { f2 = f1; f1 = d; } else if (d < f2) f2 = d;
            }
            return f2 - f1;
        }

        static void BuildRoadMarkers(Transform parent)
        {
            // Roads are painted into the terrain; these markers document them (useful for NPC waypoints later).
            foreach (var road in Roads)
            {
                var r = new GameObject(road.name).transform;
                r.SetParent(parent, false);
                var a = new GameObject("Start").transform; a.SetParent(r, false); a.position = new Vector3(road.a.x, 0f, road.a.y);
                var b = new GameObject("End").transform; b.SetParent(r, false); b.position = new Vector3(road.b.x, 0f, road.b.y);
            }
            var ring = new GameObject("Road_SquareRing").transform;
            ring.SetParent(parent, false);
            ring.position = SquareCenter;
        }

        static void BuildMapBounds(Transform parent)
        {
            float h = 30f, e = PlayHalfExtent, len = e * 2f + 2f;
            void Wall(string n, Vector3 pos, Vector3 size)
            {
                var w = new GameObject(n);
                w.transform.SetParent(parent, false);
                w.transform.position = pos;
                w.AddComponent<BoxCollider>().size = size;
            }
            Wall("Bound_North", new Vector3(0, h / 2, e), new Vector3(len, h, 1));
            Wall("Bound_South", new Vector3(0, h / 2, -e), new Vector3(len, h, 1));
            Wall("Bound_East", new Vector3(e, h / 2, 0), new Vector3(1, h, len));
            Wall("Bound_West", new Vector3(-e, h / 2, 0), new Vector3(1, h, len));
            Log($"Invisible map bounds at +/-{e} m.");
        }

        // ================================================================== bottle

        static GameObject BuildBottle(Transform root, out Vector3 inputAccess, out Vector3 outputAccess)
        {
            inputAccess = outputAccess = SquareCenter;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(TankPath);
            if (model == null)
            {
                Warn($"MISSING: {TankPath}");
                return new GameObject("GiantWineBottle");
            }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            go.name = "GiantWineBottle";
            go.transform.SetParent(root, false);
            go.transform.position = SquareCenter;

            // Colliders on every mesh except the liquid inside.
            int added = 0;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.name.Contains("WineLiquid")) continue;
                if (mf.GetComponent<Collider>() == null) { mf.gameObject.AddComponent<MeshCollider>(); added++; }
            }

            // Face the grape sign toward the spawn side (south).
            var sign = FindDeep(go.transform, "Tank_GrapeSign");
            if (sign != null)
            {
                Vector3 d = BoundsOf(sign.gameObject).center - go.transform.position; d.y = 0;
                // Pure yaw. (FromToRotation flips the model upside down when the vectors are opposite.)
                if (d.sqrMagnitude > 0.01f)
                    go.transform.rotation = Quaternion.Euler(0f, Vector3.SignedAngle(d, Vector3.back, Vector3.up), 0f) * go.transform.rotation;
            }
            // Recentre on the square using the support structure / overall bounds.
            var b = BoundsOf(go);
            go.transform.position += new Vector3(SquareCenter.x - b.center.x, 0f, SquareCenter.z - b.center.z);
            b = BoundsOf(go);
            if (b.min.y < -0.5f || b.min.y > 0.5f) Warn($"Bottle is not standing on the ground (bottom at {b.min.y:F2} m). Check its orientation.");
            Log($"Bottle: size {b.size.x:F1} x {b.size.y:F1} x {b.size.z:F1} m at {Fmt(b.center)}; added {added} MeshColliders.");
            if (b.size.y < 4f || b.size.y > 20f) Warn($"Bottle height {b.size.y:F1} m looks off. Check the FBX scale.");

            // Access markers for future gameplay.
            Vector3 MakeAccess(string childName, string markerName)
            {
                var part = FindDeep(go.transform, childName);
                if (part == null) { Warn($"Bottle part '{childName}' not found."); return SquareCenter; }
                var pb = BoundsOf(part.gameObject);
                Vector3 dir = pb.center - b.center; dir.y = 0; dir.Normalize();
                // Furthest point of the part along dir, plus clearance for a player.
                float reach = Vector3.Dot(pb.extents, new Vector3(Mathf.Abs(dir.x), 0, Mathf.Abs(dir.z)));
                Vector3 p = new Vector3(pb.center.x, 0f, pb.center.z) + dir * (reach + 1.0f);
                var m = new GameObject(markerName).transform;
                m.SetParent(go.transform, true);
                m.position = p;
                m.rotation = Quaternion.LookRotation(-dir, Vector3.up);
                Log($"  {childName}: centre height {pb.center.y:F2} m (bottom {pb.min.y:F2}, top {pb.max.y:F2}); access marker '{markerName}' at {Fmt(p)}");
                return p;
            }
            inputAccess = MakeAccess("WineInput_Funnel", "InputFunnel_Access");
            outputAccess = MakeAccess("WineTap_Spout", "OutputTap_Access");
            return go;
        }

        // ================================================================== town

        struct HouseSlot { public string prefab; public Vector2 pos; public float yawJitter; public bool faceSquare; public float fixedYaw; }

        static void BuildHouses(Transform parent, List<Rect> footprints)
        {
            Vector2 c = XZ(SquareCenter);
            Vector2 Polar(float deg, float r) => c + new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)) * r;

            var slots = new List<HouseSlot>
            {
                // Houses are ~16-18 m footprints, so three on the north arc (r 25) is what fits inside the map.
                new HouseSlot { prefab = "House_1", pos = Polar(35f, 25f),  yawJitter = 6f,  faceSquare = true },
                new HouseSlot { prefab = "House_2", pos = Polar(90f, 24f),  yawJitter = -5f, faceSquare = true },
                new HouseSlot { prefab = "House_3", pos = Polar(145f, 25f), yawJitter = 8f,  faceSquare = true },
                new HouseSlot { prefab = "House_2", pos = new Vector2(27f, -6f),  yawJitter = 4f,  faceSquare = true },
                new HouseSlot { prefab = "House_3", pos = new Vector2(-27f, -6f), yawJitter = -4f, faceSquare = true },
                // Farmhouse next to the vineyard, facing the farm path.
                new HouseSlot { prefab = "House_2", pos = new Vector2(-26f, -29f), faceSquare = false, fixedYaw = 90f },
            };

            int i = 0;
            foreach (var s in slots)
            {
                i++;
                var prefab = LoadPack(s.prefab);
                if (prefab == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                go.name = $"{s.prefab}_{i:00}" + (i == slots.Count ? "_Farmhouse" : "");

                float yaw;
                if (s.faceSquare)
                {
                    Vector2 toSquare = c - s.pos;
                    yaw = Mathf.Atan2(toSquare.x, toSquare.y) * Mathf.Rad2Deg + s.yawJitter;
                }
                else yaw = s.fixedYaw;
                yaw += HouseFrontYawOffset;

                PlaceCentered(go, s.pos, yaw);
                EnsureColliders(go);

                // Push outward until it no longer overlaps other houses / the square / roads / work areas.
                Vector2 outward = (s.pos - c).sqrMagnitude > 0.01f ? (s.pos - c).normalized : Vector2.up;
                if (!s.faceSquare) outward = Vector2.left;
                int pushes = 0;
                while (pushes < 15)
                {
                    var fp = FootprintOf(go, 1.0f);
                    string clash = Clash(fp, footprints, checkRoads: true);
                    if (clash == null) break;
                    go.transform.position += new Vector3(outward.x, 0f, outward.y);
                    pushes++;
                    if (pushes == 15) Warn($"{go.name} still overlaps {clash} after moving it outward 15 m.");
                }
                var finalFp = FootprintOf(go, 1.0f);
                footprints.Add(finalFp);
                if (finalFp.xMin < -PlayHalfExtent || finalFp.xMax > PlayHalfExtent || finalFp.yMin < -PlayHalfExtent || finalFp.yMax > PlayHalfExtent)
                    Warn($"{go.name} crosses the map boundary (+/-{PlayHalfExtent} m).");
                var bb = BoundsOf(go);
                Log($"House {go.name}: size {bb.size.x:F1} x {bb.size.y:F1} x {bb.size.z:F1} m at {Fmt(bb.center)}, yaw {yaw:F0}, pushed {pushes} m");
                if (bb.size.y < 3f || bb.size.y > 30f) Warn($"{go.name} height {bb.size.y:F1} m looks off. Check the pack scale.");
            }
        }

        static void BuildSquareProps(Transform parent, List<Rect> footprints)
        {
            // Lanterns around the edge of the square (none on the south side where the spawns / main road are).
            var lantern = LoadPack("Lantern");
            if (lantern != null)
            {
                float[] angles = { 0f, 45f, 90f, 135f, 180f, 215f, 325f };
                int i = 0;
                foreach (float a in angles)
                {
                    Vector2 p = XZ(SquareCenter) + Dir(a) * (SquareRadius + 0.6f);
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(lantern, parent);
                    go.name = $"Lantern_{++i:00}";
                    go.transform.position = new Vector3(p.x, 0f, p.y);
                    go.transform.rotation = Quaternion.Euler(0f, 180f - a + 90f, 0f); // arm points roughly into the square
                    EnsureColliders(go);
                }
            }

            // Benches on the north half, facing the bottle.
            var bench = LoadPack("Bench");
            if (bench != null)
            {
                float[] angles = { 40f, 90f, 140f };
                int i = 0;
                foreach (float a in angles)
                {
                    Vector2 p = XZ(SquareCenter) + Dir(a) * (SquareRadius - 2.2f);
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(bench, parent);
                    go.name = $"Bench_{++i:00}";
                    Vector2 toCenter = XZ(SquareCenter) - p;
                    PlaceCentered(go, p, Mathf.Atan2(toCenter.x, toCenter.y) * Mathf.Rad2Deg);
                    EnsureColliders(go);
                }
            }

            // A few flowers / pots near the lanterns (no colliders: purely decorative).
            string[] flowers = { "Flower_1", "Flower_2", "Flower_3", "Flower_4", "Flower_5", "Flower Pot" };
            var rng = new System.Random(7);
            for (int i = 0; i < 10; i++)
            {
                var prefab = LoadPack(flowers[i % flowers.Length], quiet: true);
                if (prefab == null) continue;
                // Flank the lanterns on the square's edge (north half only).
                float a = (i / 2) * 45f + (i % 2 == 0 ? -2.5f : 2.5f);
                Vector2 p = XZ(SquareCenter) + Dir(a) * (SquareRadius + 0.3f + (float)rng.NextDouble() * 0.6f);
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                go.name = $"Decor_{prefab.name}_{i:00}";
                go.transform.position = new Vector3(p.x, 0f, p.y);
                go.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
                foreach (var col in go.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(col);
            }
        }

        // ================================================================== vineyard

        static List<Vector3> BuildVineyard(Transform parent)
        {
            var aisles = new List<Vector3>();
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(VinePath);
            if (model == null) { Warn($"MISSING: {VinePath}"); return aisles; }

            const int rows = 4, perRow = 4;
            const float rowSpacing = 4.2f;   // ~2.8 m clear aisle between trellises
            float[] rowX = new float[rows];
            for (int r = 0; r < rows; r++) rowX[r] = (r - (rows - 1) / 2f) * rowSpacing;

            // Measure one vine to space them end-to-end along the trellis.
            var probe = (GameObject)PrefabUtility.InstantiatePrefab(model);
            var pb = BoundsOf(probe);
            bool alongX = pb.size.x > pb.size.z;
            float length = alongX ? pb.size.x : pb.size.z;
            Object.DestroyImmediate(probe);
            float step = Mathf.Max(length - 0.25f, 2.5f);
            float zStart = VineyardRect.yMax - 3.9f - step / 2f; // leaves a ~3.9 m headland at the north entrance
            Log($"GrapeVine: size {pb.size.x:F2} x {pb.size.y:F2} x {pb.size.z:F2} m, trellis along {(alongX ? "X (rotated 90)" : "Z")}, step {step:F2} m");

            int n = 0;
            for (int r = 0; r < rows; r++)
            {
                var row = new GameObject($"Row_{r + 1:00}").transform;
                row.SetParent(parent, false);
                for (int i = 0; i < perRow; i++)
                {
                    var vine = (GameObject)PrefabUtility.InstantiatePrefab(model, row);
                    vine.name = $"GrapeVine_R{r + 1}_{i + 1:00}";
                    float z = zStart - i * step;
                    PlaceCentered(vine, new Vector2(rowX[r], z), alongX ? 90f : 0f, keepPivotY: true);
                    vine.transform.position = new Vector3(vine.transform.position.x, 0f, vine.transform.position.z);

                    // Simple trellis collider (rows act like walls; aisles stay free).
                    var vb = BoundsOf(vine);
                    var box = vine.AddComponent<BoxCollider>();
                    Vector3 localCenter = vine.transform.InverseTransformPoint(new Vector3(vb.center.x, 1.1f, vb.center.z));
                    box.center = localCenter;
                    box.size = alongX ? new Vector3(step, 2.2f, 0.4f) : new Vector3(0.4f, 2.2f, step);
                    n++;
                }
            }
            float rowEnd = zStart - (perRow - 1) * step - step / 2f;
            for (int r = 0; r < rows - 1; r++)
                aisles.Add(new Vector3((rowX[r] + rowX[r + 1]) / 2f, 0f, (zStart + rowEnd) / 2f));
            Log($"Vineyard: {n} vines in {rows} rows, row spacing {rowSpacing} m, rows span z {zStart + step / 2f:F1}..{rowEnd:F1}");
            if (rowEnd < VineyardRect.yMin + 1f) Warn("Vine rows reach the southern fence.");
            return aisles;
        }

        static void BuildFences(Transform parent)
        {
            var fence = LoadPack("Fence");
            if (fence == null) return;
            var probe = (GameObject)PrefabUtility.InstantiatePrefab(fence);
            var b = BoundsOf(probe);
            float seg = Mathf.Max(b.size.x, b.size.z);
            bool alongX = b.size.x >= b.size.z;
            Object.DestroyImmediate(probe);

            var r = VineyardRect;
            float gapHalf = 3.5f; // north entrance width ~7 m
            void Line(string name, Vector2 a, Vector2 bb, float skipFrom = float.NaN, float skipTo = float.NaN)
            {
                var line = new GameObject(name).transform;
                line.SetParent(parent, false);
                Vector2 d = bb - a; float len = d.magnitude; d /= len;
                int count = Mathf.CeilToInt(len / seg - 0.05f);
                float actual = len / count;
                float yaw = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg - (alongX ? 90f : 0f);
                for (int i = 0; i < count; i++)
                {
                    float t0 = i * actual, t1 = t0 + actual;
                    if (!float.IsNaN(skipFrom) && t1 > skipFrom && t0 < skipTo) continue;
                    Vector2 p = a + d * (t0 + actual / 2f);
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(fence, line);
                    go.name = $"Fence_{i + 1:00}";
                    PlaceCentered(go, p, yaw, keepPivotY: true);
                    EnsureColliders(go);
                }
            }
            float w = r.width;
            Line("VineyardFence_North", new Vector2(r.xMin, r.yMax), new Vector2(r.xMax, r.yMax), w / 2f - gapHalf, w / 2f + gapHalf);
            Line("VineyardFence_South", new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin));
            Line("VineyardFence_West", new Vector2(r.xMin, r.yMin), new Vector2(r.xMin, r.yMax));
            Line("VineyardFence_East", new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax));
            Log($"Fences: segment {seg:F2} m, vineyard perimeter with ~{gapHalf * 2f:F0} m north entrance.");
        }

        // ================================================================== production

        static Dictionary<string, Vector3> BuildProductionArea(Transform parent)
        {
            var points = new Dictionary<string, Vector3>();
            var padMat = MakeMat("StationPad", new Color(0.86f, 0.74f, 0.42f));
            (string name, Vector2 pos, Vector2 size)[] stations =
            {
                ("CrushingStation_Spawn",     new Vector2(-7.5f, -8.5f),  new Vector2(5f, 4.5f)),
                ("FermentationStation_Spawn", new Vector2(7.5f, -8.5f),   new Vector2(5f, 4.5f)),
                ("BarrelStorage_Spawn",       new Vector2(-7.5f, -14.5f), new Vector2(5f, 4.5f)),
                ("ExpansionStation_Slot_01",  new Vector2(7.5f, -14.5f),  new Vector2(5f, 4.5f)),
            };
            foreach (var s in stations)
            {
                var go = new GameObject(s.name);
                go.transform.SetParent(parent, false);
                go.transform.position = new Vector3(s.pos.x, 0f, s.pos.y);
                go.transform.rotation = Quaternion.LookRotation(new Vector3(-Mathf.Sign(s.pos.x), 0, 0)); // face the main road

                // Thin, non-colliding pad so the reserved space is visible in game.
                var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
                pad.name = "PlaceholderPad";
                Object.DestroyImmediate(pad.GetComponent<Collider>());
                pad.transform.SetParent(go.transform, false);
                pad.transform.localPosition = new Vector3(0f, 0.01f, 0f);
                pad.transform.localScale = new Vector3(s.size.y, 0.02f, s.size.x);
                pad.GetComponent<Renderer>().sharedMaterial = padMat;
                pad.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
                points[s.name] = go.transform.position;
            }

            // A few crates by the barrel storage slot (decor, solid).
            var crate = LoadPack("Crate");
            if (crate != null)
            {
                Vector3[] cp = { new Vector3(-11f, 0f, -16.8f), new Vector3(-9.9f, 0f, -16.9f), new Vector3(-10.5f, 1f, -16.85f) };
                for (int i = 0; i < cp.Length; i++)
                {
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(crate, parent);
                    go.name = $"Crate_{i + 1:00}";
                    go.transform.position = cp[i];
                    go.transform.rotation = Quaternion.Euler(0f, i * 17f, 0f);
                    EnsureColliders(go);
                }
            }
            Log("Production area: 4 reserved station slots (empty GameObjects + flat non-colliding pads).");
            return points;
        }

        // ================================================================== nature

        static void BuildNature(Transform parent, List<Rect> footprints)
        {
            var rng = new System.Random(1234);
            string[] trees = { "Tree_1", "Tree_2", "Tree_3", "Tree_4", "Tree_5", "Tree_6", "Tree_7", "Tree_8",
                               "Pine Tree_1", "Pine Tree_2", "Pine Tree_3", "Pine Tree_4", "Pine Tree_5" };
            var treeRoot = Child(parent, "Trees");
            int placed = 0, skipped = 0;

            bool Clear(Vector2 p, float radius)
            {
                var r = new Rect(p.x - radius, p.y - radius, radius * 2, radius * 2);
                return Clash(r, footprints, checkRoads: true) == null;
            }

            void Tree(Vector2 p, float clearance)
            {
                if (!Clear(p, clearance)) { skipped++; return; }
                var prefab = LoadPack(trees[rng.Next(trees.Length)], quiet: true);
                if (prefab == null) return;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, treeRoot);
                go.name = $"{prefab.name}_{++placed:00}";
                go.transform.position = new Vector3(p.x, 0f, p.y);
                go.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
                float s = 0.85f + (float)rng.NextDouble() * 0.3f;
                go.transform.localScale = Vector3.one * s;
                EnsureColliders(go);
                footprints.Add(new Rect(p.x - 1.5f, p.y - 1.5f, 3f, 3f));
            }

            // Perimeter ring of trees (a soft natural border).
            for (float a = 0f; a < 360f; a += 11f)
            {
                float ang = a + (float)rng.NextDouble() * 5f;
                float r = 39f + (float)rng.NextDouble() * 5f;
                Vector2 p = Dir(ang) * r;
                p.x = Mathf.Clamp(p.x, -43.5f, 43.5f);
                p.y = Mathf.Clamp(p.y, -43.5f, 43.5f);
                Tree(p, 2f);
            }
            // Outer layer beyond the walls (visual depth).
            for (float a = 5f; a < 360f; a += 14f)
                Tree(Dir(a + (float)rng.NextDouble() * 6f) * (52f + (float)rng.NextDouble() * 10f), 2f);
            // A few trees inside the village for shade / framing.
            Vector2[] inner = { new Vector2(-17f, -10f), new Vector2(17f, -12f), new Vector2(16f, -30f), new Vector2(-14f, -38f),
                                new Vector2(31f, -22f), new Vector2(-33f, 20f), new Vector2(34f, 20f), new Vector2(20f, 36f), new Vector2(-21f, 36f) };
            foreach (var p in inner) Tree(p, 2.5f);
            Log($"Trees: {placed} placed, {skipped} skipped because they would block a road or building.");

            // Rocks.
            var rockRoot = Child(parent, "Rocks");
            Vector2[] rocks = { new Vector2(13f, -24f), new Vector2(-13f, -33f), new Vector2(14f, -37f), new Vector2(-16f, 2f),
                                new Vector2(19f, 0f), new Vector2(-36f, -14f), new Vector2(37f, -9f), new Vector2(-8f, 38f) };
            int rp = 0;
            foreach (var p in rocks)
            {
                if (!Clear(p, 1.2f)) continue;
                var prefab = LoadPack($"Rock_{rng.Next(1, 5)}", quiet: true);
                if (prefab == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, rockRoot);
                go.name = $"{prefab.name}_{++rp:00}";
                go.transform.position = new Vector3(p.x, 0.1f, p.y);
                go.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
                EnsureColliders(go);
            }

            // Distant mountains as a horizon backdrop (outside the playable area).
            var mountRoot = Child(parent, "Mountains");
            for (int i = 0; i < 4; i++)
            {
                var prefab = LoadPack($"Mountain_{i + 1}", quiet: true);
                if (prefab == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, mountRoot);
                float a = 35f + i * 90f;
                Vector2 p = Dir(a) * 150f;
                go.transform.position = new Vector3(p.x, -2f, p.y);
                go.transform.rotation = Quaternion.Euler(0f, -a + 90f, 0f);
                foreach (var col in go.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(col); // unreachable
            }
        }

        // ================================================================== lighting / camera

        static void BuildLighting(Transform parent)
        {
            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(parent, false);
            sunGo.transform.rotation = Quaternion.Euler(42f, -35f, 0f);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.91f, 0.76f);
            sun.intensity = 1.35f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.75f;
            RenderSettings.sun = sun;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.70f, 0.82f);
            RenderSettings.ambientEquatorColor = new Color(0.62f, 0.58f, 0.50f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.27f, 0.21f);
            RenderSettings.skybox = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.80f, 0.83f, 0.86f);
            RenderSettings.fogStartDistance = 70f;
            RenderSettings.fogEndDistance = 260f;

            // Global post-processing volume (warm, soft).
            string profilePath = GenDir + "/VillageVolumeProfile.asset";
            AssetDatabase.DeleteAsset(profilePath);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, profilePath);
            var tm = profile.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.Neutral);
            var bloom = profile.Add<Bloom>(true); bloom.intensity.Override(0.35f); bloom.threshold.Override(1.05f);
            var ca = profile.Add<ColorAdjustments>(true); ca.saturation.Override(10f); ca.postExposure.Override(0.1f);
            var wb = profile.Add<WhiteBalance>(true); wb.temperature.Override(8f);
            var vig = profile.Add<Vignette>(true); vig.intensity.Override(0.18f);
            foreach (var comp in profile.components) { comp.name = comp.GetType().Name; AssetDatabase.AddObjectToAsset(comp, profile); }
            EditorUtility.SetDirty(profile);

            var volGo = new GameObject("GlobalVolume");
            volGo.transform.SetParent(parent, false);
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = profile;

            EnablePostProcessingOnPlayerCamera();
            Log("Lighting: warm directional sun with soft shadows, trilight ambient, linear fog, global volume.");
        }

        static void EnablePostProcessingOnPlayerCamera()
        {
            var player = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (player == null) return;
            var cam = player.GetComponentInChildren<Camera>(true);
            if (cam == null) return;
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data != null && data.renderPostProcessing) return;
            using (var scope = new PrefabUtility.EditPrefabContentsScope(PlayerPrefabPath))
            {
                var c = scope.prefabContentsRoot.GetComponentInChildren<Camera>(true);
                var d = c.GetComponent<UniversalAdditionalCameraData>();
                if (d == null) d = c.gameObject.AddComponent<UniversalAdditionalCameraData>();
                d.renderPostProcessing = true;
            }
            Log("Player prefab camera: enabled post-processing (visual only, no network change).");
        }

        static void BuildMenuCamera(Transform root)
        {
            var camGo = new GameObject("MenuCamera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(root, false);
            camGo.transform.position = new Vector3(14f, 16f, -24f);
            camGo.transform.LookAt(SquareCenter + Vector3.up * 3f);
            var cam = camGo.AddComponent<Camera>();
            cam.farClipPlane = 600f;
            camGo.AddComponent<AudioListener>();
            var data = camGo.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
        }

        // ================================================================== spawns

        static List<Vector3> BuildSpawns(Transform parent)
        {
            var list = new List<Vector3>();
            Vector3[] candidates =
            {
                new Vector3(-6f, 0f, 1.0f), new Vector3(-2f, 0f, 0.0f),
                new Vector3(2f, 0f, 0.0f),  new Vector3(6f, 0f, 1.0f),
            };
            Physics.SyncTransforms();
            for (int i = 0; i < candidates.Length; i++)
            {
                Vector3 p = FindFreeSpot(candidates[i]);
                var go = new GameObject($"PlayerSpawn_{i + 1:00}");
                go.transform.SetParent(parent, false);
                go.transform.position = p;
                Vector3 look = SquareCenter - p; look.y = 0;
                go.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
                go.AddComponent<NetworkStartPosition>();
                list.Add(p);
            }
            return list;
        }

        static bool CapsuleFree(Vector3 feet) =>
            !Physics.CheckCapsule(feet + Vector3.up * 0.55f, feet + Vector3.up * 1.6f, 0.5f, ~0, QueryTriggerInteraction.Ignore);

        static Vector3 FindFreeSpot(Vector3 p)
        {
            if (CapsuleFree(p)) return p;
            for (float r = 0.5f; r <= 4f; r += 0.5f)
                for (float a = 0; a < 360f; a += 30f)
                {
                    Vector3 q = p + new Vector3(Mathf.Cos(a * Mathf.Deg2Rad), 0, Mathf.Sin(a * Mathf.Deg2Rad)) * r;
                    if (CapsuleFree(q)) return q;
                }
            Warn($"No free spawn spot near {Fmt(p)}.");
            return p;
        }

        // ================================================================== verification

        static void Verify(Transform root, List<Vector3> spawns, Vector3 inputAccess, Vector3 outputAccess,
                           Dictionary<string, Vector3> stations, List<Vector3> aisles)
        {
            Log("---- Verification ----");

            // Colliders on buildings.
            foreach (var r in root.Find("Town/Buildings").GetComponentsInChildren<Renderer>())
                if (r.GetComponentInParent<Collider>() == null && r.GetComponent<Collider>() == null)
                    Warn($"Renderer without collider: {r.name}");

            // Ground under each spawn, spawn free.
            for (int i = 0; i < spawns.Count; i++)
            {
                bool ground = Physics.Raycast(spawns[i] + Vector3.up * 5f, Vector3.down, out var hit, 10f) && hit.collider is TerrainCollider;
                bool free = CapsuleFree(spawns[i]);
                Log($"Spawn {i + 1} {Fmt(spawns[i])}: ground={(ground ? "terrain" : "NOT TERRAIN")} free={free}");
                if (!ground || !free) Warn($"Spawn {i + 1} is blocked or has no terrain under it.");
            }
            Log($"Bottle input access free: {CapsuleFree(inputAccess)}, output access free: {CapsuleFree(outputAccess)}");
            if (!CapsuleFree(inputAccess) || !CapsuleFree(outputAccess)) Warn("A bottle access point is blocked.");

            // NavMesh over the play area: used to verify routes (and kept for future NPCs).
            var navGo = new GameObject("NavMesh");
            navGo.transform.SetParent(root, false);
            var surface = navGo.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Volume;
            surface.center = new Vector3(0f, 5f, 0f);
            surface.size = new Vector3(PlayHalfExtent * 2f, 20f, PlayHalfExtent * 2f);
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.agentTypeID = 0; // Humanoid: radius 0.5, so every passage that bakes is at least ~1 m wide
            surface.BuildNavMesh();
            string navPath = GenDir + "/Village_NavMesh.asset";
            AssetDatabase.DeleteAsset(navPath);
            if (surface.navMeshData != null)
            {
                AssetDatabase.CreateAsset(surface.navMeshData, navPath);
                var tri = NavMesh.CalculateTriangulation();
                Log($"NavMesh baked: {tri.indices.Length / 3} triangles (agent radius {NavMesh.GetSettingsByID(0).agentRadius} m).");
            }
            else Warn("NavMesh bake produced no data.");

            if (spawns.Count == 0) return;
            Vector3 from = spawns[0];
            var targets = new List<(string, Vector3)>
            {
                ("Bottle input funnel", inputAccess),
                ("Bottle output tap", outputAccess),
                ("Square north side", SquareCenter + new Vector3(0, 0, 8f)),
                ("East road exit", new Vector3(40f, 0f, 4.5f)),
                ("West road exit", new Vector3(-40f, 0f, 6.5f)),
                ("Farm path end", new Vector3(-17f, 0f, -23f)),
            };
            foreach (var kv in stations) targets.Add((kv.Key, kv.Value));
            for (int i = 0; i < aisles.Count; i++) targets.Add(($"Vineyard aisle {i + 1}", aisles[i]));

            var path = new NavMeshPath();
            for (int i = 1; i < spawns.Count; i++) targets.Add(($"Spawn {i + 1}", spawns[i]));
            foreach (var (name, target) in targets)
            {
                if (!NavMesh.SamplePosition(from, out var a, 2f, NavMesh.AllAreas) ||
                    !NavMesh.SamplePosition(target, out var b, 2f, NavMesh.AllAreas))
                {
                    Warn($"Route to {name}: point not on the NavMesh ({Fmt(target)}).");
                    continue;
                }
                NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path);
                float len = 0f;
                for (int k = 1; k < path.corners.Length; k++) len += Vector3.Distance(path.corners[k - 1], path.corners[k]);
                Log($"Route spawn 1 -> {name}: {path.status}, {len:F0} m (~{len / 5f:F0} s walking)");
                if (path.status != NavMeshPathStatus.PathComplete) Warn($"No complete route to {name}.");
            }
        }

        // ================================================================== helpers

        static GameObject LoadPack(string name, bool quiet = false)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(Pack + name + ".prefab");
            if (go == null && !quiet) Warn($"MISSING prefab: {Pack}{name}.prefab");
            return go;
        }

        static Transform Child(Transform parent, string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            return t;
        }

        static Transform FindDeep(Transform t, string name)
        {
            foreach (var c in t.GetComponentsInChildren<Transform>(true))
                if (c.name == name) return c;
            return null;
        }

        static Bounds BoundsOf(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }

        static Rect FootprintOf(GameObject go, float margin)
        {
            var b = BoundsOf(go);
            return Rect.MinMaxRect(b.min.x - margin, b.min.z - margin, b.max.x + margin, b.max.z + margin);
        }

        /// <summary>Rotate, then move so the renderer bounds are centred on xz (pivots in the pack are not always centred).</summary>
        static void PlaceCentered(GameObject go, Vector2 xz, float yaw, bool keepPivotY = true)
        {
            // Keep the model's imported root rotation (e.g. Blender axis conversion), add the yaw on top.
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f) * go.transform.rotation;
            go.transform.position = new Vector3(xz.x, 0f, xz.y);
            var b = BoundsOf(go);
            go.transform.position += new Vector3(xz.x - b.center.x, 0f, xz.y - b.center.z);
            if (!keepPivotY) go.transform.position += Vector3.up * -b.min.y;
        }

        static void EnsureColliders(GameObject go)
        {
            if (go.GetComponentInChildren<Collider>() != null) return;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                mf.gameObject.AddComponent<MeshCollider>();
            Log($"  added MeshCollider(s) to {go.name}");
        }

        /// <summary>Returns what a footprint overlaps (null if nothing).</summary>
        static string Clash(Rect fp, List<Rect> others, bool checkRoads)
        {
            foreach (var o in others) if (fp.Overlaps(o)) return "another object";
            Vector2 c = XZ(SquareCenter);
            if (RectCircleDistance(fp, c) < RingRoadOuter + 0.5f) return "the village square";
            if (fp.Overlaps(Expand(ProductionRect, 1f))) return "the production area";
            if (fp.Overlaps(Expand(VineyardRect, 1.5f))) return "the vineyard";
            if (checkRoads)
                foreach (var r in Roads)
                    for (int i = 0; i <= 20; i++)
                    {
                        Vector2 p = Vector2.Lerp(r.a, r.b, i / 20f);
                        if (RectPointDistance(fp, p) < r.width * 0.5f + 0.5f) return r.name;
                    }
            return null;
        }

        static Rect Expand(Rect r, float m) => Rect.MinMaxRect(r.xMin - m, r.yMin - m, r.xMax + m, r.yMax + m);
        static float RectPointDistance(Rect r, Vector2 p)
        {
            float dx = Mathf.Max(r.xMin - p.x, 0f, p.x - r.xMax);
            float dz = Mathf.Max(r.yMin - p.y, 0f, p.y - r.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
        static float RectCircleDistance(Rect r, Vector2 c) => RectPointDistance(r, c);

        static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        static Vector2 XZ(Vector3 v) => new Vector2(v.x, v.z);
        static Vector2 Dir(float deg) => new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad));
        static string Fmt(Vector3 v) => $"({v.x:F1}, {v.y:F1}, {v.z:F1})";

        static Material MakeMat(string name, Color color)
        {
            string path = $"{GenDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.1f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void SetStaticRecursive(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.SetStaticEditorFlags(t.gameObject,
                    StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static void AddSceneToBuildSettingsFirst(string path)
        {
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            list.RemoveAll(s => s.path == path);
            list.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        static void Log(string msg) => report.AppendLine(msg);
        static void Warn(string msg) { warnings++; report.AppendLine("WARNING: " + msg); Debug.LogWarning("[Village] " + msg); }
    }
}
