using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ca.Jwsm.Railroader.Experiments.TrackProfile
{
    /// <summary>
    /// Renders annotations along the x-axis: mileposts, switches, signals,
    /// stations, industries, end-of-line markers. Each annotation is an
    /// absolutely-positioned glyph + optional label. Visibility is gated on
    /// whether the annotation falls inside the current ViewWindow.
    ///
    /// Visual layering (high y to low y on screen, top to bottom):
    ///   - Vehicle row centered on 0% line (rendered separately by VehicleRow)
    ///   - Switch / signal / station markers float above or below the line
    ///   - Mileposts as ticks at the bottom edge of the chart with labels
    ///     beneath
    /// </summary>
    public sealed class AnnotationLayer
    {
        private const float MarkerWidthPx = 16f;
        private const float MarkerHeightPx = 16f;
        private const float MilepostTickHeightPx = 8f;

        private readonly Theme _theme;
        private readonly RouteData _route;
        private readonly float _uiScale;

        private VisualElement _container;
        // Pre-built annotation elements (one per route annotation). We toggle
        // visibility + reposition each Update() rather than rebuilding the
        // tree, which keeps the per-frame cost trivial.
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
        /// Tear down and rebuild internal annotation elements to match the
        /// current Route.Annotations list. Called when annotations might
        /// have changed (live-data refreshes, switch overrides discovered
        /// along route, etc.). Cheap when annotations are stable; we
        /// short-circuit on identity match.
        /// </summary>
        private void RebuildElementsFromRoute()
        {
            // Identity-cheap check: if the list reference + count match,
            // assume nothing changed. Live-source might mutate the same
            // list in place — fall back to count check.
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

            // Tear down existing
            foreach (var e in _elements)
            {
                if (e.Root != null && e.Root.parent != null) e.Root.RemoveFromHierarchy();
            }
            _elements.Clear();

            // Rebuild
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

        public void Update(float pxPerFt, float startFt, float endFt)
        {
            if (_container == null || pxPerFt <= 0f) return;
            RebuildElementsFromRoute();
            var rect = _container.contentRect;
            if (rect.height <= 0f) return;

            var centerY = rect.height * 0.5f;
            foreach (var elem in _elements)
            {
                var visible = elem.Annotation.DistFt >= startFt - 50f
                           && elem.Annotation.DistFt <= endFt + 50f;
                elem.Root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (!visible) continue;

                var x = (elem.Annotation.DistFt - startFt) * pxPerFt;
                elem.Position(x, centerY, rect.height, S);
            }
        }

        private AnnotationElement BuildElementFor(RouteData.Annotation ann)
        {
            switch ((ann.Type ?? "").ToLowerInvariant())
            {
                case "milepost":   return BuildMilepost(ann);
                case "switch":     return BuildSwitch(ann);
                case "signal":     return BuildSignal(ann);
                case "station":    return BuildStation(ann);
                case "industry":   return BuildIndustry(ann);
                case "endofline":  return BuildEndOfLine(ann);
                default:           return null;
            }
        }

        // ---- Mileposts ----
        // Vertical tick at the bottom of the chart. Labeled posts (every
        // mile) get a text label below the tick; unlabeled (half-mile) ticks
        // are shorter and unmarked.

        private AnnotationElement BuildMilepost(RouteData.Annotation ann)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            var tick = new VisualElement();
            tick.style.position = Position.Absolute;
            tick.style.width = 1;
            tick.style.backgroundColor = _theme.TextMuted;
            tick.style.height = ann.Labeled ? S(MilepostTickHeightPx + 2) : S(MilepostTickHeightPx);
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
                Position = (x, centerY, fullH, scale) =>
                {
                    var tickH = ann.Labeled ? scale(MilepostTickHeightPx + 2) : scale(MilepostTickHeightPx);
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

        // ---- Switches ----
        // Diamond glyph at the bottom-edge of the chart, with the switch's
        // identifier label above. Color borrows the theme accent.

        private AnnotationElement BuildSwitch(RouteData.Annotation ann)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            var glyph = new Label("◆");
            glyph.style.position = Position.Absolute;
            glyph.style.color = _theme.Accent;
            glyph.style.fontSize = S(11);
            glyph.style.unityTextAlign = TextAnchor.MiddleCenter;
            glyph.style.width = S(MarkerWidthPx);
            glyph.style.height = S(MarkerHeightPx);
            glyph.pickingMode = PickingMode.Ignore;
            root.Add(glyph);

            var label = new Label(ann.Label);
            label.style.position = Position.Absolute;
            label.style.color = _theme.Accent;
            label.style.fontSize = S(8);
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.width = S(40);
            label.pickingMode = PickingMode.Ignore;
            root.Add(label);

            return new AnnotationElement
            {
                Annotation = ann,
                Root = root,
                Position = (x, centerY, fullH, scale) =>
                {
                    var glyphSize = scale(MarkerHeightPx);
                    glyph.style.left = x - glyphSize * 0.5f;
                    glyph.style.top = fullH - glyphSize - scale(2);
                    label.style.left = x - scale(20);
                    label.style.top = fullH - glyphSize - scale(11);
                }
            };
        }

        // ---- Signals ----
        // Filled circle at the top of the chart, color-coded by aspect.

        private AnnotationElement BuildSignal(RouteData.Annotation ann)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            var dot = new VisualElement();
            dot.style.position = Position.Absolute;
            dot.style.width = S(8);
            dot.style.height = S(8);
            dot.style.borderTopLeftRadius = S(4);
            dot.style.borderTopRightRadius = S(4);
            dot.style.borderBottomLeftRadius = S(4);
            dot.style.borderBottomRightRadius = S(4);
            dot.style.backgroundColor = ColorForAspect(ann.Aspect);
            dot.pickingMode = PickingMode.Ignore;
            root.Add(dot);

            return new AnnotationElement
            {
                Annotation = ann,
                Root = root,
                Position = (x, centerY, fullH, scale) =>
                {
                    var size = scale(8);
                    dot.style.left = x - size * 0.5f;
                    dot.style.top = scale(2);
                }
            };
        }

        private Color ColorForAspect(string aspect)
        {
            switch ((aspect ?? "").ToLowerInvariant())
            {
                case "clear":    return _theme.Success;
                case "approach": return _theme.Warning;
                case "stop":     return _theme.Danger;
                default:         return _theme.TextMuted;
            }
        }

        // ---- Stations ----
        // Vertical accent line spanning chart height, with station name above.

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
                Position = (x, centerY, fullH, scale) =>
                {
                    line.style.left = x;
                    line.style.top = scale(14);
                    line.style.height = fullH - scale(28);
                    label.style.left = x - scale(48);
                    label.style.top = 0;
                }
            };
        }

        // ---- Industries ----
        // Square glyph at the bottom of the chart with industry name label.
        // Visually quieter than stations.

        private AnnotationElement BuildIndustry(RouteData.Annotation ann)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            var glyph = new VisualElement();
            glyph.style.position = Position.Absolute;
            glyph.style.width = S(8);
            glyph.style.height = S(8);
            glyph.style.backgroundColor = _theme.Warning;
            glyph.style.borderTopLeftRadius = S(1);
            glyph.style.borderTopRightRadius = S(1);
            glyph.style.borderBottomLeftRadius = S(1);
            glyph.style.borderBottomRightRadius = S(1);
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
                Position = (x, centerY, fullH, scale) =>
                {
                    glyph.style.left = x - scale(4);
                    glyph.style.top = fullH - scale(20);
                    label.style.left = x - scale(48);
                    label.style.top = fullH - scale(11);
                }
            };
        }

        // ---- End of line ----
        // Bold red vertical line spanning full chart height with "END" label.

        private AnnotationElement BuildEndOfLine(RouteData.Annotation ann)
        {
            var root = new VisualElement();
            root.style.position = Position.Absolute;
            root.pickingMode = PickingMode.Ignore;

            var line = new VisualElement();
            line.style.position = Position.Absolute;
            line.style.width = S(2);
            line.style.backgroundColor = _theme.Danger;
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
                Position = (x, centerY, fullH, scale) =>
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
            // Caller invokes Position(x, centerY, fullH, S) every Update() to
            // re-place the element. The closure captures the inner Visual
            // Elements built once at Build() time.
            public System.Action<float, float, float, System.Func<float, float>> Position;
        }
    }
}
