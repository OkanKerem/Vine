# Performance

Tooling: `Assets/_Project/Editor/VillagePerformance.cs` (menu **Tools > Vineyard > Performance**).

1. **Capture Baseline (Play Mode)**: static scene stats plus runtime counters (FPS, frame ms, batches,
   SetPass calls, triangles, shadow casters, main-thread / GPU time, memory) from 4 fixed views.
   Output: `Logs/Performance/Baseline.txt`.
2. **Apply Optimizations**: backs up the scene (`Scenes/Backups/Village_BeforePerfOpt_*`) and the settings
   files (`Logs/Performance/Backup_*`), then applies the changes below. Idempotent.
3. **Capture After Optimization (Play Mode)**: writes `After.txt` and `Comparison.txt`.

Use the same machine, Game view size and quality tier for both captures, and keep the Scene view hidden.

## What gets changed

| Area | Change |
|---|---|
| Import (.meta only) | Mesh LOD generation on Grapevine, GrapeBunch, GiantWineTank, SmallGrapePress, SmallFermentationStation |
| URP PC asset | 4 → 2 shadow cascades (first split 10 m), soft shadows High → Medium, opaque texture off |
| PC renderer | SSAO half resolution, depth source (no depth-normals pre-pass) |
| Quality / Player | PC tier V-Sync on, Metal API validation off, frame timing stats on |
| Scene | grape bunch shadows off; tiny/flat decoration shadows off; cull-only LODGroups on small props (≤1.3 m); Crate/Flower Pot MeshCollider → BoxCollider; colliders outside the walkable bounds disabled; terrain detail distance 75 → 60 m, pixel error 3 → 4 |
| Scripts | `GrapevineController` skips transform writes for settled bunches/foliage; `WineryHUD` caches its camera |

The Village builder re-applies the scene changes after every rebuild.

## Still needs Blender

- **SmallGrapePress.fbx**: 34.8k tris for a 1.2 m prop (8 parts, up to 5 materials each). Target 3–5k.
- **GiantWineTank.fbx**: 31.3k tris, 30 renderers, 12 materials. Merge the non-interactive details (bolts,
  emblem, bands, flanges) into the support mesh; keep funnel / pipe / tap / glass / liquid / cork separate.
- **Material atlasing**: every custom model uses several flat-colour URP/Lit materials (vine 6, bunch 3, press 5,
  fermenter 4+, tank 12). One palette texture per model would cut the vineyard from about 420 sub-mesh draws (20 vines × 6 + 100 bunches × 3) to about 120.
- Hand-made LODs for the vine leaves, if the automatic Mesh LOD silhouettes are not good enough at mid range.
