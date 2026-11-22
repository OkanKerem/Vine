using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// Spawning store-bought particle prefabs at winery size. Those packs are authored for big scenes, and their
    /// systems use different scaling modes, so scaling the transform alone is unreliable (gravity, speeds and
    /// sizes would not shrink together). Instead every system's sizes, speeds, forces and shape are multiplied.
    /// </summary>
    public static class FxUtil
    {
        /// <summary>Instantiates <paramref name="prefab"/> (keeping its authored rotation) and scales all its particle systems by <paramref name="k"/>.</summary>
        public static GameObject Spawn(GameObject prefab, Vector3 position, Transform parent, float k)
        {
            if (!prefab) return null;
            var go = Object.Instantiate(prefab, position, prefab.transform.rotation, parent);
            go.name = prefab.name + " (runtime)";
            Scale(go, k);
            return go;
        }

        /// <summary>Seconds until every system of <paramref name="go"/> has finished (for one-shot effects).</summary>
        public static float Lifetime(GameObject go)
        {
            float t = 0f;
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var m = ps.main;
                t = Mathf.Max(t, m.startDelay.constantMax + m.duration + m.startLifetime.constantMax);
            }
            return t + 0.2f;
        }

        public static void Scale(GameObject go, float k)
        {
            if (Mathf.Approximately(k, 1f)) return;
            foreach (var tr in go.GetComponentsInChildren<Transform>(true))
                if (tr != go.transform) tr.localPosition *= k;
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                bool playing = ps.isPlaying;
                if (playing) ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);

                var m = ps.main;
                m.scalingMode = ParticleSystemScalingMode.Local;   // values below are already final
                m.startSizeMultiplier *= k;
                if (m.startSize3D) { m.startSizeXMultiplier *= k; m.startSizeYMultiplier *= k; m.startSizeZMultiplier *= k; }
                m.startSpeedMultiplier *= k;
                m.gravityModifierMultiplier *= k;

                var sh = ps.shape;
                sh.radius *= k; sh.length *= k; sh.position *= k; sh.scale *= k;
                sh.boxThickness *= k;

                var v = ps.velocityOverLifetime;
                if (v.enabled)
                {
                    v.xMultiplier *= k; v.yMultiplier *= k; v.zMultiplier *= k;
                    v.radialMultiplier *= k;
                    v.orbitalOffsetXMultiplier *= k; v.orbitalOffsetYMultiplier *= k; v.orbitalOffsetZMultiplier *= k;
                }
                var f = ps.forceOverLifetime;
                if (f.enabled) { f.xMultiplier *= k; f.yMultiplier *= k; f.zMultiplier *= k; }
                var lv = ps.limitVelocityOverLifetime;
                if (lv.enabled) { lv.limitMultiplier *= k; lv.limitXMultiplier *= k; lv.limitYMultiplier *= k; lv.limitZMultiplier *= k; }
                var n = ps.noise;
                if (n.enabled) { n.strengthMultiplier *= k; n.strengthXMultiplier *= k; n.strengthYMultiplier *= k; n.strengthZMultiplier *= k; }

                if (playing) ps.Play(false);
            }
            foreach (var tr in go.GetComponentsInChildren<TrailRenderer>(true)) tr.widthMultiplier *= k;
        }
    }
}
