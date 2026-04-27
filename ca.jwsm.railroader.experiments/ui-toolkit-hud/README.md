# Experiment: UI Toolkit HUD

## STATUS: COMPLETE — 2026-04-26

All four phases passed their visual acceptance gates. Both clones (HUD and CarInspector) render at vanilla-faithful structure with charcoal-palette differentiation. Additive-value extensions (dynamic brake slider, DPU toggle, coupler-forces pill strip) work end-to-end without touching vanilla UI.

### Outcome per phase

| Phase | Result |
|---|---|
| 1. Clone vanilla HUD (LocoControlsUI) | ✅ Structure matches vanilla; charcoal palette intentional |
| 2. Insert dynamic brake slider above throttle | ✅ Panel grows 160→210, slider sits where intended |
| 3. Clone vanilla CarInspector | ✅ Side-by-side comparison shows tight structural match |
| 4. Add DPU toggle under MU | ✅ One-line insertion via `Insert(muIndex+1, dpu)`; window auto-grew 366→396 |
| Bonus: Coupler-forces pill strip | ✅ Reused `BuildPillStrip` helper; stacks below brake strip cleanly |

### Decisions reached

- **Use UI Toolkit (not uGUI)** for the bulk of production UI. Flexbox handles vanilla's patterns cleanly; programmatic construction in C# is pleasant when paired with a small theme-tokens helper.
- **Theme via JSON tokens + `IStyle` setters** (Path 3 from the early discussion). No USS at runtime — Unity's StyleSheet parser is editor-only, and we don't want a bundling pipeline just for theming. JSON loaded at startup, applied programmatically. Works.
- **Multiply at construction** for UI scale, not GPU `style.scale` transform. Deterministic, no transform-origin quirks. `S(v) => v * uiScale` helper threaded through every dimension literal.
- **Vanilla's `Messenger<CanvasScaleChanged>` is the right hook** for live UI-scale updates. Not polling. Confirmed via ILSPY, subscribed via `Messenger.Default.Register<CanvasScaleChanged>` (GalaSoft.MvvmLight bundled inside Assembly-CSharp).
- **The "panel grows when extension added" pattern** (constants `BasePanelHeight` / `HeightWithDynamicBrake`) is real and reusable. Production `ca.jwsm.railroader.ui` should formalize this as a helper for any extensible window.
- **Always-on UPPERCASE labels** (`.ToUpper()` at source) match vanilla's TMP `UPPERCASE` font-material preset since UI Toolkit lacks `text-transform`.

### Notable infrastructure built (worth migrating in spirit)

- **Runtime dumper** (`RuntimeDumper.cs`) — F8 dumps everything (active scenes + DDOL), F9 dumps by name fragment. The only reliable way to capture dynamically-instantiated UI hierarchies — runtime inspection is required because the relevant GameObjects only exist while the panel is open. Should land as a permanent dev tool in production (`mods/console` or similar).
- **Pill-strip primitive** (`BuildPillStrip(track, fill, fillPct, marginTop)`) — generic gauge widget; stack as many as needed (brake, coupler, future stress indicators). Trivial template for any production gauge cluster.
- **Reverser column with twin REV/FWD labels** — pattern for sliders that semantically need split labels.

### Honest gotchas the session surfaced (write down so we don't repeat)

