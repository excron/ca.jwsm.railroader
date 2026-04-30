using UnityEngine;
using UnityEngine.UIElements;

namespace Ca.Jwsm.Railroader.Experiments.TrackProfile
{
    /// <summary>
    /// Variant 1: Y = elevation, centered on the train's current elevation.
    ///
    /// The chart's vertical center represents the train head's elevation
    /// RIGHT NOW. Line above center = track ahead/behind is HIGHER than the
    /// train. Line below center = LOWER than the train.
    ///
    /// This is the engineer's-cab perspective: a 4.5% climb visibly looks
    /// like a climb (the line angles up); a steady grade reads as a slope
    /// rather than a flat horizontal at some +Y position.
    ///
    /// Y-range auto-scales to fit the elevation range in the visible window,
    /// with a floor (so flat sections don't amplify noise) and a small
    /// headroom past the rounded extreme. Hysteresis: yMax only grows
    /// promptly; shrinking is smoothed via a damped exponential so the
    /// chart doesn't "breathe" as terrain comes in/out of view.
    ///
    /// Side-scale (left): elevation labels in feet relative to the train.
    ///
    /// Top-right readout: current grade % + max abs grade ahead.
    ///
    /// Variant-2 fill: grey area between the track line and the centerline
    /// (the train's current elevation reference).
    /// </summary>
    public sealed class ChartView
    {
        // ---- Layout constants (in 1× scale pixels) ----
        private const float SideScaleWidth = 48f;
        private const float TrackLineWidthPx = 3f;
        private const float GridlineWidthPx = 1f;
        private const float ZeroGridlineWidthPx = 1.5f;

        private const float MinHalfRangeFt = 25f;        // floor — flat sections still show ±25 ft
        private const float YHeadroomFt = 5f;            // padding past the rounded extreme
        private const float ShrinkLerpRate = 1.5f;       // per-second exponential lerp toward smaller halfRange

        private readonly Theme _theme;
        private readonly float _uiScale;
        private readonly RouteData _route;

        private VisualElement _root;
        private VisualElement _scaleColumn;
        private VisualElement _drawArea;
        private Label _gradeReadout;
        private Label _maxAheadReadout;

        private ViewWindow _window;
        private float _halfRangeFt = MinHalfRangeFt;
        private float _lastBuiltHalfRange = -1f;

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
            _drawArea.style.overflow = Overflow.Hidden;
            _drawArea.pickingMode = PickingMode.Ignore;
            _drawArea.generateVisualContent += DrawChart;
            _root.Add(_drawArea);

            // Top-right grade readouts. Two stacked labels:
            //   Big bold:    current grade at the train (e.g. "+4.5% UP")
            //   Smaller dim: max abs grade in the lookahead (e.g. "max +5.2% / -1.8% ahead")
            _gradeReadout = new Label("");
            _gradeReadout.style.position = Position.Absolute;
            _gradeReadout.style.right = S(8);
            _gradeReadout.style.top = S(2);
            _gradeReadout.style.color = _theme.TextPrimary;
            _gradeReadout.style.fontSize = S(13);
            _gradeReadout.style.unityFontStyleAndWeight = FontStyle.Bold;
            _gradeReadout.pickingMode = PickingMode.Ignore;
            _drawArea.Add(_gradeReadout);

            _maxAheadReadout = new Label("");
            _maxAheadReadout.style.position = Position.Absolute;
            _maxAheadReadout.style.right = S(8);
            _maxAheadReadout.style.top = S(18);
            _maxAheadReadout.style.color = _theme.TextMuted;
            _maxAheadReadout.style.fontSize = S(9);
            _maxAheadReadout.pickingMode = PickingMode.Ignore;
            _drawArea.Add(_maxAheadReadout);

            return _root;
        }

        public void SetWindow(ViewWindow window)
        {
            _window = window;
            RecomputeHalfRange();
            RebuildScaleIfNeeded();
            UpdateReadouts();
            _drawArea?.MarkDirtyRepaint();
        }

        public VisualElement DrawArea => _drawArea;

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

        // ---- Dynamic Y-scale (elevation feet) ----

