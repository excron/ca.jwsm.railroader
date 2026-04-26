using UnityEngine;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.UiToolkitHud
{
    /// <summary>
    /// UMM entry point for the UI Toolkit HUD experiment.
    ///
    /// Currently scaffold-only — proves the mod loads, logs that it's alive.
    /// The actual UI render path (load AssetBundle with imported StyleSheets,
    /// build VisualElement tree, apply theme classes, attach to active panel)
    /// lands once the Unity authoring project exists to bundle the USS files
    /// in Themes/.
    ///
    /// See README.md for the full experiment plan and runtime constraints.
    /// </summary>
    public static class ExperimentEntry
    {
        public static UnityModManager.ModEntry Mod { get; private set; }

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;
            modEntry.Logger.Log("UI Toolkit HUD experiment: loaded.");
            modEntry.Logger.Log("UI Toolkit HUD experiment: render path is stubbed; awaiting Unity authoring project + AssetBundle pipeline for USS.");
            return true;
        }
    }
}
