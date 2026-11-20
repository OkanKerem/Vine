using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mirror;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Vineyard.Player;
using Vineyard.Winery;

namespace Vineyard.EditorTools
{
    /// <summary>
    /// Tools > Vineyard > Run Winery Loop Test (Play Mode)
    ///
    /// Enters Play Mode in Village.unity, starts a host and plays the production loop through the real
    /// gameplay path: the player camera is aimed at each part, the raycast target and prompt are checked,
    /// then PlayerInteractor.TryInteract() (what a left click calls) sends the Mirror command to the host.
    /// Results: Logs/WineryLoopTest.txt, renders of each step: Logs/WineryTest/*.jpg
    /// </summary>
    [InitializeOnLoad]
    public static class WineryLoopTest
    {
        const string Flag = "Vineyard.WineryLoopTest.Run";
        const string ScenePath = "Assets/_Project/Scenes/Village.unity";
        const string LogPath = "Logs/WineryLoopTest.txt";
        const string ShotDir = "Logs/WineryTest";

        static WineryLoopTest()
        {
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Flag, false))
                {
                    SessionState.SetBool(Flag, false);
                    Begin();
                }
            };
        }

        [MenuItem("Tools/Vineyard/Run Winery Loop Test (Play Mode)")]
        public static void RunMenu() => Run();

        public static string Run()
        {
            if (EditorApplication.isPlaying) return "already playing";
            if (EditorSceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath);
            SessionState.SetBool(Flag, true);
            EditorApplication.isPlaying = true;
            return "entering play mode";
        }

        // ------------------------------------------------------------------ runner

        static IEnumerator<float> routine;
        static double resumeAt;
        static StringBuilder log;
        static int pass, fail;

        static void Begin()
        {
            log = new StringBuilder();
            pass = fail = 0;
            Directory.CreateDirectory(ShotDir);
            Log($"Winery loop test {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            routine = Steps();
            resumeAt = 0;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { Finish("play mode exited early"); return; }
            if (EditorApplication.timeSinceStartup < resumeAt) return;
            bool more;
            try { more = routine.MoveNext(); }
            catch (Exception e) { Log("EXCEPTION: " + e); fail++; more = false; }
            if (!more) { Finish(null); return; }
            resumeAt = EditorApplication.timeSinceStartup + routine.Current;
        }

        static void Finish(string reason)
        {
            EditorApplication.update -= Tick;
            if (reason != null) Log("Stopped: " + reason);
            Log($"RESULT: {pass} passed, {fail} failed");
            File.WriteAllText(LogPath, log.ToString());
            Debug.Log($"[WineryLoopTest] {pass} passed, {fail} failed. {Path.GetFullPath(LogPath)}");
            if (NetworkManager.singleton != null && (NetworkServer.active || NetworkClient.active)) NetworkManager.singleton.StopHost();
            EditorApplication.isPlaying = false;
        }

        static void Log(string s) => log.AppendLine(s);
        static void Check(string what, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Log($"{(ok ? "PASS" : "FAIL")}  {what}{(string.IsNullOrEmpty(detail) ? "" : "  — " + detail)}");
        }

        // ------------------------------------------------------------------ helpers

        static NetworkPlayer player;
        static PlayerInteractor interactor;
        static PlayerBucket bucket;
        static readonly List<string> feedback = new List<string>();
        static readonly FieldInfo YawF = typeof(NetworkPlayer).GetField("yaw", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo PitchF = typeof(NetworkPlayer).GetField("pitch", BindingFlags.Instance | BindingFlags.NonPublic);

        static string B() => $"{bucket.Type} {bucket.Amount}/{bucket.Capacity}";

        static void Stand(Vector3 feet)
        {
            var motor = player.GetComponent<PlayerMotor>();
            motor.Teleport(feet + Vector3.up * 0.05f);
        }

        /// <summary>Aim the real player camera at a point (sets the controller's yaw / pitch).</summary>
        static void Aim(Vector3 target)
        {
            Vector3 eye = player.cameraRoot.position;
            Vector3 d = target - eye;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan2(-d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
            YawF.SetValue(player, yaw);
            PitchF.SetValue(player, pitch);
        }

        static Vector3 Center(WineryInteractable it)
        {
            var cols = it.colliders.Where(c => c && !c.isTrigger).ToArray();
            if (cols.Length == 0) cols = it.colliders;
            return cols[0].bounds.center;
        }

        /// <summary>Stand at 'feet', aim at the part, verify raycast + prompt, then click.</summary>
        static IEnumerable<float> Click(string label, Vector3 feet, WineryInteractable target, bool expectActionable)
        {
            Stand(feet);
            yield return 0.15f;
            Aim(Center(target));
            yield return 0.15f;
            var cur = interactor.Current;
            bool sameTarget = cur != null && cur.station == target.station && cur.part == target.part && cur.index == target.index;
            var pr = interactor.CurrentPrompt;
            Check($"{label}: raycast finds the part and shows a prompt", sameTarget,
                  sameTarget ? $"\"{pr.title}\" / \"{(pr.actionable ? pr.action : pr.blockReason)}\" / \"{pr.status}\"" : $"aimed at {(cur ? cur.name + " " + cur.part : "nothing")}");
            Check($"{label}: prompt actionable = {expectActionable}", pr.actionable == expectActionable, pr.actionable ? pr.action : pr.blockReason);
            feedback.Clear();
            interactor.TryInteract();       // exactly what a left click does
            yield return 0.35f;             // command -> host -> SyncVars -> TargetRpc
        }

        static void Shot(string name, Vector3 camPos, Vector3 lookAt, float fov = 55f)
        {
            var go = new GameObject("__TestCam") { hideFlags = HideFlags.HideAndDontSave };
            var cam = go.AddComponent<Camera>();
            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            cam.fieldOfView = fov; cam.nearClipPlane = 0.03f;
            go.transform.SetPositionAndRotation(camPos, Quaternion.LookRotation(lookAt - camPos));
            var rt = new RenderTexture(960, 540, 24) { antiAliasing = 4 };
            cam.targetTexture = rt; cam.Render();
            var tex = new Texture2D(960, 540, TextureFormat.RGB24, false);
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 960, 540), 0, 0); tex.Apply(); RenderTexture.active = null;
            File.WriteAllBytes($"{ShotDir}/{name}.jpg", tex.EncodeToJPG(88));
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
        }

        /// <summary>What the player sees (including the first-person bucket).</summary>
        static void PlayerShot(string name)
        {
            var cam = player.playerCamera;
            var rt = new RenderTexture(960, 540, 24) { antiAliasing = 4 };
            var prev = cam.targetTexture;
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = prev;
            var tex = new Texture2D(960, 540, TextureFormat.RGB24, false);
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 960, 540), 0, 0); tex.Apply(); RenderTexture.active = null;
            File.WriteAllBytes($"{ShotDir}/{name}.jpg", tex.EncodeToJPG(88));
            UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
        }

        static IEnumerable<float> Wait(Func<bool> cond, float timeout)
        {
            double end = EditorApplication.timeSinceStartup + timeout;
            while (!cond() && EditorApplication.timeSinceStartup < end) yield return 0.1f;
        }

        // ------------------------------------------------------------------ the test

        static IEnumerator<float> Steps()
        {
            var nm = NetworkManager.singleton;
            Check("NetworkManager present in Village scene", nm != null);
            if (nm == null) yield break;
            nm.StartHost();
            foreach (var w in Wait(() => NetworkClient.localPlayer != null, 10f)) yield return w;
            Check("Host started and local player spawned", NetworkClient.localPlayer != null);
            if (NetworkClient.localPlayer == null) yield break;
            yield return 0.5f;

            player = NetworkClient.localPlayer.GetComponent<NetworkPlayer>();
            interactor = player.GetComponent<PlayerInteractor>();
            bucket = player.GetComponent<PlayerBucket>();
            interactor.Feedback += (m, ok) => feedback.Add((ok ? "+" : "-") + m);
            Check("Player has bucket + interactor + HUD", interactor && bucket && player.GetComponent<WineryHUD>());
            Check("Bucket starts empty with capacity 10", bucket.Type == ResourceType.Empty && bucket.Amount == 0 && bucket.Capacity == 10, B());
            Check("First-person bucket active for the local player", bucket.viewBucket && bucket.viewBucket.activeInHierarchy);

            var vineyard = UnityEngine.Object.FindFirstObjectByType<VineyardController>();
            var press = UnityEngine.Object.FindFirstObjectByType<GrapePressController>();
            var ferm = UnityEngine.Object.FindFirstObjectByType<FermentationMachineController>();
            var bottle = UnityEngine.Object.FindFirstObjectByType<GiantWineBottleController>();
            Check("All four stations spawned (active)", vineyard && press && ferm && bottle,
                  $"vineyard={(bool)vineyard} press={(bool)press} fermenter={(bool)ferm} bottle={(bool)bottle}");
            if (!vineyard || !press || !ferm || !bottle) yield break;
            Check("Vineyard has 20 vines with 8 grapes each", vineyard.vines.Length == 20 && Enumerable.Range(0, 20).All(i => vineyard.Available(i) == 8));

            WineryInteractable Vine(int i) => vineyard.Find(InteractionPart.Harvest, i);
            Vector3 VineStand(int i)
            {
                var v = vineyard.vines[i].transform.position;
                var p = v + new Vector3(1.45f, 0f, 0.3f);
                p.y = Ground(p);
                return p;
            }
            Vector3 Access(Transform station, string n)
            {
                var t = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).First(x => x.name == n && (station == null || x.parent == station || x.IsChildOf(station)));
                var p = t.position; p.y = Ground(p); return p;
            }

            // ---------------- Test 1: basic harvesting
            Log("\n== Test 1 — Basic harvesting");
            foreach (var w in Click("Harvest vine 1", VineStand(0), Vine(0), true)) yield return w;
            Check("Bucket receives 8 grapes", bucket.Type == ResourceType.Grapes && bucket.Amount == 8, B());
            Check("Vine 1 becomes empty", vineyard.Available(0) == 0, $"vine={vineyard.Available(0)}");
            Check("Feedback message shown", feedback.Any(f => f.Contains("Harvested 8")), string.Join(" | ", feedback));
            yield return 0.6f;
            PlayerShot("t1_bucket_8_grapes_firstperson");
            Shot("t1_vine_after_harvest", vineyard.vines[0].transform.position + new Vector3(2.2f, 1.5f, -0.6f), vineyard.vines[0].transform.position + Vector3.up * 0.9f);

            // ---------------- Test 2: partial harvesting
            Log("\n== Test 2 — Partial harvesting");
            bucket.ServerRemove(ResourceType.Grapes, 1);    // test setup: 7 grapes
            yield return 0.2f;
            Check("Setup: bucket holds 7 grapes", bucket.Amount == 7, B());
            foreach (var w in Click("Harvest vine 2", VineStand(1), Vine(1), true)) yield return w;
            Check("Bucket becomes 10/10 (+3)", bucket.Amount == 10, B());
            Check("Vine 2 keeps 5 grapes", vineyard.Available(1) == 5, $"vine={vineyard.Available(1)}");
            foreach (var w in Click("Harvest vine 3 with a full bucket", VineStand(2), Vine(2), false)) yield return w;
            Check("Full bucket: nothing collected, vine 3 untouched", bucket.Amount == 10 && vineyard.Available(2) == 8, $"{B()} vine3={vineyard.Available(2)}");
            Check("'Bucket Full' explained", feedback.Any(f => f.Contains("Bucket Full")), string.Join(" | ", feedback));
            yield return 0.4f;
            PlayerShot("t2_bucket_full_grapes_firstperson");

            // ---------------- Test 3: grape press
            Log("\n== Test 3 — Grape press");
            var pressRoot = press.handle.root;
            var pIn = press.Find(InteractionPart.PressInput, 0);
            var pHandle = press.Find(InteractionPart.PressHandle, 0);
            var pOut = press.Find(InteractionPart.PressOutput, 0);
            Vector3 pressFront = Access(null, "Access_GrapeInput");
            Vector3 pressOutSpot = Access(null, "Access_JuiceOutput");
            {   // stand ~1.8 m in front of the output bucket, as a player naturally would
                Vector3 bucketC = Center(pOut); Vector3 away = pressOutSpot - new Vector3(bucketC.x, pressOutSpot.y, bucketC.z); away.y = 0f;
                pressOutSpot = new Vector3(bucketC.x, 0f, bucketC.z) + away.normalized * 1.8f; pressOutSpot.y = Ground(pressOutSpot);
            }
            foreach (var w in Click("Press: start with no grapes", pressFront, pHandle, false)) yield return w;
            Check("Handle refuses without grapes", press.State == GrapePressController.PressState.Empty, string.Join(" | ", feedback));
            foreach (var w in Click("Press: add grapes", pressFront, pIn, true)) yield return w;
            Check("Bucket empty after deposit", bucket.Type == ResourceType.Empty && bucket.Amount == 0, B());
            Check("Press holds 10 grapes, state Loaded", press.StoredGrapes == 10 && press.State == GrapePressController.PressState.Loaded, $"{press.StoredGrapes} {press.State}");
            Vector3 pressCenter = RB(press.screw).center;
            Shot("t3a_press_loaded", pressCenter + new Vector3(1.5f, 0.9f, 1.3f), pressCenter + Vector3.down * 0.35f, 50f);
            float screwY0 = press.screw.position.y;            // rest height, before the cycle starts
            foreach (var w in Click("Press: start (handle)", pressFront, pHandle, true)) yield return w;
            Check("Press state Pressing", press.State == GrapePressController.PressState.Pressing, press.State.ToString());
            yield return 1.2f;
            float screwYMid = press.screw.position.y;
            Shot("t3b_press_mid_stroke", pressCenter + new Vector3(1.5f, 0.9f, 1.3f), pressCenter + Vector3.down * 0.35f, 50f);
            Check("Screw / handle / plate moved down mid-cycle", screwYMid < screwY0 - 0.1f, $"screw y {screwY0:F2} -> {screwYMid:F2}");
            interactor.RequestInteract(pHandle);   // duplicate activation while pressing
            yield return 0.3f;
            Check("Duplicate start rejected while pressing", press.State == GrapePressController.PressState.Pressing && press.StoredGrapes == 10);
            foreach (var w in Wait(() => press.State != GrapePressController.PressState.Pressing, 5f)) yield return w;
            yield return 0.4f;
            Check("Pressing completes: 10 L juice, state JuiceReady", press.Juice == 10 && press.StoredGrapes == 0 && press.State == GrapePressController.PressState.JuiceReady, $"{press.Juice} L {press.State}");
            Check("Plate returned to rest", Mathf.Abs(press.screw.position.y - screwY0) < 0.02f, $"screw y {press.screw.position.y:F2}");
            Shot("t3c_press_juice_ready", pressCenter + new Vector3(1.5f, 0.9f, 1.3f), pressCenter + Vector3.down * 0.35f, 50f);
            foreach (var w in Click("Press: collect juice", pressOutSpot, pOut, true)) yield return w;
            Check("Bucket holds 10 L grape juice", bucket.Type == ResourceType.GrapeJuice && bucket.Amount == 10, B());
            Check("Press is empty again", press.Juice == 0 && press.State == GrapePressController.PressState.Empty, $"{press.Juice} {press.State}");
            yield return 0.8f;
            PlayerShot("t3d_bucket_juice_firstperson");

            // ---------------- Test 4: fermentation
            Log("\n== Test 4 — Fermentation");
            var fIn = ferm.Find(InteractionPart.FermenterInput, 0);
            var fOut = ferm.Find(InteractionPart.FermenterOutput, 0);
            Vector3 fermIn = Access(null, "Access_Input");
            Vector3 fermOut = Access(null, "Access_Output");
            Vector3 fermCenter = RB(ferm.lid).center;
            foreach (var w in Click("Fermenter: collect wine before ready", fermOut, fOut, false)) yield return w;
            Check("Output refuses when no wine", ferm.Wine == 0 && bucket.Amount == 10, string.Join(" | ", feedback));
            float lidAngle0 = ferm.lid.eulerAngles.x;
            foreach (var w in Click("Fermenter: add juice", fermIn, fIn, true)) yield return w;
            Check("Bucket empty after pouring", bucket.Amount == 0, B());
            Check("Fermenter holds 10 L juice", ferm.Juice == 10, $"{ferm.Juice} {ferm.State}");
            yield return 0.3f;
            Shot("t4a_lid_open_pouring", fermCenter + new Vector3(1.3f, 0.8f, 1.2f), fermCenter + Vector3.down * 0.4f, 50f);
            float lidAngleOpen = ferm.lid.eulerAngles.x;
            Check("Lid rotates during the pour", Mathf.Abs(Mathf.DeltaAngle(lidAngle0, lidAngleOpen)) > 30f, $"lid x {lidAngle0:F0} -> {lidAngleOpen:F0}");
            foreach (var w in Wait(() => ferm.State == FermentationMachineController.FermentState.Fermenting, 4f)) yield return w;
            Check("Fermentation starts automatically", ferm.State == FermentationMachineController.FermentState.Fermenting, ferm.State.ToString());
            Check("Lid closed again", Mathf.Abs(Mathf.DeltaAngle(ferm.lid.eulerAngles.x, lidAngle0)) < 3f, $"lid x {ferm.lid.eulerAngles.x:F0}");
            float needle0 = ferm.needle.eulerAngles.z;
            foreach (var w in Click("Fermenter: add juice while fermenting", fermIn, fIn, false)) yield return w;
            yield return 4.6f;
            var dialC = RB(ferm.dial).center;
            Vector3 front = RB(ferm.Find(InteractionPart.FermenterOutput, 0).transform).center - RB(ferm.lid).center; front.y = 0f; front.Normalize();
            Shot("t4b_fermenting_needle", dialC + front * 0.75f + Vector3.up * 0.15f, dialC, 35f);
            Check("Needle moves with progress", Mathf.Abs(Mathf.DeltaAngle(needle0, ferm.needle.eulerAngles.z)) > 40f, $"needle z {needle0:F0} -> {ferm.needle.eulerAngles.z:F0}");
            foreach (var w in Wait(() => ferm.State == FermentationMachineController.FermentState.WineReady, 12f)) yield return w;
            Check("Fermentation completes: 10 L wine", ferm.Wine == 10 && ferm.Juice == 0 && ferm.State == FermentationMachineController.FermentState.WineReady, $"{ferm.Wine} L {ferm.State}");
            Shot("t4c_wine_ready", fermCenter + new Vector3(1.3f, 0.6f, 1.6f), fermCenter + Vector3.down * 0.6f, 50f);
            foreach (var w in Click("Fermenter: collect wine", fermOut, fOut, true)) yield return w;
            Check("Bucket holds 10 L wine", bucket.Type == ResourceType.Wine && bucket.Amount == 10, B());
            Check("Fermenter empty again", ferm.Wine == 0 && ferm.State == FermentationMachineController.FermentState.Empty, $"{ferm.Wine} {ferm.State}");
            foreach (var w in Click("Harvest while carrying wine", VineStand(2), Vine(2), false)) yield return w;
            Check("Wrong resource: grapes refused while carrying wine", bucket.Type == ResourceType.Wine && vineyard.Available(2) == 8);
            yield return 0.6f;
            PlayerShot("t4d_bucket_wine_firstperson");

            // ---------------- Test 5: giant bottle
            Log("\n== Test 5 — Giant bottle");
            var funnel = bottle.Find(InteractionPart.BottleFunnel, 0);
            Vector3 bottleSpot = Access(null, "InputFunnel_Access");
            float liquidY0 = bottle.wineLiquid.GetComponent<Renderer>().bounds.max.y;
            bool liquidVisible0 = bottle.wineLiquid.gameObject.activeInHierarchy;
            Shot("t5a_bottle_before", bottleSpot + new Vector3(-6f, 2.5f, -5f), bottle.wineLiquid.position + Vector3.up * 1.5f, 55f);
            foreach (var w in Click("Bottle: pour wine", bottleSpot, funnel, true)) yield return w;
            Check("Bucket empties", bucket.Amount == 0 && bucket.Type == ResourceType.Empty, B());
            Check("Bottle gains 10 L", bottle.Wine == 10, $"{bottle.Wine}/{bottle.capacity}");
            yield return 0.4f;
            Shot("t5b_bottle_pouring", bottleSpot + new Vector3(-2.5f, 1.8f, -2.5f), funnel.FeedbackPoint, 55f);
            yield return 2.5f;
            var lr = bottle.wineLiquid.GetComponent<Renderer>();
            Check("Bottle liquid visible after pouring (was hidden at 0 L)", !liquidVisible0 && lr.gameObject.activeInHierarchy, $"before visible={liquidVisible0}");
            Shot("t5c_bottle_after_10L", bottleSpot + new Vector3(-6f, 2.5f, -5f), bottle.wineLiquid.position + Vector3.up * 1.5f, 55f);
            float level10 = lr.bounds.max.y;
            if (bottle.glass) { bottle.glass.enabled = false; Shot("t5d_bottle_10L_noglass_diagnostic", bottleSpot + new Vector3(-3.5f, 1.2f, -2.5f), RB(bottle.wineLiquid).center, 60f); bottle.glass.enabled = true; }

            // ---------------- Test 7: repeated production (two more full loops, other vines)
            Log("\n== Test 7 — Repeated production");
            for (int loop = 0; loop < 2; loop++)
            {
                int va = 3 + loop * 2, vb = 4 + loop * 2;
                foreach (var w in Click($"Loop {loop + 2}: harvest", VineStand(va), Vine(va), true)) yield return w;
                foreach (var w in Click($"Loop {loop + 2}: harvest", VineStand(vb), Vine(vb), true)) yield return w;
                int carried = bucket.Amount;
                foreach (var w in Click($"Loop {loop + 2}: press input", pressFront, pIn, true)) yield return w;
                foreach (var w in Click($"Loop {loop + 2}: press start", pressFront, pHandle, true)) yield return w;
                foreach (var w in Wait(() => press.State == GrapePressController.PressState.JuiceReady, 6f)) yield return w;
                foreach (var w in Click($"Loop {loop + 2}: collect juice", pressOutSpot, pOut, true)) yield return w;
                foreach (var w in Click($"Loop {loop + 2}: fermenter input", fermIn, fIn, true)) yield return w;
                foreach (var w in Wait(() => ferm.State == FermentationMachineController.FermentState.WineReady, 15f)) yield return w;
                foreach (var w in Click($"Loop {loop + 2}: collect wine", fermOut, fOut, true)) yield return w;
                foreach (var w in Click($"Loop {loop + 2}: pour", bottleSpot, funnel, true)) yield return w;
                Check($"Loop {loop + 2}: {carried} grapes -> {carried} L wine in the bottle, machines reset",
                      bucket.Amount == 0 && press.State == GrapePressController.PressState.Empty && ferm.State == FermentationMachineController.FermentState.Empty,
                      $"bottle {bottle.Wine} L, press {press.State}, fermenter {ferm.State}");
            }
            Check("Bottle total after 3 loops = 30 L", bottle.Wine == 30, $"{bottle.Wine} L");
            yield return 3f;
            float level30 = lr.bounds.max.y;
            Check("Bottle liquid level rises with the amount", level30 > level10 + 0.5f, $"top y {level10:F2} (10 L) -> {level30:F2} (30 L)");
            Shot("t7_bottle_30L", bottleSpot + new Vector3(-6f, 2.5f, -5f), bottle.wineLiquid.position + Vector3.up * 1.5f, 55f);
            Shot("t7b_bottle_30L_close", bottleSpot + new Vector3(-3.5f, 1.2f, -2.5f), RB(bottle.wineLiquid).center, 60f);
            if (bottle.glass) { bottle.glass.enabled = false; Shot("t7c_bottle_30L_noglass_diagnostic", bottleSpot + new Vector3(-3.5f, 1.2f, -2.5f), RB(bottle.wineLiquid).center, 60f); bottle.glass.enabled = true; }

            // ---------------- Regrowth (vine 1 was emptied at the start)
            Log("\n== Regrowth");
            Check("Vine 1 regrows gradually (one unit per interval, not all at once)", vineyard.Available(0) > 0 && vineyard.Available(0) < 8, $"vine 1 = {vineyard.Available(0)}/8 since it was emptied");
            Shot("t8_regrowing_vines", vineyard.vines[0].transform.position + new Vector3(2.4f, 1.6f, 1.5f), vineyard.vines[1].transform.position + Vector3.up * 0.8f, 60f);

            // ---------------- Bottle full / capacity
            Log("\n== Capacity limits");
            if (bucket.Amount > 0) bucket.ServerRemove(bucket.Type, 99);
            int before = bottle.Wine;
            bottle.capacity = before + 5;                       // upgrade-style runtime change (host)
            bucket.ServerAdd(ResourceType.Wine, 10);            // test setup
            yield return 0.2f;
            foreach (var w in Click("Bottle: pour into nearly full bottle", bottleSpot, funnel, true)) yield return w;
            Check("Only the free 5 L are accepted, 5 L stay in the bucket", bottle.Wine == before + 5 && bucket.Amount == 5, $"bottle {bottle.Wine}, {B()}");
            foreach (var w in Click("Bottle: pour into full bottle", bottleSpot, funnel, false)) yield return w;
            Check("'Bottle Full' prevents pouring", bottle.Wine == before + 5 && bucket.Amount == 5 && feedback.Any(f => f.Contains("Bottle Full")), string.Join(" | ", feedback));
            bottle.capacity = 100;
            bucket.ServerRemove(ResourceType.Wine, 99);

            // ---------------- Out of range (host validation)
            Log("\n== Host validation");
            Stand(pressFront + new Vector3(0f, 0f, 9f));
            yield return 0.3f;
            feedback.Clear();
            interactor.RequestInteract(pHandle);
            yield return 0.4f;
            Check("Request from 9 m away rejected as 'Out of Range'", feedback.Any(f => f.Contains("Out of Range")), string.Join(" | ", feedback));

            // ---------------- HUD (Game view capture, includes IMGUI)
            Log("\n== HUD");
            bucket.ServerAdd(ResourceType.Wine, 7);
            Stand(bottleSpot);
            yield return 0.2f;
            Aim(Center(funnel));
            yield return 0.5f;
            ScreenCapture.CaptureScreenshot(Path.GetFullPath($"{ShotDir}/hud_bottle_prompt.png"));
            yield return 0.6f;
            bucket.ServerRemove(ResourceType.Wine, 99);
            bucket.ServerAdd(ResourceType.Grapes, 6);
            Stand(pressFront);
            yield return 0.2f;
            Aim(Center(pIn));
            yield return 0.5f;
            ScreenCapture.CaptureScreenshot(Path.GetFullPath($"{ShotDir}/hud_press_prompt.png"));
            yield return 0.6f;
            Check("HUD screenshots written (Game view)", File.Exists($"{ShotDir}/hud_bottle_prompt.png") && File.Exists($"{ShotDir}/hud_press_prompt.png"));
            bucket.ServerRemove(ResourceType.Grapes, 99);

            // ---------------- Fill the bottle to 100 % through the funnel
            Log("\n== Full bottle");
            Stand(bottleSpot);
            for (int k = 0; k < 25 && bottle.Wine < bottle.capacity; k++)
            {
                if (bucket.Amount == 0) bucket.ServerAdd(ResourceType.Wine, 10);   // test supply
                yield return 0.1f;
                interactor.RequestInteract(funnel);
                yield return 0.35f;
            }
            Check("Bottle reaches 100 / 100 L through repeated pours", bottle.Wine == 100, $"{bottle.Wine} L");
            if (bucket.Amount == 0) bucket.ServerAdd(ResourceType.Wine, 3);
            feedback.Clear();
            Aim(Center(funnel));
            yield return 0.3f;
            interactor.TryInteract();
            yield return 0.3f;
            Check("At 100 L pouring is refused with 'Bottle Full'", bottle.Wine == 100 && feedback.Any(f => f.Contains("Bottle Full")), string.Join(" | ", feedback));
            yield return 7f;
            Shot("t9_bottle_full_100L", bottleSpot + new Vector3(-7f, 3f, -6f), RB(bottle.glass.transform).center, 60f);
            float glassTop = RB(bottle.glass.transform).max.y;
            Check("Full liquid stays inside the straight part of the glass", lr.bounds.max.y < glassTop - 2.5f, $"liquid top {lr.bounds.max.y:F2}, glass top {glassTop:F2}");

            Log("\nScreenshots: " + Path.GetFullPath(ShotDir));
        }

        static Bounds RB(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>();
            var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds); return b;
        }

        static float Ground(Vector3 p)
        {
            var t = Terrain.activeTerrain;
            return t ? t.SampleHeight(p) + t.transform.position.y : p.y;
        }
    }
}
