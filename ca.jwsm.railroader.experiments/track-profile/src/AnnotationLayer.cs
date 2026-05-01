using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ca.Jwsm.Railroader.Experiments.TrackProfile
{
    /// <summary>
    /// Renders POIs (switches, signals, mileposts, etc.) along the chart's
    /// x-axis. Each POI is a colored dot on a "POI rail" near the bottom of
    /// the chart with a thin vertical guide line connecting the dot to the
    /// track line at that x position.
    ///
    /// Color encodings (deliberately muted to live within the charcoal
    /// theme — pure RGB primaries would shout):
    ///   Switch normal    → soft white
    ///   Switch reversed  → muted accent blue
    ///   Signal clear     → muted success green
    ///   Signal approach  → muted warning amber
    ///   Signal stop      → muted danger red
    ///
    /// Vertical guide lines: ~1 px wide, low-alpha border color. They visually
    /// anchor each POI to the spot on the track it represents, regardless of
    /// where the line is on the chart vertically.
    ///
    /// All dots share the same POI rail at the bottom — type is inferred
    /// from color, not from rail position. Mileposts (when re-introduced
    /// from live data) get tiny ticks at the very bottom edge.
    /// </summary>
    public sealed class AnnotationLayer
    {
        // Visual constants in 1× scale pixels
        private const float DotDiameterPx = 7f;
        private const float POIRailFromBottomPx = 14f;

        private readonly Theme _theme;
        private readonly RouteData _route;
        private readonly float _uiScale;

        private VisualElement _container;
        private readonly List<AnnotationElement> _elements = new List<AnnotationElement>();

        public AnnotationLayer(Theme theme, RouteData route, float uiScale = 1f)
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

            RebuildElementsFromRoute();
            return _container;
        }

        /// <summary>
        /// Tear down + rebuild internal annotation elements when the route's
        /// annotation list has changed (different references or count).
        /// Live-data refresh repopulates the list every 5 Hz so this fires
        /// frequently, but each element is small and the rebuild is cheap.
        /// </summary>
        private void RebuildElementsFromRoute()
        {
            if (_elements.Count == _route.Annotations.Count)
            {
                bool same = true;
                for (int i = 0; i < _elements.Count; i++)
                {
                    if (!ReferenceEquals(_elements[i].Annotation, _route.Annotations[i]))
                    {
                        same = false;
                        break;
                    }
                }
                if (same) return;
            }

            foreach (var e in _elements)
            {
                if (e.Root != null && e.Root.parent != null) e.Root.RemoveFromHierarchy();
            }
            _elements.Clear();

            foreach (var ann in _route.Annotations)
            {
                var elem = BuildElementFor(ann);
                if (elem != null)
                {
                    _container.Add(elem.Root);
                    _elements.Add(elem);
                }
            }
        }

        public void Update(float pxPerFt, float startFt, float endFt, float pxPerElevFt)
        {
            if (_container == null || pxPerFt <= 0f) return;
            RebuildElementsFromRoute();
            var rect = _container.contentRect;
            if (rect.height <= 0f) return;

            // Compute per-element stack indices so multiple POIs of the same
            // type at the same x-bucket render stacked vertically rather
            // than overlapping. Bucket width is twice the dot diameter —
            // good enough to dedupe coincident signal pairs, doesn't merge
            // adjacent-but-distinct switches.
            var bucketSize = S(DotDiameterPx) * 2f / Mathf.Max(0.0001f, pxPerFt);
            var stackCounts = new Dictionary<int, int>();
            foreach (var elem in _elements)
            {
                if (elem.Annotation.Type != "signal" && elem.Annotation.Type != "switch")
                {
                    elem.StackIndex = 0;
                    continue;
                }
                int key = ((int)Mathf.Round(elem.Annotation.DistFt / bucketSize)) * 1000
                          + (elem.Annotation.Type == "signal" ? 1 : 2);  // separate buckets per type
                int idx;
                stackCounts.TryGetValue(key, out idx);
                elem.StackIndex = idx;
                stackCounts[key] = idx + 1;
            }

            var centerY = rect.height * 0.5f;
            foreach (var elem in _elements)
            {
                var ann = elem.Annotation;
                // For ranges, visibility is true if any part of the range
                // overlaps the visible window. For points, DistFtEnd ==
                // DistFt so the check degenerates to the point overlap.
                var rangeStart = ann.DistFt;
                var rangeEnd = (ann.DistFtEnd > ann.DistFt) ? ann.DistFtEnd : ann.DistFt;
                var visible = rangeEnd >= startFt - 50f && rangeStart <= endFt + 50f;
                elem.Root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (!visible) continue;

                var x = (rangeStart - startFt) * pxPerFt;
                var xEnd = (rangeEnd - startFt) * pxPerFt;
                var lineY = (pxPerElevFt > 0f)
                    ? centerY - _route.ElevationAt(rangeStart) * pxPerElevFt
                    : centerY;
                elem.Position(x, xEnd, centerY, lineY, rect.height, S, elem.StackIndex);
            }
        }

        private AnnotationElement BuildElementFor(RouteData.Annotation ann)
        {
            switch ((ann.Type ?? "").ToLowerInvariant())
            {
                case "switch":     return BuildDot(ann, ColorForSwitch(ann));
                case "signal":     return BuildDot(ann, ColorForSignal(ann));
                case "area":       return BuildArea(ann);
                case "span":       return BuildSpan(ann);
                case "station":    return BuildStation(ann);
                case "milepost":   return BuildMilepost(ann);
                case "industry":   return BuildIndustry(ann);
                case "endofline":  return BuildEndOfLine(ann);
                default:           return null;
            }
        }

        // ---- Generic colored dot with vertical guide line ----

        private AnnotationElement BuildDot(RouteData.Annotation ann, Color color)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            // Vertical guide: thin line connecting the track line to the dot.
            var guide = new VisualElement();
            guide.style.position = Position.Absolute;
            guide.style.width = 1;
            var guideColor = _theme.Border;
            guideColor.a *= 0.45f;
            guide.style.backgroundColor = guideColor;
            guide.pickingMode = PickingMode.Ignore;
            root.Add(guide);

            // Filled colored dot on the POI rail.
            var dot = new VisualElement();
            dot.style.position = Position.Absolute;
            var size = S(DotDiameterPx);
            dot.style.width = size;
            dot.style.height = size;
            var r = size * 0.5f;
            dot.style.borderTopLeftRadius     = r;
            dot.style.borderTopRightRadius    = r;
            dot.style.borderBottomLeftRadius  = r;
            dot.style.borderBottomRightRadius = r;
            dot.style.backgroundColor = color;
            // Subtle border for contrast against any background tone.
            var ring = _theme.Background;
            ring.a = 0.85f;
            dot.style.borderTopWidth    = 1;
            dot.style.borderBottomWidth = 1;
            dot.style.borderLeftWidth   = 1;
            dot.style.borderRightWidth  = 1;
            dot.style.borderTopColor    = ring;
            dot.style.borderBottomColor = ring;
            dot.style.borderLeftColor   = ring;
            dot.style.borderRightColor  = ring;
            dot.pickingMode = PickingMode.Ignore;
            root.Add(dot);

            return new AnnotationElement
            {
                Annotation = ann,
                Root = root,
                Position = (x, xEnd, centerY, lineY, fullH, scale, stackIndex) =>
                {
                    var dSize = scale(DotDiameterPx);
                    var dRadius = dSize * 0.5f;
                    // Stack index slides each successive same-bucket POI up
                    // by one dot-height (+ 1 px gap) so coincident signal
                    // pairs render as a vertical column rather than
                    // overlapping single dot.
                    var poiY = fullH - scale(POIRailFromBottomPx) - stackIndex * (dSize + 1f);

                    // Guide goes between the line's Y and the topmost dot
                    // in the stack so a stacked column shares one guide.
                    // Stops short of the dot so it doesn't bisect it.
                    var guideTop = Mathf.Min(lineY, poiY - dRadius);
                    var guideBot = Mathf.Max(lineY, poiY - dRadius);
                    guide.style.left = x;
                    guide.style.top = guideTop;
                    guide.style.height = Mathf.Max(0f, guideBot - guideTop);

                    dot.style.left = x - dRadius;
                    dot.style.top  = poiY - dRadius;
                }
            };
        }

        // ---- Color helpers ----

        /// <summary>
        /// Slightly desaturate + darken a base aspect color so it lives within
        /// the charcoal theme without overpowering the chart. Pure FF0000-style
        /// primaries would shout.
        /// </summary>
        private static Color Mute(Color c, float darken = 0.85f, float alpha = 0.95f)
        {
            return new Color(c.r * darken, c.g * darken, c.b * darken, alpha);
        }

        private Color ColorForSwitch(RouteData.Annotation ann)
        {
            var reversed = string.Equals(ann.Diverging, "reversed", StringComparison.OrdinalIgnoreCase);
            return reversed ? Mute(_theme.Accent) : Mute(_theme.TextPrimary);
        }

        private Color ColorForSignal(RouteData.Annotation ann)
        {
            switch ((ann.Aspect ?? "").ToLowerInvariant())
            {
                case "clear":    return Mute(_theme.Success);
                case "approach": return Mute(_theme.Warning);
                case "stop":     return Mute(_theme.Danger);
                default:         return Mute(_theme.TextMuted);
            }
        }

        // ---- Mileposts (kept; unused on live data for now) ----

        private AnnotationElement BuildMilepost(RouteData.Annotation ann)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            var tick = new VisualElement();
            tick.style.position = Position.Absolute;
            tick.style.width = 1;
            tick.style.backgroundColor = _theme.TextMuted;
            tick.style.height = ann.Labeled ? S(10) : S(8);
            tick.pickingMode = PickingMode.Ignore;
            root.Add(tick);

            Label label = null;
            if (ann.Labeled)
            {
                label = new Label(ann.Label);
                label.style.position = Position.Absolute;
                label.style.color = _theme.TextMuted;
                label.style.fontSize = S(9);
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.width = S(48);
                label.pickingMode = PickingMode.Ignore;
                root.Add(label);
            }

            return new AnnotationElement
            {
                Annotation = ann,
                Root = root,
                Position = (x, xEnd, centerY, lineY, fullH, scale, stackIndex) =>
                {
                    var tickH = ann.Labeled ? scale(10) : scale(8);
                    tick.style.left = x;
                    tick.style.top = fullH - tickH;
                    if (label != null)
                    {
                        label.style.left = x - scale(24);
                        label.style.top = fullH - tickH - scale(11);
                    }
                }
            };
        }

        // ---- Area: vertical line at start, label hugs the visible-window
        //      portion of the area, sits at the very top of the chart so
        //      it acts like a region label without crowding the line.

        private AnnotationElement BuildArea(RouteData.Annotation ann)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            // Faint vertical line at the area's start
            var entryLine = new VisualElement();
            entryLine.style.position = Position.Absolute;
            entryLine.style.width = 1;
            var entryColor = _theme.AccentHover;
            entryColor.a *= 0.35f;
            entryLine.style.backgroundColor = entryColor;
            entryLine.pickingMode = PickingMode.Ignore;
            root.Add(entryLine);

            // Label box centered horizontally within the visible portion
            // of the area, with a small backing so it stays readable when
            // it overlaps the gridlines.
            var labelBox = new VisualElement();
            labelBox.style.position = Position.Absolute;
            var bg = _theme.Panel;
            bg.a = Mathf.Min(1f, bg.a + 0.1f);
            labelBox.style.backgroundColor = bg;
            labelBox.style.paddingLeft = S(6);
            labelBox.style.paddingRight = S(6);
            labelBox.style.paddingTop = S(1);
            labelBox.style.paddingBottom = S(1);
            labelBox.style.borderTopLeftRadius     = S(3);
            labelBox.style.borderTopRightRadius    = S(3);
            labelBox.style.borderBottomLeftRadius  = S(3);
            labelBox.style.borderBottomRightRadius = S(3);
            labelBox.pickingMode = PickingMode.Ignore;
            root.Add(labelBox);

            var label = new Label(ann.Label.ToUpperInvariant());
            label.style.color = _theme.AccentHover;
            label.style.fontSize = S(11);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.pickingMode = PickingMode.Ignore;
            labelBox.Add(label);

            return new AnnotationElement
            {
                Annotation = ann,
                Root = root,
                Position = (x, xEnd, centerY, lineY, fullH, scale, stackIndex) =>
                {
                    // Entry line: full chart height
                    entryLine.style.left = x;
                    entryLine.style.top = 0;
                    entryLine.style.height = fullH;

                    // Label sits at top of chart, centered over the visible
                    // intersection of the area with the chart's x range.
                    var spanW = xEnd - x;
                    labelBox.style.top = scale(2);
                    labelBox.style.left = x + spanW * 0.5f - S(36);
                    labelBox.style.width = S(72);
                }
            };
        }

        // ---- Span: thin colored bar near the bottom (under the POI rail)
        //      with industry name centered. Only fires for spans on the
        //      currently lined route — parallel-track spans don't appear.

        private AnnotationElement BuildSpan(RouteData.Annotation ann)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            var bar = new VisualElement();
            bar.style.position = Position.Absolute;
            bar.style.height = S(3);
            bar.style.backgroundColor = Mute(_theme.Warning);
            bar.style.borderTopLeftRadius     = S(1);
            bar.style.borderTopRightRadius    = S(1);
            bar.style.borderBottomLeftRadius  = S(1);
            bar.style.borderBottomRightRadius = S(1);
            bar.pickingMode = PickingMode.Ignore;
            root.Add(bar);

            var label = new Label(ann.Label);
            label.style.position = Position.Absolute;
            label.style.color = _theme.Warning;
            label.style.fontSize = S(9);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.pickingMode = PickingMode.Ignore;
            root.Add(label);

            return new AnnotationElement
            {
                Annotation = ann,
                Root = root,
                Position = (x, xEnd, centerY, lineY, fullH, scale, stackIndex) =>
                {
                    var spanW = Mathf.Max(2f, xEnd - x);
                    var barY = fullH - scale(3);
                    bar.style.left = x;
                    bar.style.width = spanW;
                    bar.style.top = barY;
                    // Label centered on bar; clamped width to span (with min)
                    var labelW = Mathf.Max(S(40), spanW);
                    label.style.left = x + spanW * 0.5f - labelW * 0.5f;
                    label.style.width = labelW;
                    label.style.top = barY - S(13);
                }
            };
        }

        // ---- Station: small rectangle on the chart with name above.
        //      Renders unconditionally (proximity-based discovery).

        private AnnotationElement BuildStation(RouteData.Annotation ann)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            // Vertical guide from the track line down to the rectangle
            var guide = new VisualElement();
            guide.style.position = Position.Absolute;
            guide.style.width = 1;
            var guideColor = _theme.Border;
            guideColor.a *= 0.45f;
            guide.style.backgroundColor = guideColor;
            guide.pickingMode = PickingMode.Ignore;
            root.Add(guide);

            // Rectangle marker (taller than POI dots so it stands out)
            var rect = new VisualElement();
            rect.style.position = Position.Absolute;
            var rectW = S(10);
            var rectH = S(12);
            rect.style.width = rectW;
            rect.style.height = rectH;
            rect.style.backgroundColor = Mute(_theme.AccentHover);
            rect.style.borderTopLeftRadius     = S(2);
            rect.style.borderTopRightRadius    = S(2);
            rect.style.borderBottomLeftRadius  = S(2);
            rect.style.borderBottomRightRadius = S(2);
            var ringColor = _theme.Background;
            ringColor.a = 0.85f;
            rect.style.borderTopWidth    = 1;
            rect.style.borderBottomWidth = 1;
            rect.style.borderLeftWidth   = 1;
            rect.style.borderRightWidth  = 1;
            rect.style.borderTopColor    = ringColor;
            rect.style.borderBottomColor = ringColor;
            rect.style.borderLeftColor   = ringColor;
            rect.style.borderRightColor  = ringColor;
            rect.pickingMode = PickingMode.Ignore;
            root.Add(rect);

            var label = new Label(ann.Label);
            label.style.position = Position.Absolute;
            label.style.color = _theme.AccentHover;
            label.style.fontSize = S(10);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.width = S(120);
            label.pickingMode = PickingMode.Ignore;
            root.Add(label);

            return new AnnotationElement
            {
                Annotation = ann,
                Root = root,
                Position = (x, xEnd, centerY, lineY, fullH, scale, stackIndex) =>
                {
                    var w = scale(10);
                    var h = scale(12);
                    var rectY = fullH - scale(28);

                    // Guide between line and top of rectangle
                    var guideTop = Mathf.Min(lineY, rectY);
                    var guideBot = Mathf.Max(lineY, rectY);
                    guide.style.left = x;
                    guide.style.top = guideTop;
                    guide.style.height = Mathf.Max(0f, guideBot - guideTop);

                    rect.style.left = x - w * 0.5f;
                    rect.style.top = rectY;

                    label.style.left = x - scale(60);
                    label.style.top = rectY - scale(13);
                }
            };
        }

        private AnnotationElement BuildIndustry(RouteData.Annotation ann)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            var glyph = new VisualElement();
            glyph.style.position = Position.Absolute;
            glyph.style.width = S(8);
            glyph.style.height = S(8);
            glyph.style.backgroundColor = Mute(_theme.Warning);
            glyph.pickingMode = PickingMode.Ignore;
            root.Add(glyph);

            var label = new Label(ann.Label);
            label.style.position = Position.Absolute;
            label.style.color = _theme.Warning;
            label.style.fontSize = S(8);
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.width = S(96);
            label.pickingMode = PickingMode.Ignore;
            root.Add(label);

            return new AnnotationElement
            {
                Annotation = ann,
                Root = root,
                Position = (x, xEnd, centerY, lineY, fullH, scale, stackIndex) =>
                {
                    glyph.style.left = x - scale(4);
                    glyph.style.top = fullH - scale(20);
                    label.style.left = x - scale(48);
                    label.style.top = fullH - scale(11);
                }
            };
        }

        private AnnotationElement BuildEndOfLine(RouteData.Annotation ann)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            var line = new VisualElement();
            line.style.position = Position.Absolute;
            line.style.width = S(2);
            line.style.backgroundColor = Mute(_theme.Danger);
            line.pickingMode = PickingMode.Ignore;
            root.Add(line);

            var label = new Label("END");
            label.style.position = Position.Absolute;
            label.style.color = _theme.Danger;
            label.style.fontSize = S(9);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.width = S(40);
            label.pickingMode = PickingMode.Ignore;
            root.Add(label);

            return new AnnotationElement
            {
                Annotation = ann,
                Root = root,
                Position = (x, xEnd, centerY, lineY, fullH, scale, stackIndex) =>
                {
                    line.style.left = x;
                    line.style.top = 0;
                    line.style.height = fullH;
                    label.style.left = x - scale(36);
                    label.style.top = scale(2);
                }
            };
        }

        private sealed class AnnotationElement
        {
            public RouteData.Annotation Annotation;
            public VisualElement Root;
            // StackIndex = 0 means the topmost-on-rail dot; >0 stacks above.
            // AnnotationLayer.Update computes per-bucket per-type indices
            // before invoking Position so coincident signal pairs render
            // as a column rather than overlapping each other.
            public int StackIndex;
            // Caller invokes Position(x, xEnd, centerY, lineY, fullH, scale, stackIdx).
            // For point annotations xEnd == x. For range annotations (area,
            // span) xEnd is the end of the range in pixels. lineY is the
            // track line's local-y at the annotation's distFt — used by POI
            // types that draw a vertical guide.
            public Action<float, float, float, float, float, Func<float, float>, int> Position;
        }
    }
}
