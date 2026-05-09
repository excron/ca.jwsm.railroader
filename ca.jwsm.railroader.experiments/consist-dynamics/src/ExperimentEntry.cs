using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.ConsistDynamics
{
    /// <summary>
    /// UMM entry. Installs Harmony patches that suppress vanilla physics
    /// wholesale, then steps aside — the driver runs from a Harmony postfix
    /// on TrainController.FixedUpdate (prefix returns false, postfix still
    /// fires per Harmony 2 semantics, giving us a synchronized 50 Hz pulse).
    /// </summary>
    public static class ExperimentEntry
    {
        private const string HarmonyId = "ca.jwsm.railroader.experiments.consist-dynamics";

        public static UnityModManager.ModEntry Mod { get; private set; }

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;

            try
            {
                var harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                modEntry.Logger.Log("Consist Dynamics: Harmony patches installed.");
                modEntry.Logger.Log("Consist Dynamics: vanilla TrainController.FixedUpdate, Car.FixedUpdate, BaseLocomotive.FixedUpdate suppressed.");
                modEntry.Logger.Log("Consist Dynamics: ready. Spawn cars via vanilla spawner; we'll adopt them on the next tick.");
            }
            catch (System.Exception ex)
            {
                modEntry.Logger.Error($"Consist Dynamics: failed to install patches: {ex}");
                return false;
            }

            return true;
        }
    }
}
