# ca.jwsm.railroader.mods

Container for feature/UI mods. Each subfolder is one mod that bootstraps into the api kernel.

## Hard rule: no patches

**Mods in `mods/*` cannot Harmony-patch the game.** Period. If a mod thinks it needs a patch, that's a signal:

- The primitive it needs doesn't exist yet → extend api/physics/ui to expose it.
- It's a new foundational concern → consider promoting to top-level.

There is no "just this once" exception. The patch surface stays bounded to api, physics, and ui. This forces the foundation to be honest about what it exposes.

## Canonical mod shape

A mod can simultaneously be three things, and most are at least two:

1. **Consumer** — subscribes to bus events, queries contracts, reads streams.
2. **Service provider** — implements a contract from api and registers it for others.
3. **UI contributor** — registers components into ui-owned surfaces.

ETA is the canonical example of all three: consumer of physics + waypoints, provider of `IEta`, contributor to the equipment window.

## Cross-mod relationships

- Mods **never reference each other's assemblies**. All cross-mod talk goes through api contracts, the bus, or streams.
- **Optional dependencies** — `registry.TryGet<IFoo>()`, gracefully degrade if missing.
- **Hard dependencies** — declare `requires: [IFoo]` in `info.json`. Composition root refuses to bootstrap if missing.

## Reference

See `..\ARCHITECTURE.md`.
