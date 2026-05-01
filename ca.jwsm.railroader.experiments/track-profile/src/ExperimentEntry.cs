using System.IO;
using System.Reflection;
using GalaSoft.MvvmLight.Messaging;
using Game.Events;
using Game.Settings;
using Model;
using UnityEngine;
using UnityEngine.UIElements;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.TrackProfile
{
    /// <summary>
    /// UMM entry point for the track-in-profile experiment, wired to live
    /// game state.
    ///
    /// Visibility: the panel only appears when the player has selected a
    /// locomotive (TrainController.Shared.SelectedCar with IsLocomotive == true).
    ///
    /// Refresh cadence: ~5 Hz. Train movement between refreshes is small enough
    /// at typical speeds that 5 Hz reads as smooth, and the per-tick cost
    /// (route projection + grade sampling + consist enumeration) is trivial
    /// compared to per-frame.
    /// </summary>
    public static class ExperimentEntry
    {
        // 30 Hz refresh — at 30 mph the train moves ~1.5 ft per tick instead
        // of ~9 ft, eliminating the visible jumping of POIs as the train
        // moves. Per-tick cost is small (route walk + signal proximity
        // both linear in step count + scene signals).
        private const float RefreshIntervalSeconds = 1f / 30f;

        public static UnityModManager.ModEntry Mod { get; private set; }

        private static Theme _theme;
        private static bool _shown;
        private static GameObject _panelGo;
        private static float _appliedScale = -1f;

        private static TrackProfilePanel _profile;
        private static RouteData _route;
        private static LiveRouteSampler.State _samplerState;
        private static float _refreshTimer;
        private static float _diagTimer;     // 1 Hz log throttle (debug)

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

            _route = RouteData.CreateEmpty();
            _samplerState = new LiveRouteSampler.State();

            modEntry.OnUpdate = OnUpdate;
            modEntry.Logger.Log("Track profile experiment: loaded. Awaiting scene to attach panel.");
            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            // First-show: wait for camera to be ready, then attach.
            if (!_shown)
            {
                if (Camera.main == null) return;
                try
                {
                    ShowExperiment();
                    _shown = true;
                    modEntry.Logger.Log("Track profile experiment: panel attached. Hidden until a locomotive is selected.");
                }
                catch (System.Exception ex)
                {
                    modEntry.Logger.Error($"Failed to attach experiment panel: {ex}");
                    _shown = true;
                }
                return;
            }

            // Throttled refresh from live game state. We poll selection here
            // rather than relying solely on SelectedCarChanged because the
            // panel content also depends on the loco's continually-changing
            // position; the event-driven path covers re-show after a swap.
            _refreshTimer -= dt;
            if (_refreshTimer <= 0f)
            {
                _refreshTimer = RefreshIntervalSeconds;
                RefreshFromLive();
            }
        }

        /// <summary>
        /// Read live state, decide whether to show the panel, and if so push
        /// a fresh data snapshot through to the chart.
        /// </summary>
        private static void RefreshFromLive()
        {
            if (_profile == null) return;

            var tc = TrainController.Shared;
            if (tc == null) { _profile.SetVisible(false); return; }

            var loco = tc.SelectedCar;
            if (loco == null || !loco.IsLocomotive)
            {
                _profile.SetVisible(false);
                return;
            }

            if (!LiveRouteSampler.Sample(loco, _route, _samplerState))
            {
                _profile.SetVisible(false);
                return;
            }

            _profile.SetVisible(true);
            _profile.RefreshLive(_samplerState.LastReversed);

            // 1 Hz diagnostic line. Helps confirm whether issues are in
            // detection (counts) or rendering (counts look right but
            // visuals don't match). Cheap; remove later when we're done
            // diagnosing live-data correctness.
            _diagTimer -= RefreshIntervalSeconds;
            if (_diagTimer <= 0f)
            {
                _diagTimer = 1f;
                EmitDiagnostic();
            }
        }

        private static void EmitDiagnostic()
        {
            int swNorm = 0, swRev = 0;
            int sigClear = 0, sigApproach = 0, sigStop = 0, sigOther = 0;
            foreach (var a in _route.Annotations)
            {
                if (a.Type == "switch")
                {
                    if (a.Diverging == "reversed") swRev++; else swNorm++;
                }
                else if (a.Type == "signal")
                {
                    switch (a.Aspect)
                    {
                        case "clear":    sigClear++;    break;
                        case "approach": sigApproach++; break;
                        case "stop":     sigStop++;     break;
                        default:         sigOther++;    break;
                    }
                }
            }
            var sigCacheCount = _samplerState.CachedSignals?.Count ?? 0;
            Mod.Logger.Log(
                $"[track-profile] grade={_route.CurrentGradePct:+0.0;-0.0;0.0}% " +
                $"samples={_route.Samples.Count} " +
                $"switches=W:{swNorm}/B:{swRev} " +
                $"signals(scene:{sigCacheCount} matched:G{sigClear}/Y{sigApproach}/R{sigStop}/?{sigOther})");
            // Per-switch detail with the raw field values we read. If a
            // switch's CTC display state isn't matching the in-game switch,
            // these lines show whether ctc/t/d are reading what we expect.
            foreach (var a in _route.Annotations)
            {
                if (a.Type != "switch") continue;
                Mod.Logger.Log($"[track-profile]   switch @ {a.DistFt:0}ft  state={a.Diverging}  raw=[{a.Label}]");
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
            _profile.SetVisible(false);  // hidden until a loco is selected

            // Trigger an immediate refresh after rebuild so the panel reflects
            // the current selection state without waiting for the next 5 Hz tick.
            _refreshTimer = 0f;
        }

        private static void ShowExperiment()
        {
            var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelSettings.name = "TrackProfilePanelSettings";
            panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
            panelSettings.referenceDpi = 96;
            panelSettings.fallbackDpi = 96;
            panelSettings.sortingOrder = 105;

            _panelGo = new GameObject("TrackProfilePanelHost");
            Object.DontDestroyOnLoad(_panelGo);
            var doc = _panelGo.AddComponent<UIDocument>();
            doc.panelSettings = panelSettings;

            doc.rootVisualElement.style.flexGrow = 1;
            doc.rootVisualElement.pickingMode = PickingMode.Ignore;

            // UI-scale change subscription (vanilla CanvasScaleChanged event).
            Messenger.Default.Register<CanvasScaleChanged>(_panelGo, _ => RebuildPanel());

            // Selection-change subscription. Triggers an immediate refresh so
            // the panel toggles visibility the same frame the player swaps loco.
            Messenger.Default.Register<SelectedCarChanged>(_panelGo, _ => { _refreshTimer = 0f; });

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