        private void RecomputeHalfRange()
        {
            // Find max |ElevationFtRel| in the visible window. Round up to
            // a tidy step and add headroom; floor at MinHalfRangeFt so flat
            // sections don't auto-amplify into noise.
            float maxAbs = 0f;
            foreach (var s in _route.Samples)
            {
                if (s.DistFt < _window.StartFt || s.DistFt > _window.EndFt) continue;
                var ag = Mathf.Abs(s.ElevationFtRel);
                if (ag > maxAbs) maxAbs = ag;
            }
            var step = StepForRange(maxAbs);
            var rounded = Mathf.Ceil(maxAbs / step) * step;
            var target = Mathf.Max(MinHalfRangeFt, rounded + YHeadroomFt);

            // Hysteresis: grow promptly, shrink slowly. Avoids the chart
            // "breathing" as terrain comes in / out of view.
            if (target >= _halfRangeFt)
            {
                _halfRangeFt = target;
            }
            else
            {
                var dt = Time.deltaTime;
                var t = 1f - Mathf.Exp(-ShrinkLerpRate * dt);
                _halfRangeFt = Mathf.Lerp(_halfRangeFt, target, t);
            }
        }

        /// <summary>
        /// Pick a "tidy" rounding step for the auto-scale based on the rough
        /// magnitude — keeps gridlines on round numbers.
        /// </summary>
        private float StepForRange(float maxAbs)
        {
            if (maxAbs <= 25f)  return 5f;
            if (maxAbs <= 50f)  return 10f;
            if (maxAbs <= 100f) return 25f;
            if (maxAbs <= 250f) return 50f;
            return 100f;
        }

        private float GridlineSpacingFor(float halfRange)
        {
            if (halfRange <= 30f)  return 5f;
            if (halfRange <= 60f)  return 10f;
            if (halfRange <= 125f) return 25f;
            if (halfRange <= 300f) return 50f;
            return 100f;
        }

        private void RebuildScaleIfNeeded()
        {
            // Rebuild only when the rounded gridline spacing actually changes
            // — that's the visible-to-the-user threshold for label changes.
            // Otherwise we'd thrash labels every frame during shrink lerp.
            var prevSpacing = GridlineSpacingFor(_lastBuiltHalfRange);
            var thisSpacing = GridlineSpacingFor(_halfRangeFt);
            // Force-build on first run (sentinel), or when spacing bucket
            // changes, or when the half-range itself changes by more than a
            // gridline step.
            if (_lastBuiltHalfRange < 0f
                || !Mathf.Approximately(prevSpacing, thisSpacing)
                || Mathf.Abs(_halfRangeFt - _lastBuiltHalfRange) > thisSpacing)
            {
                _lastBuiltHalfRange = _halfRangeFt;
                BuildScaleLabels(_scaleColumn);
            }
        }

        private void UpdateReadouts()
        {
            if (_gradeReadout == null) return;

            var current = _route.CurrentGradePct;
            string currentText;
            if (current > 0.05f)       currentText = $"+{current:0.0}%  UP";
            else if (current < -0.05f) currentText = $"{current:0.0}%  DN";
            else                       currentText = "0.0%  LEVEL";
            _gradeReadout.text = currentText;
            _gradeReadout.style.color = current > 0.05f ? _theme.Warning
                                       : current < -0.05f ? _theme.AccentHover
                                       : _theme.TextPrimary;

            // Max abs ahead (samples with DistFt > 0)
            float maxPos = 0f, maxNeg = 0f;
            foreach (var s in _route.Samples)
            {
                if (s.DistFt <= 0f) continue;
                if (s.GradePct > maxPos) maxPos = s.GradePct;
                if (s.GradePct < maxNeg) maxNeg = s.GradePct;
            }
            string aheadText;
            if (maxPos > 0.05f && maxNeg < -0.05f)
                aheadText = $"ahead: +{maxPos:0.0}% / {maxNeg:0.0}%";
            else if (maxPos > 0.05f)
                aheadText = $"ahead: +{maxPos:0.0}%";
            else if (maxNeg < -0.05f)
                aheadText = $"ahead: {maxNeg:0.0}%";
            else
                aheadText = "ahead: flat";
            _maxAheadReadout.text = aheadText;
        }

