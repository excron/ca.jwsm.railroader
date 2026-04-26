using UnityEngine;
using UnityEngine.UIElements;

namespace Ca.Jwsm.Railroader.Experiments.UiToolkitHud
{
    /// <summary>
    /// Phase 1 of the experiment: clone vanilla's LocoControlsUI in UI Toolkit.
    ///
    /// Mirrors structure observed in the GameUI scene dump:
    ///   - Outer panel anchored bottom-left, 450x160
    ///   - Selected Info section (engine name, info subtitles, speed readout)
    ///   - Divider
    ///   - Buttons row (Inspect, Follow, ...)
    ///   - Controls section (Manual mode: Train Brake, Independent, Throttle, Reverser)
    ///
    /// All sizing/positioning values lifted from the dump. Data is hardcoded
    /// (this is a visual fidelity test, not a real wiring).
    ///
    /// Phase 2 (if Phase 1 looks good): insert dynamic-brake slider between
    /// info and controls sections via AddDynamicBrakeSlider().
    /// </summary>
    public sealed class HudClone
    {
        private readonly Theme _theme;
        private VisualElement _root;
        private VisualElement _controlsContainer;

        public HudClone(Theme theme)
        {
            _theme = theme;
        }

        public VisualElement Build()
        {
            _root = NewPanel();

            _root.Add(BuildSelectedInfo());
            _root.Add(BuildDivider());
            _root.Add(BuildButtonsRow());

            _controlsContainer = BuildControlsContainer();
            _root.Add(_controlsContainer);

            _controlsContainer.Add(BuildManualControls());

            return _root;
        }

        /// <summary>
        /// Phase 2: insert a dynamic-brake slider between the info section and
        /// the existing controls grid. Demonstrates the additive value pattern.
        /// </summary>
        public void AddDynamicBrakeSlider()
        {
            var dynBrake = new VisualElement();
            dynBrake.style.flexDirection = FlexDirection.Row;
            dynBrake.style.alignItems = Align.Center;
            dynBrake.style.marginTop = 4;
            dynBrake.style.marginBottom = 4;

            var label = new Label("Dynamic Brake");
            label.style.color = _theme.TextMuted;
            label.style.fontSize = 10;
            label.style.width = 100;

            var slider = NewStyledSlider(0f, 100f, 0f);

            var valueLabel = new Label("0");
            valueLabel.style.color = _theme.TextPrimary;
            valueLabel.style.fontSize = 14;
            valueLabel.style.width = 28;
            valueLabel.style.marginLeft = 6;
            slider.RegisterValueChangedCallback(evt => valueLabel.text = $"{(int)evt.newValue}");

            dynBrake.Add(label);
            dynBrake.Add(slider);
            dynBrake.Add(valueLabel);

            // Insert as the first child of the controls container, above
            // the existing Manual controls grid.
            _controlsContainer.Insert(0, dynBrake);
        }

        // ---- Construction primitives ----

        private VisualElement NewPanel()
        {
            var panel = new VisualElement();
            panel.style.position = Position.Absolute;
            panel.style.left = 0;
            panel.style.bottom = 0;
            panel.style.width = 450;
            panel.style.height = 160;
            panel.style.marginLeft = 15;
            panel.style.marginBottom = 15;
            panel.style.paddingLeft = 15;
            panel.style.paddingRight = 15;
            panel.style.paddingTop = 12;
            panel.style.paddingBottom = 12;
            panel.style.backgroundColor = _theme.Panel;
            panel.style.borderTopLeftRadius = 8;
            panel.style.borderTopRightRadius = 8;
            panel.style.borderBottomLeftRadius = 8;
            panel.style.borderBottomRightRadius = 8;
            return panel;
        }

        private VisualElement BuildSelectedInfo()
        {
            var info = new VisualElement();
            info.style.flexDirection = FlexDirection.Row;
            info.style.justifyContent = Justify.SpaceBetween;
            info.style.height = 45;

            var nameInfo = new VisualElement();
            nameInfo.style.flexDirection = FlexDirection.Column;

            var engineName = new Label("SOU 630");
            engineName.style.color = _theme.TextPrimary;
            engineName.style.fontSize = 24;
            engineName.style.unityFontStyleAndWeight = FontStyle.Bold;

            var carInfo = new Label("8 Cars - 1,570 T");
            carInfo.style.color = _theme.TextMuted;
            carInfo.style.fontSize = 12;

            var location = new Label("Bryson Local");
            location.style.color = _theme.TextMuted;
            location.style.fontSize = 12;

            nameInfo.Add(engineName);
            nameInfo.Add(carInfo);
            nameInfo.Add(location);

            var speedReadout = new Label("15");
            speedReadout.style.color = _theme.TextPrimary;
            speedReadout.style.fontSize = 28;
            speedReadout.style.unityFontStyleAndWeight = FontStyle.Bold;
            speedReadout.style.alignSelf = Align.FlexStart;

            info.Add(nameInfo);
            info.Add(speedReadout);
            return info;
        }

