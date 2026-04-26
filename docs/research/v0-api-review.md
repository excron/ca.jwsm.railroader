# v0 api Review (against v1 discipline)

**Date:** 2026-04-26
**Source:** `_reference/ca.jwsm.railroader.api/` (v0 api host, archived as `excron/ca.jwsm.railroader.api-v0`)
**Purpose:** Critical assessment of v0's api against v1's architectural rules. Identify what was on the right track (preserve in spirit) and what drifted (deliberately do differently). **Not a copy-paste source.**

---

## Headlines

- **Service registry, event bus, persistence shapes are basically right.** v1 keeps these in spirit; v0 just had them in the wrong host.
- **The kernel was bloated** — 21 services in v0's composition root, only 6-7 actually kernel-level. The rest belong in `physics` or feature mods. The bloat happened because v0 had no foundational-mod layer to absorb domain services.
- **Two structural anti-patterns to retire:** static log coalescer (called from 200+ sites) and static patch-state holders.
- **AutoEngineerSmoothingPatch is in v0 with a `"PARKED — KNOWN BROKEN — DO NOT BUILD ON TOP OF THIS YET"` comment.** The exact incident our v1 closed-loop control rule exists to prevent. v0 *knew* it was broken; the architecture didn't give it anywhere safer to live.
- **The patches-as-event-publishers refinement** is the most useful new insight from this pass — it eliminates the static patch-state holder pattern entirely.

---

## What survives v0 → v1 (in shape, not code)

### Service registry pattern
- File: `core/Api/ServiceRegistry.cs`
- Shape: `Register<T>` / `TryGet<T>` / `GetRequired<T>`, type-keyed, explicit composition-root wiring
- Why it works: no magic, no reflection-based DI, transparent object graph
- v1 keeps the same shape. Maybe extend with declarative authority-class hints at registration.

### Event bus
- File: `core/Events/EventBus.cs`
- Shape: generic type-based pub/sub, disposable subscription handles, thread-safe snapshot iteration
- Why it works: clean, predictable, no implicit topic routing
- v1 keeps unchanged. Streams are an *addition* alongside, not a replacement.

### Persistence two-tier
- File: `persistence/Contracts/IModDataStore.cs`
- Shape: `TryLoadJson` / `SaveJson` for convenience + generic `TryLoad<T>` / `Save<T>` for power users
- Already matches v1's persistence contract.

### `RepeatedLogCoalescer` algorithm
- File: `host/Diagnostics/RepeatedLogCoalescer.cs`
- The dedup-within-window logic + summary-on-flush is the right behavior
- v1 keeps the algorithm but **as a pipeline stage inside `ILoggerFactory`**, not as a static.

### Frame-scoped caching pattern
- File: `host/Services/ConsistTopologyService.cs:59-66`
- Caches consist walk per `Time.frameCount`
- v1 makes this the **default contract** for any service touching game state, not per-service rediscovery.

### Composition root pattern
- File: `host/Bootstrap/HostCompositionRoot.cs`
- Explicit wiring at one place is right. Just needs less wired into it.

---

## Drift inventory

### Rule 1 — kernel stays thin
**Massive drift.** 21 services in v0's composition root; 6-7 belong in the kernel. The rest landed in api because v0 had no foundational-mod layer to absorb them.

| Service | v0 location | v1 destination |
|---|---|---|
| `EngineControlService` (2373 lines!) | api/host | `mods/enginecontrol` |
| `ConsistTopologyService` | api/host | `physics` |
| `ConsistDirectionService` | api/host | `physics` |
| `ConsistLookaheadService` | api/host | `physics` |
| `WaypointNavigationService` | api/host | `mods/eta` (or `physics` if pure derivation) |
| `TrainIntegrationService` | api/host | `physics` |
| `WorldLayoutService`, `WorldAssetStoreService`, `TerrainService` | api/host | TBD — likely a `world` foundational concern or feature mod |

Stays in v1 api: registry, event bus, logger factory, authority, command registry, persistence service, request router. That's it.

### Rule 2 — bus + streams + registry
**No streams.** Continuous state (consist topology, direction) is read via synchronous service calls every tick. v1 needs stream contracts as a first-class shape — that's genuinely new, not a port.

