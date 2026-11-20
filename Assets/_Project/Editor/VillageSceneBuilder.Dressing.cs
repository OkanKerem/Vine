using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Vineyard.EditorTools
{
    public static partial class VillageSceneBuilder
    {
        // Planting beds on the square (grass + flowers, one with a shade tree).
        static readonly Vector2[] SquareBeds = { new Vector2(-0.8f, 15.2f), new Vector2(-11.2f, 1.0f), new Vector2(5.8f, -5.4f) };
        static readonly Vector2 DistributionArea = new Vector2(0f, 10.5f);   // in front of the bottle tap (north)
        static readonly Vector2 LoadingArea = new Vector2(0f, -1.5f);        // in front of the bottle funnel (south)
        const float BottleYaw = 90f;                                         // funnel (+X local) -> south, tap -> north

        static readonly List<Vector2> lanternSpots = new List<Vector2>();
        static readonly List<(Vector2 p, float r)> blockers = new List<(Vector2, float)>();   // props already placed

        // ================================================================== bottle

        static GameObject BuildBottle(Transform parent, out Vector3 inputAccess, out Vector3 outputAccess)
        {
            inputAccess = outputAccess = W(SquareC);
            var model = LoadAsset<GameObject>(TankPath);
            if (model == null) return new GameObject("GiantWineBottle");
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model, parent);
            go.name = "GiantWineBottle";
            go.transform.rotation = Quaternion.Euler(0f, BottleYaw, 0f) * model.transform.rotation;
            go.transform.position = new Vector3(SquareC.x, GroundY(SquareC.x, SquareC.y) - 0.02f, SquareC.y);

            int added = 0;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.name.Contains("WineLiquid") || mf.name.StartsWith("Sign_") || mf.name.StartsWith("Port_Bolt")) continue;
                if (mf.GetComponent<Collider>() == null) { mf.gameObject.AddComponent<MeshCollider>(); added++; }
            }
            var b = BoundsOf(go);
            Log($"Giant bottle: {b.size.x:F1} x {b.size.y:F1} x {b.size.z:F1} m at ({SquareC.x}, {SquareC.y}), yaw {BottleYaw} (funnel south, tap north), {added} MeshColliders.");

            Vector3 MakeAccess(string part, string marker)
            {
                var t = FindDeep(go.transform, part);
                if (t == null) { Warn($"Bottle part '{part}' not found."); return go.transform.position; }
                var pb = BoundsOf(t.gameObject);
                Vector3 dir = pb.center - b.center; dir.y = 0; dir.Normalize();
                float reach = Vector3.Dot(pb.extents, new Vector3(Mathf.Abs(dir.x), 0, Mathf.Abs(dir.z)));
                Vector3 p = new Vector3(pb.center.x, 0f, pb.center.z) + dir * (reach + 1.0f);
                p.y = GroundY(p.x, p.z);
                var m = new GameObject(marker).transform;
                m.SetParent(go.transform, true);
                m.position = p;
                m.rotation = Quaternion.LookRotation(-dir, Vector3.up);
                Log($"  {part}: height {pb.center.y - go.transform.position.y:F2} m above ground, access marker '{marker}' at {Fmt(p)}");
                return p;
            }
            inputAccess = MakeAccess("WineInput_Funnel", "InputFunnel_Access");
            outputAccess = MakeAccess("WineTap_Spout", "OutputTap_Access");
            blockers.Add((SquareC, 4.2f));
            return go;
        }

        // ================================================================== village square

        static void BuildSquare(List<HouseInfo> houses)
        {
            var paving = Child(H.Square, "BottleFoundation");
            var curb = Child(H.Square, "StoneBorders");
            var beds = Child(H.Square, "PlantingBeds");

            // Flagstone rings around the bottle's own stone base (radius ~2.5 m).
            int n = 0;
            foreach (var (radius, count) in new[] { (3.2f, 13), (4.25f, 17) })
                for (int i = 0; i < count; i++)
                {
                    float a = (i + (float)rng.NextDouble() * 0.35f) / count * Mathf.PI * 2f;
                    Vector2 p = SquareC + Dir(a) * (radius + R(-0.15f, 0.15f));
                    Flagstone(paving, p, R(1.05f, 1.35f), $"FoundationStone_{++n:00}");
                }

            // Low stone curb around the foundation, open at the funnel (south), tap (north) and both sides.
            float[] gaps = { 90f, -90f, 0f, 180f };
            float[] gapHalf = { 30f, 34f, 12f, 12f };
            int c = 0;
            for (float a = 0f; a < 360f; a += 9.5f)
            {
                bool skip = false;
                for (int g = 0; g < gaps.Length; g++) if (Mathf.Abs(Mathf.DeltaAngle(a, gaps[g])) < gapHalf[g]) skip = true;
                if (skip) continue;
                Vector2 p = SquareC + DirDeg(a) * 5.35f;
                Rock(curb, p, R(0.42f, 0.55f), 0.45f, $"FoundationCurb_{++c:00}", collider: true);
            }
            // Flower pots flank the funnel and tap openings.
            foreach (float a in new[] { 90f - 38f, 90f + 38f, -90f - 42f, -90f + 42f })
                Prop("Flower Pot", H.CenterDecor, SquareC + DirDeg(a) * 5.5f, R(0f, 360f), R(0.9f, 1.05f), ColliderMode.Box, "FoundationPot");
            // Lanterns on the diagonals of the foundation.
            foreach (float a in new[] { 45f, 135f, 225f, 315f })
                Lantern(H.CenterDecor, SquareC + DirDeg(a) * 6.6f, SquareC, "BottleLantern");

            int pn = 0;
            // Planting beds: stone ring, grass, flowers; a shade tree in the northern bed.
            for (int bi = 0; bi < SquareBeds.Length; bi++)
            {
                var bed = Child(beds, $"Bed_{bi + 1:00}");
                Vector2 bc = SquareBeds[bi];
                for (int k = 0; k < 13; k++)
                    Rock(bed, bc + Dir(k / 13f * Mathf.PI * 2f + R(-0.08f, 0.08f)) * 1.95f, R(0.38f, 0.5f), 0.45f, $"BedStone_{k:00}", collider: true);
                for (int k = 0; k < 5; k++)
                    Flower(bed, bc + Dir(R(0f, 6.28f)) * R(0.2f, 1.3f), R(0.75f, 1.0f));
                if (bi == 0) TreeAt(bed, bc, "Tree_2", 0.62f, true);
                if (bi == 2) TreeAt(bed, bc, "Tree_1", 0.5f, true);
                if (bi == 1) Prop("Flower Pot", bed, bc, R(0, 360), 1.1f, ColliderMode.Box, "BedPot");
                blockers.Add((bc, 2.2f));
            }

            // Stone border along the square's edge (gaps where streets enter).
            int sc = 0;
            float perim = 0f;
            for (float a = 0f; a < Mathf.PI * 2f; a += 0.004f)
            {
                float r = PlazaRadius(a) + 0.25f;
                perim += r * 0.004f;
                if (perim < R(0.85f, 1.15f)) continue;
                perim = 0f;
                Vector2 p = SquareC + Dir(a) * r;
                if (RoadEdge(p.x, p.y) < 0.8f) continue;
                if (SquareBeds.Any(b => Vector2.Distance(b, p) < 2.6f)) continue;
                if (houses.Any(h => h.ObbDistance(p) < 0.6f)) continue;
                Rock(curb, p, R(0.32f, 0.5f), 0.5f, $"SquareBorder_{++sc:000}", collider: false);
                if (rng.NextDouble() < 0.12) Flower(H.Flowers, p + (p - SquareC).normalized * R(0.6f, 1.2f), R(0.7f, 0.9f));
            }

            // Benches facing the bottle (clear of the funnel / tap areas).
            foreach (float a in new[] { 105f, 165f, 215f, 325f })
            {
                Vector2 p = SquareC + DirDeg(a) * 9.3f;
                Bench(H.CenterDecor, p, SquareC, "SquareBench");
            }

            // Lanterns around the square's edge.
            foreach (float a in new[] { 12f, 58f, 128f, 150f, 196f, 240f, 292f, 345f })
            {
                Vector2 p = SquareC + DirDeg(a) * (PlazaRadius(a * Mathf.Deg2Rad) - 0.9f);
                if (RoadEdge(p.x, p.y) < 0.4f || !IsFree(p, 1.2f)) continue;
                Lantern(H.CenterDecor, p, SquareC, "SquareLantern");
            }

            // Market corner (north-east): crates, a pot and a bench.
            Vector2 mk = SquareC + DirDeg(45f) * 11f;
            var market = Child(H.CenterDecor, "MarketCorner");
            CrateStack(market, mk, 3, R(0, 360));
            CrateStack(market, mk + new Vector2(1.3f, -0.6f), 1, R(0, 360));
            Prop("Flower Pot", market, mk + new Vector2(-1.2f, 0.6f), 0f, 1f, ColliderMode.Box, "MarketPot");
            blockers.Add((mk, 2f));
            Log($"Square: {n} foundation stones, {c} curb stones, {pn} flagstone patches, {sc} border stones, {SquareBeds.Length} planting beds.");
        }

        // ================================================================== production

        static Dictionary<string, Vector3> BuildProduction()
        {
            var points = new Dictionary<string, Vector3>();
            Vector2 bottleFunnel = LoadingArea;

            // Crushing press: output bucket (+Z) faces the fermenter.
            var press = LoadAsset<GameObject>(PressPath);
            if (press != null)
            {
                float yaw = YawTo(PressPos, FermenterPos);
                var pad = Place(LoadPack("Path_1"), H.Press, PressPos, R(0, 360), 0.78f, true, -0.03f, "WorkPad_Press");
                SetCollider(pad, ColliderMode.None);
                var go = (GameObject)PrefabUtility.InstantiatePrefab(press, H.Press);
                go.name = "SmallGrapePress";
                go.transform.SetPositionAndRotation(new Vector3(PressPos.x, GroundY(PressPos.x, PressPos.y) + 0.01f, PressPos.y), Quaternion.Euler(0, yaw, 0) * press.transform.rotation);
                AddPartBoxColliders(go, "Press_Grapes");
                AccessPoint(H.Press, "Access_GrapeInput", PressPos - Fwd(yaw) * 1.1f, yaw);
                AccessPoint(H.Press, "Access_JuiceOutput", PressPos + Fwd(yaw) * 1.3f, yaw + 180f);
                AccessPoint(H.Press, "Access_Handle", PressPos + Right(yaw) * 1.2f, yaw - 90f);
                points["Press output"] = W(PressPos + Fwd(yaw) * 1.3f);
                points["Press input"] = W(PressPos - Fwd(yaw) * 1.1f);
                blockers.Add((PressPos, 2.2f));
                Log($"Press at ({PressPos.x}, {PressPos.y}), yaw {yaw:F0} (output toward the fermenter).");
            }

            // Fermenter: output tap (+Z) faces the bottle's funnel.
            var ferm = LoadAsset<GameObject>(FermenterPath);
            if (ferm != null)
            {
                float yaw = YawTo(FermenterPos, bottleFunnel);
                var pad = Place(LoadPack("Path_2"), H.Fermenter, FermenterPos, R(0, 360), 0.78f, true, -0.03f, "WorkPad_Fermenter");
                SetCollider(pad, ColliderMode.None);
                var go = (GameObject)PrefabUtility.InstantiatePrefab(ferm, H.Fermenter);
                go.name = "SmallFermentationStation";
                go.transform.SetPositionAndRotation(new Vector3(FermenterPos.x, GroundY(FermenterPos.x, FermenterPos.y) + 0.01f, FermenterPos.y), Quaternion.Euler(0, yaw, 0) * ferm.transform.rotation);
                AddPartBoxColliders(go, "Fermenter_Liquid");
                AccessPoint(H.Fermenter, "Access_Input", FermenterPos - Right(yaw) * 1.2f, yaw + 90f);
                AccessPoint(H.Fermenter, "Access_Output", FermenterPos + Fwd(yaw) * 1.6f, yaw + 180f);
                points["Fermenter output"] = W(FermenterPos + Fwd(yaw) * 1.6f);
                points["Fermenter input side"] = W(FermenterPos - Right(yaw) * 1.2f);
                blockers.Add((FermenterPos, 2.4f));
                Log($"Fermenter at ({FermenterPos.x}, {FermenterPos.y}), yaw {yaw:F0} (tap toward the bottle funnel).");
            }

            // Reserved expansion slots (kept clear; a flagstone pad marks each).
            for (int i = 0; i < ExpansionSlots.Length; i++)
            {
                var slot = new GameObject($"ExpansionSlot_{i + 1:00}");
                slot.transform.SetParent(H.Expansion, false);
                slot.transform.position = W(ExpansionSlots[i]);
                slot.transform.rotation = Quaternion.Euler(0, YawTo(ExpansionSlots[i], CourtC), 0);
                var pad = Place(LoadPack("Path_2"), slot.transform, ExpansionSlots[i], R(0, 360), 0.7f, true, -0.03f, "SlotPad");
                SetCollider(pad, ColliderMode.None);
                points[$"Expansion slot {i + 1}"] = slot.transform.position;
                blockers.Add((ExpansionSlots[i], 2.2f));
            }

            // Storage beside the winery hall (vats from the press model, crates).
            Vector2[] vats = { V(-12.2f, -10.4f), V(-11.0f, -11.3f), V(-12.4f, -11.8f) };
            for (int i = 0; i < vats.Length; i++)
                PartProp(H.Storage, $"Decor_WineVat_{i + 1:00}", PressPath, "Press_Container", vats[i], R(0, 360), R(1.15f, 1.3f), true);
            CrateStack(H.Storage, V(-12.3f, -18.7f), 3, 12f);
            CrateStack(H.Storage, V(-11.1f, -19.5f), 2, -8f);
            CrateStack(H.Storage, V(-12.5f, -20.0f), 1, 30f);
            blockers.Add((V(-11.8f, -11.2f), 1.8f));
            blockers.Add((V(-11.9f, -19.4f), 1.6f));

            // Harvest crate with grapes next to the press, buckets around it.
            var bunch = LoadAsset<GameObject>(BunchPath);
            var hc = CrateStack(H.ProdDecor, V(-10.4f, -17.0f), 1, 20f);
            if (bunch != null && hc != null)
                for (int i = 0; i < 3; i++)
                {
                    var g = (GameObject)PrefabUtility.InstantiatePrefab(bunch, H.ProdDecor);
                    g.name = $"Decor_HarvestGrapes_{i + 1}";
                    var top = hc.transform.position + Vector3.up * 0.93f;
                    g.transform.SetPositionAndRotation(top + new Vector3(R(-0.25f, 0.25f), 0.12f, R(-0.25f, 0.25f)), Quaternion.Euler(R(70f, 95f), R(0, 360), 0f));
                }
            PartProp(H.ProdDecor, "Decor_Bucket_01", PressPath, "Press_Bucket", V(-9.9f, -20.6f), R(0, 360), 1.6f, false);
            PartProp(H.ProdDecor, "Decor_Bucket_02", PressPath, "Press_Bucket", V(-10.6f, -20.2f), R(0, 360), 1.6f, false);
            PartProp(H.ProdDecor, "Decor_Bucket_03", FermenterPath, "Fermenter_OutputBucket", V(0.9f, -14.6f), R(0, 360), 1.5f, false);
            PartProp(H.ProdDecor, "Decor_Bucket_04", FermenterPath, "Fermenter_OutputBucket", V(1.5f, -14.0f), R(0, 360), 1.5f, false);

            // Worker's bench against the winery wall, lanterns, a fence closing the yard's east side.
            Bench(H.ProdDecor, V(-12.9f, -16.9f), V(-6f, -16.9f), "YardBench");
            Lantern(H.ProdDecor, V(-12.9f, -13.0f), CourtC, "YardLantern");
            Lantern(H.ProdDecor, V(4.6f, -11.2f), CourtC, "YardLantern");
            Lantern(H.ProdDecor, V(-4.0f, -22.4f), CourtC, "YardLantern");
            FenceLine(H.ProdDecor, "YardFence", new[] { V(-3.0f, -23.4f), V(4.6f, -21.4f), V(6.6f, -15.4f), V(5.8f, -9.6f) }, null);
            for (int i = 0; i < 6; i++) Flower(H.ProdDecor, V(-13.6f, -21.5f + i * 0.5f) + new Vector2(R(-0.2f, 0.2f), 0), R(0.7f, 0.9f));
            return points;
        }

        // ================================================================== vineyard

        static List<Vector3> BuildVineyard()
        {
            var aisles = new List<Vector3>();
            var vine = LoadAsset<GameObject>(VinePath);
            var bunch = LoadAsset<GameObject>(BunchPath);
            if (vine == null) return aisles;

            int vines = 0, bunches = 0;
            for (int r = 0; r < RowX.Length; r++)
            {
                var row = Child(H.GrapeRows, $"Row_{r + 1:00}");
                for (int i = 0; i < VineZ.Length; i++)
                {
                    float x = RowX[r] + R(-0.04f, 0.04f), z = VineZ[i];
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(vine, row);
                    go.name = $"Grapevine_R{r + 1}_{i + 1:00}";
                    float yaw = (i % 2 == 0 ? 90f : 270f) + R(-1.5f, 1.5f);   // alternate facing so leaves vary along a row
                    go.transform.SetPositionAndRotation(new Vector3(x, GroundY(x, z) - 0.03f, z), Quaternion.Euler(0, yaw, 0) * vine.transform.rotation);

                    // Simple trellis collider: rows behave like walls, aisles stay open.
                    // Measured in the model's own orientation; works for the single-mesh vine (and a separate support part if present).
                    var support = FindDeep(go.transform, "Grapevine_Support");
                    var box = go.AddComponent<BoxCollider>();
                    {
                        var rot = go.transform.rotation; go.transform.rotation = vine.transform.rotation;
                        var sb = BoundsOf(support != null ? support.gameObject : go);
                        go.transform.rotation = rot;
                        box.center = Quaternion.Inverse(go.transform.rotation) * (sb.center - go.transform.position);
                        box.size = new Vector3(sb.size.x, sb.size.y + 0.1f, 0.35f);
                    }
                    vines++;

                    if (bunch != null)
                        foreach (Transform t in go.GetComponentsInChildren<Transform>())
                        {
                            if (!t.name.StartsWith("GrapeSpawn")) continue;
                            var g = (GameObject)PrefabUtility.InstantiatePrefab(bunch, t);
                            g.name = "GrapeBunch";
                            g.transform.position = t.position;
                            g.transform.rotation = Quaternion.Euler(0f, R(0, 360), 0f);
                            bunches++;
                        }
                }
            }
            for (int r = 0; r < RowX.Length - 1; r++)
                aisles.Add(W(new Vector2((RowX[r] + RowX[r + 1]) / 2f, (VineZ[0] + VineZ[VineZ.Length - 1]) / 2f)));
            Log($"Vineyard: {vines} grapevines in {RowX.Length} rows (aisles {RowX[1] - RowX[0] - 0.58f:F1} m clear), {bunches} GrapeBunch instances parented to the GrapeSpawn points.");

            // Fence with a north gate (to the press) and a west gap (farm lane).
            var rect = VineyardRect;
            FenceLine(H.VinFences, "Fence_North", new[] { V(rect.xMin, rect.yMax), V(rect.xMax, rect.yMax) }, (VineyardGateX0, VineyardGateX1, true));
            FenceLine(H.VinFences, "Fence_East", new[] { V(rect.xMax, rect.yMax), V(rect.xMax, rect.yMin) }, null);
            FenceLine(H.VinFences, "Fence_South", new[] { V(rect.xMax, rect.yMin), V(rect.xMin, rect.yMin) }, null);
            FenceLine(H.VinFences, "Fence_West", new[] { V(rect.xMin, rect.yMin), V(rect.xMin, rect.yMax) }, (VineyardWestGapZ0, VineyardWestGapZ1, false));
            Lantern(H.VinDecor, V(VineyardGateX0 - 0.5f, rect.yMax + 0.6f), V(-8.9f, -26f), "GateLantern");
            Lantern(H.VinDecor, V(VineyardGateX1 + 0.5f, rect.yMax + 0.6f), V(-8.9f, -26f), "GateLantern");

            // Stones turned up from the soil, piled at the corners (cleared from the rows).
            foreach (var corner in new[] { V(rect.xMin - 1.6f, rect.yMin - 1.4f), V(rect.xMax + 1.5f, rect.yMin - 1.5f), V(rect.xMin - 1.7f, rect.yMax + 1.6f) })
                RockPile(H.VinDecor, corner, 7, 1.3f);
            int clods = 0;
            foreach (float rx in RowX)
                for (int k = 0; k < 5; k++)
                {
                    Vector2 p = V(rx + R(-0.6f, 0.6f) * (rng.NextDouble() < 0.5 ? 1 : -1), R(VineZ[VineZ.Length - 1] - 1f, VineZ[0] + 1f));
                    if (Mathf.Abs(p.x - rx) < 0.35f) p.x = rx + 0.5f;
                    Rock(H.Soil, p, R(0.16f, 0.26f), 0.4f, $"SoilStone_{++clods:000}", collider: false);
                }

            // Harvest supplies at the gate; a bench under a tree looking over the rows.
            CrateStack(H.VinDecor, V(-13.4f, -23.5f), 2, 10f);
            CrateStack(H.VinDecor, V(-14.5f, -23.2f), 1, -20f);
            PartProp(H.VinDecor, "Decor_Bucket_Gate_01", PressPath, "Press_Bucket", V(-5.0f, -23.6f), R(0, 360), 1.6f, false);
            PartProp(H.VinDecor, "Decor_Bucket_Gate_02", PressPath, "Press_Bucket", V(-4.4f, -23.2f), R(0, 360), 1.6f, false);
            Bench(H.VinDecor, V(-1.8f, -31f), V(-10f, -31f), "VineyardBench");
            TreeAt(H.VinDecor, V(0.6f, -33.4f), "Tree_3", 0.8f, true);
            for (int i = 0; i < 10; i++)
                Flower(H.VinDecor, V(R(rect.xMin + 0.5f, rect.xMax - 0.5f), rect.yMax + R(0.35f, 0.9f)), R(0.6f, 0.85f), avoidRoads: true);
            for (int i = 0; i < 8; i++)
                Flower(H.VinDecor, V(rect.xMax + R(0.4f, 1.0f), R(rect.yMin + 0.5f, rect.yMax - 0.5f)), R(0.6f, 0.85f), avoidRoads: true);
            blockers.Add((new Vector2(rect.center.x, rect.center.y), 0f));
            return aisles;
        }

        // ================================================================== streets & houses

        static void DressRoads(List<HouseInfo> houses)
        {
            int lan = 0, stones = 0;
            var borders = Child(H.Roads, "RoadsideStones");
            foreach (var road in Roads)
            {
                // Waypoints (for future NPC navigation).
                var rgo = Child(H.Roads, road.name);
                for (int i = 0; i < road.pts.Count; i += 5)
                {
                    var wp = new GameObject($"Waypoint_{i / 5:00}");
                    wp.transform.SetParent(rgo, false);
                    wp.transform.position = W(road.pts[i]);
                }

                float acc = 6f; int side = rng.NextDouble() < 0.5 ? 1 : -1;
                float stoneAcc = 0f;
                for (int i = 1; i < road.pts.Count; i++)
                {
                    Vector2 a = road.pts[i - 1], b = road.pts[i];
                    float seg = Vector2.Distance(a, b);
                    Vector2 dir = (b - a).normalized, nrm = new Vector2(dir.y, -dir.x);
                    acc += seg; stoneAcc += seg;
                    if (road.lanterns && acc > 13f)
                    {
                        Vector2 p = b + nrm * side * (road.width / 2f + 0.9f);
                        if (CanPlaceRoadside(p, houses, 1.0f) && !lanternSpots.Any(q => Vector2.Distance(q, p) < 8f) && PlazaSignedDist(p) > 2f)
                        {
                            Lantern(H.ResDecor, p, b, "StreetLantern");
                            lan++; acc = 0f; side = -side;
                        }
                    }
                    if (!road.canCobble || Vector2.Distance(b, SquareC) > 26f)
                    {
                        if (stoneAcc > 1.7f)
                        {
                            stoneAcc = 0f;
                            foreach (int s in new[] { -1, 1 })
                            {
                                if (rng.NextDouble() > 0.45) continue;
                                Vector2 p = b + nrm * s * (road.width / 2f + R(0.15f, 0.45f)) + dir * R(-0.4f, 0.4f);
                                if (!CanPlaceRoadside(p, houses, 0.4f) || RoadEdge(p.x, p.y) < 0.05f) continue;
                                Rock(borders, p, R(0.28f, 0.45f), 0.5f, $"EdgeStone_{++stones:000}", collider: false);
                            }
                        }
                    }
                }
            }
            Log($"Streets: {Roads.Length} roads with waypoints, {lan} street lanterns, {stones} roadside stones.");
        }

        static bool CanPlaceRoadside(Vector2 p, List<HouseInfo> houses, float clearance)
        {
            if (!PlayBounds.Contains(p)) return false;
            if (houses.Any(h => h.ObbDistance(p) < clearance)) return false;
            if (StreamDist(p.x, p.y) < 5f) return false;
            if (bridgeInfo != null && Vector2.Distance(p, bridgeInfo.center) < 8f) return false;
            if (CourtEllipse(p) < 1.05f) return false;
            if (blockers.Any(b => Vector2.Distance(b.p, p) < b.r)) return false;
            return true;
        }

        static void DressHouses(List<HouseInfo> houses)
        {
            var stepRoot = Child(H.ResDecor, "DoorPaths");
            foreach (var h in houses)
            {
                Transform deco = h.def.role == "VillageCenter" ? H.CenterDecor : h.def.role == "Production" ? H.ProdDecor : H.ResDecor;
                var grp = Child(deco, $"{h.def.id}_Props");
                Vector2 door = h.DoorPoint;
                Vector2 f = h.Forward, rt = h.Right;

                // Stepping-stone path from the door to the nearest street (not for the yard / square fronts).
                if (h.def.role == null)
                {
                    Vector2 best = door; float bd = float.MaxValue;
                    foreach (var r in Roads) foreach (var p in r.pts) { float d = Vector2.Distance(p, door); if (d < bd) { bd = d; best = p; } }
                    if (bd < 16f)
                    {
                        Vector2 dir = (best - door).normalized; int k = 0;
                        for (float t = 0.2f; t < bd; t += 0.85f)
                        {
                            Vector2 p = door + dir * t + new Vector2(dir.y, -dir.x) * ((k % 2 == 0 ? 0.22f : -0.22f) + R(-0.08f, 0.08f));
                            if (RoadEdge(p.x, p.y) < 0.1f) break;
                            Flagstone(stepRoot, p, R(0.85f, 1.05f), $"{h.def.id}_Step_{++k:00}");
                        }
                    }
                }

                // Lantern by the door, flowers at the front corners, a pot on the other side of the door.
                int ls = rng.NextDouble() < 0.5 ? 1 : -1;
                if (h.def.role != "VillageCenter")
                    Lantern(grp, door + rt * ls * 2.4f + f * 0.3f, door + f * 1.5f, "DoorLantern", force: true);
                if (rng.NextDouble() < 0.7) Prop("Flower Pot", grp, door - rt * ls * 1.7f - f * 0.15f, R(0, 360), R(0.85f, 1f), ColliderMode.Box, "DoorPot");
                foreach (int s in new[] { -1, 1 })
                {
                    Vector2 corner = h.center + rt * s * (h.half.x - 0.9f) + f * (h.half.y + 0.55f);
                    int cnt = rng.Next(2, 5);
                    for (int i = 0; i < cnt; i++) Flower(grp, corner + rt * R(-0.9f, 0.9f) + f * R(-0.2f, 0.3f), R(0.65f, 0.9f), avoidRoads: true);
                }

                // Crates against a side wall, a couple of rocks at the back corners.
                if (rng.NextDouble() < 0.75)
                {
                    int s = rng.NextDouble() < 0.5 ? 1 : -1;
                    Vector2 p = h.center + rt * s * (h.half.x + 0.75f) + f * R(-h.half.y * 0.5f, h.half.y * 0.3f);
                    if (CanPlaceRoadside(p, houses.Where(o => o != h).ToList(), 1f) && RoadEdge(p.x, p.y) > 0.6f)
                        CrateStack(grp, p, rng.Next(1, 4), h.yaw + R(-10f, 10f));
                }
                foreach (int s in new[] { -1, 1 })
                    if (rng.NextDouble() < 0.6)
                    {
                        Vector2 p = h.center + rt * s * (h.half.x + 0.3f) - f * (h.half.y + 0.3f);
                        if (RoadEdge(p.x, p.y) > 0.5f && StreamDist(p.x, p.y) > 5f) RockPile(grp, p, rng.Next(2, 4), 0.6f);
                    }

                if (h.def.id == "Tavern")
                {
                    // Tavern terrace facing the tap: benches, crates, pots, lanterns on both sides of the door.
                    foreach (int s in new[] { -1, 1 })
                    {
                        Bench(grp, door + rt * s * 3.4f + f * 1.2f, door + rt * s * 3.4f + f * 5f, "TavernBench");
                        Lantern(grp, door + rt * s * 1.9f + f * 0.2f, door + f * 2f, "TavernLantern", force: true);
                    }
                    CrateStack(grp, h.center + rt * (h.half.x + 0.8f) + f * (h.half.y - 1.5f), 3, h.yaw);
                    CrateStack(grp, h.center + rt * (h.half.x + 0.9f) + f * (h.half.y - 2.8f), 1, h.yaw + 25f);
                    Prop("Flower Pot", grp, door + rt * 5.6f + f * 0.4f, 0, 1f, ColliderMode.Box, "TavernPot");
                    Prop("Flower Pot", grp, door - rt * 5.6f + f * 0.4f, 0, 1f, ColliderMode.Box, "TavernPot");
                }

                if (h.def.garden) Garden(grp, h, houses);
            }
        }

        static void Garden(Transform parent, HouseInfo h, List<HouseInfo> houses)
        {
            foreach (int s in new[] { 1, -1 })
            {
                Vector2 c = h.center + h.Right * s * (h.half.x + 3.9f) - h.Forward * (h.half.y * 0.15f);
                bool free = RoadEdge(c.x, c.y) > 4.0f && StreamDist(c.x, c.y) > 8f && houses.All(o => o == h || o.ObbDistance(c) > 4f)
                            && PlayBounds.Contains(c) && CourtEllipse(c) > 1.6f && RectDist(VineyardRect, c) > 4f;
                if (!free) continue;
                var g = Child(parent, "Garden");
                Vector2 f = h.Forward, r = h.Right;
                float e = 3.2f;
                Vector2 p00 = c - r * e - f * e, p10 = c + r * e - f * e, p11 = c + r * e + f * e, p01 = c - r * e + f * e;
                FenceLine(g, "GardenFence", new[] { p01, p00, p10, p11 }, null);
                FenceLine(g, "GardenFenceFront", new[] { p11, p01 }, (0f, 3.2f, true), relative: true);
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        Flower(g, c + r * (i - 1) * 1.8f + f * (j - 1) * 1.8f + new Vector2(R(-0.3f, 0.3f), R(-0.3f, 0.3f)), R(0.8f, 1.0f));
                CrateStack(g, c - r * 2.2f - f * 2.2f, 1, h.yaw);
                blockers.Add((c, 3.5f));
                return;
            }
        }

        // ================================================================== water

        static BridgeInfo bridgeInfo;

        static void BuildWater()
        {
            var mat = WaterMaterial();
            // Stream ribbon.
            var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
            float len = 0f;
            for (int i = 0; i < streamPts.Count; i++)
            {
                Vector2 p = streamPts[i];
                Vector2 d = (streamPts[Mathf.Min(i + 1, streamPts.Count - 1)] - streamPts[Mathf.Max(i - 1, 0)]).normalized;
                Vector2 nrm = new Vector2(d.y, -d.x);
                float y = streamRef[i] - 0.95f;
                float hw = 3.3f;
                if (i > 0) len += Vector2.Distance(streamPts[i], streamPts[i - 1]);
                verts.Add(new Vector3(p.x - nrm.x * hw, y, p.y - nrm.y * hw));
                verts.Add(new Vector3(p.x + nrm.x * hw, y, p.y + nrm.y * hw));
                uvs.Add(new Vector2(0f, len / 6f)); uvs.Add(new Vector2(1f, len / 6f));
                if (i > 0)
                {
                    int b = verts.Count - 4;
                    tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
                }
            }
            var mesh = new Mesh { name = "StreamWater" };
            mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            SaveMesh(mesh, GenDir + "/StreamWater.asset");
            var go = new GameObject("Stream");
            go.transform.SetParent(H.Water, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Pond disc.
            int pi = NearestIndex(streamPts, PondC);
            float py = streamRef[pi] - 0.95f;
            var pv = new List<Vector3> { new Vector3(PondC.x, py, PondC.y) };
            var puv = new List<Vector2> { new Vector2(0.5f, 0.5f) };
            var pt = new List<int>();
            int seg = 48; float pr = PondExtra * 1.2f + 3.3f;
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                pv.Add(new Vector3(PondC.x + Mathf.Cos(a) * pr, py + 0.002f, PondC.y + Mathf.Sin(a) * pr));
                puv.Add(new Vector2(0.5f + Mathf.Cos(a) * 0.5f, 0.5f + Mathf.Sin(a) * 0.5f));
                if (i > 0) pt.AddRange(new[] { 0, i + 1, i });
            }
            var pm = new Mesh { name = "PondWater" };
            pm.SetVertices(pv); pm.SetUVs(0, puv); pm.SetTriangles(pt, 0); pm.RecalculateNormals(); pm.RecalculateBounds();
            SaveMesh(pm, GenDir + "/PondWater.asset");
            var pgo = new GameObject("Pond");
            pgo.transform.SetParent(H.Water, false);
            pgo.AddComponent<MeshFilter>().sharedMesh = pm;
            var pmr = pgo.AddComponent<MeshRenderer>(); pmr.sharedMaterial = mat; pmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Log($"Water: stream ribbon {len:F0} m long, pond r={pr:F1} m, simple URP Lit transparent material (no new dependency).");
        }

        static Material WaterMaterial()
        {
            string path = GenDir + "/Water.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(mat, path); }
            mat.shader = Shader.Find("Universal Render Pipeline/Lit");
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            mat.SetColor("_BaseColor", new Color(0.24f, 0.53f, 0.60f, 0.78f));
            mat.SetFloat("_Smoothness", 0.93f);
            mat.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void DressStream(BridgeInfo bridge)
        {
            int rocks = 0, cliffs = 0;
            var bank = Child(H.Rocks, "StreamBanks");
            for (int i = 0; i < streamPts.Count; i += 4)
            {
                Vector2 p = streamPts[i];
                if (p.y > 95f || p.y < -110f) continue;
                Vector2 d = (streamPts[Mathf.Min(i + 1, streamPts.Count - 1)] - streamPts[Mathf.Max(i - 1, 0)]).normalized;
                Vector2 nrm = new Vector2(d.y, -d.x);
                int side = (i / 4) % 2 == 0 ? 1 : -1;
                if (bridge != null && Vector2.Distance(p, bridge.center) < 8f) continue;
                Vector2 q = p + nrm * side * R(3.4f, 4.5f);
                if (RoadEdge(q.x, q.y) < 0.6f) continue;
                Rock(bank, q, R(0.6f, 1.6f), 0.4f, $"BankRock_{++rocks:000}", collider: PlayBounds.Contains(q));
                if (rng.NextDouble() < 0.35) Rock(bank, q + d * R(0.8f, 1.5f), R(0.3f, 0.6f), 0.4f, $"BankRock_{++rocks:000}", collider: false);
                if (rng.NextDouble() < 0.3 && PlayBounds.Contains(q)) Flower(H.Flowers, p + nrm * side * R(5.2f, 6.5f), R(0.7f, 0.9f), avoidRoads: true);

                // Rocky banks where the stream comes out of the hills (north) and leaves the valley (south).
                if ((p.y > 46f || p.y < -52f) && i % 8 == 0)
                {
                    foreach (int s in new[] { -1, 1 })
                    {
                        Vector2 cp = p + nrm * s * R(6.5f, 8.5f);
                        var cl = Cliff(H.Cliffs, cp, R(0.4f, 0.65f), YawFromDir(d) + R(-25f, 25f), R(1.2f, 2.6f), $"StreamCliff_{++cliffs:00}");
                        if (cl != null && !PlayBounds.Contains(cp)) SetCollider(cl, ColliderMode.None);
                    }
                }
            }
            // Pond shore.
            for (int k = 0; k < 14; k++)
            {
                float a = k / 14f * Mathf.PI * 2f + R(-0.15f, 0.15f);
                Vector2 q = PondC + Dir(a) * (PondExtra + R(3.6f, 4.6f));
                if (RoadEdge(q.x, q.y) < 0.6f || StreamDist(q.x, q.y) < 3.2f) continue;
                Rock(bank, q, R(0.5f, 1.3f), 0.4f, $"PondRock_{++rocks:000}", collider: true);
            }

            // Beached boat, a bench looking over the pond, a crate of the fisherman's things.
            Vector2 boatP = PondC + DirDeg(-25f) * (PondExtra + 4.2f);
            var boat = Prop("Boat", H.ResDecor, boatP, YawFromDir(DirDeg(65f)), 0.8f, ColliderMode.Keep, "PondBoat", sink: 0.35f);
            CrateStack(H.ResDecor, boatP + DirDeg(40f) * 2.6f, 2, 30f);
            Vector2 benchP = PondC + DirDeg(150f) * (PondExtra + 6.2f);
            Bench(H.ResDecor, benchP, PondC, "PondBench");
            TreeAt(H.Trees, benchP + DirDeg(150f) * 2.8f, "Tree_3", 0.9f, true);

            if (bridge != null)
            {
                foreach (int s in new[] { -1, 1 })
                {
                    Vector2 e = bridge.center + bridge.axis * s * 7.6f + new Vector2(bridge.axis.y, -bridge.axis.x) * s * 2.9f;
                    Lantern(H.Bridges, e, bridge.center, "BridgeLantern", force: true);
                    RockPile(H.Bridges, bridge.center + bridge.axis * s * 6.8f - new Vector2(bridge.axis.y, -bridge.axis.x) * s * 3.4f, 3, 0.9f);
                }
            }
            Log($"Stream dressing: {rocks} bank rocks, {cliffs} cliff pieces, boat + bench at the pond.");
        }

        // ================================================================== nature

        static readonly Vector2[] VillageTreeSpots =
        {
            V(-14f, 31f), V(-21f, 34.5f), V(-38f, 34f), V(-48f, 37f), V(-50f, 6f), V(-46f, -12f), V(-48f, -31f), V(-31.5f, -34f),
            V(-28f, -42f), V(-17f, -43f), V(-6f, -43.5f), V(4f, -38f), V(9f, -26f), V(14f, -31f), V(18f, -39f), V(9f, -44f),
            V(25f, -42f), V(33.5f, -13f), V(33.5f, 0f), V(33f, 27f), V(37f, 34f), V(47f, 33f), V(50f, 2f), V(50f, -22f), V(46f, -43f),
            V(30f, 45f), V(10f, 46f), V(-12f, 46f), V(-26f, 44f), V(-51f, 44f), V(-23f, 9.5f + 6f), V(13f, 13.5f), V(-15f, -4f),
            V(-27.5f, 2.5f), V(10f, -20f), V(24.5f, 18f), V(-44f, 12f), V(15f, 33.5f), V(-9f, -46f), V(-3f, 33f),
        };
        static readonly string[] Deciduous = { "Tree_1", "Tree_2", "Tree_3", "Tree_4", "Tree_5", "Tree_6", "Tree_7", "Tree_8" };
        static readonly string[] Pines = { "Pine Tree_1", "Pine Tree_2", "Pine Tree_3", "Pine Tree_4", "Pine Tree_5" };

        static void BuildNature(List<HouseInfo> houses)
        {
            int inner = 0, outer = 0, skipped = 0;
            BuildOrchard();
            foreach (var p in VillageTreeSpots)
            {
                if (!TreeSpotFree(p, houses, 2.6f)) { skipped++; continue; }
                string kind = rng.NextDouble() < 0.78 ? Deciduous[rng.Next(Deciduous.Length)] : Pines[rng.Next(Pines.Length)];
                TreeAt(H.Trees, p, kind, R(0.75f, 1.05f), true);
                inner++;
            }

            // Forest belts and copses on the surrounding hills (clustered, with clearings).
            var forest = Child(H.Trees, "Forest");
            float step = 6.5f;
            for (float x = -122f; x <= 122f; x += step)
                for (float z = -122f; z <= 122f; z += step)
                {
                    Vector2 p = new Vector2(x + R(-2.5f, 2.5f), z + R(-2.5f, 2.5f));
                    float r = p.magnitude;
                    if (r < 47f) continue;
                    float mask = Fbm(p.x * 0.018f + 50f, p.y * 0.018f + 70f);
                    float need = Mathf.Lerp(0.56f, 0.38f, Smooth01(60f, 110f, r));
                    if (mask < need) continue;
                    if (StreamDist(p.x, p.y) < 9f || RoadEdge(p.x, p.y) < 2f) continue;
                    bool inPlay = PlayBounds.Contains(p);
                    if (inPlay && !TreeSpotFree(p, houses, 3f)) continue;
                    float hgt = GroundY(p.x, p.y);
                    bool pine = (hgt > 7f && rng.NextDouble() < 0.8) || rng.NextDouble() < 0.3;
                    string kind = pine ? Pines[rng.Next(Pines.Length)] : Deciduous[rng.Next(Deciduous.Length)];
                    TreeAt(forest, p, kind, R(0.85f, 1.45f), inPlay || PlayBounds.Contains(p + Vector2.one * 3f) || PlayBounds.Contains(p - Vector2.one * 3f));
                    outer++;
                }
            Log($"Trees: {inner} hand-placed in the village ({skipped} spots skipped as blocked), {outer} in forests / copses.");

            // Rock clusters in and around the village.
            Vector2[] clusters =
            {
                V(-27f, -8f), V(-46f, 9f), V(-16f, 39f), V(26f, 33f), V(49f, 0f), V(46f, -30f), V(-35f, -44f), V(12f, -34f),
                V(-45f, 41f), V(29f, -45f), V(-50f, -22f), V(10f, 42f), V(-24f, 14f + 14f), V(3f, -46f),
            };
            var rc = Child(H.Rocks, "Clusters");
            int ncl = 0;
            foreach (var c in clusters)
            {
                if (!TreeSpotFree(c, houses, 2.5f)) continue;
                var g = Child(rc, $"RockCluster_{++ncl:00}");
                Rock(g, c, R(1.5f, 2.3f), 0.35f, "Boulder", collider: true);
                int m = rng.Next(2, 5);
                for (int i = 0; i < m; i++) Rock(g, c + Dir(R(0, 6.28f)) * R(1.3f, 2.2f), R(0.7f, 1.2f), 0.35f, "Rock", collider: true);
                int s = rng.Next(3, 6);
                for (int i = 0; i < s; i++) Rock(g, c + Dir(R(0, 6.28f)) * R(1.8f, 3.2f), R(0.25f, 0.5f), 0.4f, "Stone", collider: false);
                if (rng.NextDouble() < 0.6) for (int i = 0; i < 3; i++) Flower(g, c + Dir(R(0, 6.28f)) * R(1.5f, 2.6f), R(0.7f, 0.9f), avoidRoads: true);
            }
            // Boulder groups on the hillsides and at forest edges (grouped, never sprinkled).
            var hillRocks = Child(H.Rocks, "Hills");
            int hr = 0, tries = 0;
            while (hr < 22 && tries++ < 400)
            {
                Vector2 c = Dir(R(0, 6.28f)) * R(54f, 112f);
                if (StreamDist(c.x, c.y) < 9f || RoadEdge(c.x, c.y) < 3f || BaseHeight(c.x, c.y) < 2.5f) continue;
                if (PlayBounds.Contains(c) && !TreeSpotFree(c, houses, 3f)) continue;
                var g = Child(hillRocks, $"HillRocks_{++hr:00}");
                bool col = PlayBounds.Contains(c);
                Rock(g, c, R(1.8f, 3.4f), 0.4f, "Boulder", col);
                int m = rng.Next(2, 5);
                for (int i = 0; i < m; i++) Rock(g, c + Dir(R(0, 6.28f)) * R(1.8f, 3.5f), R(0.7f, 1.6f), 0.35f, "Rock", col);
                for (int i = 0; i < 4; i++) Rock(g, c + Dir(R(0, 6.28f)) * R(2.5f, 5f), R(0.3f, 0.6f), 0.4f, "Stone", false);
            }

            // Cliff outcrops on the hills (not toward the open south-east valley).
            int co = 0;
            for (float a = 0f; a < 360f; a += 21f)
            {
                float ang = a + R(-7f, 7f);
                if (ang > 285f && ang < 345f) continue;
                Vector2 p = DirDeg(ang) * R(70f, 100f);
                if (StreamDist(p.x, p.y) < 10f || BaseHeight(p.x, p.y) < 6f) continue;
                var cl = Cliff(H.Cliffs, p, R(0.8f, 1.4f), R(0, 360), R(1.6f, 3.8f), $"HillOutcrop_{++co:00}");
                if (cl != null && !PlayBounds.Contains(p)) SetCollider(cl, ColliderMode.None);
                if (rng.NextDouble() < 0.6) Cliff(H.Cliffs, p + Dir(R(0, 6.28f)) * R(6f, 9f), R(0.45f, 0.8f), R(0, 360), R(1.0f, 2.2f), $"HillOutcrop_{++co:00}");
            }
            // Small rocky knoll west of the vineyard.
            Cliff(H.Cliffs, V(-33f, -38.5f), 0.42f, 30f, 1.6f, "Knoll_A");
            Cliff(H.Cliffs, V(-37.5f, -35f), 0.36f, 140f, 1.2f, "Knoll_B");
            Log($"Rocks: {ncl} village clusters, {hr} hillside boulder groups; cliffs: {co} hill outcrops + knoll.");
        }

        // ================================================================== orchard / fields / retaining stones

        static readonly (Rect rect, bool rowsAlongX, string name)[] Fields =
        {
            (Rect.MinMaxRect(43.5f, -28f, 50.5f, -17.5f), true, "Field_HamletGarden"),
            (Rect.MinMaxRect(-50.5f, -37f, -42.5f, -27.5f), false, "Field_FarmPlot"),
        };

        static void BuildOrchard()
        {
            var orchard = Child(H.Vegetation, "Orchard_EastBank");
            int n = 0;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 4; j++)
                {
                    Vector2 p = V(38.5f + i * 4.2f + R(-0.3f, 0.3f), 29.5f + j * 4.2f + R(-0.3f, 0.3f));
                    if (StreamDist(p.x, p.y) < 7f || !PlayBounds.Contains(p)) continue;
                    TreeAt(orchard, p, rng.NextDouble() < 0.5 ? "Tree_1" : "Tree_7", R(0.5f, 0.62f), true);
                    n++;
                }
            CrateStack(orchard, V(36.8f, 31f), 3, 80f);
            CrateStack(orchard, V(36.6f, 33.2f), 1, 95f);
            Bench(orchard, V(36.4f, 37.5f), V(44f, 37.5f), "OrchardBench");
            Lantern(orchard, V(36.2f, 27.4f), V(44f, 33f), "OrchardLantern");
            Log($"Orchard: {n} fruit trees in rows on the east bank.");
        }

        static void DressFields()
        {
            foreach (var f in Fields)
            {
                var g = Child(H.ResDecor, f.name);
                var r = f.rect;
                bool east = r.center.x > 0;
                if (east)
                {
                    // Kitchen garden by the hamlet cottage: fenced, gate toward the lane, flower rows between furrows.
                    FenceLine(g, "Fence", new[] { V(r.xMin, r.yMax), V(r.xMax, r.yMax), V(r.xMax, r.yMin), V(r.xMin, r.yMin) }, null);
                    FenceLine(g, "FenceGate", new[] { V(r.xMin, r.yMin), V(r.xMin, r.yMax) }, (r.center.y - 1.6f, r.center.y + 1.6f, false));
                    for (float z = r.yMin + 0.75f; z < r.yMax - 0.5f; z += 1.5f)
                        for (float x = r.xMin + 0.9f; x < r.xMax - 0.6f; x += R(0.8f, 1.2f))
                            if (rng.NextDouble() < 0.6) Flower(g, V(x, z + 0.75f), R(0.6f, 0.8f));
                    CrateStack(g, V(r.xMin + 1.2f, r.yMax + 1.0f), 2, 0f);
                }
                else
                {
                    // Tilled plot by the farmhouse.
                    FenceLine(g, "Fence", new[] { V(r.xMax, r.yMin), V(r.xMin, r.yMin), V(r.xMin, r.yMax), V(r.xMax, r.yMax) }, null);
                    FenceLine(g, "FenceGate", new[] { V(r.xMax, r.yMax), V(r.xMax, r.yMin) }, (r.center.y - 1.6f, r.center.y + 1.6f, false));
                    CrateStack(g, V(r.xMax + 1.1f, r.yMax - 1.0f), 2, 90f);
                    PartProp(g, "Decor_FieldBucket", PressPath, "Press_Bucket", V(r.xMax + 1.0f, r.yMax - 2.6f), R(0, 360), 1.6f, false);
                    RockPile(g, V(r.xMin - 1.4f, r.yMin - 1.2f), 6, 1.2f);
                }
                blockers.Add((r.center, 0f));
            }
            Log($"Fields: {Fields.Length} fenced plots (kitchen garden by the hamlet cottage, tilled plot by the farmhouse).");
        }

        static void RetainingStones(List<HouseInfo> houses)
        {
            int n = 0;
            var root = Child(H.ResDecor, "RetainingStones");
            foreach (var h in houses)
            {
                float pad = h.go.transform.position.y + 0.08f;
                for (int side = 0; side < 4; side++)
                {
                    Vector2 axis = side < 2 ? h.Right : h.Forward;
                    Vector2 along = side < 2 ? h.Forward : h.Right;
                    float sgn = side % 2 == 0 ? 1f : -1f;
                    float off = (side < 2 ? h.half.x : h.half.y) + 0.9f;
                    float len = side < 2 ? h.half.y : h.half.x;
                    for (float t = -len; t <= len; t += 0.95f)
                    {
                        if (side == 2 && Mathf.Abs(t) < 1.8f) continue;            // keep the door clear
                        Vector2 p = h.center + axis * sgn * off + along * t;
                        if (RoadEdge(p.x, p.y) < 0.4f) continue;
                        float drop = pad - GroundY(p.x + axis.x * sgn * 1.2f, p.y + axis.y * sgn * 1.2f);
                        if (drop < 0.3f) continue;
                        Rock(root, p, Mathf.Clamp(0.45f + drop * 0.5f, 0.45f, 1.1f), 0.35f, $"Retaining_{++n:000}", collider: true);
                    }
                }
            }
            Log($"Retaining stones: {n} placed where building pads sit above falling ground.");
        }

        static bool TreeSpotFree(Vector2 p, List<HouseInfo> houses, float r)
        {
            if (houses.Any(h => h.ObbDistance(p) < r)) return false;
            if (RoadEdge(p.x, p.y) < r * 0.8f) return false;
            if (PlazaSignedDist(p) < r) return false;
            if (CourtEllipse(p) < 1.25f) return false;
            if (RectDist(VineyardRect, p) < r) return false;
            foreach (var f in Fields) if (RectDist(f.rect, p) < r) return false;
            if (StreamDist(p.x, p.y) < 5f + r * 0.5f) return false;
            if (blockers.Any(b => Vector2.Distance(b.p, p) < b.r + r * 0.6f)) return false;
            if (SpawnCandidates.Any(s => Vector2.Distance(s, p) < 3f)) return false;
            return true;
        }

        static void BuildMountains()
        {
            (string prefab, float ang, float dist, float scale, float yaw, float sink)[] list =
            {
                ("Mountain_2", 98f, 195f, 2.3f, 20f, 6f),
                ("Mountain_4", 138f, 165f, 1.8f, 70f, 5f),
                ("Mountain_3", 120f, 128f, 1.05f, 160f, 4f),
                ("Mountain_1", 176f, 205f, 2.6f, 150f, 6f),
                ("Mountain_3", 214f, 175f, 1.7f, 200f, 5f),
                ("Mountain_4", 262f, 235f, 2.7f, 300f, 6f),
                ("Mountain_1", 60f, 215f, 2.1f, 40f, 6f),
                ("Mountain_3", 22f, 255f, 2.9f, 120f, 8f),
                ("Mountain_2", 318f, 275f, 3.3f, 10f, 8f),
                ("Mountain_1", 236f, 150f, 1.2f, 220f, 4f),
                ("Mountain_2", 158f, 140f, 1.0f, 95f, 4f),
                ("Mountain_4", 148f, 265f, 3.0f, 15f, 8f),
                ("Mountain_2", 80f, 285f, 3.2f, 250f, 8f),
                ("Mountain_3", 196f, 290f, 3.0f, 60f, 8f),
            };
            int i = 0;
            foreach (var m in list)
            {
                var pf = LoadPack(m.prefab);
                if (pf == null) continue;
                Vector2 p = DirDeg(m.ang) * m.dist;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(pf, H.Mountains);
                go.name = $"{m.prefab}_{++i:00}";
                go.transform.SetPositionAndRotation(new Vector3(p.x, TerrainBaseY + 2f - m.sink, p.y), Quaternion.Euler(0, m.yaw, 0) * pf.transform.rotation);
                go.transform.localScale = Vector3.one * m.scale;
                SetCollider(go, ColliderMode.None);
                var mr = go.GetComponentInChildren<MeshRenderer>();
                if (mr) mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            Log($"Mountains: {i} placed at 128-290 m, scales 1.0-3.3, no colliders, no shadow casting.");
        }

        static void BuildMapBounds()
        {
            float h = 40f;
            void Wall(string n, Vector3 pos, Vector3 size)
            {
                var w = new GameObject(n);
                w.transform.SetParent(H.Bounds, false);
                w.transform.position = pos;
                w.AddComponent<BoxCollider>().size = size;
            }
            var b = PlayBounds;
            Wall("Bound_North", new Vector3(b.center.x, h / 2, b.yMax), new Vector3(b.width + 2, h, 1));
            Wall("Bound_South", new Vector3(b.center.x, h / 2, b.yMin), new Vector3(b.width + 2, h, 1));
            Wall("Bound_East", new Vector3(b.xMax, h / 2, b.center.y), new Vector3(1, h, b.height + 2));
            Wall("Bound_West", new Vector3(b.xMin, h / 2, b.center.y), new Vector3(1, h, b.height + 2));
            Log($"Invisible map bounds: x {b.xMin}..{b.xMax}, z {b.yMin}..{b.yMax}.");
        }
    }
}