        private VisualElement BuildDivider()
        {
            var divider = new VisualElement();
            divider.style.height = 1;
            divider.style.marginTop = 8;
            divider.style.marginBottom = 8;
            divider.style.backgroundColor = _theme.Border;
            return divider;
        }

        private VisualElement BuildButtonsRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.height = 30;

            row.Add(NewIconButton("I"));
            row.Add(NewIconButton("F"));
            row.Add(NewIconButton("..."));

            // Mode dropdown placeholder
            var modeBg = new VisualElement();
            modeBg.style.flexDirection = FlexDirection.Row;
            modeBg.style.alignItems = Align.Center;
            modeBg.style.justifyContent = Justify.SpaceBetween;
            modeBg.style.marginLeft = 8;
            modeBg.style.paddingLeft = 8;
            modeBg.style.paddingRight = 8;
            modeBg.style.width = 100;
            modeBg.style.backgroundColor = _theme.PanelStripe;
            modeBg.style.borderTopLeftRadius = 4;
            modeBg.style.borderTopRightRadius = 4;
            modeBg.style.borderBottomLeftRadius = 4;
            modeBg.style.borderBottomRightRadius = 4;

            var modeLabel = new Label("Manual");
            modeLabel.style.color = _theme.TextPrimary;
            modeLabel.style.fontSize = 12;

            var arrow = new Label("v");
            arrow.style.color = _theme.TextMuted;
            arrow.style.fontSize = 10;

            modeBg.Add(modeLabel);
            modeBg.Add(arrow);
            row.Add(modeBg);

            return row;
        }

        private Button NewIconButton(string letter)
        {
            var b = new Button();
            b.text = letter;
            b.style.width = 30;
            b.style.height = 30;
            b.style.marginLeft = 0;
            b.style.marginRight = 4;
            b.style.paddingLeft = 0;
            b.style.paddingRight = 0;
            b.style.paddingTop = 0;
            b.style.paddingBottom = 0;
            b.style.backgroundColor = _theme.PanelStripe;
            b.style.color = _theme.TextPrimary;
            b.style.fontSize = 14;
            b.style.borderTopLeftRadius = 4;
            b.style.borderTopRightRadius = 4;
            b.style.borderBottomLeftRadius = 4;
            b.style.borderBottomRightRadius = 4;
            b.style.borderTopWidth = 0;
            b.style.borderRightWidth = 0;
            b.style.borderBottomWidth = 0;
            b.style.borderLeftWidth = 0;
            return b;
        }

        private VisualElement BuildControlsContainer()
        {
            var container = new VisualElement();
            container.style.marginTop = 8;
            container.style.flexDirection = FlexDirection.Column;
            return container;
        }

        private VisualElement BuildManualControls()
        {
            // 2x2 grid via two rows of two sliders
            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Column;

            var topRow = new VisualElement();
            topRow.style.flexDirection = FlexDirection.Row;
            topRow.style.justifyContent = Justify.SpaceBetween;
            topRow.style.marginBottom = 6;
            topRow.Add(BuildLabeledSlider("Train Brake", 99));
            topRow.Add(BuildLabeledSlider("Throttle", 50));

            var bottomRow = new VisualElement();
            bottomRow.style.flexDirection = FlexDirection.Row;
            bottomRow.style.justifyContent = Justify.SpaceBetween;
            bottomRow.Add(BuildLabeledSlider("Independent", 30));
            bottomRow.Add(BuildLabeledSlider("Reverser", 50));

            grid.Add(topRow);
            grid.Add(bottomRow);
            return grid;
        }

        private VisualElement BuildLabeledSlider(string labelText, float initialValue)
        {
            var col = new VisualElement();
            col.style.flexDirection = FlexDirection.Column;
            col.style.width = 200;

            var label = new Label(labelText);
            label.style.color = _theme.TextMuted;
            label.style.fontSize = 10;

            var sliderRow = new VisualElement();
            sliderRow.style.flexDirection = FlexDirection.Row;
            sliderRow.style.alignItems = Align.Center;

            var slider = NewStyledSlider(0f, 100f, initialValue);

            var valueLabel = new Label($"{(int)initialValue}");
            valueLabel.style.color = _theme.TextPrimary;
            valueLabel.style.fontSize = 14;
            valueLabel.style.width = 28;
            valueLabel.style.marginLeft = 6;
            slider.RegisterValueChangedCallback(evt => valueLabel.text = $"{(int)evt.newValue}");

            sliderRow.Add(slider);
            sliderRow.Add(valueLabel);

            col.Add(label);
            col.Add(sliderRow);
            return col;
        }

        private Slider NewStyledSlider(float min, float max, float value)
        {
            var s = new Slider(min, max) { value = value };
            s.style.flexGrow = 1;
            // Slider's internal styling is mostly fine for a first pass;
            // deeper visual styling lands once we evaluate fidelity vs vanilla.
            return s;
        }
    }
}
