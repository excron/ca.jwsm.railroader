using HarmonyLib;
using RollingStock.Diesel;

namespace Ca.Jwsm.Railroader.Experiments.DieselExhaustVfx
{
    /// <summary>
    /// Swaps the prefab-baked VisualEffectAsset on a diesel exhaust controller
    /// for our custom-authored asset, before the controller wraps it for
    /// property driving. Falls back silently to vanilla if our asset failed
    /// to load.
    /// </summary>
    [HarmonyPatch(typeof(DieselExhaustParticleController), "OnEnable")]
    public static class DieselExhaustControllerPatch
    {
        [HarmonyPrefix]
        public static void Prefix(DieselExhaustParticleController __instance)
        {
            var replacement = ExperimentEntry.ReplacementAsset;
            if (replacement == null) return;
            if (__instance.visualEffect == null) return;

            __instance.visualEffect.visualEffectAsset = replacement;
        }
    }
}
