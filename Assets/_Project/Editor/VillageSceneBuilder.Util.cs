using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mirror;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vineyard.EditorTools
{
    public static partial class VillageSceneBuilder
    {
        // ================================================================== lighting / camera

        static void BuildLighting(Transform parent)
        {
            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(parent, false);
            sunGo.transform.rotation = Quaternion.Euler(38f, -42f, 0f);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.89f, 0.74f);
            sun.intensity = 1.45f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.8f;
            RenderSettings.sun = sun;

            string skyPath = GenDir + "/VillageSky.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
            var skyShader = Shader.Find("Skybox/Procedural");
            if (sky == null && skyShader != null) { sky = new Material(skyShader); AssetDatabase.CreateAsset(sky, skyPath); }
            if (sky != null)
            {
                sky.SetFloat("_SunDisk", 2f);
                sky.SetFloat("_SunSize", 0.03f);
                sky.SetFloat("_SunSizeConvergence", 6f);
                sky.SetFloat("_AtmosphereThickness", 0.82f);
                sky.SetColor("_SkyTint", new Color(0.52f, 0.62f, 0.78f));
                sky.SetColor("_GroundColor", new Color(0.62f, 0.68f, 0.70f));   // hazy, blends with the fog past the terrain edge
                sky.SetFloat("_Exposure", 1.25f);
                EditorUtility.SetDirty(sky);
                RenderSettings.skybox = sky;
            }

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.66f, 0.74f, 0.86f);
            RenderSettings.ambientEquatorColor = new Color(0.66f, 0.63f, 0.55f);
            RenderSettings.ambientGroundColor = new Color(0.33f, 0.30f, 0.24f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.76f, 0.82f, 0.88f);
            RenderSettings.fogStartDistance = 85f;
            RenderSettings.fogEndDistance = 470f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;

            // Post-processing: gentle, readability first.
            string profilePath = GenDir + "/VillageVolumeProfile.asset";
            AssetDatabase.DeleteAsset(profilePath);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, profilePath);
            var tm = profile.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.Neutral);
            var bloom = profile.Add<Bloom>(true); bloom.intensity.Override(0.22f); bloom.threshold.Override(1.1f);
            var ca = profile.Add<ColorAdjustments>(true); ca.saturation.Override(6f); ca.contrast.Override(8f); ca.postExposure.Override(0.15f);
            var wb = profile.Add<WhiteBalance>(true); wb.temperature.Override(6f);
            var vig = profile.Add<Vignette>(true); vig.intensity.Override(0.15f);
            foreach (var comp in profile.components) { comp.name = comp.GetType().Name; AssetDatabase.AddObjectToAsset(comp, profile); }
            EditorUtility.SetDirty(profile);
            var volGo = new GameObject("GlobalVolume");
            volGo.transform.SetParent(parent, false);
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = profile;

            // One baked reflection probe covering the village (custom cubemap, no lightmap bake required).
            var probeGo = new GameObject("ReflectionProbe_Village");
            probeGo.transform.SetParent(parent, false);
            probeGo.transform.position = new Vector3(SquareC.x, GroundY(SquareC.x, SquareC.y) + 3f, SquareC.y);
            var probe = probeGo.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Baked;
            probe.size = new Vector3(130f, 50f, 130f);
            probe.resolution = 128;
            probe.boxProjection = false;
            string cubePath = GenDir + "/VillageReflection.exr";
            Physics.SyncTransforms();
            if (Lightmapping.BakeReflectionProbe(probe, cubePath))
            {
                AssetDatabase.ImportAsset(cubePath);
                probe.mode = ReflectionProbeMode.Custom;
                probe.customBakedTexture = AssetDatabase.LoadAssetAtPath<Texture>(cubePath);
                Log("Reflection probe baked to " + cubePath);
            }
            else Warn("Reflection probe bake failed (probe left in Baked mode; bake lighting manually).");

            EnablePostProcessingOnPlayerCamera();
            Log("Lighting: warm directional sun (soft shadows), procedural sky, trilight ambient, light linear fog 85-470 m (aerial depth on the mountains), gentle global volume (no DOF / no heavy effects).");
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
            Log("Player prefab camera: enabled post-processing (visual only).");
        }

        static void BuildMenuCamera(Transform root)
        {
            var camGo = new GameObject("MenuCamera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(root, false);
            camGo.transform.position = new Vector3(-7f, 17f, -60f);
            camGo.transform.LookAt(new Vector3(0f, 3f, 4f));
            var cam = camGo.AddComponent<Camera>();
            cam.farClipPlane = 900f;
            cam.fieldOfView = 55f;
            camGo.AddComponent<AudioListener>();
            var data = camGo.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        }

        // ================================================================== spawns

        static List<Vector3> BuildSpawns(Transform parent)
        {
            var list = new List<Vector3>();
            Physics.SyncTransforms();
            for (int i = 0; i < SpawnCandidates.Length; i++)
            {
                Vector3 p = FindFreeSpot(W(SpawnCandidates[i]));
                var go = new GameObject($"PlayerSpawn_{i + 1:00}");
                go.transform.SetParent(parent, false);
                go.transform.position = p + Vector3.up * 0.05f;
                Vector3 look = W(SquareC) - p; look.y = 0;
                go.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
                go.AddComponent<NetworkStartPosition>();
                list.Add(p);
            }
            return list;
        }

        static bool CapsuleFree(Vector3 feet) =>
            !Physics.CheckCapsule(feet + Vector3.up * 0.55f, feet + Vector3.up * 1.6f, 0.45f, ~0, QueryTriggerInteraction.Ignore);

        static Vector3 FindFreeSpot(Vector3 p)
        {
            if (CapsuleFree(p)) return p;
            for (float r = 0.5f; r <= 4f; r += 0.5f)
                for (float a = 0; a < 360f; a += 30f)
                {
                    Vector3 q = p + new Vector3(Mathf.Cos(a * Mathf.Deg2Rad), 0, Mathf.Sin(a * Mathf.Deg2Rad)) * r;
                    q.y = GroundY(q.x, q.z);
                    if (CapsuleFree(q)) return q;
                }
            Warn($"No free spawn spot near {Fmt(p)}.");
            return p;
        }

        // ================================================================== verification

        static void Verify(Transform root, List<HouseInfo> houses, List<Vector3> spawns, Vector3 inputAccess, Vector3 outputAccess,
                           Dictionary<string, Vector3> stations, List<Vector3> aisles, BridgeInfo bridge)
        {
            Log("---- Verification ----");
            for (int i = 0; i < spawns.Count; i++)
            {
                bool ground = Physics.Raycast(spawns[i] + Vector3.up * 3f, Vector3.down, out var hit, 6f) && hit.collider is TerrainCollider;
                bool free = CapsuleFree(spawns[i]);
                Log($"Spawn {i + 1} {Fmt(spawns[i])}: ground={(ground ? "terrain" : "NOT TERRAIN")} free={free}");
                if (!ground || !free) Warn($"Spawn {i + 1} is blocked or has no terrain under it.");
            }
            for (int i = 0; i < spawns.Count; i++)
                for (int j = i + 1; j < spawns.Count; j++)
                    if (Vector3.Distance(spawns[i], spawns[j]) < 1.5f) Warn($"Spawns {i + 1} and {j + 1} are closer than 1.5 m.");

            var checks = new List<(string, Vector3)> { ("Bottle funnel access", inputAccess), ("Bottle tap access", outputAccess) };
            foreach (var kv in stations) checks.Add((kv.Key, kv.Value));
            foreach (var (n, p) in checks)
            {
                bool free = CapsuleFree(p);
                Log($"{n} {Fmt(p)} free={free}");
                if (!free) Warn($"{n} is blocked.");
            }

            // Bridge walkability: profile along the deck.
            if (bridge != null)
            {
                float maxStep = 0f, maxSlope = 0f, prevY = float.NaN; string where = "";
                var prof = new System.Text.StringBuilder();
                foreach (float lateral in new[] { -1.2f, 0f, 1.2f })
                {
                    prevY = float.NaN;
                    Vector2 side = new Vector2(bridge.axis.y, -bridge.axis.x) * lateral;
                    for (float s = -9f; s <= 9f; s += 0.25f)
                    {
                        Vector2 p = bridge.center + bridge.axis * s + side;
                        if (!Physics.Raycast(new Vector3(p.x, 30f, p.y), Vector3.down, out var hit, 60f, ~0, QueryTriggerInteraction.Ignore)) continue;
                        if (lateral == 0f && Mathf.Abs(s % 1f) < 0.01f) prof.Append($"{s:F0}:{hit.point.y:F2}({hit.collider.name.Substring(0, Mathf.Min(6, hit.collider.name.Length))}) ");
                        if (!float.IsNaN(prevY))
                        {
                            float dy = Mathf.Abs(hit.point.y - prevY);
                            if (dy > maxStep) { maxStep = dy; where = $"s={s:F2} lateral={lateral} on {hit.collider.name}"; }
                            maxSlope = Mathf.Max(maxSlope, Mathf.Atan2(dy, 0.25f) * Mathf.Rad2Deg);
                        }
                        prevY = hit.point.y;
                    }
                }
                Log($"Bridge deck profile: max height change per 25 cm = {maxStep:F2} m at {where} (player step 0.35), max slope {maxSlope:F0} deg (player limit 45).");
                Log("  centre line: " + prof);
                if (maxStep > 0.33f) Warn("Bridge has a step higher than the player's step offset.");
            }

            // Props that float above the ground.
            int floating = 0;
            foreach (var parent in new[] { H.CenterDecor, H.ResDecor, H.VinDecor, H.ProdDecor, H.Storage, H.Trees })
                foreach (Transform t in parent.GetComponentsInChildren<Transform>())
                {
                    if (!PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject)) continue;
                    string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject);
                    if (!assetPath.Contains("OccaSoftware") || assetPath.Contains("Crate")) continue;   // stacked crates float by design
                    float gy = GroundY(t.position.x, t.position.z);
                    var b = BoundsOf(t.gameObject);
                    if (b.min.y - gy > 0.2f) { if (floating++ < 8) Warn($"Floating prop {t.name} at {Fmt(t.position)} ({b.min.y - gy:F2} m above ground)"); }
                }
            Log($"Floating-prop check: {floating} found.");

            // Materials.
            int badMats = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m == null || m.shader == null || !m.shader.isSupported || m.shader.name == "Hidden/InternalErrorShader")
                    { if (badMats++ < 5) Warn($"Missing / broken material on {r.name}"); }
            Log($"Material check: {badMats} missing or broken material slots.");

            // Scene statistics (performance sanity check).
            {
                var rs = root.GetComponentsInChildren<MeshRenderer>(true);
                long tris = 0; int stat = 0;
                foreach (var r in rs)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf && mf.sharedMesh) tris += mf.sharedMesh.triangles.Length / 3;
                    if (GameObjectUtility.AreStaticEditorFlagsSet(r.gameObject, StaticEditorFlags.BatchingStatic)) stat++;
                }
                var cols = root.GetComponentsInChildren<Collider>(true);
                int meshCols = cols.Count(c => c is MeshCollider);
                var mats = new HashSet<Material>(rs.SelectMany(r => r.sharedMaterials).Where(m => m != null));
                Log($"Stats: {rs.Length} mesh renderers ({stat} batching-static), {tris / 1000}k triangles total (before culling), {mats.Count} distinct materials, {cols.Length} colliders ({meshCols} mesh, rest primitive), 0 realtime lights besides the sun.");
            }

            // NavMesh (also used to verify routes; kept for future NPCs).
            var navGo = new GameObject("NavMesh");
            navGo.transform.SetParent(root, false);
            var surface = navGo.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Volume;
            surface.center = new Vector3(PlayBounds.center.x, 10f, PlayBounds.center.y);
            surface.size = new Vector3(PlayBounds.width, 40f, PlayBounds.height);
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.agentTypeID = 0;
            surface.BuildNavMesh();
            string navPath = GenDir + "/Village_NavMesh.asset";
            AssetDatabase.DeleteAsset(navPath);
            if (surface.navMeshData != null)
            {
                AssetDatabase.CreateAsset(surface.navMeshData, navPath);
                var tri = NavMesh.CalculateTriangulation();
                Log($"NavMesh baked: {tri.indices.Length / 3} triangles.");
            }
            else { Warn("NavMesh bake produced no data."); return; }

            if (spawns.Count == 0) return;
            var targets = new List<(string, Vector3)>
            {
                ("Bottle funnel", inputAccess), ("Bottle tap", outputAccess),
                ("Vineyard gate", W(V(-8.9f, -25.5f))),
                ("Tavern door", W(houses.First(h => h.def.id == "Tavern").DoorPoint + houses.First(h => h.def.id == "Tavern").Forward)),
                ("East hamlet (across bridge)", W(V(40f, 8.5f))),
                ("Pond bench", W(PondC + DirDeg(150f) * (PondExtra + 5f))),
                ("Hilltop house", W(houses.First(h => h.def.id == "House_Hilltop").DoorPoint + houses.First(h => h.def.id == "House_Hilltop").Forward)),
                ("Farmhouse", W(houses.First(h => h.def.id == "Farmhouse").DoorPoint + houses.First(h => h.def.id == "Farmhouse").Forward)),
            };
            foreach (var kv in stations) targets.Add((kv.Key, kv.Value));
            for (int i = 0; i < aisles.Count; i++) targets.Add(($"Vineyard aisle {i + 1}", aisles[i]));
            var path = new NavMeshPath();
            foreach (var (name, target) in targets)
            {
                if (!NavMesh.SamplePosition(spawns[0], out var a, 2f, NavMesh.AllAreas) || !NavMesh.SamplePosition(target, out var b, 2f, NavMesh.AllAreas))
                { Warn($"Route to {name}: point not on the NavMesh ({Fmt(target)})."); continue; }
                NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path);
                float len = 0f;
                for (int k = 1; k < path.corners.Length; k++) len += Vector3.Distance(path.corners[k - 1], path.corners[k]);
                Log($"Route spawn 1 -> {name}: {path.status}, {len:F0} m (~{len / 5f:F0} s walking)");
                if (path.status != NavMeshPathStatus.PathComplete) Warn($"No complete route to {name}.");
            }
            // The production loop itself.
            void Leg(string n, Vector3 from, Vector3 to)
            {
                if (!NavMesh.SamplePosition(from, out var a, 2f, NavMesh.AllAreas) || !NavMesh.SamplePosition(to, out var b, 2f, NavMesh.AllAreas)) { Warn($"Leg {n}: off NavMesh"); return; }
                NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path);
                float len = 0f;
                for (int k = 1; k < path.corners.Length; k++) len += Vector3.Distance(path.corners[k - 1], path.corners[k]);
                Log($"Production leg {n}: {path.status}, {len:F1} m");
            }
            if (aisles.Count > 0 && stations.ContainsKey("Press input")) Leg("vineyard aisle 4 -> press", aisles[aisles.Count - 1], stations["Press input"]);
            if (stations.ContainsKey("Press output") && stations.ContainsKey("Fermenter input side")) Leg("press -> fermenter", stations["Press output"], stations["Fermenter input side"]);
            if (stations.ContainsKey("Fermenter output")) Leg("fermenter -> bottle funnel", stations["Fermenter output"], inputAccess);
        }

        static void LogAssetsUsed(Transform root)
        {
            var counts = new SortedDictionary<string, int>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject)) continue;
                string p = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject);
                counts.TryGetValue(p, out int c); counts[p] = c + 1;
            }
            Log("---- Asset usage (prefab / model instances) ----");
            foreach (var kv in counts) Log($"  {kv.Value,4} x {kv.Key}");
            int det = terrain != null ? terrain.terrainData.detailPrototypes.Length : 0;
            Log($"  + terrain detail prototypes: {det} (grass tuft texture + pack flower meshes)");
            if (missing.Count > 0) Log("MISSING assets: " + string.Join(", ", missing.Distinct()));
        }

        // ================================================================== placement helpers

        enum ColliderMode { Keep, None, Box, Capsule }

        static readonly Dictionary<GameObject, Bounds> localBoundsCache = new Dictionary<GameObject, Bounds>();

        static Bounds PrefabLocalBounds(GameObject prefab)
        {
            if (localBoundsCache.TryGetValue(prefab, out var b)) return b;
            var go = (GameObject)Object.Instantiate(prefab);
            go.transform.SetPositionAndRotation(Vector3.zero, prefab.transform.rotation);
            go.transform.localScale = Vector3.one;
            b = BoundsOf(go);
            Object.DestroyImmediate(go);
            localBoundsCache[prefab] = b;
            return b;
        }

        static GameObject Place(GameObject prefab, Transform parent, Vector2 p, float yaw, float scale, bool alignToGround, float sink, string name)
        {
            if (prefab == null) return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.name = name;
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f) * prefab.transform.rotation;
            if (alignToGround) rot = Quaternion.FromToRotation(Vector3.up, GroundNormal(p.x, p.y)) * rot;
            go.transform.SetPositionAndRotation(new Vector3(p.x, GroundY(p.x, p.y) - sink, p.y), rot);
            go.transform.localScale = prefab.transform.localScale * scale;
            return go;
        }

        static GameObject Prop(string prefabName, Transform parent, Vector2 p, float yaw, float scale, ColliderMode mode, string name, float sink = 0.02f)
        {
            var pf = LoadPack(prefabName);
            if (pf == null) return null;
            var go = Place(pf, parent, p, yaw, scale, false, sink, $"{name}_{prefabName.Replace(" ", "")}");
            SetCollider(go, mode);
            blockers.Add((p, 0.8f * scale));
            return go;
        }

        static void SetCollider(GameObject go, ColliderMode mode)
        {
            if (go == null || mode == ColliderMode.Keep) return;
            foreach (var c in go.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
            if (mode == ColliderMode.None) return;
            var mf = go.GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;
            var mb = mf.sharedMesh.bounds;
            if (mode == ColliderMode.Box)
            {
                var box = mf.gameObject.AddComponent<BoxCollider>();
                box.center = mb.center; box.size = mb.size;
            }
            else
            {
                var cap = mf.gameObject.AddComponent<CapsuleCollider>();
                cap.center = new Vector3(0f, mb.size.y * 0.25f + mb.min.y, 0f);
                cap.radius = 0.35f;
                cap.height = mb.size.y * 0.5f;
                cap.direction = 1;
            }
        }

        static void Rock(Transform parent, Vector2 p, float scale, float sinkFrac, string name, bool collider)
        {
            var pf = LoadPack($"Rock_{rng.Next(1, 5)}");
            if (pf == null) return;
            var lb = PrefabLocalBounds(pf);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(pf, parent);
            go.name = name;
            var s = new Vector3(scale * R(0.9f, 1.15f), scale * R(0.75f, 1.1f), scale * R(0.9f, 1.15f));
            go.transform.localScale = s;
            var n = Vector3.Slerp(Vector3.up, GroundNormal(p.x, p.y), 0.7f);
            go.transform.rotation = Quaternion.FromToRotation(Vector3.up, n) * Quaternion.Euler(R(-6f, 6f), R(0, 360), R(-6f, 6f)) * pf.transform.rotation;
            float bottom = lb.min.y * s.y, height = lb.size.y * s.y;
            go.transform.position = new Vector3(p.x, GroundY(p.x, p.y) - bottom - height * sinkFrac, p.y);
            if (!collider) SetCollider(go, ColliderMode.None);
        }

        static void RockPile(Transform parent, Vector2 c, int count, float radius)
        {
            var g = Child(parent, "RockPile");
            g.position = W(c);
            for (int i = 0; i < count; i++)
            {
                Vector2 p = c + Dir(R(0, 6.28f)) * R(0f, radius);
                Rock(g, p, R(0.3f, 0.65f) * (i == 0 ? 1.4f : 1f), 0.4f, $"Stone_{i:00}", collider: i == 0);
            }
        }

        static void Flagstone(Transform parent, Vector2 p, float scale, string name)
        {
            var pf = LoadPack($"Path Piece_{rng.Next(1, 17)}");
            if (pf == null) return;
            var go = Place(pf, parent, p, R(0, 360), 1f, true, -0.025f, name);
            go.transform.localScale = new Vector3(scale, 1f, scale);
            SetCollider(go, ColliderMode.None);
        }

        static void Flower(Transform parent, Vector2 p, float scale, bool avoidRoads = false)
        {
            if (avoidRoads && (RoadEdge(p.x, p.y) < 0.3f || PlazaSignedDist(p) < 0.2f || StreamDist(p.x, p.y) < 3.8f)) return;
            if (avoidRoads && blockers.Any(b => b.r > 0.5f && Vector2.Distance(b.p, p) < b.r * 0.6f)) return;
            var pf = LoadPack($"Flower_{rng.Next(1, 6)}");
            if (pf == null) return;
            var go = Place(pf, parent, p, R(0, 360), scale, true, 0.04f, pf.name);
            SetCollider(go, ColliderMode.None);
            var mr = go.GetComponentInChildren<MeshRenderer>();
            if (mr) mr.shadowCastingMode = ShadowCastingMode.Off;
        }

        static void TreeAt(Transform parent, Vector2 p, string kind, float scale, bool collider)
        {
            var pf = LoadPack(kind);
            if (pf == null) return;
            var go = Place(pf, parent, p, R(0, 360), scale, false, 0.18f, $"{kind.Replace(" ", "")}");
            go.transform.localScale = new Vector3(scale * R(0.92f, 1.08f), scale * R(0.9f, 1.12f), scale * R(0.92f, 1.08f));
            SetCollider(go, collider ? ColliderMode.Capsule : ColliderMode.None);
            if (collider) blockers.Add((p, 1.2f));
        }

        static GameObject Cliff(Transform parent, Vector2 p, float scale, float yaw, float exposure, string name)
        {
            var pf = LoadPack($"Cliff_{rng.Next(1, 9)}");
            if (pf == null) return null;
            var lb = PrefabLocalBounds(pf);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(pf, parent);
            go.name = name;
            go.transform.localScale = Vector3.one * scale;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f) * pf.transform.rotation;
            float g = GroundY(p.x, p.y);
            go.transform.position = new Vector3(p.x, g + exposure - lb.max.y * scale, p.y);
            return go;
        }

        static void Lantern(Transform parent, Vector2 p, Vector2 lookAt, string name, bool force = false)
        {
            var pf = LoadPack("Lantern");
            if (pf == null) return;
            var go = Place(pf, parent, p, YawTo(p, lookAt), 1f, false, 0.03f, name);
            SetCollider(go, ColliderMode.None);
            var cap = go.AddComponent<CapsuleCollider>();
            cap.center = new Vector3(0f, 1.6f, 0f); cap.radius = 0.18f; cap.height = 3.2f;
            lanternSpots.Add(p);
            blockers.Add((p, 0.9f));
        }

        const float BenchFrontYaw = 0f;   // the bench model's seat faces local +Z (checked in front/back renders)

        static void Bench(Transform parent, Vector2 p, Vector2 lookAt, string name)
        {
            var pf = LoadPack("Bench");
            if (pf == null) return;
            var go = Place(pf, parent, p, YawTo(p, lookAt) + BenchFrontYaw, 1f, false, 0.03f, name);
            SetCollider(go, ColliderMode.Box);
            blockers.Add((p, 1.5f));
        }

        static GameObject CrateStack(Transform parent, Vector2 p, int count, float yaw)
        {
            var pf = LoadPack("Crate");
            if (pf == null) return null;
            var g = Child(parent, "CrateStack");
            g.position = W(p);
            Vector2 r = Right(yaw);
            GameObject first = null;
            Vector2[] offs = count >= 2 ? new[] { -r * 0.52f, r * 0.52f } : new[] { Vector2.zero };
            float baseY = Mathf.Min(GroundY(p.x, p.y), GroundY(p.x + r.x * 0.5f, p.y + r.y * 0.5f), GroundY(p.x - r.x * 0.5f, p.y - r.y * 0.5f));
            int placed = 0;
            for (int i = 0; i < offs.Length && placed < count; i++, placed++)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(pf, g);
                go.name = $"Crate_{placed + 1}";
                go.transform.SetPositionAndRotation(new Vector3(p.x + offs[i].x, baseY + 0.06f, p.y + offs[i].y), Quaternion.Euler(0, yaw + R(-6f, 6f), 0) * pf.transform.rotation);
                SetCollider(go, ColliderMode.Box);
                if (first == null) first = go;
            }
            for (; placed < count; placed++)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(pf, g);
                go.name = $"Crate_{placed + 1}";
                Vector2 o = placed == 2 ? Vector2.zero : r * 0.5f;
                go.transform.SetPositionAndRotation(new Vector3(p.x + o.x, baseY + 0.06f + 0.99f * (placed - 1), p.y + o.y), Quaternion.Euler(0, yaw + R(-15f, 15f), 0) * pf.transform.rotation);
                go.transform.localScale = Vector3.one * R(0.85f, 0.95f);
                SetCollider(go, ColliderMode.Box);
            }
            blockers.Add((p, 1.3f));
            return first;
        }

        /// <summary>Fence segments along a polyline. gap: skip segments whose centre lies in [a,b] (x or z, or distance along the line when relative).</summary>
        static void FenceLine(Transform parent, string name, Vector2[] pts, (float a, float b, bool alongX)? gap, bool relative = false)
        {
            var pf = LoadPack("Fence");
            if (pf == null) return;
            var line = Child(parent, name);
            float segLen = 3.21f, along = 0f;
            int n = 0;
            for (int i = 0; i < pts.Length - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                float len = Vector2.Distance(a, b);
                int count = Mathf.Max(1, Mathf.RoundToInt(len / segLen));
                float actual = len / count;
                Vector2 d = (b - a) / len;
                float yaw = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg - 90f;
                for (int k = 0; k < count; k++)
                {
                    Vector2 c = a + d * actual * (k + 0.5f);
                    float key = relative ? along + actual * (k + 0.5f) : (gap.HasValue && gap.Value.alongX ? c.x : c.y);
                    if (gap.HasValue && key >= Mathf.Min(gap.Value.a, gap.Value.b) && key <= Mathf.Max(gap.Value.a, gap.Value.b)) continue;
                    Vector2 e0 = c - d * actual / 2f, e1 = c + d * actual / 2f;
                    float y = Mathf.Min(GroundY(e0.x, e0.y), GroundY(e1.x, e1.y), GroundY(c.x, c.y));
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(pf, line);
                    go.name = $"Fence_{++n:00}";
                    go.transform.SetPositionAndRotation(new Vector3(c.x, y - 0.04f, c.y), Quaternion.Euler(0, yaw, 0) * pf.transform.rotation);
                    go.transform.localScale = new Vector3(actual / segLen, 1f, 1f);
                    SetCollider(go, ColliderMode.Box);
                }
                along += len;
            }
        }

        /// <summary>Decorative copy of one part of a model (e.g. the press's vat or bucket), unpacked so it is not a gameplay object.</summary>
        static void PartProp(Transform parent, string name, string fbxPath, string part, Vector2 p, float yaw, float scale, bool collider)
        {
            var model = LoadAsset<GameObject>(fbxPath);
            if (model == null) return;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model, parent);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = name;
            foreach (Transform c in go.transform.Cast<Transform>().ToList())
                if (c.name != part) Object.DestroyImmediate(c.gameObject);
            if (go.transform.childCount == 0) { Warn($"Part {part} not found in {fbxPath}"); Object.DestroyImmediate(go); return; }
            go.transform.localScale = Vector3.one * scale;
            go.transform.rotation = Quaternion.Euler(0, yaw, 0) * model.transform.rotation;
            go.transform.position = Vector3.zero;
            var b = BoundsOf(go);
            float g = GroundY(p.x, p.y);
            go.transform.position = new Vector3(p.x - b.center.x, g - b.min.y - 0.02f, p.y - b.center.z);
            if (collider)
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                { var bc = mf.gameObject.AddComponent<BoxCollider>(); bc.center = mf.sharedMesh.bounds.center; bc.size = mf.sharedMesh.bounds.size; }
            blockers.Add((p, 0.8f * scale));
        }

        static void AccessPoint(Transform parent, string name, Vector2 p, float yaw)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(W(p), Quaternion.Euler(0, yaw, 0));
        }

        static void AddPartBoxColliders(GameObject go, string skip)
        {
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.name.Contains(skip) || mf.sharedMesh == null) continue;
                var bc = mf.gameObject.AddComponent<BoxCollider>();
                bc.center = mf.sharedMesh.bounds.center; bc.size = mf.sharedMesh.bounds.size;
            }
        }

        static bool IsFree(Vector2 p, float r)
        {
            if (blockers.Any(b => Vector2.Distance(b.p, p) < b.r + r * 0.5f)) return false;
            if (allHouses.Any(h => h.ObbDistance(p) < r)) return false;
            return true;
        }

        static List<HouseInfo> allHouses = new List<HouseInfo>();

        // ================================================================== terrain textures

        static TerrainLayer[] BuildTerrainLayers()
        {
            string dir = GenDir + "/Textures";
            EnsureFolder(dir);
            foreach (var old in new[] { "Grass", "GrassDark", "Dirt", "Cobble" }) { /* overwritten below */ }

            var list = new List<TerrainLayer>();
            list.Add(MakeLayer("Grass", 7f, (u, v) =>
            {
                float n = TileNoise(u, v, 5f) * 0.55f + TileNoise(u + 0.3f, v + 0.6f, 19f) * 0.45f;
                var c = Color.Lerp(new Color(0.40f, 0.58f, 0.22f), new Color(0.50f, 0.67f, 0.27f), n);
                float s = TileNoise(u + 0.7f, v + 0.1f, 64f);
                if (s > 0.68f) c = Color.Lerp(c, new Color(0.58f, 0.74f, 0.32f), (s - 0.68f) * 3f);
                return c;
            }, (u, v) => TileNoise(u, v, 48f) * 0.6f + TileNoise(u, v, 12f) * 0.4f, 0.6f));
            list.Add(MakeLayer("GrassDark", 9f, (u, v) =>
            {
                float n = TileNoise(u, v, 7f) * 0.6f + TileNoise(u + 0.5f, v, 29f) * 0.4f;
                return Color.Lerp(new Color(0.27f, 0.44f, 0.18f), new Color(0.36f, 0.52f, 0.21f), n);
            }, (u, v) => TileNoise(u, v, 40f), 0.6f));
            list.Add(MakeLayer("Meadow", 8f, (u, v) =>
            {
                float n = TileNoise(u, v, 6f) * 0.6f + TileNoise(u + 0.2f, v + 0.4f, 23f) * 0.4f;
                var c = Color.Lerp(new Color(0.52f, 0.64f, 0.26f), new Color(0.63f, 0.71f, 0.32f), n);
                var ci = Cell(u, v, 22);
                if (ci.f1 < 0.12f && ci.hash > 0.75f) c = Color.Lerp(c, new Color(0.95f, 0.88f, 0.45f), 0.8f);
                return c;
            }, (u, v) => TileNoise(u, v, 40f), 0.5f));
            list.Add(MakeLayer("Dirt", 5f, (u, v) =>
            {
                float n = TileNoise(u, v, 6f) * 0.6f + TileNoise(u + 0.2f, v + 0.7f, 30f) * 0.4f;
                var c = Color.Lerp(new Color(0.55f, 0.42f, 0.28f), new Color(0.66f, 0.53f, 0.37f), n);
                var ci = Cell(u, v, 20);
                if (ci.f1 < 0.22f && ci.hash > 0.55f) c = Color.Lerp(c, new Color(0.72f, 0.66f, 0.56f), 0.7f * (1f - ci.f1 / 0.22f));
                return c;
            }, (u, v) => { var ci = Cell(u, v, 20); float peb = ci.hash > 0.55f ? Mathf.Clamp01(1f - ci.f1 / 0.22f) : 0f; return peb * 0.8f + TileNoise(u, v, 24f) * 0.3f; }, 1.2f));
            list.Add(MakeLayer("Soil", 4f, (u, v) =>
            {
                float n = TileNoise(u, v, 9f) * 0.5f + TileNoise(u + 0.4f, v + 0.2f, 33f) * 0.5f;
                var c = Color.Lerp(new Color(0.33f, 0.22f, 0.14f), new Color(0.45f, 0.31f, 0.20f), n);
                var ci = Cell(u, v, 14);
                c *= 0.9f + 0.2f * Mathf.Clamp01(ci.f2 - ci.f1);
                c.a = 1f;
                return c;
            }, (u, v) => { var ci = Cell(u, v, 14); return Mathf.Clamp01((ci.f2 - ci.f1) * 2f) * 0.7f + TileNoise(u, v, 30f) * 0.3f; }, 1.4f));
            list.Add(MakeLayer("Cobble", 3.2f, (u, v) =>
            {
                var ci = Cell(u, v, 7);
                float edge = Mathf.SmoothStep(0f, 1f, (ci.f2 - ci.f1) * 5f);
                var stone = Color.Lerp(new Color(0.60f, 0.56f, 0.50f), new Color(0.76f, 0.71f, 0.62f), ci.hash);
                stone = Color.Lerp(stone, new Color(0.70f, 0.60f, 0.48f), Mathf.Clamp01(ci.hash2 - 0.7f) * 2f);
                stone *= 0.92f + 0.12f * TileNoise(u, v, 40f);
                var mortar = new Color(0.36f, 0.33f, 0.28f);
                var c = Color.Lerp(mortar, stone, edge); c.a = 1f;
                return c;
            }, (u, v) => { var ci = Cell(u, v, 7); return Mathf.SmoothStep(0f, 1f, (ci.f2 - ci.f1) * 4f) * (0.85f + 0.15f * TileNoise(u, v, 30f)); }, 3f, 0.18f));
            list.Add(MakeLayer("Gravel", 2.5f, (u, v) =>
            {
                var ci = Cell(u, v, 26);
                float edge = Mathf.SmoothStep(0f, 1f, (ci.f2 - ci.f1) * 4f);
                var stone = Color.Lerp(new Color(0.58f, 0.55f, 0.50f), new Color(0.78f, 0.74f, 0.66f), ci.hash);
                var c = Color.Lerp(new Color(0.40f, 0.36f, 0.30f), stone, edge); c.a = 1f;
                return c;
            }, (u, v) => { var ci = Cell(u, v, 26); return Mathf.SmoothStep(0f, 1f, (ci.f2 - ci.f1) * 4f); }, 1.6f, 0.12f));
            return list.ToArray();
        }

        static TerrainLayer MakeLayer(string name, float tile, System.Func<float, float, Color> color, System.Func<float, float, float> height, float normalStrength, float smoothness = 0.04f)
        {
            string dir = GenDir + "/Textures";
            const int size = 512;
            var px = new Color[size * size];
            var hgt = new float[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, v = y / (float)size;
                    px[y * size + x] = color(u, v);
                    hgt[y * size + x] = height(u, v);
                }
            var nrm = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float hl = hgt[y * size + (x + size - 1) % size], hr = hgt[y * size + (x + 1) % size];
                    float hd = hgt[((y + size - 1) % size) * size + x], hu = hgt[((y + 1) % size) * size + x];
                    var n = new Vector3((hl - hr) * normalStrength, (hd - hu) * normalStrength, 1f).normalized;
                    nrm[y * size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }
            var albedo = SaveTexture($"{dir}/{name}.png", px, size, false);
            var normal = SaveTexture($"{dir}/{name}_Normal.png", nrm, size, true);

            string path = $"{GenDir}/Layer_{name}.terrainlayer";
            AssetDatabase.DeleteAsset(path);
            var layer = new TerrainLayer
            {
                diffuseTexture = albedo, normalMapTexture = normal, normalScale = 1f,
                tileSize = new Vector2(tile, tile), smoothness = smoothness, metallic = 0f,
            };
            AssetDatabase.CreateAsset(layer, path);
            return layer;
        }

        static Texture2D SaveTexture(string path, Color[] px, int size, bool normalMap)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.mipmapEnabled = true;
            importer.textureType = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = !normalMap;
            importer.anisoLevel = 4;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Texture2D MakeGrassTuftTexture(string path)
        {
            const int w = 128, h = 128;
            var px = new Color[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(0.45f, 0.62f, 0.25f, 0f);
            var r = new System.Random(5);
            for (int b = 0; b < 22; b++)
            {
                float x0 = 10f + (float)r.NextDouble() * (w - 20f);
                float lean = ((float)r.NextDouble() - 0.5f) * 40f;
                float bh = h * (0.45f + (float)r.NextDouble() * 0.5f);
                float bw = 3f + (float)r.NextDouble() * 3f;
                float shade = (float)r.NextDouble();
                for (int y = 0; y < bh; y++)
                {
                    float t = y / bh;
                    float cx = x0 + lean * t * t;
                    float half = bw * (1f - t);
                    for (int x = Mathf.FloorToInt(cx - half); x <= Mathf.CeilToInt(cx + half); x++)
                    {
                        if (x < 0 || x >= w) continue;
                        var c = Color.Lerp(new Color(0.30f, 0.45f, 0.17f), new Color(0.62f, 0.78f, 0.34f), t * 0.8f + shade * 0.2f);
                        c.a = 1f;
                        px[y * w + x] = c;
                    }
                }
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.alphaIsTransparency = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.mipmapEnabled = true;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static float TileNoise(float u, float v, float freq)
        {
            u = Mathf.Repeat(u, 1f); v = Mathf.Repeat(v, 1f);
            float x = u * freq, y = v * freq;
            float a = Mathf.PerlinNoise(x, y), b = Mathf.PerlinNoise(x - freq, y);
            float c = Mathf.PerlinNoise(x, y - freq), d = Mathf.PerlinNoise(x - freq, y - freq);
            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        struct CellInfo { public float f1, f2, hash, hash2; }

        /// <summary>Tileable Voronoi: distance to nearest / second nearest cell point and the nearest cell's random values.</summary>
        static CellInfo Cell(float u, float v, int cells)
        {
            u = Mathf.Repeat(u, 1f); v = Mathf.Repeat(v, 1f);
            float f1 = 9f, f2 = 9f, hsh = 0f, hsh2 = 0f;
            int cx = Mathf.FloorToInt(u * cells), cy = Mathf.FloorToInt(v * cells);
            for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    int gx = cx + ox, gy = cy + oy;
                    int hx = ((gx % cells) + cells) % cells, hy = ((gy % cells) + cells) % cells;
                    float px = (gx + 0.15f + 0.7f * Hash(hx, hy, 1)) / cells;
                    float py = (gy + 0.15f + 0.7f * Hash(hx, hy, 2)) / cells;
                    float d = Vector2.Distance(new Vector2(u, v), new Vector2(px, py)) * cells;
                    if (d < f1) { f2 = f1; f1 = d; hsh = Hash(hx, hy, 3); hsh2 = Hash(hx, hy, 4); }
                    else if (d < f2) f2 = d;
                }
            return new CellInfo { f1 = f1, f2 = f2, hash = hsh, hash2 = hsh2 };
        }

        // ================================================================== generic helpers

        static GameObject LoadPack(string name)
        {
            string path = Pack + name + ".prefab";
            if (prefabCache.TryGetValue(path, out var go)) return go;
            go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) { missing.Add(path); Warn($"MISSING prefab: {path}"); }
            prefabCache[path] = go;
            return go;
        }

        static T LoadAsset<T>(string path) where T : Object
        {
            var a = AssetDatabase.LoadAssetAtPath<T>(path);
            if (a == null) { missing.Add(path); Warn($"MISSING asset: {path}"); }
            return a;
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

        static List<Vector2> CatmullRom(Vector2[] ctrl, float step)
        {
            var outPts = new List<Vector2>();
            int n = ctrl.Length;
            for (int i = 0; i < n - 1; i++)
            {
                Vector2 p0 = ctrl[Mathf.Max(i - 1, 0)], p1 = ctrl[i], p2 = ctrl[i + 1], p3 = ctrl[Mathf.Min(i + 2, n - 1)];
                int segs = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(p1, p2) / step));
                for (int s = 0; s < segs; s++)
                {
                    float t = s / (float)segs, t2 = t * t, t3 = t2 * t;
                    outPts.Add(0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3));
                }
            }
            outPts.Add(ctrl[n - 1]);
            return outPts;
        }

        static float DistToSegment(Vector2 p, Vector2 a, Vector2 b, out float t)
        {
            Vector2 ab = b - a;
            t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        static int NearestIndex(List<Vector2> pts, Vector2 p)
        {
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < pts.Count; i++) { float d = (pts[i] - p).sqrMagnitude; if (d < bd) { bd = d; best = i; } }
            return best;
        }

        static float RectDist(Rect r, Vector2 p)
        {
            float dx = Mathf.Max(r.xMin - p.x, 0f, p.x - r.xMax);
            float dz = Mathf.Max(r.yMin - p.y, 0f, p.y - r.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static float GroundY(float x, float z)
        {
            if (terrain == null) return BaseHeight(x, z);
            return terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y;
        }

        static Vector3 GroundNormal(float x, float z)
        {
            if (terrain == null) return Vector3.up;
            var tp = terrain.transform.position;
            return terrain.terrainData.GetInterpolatedNormal((x - tp.x) / TerrainSize, (z - tp.z) / TerrainSize);
        }

        static Vector3 W(Vector2 p) => new Vector3(p.x, GroundY(p.x, p.y), p.y);
        static Vector2 Dir(float rad) => new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        static Vector2 DirDeg(float deg) => Dir(deg * Mathf.Deg2Rad);
        static float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        static float YawTo(Vector2 from, Vector2 to) { var d = to - from; return Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg; }
        static float YawFromDir(Vector2 d) => Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
        static Vector2 Fwd(float yaw) => new Vector2(Mathf.Sin(yaw * Mathf.Deg2Rad), Mathf.Cos(yaw * Mathf.Deg2Rad));
        static Vector2 Right(float yaw) => new Vector2(Mathf.Cos(yaw * Mathf.Deg2Rad), -Mathf.Sin(yaw * Mathf.Deg2Rad));
        static string Fmt(Vector3 v) => $"({v.x:F1}, {v.y:F1}, {v.z:F1})";

        static void SaveMesh(Mesh mesh, string path)
        {
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
        }

        static void SetStaticRecursive(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.SetStaticEditorFlags(t.gameObject,
                    StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic |
                    StaticEditorFlags.ReflectionProbeStatic | StaticEditorFlags.ContributeGI);
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
