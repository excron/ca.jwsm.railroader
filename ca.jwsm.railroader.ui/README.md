# ca.jwsm.railroader.ui

**Required-foundational mod.** Provides the windowing framework, theme, and assets. The "dirty work" of UI lives here so the api kernel doesn't have to host it.

## Owns

- **Window framework** — `IWindowService`, surfaces, chrome, lifecycle, position/state persistence.
- **Theme system** — USS, color tokens, typography, spacing scale (`IThemeService`).
- **Assets** — fonts, icons, sprites, stylesheets — all visual resources (`IAssetService`).
- **Surface registry** — the contribution-point machinery mods bind into.
- **Patches that modify game UI** to host our surfaces (bottom bar injection, CarInspector additions, etc.).

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
