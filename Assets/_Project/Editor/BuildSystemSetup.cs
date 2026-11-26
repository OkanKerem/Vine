using System.IO;
using System.Linq;
using Mirror;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Vineyard.Building;
using Vineyard.Upgrades;
using Vineyard.Winery;

namespace Vineyard.EditorTools
{
    /// <summary>
    /// Tools > Vineyard > Setup Build System (also run by Setup Winery Gameplay). Idempotent:
    ///  * Resources/Buildables/: networked prefabs for the buildable grape field, grape press and fermenter
    ///    (NetworkIdentity + station on the root, model centred with its bottom on the root), wired exactly like
    ///    the stations in the village scene.
    ///  * Preview materials (transparent white / transparent red) and the BuildCatalog asset. Existing names,
    ///    prices and descriptions in the catalog are kept.
    ///  * Player prefab: PlayerBuilder + a placeholder hammer (first-person and in-hand). Existing hammer objects are
    ///    never replaced, so a real model put under Hammer_View / Hammer_World survives re-running the setup.
    /// </summary>
    public static class BuildSystemSetup
    {
        const string Dir = "Assets/_Project/Resources/Buildables";
        const string PlayerPrefabPath = "Assets/_Project/Prefabs/NetworkPlayer.prefab";
        const string VinePath = "Assets/Grapevine.fbx";
        const string BunchPath = "Assets/GrapeBunch.fbx";
        const string PressPath = "Assets/SmallGrapePress.fbx";
        const string FermenterPath = "Assets/SmallFermentationStation.fbx";
        const string HammerPath = "Assets/BuildHammer.fbx";
        const string HammerModelName = "BuildHammer";
        const string SoilTexturePath = "Assets/_Project/Generated/Village/Textures/Soil.png";
        const string IconDir = "Assets/iloveimg-background-removed";
        const int IgnoreRaycastLayer = 2;

        [MenuItem("Tools/Vineyard/Setup Build System")]
        public static void Menu()
        {
            Debug.Log("[Build] " + SetupAll());
            AssetDatabase.SaveAssets();
        }

        public static string SetupAll()
        {
            EnsureFolder();
            AssetDatabase.DeleteAsset($"{Dir}/Buildable_GrapeField.prefab");   // old 4-vine field, replaced by one vine
            string hammer = BuildWorldHammer();
            string upgrades = BuildTeamUpgrades();
            var field = BuildGrapevine();
            var press = BuildPress();
            var ferm = BuildFermenter();
            var catalog = BuildCatalogAsset(field, press, ferm);
            string player = SetupPlayer();
            AssetDatabase.SaveAssets();
            return $"Build system: {catalog.entries.Count} buildables in {Dir} ({string.Join(", ", catalog.entries.Select(e => $"{e.displayName} {e.cost}"))}); {hammer}; {upgrades}; {player}";
        }

        static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Resources")) AssetDatabase.CreateFolder("Assets/_Project", "Resources");
            if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets/_Project/Resources", "Buildables");
        }

        // ================================================================== buildable prefabs

        /// <summary>
        /// Creates root (NetworkIdentity + station) with a "Model" child, lets <paramref name="fill"/> build the model,
        /// then moves the model so its bottom centre sits on the root (= the ground point the player aims at).
        /// </summary>
        static GameObject SavePrefab<T>(string file, string display, System.Action<Transform, T> fill) where T : WineryStation
        {
            var root = new GameObject(file);
            try
            {
                root.AddComponent<NetworkIdentity>();
                var station = root.AddComponent<T>();
                station.displayName = display;
                var model = new GameObject("Model").transform;
                model.SetParent(root.transform, false);
                fill(model, station);

                var b = WinerySetup.WorldBounds(model);
                model.position -= new Vector3(b.center.x, b.min.y, b.center.z);
                return PrefabUtility.SaveAsPrefabAsset(root, $"{Dir}/{file}.prefab");
            }
            finally { Object.DestroyImmediate(root); }
        }

