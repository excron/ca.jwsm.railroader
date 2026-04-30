using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ca.Jwsm.Railroader.Experiments.TrackProfile
{
    /// <summary>
    /// Y = grade % (Option B). Zero in the center, gridlines at ±0.5% / ±1% / ±1.5%
    /// (extending toward map-max). 0% gridline is slightly more prominent than
    /// the others — it's the chart's anchor and where the consist row sits.
    ///
    /// White track line (3px) drawn via Painter2D. Variant-2 fill: grey area
    /// between the track line and the 0% reference line, on both sides.
    ///
    /// Side-scale (left): grade labels next to each gridline.
    /// </summary>
    public sealed class ChartView
    {
        // ---- Layout constants (in 1× scale pixels) ----
        private const float SideScaleWidth = 36f;
        private const float TrackLineWidthPx = 3f;
        private const float GridlineWidthPx = 1f;
        private const float ZeroGridlineWidthPx = 1.5f;

        // Default visible y-range. ±2% covers most rolling-stock-friendly
        // grades; we'll tighten/expand if the dataset's local max differs.
        private const float DefaultVisibleYMaxPct = 2.0f;

        // Gridline ladder. Includes the zero line; 0% gets emphasis treatment
        // separately at draw time. Symmetric: positives mirrored as negatives.
        private static readonly float[] GridlineMagnitudesPct = { 0.5f, 1.0f, 1.5f, 2.0f };

        private readonly Theme _theme;
        private readonly float _uiScale;
        private readonly RouteData _route;

        private VisualElement _root;
        private VisualElement _scaleColumn;
        private VisualElement _drawArea;

        private ViewWindow _window;
        private float _visibleYMaxPct = DefaultVisibleYMaxPct;

        public ChartView(Theme theme, RouteData route, float uiScale = 1f)
        {
            _theme = theme;
            _route = route;
            _uiScale = uiScale;
        }

        private float S(float px) => px * _uiScale;

        public VisualElement Build()
        {
            _root = new VisualElement();
            _root.style.flexDirection = FlexDirection.Row;
            _root.style.flexGrow = 1;
            _root.style.flexShrink = 1;
            _root.pickingMode = PickingMode.Ignore;

            _scaleColumn = BuildScaleColumn();
            _root.Add(_scaleColumn);

            _drawArea = new VisualElement();
            _drawArea.style.flexGrow = 1;
            _drawArea.pickingMode = PickingMode.Ignore;
            _drawArea.generateVisualContent += DrawChart;
            _root.Add(_drawArea);

            return _root;
        }

        /// <summary>
        /// Caller (TrackProfilePanel) sets the visible-window before each
        /// repaint. Layout is unchanged; only the drawn content changes.
        /// </summary>
        public void SetWindow(ViewWindow window)
        {
            _window = window;
            _drawArea?.MarkDirtyRepaint();
        }

        /// <summary>
        /// The drawing area's exposed VisualElement — siblings (vehicle row,
        /// annotation overlay) parent themselves under this so their absolute
        /// positions align 1:1 with the chart's pixel-per-foot mapping.
        /// </summary>
        public VisualElement DrawArea => _drawArea;

        /// <summary>
        /// Convert a route distance (ft) to a local x-pixel inside the draw
        /// area. Returns NaN when the window isn't set yet.
        /// </summary>
        public float DistFtToLocalX(float distFt)
        {
            if (_window.WidthFt <= 0f || _drawArea == null) return float.NaN;
            var w = _drawArea.contentRect.width;
            if (w <= 0f) return float.NaN;
            var t = (distFt - _window.StartFt) / _window.WidthFt;
            return t * w;
        }

        public float PixelsPerFoot
        {
            get
            {
                if (_window.WidthFt <= 0f || _drawArea == null) return 0f;
                var w = _drawArea.contentRect.width;
                return w / _window.WidthFt;
            }
        }

        // ---- Scale column ----

        private VisualElement BuildScaleColumn()
        {
            var col = new VisualElement();
            col.style.width = S(SideScaleWidth);
            col.style.flexShrink = 0;
            col.style.flexDirection = FlexDirection.Column;
            col.style.justifyContent = Justify.Center;
            col.pickingMode = PickingMode.Ignore;
            // The scale labels are positioned absolutely so they line up with
            // the chart's gridlines exactly, regardless of label height.
            col.style.position = Position.Relative;

            // Build labels for visible gridlines. We rebuild lazily when
            // visible-y-max changes; for the dummy dataset that's never.
            BuildScaleLabels(col);
            return col;
        }

        private void BuildScaleLabels(VisualElement col)
        {
            // 0% always present
            AddScaleLabel(col, 0f, "0%");
            foreach (var mag in GridlineMagnitudesPct)
            {
                if (mag > _visibleYMaxPct + 0.01f) continue;
                AddScaleLabel(col,  mag, $"+{mag:0.0}%");
                AddScaleLabel(col, -mag, $"-{mag:0.0}%");
            }
        }

        private void AddScaleLabel(VisualElement col, float gradePct, string text)
        {
            var label = new Label(text);
            label.style.position = Position.Absolute;
            label.style.right = S(4);
            // We can't know the column height at build time. Use percent
            // positioning relative to the column's full height, accepting
            // a small ±1px drift vs the chart's gridlines (which are in
            // physical pixels). Acceptable given charcoal/text contrast.
            var t = (gradePct + _visibleYMaxPct) / (2f * _visibleYMaxPct);
            // top is the top edge of the label; offset by half line-height
            // for vertical centering (~6px at 12pt).
            label.style.top = new Length(100f - t * 100f, LengthUnit.Percent);
            label.style.translate = new Translate(0, S(-6));
            label.style.color = _theme.TextMuted;
            label.style.fontSize = S(10);
            label.style.unityFontStyleAndWeight = FontStyle.Normal;
            label.pickingMode = PickingMode.Ignore;
            col.Add(label);
        }

        // ---- Chart drawing ----

        private void DrawChart(MeshGenerationContext mgc)
        {
            var rect = mgc.visualElement.contentRect;
            if (rect.width <= 0f || rect.height <= 0f) return;
            if (_window.WidthFt <= 0f) return;

            var p2d = mgc.painter2D;

            // Establish the y-mapping. Center is 0%; +ymax sits at top, -ymax at bottom.
            var midY = rect.height * 0.5f;
            var pxPerPct = midY / _visibleYMaxPct;

            // ---- Variant-2 fill: between line and 0% reference ----
            FillBetweenLineAndZero(p2d, rect, midY, pxPerPct);

            // ---- Gridlines (horizontal) ----
            DrawGridlines(p2d, rect, midY, pxPerPct);

            // ---- Track line ----
            DrawTrackLine(p2d, rect, midY, pxPerPct);
        }

        private float GradeToY(float gradePct, float midY, float pxPerPct)
        {
            // Positive grade -> upward on screen -> smaller y.
            return midY - gradePct * pxPerPct;
        }

        private float DistFtToX(float distFt, Rect rect)
        {
            var t = (distFt - _window.StartFt) / _window.WidthFt;
            return t * rect.width;
        }

        /// <summary>
        /// Sample the route at fixed pixel intervals across the visible window
        /// and emit a poly-line. Sampling at ~1px cadence gives a smooth curve
        /// without aliasing; cheaper than per-route-sample drawing on long routes.
        /// </summary>
        private void DrawTrackLine(Painter2D p2d, Rect rect, float midY, float pxPerPct)
        {
            int steps = Mathf.Max(2, Mathf.CeilToInt(rect.width));
            p2d.lineWidth = S(TrackLineWidthPx);
            p2d.strokeColor = _theme.TextPrimary;
            p2d.lineCap = LineCap.Round;
            p2d.lineJoin = LineJoin.Round;
            p2d.BeginPath();
            for (int i = 0; i <= steps; i++)
            {
                var t = (float)i / steps;
                var distFt = Mathf.Lerp(_window.StartFt, _window.EndFt, t);
                var grade = _route.GradeAt(distFt);
                var x = t * rect.width;
                var y = GradeToY(grade, midY, pxPerPct);
                if (i == 0) p2d.MoveTo(new Vector2(x, y));
                else p2d.LineTo(new Vector2(x, y));
            }
            p2d.Stroke();
        }

        /// <summary>
        /// Variant-2 fill: shade the area between the grade line and the 0%
        /// reference, regardless of sign. We sample at fixed pixel cadence,
        /// then build a single closed polygon: forward along the line, back
        /// along the 0% reference. This works visually because Painter2D's
        /// even-odd / non-zero fill handles the "above-zero positives flip
        /// to below-zero negatives" case as one continuous shape.
        /// </summary>
        private void FillBetweenLineAndZero(Painter2D p2d, Rect rect, float midY, float pxPerPct)
        {
            // Fill is theme-driven: a slightly darker overlay on the panel
            // background. We use the 'border' color tinted heavily toward
            // transparent so the panel's stripe still bleeds through.
            var fill = _theme.PanelStripe;
            fill.a *= 0.85f;
            p2d.fillColor = fill;

            int steps = Mathf.Max(2, Mathf.CeilToInt(rect.width));
            p2d.BeginPath();
            // Forward along the grade line (left to right).
            for (int i = 0; i <= steps; i++)
            {
                var t = (float)i / steps;
                var distFt = Mathf.Lerp(_window.StartFt, _window.EndFt, t);
                var grade = _route.GradeAt(distFt);
                var x = t * rect.width;
                var y = GradeToY(grade, midY, pxPerPct);
                if (i == 0) p2d.MoveTo(new Vector2(x, y));
                else p2d.LineTo(new Vector2(x, y));
            }
            // Back along the 0% reference (right to left).
            p2d.LineTo(new Vector2(rect.width, midY));
            p2d.LineTo(new Vector2(0f, midY));
            p2d.ClosePath();
            p2d.Fill();
        }

        private void DrawGridlines(Painter2D p2d, Rect rect, float midY, float pxPerPct)
        {
            // Subtle gridlines: theme border color at low alpha.
            var grid = _theme.Border;
            grid.a *= 0.35f;
            p2d.strokeColor = grid;

            // Non-zero gridlines first (thinner)
            p2d.lineWidth = S(GridlineWidthPx);
            foreach (var mag in GridlineMagnitudesPct)
            {
                if (mag > _visibleYMaxPct + 0.01f) continue;
                DrawHorizontalLine(p2d, rect, GradeToY( mag, midY, pxPerPct));
                DrawHorizontalLine(p2d, rect, GradeToY(-mag, midY, pxPerPct));
            }

            // Zero gridline (more prominent)
            var zeroColor = _theme.Border;
            zeroColor.a *= 0.6f;
            p2d.strokeColor = zeroColor;
            p2d.lineWidth = S(ZeroGridlineWidthPx);
            DrawHorizontalLine(p2d, rect, midY);
        }

        private void DrawHorizontalLine(Painter2D p2d, Rect rect, float y)
        {
            p2d.BeginPath();
            p2d.MoveTo(new Vector2(0f, y));
            p2d.LineTo(new Vector2(rect.width, y));
            p2d.Stroke();
        }
    }

    /// <summary>
    /// The visible window of route currently rendered, in feet along the route.
    /// Width = EndFt - StartFt. The chart, vehicle row, and annotation overlay
    /// all share this window and map distFt → pixel-x consistently.
    /// </summary>
    public struct ViewWindow
    {
        public float StartFt;
        public float EndFt;
        public float WidthFt => EndFt - StartFt;
    }
}