- **Dynamically-instantiated UI is invisible to any static introspection** — the CarInspector lives in `DontDestroyOnLoad` and only exists once the panel has been opened in a live session. F9 fragment search of the running scene is the only reliable way to find it; runtime inspection is mandatory for this class of UI.
- **DDOL scene must be walked separately** — `SceneManager.GetSceneAt(i)` doesn't include it. Trick: create a temp GameObject, mark `DontDestroyOnLoad`, then `temp.scene.GetRootGameObjects()`.
- **Vanilla GameObject names can be the full type name** (e.g., `UI.CarInspector.CarInspector`). Search by name fragment, not exact match.
- **Legacy `UnityEngine.Input.GetKeyDown` is often a no-op** when the new InputSystem is the active handler. Use `Keyboard.current.fXKey.wasPressedThisFrame` instead. (Wasn't actually needed here in the end — the legacy API worked — but worth knowing.)
- **TMP rich-text in dumps was being truncated** (40-char cap in the editor-side dumper). The runtime dumper deliberately doesn't truncate — captures rich-text formatting fully so multi-line/styled content like `<mspace>15</mspace>\n<color>MPH</color>` is preserved.
- **Vanilla's `Preferences.GraphicsCanvasScale`** is just `PlayerPrefs.GetFloat("gfx.canvas.scale", 1f)`. Read directly; don't go hunting for `CanvasScaler.scaleFactor` on a specific canvas (only canvases with `CanvasSettingsApplicator` get the value applied, and `Canvas - HUD` isn't necessarily one of them).

### What does NOT migrate to production

- **The experiment code itself** — production code is rewritten cleanly in `ca.jwsm.railroader.ui` informed by the lessons here. Same posture as our "no copy-paste from v0" rule.
- **`InspectorClone.cs`** — built for the wrong inspector (ConsistInspectorPanel is not the vehicle inspector users actually open). Kept here as a "we built the wrong one" artifact. Lesson: confirm the target with a runtime dump *first*, not from a static-scene assumption.
- **The `ConstantPixelSize` PanelSettings + `unityFont = LegacyRuntime`** specifics — production will use proper TMP font loading and probably a different Panel Settings asset shipped via AssetBundle. We approximated to get visual fidelity in the experiment.

### Cleanup

This experiment **does not get deleted** — it's frozen as a high-value reference artifact. Anyone asking "is UI Toolkit viable for our needs?" reads this README and looks at the code. The mod stays buildable so future-us can re-run it for visual sanity-checking when designing production windows.

If/when `ca.jwsm.railroader.ui` ships windows that supersede this experiment's, the experiment can be deleted. Until then, leave it.

---



## The question

**Is UI Toolkit + programmatic layout the right tech for the bulk of our UI work** — specifically the HUD and Inspector that gave us the most pain in v0?

Approach: **Path 3** (programmatic styling with JSON theme tokens). Themes live in `Themes/<name>.json` as data; layout and styling get applied in C# via `IStyle` setters. No USS authoring, no Unity authoring project, no AssetBundles — the experiment compiles via `dotnet build` alone and deploys as a UMM mod.

The CSS-comfortable USS authoring path (Paths 1/2 from the discussion) is *interesting* but secondary; the more pressing question is whether UI Toolkit's flexbox-based layout is pleasant for our patterns. Once that's answered, USS-based theming is a separate decision.

## Phased plan with visual acceptance gates

Each phase is a concrete deliverable. Each gates on visual fidelity to vanilla; if a phase doesn't look acceptable, we stop and reconsider rather than push deeper.

### Phase 1 — Clone vanilla HUD ✦ (current focus)

Build a UI Toolkit replication of vanilla's `LocoControlsUI` (the 450x160 panel anchored bottom-left). Mirror values from the GameUI scene dump:

- Outer panel: 450x160, anchored bottom-left, dark warm bg, rounded
- Selected Info section: engine name (large), cars/tonnage, location, speed readout (right-aligned)
- Divider
- Buttons row: Inspect / Follow / ... / Mode dropdown
- Controls section: 2x2 grid of sliders (Train Brake / Throttle / Independent / Reverser)

Data is hardcoded — visual fidelity test, not real wiring.

**Acceptance gate:** does it look comparable to vanilla? Side-by-side with the real game HUD running. Style differences are expected (charcoal palette vs vanilla warm-cream) — what we're judging is *layout fidelity* and *production quality*.

### Phase 2 — HUD addition: dynamic brake slider

Insert a dynamic-brake slider **above the throttle slider, between the info section and controls section**. Demonstrates the additive value pattern — extending vanilla's UI with new functionality cleanly, not by injecting into vanilla's prefab but by being our own surface that can include both vanilla's controls AND new ones in a coherent layout.

**Acceptance gate:** does the new slider integrate visually without looking bolted on? Is the layout still coherent?

### Phase 3 — Clone vanilla Inspector

Build a UI Toolkit replication of `ConsistInspectorPanel` (the horizontal-scrolling consist inspector). Per-car cell: CarType, CarName, Destination, Car Content, Brake Stats, Anglecock L/R sliders, Cut Lever L/R buttons.

This is the pain point that motivated the experiment. Its v0 perf was bad (per-tick rebuild of 8+ widgets per car × 200 cars). Our v1 architecture (in-place updates, bindings) addresses this; we'll use that pattern in the cloned inspector.

**Acceptance gate:** does it look at least as good as vanilla's basic Consist Inspector (low bar — vanilla's is unstyled), and is the per-cell construction pattern clean enough to scale?

### Phase 4 — Inspector addition: DPU checkbox

Add a **DPU checkbox underneath MU** in each cell. Demonstrates additive value for the inspector — one new control per car, integrated cleanly.

**Acceptance gate:** does the new checkbox sit naturally in the cell layout? Does adding it require restructuring the cell or does the layout absorb it?

## Phase toggles

`src/ExperimentEntry.cs` has compile-time `const bool` toggles for each phase. Flip them as we gate through:

```csharp
private const bool ShowHudClone           = true;   // Phase 1
private const bool AddDynamicBrakeSlider  = false;  // Phase 2
private const bool ShowInspectorClone     = false;  // Phase 3
private const bool AddDpuCheckbox         = false;  // Phase 4
```

Trivial, works for an experiment. (Production would be settings-driven; we're not production.)

## What's being tested (across phases)

- Does UI Toolkit's flexbox layout handle the kinds of layouts vanilla uses (grid-of-sliders, button row, scrolling cell list)?
- Is programmatic construction pleasant in C#, or does it feel verbose vs. prefab + binding?
- Does the JSON theme + `IStyle` setter pattern cleanly separate visual identity from layout code?
- For the inspector: do bindings + in-place updates actually cure the per-tick rebuild perf pain?
- Are the v0 picking-mode quirks (MouseDownEvent not bubbling through Label) easy to work around with `pickingMode = PickingMode.Ignore` or `ClickEvent`?

## What's NOT being tested

- Performance under load (defer)
- Accessibility (defer)
- Save/load integration (irrelevant)
- MP behavior (never ships)
- Real game-state wiring (data is hardcoded)
- Visual fidelity to vanilla's *exact* aesthetic (charcoal palette intentionally differs — what matters is whether the structure is coherent)

## UI scaling

The game offers a UI scale setting (e.g., 120% on a 4K display). For UI Toolkit:
- `PanelSettings.scaleMode = Scale With Screen Size`
- `referenceResolution = 1920x1080` (matches vanilla's `LocoControlsUI` Canvas Scaler config)
- `match = 0.5`

This handles resolution scaling. Reading the game's actual UI scale setting and combining with display DPI is deferred to production wiring against api primitives.

## Layout

```
ui-toolkit-hud/
├── README.md                                                ← this doc
├── ca.jwsm.railroader.experiments.ui-toolkit-hud.csproj
├── info.json                                                ← UMM manifest
├── Themes/
│   └── charcoal.json                                        ← palette as JSON tokens
├── Assets/                                                  ← reserved for future assets
└── src/
    ├── Theme.cs                                             ← JSON theme loader
    ├── HudClone.cs                                          ← Phase 1+2 panel builder
    └── ExperimentEntry.cs                                   ← UMM bootstrap, phase toggles
```

## Decision criteria

After running through the phases:

**UI Toolkit + programmatic + JSON theme is the right path if:**
- HUD clone (Phase 1) reaches acceptable visual fidelity with reasonable code volume
- Adding a new control (Phase 2) is a small, clean diff
- Inspector clone (Phase 3) is comparable to vanilla and the per-cell pattern scales
- Adding to a cell (Phase 4) doesn't require restructuring

**Reconsider if:**
- Code volume for the layout balloons past "comfortable" (e.g., 1000+ lines for the HUD clone alone)
- Adding controls requires layout rework rather than additive insertion
- Per-cell perf in the inspector is bad even with our update model
- UI Toolkit's runtime APIs have show-stopping limitations we hit

**Switch to USS-based path (Paths 1/2) if:**
- Programmatic styling feels much more verbose than CSS would
- Theme swapping requires touching too much code
- We start wanting cascade/specificity behavior the programmatic approach can't provide cleanly

## Cleanup

Findings inform `ca.jwsm.railroader.ui` design. **The experiment's code does not promote to production.** Production code is rewritten cleanly, informed by what worked and what didn't.

When done with all phases, this folder either gets deleted or marked frozen with a status note here.
