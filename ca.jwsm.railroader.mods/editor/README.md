# mods/editor

Map and track editing tools.

## Mod roles

- **Consumer** — `IMapModRegistry` (which map is loaded?), physics topology (where is the track?), bus events for state changes.
- **Service provider** — `IEditorSession` (e.g., "the user is currently editing — freeze sim", or "this segment is being modified").
- **UI contributor** — tool panels, in-world handles, side panels.

## Depends on

- `IMapModRegistry` (from `mods/mapmodloader`) — **hard dep, manifest-declared**. No map loader = nothing to edit.
- Physics topology contracts (from `physics`).
