using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Mirror;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Vineyard.Winery;

namespace Vineyard.EditorTools
{
    /// <summary>
    /// Tools > Vineyard > Performance
    ///
    ///   1. Capture Baseline (Play Mode)        - measure BEFORE optimizing (static scene stats + runtime counters)
    ///   2. Apply Optimizations                 - backs up, then applies the project / import / scene changes below
    ///   3. Capture After Optimization (Play)   - measure again; writes Logs/Performance/Comparison.txt
    ///
    /// Everything is idempotent. Scene-level optimizations are also re-applied by the Village builder, so a rebuild
    /// keeps them. Nothing here touches gameplay logic, networking, prefab references or object placement.
    ///
    /// What "Apply Optimizations" changes:
    ///  Import (non-destructive, .meta only): Mesh LOD generation on the 5 custom models (vine, bunch, press,
    ///     fermenter, giant bottle). Unity simplifies at import (keeps UVs / normals / sub-meshes) and picks a level per
    ///     renderer from its screen-space error, so close-ups stay at full detail. Source FBX files are untouched.
    ///  URP (PC tier): 2 shadow cascades instead of 4, medium soft-shadow filter instead of high, no opaque-texture copy
    ///     (nothing samples it), SSAO at half resolution from depth (drops the extra depth-normals pre-pass).
    ///  Quality / player: V-Sync on for the PC tier (stops uncapped frame rates heating Mac laptops into throttling),
    ///     Metal API validation off (CPU overhead in the Mac editor), frame-timing stats on (GPU time in builds).
    ///  Scene: see <see cref="OptimizeScene"/>.
    /// </summary>
    [InitializeOnLoad]
    public static class VillagePerformance
    {
        const string ScenePath = "Assets/_Project/Scenes/Village.unity";
        const string OutDir = "Logs/Performance";
        const string QualityTier = "PC";

        static readonly string[] MeshLodModels =
        {
            "Assets/Grapevine.fbx", "Assets/GrapeBunch.fbx", "Assets/GiantWineTank.fbx",
            "Assets/SmallGrapePress.fbx", "Assets/SmallFermentationStation.fbx",
        };

        // Tunables (see OptimizeScene).
        const float TinyShadowSize = 0.4f;          // m: renderers smaller than this stop casting shadows (not gameplay parts)
        const float FlatShadowHeight = 0.08f;       // m: ground-hugging pieces (flagstones) stop casting shadows
        const float SmallPropMaxSize = 1.3f;        // m: props up to this size get a cull-only LODGroup
        const float SmallPropCullDistance = 50f;    // m at LOD bias 1 and 75 deg FOV (the PC tier uses LOD bias 2 -> ~100 m)
        const float DetailDistance = 60f;           // m: terrain grass / flower draw distance (was 75)
        const float TerrainPixelError = 4f;         // was 3
        static readonly Rect PlayBounds = Rect.MinMaxRect(-53f, -47f, 53f, 49f);   // VillageSceneBuilder.PlayBounds
        const float PlayBoundsMargin = 3f;

        // ================================================================== menu

        [MenuItem("Tools/Vineyard/Performance/1. Capture Baseline (Play Mode)", priority = 200)]
        static void CaptureBaselineMenu() => Capture("Baseline");

        [MenuItem("Tools/Vineyard/Performance/2. Apply Optimizations", priority = 201)]
        static void ApplyMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { EditorUtility.DisplayDialog("Performance", "Exit Play Mode first.", "OK"); return; }
            if (!File.Exists(Path.Combine(OutDir, "Baseline.tsv")) &&
                !EditorUtility.DisplayDialog("Performance", "No baseline capture found (Logs/Performance/Baseline.tsv).\n\n" +
                    "Run '1. Capture Baseline' first if you want a before/after comparison.", "Apply anyway", "Cancel")) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string result = ApplyAll();
            EditorUtility.DisplayDialog("Performance", result + "\n\nDetails: Logs/Performance/OptimizationLog.txt\n" +
                "Next: '3. Capture After Optimization' to measure.", "OK");
        }

        [MenuItem("Tools/Vineyard/Performance/3. Capture After Optimization (Play Mode)", priority = 202)]
        static void CaptureAfterMenu() => Capture("After");

        [MenuItem("Tools/Vineyard/Performance/Write Scene Stats (Edit Mode)", priority = 220)]
        static void StatsMenu()
        {
            Directory.CreateDirectory(OutDir);
            var path = Path.Combine(OutDir, "SceneStats.txt");
            File.WriteAllText(path, SceneStats.Collect(SceneManager.GetActiveScene()).ToText());
            Debug.Log("[Performance] " + Path.GetFullPath(path));
        }

        // ================================================================== apply

