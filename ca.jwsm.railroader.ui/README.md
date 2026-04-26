# ca.jwsm.railroader.ui

**Required-foundational mod.** Provides the windowing framework, theme, and assets. The "dirty work" of UI lives here so the api kernel doesn't have to host it.

## Owns

- **Window framework** — `IWindowService`, surfaces, chrome, lifecycle, position/state persistence.
- **Theme system** — USS, color tokens, typography, spacing scale (`IThemeService`).
- **Assets** — fonts, icons, sprites, stylesheets — all visual resources (`IAssetService`).
- **Surface registry** — the contribution-point machinery mods bind into.
- **Our own UI surfaces** — our-own-bottom-bar, our-own-toolbars, our own windows. Self-contained, parallel to the game's UI.

## No vanilla UI modification — but replacement is allowed

We do **not** modify vanilla UI prefabs. We never edit them, never inject our components into their hierarchy, never adjust their layout. v0 tried that approach and it was fragile — layout changes, prefab updates, and Unity edge cases all broke the injections.

But there's an important distinction:

- **Modification** (editing vanilla's prefab, mucking with its layout, injecting widgets into it) → forbidden, always.
- **Replacement** (hiding vanilla's element wholesale + shipping our equivalent in the same visual space) → allowed and sometimes the only clean path.

### When we replace

Some surfaces — the HUD especially — can't sustain a parallel duplicate. We can't show a coupler-stress bar *next to* vanilla's HUD; we can't add a dynamic-brake slider *alongside* vanilla's brake controls without visual clutter. For those, the only clean answer is to suppress vanilla's element wholesale and run our equivalent (with vanilla's behavior + our additions) in the same slot.

For modal windows (Inspector, Equipment, Roster), we usually build alongside first and offer a toggle to replace once feature parity lands.

### How we replace (without violating the rule)

A small surface of behavior-changing Harmony patches in `ui` deactivates the vanilla GameObjects / Canvases that host the elements we're replacing. We do **not** edit vanilla's prefabs — they stay exactly as the game shipped them. We just don't render them. Our equivalent shows up in the same visual space.

This is allowed because:
- We treat vanilla's widget as a black box that we hide from outside; never edit.
- Behavior patches are explicitly allowed in L2 mods (per ARCHITECTURE.md patch policy).
- Vanilla's UI definition is untouched; could be restored just by re-enabling the GameObject.

So `ui` does have a small Harmony surface, scoped narrowly to "suppress vanilla element X." Not a free-for-all.

## Mods contribute, never render directly

Mods describe content declaratively and register components against named surfaces (Equipment window, bottom bar, HUD). Mods never touch Unity UI Toolkit. ui owns the rendering.

This kills two v0 potholes structurally:

- **Theme drift** — mods can't theme; they can only request tokens.
- **Asset duplication** — mods request assets by key; ui owns the file.

## Why top-level

Required-foundational: most mods will surface UI components. Heavy enough (windowing impl + theme + assets) that it doesn't belong inside the api kernel.

## v0 caveat

v0 had an `abstractions / core / runtime` three-tier split that became a cluster. **v1 is a single project** — one assembly, registers contracts, owns implementation. No tiers.

## Reference

See `..\ARCHITECTURE.md`.
