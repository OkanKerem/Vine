using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mirror;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Vineyard.Player;
using Vineyard.Winery;

namespace Vineyard.EditorTools
{
    /// <summary>
    /// Tools > Vineyard > Setup Winery Gameplay
    ///
    /// Wires the production loop into existing assets (idempotent, safe to re-run):
    ///  * Player prefab: PlayerBucket, PlayerInteractor, WineryHUD, a first-person bucket under the camera and a
    ///    third-person bucket in the character's hand (bucket model = the fermenter's wooden bucket mesh).
    ///  * Village scene: a "Gameplay" group with one networked station object per production step
    ///    (vineyard, press, fermenter, giant bottle). The visual models stay untouched apart from
    ///    WineryInteractable markers, a few trigger volumes and particle emitters.
    /// Also called automatically by the Village and Multiplayer builders.
    /// </summary>
    public static class WinerySetup
    {
        const string PlayerPrefabPath = "Assets/_Project/Prefabs/NetworkPlayer.prefab";
        const string FermenterPath = "Assets/SmallFermentationStation.fbx";
        const string BunchPath = "Assets/GrapeBunch.fbx";
        const string MatDir = "Assets/_Project/Materials/Winery";
        const int IgnoreRaycastLayer = 2;

        [MenuItem("Tools/Vineyard/Setup Winery Gameplay")]
        public static void SetupMenu()
        {
            string p = SetupPlayerPrefab();
            string s = SetupScene();
            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"[Winery] {p}\n{s}");
        }

        public static string SetupAll()
        {
            string p = SetupPlayerPrefab();
            string s = SetupScene();
            EditorSceneManager.SaveOpenScenes();
            return p + " | " + s;
        }

        // ================================================================== materials

