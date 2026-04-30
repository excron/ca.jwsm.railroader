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

            var centerY = rect.height * 0.5f;
            foreach (var elem in _elements)
            {
                var ann = elem.Annotation;
                var visible = ann.DistFt >= startFt - 50f && ann.DistFt <= endFt + 50f;
                elem.Root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (!visible) continue;

                var x = (ann.DistFt - startFt) * pxPerFt;
                // Track-line Y at the POI's distFt — used so the vertical
                // guide can connect the line itself (which moves with the
                // grade) to the POI rail (anchored at chart bottom).
                var lineY = (pxPerElevFt > 0f)
                    ? centerY - _route.ElevationAt(ann.DistFt) * pxPerElevFt
                    : centerY;
                elem.Position(x, centerY, lineY, rect.height, S);
            }
        }

        private AnnotationElement BuildElementFor(RouteData.Annotation ann)
        {
            switch ((ann.Type ?? "").ToLowerInvariant())
            {
                case "switch":     return BuildDot(ann, ColorForSwitch(ann));
                case "signal":     return BuildDot(ann, ColorForSignal(ann));
                case "milepost":   return BuildMilepost(ann);
                case "station":    return BuildStation(ann);
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
                Position = (x, centerY, lineY, fullH, scale) =>
                {
                    var poiY = fullH - scale(POIRailFromBottomPx);
                    var dSize = scale(DotDiameterPx);
                    var dRadius = dSize * 0.5f;

                    // Guide goes between the line's Y and the dot's Y, on
                    // whichever side the line is. Stops short of the dot so
                    // it doesn't bisect it.
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
                Position = (x, centerY, lineY, fullH, scale) =>
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

        // ---- Stations / industries / EOL (kept for future live wiring) ----

        private AnnotationElement BuildStation(RouteData.Annotation ann)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            var line = new VisualElement();
            line.style.position = Position.Absolute;
            line.style.width = 1;
            var lineColor = _theme.AccentHover;
            lineColor.a *= 0.5f;
            line.style.backgroundColor = lineColor;
            line.pickingMode = PickingMode.Ignore;
            root.Add(line);

            var label = new Label(ann.Label);
            label.style.position = Position.Absolute;
            label.style.color = _theme.AccentHover;
            label.style.fontSize = S(9);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.width = S(96);
            label.pickingMode = PickingMode.Ignore;
            root.Add(label);

            return new AnnotationElement
            {
                Annotation = ann,
                Root = root,
                Position = (x, centerY, lineY, fullH, scale) =>
                {
                    line.style.left = x;
                    line.style.top = scale(14);
                    line.style.height = fullH - scale(28);
                    label.style.left = x - scale(48);
                    label.style.top = 0;
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
                Position = (x, centerY, lineY, fullH, scale) =>
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
                Position = (x, centerY, lineY, fullH, scale) =>
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
            // Caller invokes Position(x, centerY, lineY, fullH, scale) per
            // refresh. lineY is the track line's local-y at the annotation's
            // distFt — used by POI types that draw a vertical guide.
            public Action<float, float, float, float, Func<float, float>> Position;
        }
    }
}