        static GameObject Model(string path, Transform parent, string name)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (fbx == null) throw new System.Exception($"{path} not found");
            var go = Object.Instantiate(fbx, parent);   // keeps the model's own import rotation / scale
            go.name = name;
            go.transform.localPosition = Vector3.zero;
            return go;
        }

        /// <summary>One box collider per mesh part (same as the village builder does for the scene machines).</summary>
        static void PartColliders(GameObject go, string skip)
        {
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.name.Contains(skip) || mf.sharedMesh == null || mf.GetComponent<Collider>()) continue;
                var bc = mf.gameObject.AddComponent<BoxCollider>();
                bc.center = mf.sharedMesh.bounds.center; bc.size = mf.sharedMesh.bounds.size;
            }
        }

        /// <summary>
        /// One grapevine on a patch of tilled soil (the soil the village rows have painted into the terrain).
        /// Only the trellis has a solid collider, so vines can be planted next to each other in rows.
        /// </summary>
        static GameObject BuildGrapevine() => SavePrefab<VineyardController>("Buildable_Grapevine", "Grapevine", (model, vineyard) =>
        {
            var vineFbx = AssetDatabase.LoadAssetAtPath<GameObject>(VinePath);
            var bunchFbx = AssetDatabase.LoadAssetAtPath<GameObject>(BunchPath);
            var go = Model(VinePath, model, "Grapevine");
            go.transform.localPosition = new Vector3(0f, -0.03f, 0f);
            go.transform.localRotation = vineFbx.transform.rotation;

            // Trellis collider: the row behaves like a wall (measured in the model's own orientation, as in the village).
            var support = go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Grapevine_Support");
            var box = go.AddComponent<BoxCollider>();
            var sb = WinerySetup.WorldBounds(support != null ? support : go.transform);
            box.center = Quaternion.Inverse(go.transform.rotation) * (sb.center - go.transform.position);
            box.size = new Vector3(sb.size.x, sb.size.y + 0.1f, 0.35f);

            var rnd = new System.Random(7);
            if (bunchFbx != null)
                foreach (var t in go.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("GrapeSpawn")).ToList())
                {
                    var g = Object.Instantiate(bunchFbx, t);
                    g.name = "GrapeBunch";
                    g.transform.position = t.position;
                    g.transform.rotation = Quaternion.Euler(0f, (float)rnd.NextDouble() * 360f, 0f);
                }

            vineyard.vines = new[] { WinerySetup.WireVine(go.transform, vineyard, 0, out var it) };
            vineyard.interactables = new[] { it };

            // Soil: 1.7 m wide like a village row, running 0.7 m past both ends of the vine.
            var vb = WinerySetup.WorldBounds(go.transform);
            bool alongX = vb.size.x >= vb.size.z;
            float length = (alongX ? vb.size.x : vb.size.z) + 1.4f, width = 1.7f;
            var soil = SoilPatchObject(model, alongX ? length : width, alongX ? width : length);
            soil.transform.position = new Vector3(vb.center.x, model.position.y, vb.center.z);
        });

        /// <summary>Flat grid (conformed to the ground at runtime by <see cref="SoilPatch"/>) with the baked soil texture.</summary>
        static GameObject SoilPatchObject(Transform parent, float sizeX, float sizeZ)
        {
            var go = new GameObject("SoilPatch");
            go.transform.SetParent(parent, false);
            go.layer = IgnoreRaycastLayer;   // never in the way of aiming
            go.AddComponent<MeshFilter>().sharedMesh = SoilMesh(sizeX, sizeZ);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = SoilMaterial(sizeX, sizeZ);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            go.AddComponent<SoilPatch>();
            return go;
        }

        static Mesh SoilMesh(float sx, float sz)
        {
            string path = $"{Dir}/SoilPatch_Mesh.asset";
            int nx = Mathf.Max(2, Mathf.CeilToInt(sx / 0.25f)), nz = Mathf.Max(2, Mathf.CeilToInt(sz / 0.25f));
            var verts = new Vector3[(nx + 1) * (nz + 1)];
            var uvs = new Vector2[verts.Length];
            for (int j = 0; j <= nz; j++)
                for (int i = 0; i <= nx; i++)
                {
                    float u = i / (float)nx, v = j / (float)nz;
                    verts[j * (nx + 1) + i] = new Vector3((u - 0.5f) * sx, 0f, (v - 0.5f) * sz);
                    uvs[j * (nx + 1) + i] = new Vector2(u, v);
                }
            var tris = new int[nx * nz * 6];
            int k = 0;
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    int a = j * (nx + 1) + i, b = a + 1, c = a + nx + 1, d = c + 1;
                    tris[k++] = a; tris[k++] = c; tris[k++] = b;
                    tris[k++] = b; tris[k++] = c; tris[k++] = d;
                }
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool create = mesh == null;
            if (create) mesh = new Mesh();
            mesh.Clear();
            mesh.name = "SoilPatch";
            mesh.vertices = verts; mesh.uv = uvs; mesh.triangles = tris;
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            if (create) AssetDatabase.CreateAsset(mesh, path);
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        /// <summary>
        /// Bakes the terrain's Soil layer (4 m tile, 512 px) at the same scale onto the patch, with a ragged,
        /// rounded alpha edge so it blends into the grass like the painted rows. Cut-out, so no sorting issues.
        /// </summary>
        static Material SoilMaterial(float sx, float sz)
        {
            const float pxPerMetre = 128f;
            int tw = Mathf.CeilToInt(sx * pxPerMetre), th = Mathf.CeilToInt(sz * pxPerMetre);
            Texture2D soil = null;
            if (File.Exists(SoilTexturePath)) { soil = new Texture2D(2, 2); soil.LoadImage(File.ReadAllBytes(SoilTexturePath)); }

            var px = new Color[tw * th];
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    Color c = soil != null ? soil.GetPixel(x % soil.width, y % soil.height)
                                           : Color.Lerp(new Color(0.33f, 0.22f, 0.14f), new Color(0.45f, 0.31f, 0.20f), Mathf.PerlinNoise(x * 0.05f, y * 0.05f));
                    // Signed distance (m) to a rounded rectangle, positive inside.
                    float mx = x / pxPerMetre, my = y / pxPerMetre, r = 0.6f;
                    float qx = Mathf.Abs(mx - sx / 2f) - (sx / 2f - r), qy = Mathf.Abs(my - sz / 2f) - (sz / 2f - r);
                    float inside = -(new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - r);
                    float noise = Mathf.PerlinNoise(x * 0.045f + 13.1f, y * 0.045f + 7.7f) * 0.7f + Mathf.PerlinNoise(x * 0.17f + 3.3f, y * 0.17f + 1.9f) * 0.3f;
                    c.a = Mathf.Clamp01(inside / 0.35f + (noise - 0.5f) * 1.1f);
                    px[y * tw + x] = c;
                }
            if (soil != null) Object.DestroyImmediate(soil);

            string texPath = $"{Dir}/SoilPatch.png";
            var tex = new Texture2D(tw, th, TextureFormat.RGBA32, true);
            tex.SetPixels(px);
            File.WriteAllBytes(texPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(texPath);
            if (AssetImporter.GetAtPath(texPath) is TextureImporter ti)
            {
                ti.alphaIsTransparency = true;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.mipMapsPreserveCoverage = true;   // keeps the cut-out edge at a distance
                ti.alphaTestReferenceValue = 0.5f;
                ti.SaveAndReimport();
            }

            string matPath = $"{Dir}/SoilPatch.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, matPath); }
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0.04f);
            m.SetFloat("_AlphaClip", 1f);
            m.SetFloat("_Cutoff", 0.5f);
            m.EnableKeyword("_ALPHATEST_ON");
            m.SetOverrideTag("RenderType", "TransparentCutout");
            m.renderQueue = (int)RenderQueue.AlphaTest;
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        static GameObject BuildPress() => SavePrefab<GrapePressController>("Buildable_GrapePress", "Grape Press", (model, pc) =>
        {
            var go = Model(PressPath, model, "SmallGrapePress");
            PartColliders(go, "Press_Grapes");
            WinerySetup.WirePress(go.transform, pc);
        });

        static GameObject BuildFermenter() => SavePrefab<FermentationMachineController>("Buildable_Fermenter", "Fermentation Machine", (model, fc) =>
        {
            var go = Model(FermenterPath, model, "SmallFermentationStation");
            PartColliders(go, "Fermenter_Liquid");
            WinerySetup.WireFermenter(go.transform, fc);
        });

        // ================================================================== team upgrades

        /// <summary>
        /// Resources/TeamUpgrades.prefab (spawned by the host at start). Created only once: prices and values
        /// tuned on the prefab are never overwritten.
        /// </summary>
        static string BuildTeamUpgrades()
        {
            string path = $"Assets/_Project/Resources/{TeamUpgrades.ResourcePath}.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return "team upgrades prefab kept";
            var root = new GameObject(TeamUpgrades.ResourcePath);
            try
            {
                root.AddComponent<NetworkIdentity>();
                root.AddComponent<TeamUpgrades>();
                return PrefabUtility.SaveAsPrefabAsset(root, path) ? "team upgrades prefab created" : "team upgrades prefab: SAVE FAILED";
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ================================================================== hammer lying on the ground

        /// <summary>Resources/WorldHammer.prefab: the hammer lying flat, clickable (pick up / swap with the bucket).</summary>
        static string BuildWorldHammer()
        {
            var root = new GameObject(WorldHammer.ResourcePath);
            try
            {
                root.AddComponent<NetworkIdentity>();
                var wh = root.AddComponent<WorldHammer>();
                wh.displayName = "Hammer";
                var visual = new GameObject("Visual").transform;
                visual.SetParent(root.transform, false);

                var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(HammerPath);
                GameObject model;
                if (fbx != null)
                {
                    model = (GameObject)PrefabUtility.InstantiatePrefab(fbx, visual);
                    model.name = HammerModelName;
                }
                else model = PlaceholderHammer(visual, "Placeholder");

                // Lying flat: the handle (up axis) turned horizontal, the thin side of the head down.
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.Euler(90f, 0f, 0f) * model.transform.localRotation;
                var b = WinerySetup.WorldBounds(visual);
                visual.position -= new Vector3(b.center.x, b.min.y, b.center.z);
                b = WinerySetup.WorldBounds(visual);

                // A box a little bigger than the model, so the thin hammer is easy to aim at.
                var box = root.AddComponent<BoxCollider>();
                box.center = root.transform.InverseTransformPoint(b.center) + Vector3.up * 0.03f;
                box.size = new Vector3(b.size.x + 0.08f, Mathf.Max(b.size.y, 0.12f) + 0.06f, b.size.z + 0.08f);

                var it = root.AddComponent<WineryInteractable>();
                it.station = wh; it.part = InteractionPart.HammerPickup; it.index = 0;
                it.highlightRenderers = visual.GetComponentsInChildren<Renderer>(true);
                it.colliders = new Collider[] { box };
                wh.interactables = new[] { it };

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"Assets/_Project/Resources/{WorldHammer.ResourcePath}.prefab");
                return prefab ? $"ground hammer prefab ({(fbx ? "BuildHammer.fbx" : "placeholder")})" : "ground hammer prefab: SAVE FAILED";
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ================================================================== catalog + preview materials

        static BuildCatalog BuildCatalogAsset(GameObject field, GameObject press, GameObject ferm)
        {
            string path = $"{Dir}/BuildCatalog.asset";
            var cat = AssetDatabase.LoadAssetAtPath<BuildCatalog>(path);
            if (cat == null) { cat = ScriptableObject.CreateInstance<BuildCatalog>(); AssetDatabase.CreateAsset(cat, path); }

            void Ensure(string id, string name, string desc, float cost, GameObject prefab)
            {
                var e = cat.entries.FirstOrDefault(x => x != null && x.id == id);
                if (e == null)
                {
                    e = new BuildCatalog.Entry { id = id, displayName = name, description = desc, cost = cost };
                    cat.entries.Add(e);
                }
                e.prefab = prefab;   // names / prices / descriptions edited in the asset are kept
            }
            Ensure("grape_field", "Grapevine", "One grapevine on tilled soil", 1f, field);
            Ensure("grape_press", "Grape Press", "Grapes → grape juice", 3f, press);
            Ensure("fermenter", "Fermenter", "Grape juice → wine", 4f, ferm);

            var vine = cat.entries.First(x => x.id == "grape_field");
            if (vine.displayName == "Grape Field") { vine.displayName = "Grapevine"; vine.description = "One grapevine on tilled soil"; vine.cost = 1f; }
            Icon(cat, "grape_field", "Grapevine_Preview.png");
            Icon(cat, "grape_press", "SmallGrapePress_Preview.png");
            Icon(cat, "fermenter", "SmallFermentationStation_Preview.png");

            cat.validMaterial = GhostMat("BuildPreview_Valid", new Color(1f, 1f, 1f, 0.32f), new Color(0.35f, 0.35f, 0.35f));
            cat.invalidMaterial = GhostMat("BuildPreview_Invalid", new Color(1f, 0.12f, 0.08f, 0.42f), new Color(0.55f, 0.04f, 0.02f));
            EditorUtility.SetDirty(cat);
            return cat;
        }

        static void Icon(BuildCatalog cat, string id, string file)
        {
            string path = $"{IconDir}/{file}";
            if (AssetImporter.GetAtPath(path) is TextureImporter ti && (!ti.alphaIsTransparency || ti.maxTextureSize > 512))
            {
                ti.alphaIsTransparency = true;   // no dark fringe around the cut-out
                ti.maxTextureSize = 512;
                ti.SaveAndReimport();
            }
            var e = cat.entries.FirstOrDefault(x => x.id == id);
            if (e != null) e.icon = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>URP Lit, transparent, slightly emissive so the preview stays readable in shade.</summary>
        static Material GhostMat(string name, Color c, Color emission)
        {
            string path = $"{Dir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", 0.2f);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetColor("_EmissionColor", emission);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        // ================================================================== player: PlayerBuilder + placeholder hammer

        static string SetupPlayer()
        {
            bool created = false, real;
            using (var scope = new PrefabUtility.EditPrefabContentsScope(PlayerPrefabPath))
            {
                var root = scope.prefabContentsRoot;
                var np = root.GetComponent<Vineyard.Player.NetworkPlayer>();
                var builder = root.GetComponent<PlayerBuilder>() ?? root.AddComponent<PlayerBuilder>();
                Transform camT = np && np.playerCamera ? np.playerCamera.transform : root.transform;

                Transform Deep(string n) => root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);

                var view = Deep("Hammer_View");
                if (view == null)
                {
                    view = PlaceholderHammer(camT, "Hammer_View").transform;
                    view.localPosition = new Vector3(0.32f, -0.4f, 0.55f);
                    view.localRotation = Quaternion.Euler(-20f, -25f, -12f);
                    foreach (var r in view.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = ShadowCastingMode.Off;
                    created = true;
                }
                var world = Deep("Hammer_World");
                if (world == null)
                {
                    world = PlaceholderHammer(root.transform, "Hammer_World").transform;
                    world.localPosition = new Vector3(0.45f, 0.85f, 0.28f);
                    world.localRotation = Quaternion.Euler(25f, -10f, 0f);
                    created = true;
                }
                real = UseRealHammer(view, false) | UseRealHammer(world, true);
                view.gameObject.SetActive(false);
                world.gameObject.SetActive(false);
                builder.hammerView = view.gameObject;
                builder.hammerWorld = world.gameObject;
                EditorUtility.SetDirty(builder);
            }
            return (created ? "player: PlayerBuilder + new hammer holders" : "player: PlayerBuilder (existing hammer holders kept)")
                 + (real ? $", {HammerPath} placed" : "");
        }

        /// <summary>
        /// Swaps the "Placeholder" child of a hammer holder for Assets/BuildHammer.fbx (once; a holder that already
        /// has the model is left alone). The grip is the model's origin; the model is turned so its striking face
        /// (Hammer_ImpactPoint) points forward (+Z), which is the direction of the swing animation.
        /// </summary>
        static bool UseRealHammer(Transform holder, bool castShadows)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(HammerPath);
            if (fbx == null || holder.Find(HammerModelName) != null) return false;
            var placeholder = holder.Find("Placeholder");
            if (placeholder) Object.DestroyImmediate(placeholder.gameObject);

            var go = (GameObject)PrefabUtility.InstantiatePrefab(fbx, holder);   // stays linked: re-imports update it
            go.name = HammerModelName;
            go.transform.localPosition = Vector3.zero;
            var impact = go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Hammer_ImpactPoint");
            if (impact)
            {
                Vector3 d = holder.InverseTransformPoint(impact.position) - holder.InverseTransformPoint(go.transform.position);
                d.y = 0f;
                if (d.sqrMagnitude > 1e-6f) go.transform.localRotation = Quaternion.Euler(0f, Vector3.SignedAngle(d, Vector3.forward, Vector3.up), 0f) * go.transform.localRotation;
            }
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                r.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return true;
        }

        /// <summary>
        /// Placeholder: wooden handle + iron head, grip at the object's origin. To use the real model, delete the
        /// "Placeholder" child and put the model under Hammer_View / Hammer_World instead.
        /// </summary>
        static GameObject PlaceholderHammer(Transform parent, string name)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            var ph = new GameObject("Placeholder").transform;
            ph.SetParent(root.transform, false);

            var wood = WinerySetup.Mat("Build_HammerWood", new Color(0.45f, 0.28f, 0.14f), 0.15f);
            var iron = WinerySetup.Mat("Build_HammerIron", new Color(0.32f, 0.33f, 0.36f), 0.55f);

            var handle = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            handle.name = "Handle";
            Object.DestroyImmediate(handle.GetComponent<Collider>());
            handle.transform.SetParent(ph, false);
            handle.transform.localScale = new Vector3(0.035f, 0.17f, 0.035f);   // 0.34 m long
            handle.transform.localPosition = new Vector3(0f, 0.12f, 0f);
            handle.GetComponent<MeshRenderer>().sharedMaterial = wood;

            var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
            head.name = "Head";
            Object.DestroyImmediate(head.GetComponent<Collider>());
            head.transform.SetParent(ph, false);
            head.transform.localScale = new Vector3(0.075f, 0.075f, 0.17f);
            head.transform.localPosition = new Vector3(0f, 0.29f, 0.02f);
            head.GetComponent<MeshRenderer>().sharedMaterial = iron;
            return root;
        }
    }
}