        public static string ApplyAll()
        {
            var log = new StringBuilder();
            log.AppendLine($"Performance optimization {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Directory.CreateDirectory(OutDir);

            if (SceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath);
            var scene = SceneManager.GetActiveScene();
            string backup = Backup(log);

            log.AppendLine();
            log.AppendLine("---- Project settings / import ----");
            ApplyProjectSettings(log);

            log.AppendLine();
            log.AppendLine("---- Scene ----");
            scene = SceneManager.GetActiveScene();          // re-imports must not have unloaded it, but be safe
            log.AppendLine(OptimizeScene(scene, log));
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            log.AppendLine();
            log.AppendLine("---- Scene stats after ----");
            log.Append(SceneStats.Collect(scene).ToText());
            File.WriteAllText(Path.Combine(OutDir, "OptimizationLog.txt"), log.ToString());
            Debug.Log($"[Performance] Optimizations applied. Backup: {backup}. Log: {Path.GetFullPath(Path.Combine(OutDir, "OptimizationLog.txt"))}");
            return $"Optimizations applied.\nBackup: {backup}";
        }

        static string Backup(StringBuilder log)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            // Scene copy next to the builder's own backups (tracked, restorable from the Project window).
            const string sceneBackupDir = "Assets/_Project/Scenes/Backups";
            if (!AssetDatabase.IsValidFolder(sceneBackupDir)) AssetDatabase.CreateFolder("Assets/_Project/Scenes", "Backups");
            string sceneCopy = $"{sceneBackupDir}/Village_BeforePerfOpt_{stamp}.unity";
            AssetDatabase.CopyAsset(ScenePath, sceneCopy);
            log.AppendLine("Scene backup: " + sceneCopy);

            // Raw copies of every settings / import file this tool edits (outside Assets so Unity does not import them).
            string dir = Path.Combine(OutDir, "Backup_" + stamp);
            Directory.CreateDirectory(dir);
            var files = new List<string> { "ProjectSettings/QualitySettings.asset", "ProjectSettings/ProjectSettings.asset" };
            var rp = PcPipelineAsset();
            if (rp != null)
            {
                files.Add(AssetDatabase.GetAssetPath(rp));
                foreach (var rd in RendererDatas(rp)) files.Add(AssetDatabase.GetAssetPath(rd));
            }
            files.AddRange(MeshLodModels.Select(m => m + ".meta"));
            foreach (var f in files.Distinct().Where(File.Exists))
                File.Copy(f, Path.Combine(dir, f.Replace('/', '_')), true);
            log.AppendLine("Settings backup: " + Path.GetFullPath(dir) + " (" + string.Join(", ", files.Distinct()) + ")");
            return sceneCopy + " + " + dir;
        }

        // ================================================================== project settings

        static UniversalRenderPipelineAsset PcPipelineAsset()
        {
            int i = Array.IndexOf(QualitySettings.names, QualityTier);
            var rp = i >= 0 ? QualitySettings.GetRenderPipelineAssetAt(i) as UniversalRenderPipelineAsset : null;
            return rp != null ? rp : GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
        }

        static IEnumerable<ScriptableRendererData> RendererDatas(UniversalRenderPipelineAsset rp)
        {
            var so = new SerializedObject(rp);
            var list = so.FindProperty("m_RendererDataList");
            for (int i = 0; list != null && i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue is ScriptableRendererData d) yield return d;
        }

        public static void ApplyProjectSettings(StringBuilder log)
        {
            // ---- Mesh LOD on the custom models (import setting only; the FBX files are not modified).
            foreach (var path in MeshLodModels)
            {
                if (!(AssetImporter.GetAtPath(path) is ModelImporter mi)) { log.AppendLine($"  {path}: not found"); continue; }
                if (mi.generateMeshLods && mi.maximumMeshLod == -1) { log.AppendLine($"  {path}: Mesh LOD already on"); continue; }
                mi.generateMeshLods = true;
                mi.maximumMeshLod = -1;                 // generate until a level is ~64 indices
                mi.SaveAndReimport();
                var meshes = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToList();
                string lods = string.Join(", ", meshes.Select(m => $"{m.name}:{LodSummary(m)}").Take(6));
                log.AppendLine($"  {path}: Mesh LOD generation ON -> {lods}{(meshes.Count > 6 ? $" (+{meshes.Count - 6} more parts)" : "")}");
            }

            // ---- URP asset used by the PC tier.
            var rp = PcPipelineAsset();
            if (rp == null) log.AppendLine("  URP asset not found");
            else
            {
                var so = new SerializedObject(rp);
                SetInt(so, "m_ShadowCascadeCount", 2, log, rp.name, onlyIfGreater: true);
                SetFloat(so, "m_Cascade2Split", 0.2f, log, rp.name);
                SetInt(so, "m_SoftShadowQuality", (int)SoftShadowQuality.Medium, log, rp.name, onlyIfGreater: true);
                SetBool(so, "m_RequireOpaqueTexture", false, log, rp.name);
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(rp);

                foreach (var rd in RendererDatas(rp))
                    foreach (var f in rd.rendererFeatures.Where(f => f != null && f.GetType().Name == "ScreenSpaceAmbientOcclusion"))
                    {
                        var fso = new SerializedObject(f);
                        SetBool(fso, "m_Settings.Downsample", true, log, rd.name + "/SSAO");
                        SetInt(fso, "m_Settings.Source", 0, log, rd.name + "/SSAO");   // 0 = Depth, 1 = DepthNormals
                        fso.ApplyModifiedPropertiesWithoutUndo();
                        EditorUtility.SetDirty(f);
                        EditorUtility.SetDirty(rd);
                    }
            }

            // ---- Quality tier: V-Sync.
            var qAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset").FirstOrDefault();
            if (qAsset != null)
            {
                var qso = new SerializedObject(qAsset);
                var levels = qso.FindProperty("m_QualitySettings");
                for (int i = 0; i < levels.arraySize; i++)
                {
                    var lvl = levels.GetArrayElementAtIndex(i);
                    if (lvl.FindPropertyRelative("name").stringValue != QualityTier) continue;
                    SetInt(lvl.FindPropertyRelative("vSyncCount"), 1, log, $"Quality '{QualityTier}' vSyncCount");
                }
                qso.ApplyModifiedPropertiesWithoutUndo();
            }

            // ---- Player settings.
            if (PlayerSettings.enableMetalAPIValidation)
            {
                PlayerSettings.enableMetalAPIValidation = false;
                log.AppendLine("  PlayerSettings.enableMetalAPIValidation: true -> false");
            }
            if (!PlayerSettings.enableFrameTimingStats)
            {
                PlayerSettings.enableFrameTimingStats = true;
                log.AppendLine("  PlayerSettings.enableFrameTimingStats: false -> true");
            }
            AssetDatabase.SaveAssets();
        }

