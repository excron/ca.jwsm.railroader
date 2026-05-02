using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.VFX;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.DieselExhaustVfx
{
    public static class ExperimentEntry
    {
        private const string BundleFileName = "dieselexhaust.bundle";
        private const string AssetName = "DieselExhaust";

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
                    modEntry.Logger.Error($"Bundle not found at {bundlePath}. Build it from the Unity authoring project (Tools > Build Diesel Exhaust Bundle).");
                    return false;
                }

                var bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    modEntry.Logger.Error($"Failed to load AssetBundle at {bundlePath}.");
                    return false;
                }

                ReplacementAsset = bundle.LoadAsset<VisualEffectAsset>(AssetName);
                if (ReplacementAsset == null)
                {
                    var available = string.Join(", ", bundle.GetAllAssetNames());
                    modEntry.Logger.Error($"VisualEffectAsset '{AssetName}' not found in bundle. Available: {available}");
                    return false;
                }

                modEntry.Logger.Log($"Loaded replacement VFX: {ReplacementAsset.name}");
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
