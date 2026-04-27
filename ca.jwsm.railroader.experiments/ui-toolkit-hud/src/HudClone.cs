using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ca.Jwsm.Railroader.Experiments.UiToolkitHud
{
    /// <summary>
    /// Phase 1 of the experiment: clone vanilla's LocoControlsUI in UI Toolkit.
    ///
    /// Mirrors structure observed in the GameUI scene dump:
    ///   - Outer panel anchored bottom-left, 450x160, RoundedRect20-style bg
    ///   - Selected Info (top, height 45):
    ///       Name Info row [Engine Name (large) | stacked (Engine Info 1, Engine Info 2)]
    ///       + Speed Readout right-aligned
    ///   - Divider (1-2px line) below
    ///   - Bottom row split into:
    ///       Buttons column on left (icon row + Mode dropdown + Options dropdown stacked)
    ///       Controls grid on right (2x2: Train Brake | Throttle / Independent | Reverser)
    ///
    /// Phase 2: AddDynamicBrakeSlider() inserts a new slider between info and controls.
    /// </summary>
    public sealed class HudClone
    {
        // Vanilla LocoControlsUI is 160 tall. We add 14 to fit the
        // coupler-forces pill strip below the brake strip in Selected Info
        // (10 pill + 4 padding). Adding the dynamic brake row further
        // extends the panel by 50 to give the new slider breathing room.
        private const float BasePanelHeight             = 174f;
        private const float HeightWithDynamicBrake      = 224f;
        private const float SelectedInfoHeight          = 59f;   // 35 top row + 10 brake + 4 pad + 10 coupler

        private readonly Theme _theme;
        private readonly float _uiScale;
        private VisualElement _root;
        private VisualElement _topSpacer;       // flex-grow gap that absorbs panel height growth
        private VisualElement _bottomRow;       // holds buttonsArea + controlsContainer (hugs bottom)
        private VisualElement _controlsContainer;
        private VisualElement _manualGrid;      // 2x2 slider grid (Train/Throttle, Indep/Reverser)

        public HudClone(Theme theme, float uiScale = 1f)
        {
            _theme = theme;
            _uiScale = uiScale;
        }

        public VisualElement Build()
        {
            _root = NewPanel();

            _root.Add(BuildSelectedInfo());
            _root.Add(BuildDivider());

            // Spacer absorbs extra panel height when AddDynamicBrakeSlider
            // bumps the height. Without dynBrake, the spacer naturally
            // collapses to zero. With it, dynBrake sits right under the
            // divider and the spacer pushes _bottomRow down to hug the
            // panel's bottom edge.
            _topSpacer = new VisualElement();
            _topSpacer.style.flexGrow = 1;
            _root.Add(_topSpacer);

            _bottomRow = new VisualElement();
            _bottomRow.style.flexDirection = FlexDirection.Row;
            _bottomRow.style.justifyContent = Justify.SpaceBetween;
            _bottomRow.style.flexShrink = 0;
            _root.Add(_bottomRow);

            _bottomRow.Add(BuildButtonsArea());

            _controlsContainer = BuildControlsContainer();
            _bottomRow.Add(_controlsContainer);
            _manualGrid = BuildManualControls();
            _controlsContainer.Add(_manualGrid);

            return _root;
        }

        /// <summary>
        /// Scales a pixel value by the game's UI scale factor. Use everywhere
        /// a literal pixel dimension is set so the entire panel scales
        /// proportionally with whatever the player has set in game options.
        /// Deterministic alternative to GPU transform-based scaling.
        /// </summary>
        private float S(float pixels) => pixels * _uiScale;

        /// <summary>
        /// Phase 2: insert a dynamic-brake slider above the throttle slider
        /// in the Manual controls grid. Visually it's a clone of the throttle
        /// (same width, label-above shape) sitting in the throttle's column
        /// with the Train Brake column position left empty as deliberate
        /// dead space. Bumps the panel height to make real room — does not
        /// compress existing slider rows.
        /// </summary>
        public void AddDynamicBrakeSlider()
        {
            // 1. Make space — taller HUD so the new row breathes
            _root.style.height = S(HeightWithDynamicBrake);

            // 2. Build a row matching the column structure of the existing
            //    Train Brake / Throttle row: [empty spacer | dyn brake]
            var dynRow = new VisualElement();
            dynRow.style.flexDirection = FlexDirection.Row;
            dynRow.style.justifyContent = Justify.SpaceBetween;
            dynRow.style.flexShrink = 0;
            // Reserve the same horizontal column footprint the bottom row's
            // controls use, so dynBrake aligns above the throttle column.
            dynRow.style.marginLeft = S(110 + 8);  // skip past buttons column + its right margin

            // Left column — empty (deliberate dead space; user OK with this)
            var spacer = new VisualElement();
            spacer.style.width = S(145);
            dynRow.Add(spacer);

            // Right column — clone of throttle's labeled slider
            var dynBrake = BuildLabeledSlider("DYNAMIC BRAKE", 0, S(145), labelBelow: false);
            dynRow.Add(dynBrake);

            // 3. Insert at the top of _root, between the divider and the
            //    flex-grow spacer. The spacer then absorbs the rest of the
            //    extra panel height, pushing the bottom row to hug the
            //    panel's bottom edge.
            var spacerIndex = _root.IndexOf(_topSpacer);
            _root.Insert(spacerIndex, dynRow);
        }

        // ---- Construction primitives ----

        private VisualElement NewPanel()
        {
            var panel = new VisualElement();
            panel.style.position = Position.Absolute;
            panel.style.left = 0;        // hug the screen edge (vanilla pattern)
            panel.style.bottom = 0;
            panel.style.width = S(450);
            panel.style.height = S(BasePanelHeight);
            panel.style.paddingLeft = S(15);
            panel.style.paddingRight = S(15);
            panel.style.paddingTop = S(12);
            panel.style.paddingBottom = S(12);
            panel.style.backgroundColor = _theme.Panel;
            // Only top-right corner is rounded — others sit flush with the
            // screen edge or the panel's own straight edge.
            panel.style.borderTopLeftRadius = 0;
            panel.style.borderTopRightRadius = S(8);
            panel.style.borderBottomLeftRadius = 0;
            panel.style.borderBottomRightRadius = 0;
            panel.style.flexDirection = FlexDirection.Column;
            return panel;
        }

        private VisualElement BuildSelectedInfo()
        {
            // Vanilla layout (per dump): Selected Info is a 45-tall block whose
            // children include a Name Info row (top), Speed Readout (top-right),
            // and a Brake Display strip anchored to the bottom (10 tall, full
            // width, populated dynamically from air state at runtime).
            //
            // We model this as a flex column: [topRow (flexGrow=1), brakeStrip (10)].
            var info = new VisualElement();
            info.style.flexDirection = FlexDirection.Column;
            info.style.height = S(SelectedInfoHeight);
            info.style.flexShrink = 0;

            // Top: name+sub-info on left, speed readout on right
            var topRow = new VisualElement();
            topRow.style.flexDirection = FlexDirection.Row;
            topRow.style.justifyContent = Justify.SpaceBetween;
            topRow.style.alignItems = Align.FlexStart;
            topRow.style.flexGrow = 1;

            var nameInfo = new VisualElement();
            nameInfo.style.flexDirection = FlexDirection.Row;
            nameInfo.style.alignItems = Align.FlexEnd;

            var engineName = new Label("SOU 630");
            engineName.style.color = _theme.TextPrimary;
            engineName.style.fontSize = S(26);
            engineName.style.unityFontStyleAndWeight = FontStyle.Bold;
            engineName.style.marginRight = S(10);

            var subInfo = new VisualElement();
            subInfo.style.flexDirection = FlexDirection.Column;
            subInfo.style.justifyContent = Justify.FlexEnd;
            subInfo.style.paddingBottom = S(4);

            var carInfo = new Label("8 Cars - 1,570 T");
            carInfo.style.color = _theme.TextMuted;
            carInfo.style.fontSize = S(11);

            var location = new Label("Bryson Local");
            location.style.color = _theme.TextMuted;
            location.style.fontSize = S(11);
            location.style.marginTop = S(2);

            subInfo.Add(carInfo);
            subInfo.Add(location);

            nameInfo.Add(engineName);
            nameInfo.Add(subInfo);

            var speedColumn = new VisualElement();
            speedColumn.style.flexDirection = FlexDirection.Column;
            speedColumn.style.alignItems = Align.FlexEnd;

            var speedNumber = new Label("15");
            speedNumber.style.color = _theme.TextPrimary;
            speedNumber.style.fontSize = S(26);
            speedNumber.style.unityFontStyleAndWeight = FontStyle.Bold;

            var speedUnit = new Label("MPH");
            speedUnit.style.color = _theme.TextMuted;
            speedUnit.style.fontSize = S(10);
            speedUnit.style.marginTop = S(-4);

            speedColumn.Add(speedNumber);
            speedColumn.Add(speedUnit);

            topRow.Add(nameInfo);
            topRow.Add(speedColumn);

            info.Add(topRow);
            // Brake display pill (vanilla — placeholder until real IAirState wiring)
            info.Add(BuildPillStrip(
                trackColor: _theme.Border,
                fillColor:  _theme.TextMuted,
                fillPct:    45f,
                marginTop:  0f));
            // Coupler forces pill (additive — would consume ICouplerForces stream
            // in production; warning-yellow indicates moderate stress here)
            info.Add(BuildPillStrip(
                trackColor: _theme.Border,
                fillColor:  _theme.Warning,
                fillPct:    35f,
                marginTop:  4f));
            return info;
        }

        /// <summary>
        /// Renders a 10px-tall pill bar (6px-tall pill inside, vertically
        /// centered) of fixed width. Used for both the vanilla-equivalent
        /// brake display strip and our additive coupler-forces strip.
        ///
        /// trackColor = the empty pill background.
        /// fillColor  = the partial fill on the left.
        /// fillPct    = 0..100 width percentage of the fill within the pill.
        /// marginTop  = px gap above the strip (0 for the first one).
        /// </summary>
        private VisualElement BuildPillStrip(Color trackColor, Color fillColor, float fillPct, float marginTop)
        {
            var strip = new VisualElement();
            strip.style.height = S(10);
            strip.style.marginTop = S(marginTop);
            strip.style.flexDirection = FlexDirection.Row;
            strip.style.alignItems = Align.Center;
            strip.style.flexShrink = 0;

            var pill = new VisualElement();
            pill.style.width = S(140);
            pill.style.height = S(6);
            pill.style.backgroundColor = trackColor;
            pill.style.borderTopLeftRadius = S(3);
            pill.style.borderTopRightRadius = S(3);
            pill.style.borderBottomLeftRadius = S(3);
            pill.style.borderBottomRightRadius = S(3);
            pill.style.flexDirection = FlexDirection.Row;

            var fill = new VisualElement();
            fill.style.width = new Length(fillPct, LengthUnit.Percent);
            fill.style.height = S(6);
            fill.style.backgroundColor = fillColor;
            fill.style.borderTopLeftRadius = S(3);
            fill.style.borderBottomLeftRadius = S(3);
            pill.Add(fill);

            strip.Add(pill);
            return strip;
        }

        private VisualElement BuildDivider()
        {
            var divider = new VisualElement();
            divider.style.height = S(2);
            divider.style.marginTop = S(4);
            divider.style.marginBottom = S(4);
            divider.style.flexShrink = 0;
            divider.style.backgroundColor = _theme.Border;
            return divider;
        }

        private VisualElement BuildButtonsArea()
        {
            // Vertical stack: icon row (I/F/...) → Mode dropdown → Options dropdown
            // → "Control Mode" label at the bottom (per vanilla's dump).
            // Width matches vanilla's Buttons container (100px in the dump).
            var area = new VisualElement();
            area.style.flexDirection = FlexDirection.Column;
            area.style.width = S(110);
            area.style.flexShrink = 0;

            var iconRow = new VisualElement();
            iconRow.style.flexDirection = FlexDirection.Row;
            iconRow.style.marginBottom = S(4);
            iconRow.Add(NewIconButton("I"));
            iconRow.Add(NewIconButton("F"));
            iconRow.Add(NewIconButton("..."));
            area.Add(iconRow);

            var modeDropdown = NewDropdown("Manual", S(100));
            modeDropdown.style.marginBottom = S(4);
            area.Add(modeDropdown);

            // (Vanilla has an Options gear-icon button here — 30x30 with a
            // GearIcon sprite. Skipped for now: without the icon asset it
            // would render as an empty button and confuse the layout.)

            var statusLabel = new Label("CONTROL MODE");
            statusLabel.style.color = _theme.TextMuted;
            statusLabel.style.fontSize = S(9);
            statusLabel.style.marginTop = S(4);
            area.Add(statusLabel);

            return area;
        }

        private Button NewIconButton(string letter)
        {
            var b = new Button();
            b.text = letter;
            b.style.width = S(30);
            b.style.height = S(30);
            b.style.marginLeft = 0;
            b.style.marginRight = S(4);
            b.style.paddingLeft = 0;
            b.style.paddingRight = 0;
            b.style.paddingTop = 0;
            b.style.paddingBottom = 0;
            b.style.backgroundColor = _theme.PanelStripe;
            b.style.color = _theme.TextPrimary;
            b.style.fontSize = S(13);
            b.style.borderTopLeftRadius = S(4);
            b.style.borderTopRightRadius = S(4);
            b.style.borderBottomLeftRadius = S(4);
            b.style.borderBottomRightRadius = S(4);
            b.style.borderTopWidth = 0;
            b.style.borderRightWidth = 0;
            b.style.borderBottomWidth = 0;
            b.style.borderLeftWidth = 0;
            return b;
        }

        private VisualElement NewDropdown(string text, float width)
        {
            // NOTE: width is already pre-scaled by the caller (callers pass S(...))
            var dd = new VisualElement();
            dd.style.flexDirection = FlexDirection.Row;
            dd.style.alignItems = Align.Center;
            dd.style.justifyContent = Justify.SpaceBetween;
            dd.style.width = width;
            dd.style.height = S(28);
            dd.style.paddingLeft = S(8);
            dd.style.paddingRight = S(8);
            dd.style.backgroundColor = _theme.PanelStripe;
            dd.style.borderTopLeftRadius = S(4);
            dd.style.borderTopRightRadius = S(4);
            dd.style.borderBottomLeftRadius = S(4);
            dd.style.borderBottomRightRadius = S(4);

            var label = new Label(text);
            label.style.color = _theme.TextPrimary;
            label.style.fontSize = S(11);

            var arrow = new Label("v");
            arrow.style.color = _theme.TextMuted;
            arrow.style.fontSize = S(9);

            dd.Add(label);
            dd.Add(arrow);
            return dd;
        }

        private VisualElement BuildControlsContainer()
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;
            container.style.flexGrow = 1;
            container.style.marginLeft = S(8);
            return container;
        }

        private VisualElement BuildManualControls()
        {
            // Vanilla pattern (per dump): top-row slider labels appear ABOVE
            // their sliders; bottom-row slider labels appear BELOW. Keeps the
            // labels concentrated in the middle horizontal band of the panel.
            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Column;
            grid.style.flexGrow = 1;
            grid.style.justifyContent = Justify.SpaceBetween;

            var topRow = new VisualElement();
            topRow.style.flexDirection = FlexDirection.Row;
            topRow.style.justifyContent = Justify.SpaceBetween;
            // Vanilla TMP uses an UPPERCASE font material preset so underlying
            // mixed-case text ("Train Brake") renders all-caps. UI Toolkit
            // doesn't have text-transform; we just uppercase at the source.
            topRow.Add(BuildLabeledSlider("TRAIN BRAKE", 99, S(145), labelBelow: false));
            topRow.Add(BuildLabeledSlider("THROTTLE",    50, S(145), labelBelow: false));

            var bottomRow = new VisualElement();
            bottomRow.style.flexDirection = FlexDirection.Row;
            bottomRow.style.justifyContent = Justify.SpaceBetween;
            bottomRow.Add(BuildLabeledSlider("INDEPENDENT", 30, S(145), labelBelow: true));
            // Reverser column is 145 wide (so it aligns under Throttle column
            // above), but the slider+REV/FWD twin labels only occupy 110 wide,
            // left-aligned within the column. Matches vanilla which has the
            // reverser at x=160 with Rev/Fwd labels at x=160/x=210.
            bottomRow.Add(BuildReverserColumn(50, columnWidth: S(145), innerWidth: S(110)));

            grid.Add(topRow);
            grid.Add(bottomRow);
            return grid;
        }

        private VisualElement BuildLabeledSlider(string labelText, float initialValue, float width, bool labelBelow)
        {
            // NOTE: width is already pre-scaled by the caller
            var col = new VisualElement();
            col.style.flexDirection = FlexDirection.Column;
            col.style.width = width;

            var label = new Label(labelText);
            label.style.color = _theme.TextMuted;
            label.style.fontSize = S(10);
            if (labelBelow)
                label.style.marginTop = S(2);
            else
                label.style.marginBottom = S(2);

            var sliderRow = new VisualElement();
            sliderRow.style.flexDirection = FlexDirection.Row;
            sliderRow.style.alignItems = Align.Center;

            var slider = NewStyledSlider(0f, 100f, initialValue);

            var valueLabel = new Label($"{(int)initialValue}");
            valueLabel.style.color = _theme.TextPrimary;
            valueLabel.style.fontSize = S(13);
            valueLabel.style.width = S(28);
            valueLabel.style.marginLeft = S(6);
            slider.RegisterValueChangedCallback(evt => valueLabel.text = $"{(int)evt.newValue}");

            sliderRow.Add(slider);
            sliderRow.Add(valueLabel);

            // Order children based on label position
            if (labelBelow)
            {
                col.Add(sliderRow);
                col.Add(label);
            }
            else
            {
                col.Add(label);
                col.Add(sliderRow);
            }
            return col;
        }

        private Slider NewStyledSlider(float min, float max, float value)
        {
            var s = new Slider(min, max) { value = value };
            s.style.flexGrow = 1;
            // Default UI Toolkit slider visuals; deeper styling lands once
            // we evaluate fidelity vs vanilla.
            return s;
        }

        /// <summary>
        /// Reverser-specific labeled slider. Vanilla has a 110-wide slider
        /// with two separate labels (Rev on left, Fwd on right) below it,
        /// rather than the single channel label other sliders use. Renders
        /// in a wider container column so it aligns with Throttle above.
        /// </summary>
        private VisualElement BuildReverserColumn(float initialValue, float columnWidth, float innerWidth)
        {
            var col = new VisualElement();
            col.style.flexDirection = FlexDirection.Column;
            col.style.width = columnWidth;
            col.style.alignItems = Align.FlexStart;  // left-align inner content

            // Inner column hosts the slider+value row and the Rev/Fwd labels row
            var inner = new VisualElement();
            inner.style.flexDirection = FlexDirection.Column;
            inner.style.width = innerWidth;

            var sliderRow = new VisualElement();
            sliderRow.style.flexDirection = FlexDirection.Row;
            sliderRow.style.alignItems = Align.Center;

            var slider = NewStyledSlider(0f, 100f, initialValue);

            var valueLabel = new Label($"{(int)initialValue}");
            valueLabel.style.color = _theme.TextPrimary;
            valueLabel.style.fontSize = S(13);
            valueLabel.style.width = S(28);
            valueLabel.style.marginLeft = S(6);
            slider.RegisterValueChangedCallback(evt => valueLabel.text = $"{(int)evt.newValue}");

            sliderRow.Add(slider);
            sliderRow.Add(valueLabel);

            var labelRow = new VisualElement();
            labelRow.style.flexDirection = FlexDirection.Row;
            labelRow.style.justifyContent = Justify.SpaceBetween;
            labelRow.style.marginTop = S(2);

            var rev = new Label("REV");
            rev.style.color = _theme.TextMuted;
            rev.style.fontSize = S(10);

            var fwd = new Label("FWD");
            fwd.style.color = _theme.TextMuted;
            fwd.style.fontSize = S(10);

            labelRow.Add(rev);
            labelRow.Add(fwd);

            inner.Add(sliderRow);
            inner.Add(labelRow);
            col.Add(inner);
            return col;
        }
    }
}
