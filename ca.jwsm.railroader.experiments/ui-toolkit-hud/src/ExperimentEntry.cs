using System.IO;
using System.Reflection;
using GalaSoft.MvvmLight.Messaging;
using Game.Settings;
using UnityEngine;
using UnityEngine.InputSystem;
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
        private const bool AddDynamicBrakeSlider  = true;   // phase 2 ← LIVE
        // ShowInspectorClone targets the wrong inspector (ConsistInspectorPanel).
        // Replaced by ShowCarInspectorClone (CarInspectorClone.cs) which clones
        // the actual vanilla vehicle inspector. InspectorClone.cs preserved as
        // a "we built the wrong one" experiment artifact.
        private const bool ShowInspectorClone     = false;
        private const bool ShowCarInspectorClone  = true;   // phase 3 ← LIVE
        private const bool AddDpuToggle           = true;   // phase 4 ← LIVE

        public static UnityModManager.ModEntry Mod { get; private set; }

        private static Theme _theme;
        private static bool _shown;
        private static GameObject _panelGo;
        private static float _appliedScale = -1f;  // sentinel — ensures first apply runs

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
            // First-show: wait until the main camera exists, then attach the
            // experiment panel. UI scale updates after that flow through the
            // CanvasScaleChanged Messenger event, not polling.
            if (!_shown)
            {
                if (Camera.main == null) return;
                try
                {
                    ShowExperiment();
                    _shown = true;
                    modEntry.Logger.Log("UI Toolkit HUD experiment: panel attached.");
                    modEntry.Logger.Log($"Mod Path: {modEntry.Path}");
                    modEntry.Logger.Log($"Keyboard.current: {(Keyboard.current != null ? "available" : "NULL — InputSystem not initialized")}");
                    modEntry.Logger.Log("Press F8 to dump the live UI hierarchy. Press F9 + open CarInspector first to dump just that.");
                }
                catch (System.Exception ex)
                {
                    modEntry.Logger.Error($"Failed to attach experiment panel: {ex}");
                    _shown = true;  // don't retry on a permanent failure
                }
            }

            // Hotkeys for the runtime dumper. Uses the new InputSystem
            // (legacy UnityEngine.Input is often a no-op when the new
            // system is the active handler). Cheap — one bool check per
            // frame, not a poll on shared state.
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.f8Key.wasPressedThisFrame)
            {
                modEntry.Logger.Log("F8 pressed — dumping full scene + DDOL...");
                RuntimeDumper.DumpAll(modEntry);
            }
            else if (kb.f9Key.wasPressedThisFrame)
            {
                modEntry.Logger.Log("F9 pressed — dumping any GameObject with 'inspect' in its name...");
                RuntimeDumper.DumpByName(modEntry, "inspect");
            }
            else if (kb.f10Key.wasPressedThisFrame)
            {
                modEntry.Logger.Log("F10 pressed — dumping CarInspector by component type...");
                RuntimeDumper.DumpByComponent<UI.CarInspector.CarInspector>(modEntry, "carinspector");
            }
        }

        /// <summary>
        /// Tear down the existing panel content and rebuild with the current
        /// UI scale. Called on initial show and whenever vanilla fires
        /// CanvasScaleChanged (i.e., the player adjusted the UI Scale slider).
        /// </summary>
        private static void RebuildPanel()
        {
            if (_panelGo == null) return;
            var doc = _panelGo.GetComponent<UIDocument>();
            if (doc == null) return;

            _appliedScale = ReadGameUIScale();
            Mod.Logger.Log($"Rebuilding panel at UI scale: {_appliedScale:0.##}x");

            var root = doc.rootVisualElement;
            root.Clear();

            // Re-apply font (panel root was cleared)
            var font = LoadFallbackFont();
            if (font != null) root.style.unityFont = new StyleFont(font);

            if (ShowHudClone)
            {
                var hud = new HudClone(_theme, _appliedScale);
                root.Add(hud.Build());
                if (AddDynamicBrakeSlider) hud.AddDynamicBrakeSlider();
            }

            if (ShowInspectorClone)
            {
                var inspector = new InspectorClone(_theme, _appliedScale);
                root.Add(inspector.Build());
            }

            if (ShowCarInspectorClone)
            {
                var carInspector = new CarInspectorClone(_theme, _appliedScale);
                root.Add(carInspector.Build());
                if (AddDpuToggle) carInspector.AddDpuToggle();
            }
        }

        private static void ShowExperiment()
        {
            // Create a PanelSettings asset at runtime. Match vanilla's HUD
            // canvas observed in the dump: ConstantPixelSize so our specified
            // pixel values render 1:1 regardless of screen resolution.
            var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelSettings.name = "ExperimentPanelSettings";
            panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
            panelSettings.referenceDpi = 96;
            panelSettings.fallbackDpi = 96;
            panelSettings.sortingOrder = 100;  // above vanilla's HUD so it's visible

            _panelGo = new GameObject("ExperimentPanelHost");
            Object.DontDestroyOnLoad(_panelGo);
            var doc = _panelGo.AddComponent<UIDocument>();
            doc.panelSettings = panelSettings;

            doc.rootVisualElement.style.flexGrow = 1;
            doc.rootVisualElement.pickingMode = PickingMode.Ignore;

            // Subscribe to vanilla's UI scale change event. The game fires
            // this from CanvasSettingsApplicator whenever the player changes
            // the UI Scale slider in Preferences. Event-driven; no polling.
            Messenger.Default.Register<CanvasScaleChanged>(_panelGo, _ => RebuildPanel());

            // Initial render. RebuildPanel reads ReadGameUIScale() each call
            // so it picks up whatever's set right now.
            RebuildPanel();
        }

        /// <summary>
        /// Reads the game's current UI scale directly from the PlayerPrefs key
        /// the game uses internally. Confirmed via ILSPY:
        ///   Preferences.GraphicsCanvasScale → PlayerPrefs.GetFloat("gfx.canvas.scale", 1f)
        /// This is the source of truth — Canvas instances only get this value
        /// applied if they have a CanvasSettingsApplicator component, which
        /// not every canvas (including Canvas - HUD) necessarily has.
        /// </summary>
        private static float ReadGameUIScale()
        {
            return PlayerPrefs.GetFloat("gfx.canvas.scale", 1f);
        }

        private static Font LoadFallbackFont()
        {
            // Try built-in first (common case).
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                       ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font != null) return font;

            // Try any Font asset already loaded by the game (vanilla pulls
            // in TMP fonts but TMP_FontAsset isn't a Font; this only finds
            // legacy Unity Font assets if the game ships any).
            var loaded = Resources.FindObjectsOfTypeAll<Font>();
            if (loaded != null && loaded.Length > 0) return loaded[0];

            // Last resort: dynamic OS font. Always available on Windows.
            try { return Font.CreateDynamicFontFromOSFont("Arial", 14); }
            catch { return null; }
        }
    }
}
