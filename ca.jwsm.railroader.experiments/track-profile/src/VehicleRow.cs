using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ca.Jwsm.Railroader.Experiments.TrackProfile
{
    /// <summary>
    /// Renders the consist as a horizontal bar of rectangles centered on the
    /// 0% gridline. The chart's grade line passes BEHIND the consist — when
    /// the train is on a crest or sag, you can see the line bob into / out
    /// of the consist box at the appropriate end.
    ///
    /// Vehicle order is always tail→head left→right within the consist box.
    /// On reverser flip (Reversed = true), the engines move from the right
    /// end of the consist to the left end (the consist box itself doesn't
    /// move; lookahead direction stays rightward).
    /// </summary>
    public sealed class VehicleRow
    {
        // Visual constants in 1× scale pixels
        private const float CarHeightPx = 14f;
        private const float CarGapPx = 1f;
        private const float LabelMinCarWidthPx = 28f;  // hide labels on cars narrower than this

        private readonly Theme _theme;
        private readonly RouteData _route;
        private readonly float _uiScale;

        private VisualElement _container;
        private readonly List<VisualElement> _carBoxes = new List<VisualElement>();
        private readonly List<Label> _carLabels = new List<Label>();

        public VehicleRow(Theme theme, RouteData route, float uiScale = 1f)
        {
            _theme = theme;
            _route = route;
            _uiScale = uiScale;
        }

        private float S(float px) => px * _uiScale;

        public VisualElement Build()
        {
            _container = new VisualElement();
            _container.style.position = Position.Absolute;
            _container.style.left = 0;
            _container.style.right = 0;
            _container.style.top = 0;
            _container.style.bottom = 0;
            _container.pickingMode = PickingMode.Ignore;

            EnsureBoxesMatchConsist();
            return _container;
        }

        /// <summary>
        /// Reconcile internal box+label list with the current consist size.
        /// Called from Update() so live-data refreshes (coupling/uncoupling)
        /// don't leave dangling or missing slots. Cheap when count is stable.
        /// </summary>
        private void EnsureBoxesMatchConsist()
        {
            var n = _route.Consist.Count;
            // Add new boxes if consist grew
            while (_carBoxes.Count < n)
            {
                var box = new VisualElement();
                box.style.position = Position.Absolute;
                box.style.height = S(CarHeightPx);
                box.style.borderTopLeftRadius = S(2);
                box.style.borderTopRightRadius = S(2);
                box.style.borderBottomLeftRadius = S(2);
                box.style.borderBottomRightRadius = S(2);
                box.style.borderTopWidth = 1;
                box.style.borderBottomWidth = 1;
                box.style.borderLeftWidth = 1;
                box.style.borderRightWidth = 1;
                box.style.borderTopColor = _theme.Border;
                box.style.borderBottomColor = _theme.Border;
                box.style.borderLeftColor = _theme.Border;
                box.style.borderRightColor = _theme.Border;
                box.pickingMode = PickingMode.Ignore;
                _container.Add(box);
                _carBoxes.Add(box);

                var lbl = new Label("");
                lbl.style.position = Position.Absolute;
                lbl.style.color = _theme.TextPrimary;
                lbl.style.fontSize = S(9);
                lbl.style.unityTextAlign = TextAnchor.MiddleCenter;
                lbl.style.height = S(10);
                lbl.pickingMode = PickingMode.Ignore;
                _container.Add(lbl);
                _carLabels.Add(lbl);
            }
            // Hide overflow if consist shrank (don't destroy — pool the elements)
            for (int i = 0; i < _carBoxes.Count; i++)
            {
                var visible = i < n;
                _carBoxes[i].style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                _carLabels[i].style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        /// <summary>
        /// Update layout. Called every time the train head moves.
        ///
        /// headFt: route distance of the leading-end-of-consist (in current
        ///         direction of travel).
        /// pxPerFt: chart pixels per foot (from ChartView.PixelsPerFoot).
        /// startFt: ViewWindow.StartFt (left edge of chart in route ft).
        /// reversed: if true, the visual order of cars flips so engines are
        ///         on the LEFT end of the consist (trailing in current travel),
        ///         maintaining the "leading-end is closest to lookahead" rule
        ///         while keeping the consist visually anchored at the same
        ///         x-range.
        /// </summary>
        public void Update(float headFt, float pxPerFt, float startFt, bool reversed)
        {
            if (_container == null || pxPerFt <= 0f) return;
            EnsureBoxesMatchConsist();
            var rect = _container.contentRect;
            if (rect.height <= 0f) return;

            // The 0% gridline sits at vertical center of the chart drawing
            // area. Because VehicleRow's container is parented under the
            // ChartView's draw area, our rect.height matches the chart's,
            // and the center is rect.height / 2.
            var centerY = rect.height * 0.5f;
            var carHeight = S(CarHeightPx);

            // Total consist length in feet (from cached sum).
            var totalLenFt = _route.TotalConsistLengthFt;
            var tailFt = headFt - totalLenFt;

            // Walk the consist tail→head left→right always. When reversed,
            // we walk the consist list in REVERSE order so the loco ends up
            // on the left visually — the consist's geographic span doesn't
            // change.
            var walk = new List<RouteData.Vehicle>(_route.Consist);
            if (reversed) walk.Reverse();

            float cursorFt = tailFt;
            for (int i = 0; i < walk.Count; i++)
            {
                var v = walk[i];
                var box = _carBoxes[i];
                var lbl = _carLabels[i];

                var leftFt = cursorFt;
                var rightFt = cursorFt + v.LengthFt;

                var leftPx = (leftFt - startFt) * pxPerFt;
                var rightPx = (rightFt - startFt) * pxPerFt;
                var widthPx = Mathf.Max(2f, rightPx - leftPx - S(CarGapPx));

                box.style.left = leftPx;
                box.style.width = widthPx;
                box.style.top = centerY - carHeight * 0.5f;
                // Update color in case kind ordering changed under reverse.
                box.style.backgroundColor = ColorForKind(v.Kind);

                // Label sits just above the box. Hide for cars narrower than
                // the threshold so labels don't overlap.
                if (widthPx >= S(LabelMinCarWidthPx))
                {
                    lbl.style.display = DisplayStyle.Flex;
                    lbl.text = v.ShortName;
                    lbl.style.left = leftPx;
                    lbl.style.width = widthPx;
                    lbl.style.top = centerY - carHeight * 0.5f - S(11);
                }
                else
                {
                    lbl.style.display = DisplayStyle.None;
                }

                cursorFt = rightFt;
            }
        }

        /// <summary>
        /// Color the box by car kind. Locos and tenders carry the accent so
        /// the leading end is visually distinct at a glance. Cabooses get
        /// danger as a soft red marker. Freight cars use the muted text color
        /// against the panel — neutral but readable.
        /// </summary>
        private Color ColorForKind(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "locomotive": return _theme.Accent;
                case "tender":     return _theme.AccentHover;
                case "caboose":    return _theme.Danger;
                default:           return _theme.PanelStripe;
            }
        }
    }
}
