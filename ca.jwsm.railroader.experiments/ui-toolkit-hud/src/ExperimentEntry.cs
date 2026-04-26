using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.UiToolkitHud
{
    /// <summary>
    /// UMM entry point for the UI Toolkit HUD experiment.
    ///
    /// Experiment phases (visual acceptance gates between each):
    ///   1. Clone vanilla HUD (LocoControlsUI shape) — ENABLED by default
    ///   2. Insert dynamic-brake slider above throttle (between info/controls)
    ///   3. Clone vanilla Inspector (ConsistInspectorPanel)
    ///   4. Add DPU checkbox under MU in inspector cells
    ///
    /// Toggle phases via the bools below as we gate through each.
    /// </summary>
    public static class ExperimentEntry
    {
        // ---- Phase toggles (flip as visual fidelity proves out) ----
        private const bool ShowHudClone           = true;
        private const bool AddDynamicBrakeSlider  = false;  // phase 2
        private const bool ShowInspectorClone     = false;  // phase 3
        private const bool AddDpuCheckbox         = false;  // phase 4

        public static UnityModManager.ModEntry Mod { get; private set; }

        private static Theme _theme;
        private static bool _shown;
        private static GameObject _panelGo;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;

            try
            {
                var modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var themePath = Path.Combine(modDir, "Themes", "charcoal.json");
                _theme = Theme.LoadFromFile(themePath);
                modEntry.Logger.Log($"Loaded theme '{_theme.Name}' from {themePath}");
            }
            catch (System.Exception ex)
            {
                modEntry.Logger.Error($"Failed to load theme: {ex}");
                return false;
            }

            modEntry.OnUpdate = OnUpdate;
            modEntry.Logger.Log("UI Toolkit HUD experiment: loaded. Awaiting scene to attach panel.");
            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            if (_shown) return;

            // Wait until the game has constructed its main camera. That's a
            // crude readiness signal but adequate for an experiment — by the
            // time Camera.main exists, we're past the initial bootstrap and
            // a Canvas-style UI host is available.
            if (Camera.main == null) return;

            try
            {
                ShowExperiment();
                _shown = true;
                modEntry.Logger.Log("UI Toolkit HUD experiment: panel attached.");
            }
            catch (System.Exception ex)
            {
                modEntry.Logger.Error($"Failed to attach experiment panel: {ex}");
                // Mark as shown anyway so we don't retry every frame on a
                // permanent failure.
                _shown = true;
            }
        }

        private static void ShowExperiment()
        {
            // Create a PanelSettings asset at runtime. Defaults match vanilla
            // HUD canvas observed in the dump: ScreenSpaceOverlay-equivalent,
            // ConstantPixelSize-ish via Scale With Screen Size + 1920x1080 ref.
            var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelSettings.name = "ExperimentPanelSettings";
            panelSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panelSettings.referenceResolution = new Vector2Int(1920, 1080);
            panelSettings.match = 0.5f;
            panelSettings.sortingOrder = 100;  // above vanilla's HUD so it's visible

            _panelGo = new GameObject("ExperimentPanelHost");
            Object.DontDestroyOnLoad(_panelGo);
            var doc = _panelGo.AddComponent<UIDocument>();
            doc.panelSettings = panelSettings;

            var root = doc.rootVisualElement;
            root.style.flexGrow = 1;
            root.pickingMode = PickingMode.Ignore;  // don't intercept clicks on empty areas

            if (ShowHudClone)
            {
                var hud = new HudClone(_theme);
                root.Add(hud.Build());
                if (AddDynamicBrakeSlider)
                {
                    hud.AddDynamicBrakeSlider();
                }
            }

            // Phase 3 + 4 land when their toggles flip.
            // if (ShowInspectorClone) { ... }
        }
    }
}
