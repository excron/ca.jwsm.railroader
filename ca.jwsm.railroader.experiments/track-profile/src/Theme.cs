using System;
using System.IO;
using UnityEngine;

namespace Ca.Jwsm.Railroader.Experiments.TrackProfile
{
    /// <summary>
    /// Theme palette loaded from a Themes/<name>.json file at startup.
    /// Hex strings parsed via Unity's ColorUtility (supports #RRGGBB and #RRGGBBAA).
    ///
    /// Identical shape to the ui-toolkit-hud experiment's Theme — kept as a
    /// per-experiment copy so each experiment is self-contained. When findings
    /// migrate to ca.jwsm.railroader.ui, the shared theme primitive lives there.
    /// </summary>
    public sealed class Theme
    {
        public string Name        { get; private set; }
        public string Description { get; private set; }

        public Color Background;
        public Color Panel;
        public Color PanelStripe;
        public Color TitlebarAccent;
        public Color Border;

        public Color TextPrimary;
        public Color TextMuted;

        public Color Accent;
        public Color AccentHover;

        public Color Success;
        public Color Warning;
        public Color Danger;

        public static Theme LoadFromFile(string jsonPath)
        {
            var json = File.ReadAllText(jsonPath);
            var data = JsonUtility.FromJson<ThemeData>(json);
            return new Theme
            {
                Name           = data.name,
                Description    = data.description,
                Background     = ParseColor(data.background),
                Panel          = ParseColor(data.panel),
                PanelStripe    = ParseColor(data.panelStripe),
                TitlebarAccent = ParseColor(data.titlebarAccent),
                Border         = ParseColor(data.border),
                TextPrimary    = ParseColor(data.textPrimary),
                TextMuted      = ParseColor(data.textMuted),
                Accent         = ParseColor(data.accent),
                AccentHover    = ParseColor(data.accentHover),
                Success        = ParseColor(data.success),
                Warning        = ParseColor(data.warning),
                Danger         = ParseColor(data.danger),
            };
        }

        private static Color ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Color.magenta;
            return ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.magenta;
        }

#pragma warning disable CS0649
        [Serializable]
        private class ThemeData
        {
            public int    schemaVersion;
            public string name;
            public string description;

            public string background;
            public string panel;
            public string panelStripe;
            public string titlebarAccent;
            public string border;

            public string textPrimary;
            public string textMuted;

            public string accent;
            public string accentHover;

            public string success;
            public string warning;
            public string danger;
        }
#pragma warning restore CS0649
    }
}
