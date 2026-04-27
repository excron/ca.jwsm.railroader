using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ca.Jwsm.Railroader.Experiments.UiToolkitHud
{
    /// <summary>
    /// Phase 3 of the experiment: clone vanilla's ConsistInspectorPanel
    /// based on the GameUI scene dump (lines 872-988).
    ///
    /// Vanilla measurements (1x):
    ///   Panel:        797.35 x 303.13   (we round to 800 x 304)
    ///   Title bar:    full-width minus ~163 (room for Reload/Close), 28 tall
    ///   Cell:         220 x 240
    ///   Cell padding: 16 inset on all sides for inner Content
    ///   Per-cell content layout (absolute in vanilla — we mirror with flex):
    ///     CarName/CarType row at y=-14.8 from top of Content (size 20 font, bold)
    ///     Destination       at y=-43.63                       (size 20)
    ///     Car Content       at y=-69.33                       (size 16)
    ///     Brake Stats       at y=-142.31, centered, 91x100   (size 20, multiline)
    ///     Anglecock L/R     at y=~63 from bottom              (sliders, 56x20)
    ///     Cut L | Follow | Cut R at y=~14 from bottom         (Follow 62x26, Cut 42x26)
    ///
    /// Charcoal palette intentionally — vanilla's inspector is unstyled basic
    /// gray; ours is a guaranteed visual upgrade. Structure mirrors vanilla;
    /// visual identity is ours.
    ///
    /// Hardcoded sample data. Real wiring deferred to production.
    ///
    /// Phase 4: AddDpuCheckbox() inserts DPU checkbox under MU on loco cells.
    /// </summary>
    public sealed class InspectorClone
    {
        private readonly Theme _theme;
        private readonly float _uiScale;
        private VisualElement _root;
        private List<VisualElement> _cellExtraSlots = new List<VisualElement>();

        public InspectorClone(Theme theme, float uiScale = 1f)
        {
            _theme = theme;
            _uiScale = uiScale;
        }

        public VisualElement Build()
        {
            _root = NewWindow();
            _root.Add(BuildTitleBar());
            _root.Add(BuildContent());
            return _root;
        }

        // ---- Top-level window (vanilla: 797 x 303) ----

        private VisualElement NewWindow()
        {
            var w = new VisualElement();
            w.style.position = Position.Absolute;
            w.style.top = S(20);
            w.style.left = S(20);
            w.style.width = S(800);
            w.style.height = S(304);
            w.style.flexDirection = FlexDirection.Column;
            w.style.backgroundColor = _theme.Panel;
            w.style.borderTopLeftRadius = S(8);
            w.style.borderTopRightRadius = S(8);
            w.style.borderBottomLeftRadius = S(8);
            w.style.borderBottomRightRadius = S(8);
            w.style.borderTopWidth = S(1);
            w.style.borderRightWidth = S(1);
            w.style.borderBottomWidth = S(1);
            w.style.borderLeftWidth = S(1);
            w.style.borderTopColor = _theme.Border;
            w.style.borderRightColor = _theme.Border;
            w.style.borderBottomColor = _theme.Border;
            w.style.borderLeftColor = _theme.Border;
            return w;
        }

        // Vanilla title bar: 28 tall, full-width minus ~163 (room for buttons
        // on the right). We use a flex row instead of vanilla's absolute layout.
        private VisualElement BuildTitleBar()
        {
            var bar = new VisualElement();
            bar.style.height = S(28);
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.paddingLeft = S(8);
            bar.style.paddingRight = S(4);
            bar.style.backgroundColor = _theme.TitlebarAccent;
            bar.style.borderTopLeftRadius = S(7);
            bar.style.borderTopRightRadius = S(7);
            bar.style.flexShrink = 0;

            // Vanilla title text: LibreBaskerville-Regular SDF size 16 #C5C5C5
            var title = new Label("Consist Inspector");
            title.style.color = _theme.TextPrimary;
            title.style.fontSize = S(13);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexGrow = 1;
            bar.Add(title);

            bar.Add(NewChromeButton("Reload", S(60)));
            bar.Add(NewChromeButton("Close",  S(50)));

            return bar;
        }

        private Button NewChromeButton(string text, float width)
        {
            var b = new Button();
            b.text = text;
            b.style.width = width;
            b.style.height = S(20);
            b.style.marginLeft = S(4);
            b.style.marginRight = 0;
            b.style.paddingLeft = 0;
            b.style.paddingRight = 0;
            b.style.paddingTop = 0;
            b.style.paddingBottom = 0;
            b.style.backgroundColor = _theme.PanelStripe;
            b.style.color = _theme.TextPrimary;
            b.style.fontSize = S(11);
            b.style.borderTopLeftRadius = S(3);
            b.style.borderTopRightRadius = S(3);
            b.style.borderBottomLeftRadius = S(3);
            b.style.borderBottomRightRadius = S(3);
            b.style.borderTopWidth = 0;
            b.style.borderRightWidth = 0;
            b.style.borderBottomWidth = 0;
            b.style.borderLeftWidth = 0;
            return b;
        }

        // ---- Scrollable horizontal cell list ----

        private VisualElement BuildContent()
        {
            var scroll = new ScrollView(ScrollViewMode.Horizontal);
            scroll.style.flexGrow = 1;
            scroll.style.paddingTop = S(8);
            scroll.style.paddingBottom = S(8);
            scroll.style.paddingLeft = S(8);
            scroll.style.paddingRight = S(8);

            foreach (var car in SampleCars())
            {
                scroll.Add(BuildCell(car));
            }
            return scroll;
        }

        private static IEnumerable<CarData> SampleCars()
        {
            yield return new CarData { Name = "SOU 630",   Type = "Loco", Destination = "Bryson Local",  Contents = "—",     BP = 90, BC = 0, RS = 90, IsLoco = true  };
            yield return new CarData { Name = "SOU 19440", Type = "XM",   Destination = "Andrews Yard",  Contents = "Boxes", BP = 90, BC = 0, RS = 90, IsLoco = false };
            yield return new CarData { Name = "SOU 21134", Type = "GH",   Destination = "Andrews Yard",  Contents = "Coal",  BP = 88, BC = 2, RS = 90, IsLoco = false };
            yield return new CarData { Name = "SOU 92847", Type = "TM",   Destination = "Andrews Yard",  Contents = "Logs",  BP = 90, BC = 0, RS = 90, IsLoco = false };
            yield return new CarData { Name = "CAB 1224",  Type = "CB",   Destination = "Andrews Yard",  Contents = "—",     BP = 90, BC = 0, RS = 90, IsLoco = false };
        }

        // Vanilla cell: 220 x 240. Inner Content: 16px inset on all sides.
        private VisualElement BuildCell(CarData car)
        {
            var cell = new VisualElement();
            cell.style.width = S(220);
            cell.style.height = S(240);
            cell.style.marginRight = S(8);
            cell.style.paddingTop = S(8);
            cell.style.paddingBottom = S(8);
            cell.style.paddingLeft = S(8);
            cell.style.paddingRight = S(8);
            cell.style.backgroundColor = _theme.PanelStripe;
            cell.style.borderTopLeftRadius = S(6);
            cell.style.borderTopRightRadius = S(6);
            cell.style.borderBottomLeftRadius = S(6);
            cell.style.borderBottomRightRadius = S(6);
            cell.style.flexDirection = FlexDirection.Column;
            cell.style.flexShrink = 0;

            // Top: CarName (left, bold size ~16) | CarType (right, bold size ~14)
            // Vanilla uses size 20 LibreBaskerville-Bold; we scale slightly down for Inter/system fonts.
            var topRow = new VisualElement();
            topRow.style.flexDirection = FlexDirection.Row;
            topRow.style.justifyContent = Justify.SpaceBetween;
            topRow.style.alignItems = Align.Center;

            var carName = new Label(car.Name);
            carName.style.color = _theme.TextPrimary;
            carName.style.fontSize = S(16);
            carName.style.unityFontStyleAndWeight = FontStyle.Bold;

            var carType = new Label(car.Type);
            carType.style.color = car.IsLoco ? _theme.Accent : _theme.TextPrimary;
            carType.style.fontSize = S(14);
            carType.style.unityFontStyleAndWeight = FontStyle.Bold;

            topRow.Add(carName);
            topRow.Add(carType);
            cell.Add(topRow);

            // Destination (vanilla: y=-43.63, size 20)
            var destination = new Label(car.Destination);
            destination.style.color = _theme.TextMuted;
            destination.style.fontSize = S(13);
            destination.style.marginTop = S(4);
            cell.Add(destination);

            // Contents (vanilla: y=-69.33, size 16)
            var contents = new Label(car.Contents);
            contents.style.color = _theme.TextMuted;
            contents.style.fontSize = S(11);
            cell.Add(contents);

            // Brake Stats (vanilla: centered mid-cell, multiline, size 20)
            // Vanilla text: "BP 90\nBC 0\nRS 90\n?? ??" — matches our render
            var brakeStats = new Label($"BP {car.BP}\nBC {car.BC}\nRS {car.RS}");
            brakeStats.style.color = _theme.TextPrimary;
            brakeStats.style.fontSize = S(13);
            brakeStats.style.unityTextAlign = TextAnchor.UpperCenter;
            brakeStats.style.alignSelf = Align.Center;
            brakeStats.style.marginTop = S(8);
            brakeStats.style.whiteSpace = WhiteSpace.Normal;
            cell.Add(brakeStats);

            // MU checkbox slot (locos only; Phase 4 inserts DPU below)
            var extraSlot = new VisualElement();
            extraSlot.style.flexDirection = FlexDirection.Column;
            extraSlot.style.marginTop = S(6);
            if (car.IsLoco)
            {
                extraSlot.Add(BuildLabeledCheckbox("MU", true));
            }
            cell.Add(extraSlot);
            _cellExtraSlots.Add(extraSlot);

            // Spacer pushes anglecock/cut rows to bottom
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            cell.Add(spacer);

            // Anglecock row (vanilla: L at y=61 from bottom, R at y=63.8, both 56x20 sliders)
            var anglRow = new VisualElement();
            anglRow.style.flexDirection = FlexDirection.Row;
            anglRow.style.justifyContent = Justify.SpaceBetween;
            anglRow.style.alignItems = Align.Center;
            anglRow.style.marginBottom = S(6);
            anglRow.Add(NewAnglecockSlider(true));
            anglRow.Add(NewAnglecockSlider(true));
            cell.Add(anglRow);

            // Cut/Follow/Cut row (vanilla: y=~14 from bottom)
            //   Cut L at left (42 wide), Follow centered (62 wide), Cut R at right (42 wide)
            var bottomRow = new VisualElement();
            bottomRow.style.flexDirection = FlexDirection.Row;
            bottomRow.style.justifyContent = Justify.SpaceBetween;
            bottomRow.style.alignItems = Align.Center;
            bottomRow.Add(NewCutLeverButton());
            bottomRow.Add(NewFollowButton());
            bottomRow.Add(NewCutLeverButton());
            cell.Add(bottomRow);

            return cell;
        }

        // Vanilla anglecock: a Slider 56.3x20 (Background + Fill + Handle).
        // Our placeholder: a small slider with a fixed value to show the shape.
        private VisualElement NewAnglecockSlider(bool open)
        {
            var s = new Slider(0f, 1f) { value = open ? 1f : 0f };
            s.style.width = S(56);
            s.style.height = S(20);
            s.style.marginLeft = 0;
            s.style.marginRight = 0;
            return s;
        }

        // Vanilla Cut Lever: 42.66 x 26.46, "Cut" text size 16
        private Button NewCutLeverButton()
        {
            var b = new Button();
            b.text = "Cut";
            b.style.width = S(42);
            b.style.height = S(26);
            b.style.marginLeft = 0;
            b.style.marginRight = 0;
            b.style.paddingLeft = 0;
            b.style.paddingRight = 0;
            b.style.paddingTop = 0;
            b.style.paddingBottom = 0;
            b.style.backgroundColor = _theme.Panel;
            b.style.color = _theme.TextPrimary;
            b.style.fontSize = S(11);
            b.style.borderTopLeftRadius = S(3);
            b.style.borderTopRightRadius = S(3);
            b.style.borderBottomLeftRadius = S(3);
            b.style.borderBottomRightRadius = S(3);
            b.style.borderTopWidth = 0;
            b.style.borderRightWidth = 0;
            b.style.borderBottomWidth = 0;
            b.style.borderLeftWidth = 0;
            return b;
        }

        // Vanilla Follow: 62.42 x 26.46, "Follow" text size 16
        private Button NewFollowButton()
        {
            var b = new Button();
            b.text = "Follow";
            b.style.width = S(62);
            b.style.height = S(26);
            b.style.marginLeft = 0;
            b.style.marginRight = 0;
            b.style.paddingLeft = 0;
            b.style.paddingRight = 0;
            b.style.paddingTop = 0;
            b.style.paddingBottom = 0;
            b.style.backgroundColor = _theme.Accent;
            b.style.color = _theme.TextPrimary;
            b.style.fontSize = S(11);
            b.style.borderTopLeftRadius = S(3);
            b.style.borderTopRightRadius = S(3);
            b.style.borderBottomLeftRadius = S(3);
            b.style.borderBottomRightRadius = S(3);
            b.style.borderTopWidth = 0;
            b.style.borderRightWidth = 0;
            b.style.borderBottomWidth = 0;
            b.style.borderLeftWidth = 0;
            return b;
        }

        private VisualElement BuildLabeledCheckbox(string label, bool isChecked)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = S(2);

            var box = new VisualElement();
            box.style.width = S(12);
            box.style.height = S(12);
            box.style.marginRight = S(6);
            box.style.borderTopWidth = S(1);
            box.style.borderRightWidth = S(1);
            box.style.borderBottomWidth = S(1);
            box.style.borderLeftWidth = S(1);
            box.style.borderTopColor = _theme.Border;
            box.style.borderRightColor = _theme.Border;
            box.style.borderBottomColor = _theme.Border;
            box.style.borderLeftColor = _theme.Border;
            box.style.borderTopLeftRadius = S(2);
            box.style.borderTopRightRadius = S(2);
            box.style.borderBottomLeftRadius = S(2);
            box.style.borderBottomRightRadius = S(2);
            box.style.backgroundColor = isChecked ? _theme.Accent : _theme.Panel;

            var lbl = new Label(label);
            lbl.style.color = _theme.TextPrimary;
            lbl.style.fontSize = S(11);

            row.Add(box);
            row.Add(lbl);
            return row;
        }

        /// <summary>
        /// Phase 4: insert DPU checkbox under MU on loco cells.
        /// </summary>
        public void AddDpuCheckbox()
        {
            foreach (var slot in _cellExtraSlots)
            {
                if (slot.childCount > 0)  // means MU is there → loco cell
                {
                    slot.Add(BuildLabeledCheckbox("DPU", false));
                }
            }
        }

        private float S(float pixels) => pixels * _uiScale;

        private struct CarData
        {
            public string Name;
            public string Type;
            public string Destination;
            public string Contents;
            public int BP;
            public int BC;
            public int RS;
            public bool IsLoco;
        }
    }
}
