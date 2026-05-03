using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.VFX;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.DieselExhaustSmokeVfx
{
    /// <summary>
    /// Lean experiment scaffold: load a single VFX bundle and Harmony-patch
    /// DieselExhaustParticleController.OnEnable to swap the controller's
    /// VisualEffectAsset for ours. No UMM settings, no extra rendering
    /// systems — just the smoke variant.
    /// </summary>
    public static class ExperimentEntry
    {
        private const string BundleFileName = "dieselsmoke.bundle";

        public static UnityModManager.ModEntry Mod { get; private set; }
        public static VisualEffectAsset ReplacementAsset { get; private set; }

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;

            try
            {
                var modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var bundlePath = Path.Combine(modDir, "Assets", BundleFileName);

                if (!File.Exists(bundlePath))
                {
                    modEntry.Logger.Error($"Bundle not found at {bundlePath}. Build it from the Unity authoring project (Tools > Build Diesel Exhaust Smoke Bundle).");
                    return false;
                }

                var bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    modEntry.Logger.Error($"Failed to load AssetBundle at {bundlePath}.");
                    return false;
                }

                var allVfxAssets = bundle.LoadAllAssets<VisualEffectAsset>();
                if (allVfxAssets.Length == 0)
                {
                    var available = string.Join(", ", bundle.GetAllAssetNames());
                    modEntry.Logger.Error($"No VisualEffectAsset found in bundle. Available: {available}");
                    return false;
                }

                ReplacementAsset = allVfxAssets[0];
                modEntry.Logger.Log($"Loaded replacement VFX: {ReplacementAsset.name} (out of {allVfxAssets.Length} in bundle)");
            }
            catch (Exception ex)
            {
                modEntry.Logger.Error($"Load failed: {ex}");
                return false;
            }

            try
            {
                var harmony = new Harmony(modEntry.Info.Id);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                modEntry.Logger.Log("Harmony patches applied.");
            }
            catch (Exception ex)
            {
                modEntry.Logger.Error($"Harmony patch failed: {ex}");
                return false;
            }

            return true;
        }
    }
}
