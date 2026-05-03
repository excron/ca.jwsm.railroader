using HarmonyLib;
using RollingStock.Diesel;
using UnityEngine;

namespace Ca.Jwsm.Railroader.Experiments.DieselExhaustVfx
{
    /// <summary>
    /// Postfix on DieselExhaustParticleController.OnEnable that attaches a
    /// flickering orange Point Light at the visualEffect's transform, so the
    /// fire/exhaust appears to actually illuminate the locomotive and
    /// surrounding scene at night. Cheap fake — one light per stack.
    /// </summary>
    [HarmonyPatch(typeof(DieselExhaustParticleController), "OnEnable")]
    public static class ExhaustPointLightPatch
    {
        // Warm orange, evoking combustion glow.
        private static readonly Color LightColor = new Color(1.0f, 0.45f, 0.15f);

        // How far the light reaches in meters.
        private const float LightRange = 6f;

        private const string ChildName = "ExhaustFlickerLight";

        [HarmonyPostfix]
        public static void Postfix(DieselExhaustParticleController __instance)
        {
            if (__instance == null || __instance.visualEffect == null) return;

            // Idempotency: don't add duplicate lights if OnEnable fires more
            // than once for the same controller (e.g., disable/re-enable cycles).
            var existing = __instance.visualEffect.transform.Find(ChildName);
            if (existing != null) return;

            var lightGo = new GameObject(ChildName);
            lightGo.transform.SetParent(__instance.visualEffect.transform, worldPositionStays: false);
            lightGo.transform.localPosition = Vector3.zero;

            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = LightColor;
            light.range = LightRange;
            light.intensity = 0f; // ExhaustFlicker drives this from UMM settings each frame.
            light.shadows = LightShadows.None; // Cheap — no shadow casting.
            light.renderMode = LightRenderMode.Auto;

            lightGo.AddComponent<ExhaustFlicker>();
        }
    }
}
