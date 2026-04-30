using UnityEngine;
using UnityEngine.UIElements;

namespace Ca.Jwsm.Railroader.Experiments.TrackProfile
{
    /// <summary>
    /// Top-level panel composing ChartView + VehicleRow + AnnotationLayer.
    ///
    /// Layout:
    ///   - Panel root: absolute-positioned to the right of the HUD, flex-grown
    ///     to right edge, height matching HUD's base height.
    ///   - Inside: a row [ side-scale (left, fixed) | drawing area (flex grow) ].
    ///   - Inside the drawing area, layered (parent → child overlays in Z):
    ///       1. ChartView's draw-content callback (gridlines + fill + line)
    ///       2. AnnotationLayer (markers, mileposts, signals, etc.)
    ///       3. VehicleRow (consist boxes — drawn last so they sit on top of
    ///          the line where they overlap)
    ///
    /// The "consist box obscuring the grade line" effect is intentional —
    /// when the train passes over a crest, the line under the consist
    /// visibly bobs in/out of the boxes at the appropriate end.
    /// </summary>
    public sealed class TrackProfilePanel
    {
        // Layout constants in 1× scale pixels
        private const float HudWidthPx = 450f;     // HudClone's panel width
        private const float HudHeightPx = 174f;    // HudClone's BasePanelHeight
        private const float LeftGapPx = 16f;       // gap between HUD right edge and profile left edge
        private const float RightMarginPx = 16f;
        private const float BottomMarginPx = 12f;

        // Visible window: 200 ft behind the consist tail, ~1 mile (5280 ft)
        // ahead of the head. Total ≈ 5920 ft + consist length.
        private const float BehindFt = 200f;
        private const float AheadFt = 5280f;

        private readonly Theme _theme;
        private readonly RouteData _route;
        private readonly float _uiScale;

        private VisualElement _root;
        private ChartView _chart;
        private VehicleRow _vehicleRow;
        private AnnotationLayer _annotations;

        private float _trainHeadFt;
        private bool _reversed;

        public TrackProfilePanel(Theme theme, RouteData route, float uiScale = 1f)
        {
            _theme = theme;
            _route = route;
            _uiScale = uiScale;
            _trainHeadFt = route.InitialHeadPositionFt;
        }

        private float S(float px) => px * _uiScale;

        public VisualElement Build()
        {
            _root = new VisualElement();
            _root.style.position = Position.Absolute;
            _root.style.left = S(HudWidthPx + LeftGapPx);
            _root.style.right = S(RightMarginPx);
            _root.style.bottom = S(BottomMarginPx);
            _root.style.height = S(HudHeightPx);
            _root.style.backgroundColor = _theme.Panel;
            _root.style.borderTopLeftRadius = S(8);
            _root.style.borderTopRightRadius = S(8);
            _root.style.borderBottomLeftRadius = S(8);
            _root.style.borderBottomRightRadius = S(8);
            _root.style.paddingLeft = S(8);
            _root.style.paddingRight = S(12);
            _root.style.paddingTop = S(8);
            _root.style.paddingBottom = S(8);
            _root.pickingMode = PickingMode.Ignore;

            _chart = new ChartView(_theme, _route, _uiScale);
            _root.Add(_chart.Build());

            // VehicleRow + AnnotationLayer parent themselves under the chart
            // draw-area so their absolute coords align with the chart's
            // pixel-per-foot mapping.
            _annotations = new AnnotationLayer(_theme, _route, _uiScale);
            _chart.DrawArea.Add(_annotations.Build());

            _vehicleRow = new VehicleRow(_theme, _route, _uiScale);
            _chart.DrawArea.Add(_vehicleRow.Build());

            // Run a layout-resolved update on first geometry (when the
            // chart's contentRect width is known). Subsequent updates are
            // triggered by SetTrainHead / SetReversed.
            _chart.DrawArea.RegisterCallback<GeometryChangedEvent>(_ => Refresh());

            return _root;
        }

        public void SetTrainHead(float headFt)
        {
            _trainHeadFt = Mathf.Clamp(headFt, 0f, _route.LengthFt);
            Refresh();
        }

        public void SetReversed(bool reversed)
        {
            _reversed = reversed;
            Refresh();
        }

        /// <summary>
        /// Show or hide the entire panel. Used to gate visibility on whether
        /// a locomotive is currently selected.
        /// </summary>
        public void SetVisible(bool visible)
        {
            if (_root != null)
                _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// Push a fresh live state in: the route data was repopulated by the
        /// LiveRouteSampler, the train head sits at distFt = 0 (head-relative
        /// frame), and reversed reflects current direction of travel.
        /// </summary>
        public void RefreshLive(bool reversed)
        {
            _trainHeadFt = 0f;
            _reversed = reversed;
            Refresh();
        }

        public float TrainHeadFt => _trainHeadFt;
        public bool  Reversed    => _reversed;
        public float CurrentGradePct => _route.GradeAt(_trainHeadFt);

        /// <summary>
        /// Recompute the visible window from current train state, push it to
        /// each sub-view. The chart re-paints; vehicle row + annotations
        /// re-layout their elements.
        /// </summary>
        private void Refresh()
        {
            var totalLenFt = _route.TotalConsistLengthFt;

            // Tail position regardless of reverser direction (reverser only
            // affects intra-consist car ordering, not where the consist sits).
            var tailFt = _trainHeadFt - totalLenFt;

            var window = new ViewWindow
            {
                StartFt = tailFt - BehindFt,
                EndFt   = _trainHeadFt + AheadFt,
            };

            _chart.SetWindow(window);

            var pxPerFt = _chart.PixelsPerFoot;
            var pxPerElevFt = _chart.PixelsPerElevationFt;
            if (pxPerFt > 0f)
            {
                _vehicleRow.Update(_trainHeadFt, pxPerFt, window.StartFt, _reversed, pxPerElevFt);
                _annotations.Update(pxPerFt, window.StartFt, window.EndFt);
            }
        }
    }
}