### Rule 3 — no statics for shared services
**Critical violation.** Two shapes:

- `RepeatedLogCoalescer.Log("key", "message")` — static method, static dictionary, called from 200+ sites in `EngineControlService` alone.
- Patch state holders: `HostCompositionRoot` line 111 stores `SaveLifecycle.Service = saveContext;`, patches read `var svc = AutoEngineerSmoothingState.Service;`. The static escape hatch around Harmony's static-method requirement.

v1 fix for the first: inject `ILoggerFactory` everywhere, no global access path.
v1 fix for the second: **patches as event publishers** (see below).

### Rule 4 — no scattered IsHost checks
**Foundation exists, declarative shape missing.** `IMultiplayerService.IsHost` is properly injected via `MultiplayerService` (uses reflection on `Network.Multiplayer.IsHost` with frame-cached read). But no `[ServiceAuthority]`-style attribute pattern; nothing that makes "this service is host-only" enforced at composition.

### Rule 5 — patches by intent (observer vs behavior)
**Critical violation.** `host/Patches/AutoEngineerSmoothingPatch.cs`:
- Patches `LocomotiveControlHelper.ChangeValue`
- Returns `false` from prefix to skip vanilla's control write, substitutes smoothed values (line 169)
- **Behavior modification, in api/host**
- Comment block (lines 1-37): `"PARKED — KNOWN BROKEN — DO NOT BUILD ON TOP OF THIS YET"` — postmortem cites hybrid-control seams, air-disconnect locks, coupler break cascades

This is the textbook violation. v1 puts it in `mods/enginecontrol` and only after physics streams exist to feed it. Until then, it doesn't get to ship.

Other patches assessed:
- `SaveLifecyclePatch`, `WorldLifecyclePatch`, `TrainIntegrationPatch` — observer (fire events / call hooks). OK in api.
- `EngineRosterRowPatch` — vanilla UI mod. Violation (Rule 6).

### Rule 6 — no vanilla UI modification
**Violation.** `EngineRosterRowPatch.cs`:
- Appends navigation info to vanilla `EngineRosterRow.infoLabel.text`
- Tweaks TextMeshPro overflow settings
- Even an "augmentation" violates the rule

v1: this becomes a contribution to OUR equipment-window-equivalent surface, owned by ui. The vanilla EngineRoster stays untouched.

### Rule 7 — frame-scoped caching as default
**Local only, not systemic.** `ConsistTopologyService` does it; `MultiplayerService.RefreshCache` does it; `EngineControlService` does it. Each rediscovers the need.

v1: a base contract or helper for "service that reads game state" makes this the default behavior.

### Rule 8 — MP dual-layer pattern
**Partial.** `IModPropertySyncService` exists with host-only checks but no framework enforcement, no typed request messages. Mods route commands through the event bus ad-hoc (`WebLocomotiveControlRequestedEvent` → `WebLocomotiveControlCompletedEvent` round-trip). Works, but no structure.

v1: explicit `IRequestRouter` typed-request system (see MP survey for shape).

### Rule 9 — closed-loop control discipline
**No framework support.** Hard to fault v0 here — the rule itself is a v1 invention informed by v0's failures.

---

## The most useful new insight: patches as event publishers

v0's patch escape hatch:

```csharp
internal static class SomeFeaturePatchState {
  internal static IServiceX Service;
}

[HarmonyPatch(typeof(GameType), "Method")]
internal static class SomeFeaturePatch {
  [HarmonyPostfix]
  private static void Postfix(GameType __instance) {
    var svc = SomeFeaturePatchState.Service;
    if (svc == null) return;
    svc.DoStuff(__instance);  // ← logic in patch land
  }
}

// Composition root:
SomeFeaturePatchState.Service = serviceX;
```

Problems:
- Static escape hatch (rule 3 violation by necessity)
- Patch contains logic that should live in a mod
- Not testable in isolation
- Composition root has to wire patch state, not just services

**v1 default pattern: patches publish events.**

