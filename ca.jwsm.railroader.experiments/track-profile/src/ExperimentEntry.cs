using System.IO;
using System.Reflection;
using GalaSoft.MvvmLight.Messaging;
using Game.Settings;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.TrackProfile
{
    /// <summary>
    /// UMM entry point for the track-in-profile experiment.
    ///
    /// First pass: a static dummy dataset (data/sample-route.json) drives
    /// the chart. A dev-mode bar sits at the top of the screen with a
    /// position slider + reverser toggle so we can scrub the train through
    /// the route and confirm the chart reads cleanly across the panel
    /// width and across UI scales / themes.
    ///
    /// Hotkeys:
    ///   Arrow Left/Right    scrub train position by 50 ft
    ///   Shift+Arrow         scrub by 500 ft (5× faster)
    ///   R                   toggle reverser direction
    /// </summary>
    public static class ExperimentEntry
    {
        public static UnityModManager.ModEntry Mod { get; private set; }

        private static Theme _theme;
        private static RouteData _route;
        private static bool _shown;
        private static GameObject _panelGo;
        private static float _appliedScale = -1f;

        private static TrackProfilePanel _profile;
        private static Slider _devSlider;
        private static Toggle _devReverser;
        private static Label _devReadout;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;

            try
            {
                var modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var themePath = Path.Combine(modDir, "Themes", "charcoal.json");
                _theme = Theme.LoadFromFile(themePath);
                modEntry.Logger.Log($"Loaded theme '{_theme.Name}' from {themePath}");

                var dataPath = Path.Combine(modDir, "data", "sample-route.json");
                _route = RouteData.LoadFromFile(dataPath);
                modEntry.Logger.Log($"Loaded route '{_route.Name}' ({_route.LengthFt:0} ft, {_route.Samples.Count} grade samples, {_route.Annotations.Count} annotations, {_route.Consist.Count} cars)");
            }
            catch (System.Exception ex)
            {
                modEntry.Logger.Error($"Failed to load assets: {ex}");
                return false;
            }

            modEntry.OnUpdate = OnUpdate;
            modEntry.Logger.Log("Track profile experiment: loaded. Awaiting scene to attach panel.");
            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            if (!_shown)
            {
                if (Camera.main == null) return;
                try
                {
                    ShowExperiment();
                    _shown = true;
                    modEntry.Logger.Log("Track profile experiment: panel attached.");
                    modEntry.Logger.Log("Arrow keys scrub position; R toggles reverser direction.");
                }
                catch (System.Exception ex)
                {
                    modEntry.Logger.Error($"Failed to attach experiment panel: {ex}");
                    _shown = true;
                }
            }

            // Hotkey-driven scrub + reverser toggle. Cheap per-frame check
            // via the new InputSystem (legacy Input is unreliable when the
            // new system is the active handler).
            if (_profile == null) return;
            var kb = Keyboard.current;
            if (kb == null) return;

            var step = (kb.shiftKey.isPressed ? 500f : 50f);
            if (kb.leftArrowKey.isPressed)
            {
                _profile.SetTrainHead(_profile.TrainHeadFt - step * dt * 60f);
                SyncDevControlsToState();
            }
            else if (kb.rightArrowKey.isPressed)
            {
                _profile.SetTrainHead(_profile.TrainHeadFt + step * dt * 60f);
                SyncDevControlsToState();
            }

            if (kb.rKey.wasPressedThisFrame)
            {
                _profile.SetReversed(!_profile.Reversed);
                SyncDevControlsToState();
            }
        }

        private static void RebuildPanel()
        {
            if (_panelGo == null) return;
            var doc = _panelGo.GetComponent<UIDocument>();
            if (doc == null) return;

            _appliedScale = ReadGameUIScale();
            Mod.Logger.Log($"Rebuilding panel at UI scale: {_appliedScale:0.##}x");

            var root = doc.rootVisualElement;
            root.Clear();

            var font = LoadFallbackFont();
            if (font != null) root.style.unityFont = new StyleFont(font);

            _profile = new TrackProfilePanel(_theme, _route, _appliedScale);
            root.Add(_profile.Build());
            _profile.SetTrainHead(_route.InitialHeadPositionFt);

            root.Add(BuildDevControls());
            SyncDevControlsToState();
        }

        /// <summary>
        /// Tiny dev-mode toolbar at the top-left of the screen. Position slider
        /// scrubs train head; reverser toggle flips the consist's intra-train
        /// orientation; readout shows current head position + grade. Lives
        /// outside the production panel so the layout we're tuning isn't
        /// polluted by dev affordances.
        /// </summary>
        private static VisualElement BuildDevControls()
        {
            var bar = new VisualElement();
            bar.style.position = Position.Absolute;
            bar.style.top = _appliedScale * 8f;
            bar.style.left = _appliedScale * 8f;
            bar.style.width = _appliedScale * 360f;
            bar.style.paddingLeft = _appliedScale * 8f;
            bar.style.paddingRight = _appliedScale * 8f;
            bar.style.paddingTop = _appliedScale * 6f;
            bar.style.paddingBottom = _appliedScale * 6f;
            bar.style.backgroundColor = _theme.Panel;
            bar.style.borderTopLeftRadius = _appliedScale * 6f;
            bar.style.borderTopRightRadius = _appliedScale * 6f;
            bar.style.borderBottomLeftRadius = _appliedScale * 6f;
            bar.style.borderBottomRightRadius = _appliedScale * 6f;
            bar.style.flexDirection = FlexDirection.Column;

            var title = new Label("DEV — TRACK PROFILE");
            title.style.color = _theme.TextMuted;
            title.style.fontSize = _appliedScale * 10f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            bar.Add(title);

            _devSlider = new Slider("Position", 0f, _route.LengthFt);
            _devSlider.style.color = _theme.TextPrimary;
            _devSlider.style.fontSize = _appliedScale * 10f;
            _devSlider.value = _route.InitialHeadPositionFt;
            _devSlider.RegisterValueChangedCallback(evt =>
            {
                if (_profile != null) _profile.SetTrainHead(evt.newValue);
                UpdateReadout();
            });
            bar.Add(_devSlider);

            _devReverser = new Toggle("Reversed");
            _devReverser.style.color = _theme.TextPrimary;
            _devReverser.style.fontSize = _appliedScale * 10f;
            _devReverser.RegisterValueChangedCallback(evt =>
            {
                if (_profile != null) _profile.SetReversed(evt.newValue);
                UpdateReadout();
            });
            bar.Add(_devReverser);

            _devReadout = new Label("");
            _devReadout.style.color = _theme.TextMuted;
            _devReadout.style.fontSize = _appliedScale * 10f;
            bar.Add(_devReadout);

            return bar;
        }

        private static void SyncDevControlsToState()
        {
            if (_devSlider != null && _profile != null)
            {
                // SetValueWithoutNotify avoids re-firing the callback when we
                // sync from hotkeys.
                _devSlider.SetValueWithoutNotify(_profile.TrainHeadFt);
            }
            if (_devReverser != null && _profile != null)
            {
                _devReverser.SetValueWithoutNotify(_profile.Reversed);
            }
            UpdateReadout();
        }

        private static void UpdateReadout()
        {
            if (_devReadout == null || _profile == null) return;
            var headFt = _profile.TrainHeadFt;
            var headMi = headFt / 5280f;
            var grade = _profile.CurrentGradePct;
            var sign = grade >= 0f ? "+" : "";
            _devReadout.text = $"Head: {headFt:0} ft  ({headMi:0.00} mi)   Grade: {sign}{grade:0.0}%   Dir: {(_profile.Reversed ? "REV" : "FWD")}";
        }

        private static void ShowExperiment()
        {
            var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelSettings.name = "TrackProfilePanelSettings";
            panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
            panelSettings.referenceDpi = 96;
            panelSettings.fallbackDpi = 96;
            // Sort above the HUD experiment (100) but below any modal overlays.
            panelSettings.sortingOrder = 105;

            _panelGo = new GameObject("TrackProfilePanelHost");
            Object.DontDestroyOnLoad(_panelGo);
            var doc = _panelGo.AddComponent<UIDocument>();
            doc.panelSettings = panelSettings;

            doc.rootVisualElement.style.flexGrow = 1;
            doc.rootVisualElement.pickingMode = PickingMode.Ignore;

            // Same UI-scale event subscription pattern as the HUD experiment.
            Messenger.Default.Register<CanvasScaleChanged>(_panelGo, _ => RebuildPanel());

            RebuildPanel();
        }

        private static float ReadGameUIScale()
        {
            return PlayerPrefs.GetFloat("gfx.canvas.scale", 1f);
        }

        private static Font LoadFallbackFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                       ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font != null) return font;

            var loaded = Resources.FindObjectsOfTypeAll<Font>();
            if (loaded != null && loaded.Length > 0) return loaded[0];

            try { return Font.CreateDynamicFontFromOSFont("Arial", 14); }
            catch { return null; }
        }
    }
}