        static string LodSummary(Mesh m)
        {
            if (m.lodCount <= 1) return $"{Tris(m)} tris (no LODs)";
            var parts = new List<string>();
            for (int l = 0; l < m.lodCount; l++)
            {
                long idx = 0;
                for (int s = 0; s < m.subMeshCount; s++) idx += m.GetLod(s, l).indexCount;
                parts.Add((idx / 3).ToString());
            }
            return string.Join("/", parts) + " tris";
        }

        static void SetInt(SerializedObject so, string prop, int v, StringBuilder log, string owner, bool onlyIfGreater = false)
        {
            var p = so.FindProperty(prop);
            if (p == null) { log.AppendLine($"  {owner}.{prop}: property not found"); return; }
            int old = p.propertyType == SerializedPropertyType.Enum ? p.enumValueIndex : p.intValue;
            if (old == v || (onlyIfGreater && old < v)) { log.AppendLine($"  {owner}.{prop}: {old} (unchanged)"); return; }
            if (p.propertyType == SerializedPropertyType.Enum) p.enumValueIndex = v; else p.intValue = v;
            log.AppendLine($"  {owner}.{prop}: {old} -> {v}");
        }

        static void SetInt(SerializedProperty p, int v, StringBuilder log, string label)
        {
            if (p == null) { log.AppendLine($"  {label}: property not found"); return; }
            if (p.intValue == v) { log.AppendLine($"  {label}: {v} (unchanged)"); return; }
            log.AppendLine($"  {label}: {p.intValue} -> {v}");
            p.intValue = v;
        }

        static void SetFloat(SerializedObject so, string prop, float v, StringBuilder log, string owner)
        {
            var p = so.FindProperty(prop);
            if (p == null) { log.AppendLine($"  {owner}.{prop}: property not found"); return; }
            if (Mathf.Approximately(p.floatValue, v)) { log.AppendLine($"  {owner}.{prop}: {v} (unchanged)"); return; }
            log.AppendLine($"  {owner}.{prop}: {p.floatValue} -> {v}");
            p.floatValue = v;
        }

        static void SetBool(SerializedObject so, string prop, bool v, StringBuilder log, string owner)
        {
            var p = so.FindProperty(prop);
            if (p == null) { log.AppendLine($"  {owner}.{prop}: property not found"); return; }
            if (p.boolValue == v) { log.AppendLine($"  {owner}.{prop}: {v} (unchanged)"); return; }
            log.AppendLine($"  {owner}.{prop}: {p.boolValue} -> {v}");
            p.boolValue = v;
        }

        // ================================================================== scene