```csharp
[HarmonyPatch(typeof(GameType), "Method")]
internal static class SomeFeaturePatch {
  [HarmonyPostfix]
  private static void Postfix(GameType __instance) {
    GamePatchBus.Publish(new SomeGameEvent(__instance.Id, __instance.SomeValue));
  }
}
```

The `GamePatchBus` (or whatever we name the patch-side publisher) is a thin static singleton that the bootstrap connects to the real `IEventBus` once it's wired. Mods subscribe through `IEventBus` like any other event. Patches own *no logic* — just shape an event payload from the patched call's arguments.

Benefits:
- Patches become trivial — one publish per hook
- All logic moves to subscribers (foundational mods or feature mods, never api)
- The static patch-state holder pattern goes away entirely
- Tests don't need to set up patch state; they publish events directly
- Adding subscribers doesn't require changing patches

When this pattern doesn't fit:
- A patch that needs to *prevent* the original (return `false` from prefix) can't be event-driven — by the time the event is processed, the original would've run. This is rare and should be rare; behavior-changing prefix patches are L2-only anyway.
- A patch that needs synchronous service-data access (e.g., querying current authority before deciding what to publish) — keep the static handoff for these, scoped narrowly.

---

## EngineControlService — the canonical "too big" service

**2373 lines** in one file. Responsibilities:

- Throttle control state + writes
- Dynamic brake control state + writes
- DPU group fan-out logic
- State persistence
- Diagnostics
- Interlock management
- AE smoothing coordination

**v1 split** (suggested for `mods/enginecontrol`):

| Layer | Concern |
|---|---|
| Model | State holder. What's the current DPU mode, dynamic brake setpoint, etc. |
| Logic | Decisions. Given physics streams + state, what should the control values be? |
| Writer | Applies decisions to vanilla via control properties. Knows about clamping, validation, KVO sync. |

Three thin classes with clear handoffs > one 2400-line monolith. Same shape generalizes to other control mods.

---

## Other surprises

### No `ILoggerFactory` in v0
Every logging caller goes through `RepeatedLogCoalescer.Log` directly. v1's `ILoggerFactory` with scoped loggers is genuinely new infrastructure.

### No `ICommandRegistry` in v0
Slash commands (if any) handled ad-hoc. v1 makes this a kernel primitive.

### `IDiagnosticsService` exists but is a stub
Just a sink registry + trace. No structured fields, no perf counters, no lazy string evaluation. v1 should make this richer or fold into the logger.

### Heavy reflection (`AccessTools`) for vanilla internals
Necessary because vanilla's internals aren't public, but tightly couples v0 to exact game versions. v1 should encapsulate reflection in adapter services so a game patch doesn't ripple across consumers.

### Save context is thread-safe but reads aren't always
`SaveContextService` uses locks; consumers (`EngineControlService` etc.) often read unsafely. v1 should bake thread-safety expectations into contracts.

### No service visibility control
Any registered service is fetchable by anyone. `services.GetRequired<IFoo>()` works regardless of context. v1's authority system (with `[ServiceAuthority]`) addresses this — but worth being explicit about it as a goal.

---

## Implications for v1 api kernel design

1. **Inventory of what api kernel actually owns** is small: registry, event bus, logger factory, authority, command registry, persistence service, request router, observer patches, all cross-mod contracts. Everything else is `physics`, `ui`, or `mods/*`.

2. **Patches as event publishers** is the default pattern. Static handoff is the narrow fallback for behavior-changing prefixes (and those only in L2 mods anyway).

3. **`ILoggerFactory` is the entry point for logging.** No statics. Coalescing is a pipeline stage, configured once at composition.

4. **Frame-scoped caching is a base contract**, not a per-service concern.

5. **`[ServiceAuthority(...)]` is declarative.** Composition root validates at wire time.

6. **`EngineControlService` doesn't return.** Replaced by Model + Logic + Writer split inside `mods/enginecontrol`.

7. **AutoEngineerSmoothingPatch doesn't return** until physics streams exist (phase 4) and `mods/enginecontrol` exists (phase 9). The "PARKED — KNOWN BROKEN" comment is the receipt that proves the closed-loop rule is real.

8. **`EngineRosterRowPatch` doesn't return.** UI augmentations live in our own surfaces, not on vanilla's prefabs.