        // ---- Scale column ----

        private VisualElement BuildScaleColumn()
        {
            var col = new VisualElement();
            col.style.width = S(SideScaleWidth);
            col.style.flexShrink = 0;
            col.style.flexDirection = FlexDirection.Column;
            col.style.position = Position.Relative;
            col.pickingMode = PickingMode.Ignore;
            return col;
        }

        private void BuildScaleLabels(VisualElement col)
        {
            if (col == null) return;
            col.Clear();

            AddScaleLabel(col, 0f, "0 ft");
            var spacing = GridlineSpacingFor(_halfRangeFt);
            for (float mag = spacing; mag <= _halfRangeFt + 0.001f; mag += spacing)
            {
                AddScaleLabel(col,  mag, $"+{mag:0} ft");
                AddScaleLabel(col, -mag, $"{-mag:0} ft");
            }
        }

        private void AddScaleLabel(VisualElement col, float elevFt, string text)
        {
            var label = new Label(text);
            label.style.position = Position.Absolute;
            label.style.right = S(4);
            var t = (elevFt + _halfRangeFt) / (2f * _halfRangeFt);
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
            if (_halfRangeFt <= 0.001f) return;

            var p2d = mgc.painter2D;
            var midY = rect.height * 0.5f;
            var pxPerFt = midY / _halfRangeFt;

            FillBetweenLineAndCenter(p2d, rect, midY, pxPerFt);
            DrawGridlines(p2d, rect, midY, pxPerFt);
            DrawTrackLine(p2d, rect, midY, pxPerFt);
        }

        private float ElevToY(float elevFt, float midY, float pxPerFt)
        {
            // Higher elevation → smaller y on screen (line goes up).
            return midY - elevFt * pxPerFt;
        }

        private void DrawTrackLine(Painter2D p2d, Rect rect, float midY, float pxPerFt)
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
                var elev = _route.ElevationAt(distFt);
                var x = t * rect.width;
                var y = ElevToY(elev, midY, pxPerFt);
                if (i == 0) p2d.MoveTo(new Vector2(x, y));
                else p2d.LineTo(new Vector2(x, y));
            }
            p2d.Stroke();
        }

        private void FillBetweenLineAndCenter(Painter2D p2d, Rect rect, float midY, float pxPerFt)
        {
            var fill = _theme.PanelStripe;
            fill.a *= 0.85f;
            p2d.fillColor = fill;

            int steps = Mathf.Max(2, Mathf.CeilToInt(rect.width));
            p2d.BeginPath();
            for (int i = 0; i <= steps; i++)
            {
                var t = (float)i / steps;
                var distFt = Mathf.Lerp(_window.StartFt, _window.EndFt, t);
                var elev = _route.ElevationAt(distFt);
                var x = t * rect.width;
                var y = ElevToY(elev, midY, pxPerFt);
                if (i == 0) p2d.MoveTo(new Vector2(x, y));
                else p2d.LineTo(new Vector2(x, y));
            }
            p2d.LineTo(new Vector2(rect.width, midY));
            p2d.LineTo(new Vector2(0f, midY));
            p2d.ClosePath();
            p2d.Fill();
        }

        private void DrawGridlines(Painter2D p2d, Rect rect, float midY, float pxPerFt)
        {
            var grid = _theme.Border;
            grid.a *= 0.35f;
            p2d.strokeColor = grid;
            p2d.lineWidth = S(GridlineWidthPx);

            var spacing = GridlineSpacingFor(_halfRangeFt);
            for (float mag = spacing; mag <= _halfRangeFt + 0.001f; mag += spacing)
            {
                DrawHorizontalLine(p2d, rect, ElevToY( mag, midY, pxPerFt));
                DrawHorizontalLine(p2d, rect, ElevToY(-mag, midY, pxPerFt));
            }

            // Center line — train's current elevation reference. More prominent.
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

    public struct ViewWindow
    {
        public float StartFt;
        public float EndFt;
        public float WidthFt => EndFt - StartFt;
    }
}
