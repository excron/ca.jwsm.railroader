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
        private const string BrakeGlowBundleFileName = "brakeglow.bundle";

        public static UnityModManager.ModEntry Mod { get; private set; }
        public static VisualEffectAsset ReplacementAsset { get; private set; }
        public static Shader EmissionOverlayShader { get; private set; }
        public static ExperimentSettings Settings { get; private set; }

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;

            Settings = UnityModManager.ModSettings.Load<ExperimentSettings>(modEntry);
            modEntry.OnGUI = e => Settings.Draw(e);
            modEntry.OnSaveGUI = e => Settings.Save(e);

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

            // Optional: emission overlay shader. Missing bundle is non-fatal —
            // brake-shoe emission overlays simply won't be created without it.
            try
            {
                var modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var bundlePath = Path.Combine(modDir, "Assets", BrakeGlowBundleFileName);

                if (File.Exists(bundlePath))
                {
                    var bundle = AssetBundle.LoadFromFile(bundlePath);
                    if (bundle != null)
                    {
                        var shaders = bundle.LoadAllAssets<Shader>();
                        if (shaders.Length > 0)
                        {
                            EmissionOverlayShader = shaders[0];
                            modEntry.Logger.Log($"Loaded emission overlay shader: {EmissionOverlayShader.name}");
                        }
                        else
                        {
                            var available = string.Join(", ", bundle.GetAllAssetNames());
                            modEntry.Logger.Warning($"No Shader found in {BrakeGlowBundleFileName}. Available: {available}");
                        }
                    }
                    else
                    {
                        modEntry.Logger.Warning($"Failed to load AssetBundle at {bundlePath}.");
                    }
                }
                else
                {
                    modEntry.Logger.Log($"{BrakeGlowBundleFileName} not present — brake-shoe overlays disabled. Build via Tools > Build Brake Glow Bundle to enable.");
                }
            }
            catch (Exception ex)
            {
                modEntry.Logger.Warning($"Emission overlay shader load failed (non-fatal): {ex}");
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
