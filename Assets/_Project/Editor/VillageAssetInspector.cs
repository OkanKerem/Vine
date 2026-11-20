using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vineyard.EditorTools
{
    /// <summary>Tools > Vineyard > Inspect Village Assets: writes Logs/VillageAssetInspection.txt.</summary>
    public static class VillageAssetInspector
    {
        [MenuItem("Tools/Vineyard/Inspect Village Assets")]
        public static string Inspect()
        {
            var sb = new StringBuilder();
            var active = EditorSceneManager.GetActiveScene();
            sb.AppendLine($"Open scene: {active.path} dirty={active.isDirty}");
            sb.AppendLine($"Pipeline: {GraphicsSettings.currentRenderPipeline?.name} ({AssetDatabase.GetAssetPath(GraphicsSettings.currentRenderPipeline)})");
            sb.AppendLine($"Quality: {QualitySettings.names[QualitySettings.GetQualityLevel()]} shadowDist={QualitySettings.shadowDistance}");
            sb.AppendLine($"Build scenes: {string.Join(", ", EditorBuildSettings.scenes.Select(s => s.path))}");

            var paths = AssetDatabase.FindAssets("t:GameObject", new[] { "Assets/OccaSoftware", "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath).Distinct()
                .Where(p => !p.Contains("/Mirror/") && !p.Contains("/TutorialInfo/") && !p.EndsWith(".fbx") || p.StartsWith("Assets/") && p.Count(c => c == '/') == 1)
                .OrderBy(p => p).ToList();

            foreach (var path in paths)
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null) continue;
                var go = (GameObject)Object.Instantiate(asset);
                go.transform.position = Vector3.zero;
                var rs = go.GetComponentsInChildren<Renderer>();
                Bounds b = rs.Length > 0 ? rs[0].bounds : new Bounds();
                foreach (var r in rs) b.Encapsulate(r.bounds);
                sb.AppendLine();
                sb.AppendLine($"== {path}");
                sb.AppendLine($"   rootRot={asset.transform.rotation.eulerAngles} scale={asset.transform.localScale} bounds center={b.center:F2} size={b.size:F2} min={b.min:F2}");
                sb.AppendLine($"   colliders={go.GetComponentsInChildren<Collider>().Length} ({string.Join(",", go.GetComponentsInChildren<Collider>().Select(c => c.GetType().Name).Distinct())}) LODGroup={go.GetComponentInChildren<LODGroup>() != null}");
                int tris = go.GetComponentsInChildren<MeshFilter>().Where(m => m.sharedMesh).Sum(m => m.sharedMesh.triangles.Length / 3);
                sb.AppendLine($"   renderers={rs.Length} tris={tris}");
                int n = 0;
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                {
                    if (n++ > 60) { sb.AppendLine("   ..."); break; }
                    int depth = 0; for (var p = t; p != go.transform && p != null; p = p.parent) depth++;
                    var r = t.GetComponent<Renderer>();
                    string mats = r ? " mats=[" + string.Join(", ", r.sharedMaterials.Select(MatInfo)) + "]" : "";
                    string bb = r ? $" b={r.bounds.center:F2}/{r.bounds.size:F2}" : "";
                    sb.AppendLine($"   {new string(' ', depth * 2)}{t.name}{bb}{mats}");
                }
                Object.DestroyImmediate(go);
            }
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/VillageAssetInspection.txt", sb.ToString());
            return $"{paths.Count} assets";
        }

        static string MatInfo(Material m)
        {
            if (m == null) return "NULL";
            var s = m.shader;
            string sh = s == null ? "NO SHADER" : s.name;
            bool broken = s == null || s.name == "Hidden/InternalErrorShader" || !s.isSupported;
            var tex = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : (m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null);
            string col = m.HasProperty("_BaseColor") ? ColorUtility.ToHtmlStringRGB(m.GetColor("_BaseColor")) : "";
            return $"{m.name}<{sh}{(broken ? " BROKEN" : "")}{(tex ? " tex=" + tex.name : "")} #{col} inst={m.enableInstancing}>";
        }
    }
}