        /// <summary>
        /// Scene-level optimizations (idempotent; also called by <see cref="VillageSceneBuilder.Build"/>):
        ///  * Vineyard grape bunches stop casting shadows (100+ small renderers x 3 sub-meshes x every shadow cascade;
        ///    the bunch shadow is hidden under the vine's own leaf shadow).
        ///  * Tiny / ground-flat decorative renderers stop casting shadows (pebbles, flagstones, bottle bolts / emblem).
        ///  * Small static props (rocks, flowers, pots, crates, path pieces) get a single-level LODGroup that culls them
        ///    once they are a few pixels tall (~100 m on the PC tier). No mesh is changed or removed.
        ///  * Crate / Flower Pot MeshColliders -> BoxColliders sized to the same mesh bounds (both are box-shaped).
        ///  * Colliders that lie entirely outside the walkable map (behind the invisible bounds) are disabled.
        ///  * Terrain: grass / flower draw distance 75 -> 60 m, heightmap pixel error 3 -> 4.
        /// Gameplay objects (vines, stations, interactables, network objects, spawns) are never touched apart from the
        /// bunch shadow flag.
        /// </summary>
        public static string OptimizeScene(Scene scene, StringBuilder log = null)
        {
            log ??= new StringBuilder();
            var roots = scene.GetRootGameObjects();
            var renderers = roots.SelectMany(r => r.GetComponentsInChildren<MeshRenderer>(true)).ToList();

            // ---- 1. grape bunch shadows
            int bunchR = 0;
            foreach (var gc in roots.SelectMany(r => r.GetComponentsInChildren<GrapevineController>(true)))
                foreach (var b in gc.bunches ?? new Transform[0])
                    if (b)
                        foreach (var r in b.GetComponentsInChildren<MeshRenderer>(true))
                            if (r.shadowCastingMode != ShadowCastingMode.Off) { r.shadowCastingMode = ShadowCastingMode.Off; bunchR++; }

            // ---- 2. tiny / flat decorative shadow casters
            int tiny = 0, flat = 0;
            foreach (var r in renderers)
            {
                if (r.shadowCastingMode != ShadowCastingMode.On) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                bool bottleDetail = r.name.StartsWith("Port_Bolt") || r.name.StartsWith("Sign_");
                if (IsGameplay(r.transform) && !bottleDetail) continue;
                Vector3 size = WorldSize(r.transform, mf.sharedMesh.bounds);
                if (Mathf.Max(size.x, size.y, size.z) < TinyShadowSize || bottleDetail) { r.shadowCastingMode = ShadowCastingMode.Off; tiny++; }
                else if (size.y < FlatShadowHeight && r.gameObject.isStatic) { r.shadowCastingMode = ShadowCastingMode.Off; flat++; }
            }

            // ---- 3. cull-only LODGroups on small static props
            int lodGroups = 0;
            float tanHalf = Mathf.Tan(37.5f * Mathf.Deg2Rad);
            foreach (var go in roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject).ToList())
            {
                if (!go.isStatic || !PrefabUtility.IsAnyPrefabInstanceRoot(go) || IsGameplay(go.transform)) continue;
                var rs = go.GetComponentsInChildren<MeshRenderer>(true);
                if (rs.Length == 0 || rs.Any(r => r.GetComponent<MeshFilter>() == null || r.GetComponent<MeshFilter>().sharedMesh == null)) continue;
                var existing = go.GetComponentInChildren<LODGroup>(true);
                if (existing != null && existing.gameObject != go) continue;
                if (go.GetComponentInParent<LODGroup>(true) is LODGroup parentGroup && parentGroup.gameObject != go) continue;
                Bounds b = WorldBounds(rs);
                float s = Mathf.Max(b.size.x, b.size.y, b.size.z);
                if (s <= 0.01f || s > SmallPropMaxSize) continue;
                var g = existing != null ? existing : go.AddComponent<LODGroup>();
                // Relative screen height of an object of size s at distance D: s / (2 D tan(fov/2)).
                float h = Mathf.Clamp(s / (2f * SmallPropCullDistance * tanHalf), 0.002f, 0.05f);
                g.fadeMode = LODFadeMode.None;
                g.SetLODs(new[] { new LOD(h, rs) });
                g.RecalculateBounds();
                lodGroups++;
            }

