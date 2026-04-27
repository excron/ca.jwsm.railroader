using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ca.Jwsm.Railroader.Experiments.UiToolkitHud
{
    /// <summary>
    /// Phase 3 (corrected): clone of vanilla's CarInspector, built from the
    /// runtime dump captured via F9 once we knew the GameObject is named
    /// "UI.CarInspector.CarInspector" under "/Canvas - Modals/".
    ///
    /// Vanilla measurements (per dump runtime-CarInspector-2026-04-26-175053):
    ///   Window:       424 x 366
    ///   Title bar:    22 tall, "WindowTitleBar" sprite, title text size 14
    ///   Content:      24px horizontal padding, 11px top, 35px bottom (sizeDelta -24,-46)
    ///   Panel Title:  50 tall — title (size 24 with rich-text prefix) + subtitle (size 16, dimmed)
    ///   Tab View:     270 tall — Buttons row (30 tall) + Content Holder (232 tall)
    ///     3 tabs:     "Car" / "Equipment" / "Operations" — Montserrat-Medium SDF size 18
    ///     Field rows: 30 tall, label (150 wide, size 12, muted) + value (242 wide, size 16)
    ///     Toggles:    20x20 (Cut Out, MU)
    ///     Bottom:     [Select | Follow | Spacer | Delete] action buttons
    ///
    /// Visual identity is charcoal palette intentionally — vanilla uses warm
    /// cream (LibreBaskerville titles, #D0CABE text). We translate to our
    /// theme tokens; structure stays faithful.
    ///
    /// Phase 4: AddDpuToggle() inserts a DPU toggle row right after MU.
    /// </summary>
    public sealed class CarInspectorClone
    {
        // Vanilla CarInspector is 424 x 366. Adding the DPU field row (30 tall,
        // matching the other field rows) bumps to 396 to keep the bottom action
        // buttons from being squeezed.
        private const float BasePanelHeight     = 366f;
        private const float HeightWithDpuToggle = 396f;

        private readonly Theme _theme;
        private readonly float _uiScale;
        private VisualElement _root;
        private VisualElement _fieldRowsContainer;
        private VisualElement _muRow;

        public CarInspectorClone(Theme theme, float uiScale = 1f)
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

        // ---- Top-level window (vanilla 424 x 366) ----

        private VisualElement NewWindow()
        {
            var w = new VisualElement();
            w.style.position = Position.Absolute;
            w.style.top = S(50);
            w.style.left = S(560);
            w.style.width = S(424);
            w.style.height = S(BasePanelHeight);
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
            w.style.paddingTop = S(5);
            w.style.paddingBottom = S(5);
            w.style.paddingLeft = S(5);
            w.style.paddingRight = S(5);
            return w;
        }

        // ---- Title bar (vanilla 22 tall, near-transparent dark + WindowTitleBar sprite) ----

        private VisualElement BuildTitleBar()
        {
            var bar = new VisualElement();
            bar.style.height = S(22);
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.paddingLeft = S(8);
            bar.style.paddingRight = S(4);
            bar.style.backgroundColor = _theme.TitlebarAccent;
            bar.style.borderTopLeftRadius = S(4);
            bar.style.borderTopRightRadius = S(4);
            bar.style.flexShrink = 0;
            bar.style.marginBottom = S(8);

            // Vanilla: "SCR 1000" Montserrat-Medium SDF size 14 #D5D0C1, centered (-60 sizeDelta to leave room for close button)
            var title = new Label("SCR 1000");
            title.style.color = _theme.TextPrimary;
            title.style.fontSize = S(13);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexGrow = 1;
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            bar.Add(title);

            // Vanilla close: 14x14, CloseButton sprite color #BFAD88
            var close = new Button();
            close.text = "x";
            close.style.width = S(16);
            close.style.height = S(16);
            close.style.marginLeft = 0;
            close.style.marginRight = 0;
            close.style.paddingLeft = 0;
            close.style.paddingRight = 0;
            close.style.paddingTop = 0;
            close.style.paddingBottom = 0;
            close.style.backgroundColor = _theme.PanelStripe;
            close.style.color = _theme.TextMuted;
            close.style.fontSize = S(11);
            close.style.borderTopWidth = 0;
            close.style.borderRightWidth = 0;
            close.style.borderBottomWidth = 0;
            close.style.borderLeftWidth = 0;
            close.style.borderTopLeftRadius = S(8);
            close.style.borderTopRightRadius = S(8);
            close.style.borderBottomLeftRadius = S(8);
            close.style.borderBottomRightRadius = S(8);
            bar.Add(close);

            return bar;
        }

        // ---- Content (vertical: PanelTitle | TabView) ----

        private VisualElement BuildContent()
        {
            var content = new VisualElement();
            content.style.flexDirection = FlexDirection.Column;
            content.style.flexGrow = 1;

            content.Add(BuildPanelTitle());
            content.Add(BuildTabView());

            return content;
        }

        // Vanilla Panel Title: 50 tall — title (rich-text "<b><size=80%>LD</size></b> SCR 1000", size 24)
        // + subtitle ("190T EMD SD7", size 16, color D0CABE8D)
        private VisualElement BuildPanelTitle()
        {
            var pt = new VisualElement();
            pt.style.height = S(50);
            pt.style.flexShrink = 0;
            pt.style.flexDirection = FlexDirection.Column;

            // UI Toolkit Label has limited rich-text. We approximate the
            // "<b>LD</b> SCR 1000" prefix by just inlining "LD SCR 1000"
            // with bold style — fidelity loss for the size=80% nuance.
            var title = new Label("LD SCR 1000");
            title.style.color = _theme.TextPrimary;
            title.style.fontSize = S(20);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.height = S(28);
            pt.Add(title);

            var subtitle = new Label("190T EMD SD7");
            subtitle.style.color = _theme.TextMuted;
            subtitle.style.fontSize = S(13);
            subtitle.style.height = S(18);
            pt.Add(subtitle);

            return pt;
        }

        // ---- Tab View: tab buttons + content holder ----

        private VisualElement BuildTabView()
        {
            var tv = new VisualElement();
            tv.style.flexDirection = FlexDirection.Column;
            tv.style.flexGrow = 1;
            tv.style.marginTop = S(8);

            // Tab buttons row (vanilla 30 tall, 3 tabs)
            var tabs = new VisualElement();
            tabs.style.height = S(30);
            tabs.style.flexDirection = FlexDirection.Row;
            tabs.style.flexShrink = 0;
            tabs.Add(NewTabButton("Car",        selected: false));
            tabs.Add(NewTabButton("Equipment",  selected: true));
            tabs.Add(NewTabButton("Operations", selected: false));
            tv.Add(tabs);

            // Content holder (vanilla 232 tall, vert layout of field rows + bottom HStack)
            var holder = new VisualElement();
            holder.style.flexGrow = 1;
            holder.style.flexDirection = FlexDirection.Column;
            holder.style.marginTop = S(8);

            // Field rows shown when Equipment tab is active (the dump captured this state)
            _fieldRowsContainer = new VisualElement();
            _fieldRowsContainer.style.flexDirection = FlexDirection.Column;
            _fieldRowsContainer.Add(BuildFieldRow("Condition",  ValueLabel("100%")));
            _fieldRowsContainer.Add(BuildFieldRow("Brake Line", ValueLabel("90")));
            _fieldRowsContainer.Add(BuildFieldRow("Cylinder",   ValueLabel("72")));
            _fieldRowsContainer.Add(BuildFieldRow("Hand Brake", BuildHandBrakeValue()));
            _fieldRowsContainer.Add(BuildFieldRow("Cut Out",    BuildToggleValue(false)));
            _muRow = BuildFieldRow("MU", BuildToggleValue(false));
            _fieldRowsContainer.Add(_muRow);
            holder.Add(_fieldRowsContainer);

            // Spacer
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            holder.Add(spacer);

            // Bottom action row (vanilla: Select | Follow | spacer | Delete)
            var bottom = new VisualElement();
            bottom.style.height = S(30);
            bottom.style.flexDirection = FlexDirection.Row;
            bottom.style.flexShrink = 0;
            bottom.Add(NewActionButton("Select"));
            var sp = new VisualElement(); sp.style.width = S(4); bottom.Add(sp);
            bottom.Add(NewActionButton("Follow"));
            var grow = new VisualElement(); grow.style.flexGrow = 1; bottom.Add(grow);
            bottom.Add(NewActionButton("Delete"));
            holder.Add(bottom);

            tv.Add(holder);
            return tv;
        }

        // Vanilla tab: TabViewToggleSelected sprite, label Montserrat-Medium SDF size 18 #D0CABE
        // selected fill #C0AE89 (gold)
        private VisualElement NewTabButton(string text, bool selected)
        {
            var b = new VisualElement();
            b.style.flexGrow = 1;
            b.style.flexDirection = FlexDirection.Row;
            b.style.justifyContent = Justify.Center;
            b.style.alignItems = Align.Center;
            b.style.backgroundColor = selected ? _theme.Accent : _theme.PanelStripe;
            b.style.marginRight = S(2);
            b.style.borderTopLeftRadius = S(4);
            b.style.borderTopRightRadius = S(4);

            var label = new Label(text);
            label.style.color = _theme.TextPrimary;
            label.style.fontSize = S(14);
            label.style.unityFontStyleAndWeight = selected ? FontStyle.Bold : FontStyle.Normal;
            b.Add(label);
            return b;
        }

        // Vanilla Field Row: 30 tall, label (150 wide, size 12 #8D8980, right-aligned)
        // + value (242 wide, size 16 #D0CABE, top-left)
        private VisualElement BuildFieldRow(string label, VisualElement value)
        {
            var row = new VisualElement();
            row.style.height = S(30);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.flexShrink = 0;

            var lbl = new Label(label);
            lbl.style.color = _theme.TextMuted;
            lbl.style.fontSize = S(12);
            lbl.style.unityTextAlign = TextAnchor.MiddleRight;
            lbl.style.width = S(140);
            lbl.style.marginRight = S(8);

            var valueWrap = new VisualElement();
            valueWrap.style.flexGrow = 1;
            valueWrap.style.flexDirection = FlexDirection.Row;
            valueWrap.style.alignItems = Align.Center;
            valueWrap.Add(value);

            row.Add(lbl);
            row.Add(valueWrap);
            return row;
        }

        private Label ValueLabel(string text)
        {
            var l = new Label(text);
            l.style.color = _theme.TextPrimary;
            l.style.fontSize = S(14);
            return l;
        }

        // Hand Brake row in vanilla: "Released" label + Apply button
        private VisualElement BuildHandBrakeValue()
        {
            var v = new VisualElement();
            v.style.flexDirection = FlexDirection.Row;
            v.style.alignItems = Align.Center;
            v.style.flexGrow = 1;
            v.Add(ValueLabel("Released"));
            var grow = new VisualElement(); grow.style.flexGrow = 1; v.Add(grow);
            v.Add(NewSmallButton("Apply"));
            return v;
        }

        // Vanilla Toggle: UISprite #434343 background, Check sprite white when checked
        private VisualElement BuildToggleValue(bool isChecked)
        {
            var t = new VisualElement();
            t.style.width = S(20);
            t.style.height = S(20);
            t.style.backgroundColor = isChecked ? _theme.Accent : _theme.PanelStripe;
            t.style.borderTopLeftRadius = S(3);
            t.style.borderTopRightRadius = S(3);
            t.style.borderBottomLeftRadius = S(3);
            t.style.borderBottomRightRadius = S(3);
            t.style.borderTopWidth = S(1);
            t.style.borderRightWidth = S(1);
            t.style.borderBottomWidth = S(1);
            t.style.borderLeftWidth = S(1);
            t.style.borderTopColor = _theme.Border;
            t.style.borderRightColor = _theme.Border;
            t.style.borderBottomColor = _theme.Border;
            t.style.borderLeftColor = _theme.Border;
            return t;
        }

        // Vanilla Builder Button Compact: ~58x24, "Apply" Montserrat-Medium SDF size 16 #BEB5A3
        private Button NewSmallButton(string text)
        {
            var b = new Button();
            b.text = text;
            b.style.height = S(22);
            b.style.marginLeft = 0;
            b.style.marginRight = 0;
            b.style.paddingLeft = S(10);
            b.style.paddingRight = S(10);
            b.style.paddingTop = 0;
            b.style.paddingBottom = 0;
            b.style.backgroundColor = _theme.PanelStripe;
            b.style.color = _theme.TextPrimary;
            b.style.fontSize = S(12);
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

        // Vanilla Builder Button: ~72x30, label Montserrat-Medium SDF size 18
        private Button NewActionButton(string text)
        {
            var b = new Button();
            b.text = text;
            b.style.height = S(28);
            b.style.minWidth = S(72);
            b.style.marginLeft = 0;
            b.style.marginRight = 0;
            b.style.paddingLeft = S(12);
            b.style.paddingRight = S(12);
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

        /// <summary>
        /// Phase 4: insert a DPU toggle row directly after the MU row.
        /// Mirrors the user-requested "DPU checkbox underneath MU" behavior
        /// for the canonical additive-value test.
        /// </summary>
        public void AddDpuToggle()
        {
            if (_fieldRowsContainer == null || _muRow == null) return;

            // Make space — taller window so the new row doesn't squeeze the
            // bottom action buttons (Select/Follow/Delete).
            _root.style.height = S(HeightWithDpuToggle);

            var dpu = BuildFieldRow("DPU", BuildToggleValue(false));
            var muIndex = _fieldRowsContainer.IndexOf(_muRow);
            _fieldRowsContainer.Insert(muIndex + 1, dpu);
        }

        private float S(float pixels) => pixels * _uiScale;
    }
}
