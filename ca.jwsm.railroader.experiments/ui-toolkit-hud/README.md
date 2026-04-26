# Experiment: UI Toolkit HUD

## The question

**Is UI Toolkit + USS the right tech for the bulk of our UI work** (panels, windows, controls, themed components)?

Original consideration was uGUI (Canvas-based, prefab-heavy) because vanilla uses it and the WYSIWYG editor is mature. But:

- Author has CSS background → USS is comfortable territory
- v0 had documented UI Toolkit pain (the `MouseDownEvent` picking-mode quirk), but the workaround is well-known and one line per element
- Code-driven authoring + USS hot-reloading = much faster iteration than the prefab loop

This experiment validates whether UI Toolkit feels right for *us* by building a representative test panel approximating vanilla's HUD style with **fully external (no inline) USS theming**.

## What's being tested

- Can we author a control panel (slider, button, label, text) entirely in C# code with **all styling externalized to USS files**?
- Does the charcoal theme actually render well in-game?
- Does the USS variable cascade work cleanly for theme swapping (charcoal → some-future-theme)?
- How's the developer iteration loop — text edit, save, see result?
- Are the v0 picking-mode quirks really easy to work around?

## What's NOT being tested

- Performance under load (defer until we have a real use case)
- Accessibility (defer until production)
- Save/load integration (irrelevant — this is dev-only)
- MP behavior (experiment never ships to a server)

## UI scaling (real concern, partial coverage in this experiment)

The game offers a UI scale setting (e.g., 120% on a 4K display). Our UI must respect it — both the game's setting and the user's display DPI.

For UI Toolkit specifically:
- `PanelSettings.scaleMode` controls how the panel scales relative to screen
- Default for our experiment: `Scale With Screen Size`, reference resolution `1920x1080`, match `0.5` (matches vanilla's `LocoControlsUI` Canvas Scaler config from the dump)
- This handles resolution scaling but **doesn't yet hook the game's UI scale setting**

What the experiment **does** test:
- Does the panel render at a sensible size on the test display?
- Does it scale acceptably when the game's resolution changes?

What the experiment **defers** (production concern):
- Reading the game's current UI scale value (likely a KVO/setting we can subscribe to — needs research when wiring against api)
- Reacting to UI-scale-changed events
- Combining game UI scale × display DPI × USS sizing into a coherent result

For now we aim for "looks right at 1920x1080 native" and note any obvious scaling issues. The actual game-UI-scale integration lands when this approach moves into the production `ca.jwsm.railroader.ui` mod with proper api subscriptions.

## Approach

- UMM bootstrap loads the experiment on game start
- Experiment creates a `UIDocument`-style overlay panel positioned roughly where vanilla's `LocoControlsUI` sits (bottom-left)
- All visual styling comes from `Themes/charcoal.uss` + `Themes/theme-base.uss`
- Demo panel includes: title, label, button, slider — enough to exercise the most common widget classes
- Vanilla's HUD is **not suppressed** for this experiment — side-by-side visual comparison is the point. Suppression is a separate concern that lands when this approach proves out.

## Status & honest constraints

> ⚠️ **USS at runtime requires Unity-bundled assets.** UI Toolkit's `StyleSheet` class can't be parsed from raw text at runtime; it must be a Unity-imported asset shipped in an `AssetBundle`.

Implication: this experiment also needs a small **Unity authoring project** (separate from this mod) that imports the USS files in `Themes/` and builds an `AssetBundle`. The mod loads the bundle at runtime and applies the `StyleSheet` to its root element.

The Unity authoring project doesn't exist yet (waiting on the install to finish). Until it does, this experiment is **scaffold-only** — the C# entry point loads, logs that it's alive, but the actual UI render path is stubbed out.

When Unity is ready, the build pipeline becomes:

1. Edit `Themes/*.uss` files in any text editor (VS Code etc.)
2. Sync them into the Unity authoring project's `Assets/Themes/` folder (symlink or watch script if manual sync gets annoying)
3. Unity auto-imports as `StyleSheet` assets
4. Build the AssetBundle (menu item we'll add)
5. Output `.bundle` file lands in this experiment's `Assets/bundles/`
6. Mod loads it at startup, gets `StyleSheet` references, applies them

## Layout

```
ui-toolkit-hud/
├── README.md                                                ← this doc
├── ca.jwsm.railroader.experiments.ui-toolkit-hud.csproj
├── info.json                                                ← UMM manifest
├── Themes/
│   ├── charcoal.uss                                         ← default theme (cool blue on charcoal)
│   └── theme-base.uss                                       ← structural rules using theme vars
├── Assets/
│   └── bundles/                                             ← built AssetBundles (output of authoring project)
└── src/
    └── ExperimentEntry.cs                                   ← UMM bootstrap; stub UI render path
```

## Decision criteria

After running the experiment:

**UI Toolkit is the right choice if:**
- Authoring loop is genuinely faster than the prefab approach
- USS theming feels comfortable (it should — it's CSS)
- v0's quirks (picking mode, etc.) are easy to work around
- Visual result looks acceptable next to vanilla's UI

**uGUI is still the right choice if:**
- USS rendering has show-stopping limitations we hit immediately
- The bundling overhead negates the iteration speedup
- The styled output feels visually wrong or hard to control
- Cross-mod surface registration patterns are awkward

**Hybrid is the right choice if:**
- UI Toolkit works for floating windows but feels wrong for HUD overlays (or vice versa)
- Different parts of the production UI naturally favor different tech

## Cleanup

This experiment, regardless of outcome, **does not promote to production as code**. The findings inform `ca.jwsm.railroader.ui`'s implementation; the implementation is written cleanly there.

When done, this folder either gets deleted (rare — usually keep as a "we tried this" artifact) or marked frozen with a status note in this README.
