using HarmonyLib;
using RollingStock.Diesel;
using UnityEngine;

namespace Ca.Jwsm.Railroader.Experiments.DieselExhaustVfx
{
    /// <summary>
    /// Postfix on DieselExhaustParticleController.SmokeStartColor() that
    /// darkens the color produced by vanilla's profile.colorGradient before
    /// it's passed to our alpha-blended VFX.
    ///
    /// Vanilla's gradient was authored for additive blending, where colors
    /// add to the framebuffer — so the gradient sits in the light-grey/
    /// off-white range. Our alpha-blended output renders those same RGB
    /// values directly, producing visible grey wisps instead of dark clag.
    /// Multiplying the RGB by a small constant pulls the entire range
    /// toward black while preserving the controller's relative variation
    /// (transient pulses, load-based color shifts) intact.
    /// </summary>
    [HarmonyPatch(typeof(DieselExhaustParticleController), "SmokeStartColor")]
    public static class SmokeColorDarkenPatch
    {
        // Tunable. 0.15 = strong clag. 0.3 = moderate. 1.0 = no change.
        private const float DarkenFactor = 0.15f;

        [HarmonyPostfix]
        public static void Postfix(ref Color __result)
        {
            __result = new Color(
                __result.r * DarkenFactor,
                __result.g * DarkenFactor,
                __result.b * DarkenFactor,
                __result.a);
        }
    }
}