        static Material Mat(string name, Color c, float smooth, bool transparent = false)
        {
            if (!AssetDatabase.IsValidFolder(MatDir)) AssetDatabase.CreateFolder("Assets/_Project/Materials", "Winery");
            string path = $"{MatDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            m.enableInstancing = true;
            if (transparent)
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)RenderQueue.Transparent;
            }
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material LiquidMat => Mat("Winery_Liquid", Color.white, 0.85f);
        static Material JuiceMat => Mat("Winery_GrapeJuice", new Color(0.52f, 0.16f, 0.55f), 0.85f);
        static Material WineDropMat => Mat("Winery_WineDrop", new Color(0.50f, 0.03f, 0.10f), 0.8f);
        static Material GrapeBedMat => Mat("Winery_GrapeBed", new Color(0.30f, 0.10f, 0.40f), 0.35f);
        static Material BubbleMat => Mat("Winery_Bubble", new Color(0.85f, 0.6f, 0.9f, 0.55f), 0.9f, true);

        // ================================================================== player prefab

        public static string SetupPlayerPrefab()
        {
            using (var scope = new PrefabUtility.EditPrefabContentsScope(PlayerPrefabPath))
            {
                var root = scope.prefabContentsRoot;
                var np = root.GetComponent<NetworkPlayer>();
                var bucket = root.GetComponent<PlayerBucket>() ?? root.AddComponent<PlayerBucket>();
                if (!root.GetComponent<PlayerInteractor>()) root.AddComponent<PlayerInteractor>();
                if (!root.GetComponent<WineryHUD>()) root.AddComponent<WineryHUD>();

                foreach (var old in root.GetComponentsInChildren<BucketVisual>(true).ToList()) Object.DestroyImmediate(old.gameObject);

                // Third-person bucket in the right hand (seen by other players).
                var world = BuildBucket(root.transform, "Bucket_World", bucket, false);
                world.transform.localPosition = new Vector3(0.5f, 0.62f, 0.22f);
                world.transform.localRotation = Quaternion.Euler(0f, -15f, 0f);
                world.transform.localScale = Vector3.one * 1.3f;

                // First-person bucket under the camera (enabled for the local player only).
                Transform camT = np && np.playerCamera ? np.playerCamera.transform : root.transform;
                var view = BuildBucket(camT, "Bucket_View", bucket, true);
                view.transform.localPosition = new Vector3(0.3f, -0.42f, 0.62f);
                view.transform.localRotation = Quaternion.Euler(-18f, -12f, 0f);
                view.transform.localScale = Vector3.one * 1.05f;
                foreach (var r in view.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = ShadowCastingMode.Off;
                view.SetActive(false);

                bucket.viewBucket = view;
                bucket.worldBucketRenderers = world.GetComponentsInChildren<Renderer>(true);
                EditorUtility.SetDirty(bucket);
            }
            return "Player prefab: PlayerBucket + PlayerInteractor + WineryHUD + first/third-person buckets.";
        }

        static GameObject BuildBucket(Transform parent, string name, PlayerBucket bucket, bool view)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);

            // Bucket model: the wooden bucket mesh from the fermentation station model.
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FermenterPath);
            var src = fbx.GetComponentsInChildren<Transform>(true).First(t => t.name == "Fermenter_OutputBucket");
            var model = CopyMesh(src, fbx.transform, root.transform, "Model", out Bounds b);
            model.transform.localPosition = -new Vector3(b.center.x, b.min.y, b.center.z);
            float height = b.size.y, radius = Mathf.Min(b.size.x, b.size.z) * 0.5f;
            float floorY = height * 0.14f, rimY = height * 0.9f, inner = radius * 0.8f;

            // Liquid (juice / wine): a cylinder whose height is driven by BucketVisual.
            var liquid = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.DestroyImmediate(liquid.GetComponent<Collider>());
            liquid.name = "Liquid";
            liquid.transform.SetParent(root.transform, false);
            liquid.transform.localScale = new Vector3(inner * 2f, 0.01f, inner * 2f);
            liquid.transform.localPosition = new Vector3(0f, floorY, 0f);
            var lr = liquid.GetComponent<MeshRenderer>();
            lr.sharedMaterial = LiquidMat;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            liquid.SetActive(false);

            // Grapes: a purple bed plus bunches (the GrapeBunch mesh) that appear as the amount grows.
            var bed = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(bed.GetComponent<Collider>());
            bed.name = "GrapeBed";
            bed.transform.SetParent(root.transform, false);
            bed.transform.localScale = new Vector3(inner * 1.9f, height * 0.18f, inner * 1.9f);
            bed.transform.localPosition = new Vector3(0f, floorY, 0f);
            bed.GetComponent<MeshRenderer>().sharedMaterial = GrapeBedMat;
            bed.SetActive(false);

            var bunchFbx = AssetDatabase.LoadAssetAtPath<GameObject>(BunchPath);
            var bunchSrc = bunchFbx.GetComponentsInChildren<Transform>(true).First(t => t.GetComponent<MeshFilter>());
            var bunches = new List<Transform>();
            var rnd = new System.Random(3);
            int count = 6;
            float s = inner * 1.7f / 0.5f;                     // a lying bunch spans most of the bucket
            for (int i = 0; i < count; i++)
            {
                var holder = new GameObject($"Bunch_{i + 1}").transform;
                holder.SetParent(root.transform, false);
                CopyMesh(bunchSrc, bunchFbx.transform, holder, "Mesh", out _);
                float yaw = i * 67f + (float)rnd.NextDouble() * 30f;
                var rot = Quaternion.Euler(78f + (float)rnd.NextDouble() * 20f, yaw, 0f);
                float y = Mathf.Lerp(floorY + height * 0.12f, rimY - height * 0.02f, i / (float)(count - 1));
                Vector3 c = new Vector3(Mathf.Cos(i * 2.4f) * inner * 0.25f, y, Mathf.Sin(i * 2.4f) * inner * 0.25f);
                holder.localRotation = rot;
                holder.localScale = Vector3.one * s;
                holder.localPosition = c - rot * new Vector3(0f, -0.25f * s, 0f);  // mesh pivot is at the stem
                holder.gameObject.SetActive(false);
                bunches.Add(holder);
            }

            var vis = root.AddComponent<BucketVisual>();
            vis.bucket = bucket;
            vis.liquid = liquid.transform;
            vis.liquidRenderer = lr;
            vis.grapeBed = bed.transform;
            vis.grapeBunches = bunches.ToArray();
            vis.floorY = floorY;
            vis.rimY = rimY;
            vis.viewModel = view;
            return root;
        }

        /// <summary>Copies one mesh part of a model (keeping its rotation / scale relative to the model root).</summary>
        static GameObject CopyMesh(Transform src, Transform srcRoot, Transform parent, string name, out Bounds localBounds)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Inverse(srcRoot.rotation) * src.rotation;
            go.transform.localScale = src.lossyScale;
            var mf = src.GetComponent<MeshFilter>();
            go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = src.GetComponent<MeshRenderer>().sharedMaterials;
            // Bounds of the mesh in the parent's space.
            var mb = mf.sharedMesh.bounds;
            var min = Vector3.one * float.MaxValue; var max = -min;
            for (int i = 0; i < 8; i++)
            {
                var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = go.transform.localRotation * Vector3.Scale(corner, go.transform.localScale);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
            localBounds = new Bounds((min + max) / 2f, max - min);
            return go;
        }

        // ================================================================== scene

        public static string SetupScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            var all = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true)).ToList();
            Transform Find(string n) => all.FirstOrDefault(t => t && t.name == n);

            var village = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Village");
            if (village == null) return "Village root not found — open Village.unity first.";

            // Remove a previous setup.
            var oldGameplay = village.transform.Find("Gameplay");
            if (oldGameplay) Object.DestroyImmediate(oldGameplay.gameObject);
            foreach (var t in all.ToList())
            {
                if (!t) continue;
                if (t.name.StartsWith("Winery_")) { Object.DestroyImmediate(t.gameObject); continue; }
                foreach (var c in t.GetComponents<WineryInteractable>()) Object.DestroyImmediate(c);
                foreach (var c in t.GetComponents<GrapevineController>()) Object.DestroyImmediate(c);
            }
            all = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true)).ToList();

            var gameplay = new GameObject("Gameplay").transform;
            gameplay.SetParent(village.transform, false);
            var log = new List<string>();

            // ---------------- vineyard
            var vinesT = all.Where(t => t && t.name.StartsWith("Grapevine_R") && t.parent && t.parent.name.StartsWith("Row_"))
                            .OrderBy(t => t.name).ToList();
            var vineyard = Station<VineyardController>(gameplay, "Station_Vineyard", "Vineyard");
            var vines = new List<GrapevineController>();
            var vineInteractables = new List<WineryInteractable>();
            for (int i = 0; i < vinesT.Count; i++)
            {
                var v = vinesT[i];
                var gc = v.gameObject.AddComponent<GrapevineController>();
                gc.bunches = v.GetComponentsInChildren<Transform>(true).Where(t => t.name == "GrapeBunch").ToArray();
                gc.foliage = v.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Grapevine_Mesh");
                vines.Add(gc);

                // Generous trigger so aiming at leaves or bunches works (players still collide with the trellis box).
                var b = WorldBounds(v);
                var vol = new GameObject("Winery_HarvestVolume");
                vol.transform.SetParent(v, false);
                var box = vol.AddComponent<BoxCollider>();
                box.isTrigger = true;
                Vector3 localCenter = v.InverseTransformPoint(b.center);
                Vector3 localSize = Abs(Quaternion.Inverse(v.rotation) * b.size);
                localSize.z = Mathf.Max(localSize.z, 0.9f);
                localSize.x = Mathf.Max(localSize.x, 0.9f);
                box.center = localCenter; box.size = localSize;
                vineInteractables.Add(Interactable(v.gameObject, vineyard, InteractionPart.Harvest, i, v.GetComponentsInChildren<Renderer>(true)));
            }
            vineyard.vines = vines.ToArray();
            vineyard.interactables = vineInteractables.ToArray();
            log.Add($"vineyard: {vines.Count} vines, {vines.Sum(x => x.bunches.Length)} bunches");

            // ---------------- grape press
            var press = Find("SmallGrapePress");
            if (press)
            {
                var pc = Station<GrapePressController>(gameplay, "Station_GrapePress", "Grape Press");
                Transform P(string n) => press.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);
                pc.handle = P("Press_Handle"); pc.screw = P("Press_Screw"); pc.plate = P("Press_Plate"); pc.grapes = P("Press_Grapes");
                var list = new List<WineryInteractable>();
                list.Add(Interactable(P("Press_Container").gameObject, pc, InteractionPart.PressInput, 0, R(P("Press_Container"), P("Press_Grapes"))));
                // A grip volume around the handle / screw top makes the thin handle easy to aim at.
                var grip = new GameObject("Winery_HandleGrip");
                grip.transform.SetParent(pc.handle, false);
                var hb = WorldBounds(pc.handle);
                grip.transform.position = hb.center;
                var gbox = grip.AddComponent<BoxCollider>();
                gbox.isTrigger = true;
                gbox.size = Abs(Quaternion.Inverse(grip.transform.rotation) * (hb.size + new Vector3(0.15f, 0.3f, 0.15f)));
                list.Add(Interactable(pc.handle.gameObject, pc, InteractionPart.PressHandle, 0, R(pc.handle, pc.screw)));
                list.Add(Interactable(pc.screw.gameObject, pc, InteractionPart.PressHandle, 0, R(pc.handle, pc.screw)));
                list.Add(Interactable(pc.plate.gameObject, pc, InteractionPart.PressHandle, 0, R(pc.handle, pc.screw, pc.plate)));
                list.Add(Interactable(P("Press_Bucket").gameObject, pc, InteractionPart.PressOutput, 0, R(P("Press_Bucket"), P("Press_OutputSpout"))));
                list.Add(Interactable(P("Press_OutputSpout").gameObject, pc, InteractionPart.PressOutput, 0, R(P("Press_Bucket"), P("Press_OutputSpout"))));
                foreach (var c in P("Press_Frame").GetComponents<Collider>()) c.gameObject.layer = IgnoreRaycastLayer;
                pc.interactables = list.ToArray();

                // Juice collecting in the output bucket.
                var bb = WorldBounds(P("Press_Bucket"));
                var jp = new GameObject("Winery_JuiceLevel").transform;
                jp.SetParent(press, false);
                jp.position = new Vector3(bb.center.x, bb.min.y + 0.03f, bb.center.z);
                jp.localScale = new Vector3(bb.size.x * 0.5f, 0.001f, bb.size.z * 0.5f);
                var cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Object.DestroyImmediate(cyl.GetComponent<Collider>());
                cyl.name = "Liquid";
                cyl.transform.SetParent(jp, false);
                cyl.transform.localPosition = new Vector3(0f, 1f, 0f);
                cyl.GetComponent<MeshRenderer>().sharedMaterial = JuiceMat;
                pc.juiceLiquid = jp; pc.juiceRenderer = cyl.GetComponent<MeshRenderer>();
                jp.gameObject.SetActive(false);
                // Juice dripping from the spout while pressing.
                var sp = WorldBounds(P("Press_OutputSpout"));
                Vector3 tip = sp.center + (bb.center - sp.center).normalized * 0.0f;
                pc.juiceStream = Stream(press, "Winery_JuiceStream", new Vector3(sp.center.x, sp.min.y, sp.center.z) + (new Vector3(bb.center.x, 0, bb.center.z) - new Vector3(sp.center.x, 0, sp.center.z)) * 0.6f,
                                        Vector3.down, JuiceMat, 40f, 0.3f, 0.6f, 0.35f, 0.035f);
                log.Add("press: input/handle/output wired, juice level + stream");
            }
            else log.Add("press: SmallGrapePress NOT FOUND");

            // ---------------- fermentation machine
            var ferm = Find("SmallFermentationStation");
            if (ferm)
            {
                var fc = Station<FermentationMachineController>(gameplay, "Station_Fermenter", "Fermentation Machine");
                Transform F(string n) => ferm.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);
                fc.lid = F("Fermenter_InputLid"); fc.needle = F("Fermenter_ProgressNeedle"); fc.dial = F("Fermenter_ProgressDial");
                fc.liquid = F("Fermenter_Liquid"); fc.liquidRenderer = fc.liquid ? fc.liquid.GetComponent<Renderer>() : null;
                var inputHighlight = R(F("Fermenter_InputLid"), F("Fermenter_InputOpening"), F("Fermenter_MainTank"));
                var outputHighlight = R(F("Fermenter_OutputTap"), F("Fermenter_OutputHandle"), F("Fermenter_OutputBucket"));
                var list = new List<WineryInteractable>();
                foreach (var n in new[] { "Fermenter_InputLid", "Fermenter_InputOpening", "Fermenter_MainTank", "Fermenter_InspectionGlass", "Fermenter_ProgressDial", "Fermenter_ProgressNeedle" })
                    if (F(n)) list.Add(Interactable(F(n).gameObject, fc, InteractionPart.FermenterInput, 0, inputHighlight));
                foreach (var n in new[] { "Fermenter_OutputTap", "Fermenter_OutputHandle", "Fermenter_OutputBucket" })
                    if (F(n)) list.Add(Interactable(F(n).gameObject, fc, InteractionPart.FermenterOutput, 0, outputHighlight));
                foreach (var n in new[] { "Fermenter_MetalBands", "Fermenter_WoodenFrame" })
                    if (F(n)) F(n).gameObject.layer = IgnoreRaycastLayer;
                fc.interactables = list.ToArray();

                var ob = WorldBounds(F("Fermenter_InputOpening"));
                fc.pourStream = Stream(ferm, "Winery_PourStream", ob.center + Vector3.up * 0.75f, Vector3.down, JuiceMat, 70f, 0.4f, 1.2f, 0.55f, 0.045f);
                fc.bubbles = Stream(ferm, "Winery_Bubbles", ob.center + Vector3.up * 0.05f, Vector3.up, BubbleMat, 5f, 0.25f, -0.05f, 1.4f, 0.03f, 25f, 0.18f);
                log.Add("fermenter: lid, needle, liquid, pour stream, bubbles wired");
            }
            else log.Add("fermenter: SmallFermentationStation NOT FOUND");

            // ---------------- giant bottle
            var bottle = Find("GiantWineBottle");
            if (bottle)
            {
                var gc = Station<GiantWineBottleController>(gameplay, "Station_GiantBottle", "Giant Wine Bottle");
                Transform B(string n) => bottle.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);
                gc.wineLiquid = B("WineLiquid");
                gc.glass = B("WineBottle_Glass") ? B("WineBottle_Glass").GetComponent<Renderer>() : null;
                // Wine drawn after the transparent glass so the thin glass film does not wash it out to pink.
                var bw = Mat("Winery_BottleWine", new Color(0.55f, 0.04f, 0.12f, 1f), 0.35f, transparent: true);
                bw.renderQueue = (int)RenderQueue.Transparent + 10;
                EditorUtility.SetDirty(bw);
                if (gc.wineLiquid) gc.wineLiquid.GetComponent<Renderer>().sharedMaterial = bw;
                var list = new List<WineryInteractable>();
                var funnelR = R(B("WineInput_Funnel"), B("WineInput_Pipe"));
                foreach (var n in new[] { "WineInput_Funnel", "WineInput_Pipe", "Input_Flange", "Input_Collar" })
                    if (B(n)) list.Add(Interactable(B(n).gameObject, gc, InteractionPart.BottleFunnel, 0, funnelR));
                var bodyR = R(B("WineBottle_Glass"), B("Bottle_SupportStructure"));
                foreach (var n in new[] { "WineBottle_Glass", "Bottle_SupportStructure", "WineBottle_Cork", "Neck_BrassBand", "Tank_GrapeSign" })
                    if (B(n)) list.Add(Interactable(B(n).gameObject, gc, InteractionPart.BottleBody, 0, bodyR));
                var tapR = R(B("WineTap_Body"), B("WineTap_Handle"), B("WineTap_Spout"));
                foreach (var n in new[] { "WineTap_Body", "WineTap_Handle", "WineTap_Spout", "Tap_Flange", "Tap_Flange_Trim" })
                    if (B(n)) list.Add(Interactable(B(n).gameObject, gc, InteractionPart.BottleTap, 0, tapR));
                gc.interactables = list.ToArray();
                var fb = WorldBounds(B("WineInput_Funnel"));
                gc.pourStream = Stream(bottle, "Winery_BottlePour", fb.center + Vector3.up * 0.9f, Vector3.down, WineDropMat, 80f, 0.5f, 1.2f, 0.5f, 0.06f);
                log.Add("bottle: funnel/body/tap wired, liquid level + pour stream");
            }
            else log.Add("bottle: GiantWineBottle NOT FOUND");

            // Persistent Mirror scene ids for the new station objects.
            var onValidate = typeof(NetworkIdentity).GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var id in gameplay.GetComponentsInChildren<NetworkIdentity>(true)) onValidate?.Invoke(id, null);

            EditorSceneManager.MarkSceneDirty(scene);
            return "Scene: " + string.Join("; ", log);
        }

        static T Station<T>(Transform parent, string name, string display) where T : WineryStation
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<NetworkIdentity>();
            var st = go.AddComponent<T>();
            st.displayName = display;
            return st;
        }

        static WineryInteractable Interactable(GameObject go, WineryStation st, InteractionPart part, int index, Renderer[] highlight)
        {
            var it = go.AddComponent<WineryInteractable>();
            it.station = st; it.part = part; it.index = index;
            it.highlightRenderers = highlight;
            it.colliders = go.GetComponentsInChildren<Collider>(true);
            return it;
        }

        static Renderer[] R(params Transform[] ts) => ts.Where(t => t).SelectMany(t => t.GetComponentsInChildren<Renderer>(true)).Distinct().ToArray();

        static Bounds WorldBounds(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>(true).Where(r => !(r is ParticleSystemRenderer)).ToArray();
            if (rs.Length == 0) return new Bounds(t.position, Vector3.zero);
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// <summary>Small droplet / bubble emitter (mesh particles, lit material), stopped by default.</summary>
        static ParticleSystem Stream(Transform parent, string name, Vector3 worldPos, Vector3 dir, Material mat,
                                     float rate, float speed, float gravity, float lifetime, float size, float coneAngle = 4f, float radius = 0.025f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, true);
            go.transform.position = worldPos;
            go.transform.rotation = Quaternion.LookRotation(dir);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.startLifetime = lifetime;
            main.startSpeed = speed;
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.7f, size * 1.2f);
            main.gravityModifier = gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 150;
            var em = ps.emission; em.rateOverTime = rate;
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = coneAngle; sh.radius = radius;
            var sol = ps.sizeOverLifetime; sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.8f, 0.8f), new Keyframe(1f, 0f)));
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Mesh;
            r.mesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            return ps;
        }
    }
}
