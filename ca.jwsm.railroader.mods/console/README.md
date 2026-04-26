# mods/console

Operational console commands like `/ops sweep <filter>`. Also a catch-all for console commands that don't fit naturally in any other mod.

## Mod roles

- **Consumer** — queries contracts to satisfy commands.
- **(future) UI contributor** — possible in-game console UX additions (history, autocomplete, paging).

## Depends on

- `ICommandRegistry` — owned by api. This mod **registers** commands; it does **not** own the registry.

## Note on ownership

Other mods register their own commands too (`/durability reset` from durability, `/air charge` from enginecontrol, etc.). `mods/console` is one consumer of the slash-command system among many — not its owner.