            // ---- 4. colliders
            int boxed = 0;
            foreach (var mc in roots.SelectMany(r => r.GetComponentsInChildren<MeshCollider>(true)).ToList())
            {
                if (mc.convex || mc.isTrigger || mc.sharedMesh == null || IsGameplay(mc.transform)) continue;
                var root = PrefabUtility.GetOutermostPrefabInstanceRoot(mc.gameObject);
                string src = root ? AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(root)) : "";
                if (!src.EndsWith("/Crate.prefab") && !src.EndsWith("/Flower Pot.prefab")) continue;
                var mb = mc.sharedMesh.bounds;
                var go = mc.gameObject;
                UnityEngine.Object.DestroyImmediate(mc);
                var box = go.AddComponent<BoxCollider>();
                box.center = mb.center; box.size = mb.size;
                boxed++;
            }

            Physics.SyncTransforms();
            var outer = new Rect(PlayBounds.xMin - PlayBoundsMargin, PlayBounds.yMin - PlayBoundsMargin,
                                 PlayBounds.width + 2 * PlayBoundsMargin, PlayBounds.height + 2 * PlayBoundsMargin);
            var disabled = new Dictionary<string, int>();
            foreach (var c in roots.SelectMany(r => r.GetComponentsInChildren<Collider>(false)).ToList())
            {
                if (!c.enabled || c.isTrigger || c is TerrainCollider || c is CharacterController || IsGameplay(c.transform)) continue;
                if (c.transform.parent && c.transform.parent.name == "MapBounds") continue;
                var b = c.bounds;
                bool outside = b.max.x < outer.xMin || b.min.x > outer.xMax || b.max.z < outer.yMin || b.min.z > outer.yMax;
                if (!outside) continue;
                c.enabled = false;
                string k = c.GetType().Name;
                disabled[k] = disabled.TryGetValue(k, out int n) ? n + 1 : 1;
            }

            // ---- 5. terrain
            var terrainNotes = new List<string>();
            foreach (var t in roots.SelectMany(r => r.GetComponentsInChildren<Terrain>(true)))
            {
                if (t.detailObjectDistance > DetailDistance) { terrainNotes.Add($"{t.name} detail distance {t.detailObjectDistance} -> {DetailDistance}"); t.detailObjectDistance = DetailDistance; }
                if (t.heightmapPixelError < TerrainPixelError) { terrainNotes.Add($"{t.name} pixel error {t.heightmapPixelError} -> {TerrainPixelError}"); t.heightmapPixelError = TerrainPixelError; }
            }

            // ---- 6. report-only: runtime material instances saved in the scene
            var instanced = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null && !EditorUtility.IsPersistent(m)).Distinct().ToList();

            EditorSceneManager.MarkSceneDirty(scene);
            string summary =
                $"bunch shadows off: {bunchR} renderers; tiny shadow casters off: {tiny}; flat ground pieces off: {flat}; " +
                $"cull LODGroups: {lodGroups}; MeshCollider->Box: {boxed}; colliders disabled outside play area: " +
                $"{(disabled.Count == 0 ? "0" : string.Join(", ", disabled.Select(kv => $"{kv.Value} {kv.Key}")))}; " +
                $"terrain: {(terrainNotes.Count == 0 ? "unchanged" : string.Join("; ", terrainNotes))}; " +
                $"non-asset material instances in scene: {instanced.Count}{(instanced.Count > 0 ? " (" + string.Join(", ", instanced.Select(m => m.name).Take(10)) + ")" : "")}";
            log.AppendLine("  " + summary);
            return summary;
        }

        static readonly HashSet<string> GameplayNames = new HashSet<string>
        {
            "Gameplay", "GiantWineBottle", "SmallGrapePress", "SmallFermentationStation", "GrapeRows", "PlayerSpawns", "Networking", "NetworkManager",
        };

        /// <summary>Anything a gameplay script drives or a player interacts with.</summary>
        static bool IsGameplay(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
            {
                if (GameplayNames.Contains(p.name)) return true;
                if (p.GetComponent<GrapevineController>() || p.GetComponent<WineryInteractable>() || p.GetComponent<WineryStation>() ||
                    p.GetComponent<NetworkIdentity>() || p.GetComponent<ParticleSystem>()) return true;
            }
            return false;
        }

        static Vector3 WorldSize(Transform t, Bounds local)
        {
            // World AABB of the local bounds (handles rotation and non-uniform scale).
            var m = t.localToWorldMatrix;
            Vector3 e = local.extents, r = Vector3.zero;
            for (int i = 0; i < 3; i++)
                r[i] = Mathf.Abs(m[i, 0]) * e.x + Mathf.Abs(m[i, 1]) * e.y + Mathf.Abs(m[i, 2]) * e.z;
            return r * 2f;
        }

        static Bounds WorldBounds(IEnumerable<MeshRenderer> rs)
        {
            bool any = false; Bounds b = default;
            foreach (var r in rs)
            {
                var mf = r.GetComponent<MeshFilter>();
                var mb = mf.sharedMesh.bounds;
                var c = r.transform.TransformPoint(mb.center);
                var bb = new Bounds(c, WorldSize(r.transform, mb));
                if (!any) { b = bb; any = true; } else b.Encapsulate(bb);
            }
            return b;
        }

        internal static long Tris(Mesh m)
        {
            long idx = 0;
            for (int s = 0; s < m.subMeshCount; s++)
                idx += m.lodCount > 1 ? m.GetLod(s, 0).indexCount : (long)m.GetIndexCount(s);
            return idx / 3;
        }

        // ================================================================== static scene stats

        internal class SceneStats
        {
            public int renderers, activeRenderers, shadowCasters, staticBatched, lodGroups, meshLodRenderers, materials, materialInstances;
            public long tris, shadowTris, vineyardTris, vineyardShadowTris;
            public int vineyardRenderers, vineyardSubmeshDraws, vineyardShadowDraws;
            public Dictionary<string, int> colliders = new Dictionary<string, int>();
            public List<(string name, long tris, int renderers)> heavy = new List<(string, long, int)>();

            public static SceneStats Collect(Scene scene)
            {
                var s = new SceneStats();
                var roots = scene.GetRootGameObjects();
                var mats = new HashSet<Material>();
                foreach (var r in roots.SelectMany(g => g.GetComponentsInChildren<MeshRenderer>(true)))
                {
                    s.renderers++;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    s.activeRenderers++;
                    long t = Tris(mf.sharedMesh);
                    s.tris += t;
                    bool shadow = r.shadowCastingMode != ShadowCastingMode.Off;
                    if (shadow) { s.shadowCasters++; s.shadowTris += t; }
                    if (GameObjectUtility.AreStaticEditorFlagsSet(r.gameObject, StaticEditorFlags.BatchingStatic)) s.staticBatched++;
                    if (mf.sharedMesh.lodCount > 1) s.meshLodRenderers++;
                    foreach (var m in r.sharedMaterials) if (m) { mats.Add(m); if (!EditorUtility.IsPersistent(m)) s.materialInstances++; }
                    bool vine = r.GetComponentInParent<GrapevineController>(true) != null;
                    if (vine)
                    {
                        s.vineyardRenderers++; s.vineyardTris += t;
                        s.vineyardSubmeshDraws += mf.sharedMesh.subMeshCount;
                        if (shadow) { s.vineyardShadowTris += t; s.vineyardShadowDraws += mf.sharedMesh.subMeshCount; }
                    }
                }
                s.materials = mats.Count;
                s.lodGroups = roots.SelectMany(g => g.GetComponentsInChildren<LODGroup>(true)).Count();
                foreach (var c in roots.SelectMany(g => g.GetComponentsInChildren<Collider>(true)))
                {
                    string k = c.GetType().Name + (c.enabled ? "" : " (disabled)");
                    s.colliders[k] = s.colliders.TryGetValue(k, out int n) ? n + 1 : 1;
                }
                // Heaviest top-level objects (direct children of the main groups).
                foreach (var t in roots.SelectMany(g => g.GetComponentsInChildren<Transform>(true)).Where(t => PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)))
                {
                    var rs = t.GetComponentsInChildren<MeshFilter>(true).Where(m => m.sharedMesh).ToList();
                    long tt = rs.Sum(m => Tris(m.sharedMesh));
                    if (tt >= 1500) s.heavy.Add((t.name, tt, rs.Count));
                }
                s.heavy = s.heavy.OrderByDescending(h => h.tris).Take(12).ToList();
                return s;
            }

            public IEnumerable<(string key, double value)> Metrics()
            {
                yield return ("scene.renderers.active", activeRenderers);
                yield return ("scene.triangles.lod0", tris);
                yield return ("scene.shadowCasters", shadowCasters);
                yield return ("scene.shadowCasterTriangles", shadowTris);
                yield return ("scene.staticBatchedRenderers", staticBatched);
                yield return ("scene.lodGroups", lodGroups);
                yield return ("scene.meshLodRenderers", meshLodRenderers);
                yield return ("scene.materials", materials);
                yield return ("scene.materialInstances", materialInstances);
                yield return ("scene.meshColliders", colliders.TryGetValue("MeshCollider", out int mc) ? mc : 0);
                yield return ("scene.collidersEnabled", colliders.Where(kv => !kv.Key.Contains("disabled")).Sum(kv => kv.Value));
                yield return ("vineyard.renderers", vineyardRenderers);
                yield return ("vineyard.triangles", vineyardTris);
                yield return ("vineyard.subMeshDraws", vineyardSubmeshDraws);
                yield return ("vineyard.shadowSubMeshDraws", vineyardShadowDraws);
                yield return ("vineyard.shadowTriangles", vineyardShadowTris);
            }

            public string ToText()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Mesh renderers: {renderers} ({activeRenderers} active), {staticBatched} batching-static, {lodGroups} LODGroups, {meshLodRenderers} using Mesh LOD");
                sb.AppendLine($"Triangles (LOD0, before culling): {tris:N0}; in shadow casters: {shadowTris:N0} ({shadowCasters} casters)");
                sb.AppendLine($"Materials: {materials} distinct, {materialInstances} non-asset instance slots");
                sb.AppendLine($"Vineyard: {vineyardRenderers} renderers, {vineyardTris:N0} tris, {vineyardSubmeshDraws} sub-mesh draws " +
                              $"({vineyardShadowDraws} also drawn per shadow cascade, {vineyardShadowTris:N0} shadow tris)");
                sb.AppendLine("Colliders: " + string.Join(", ", colliders.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}")));
                sb.AppendLine("Heaviest objects: " + string.Join(", ", heavy.Select(h => $"{h.name} {h.tris:N0} tris/{h.renderers} parts")));
                return sb.ToString();
            }
        }

        // ================================================================== runtime capture (play mode)

        const string FlagLabel = "Vineyard.Perf.Label";
        const string FlagStatic = "Vineyard.Perf.Static";
        const string FlagStaticTsv = "Vineyard.Perf.StaticTsv";
        const float WarmupSeconds = 2.5f, SampleSeconds = 5f;

        struct View { public string name; public Vector2 pos; public float eye; public Vector3 lookAt; }

        // Player-height views of the areas that matter + the menu camera's overview (worst case: whole village visible).
        static readonly View[] Views =
        {
            new View { name = "Vineyard",   pos = new Vector2(-9f, -24.2f), eye = 1.7f, lookAt = new Vector3(-15f, 0.5f, -33f) },
            new View { name = "Square",     pos = new Vector2(9f, 1.5f),    eye = 1.7f, lookAt = new Vector3(0f, 3f, 4f) },
            new View { name = "Production", pos = new Vector2(-1f, -5.5f),  eye = 1.7f, lookAt = new Vector3(-6f, 0.5f, -17f) },
            new View { name = "Overview",   pos = new Vector2(-7f, -60f),   eye = -1f,  lookAt = new Vector3(0f, 3f, 4f) },
        };

        static VillagePerformance()
        {
            EditorApplication.playModeStateChanged += s =>
            {
                if (s != PlayModeStateChange.EnteredPlayMode) return;
                string label = SessionState.GetString(FlagLabel, "");
                if (string.IsNullOrEmpty(label)) return;
                SessionState.EraseString(FlagLabel);
                Begin(label);
            };
        }

        static void Capture(string label)
        {
            if (EditorApplication.isPlaying) { EditorUtility.DisplayDialog("Performance", "Exit Play Mode first.", "OK"); return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (SceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath);
            var stats = SceneStats.Collect(SceneManager.GetActiveScene());
            SessionState.SetString(FlagStatic, stats.ToText());
            SessionState.SetString(FlagStaticTsv, string.Join("\n", stats.Metrics().Select(m => $"{m.key}\t\t{m.value.ToString(CultureInfo.InvariantCulture)}")));
            SessionState.SetString(FlagLabel, label);
            EditorApplication.isPlaying = true;
        }

        static IEnumerator<float> routine;
        static double resumeAt;
        static string runLabel;
        static StringBuilder report;
        static List<string> tsv;
        static int savedVSync, savedTargetFps;

        static void Begin(string label)
        {
            runLabel = label;
            report = new StringBuilder();
            tsv = new List<string>();
            report.AppendLine($"Performance capture '{label}' {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Unity {Application.unityVersion}, {SystemInfo.operatingSystem}, GPU {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}), CPU {SystemInfo.processorType}");
            report.AppendLine($"Quality '{QualitySettings.names[QualitySettings.GetQualityLevel()]}', pipeline {GraphicsSettings.currentRenderPipeline?.name}, game view {Screen.width}x{Screen.height}");
            report.AppendLine("Editor play mode, no network session (menu state), V-Sync / frame cap disabled during the capture.");
            report.AppendLine("Render counters include every camera the editor draws (keep the Scene view closed or hidden for clean numbers).");
            report.AppendLine();
            report.AppendLine("---- Static scene stats (edit mode, before play) ----");
            report.Append(SessionState.GetString(FlagStatic, "(none)"));
            tsv.AddRange(SessionState.GetString(FlagStaticTsv, "").Split('\n').Where(l => l.Length > 0));
            report.AppendLine();

            savedVSync = QualitySettings.vSyncCount;
            savedTargetFps = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            routine = Steps();
            resumeAt = 0;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { Finish("play mode exited early"); return; }
            SampleFrameTiming();
            if (EditorApplication.timeSinceStartup < resumeAt) return;
            bool more;
            try { more = routine.MoveNext(); }
            catch (Exception e) { report.AppendLine("EXCEPTION: " + e); more = false; }
            if (!more) { Finish(null); return; }
            resumeAt = EditorApplication.timeSinceStartup + routine.Current;
        }

        static readonly FrameTiming[] timings = new FrameTiming[1];
        static double ftGpu, ftCpu; static int ftGpuN, ftCpuN; static bool ftOn;

        static void SampleFrameTiming()
        {
            if (!ftOn) return;
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timings) < 1) return;
            if (timings[0].gpuFrameTime > 0) { ftGpu += timings[0].gpuFrameTime; ftGpuN++; }
            if (timings[0].cpuMainThreadFrameTime > 0) { ftCpu += timings[0].cpuMainThreadFrameTime; ftCpuN++; }
        }

        static readonly (ProfilerCategory cat, string name, string key, double scale)[] Counters =
        {
            (ProfilerCategory.Render, "Batches Count", "batches", 1),
            (ProfilerCategory.Render, "SetPass Calls Count", "setPassCalls", 1),
            (ProfilerCategory.Render, "Draw Calls Count", "drawCalls", 1),
            (ProfilerCategory.Render, "Triangles Count", "triangles", 1),
            (ProfilerCategory.Render, "Shadow Casters Count", "shadowCasters", 1),
            (ProfilerCategory.Internal, "Main Thread", "mainThreadMs", 1e-6),
            (ProfilerCategory.Internal, "CPU Main Thread Frame Time", "cpuMainThreadFrameMs", 1e-6),
            (ProfilerCategory.Internal, "CPU Render Thread Frame Time", "cpuRenderThreadFrameMs", 1e-6),
            (ProfilerCategory.Internal, "GPU Frame Time", "gpuFrameMs", 1e-6),
            (ProfilerCategory.Memory, "GC Allocated In Frame", "gcAllocBytesPerFrame", 1),
        };

        static IEnumerator<float> Steps()
        {
            var cam = Camera.main != null ? Camera.main : UnityEngine.Object.FindFirstObjectByType<Camera>();
            if (cam == null) { report.AppendLine("No camera found."); yield break; }
            var terrain = Terrain.activeTerrain;
            Vector3 camPos = cam.transform.position; Quaternion camRot = cam.transform.rotation;
            float fov = cam.fieldOfView, far = cam.farClipPlane;
            report.AppendLine($"---- Runtime ({WarmupSeconds}s warm-up + {SampleSeconds}s sample per view, camera '{cam.name}' at player FOV 75) ----");
            yield return 1f;    // let the scene finish loading / compiling shaders

            foreach (var v in Views)
            {
                float ground = terrain ? terrain.SampleHeight(new Vector3(v.pos.x, 0, v.pos.y)) + terrain.GetPosition().y : 0f;
                Vector3 p = v.eye > 0f ? new Vector3(v.pos.x, ground + v.eye, v.pos.y) : new Vector3(v.pos.x, 17f, v.pos.y);
                cam.transform.position = p;
                cam.transform.LookAt(v.lookAt);
                cam.fieldOfView = v.eye > 0f ? 75f : 55f;
                cam.farClipPlane = 1000f;
                yield return WarmupSeconds;

                var recs = Counters.Select(c => ProfilerRecorder.StartNew(c.cat, c.name, 4096)).ToArray();
                ftGpu = ftCpu = 0; ftGpuN = ftCpuN = 0; ftOn = true;
                int f0 = Time.frameCount; float t0 = Time.realtimeSinceStartup;
                yield return SampleSeconds;
                ftOn = false;
                int frames = Time.frameCount - f0; float elapsed = Time.realtimeSinceStartup - t0;

                var line = new List<string>();
                void Put(string key, double val, string fmt)
                {
                    tsv.Add($"{key}\t{v.name}\t{val.ToString(CultureInfo.InvariantCulture)}");
                    line.Add($"{key}={val.ToString(fmt, CultureInfo.InvariantCulture)}");
                }
                if (frames > 0)
                {
                    Put("fps", frames / elapsed, "F1");
                    Put("frameMs", elapsed * 1000.0 / frames, "F2");
                }
                for (int i = 0; i < recs.Length; i++)
                {
                    var r = recs[i];
                    if (r.Valid && r.Count > 0)
                    {
                        double sum = 0; int n = r.Count;
                        for (int k = 0; k < n; k++) sum += r.GetSample(k).Value;
                        Put(Counters[i].key, sum / n * Counters[i].scale, Counters[i].scale < 1 ? "F2" : "F0");
                    }
                    else line.Add($"{Counters[i].key}=n/a");
                    r.Dispose();
                }
                if (ftGpuN > 0) Put("ftm.gpuMs", ftGpu / ftGpuN, "F2"); else line.Add("ftm.gpuMs=n/a");
                if (ftCpuN > 0) Put("ftm.cpuMainMs", ftCpu / ftCpuN, "F2");
                report.AppendLine($"[{v.name}] frames={frames} " + string.Join(" ", line));
            }

            foreach (var (cat, name, key) in new[] { (ProfilerCategory.Memory, "Total Used Memory", "mem.totalUsedMB"), (ProfilerCategory.Memory, "Gfx Used Memory", "mem.gfxUsedMB"), (ProfilerCategory.Memory, "Total Reserved Memory", "mem.totalReservedMB") })
            {
                using var r = ProfilerRecorder.StartNew(cat, name);
                yield return 0.2f;
                if (r.Valid) { double mb = r.LastValue / (1024.0 * 1024.0); tsv.Add($"{key}\t\t{mb.ToString(CultureInfo.InvariantCulture)}"); report.AppendLine($"{key}={mb:F0}"); }
            }

            cam.transform.SetPositionAndRotation(camPos, camRot);
            cam.fieldOfView = fov; cam.farClipPlane = far;
        }

        static void Finish(string reason)
        {
            EditorApplication.update -= Tick;
            ftOn = false;
            QualitySettings.vSyncCount = savedVSync;
            Application.targetFrameRate = savedTargetFps;
            if (reason != null) report.AppendLine("Stopped: " + reason);
            Directory.CreateDirectory(OutDir);
            File.WriteAllText(Path.Combine(OutDir, runLabel + ".txt"), report.ToString());
            File.WriteAllLines(Path.Combine(OutDir, runLabel + ".tsv"), tsv);
            string msg = $"[Performance] '{runLabel}' captured: {Path.GetFullPath(Path.Combine(OutDir, runLabel + ".txt"))}";
            if (runLabel == "After" && File.Exists(Path.Combine(OutDir, "Baseline.tsv")))
            {
                WriteComparison();
                msg += $"\nComparison: {Path.GetFullPath(Path.Combine(OutDir, "Comparison.txt"))}";
            }
            Debug.Log(msg);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }

        static Dictionary<(string, string), double> ReadTsv(string path)
        {
            var d = new Dictionary<(string, string), double>();
            foreach (var l in File.ReadAllLines(path))
            {
                var p = l.Split('\t');
                if (p.Length == 3 && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) d[(p[0], p[1])] = v;
            }
            return d;
        }

        static void WriteComparison()
        {
            var a = ReadTsv(Path.Combine(OutDir, "Baseline.tsv"));
            var b = ReadTsv(Path.Combine(OutDir, "After.tsv"));
            var sb = new StringBuilder();
            sb.AppendLine($"Baseline vs After ({DateTime.Now:yyyy-MM-dd HH:mm}). Measured values only; same machine / game view size assumed.");
            sb.AppendLine($"{"metric",-34}{"view",-12}{"baseline",14}{"after",14}{"change",10}");
            foreach (var k in a.Keys.Union(b.Keys).OrderBy(k => k.Item2).ThenBy(k => k.Item1))
            {
                bool ha = a.TryGetValue(k, out double va), hb = b.TryGetValue(k, out double vb);
                string ch = ha && hb && Math.Abs(va) > 1e-9 ? $"{(vb - va) / Math.Abs(va) * 100:+0.0;-0.0;0.0}%" : "";
                sb.AppendLine($"{k.Item1,-34}{k.Item2,-12}{(ha ? va.ToString("0.##", CultureInfo.InvariantCulture) : "-"),14}{(hb ? vb.ToString("0.##", CultureInfo.InvariantCulture) : "-"),14}{ch,10}");
            }
            File.WriteAllText(Path.Combine(OutDir, "Comparison.txt"), sb.ToString());
        }
    }
}
