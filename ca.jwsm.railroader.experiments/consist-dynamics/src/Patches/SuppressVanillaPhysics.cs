using HarmonyLib;
using Model;
using UnityEngine;

namespace Ca.Jwsm.Railroader.Experiments.ConsistDynamics.Patches
{
    // -------------------------------------------------------------------
    // The three suppression patches.
    //
    // Each patch returns false from prefix → vanilla method body skipped.
    // Per Harmony 2 semantics, postfixes still run when prefix returns
    // false; we exploit that on TrainController.FixedUpdate to drive our
    // own solver on the same fixed-timestep pulse vanilla would have used.
    //
    // For Car.FixedUpdate and BaseLocomotive.FixedUpdate we have no
    // postfix — those methods are pure suppression. Per-car/per-loco work
    // (anglecocks, brake visuals, mover ticks, wheel slip, cab controls)
    // is replaced by us if/when we want it back.
    // -------------------------------------------------------------------

    [HarmonyPatch(typeof(TrainController), "FixedUpdate")]
    internal static class TrainControllerFixedUpdatePatch
    {
        private static bool Prefix() => false;

        private static void Postfix(TrainController __instance)
        {
            Driver.ConsistDriver.Tick(__instance, Time.fixedDeltaTime);
        }
    }

    [HarmonyPatch(typeof(Car), "FixedUpdate")]
    internal static class CarFixedUpdatePatch
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(BaseLocomotive), "FixedUpdate")]
    internal static class BaseLocomotiveFixedUpdatePatch
    {
        private static bool Prefix() => false;
    }
}
