# Railroader Mod Stack — Architecture (v1)

## Preamble

This document captures the architectural decisions for the v1 rebuild of the
Railroader mod stack. v0 lives in `../_reference/` (also archived on GitHub
under `<name>-v0`). v1 is a clean-slate rebuild as a single monorepo, starting
from a documented contract.

**Read this before writing any code.** Every decision here exists because v0
got it wrong somewhere; the lessons-learned section maps each rule back to a
concrete pothole. Future-you will hit the same potholes if these rules erode.

This is a **living document** until phase 0 commits — push back, edit, argue.
Once phase 0 lands, changes go through PR review like anything else.

---

## Lessons from v0

Concrete failures from the v0 codebase that drive v1 rules. Each item should
be readable as "this is why rule X exists."

### Layering / ownership

- **`EngineControlService` ended up doing too much.** It owned topology
  consumption, control writes, dynamic-brake state, persistence, throttle
  interception, *and* per-tick simulation logic for DPU power propagation. A
  consumer mod (DPU) had nowhere to live, so it leaked into api/host.
  → v1 separates **api primitives** (read/write surface) from **feature mods**
  (decisions). DPU power propagation is a mod, not a service.

- **AE smoothing experiment landed in api/host.** Same shape: it had no
  natural home in a "feature mod" because we hadn't drawn the layer line.
  → v1 makes "feature mods consume api contracts" the only legal pattern for
  game-behavior changes.

- **Per-tick simulation patches lived alongside data-exposing patches** in
  `api/host/Patches/`. The DPU air patch (changes physics) and a hypothetical
  KVO-mirror patch (exposes state) were treated as the same kind of thing.
  → v1 splits **observer patches** (api) from **behavior patches** (foundational
  mods only — physics owns its own). `mods/*` cannot patch at all.

### Closed-loop control

- **AE smoothing exploded a 200-car consist.** v0's attempt to smooth AE
  throttle/brake actions was written before any physics feedback channel
  existed. Smoothing the inputs decoupled controls from in-train slack
  response; rhythmic slack action snapped every coupler in the consist.
  → v1 has the **closed-loop control rule**: any mod that writes control
  inputs (throttle, brake) must consume the physics streams those inputs
  affect. Control-modifying mods declare physics as a hard dep; composition
  root refuses to bootstrap them without a physics provider registered.

### Physics ground truth

- **Coupler-force math floated free of the game's physics.** v0 used free-body
  math on top of vanilla's slack-only model, but the derived forces had no
  causal relationship to anything the game actually computed. Drift between
  the two models made the result fragile.
  → v1 has a **single physics source of truth** in the `physics` mod: reads
  vanilla state, derives richer values, exposes them as streams. Additive,
  never replacing — vanilla's tick keeps running. Anywhere our truth disagrees
  with vanilla, vanilla wins for what it controls; our truth informs what
  *we* control.

### Vanilla UI modification

- **Modifying game UI prefabs was fragile.** v0 reached into game windows
  (CarInspector, bottom bar, etc.) to inject our controls. Layout changes,
  prefab updates between game versions, and Unity UI Toolkit edge cases all
  broke our injections. The "elegant native feel" wasn't worth the fragility.
  → v1 forbids vanilla UI modification entirely. Our UI lives in **our own
  surfaces** — windows, our-own-bottom-bar, our-own-toolbars — built on the
  ui mod's framework. Self-contained ecosystem, less elegant initially,
  vastly more robust.

### Directionality

- **`FrontIsA` was re-derived in 4-5 sites.** DPU reverser correction,
  speedometer gauge, AE plans, equipment displays — each guessed at "which
  way is forward" independently. Bugs surfaced when one site flipped a sign
  while others didn't.
  → v1 has `IConsistDirection` as the **only** legal source of truth for
  direction, orientation, and signed speeds. Consumers never compute it.

- **Walk-order-based lead detection was wrong.** `EnumerateCoupled()` returns
  the consist starting from one end of the selected loco's orientation, which
  doesn't always match the train's "head". v0 assumed group-1 = head;
  reality is whichever group contains the IsLeadCandidate loco.
  → v1 codifies: lead designation follows **flag state**, not walk order.

### Performance / hot paths

- **Per-DPU full consist walk per physics tick.** `FindLeadInConsist` walked
  EnumerateCoupled() for every DPU on every UpdateAir call. ~48k iterations/sec
  on a 200-car consist with 4 DPUs. Caching was added retroactively.
  → v1 builds **frame-scoped caching** into the primitives layer from day one.
  Any service that reads game state caches per frame by default.

- **`ConsistTopologyService.GetGroups` called 3-10× per frame for the same
  data** (Tick + GetSelected + interceptor + every Set* method). Each call
  walked the consist fresh.
  → Same fix: frame-scoped caching, but as a **default contract**, not a
  per-service afterthought.

- **Per-tick StringBuilder allocations for diagnostics.** Coalescer deduped
  the *output* but the *work* still happened every frame.
  → v1 makes the diagnostic emission itself **fingerprint-gated**: only build
  the message when structural state actually changed.

- **EquipmentPart.RebuildList** cleared the entire ScrollView and rebuilt 7
  columns per row every periodic tick.
  → v1 mandates **in-place updates** for periodic-tick UI by default. Tear-
  down only on shape change.

### Logging

- **Static `RepeatedLogCoalescer` couldn't be scoped, replaced, or tested.**
  Every service called the same global. Logs all landed in Player.log mixed
  with game noise.
  → v1 has `ILoggerFactory` + scoped loggers + dedicated rotating log file
  under api's persistent store. Single timeline, scope-tagged, filterable.

- **Coalescer keys were ad-hoc and inconsistent.** `engine-control-tick-bail`,
  `consist-topology-lead-fallback`, `dpu-air-postfix` — different shapes,
  different conventions.
  → v1 enforces hierarchical dotted scope names: `api.consist-topology`,
  `mods.dpu.air`.

### UI

- **`MouseDownEvent` on UI Toolkit rows didn't bubble through child Labels.**
  Default Label `pickingMode` ate the click before it reached the row handler;
  symptom was DPU rows with content-filled Mode cells silently failing
  Select+Follow.
  → v1 docs: use `ClickEvent` (synthesised at press+release) for row-level
  click handlers; `MouseDownEvent` only when you specifically want the down
  edge.

- **No USS stylesheet — every visual property set inline.** Buttons rendered
  invisible because they had no `backgroundColor` until we explicitly set it.
  → v1 ships a small `theme.uss` from day one in the ui mod. Inline styles
  are the exception, not the default. Mods can't theme — they request
  tokens from `IThemeService`.

### Multiplayer

- **`StateManager.IsHost` checks scattered everywhere.** Some services
  silently no-op'd on clients with no obvious indicator; others got it wrong
  and tried to write authoritative state from a client.
  → v1 has `IAuthority` injected into every service, with declarative
  authority-class metadata enforced at startup.

---

## Workspace Layout

```
C:\Users\jsm12\OneDrive\Documents\Game_Projects\
├── Railroader-ILSPY\                        ← decompiled game source (existing)
├── _reference\                              ← legacy v0 clones, READ-ONLY
│   ├── ca.jwsm.railroader.api\
│   ├── ca.jwsm.railroader.ui\
│   ├── ca.jwsm.railroader.mods\
│   ├── ca.jwsm.railroader.mods.derailchasm\
│   ├── ca.jwsm.railroader.mods.physics\
│   └── ca.jwsm.railroader.web\
│
└── Railroader\                              ← v1 monorepo
    ├── ARCHITECTURE.md                      ← this doc
    ├── ca.jwsm.railroader.api\              ← kernel (controller)
    ├── ca.jwsm.railroader.physics\          ← required-foundational mod
    ├── ca.jwsm.railroader.ui\               ← required-foundational mod
    ├── ca.jwsm.railroader.web\              ← browser client (different runtime)
    └── ca.jwsm.railroader.mods\             ← feature + UI-contributor mods
        ├── console\
        ├── dispatch\
        ├── durability\
        ├── editor\
        ├── enginecontrol\
        ├── eta\
        ├── mapmodloader\
        └── webview\
```

**Rules for `_reference/`:**

- Cloned from GitHub fresh; never modified locally.
- Anything migrated from `_reference/` to v1 must be **rewritten**, not
  copy-pasted. Reading legacy code as "what the answer looked like before"
  is fine; pasting it forward is how layer violations sneak back in.
- `_reference/` self-deletes when it stops being useful as a reading aid.
  The GitHub `<name>-v0` archives are the long-term preservation.

**Rules for ILSPY:**

- Read-only reference for game internals.
- Use `Grep`/`Read` to mine; don't try to compile against it.
- Each phase's mining produces notes in this doc or in phase-specific design
  notes — don't carry implementation knowledge in your head between sessions.

---

## Layer Model

```
L0  Game                                     (Assembly-CSharp, read-only)
       ▲
       │ Harmony patches (only from L1 + L2; L3 cannot patch)
       │
L1  api kernel                               ca.jwsm.railroader.api
       Composition root, mod lifecycle,
       ILoggerFactory, IAuthority, bus,
       streams, registry, command registry,
       persistence, all cross-mod contracts,
       observer patches.
       ▲
       │
L2  Required-foundational mods               ca.jwsm.railroader.physics
       Implementations of L1 contracts       ca.jwsm.railroader.ui
       that the system can't function
       without. Each may patch as needed
       to do its job. Composition root
       refuses to bootstrap dependents
       without them registered.
       ▲
       │
L3  Feature mods                             ca.jwsm.railroader.mods\*
       Consumers, providers, UI
       contributors. Cannot patch.
```

### Layer rules

- **Dependencies flow downward only.** L3 depends on L1-L2; L2 depends on L1
  only; L1 depends on L0 only.
- **No cross-mod L3→L3 dependencies on implementations.** Two L3 mods
  communicate through L1 contracts (via the registry) or through the bus,
  never by referencing each other's assemblies. If they need shared state,
  that state belongs in L1 (contract) and a foundational/feature mod that
  provides it.
- **L1-L2 know nothing about L3 features.** Adding a new feature mod doesn't
  require api or foundational-mod changes. If it does, the contract surface
  is wrong.
- **Patches that change game behavior live at L2.** Observer patches that
  expose game state for primitives live at L1. **L3 cannot patch at all** —
  if a feature mod thinks it needs to patch, the foundation needs to expose
  the missing primitive (or the mod is misclassified and should be promoted
  to L2).
- **No vanilla UI modification, ever.** Our UI lives in our own surfaces
  (windows, our-own-bottom-bar, our-own-toolbars), built on the ui mod's
  framework. Patching game UI prefabs is forbidden at every layer.
- **Orchestration lives at L1.** Timing, ordering, lifecycle, coordination,
  threading, and caching are kernel primitives. Mods never roll their own.
  When a timing or coordination problem surfaces, the fix lands in the
  api, not in mod-side workarounds. (See *Orchestration discipline* under
  Feature mods.)

### Smell tests for layer placement

Use these when deciding where new code goes:

1. **"Would multiple unrelated mods break if I deleted this?"** → L1 (contract)
   or L2 (implementation).
2. **"Is this *deciding* something about the train?"** (DPU power, AE
   smoothing, ETA) → L3 feature mod.
3. **"Does this just *expose* game state in a typed way?"** → L1 contract,
   L1 observer patch, possibly L2 implementation.
4. **"Does this change what the game does at the physics level?"** → L2
   (physics mod) or L3 feature mod consuming physics streams.
5. **"Does this render UI?"** → ui mod owns the framework; the mod owning
   the content registers components into ui-owned surfaces. Never patches
   game UI.

---

## api kernel

The api project is the controller. All our mods bootstrap into it on top
of UMM/Harmony. Stays thin and coherent — never welds heavyweight
implementations onto itself.

### Components

- **Composition root + mod lifecycle.** Discovers our mods, validates
  manifests, wires services, registers patches.
- **Logging** — `ILoggerFactory` with scoped loggers, single rotating log
  file, coalescing pipeline. See *Logging* below.
- **Authority** — `IAuthority` injected into every service for host/client
  awareness. See *Multiplayer* below.
- **Communication** — `IEventBus`, stream contracts, `IServiceRegistry`.
  See *Communication shapes* below.
- **Command registry** — `ICommandRegistry`. Slash-command registration and
  dispatch. Owned by api (contract + impl) since it's small and broadly
  used.
- **Persistence** — `IPersistenceService`, two-tier API with atomic writes.
  See *Persistence* below.
- **All cross-mod contracts** — every interface and value type any mod
  exposes to or consumes from another lives in api. Physics contracts
  (`ICouplerForces` …), UI contracts (`IWindowService` …), feature contracts
  (`IEta`, `IDurability`, `IEngineControl`, …). Implementations live in
  the mod that owns them.
- **Observer patches** — Harmony patches that *expose* game state for
  primitives. Patches that *change* game behavior live in foundational mods.

### What api does NOT own

- **Implementations of physics or UI contracts** — those live in `physics`
  and `ui`.
- **Behavior-modifying patches** — those live in the foundational mod that
  owns the behavior. `mods/*` cannot patch at all.
- **Heavyweight subsystems.** If something is big enough to feel like its
  own thing (physics math, UI framework), promote it to a foundational mod
  rather than welding it into the kernel.

---

## Communication shapes

Two well-authored shapes plus a directory:

### Bus (events)

Discrete pub/sub via `IEventBus`. "Throttle changed", "save loaded",
"coupling occurred", "DPU mode toggled". Producer publishes when something
happens; subscribers react.

```csharp
bus.Publish(new ThrottleChanged(vehicleId, newNotch));
bus.Subscribe<ThrottleChanged>(e => ...);
```

### Streams (services)

Continuous producer-cadence push for physics-shaped data. Coupler force,
kinematics, brake pressure, grade ahead. The producer is computing this
every tick whether anyone subscribes or not (the physics demands it).
Consumers subscribe; the producer decides cadence.

```csharp
ICouplerForces
    void Subscribe(VehicleId v, Action<CouplerStressSample> handler);
    CouplerStressSample Latest(VehicleId v);   // cheap snapshot accessor
```

`Latest()` is allowed for ergonomics — the producer already has the value
cached, exposing it isn't extra work. It's a snapshot of the stream, not
a separate query.

### Registry (directory)

`IServiceRegistry` is how a mod *finds* a stream provider or a service
implementation. Plumbing, not a communication shape.

```csharp
var forces = registry.TryGet<ICouplerForces>();   // optional
var ui     = registry.Get<IWindowService>();      // required
```

### Boundary rule

Events fire on change, streams flow on cadence.
**If you'd call `GetX()` every tick, you wrote a stream as a query — fix
the contract, not the call site.**

### The web exception

The browser client (`ca.jwsm.railroader.web`) consumes a WebSocket published
by `mods/webview`. WebSocket exists because event-tick cadence renders
jerkily for moving entities in a browser; continuous streaming gives sub-tick
interpolation. This is a documented narrow break, **scoped to the browser-
process boundary**. In-process consumers still use the bus and streams.

---

## Lifecycle (api kernel primitive)

The api kernel owns the canonical game-lifecycle surface. Mods don't patch
lifecycle hooks themselves — they subscribe to typed events on the bus or
query `IGameLifecycle` for current phase / current save id.

This is a first-class kernel concern because almost every component cares
about it: persistence (cleanup, save state at strategic moments), foundational
mods (patch state reset across save loads), ui (show/hide on phase change),
logger (per-session file boundaries), feature mods (load mod state on world
load, save on unload).

### Phases

```
Bootstrap                 ← api itself coming up; before any mod loads
        │
PreGame                   ← UMM loaded us, vanilla at main menu, no world
        │
WorldLoading              ← a save is being loaded (or new game starting)
        │
WorldLoaded               ← in-game and ready
        │
WorldUnloading            ← about to leave the world (with reason)
        │
WorldUnloaded             ← back at main menu
        │
ApplicationShuttingDown   ← process is exiting
```

`PreGame ↔ WorldLoading → WorldLoaded → WorldUnloading → WorldUnloaded` is
a loop the player can run multiple times in a single session (exit to menu,
load another save, exit again).

### Contract

```csharp
IGameLifecycle                              // L1, in api kernel
    LifecyclePhase CurrentPhase { get; }
    string CurrentSaveId { get; }            // null when not in WorldLoaded
    bool IsInWorld { get; }                  // CurrentPhase == WorldLoaded

    // Events also published on IEventBus for subscribers that prefer that.
    event Action<WorldLoadingEvent>          WorldLoading;
    event Action<WorldLoadedEvent>           WorldLoaded;
    event Action<WorldUnloadingEvent>        WorldUnloading;
    event Action<WorldUnloadedEvent>         WorldUnloaded;
    event Action<ApplicationShutdownEvent>   ApplicationShuttingDown;
```

### Why both service and bus

Subscribing via `IEventBus` is the default. The service is for **late
arrivers**: a mod that loads after `WorldLoaded` has already fired needs to
know it's already in-world without waiting for the next event. The service
also exposes `CurrentSaveId` for "what save are we on right now" without
hooking events.

### Implementation pattern

Lifecycle is detected by **observer patches in api/host** that follow the
patches-as-event-publishers default — they shape an event payload from the
patched call and publish through `GamePatchBus`. The kernel's
`IGameLifecycle` implementation subscribes, updates `CurrentPhase` and
`CurrentSaveId`, and re-fires typed events to consumers.

No mod patches lifecycle hooks themselves. No mod uses reflection to read
StateManager state. The kernel is the single point of truth.

### Initialization order within phases

Lifecycle phases describe *when* the game is in a state. They don't yet
describe *how mods initialize within a phase*. v0 hit ordering bugs in the
map-mod-loader experiments — mod X tried to use mod Y's services before Y
had finished wiring. The kernel needs to enforce ordering authoritatively.

**Multi-step bootstrap within `Bootstrap` and `WorldLoading` phases:**

```
1. Construct        ← DI happens; services constructed; no I/O, no game-state access
2. Register         ← services register their contracts in IServiceRegistry
3. Wire             ← services subscribe to events, observe streams, attach patches
4. Ready            ← all services declared ready; cross-mod calls are now safe
```

The kernel runs **all mods through each step before advancing**. No mod's
`Wire` runs until every mod's `Register` is complete. That guarantees any
contract is resolvable during `Wire`.

Within a step, mods are ordered by their **manifest-declared `requires`**
graph — kernel topo-sorts so a dependent mod's step runs after its
dependency's step in the same step. Two mods with no dep between them can
run in parallel within a step (or any order).

This pattern covers two flavors of dependency:

- **Contract dependency** — "I need `IFoo` registered" → resolved by step 2
  (Register) being complete before step 3 (Wire).
- **Initialization-order dependency** — "I need Foo's `Wire` to have run
  before mine" → resolved by topo-sort within the Wire step using
  `requires`.

Same multi-step pattern applies during `WorldLoading` — mods that need
to react to world load (load mod state, prime caches, attach world-scoped
subscriptions) get a `WorldLoading.Wire` step where ordering is again
topo-sorted by `requires`.

**Mods don't manage their own ordering.** If a mod thinks it needs to delay
its `Wire` step to wait for another mod, it should declare a `requires`
instead. Sleep loops, retries, or "is it ready yet?" polling are
anti-patterns.

### Persistence cleanup uses lifecycle

Save-scope cleanup (see *Persistence Contract*) hooks the lifecycle events:

- **`BootstrapCompleted`** — scan + clean orphaned save data (catches
  anything from prior sessions, especially crashes that skipped shutdown).
- **`WorldLoading`** — scan + clean (defensive, catches anything that
  changed while we were at the menu).
- **`WorldUnloaded`** — scan + clean (catches saves the player just deleted
  via the in-game menu).
- **`ApplicationShuttingDown`** — scan + clean (catches saves deleted at
  the main menu after returning from a world; data doesn't linger on disk
  between sessions).

If vanilla exposes a save-list-mutation event, the cleanup also subscribes
to it for instant reactive cleanup — bonus, not source of truth.

**Performance note:** cleanup is file deletes only — sub-second even for
large stores. The `ApplicationShuttingDown` handler must complete fast
enough to never delay game close. Wrap in try/catch; on any timeout-like
condition, abandon and let the next `BootstrapCompleted` catch it.

---

## Foundational mods

Required-foundational mods sit at L2. The composition root warns or refuses
to bootstrap dependents without them registered. Each is heavy enough that
it doesn't belong inside the api kernel, and load-bearing enough that the
system can't function without it.

### `physics` — physics ground truth

Provides the physics streams other mods rely on.

- **Additive, never replacing.** Vanilla physics keeps running. Reads vanilla
  state, derives richer values (real coupler forces, slack action,
  mass-distributed dynamics), exposes them as streams.
- **Implements** all physics contracts defined in api: `ICouplerForces`,
  `IKinematics`, `IAirState`, `IPowerState`, `IMassModel`, `ITrackProfile`,
  `IConsistTopology`, `IConsistDirection`.
- **May patch** vanilla code if needed to observe state for derivation.
  Behavior modification of vanilla physics is allowed but must be deliberate
  and additive.
- **Vanilla evolves; our contracts don't.** Vanilla has scaffolding for
  features it doesn't currently use (derailment formulas, weather-coupled
  adhesion, curve-speed enforcement). Future game patches will fill some of
  these in. When that happens, our physics mod's *implementation* swaps its
  input source for that value; the *contract* on the api boundary stays
  stable. Consumers don't know or care which side computed the value. This
  gives us permission to ship "good enough" derivations now without locking
  ourselves out of cleaner implementations later.

The exact shape of the model — observation-only with better math vs. selective
intervention via `ControlProperties` — is a phase-3-ish design call.

Vanilla physics surface is mapped in `docs/research/physics-vanilla-survey.md`.

### `ui` — UI framework

Provides the windowing framework, theme, and assets. The "dirty work" of UI
lives here so the api kernel doesn't have to host it.

- **Implements** UI contracts defined in api: `IWindowService`,
  `IBottomBarService`, `IThemeService`, `IAssetService`, `ISurfaceRegistry`.
- **Owns** windowing impl, USS theme, fonts/icons/sprites, surface contracts.
- **Mods contribute components** — they describe content declaratively
  against named surfaces (Equipment window, our-own-bottom-bar, HUD).
  Mods never touch Unity UI Toolkit. ui owns rendering.
- **No game UI patching.** All our UI is self-contained in our own surfaces.
  Theme drift dies because mods can't theme; asset duplication dies because
  mods request by key.
- **Single project, no tiers.** v0's `abstractions/core/runtime` split was
  a cluster. v1 is one assembly that registers contracts and owns
  implementation.

---

## Feature mods (`mods/*`)

L3. Each subfolder is one mod that bootstraps into the api kernel.

### Hard rule: no patches

**`mods/*` cannot Harmony-patch the game.** Period. If a mod thinks it needs
a patch, that's a signal:

- The primitive it needs doesn't exist yet → extend api/physics/ui to
  expose it.
- It's a new foundational concern → consider promoting to top-level.

There is no "just this once" exception. The patch surface stays bounded to
api (observer patches) and L2 mods (their own behavior patches). This forces
the foundation to be honest about what it exposes.

### Canonical mod shape

A mod can simultaneously be three things, and most are at least two:

1. **Consumer** — subscribes to bus events, queries contracts, reads streams.
2. **Service provider** — implements a contract from api and registers it
   for others.
3. **UI contributor** — registers components into ui-owned surfaces.

ETA is the canonical example of all three: consumer of physics + waypoints,
provider of `IEta`, contributor to the equipment window.

### Cross-mod relationships

- Mods **never reference each other's assemblies**. All cross-mod talk goes
  through api contracts, the bus, or streams.
- **Optional dependencies** — `registry.TryGet<IFoo>()`, gracefully degrade
  if missing.
- **Hard dependencies** — declare `requires: [IFoo]` in `info.json`.
  Composition root refuses to bootstrap if missing.

### Closed-loop control discipline

Any mod that **writes control inputs** (throttle, brake, reverser) must
**consume the physics streams those inputs affect**. This rule exists
because v0's AE smoothing exploded a 200-car consist by writing controls
without reading physics consequences.

Control-modifying mods declare `physics` as a hard dep. Composition root
enforces. The canonical example is `mods/enginecontrol` — see its README.

### Orchestration discipline: through the api, not around it

The api is the **single source of truth** for orchestration — timing,
ordering, lifecycle, coordination, threading, caching. When a mod hits a
problem in any of those areas, **the fix lands in the api, not in mod
code**.

Anti-patterns to recognize and escalate:

- "It works most of the time but sometimes X isn't ready" → init-order
  problem. Escalate to the lifecycle/Wire phase ordering, declare a
  `requires`, or add a kernel-level ready signal. **Don't add a sleep/retry
  loop in the mod.**
- "We rebuild this every world load and it's slow" → caching primitive
  needs to expose the right hashing helper, or the lifecycle hook is
  missing. **Don't roll a per-mod file cache.**
- "I need to do this expensive work without freezing the game" → threading
  primitive. **Don't `Task.Run` directly or write your own
  SynchronizationContext juggling.**
- "Mod A keeps stepping on Mod B's state" → contract or event ordering
  problem in the api. **Don't add a "let me check if A finished" poll
  in B.**

When you find yourself reaching for sleep, polling, retry, lock, or
"phantom" coordination state inside a mod, **stop and look at the api
surface**. Either:

- The primitive you need exists and you're not using it correctly →
  read the docs.
- The primitive doesn't exist yet → file an issue, propose the api change,
  add it to the kernel.

This rule keeps mods thin and the kernel honest. v0's drift happened in
part because mods worked around api gaps instead of demanding the gap
be filled.

---

## Persistence Contract

### Layout

```
Mods\ca.jwsm.railroader.<mod>\persist\
    saves\<saveId>\
        <key>.json
        <key>.json.bak                          ← previous version, atomic-write fallback
    global\
        <key>.json
```

**Per-mod folders**, not centralized. Owned by the mod, deleted with the mod,
no cross-mod pollution.

### Two-tier API

```csharp
IPersistenceService                              // L1, lives in api
    IPersistenceContext GetContext(string ownerModId, PersistenceScope scope);

IPersistenceContext
    // Convenience tier — 90% of use cases.
    void Save<T>(string key, T value);           // JSON, atomic, .bak fallback
    bool TryLoad<T>(string key, out T value);
    void Delete(string key);

    // Power-user tier — non-JSON formats (SQLite, binary, multi-file stores).
    string GetFilePath(string key, string extension);   // .../<key>.<ext>
    string RootDirectory { get; }                       // mod handles its own IO
```

### Atomic write pattern (convenience tier)

```
1. Write new content to:    <key>.json.tmp
2. If <key>.json exists:    rename to <key>.json.bak
3. Rename:                  <key>.json.tmp → <key>.json
```

Power dies between steps 2 and 3 → `.bak` is the recoverable previous version.
Power-user tier opts out: mods using SQLite/etc. handle their own atomicity.

### Scopes

- `Save` — tied to current game save. **Auto-cleaned** when the corresponding
  game save no longer exists (see *Save-scope cleanup* below). Player never
  has to wipe by hand.
- `Global` — per-install, shared across saves. Mod settings, install-level
  caches. Never auto-cleaned.

No `World` scope yet — defer until a real use case appears.

### Mod-parity validation on load

A save records the mod set that produced it. Loading refuses if the local
mod set isn't a match, with the same hard-fail discipline as MP join
(see *Multiplayer / mod parity*). The api kernel enforces this; mods
don't opt in or out.

### Save-scope cleanup

Day-one behavior: orphaned save-scoped data is removed automatically. A mod
shouldn't accumulate gigabytes of dead data because the player deleted a
campaign three months ago.

**Mechanism (api kernel responsibility):**

- On bootstrap, the persistence service enumerates each mod's `saves/`
  directory and cross-references the game's current save manifest.
- Any `saves/<saveId>/` folder whose `<saveId>` is not in the game's save
  list is deleted (recursive, including `.bak` and `.tmp` files).
- Cleanup runs in try/catch; failures log a warning and proceed. Never
  blocks game startup.
- Cleanup is **proactive on startup**, not reactive to save-deletion events
  (player may delete saves outside the game; we can't always observe).

If a save-deletion event is exposed by the game, the persistence service
also reacts to it — but startup scan is the source of truth, not the event.

Mods don't opt in or out. Auto-cleanup is unconditional for `Save` scope.

### File extension

- Default: `.json` (Newtonsoft serialization).
- Power-user tier supports any extension via `GetFilePath(key, ext)`.

---

## Caching (api kernel primitive)

Many mod-derived artifacts are expensive to build but stable across sessions:
terrain meshes, track-graph derivations, asset stores, indexed lookups,
precomputed overlays. Re-deriving them every load wastes time. Mods
shouldn't roll their own caching — the kernel owns it.

### Goal

If the inputs haven't changed since last session, serve the cached artifact.
If they have, rebuild and replace. Mods describe inputs; kernel hashes,
stores, validates, serves.

### Contract

```csharp
ICacheService                              // L1, in api kernel
    ICacheContext GetContext(string ownerModId);

ICacheContext
    bool TryGet<T>(string key, string inputHash, out T value);
    void Put<T>(string key, string inputHash, T value);
    void Invalidate(string key);
    void InvalidateAll();

    // Helpers — mods rarely need to compute their own hashes.
    string ComputeHashOfFiles(params string[] paths);
    string ComputeHashOfStrings(params string[] inputs);
    string CombineHashes(params string[] hashes);
```

Usage shape:

```csharp
var inputHash = cache.ComputeHashOfFiles(graphFile, configFile);
if (!cache.TryGet<TrackGraph>("track-graph", inputHash, out var graph))
{
    graph = ExpensiveBuild();
    cache.Put("track-graph", inputHash, graph);
}
```

### Storage

Per-mod folder, parallel to `persist/`:

```
Mods\ca.jwsm.railroader.<mod>\cache\
    <key>.<hash>.bin          ← serialized artifact
    <key>.<hash>.meta         ← metadata (timestamp, mod version)
```

Filename includes the hash so mismatched-hash files are obviously stale and
get purged automatically.

### Auto-invalidation

Cache entries are invalidated automatically:

- **Mod version change** — at `BootstrapCompleted`, kernel checks each mod's
  manifest version against the cache's recorded version. Mismatch → purge.
- **Input hash mismatch** — `TryGet` returns false; old entry purged on next
  `Put` for the same key.
- **Explicit `Invalidate(key)`** — mods can force purge.
- **`InvalidateAll()`** — for nuclear cases (mod reset).

### What cache is NOT

- Not a replacement for persistence. Cached data is **regeneratable from
  inputs**; persistence holds **truth that can't be re-derived** (player
  state, settings, save data).
- Not for hot-path frame-scoped caching — that's per-service in-memory
  caching, not file-backed.
- Not for inter-mod sharing. Each mod's cache is its own.

### Lifecycle interaction

- **`BootstrapCompleted`** — version-check + purge stale.
- **`ApplicationShuttingDown`** — flush any in-memory pending writes.
- Cache is otherwise lazy — built on demand by `Put`, served on demand by
  `TryGet`.

### Format

- Default: MessagePack binary (compact, fast).
- Per-key opt-out: `Put` overload that takes raw bytes for mods with their
  own format.

---

## Threading (api kernel primitive)

Most game state lives on Unity's main thread — Unity API access from a
background thread is undefined behavior. But not everything is Unity API:
parsing, hashing, compilation, graph derivation, file I/O, MessagePack
encoding. These are CPU-bound and benefit from background execution.

Mods shouldn't roll their own threads, `Task.Run` choices, or
synchronization-context juggling. The kernel owns it.

### Goal

Mods declare "this is background-safe work" and get an awaitable handle
back. Continuation runs on the main thread by default so any follow-up
that touches game state Just Works. Cancellation, progress, and timing
are first-class.

### Contract

```csharp
IBackgroundExecutor                              // L1, in api kernel
    Task<T> Run<T>(string description,
                   Func<CancellationToken, T> work);

    Task<T> Run<T>(string description,
                   Func<CancellationToken, IProgress<float>, T> work);

    Task Run(string description,
             Action<CancellationToken> work);

IMainThreadDispatcher                            // L1, in api kernel
    bool IsMainThread { get; }
    Task RunOnMainThread(Action work);
    Task<T> RunOnMainThread<T>(Func<T> work);
```

### Usage shape

```csharp
// Dispatch expensive graph compilation to a background thread:
var task = executor.Run<DispatchGraph>(
    "Compiling dispatch graph",
    (ct, progress) => {
        return ExpensiveGraphCompile(rawGraphFile, ct, progress);
    });

// Continuation runs on main thread by default — safe to touch game state:
var graph = await task;
dispatchService.SetGraph(graph);   // back on main thread
```

### Hard rules

- **Background work MUST NOT touch Unity API** (any UnityEngine type with
  thread affinity — most of them) **or game state directly**. Read inputs
  on main thread, copy them in, return results, apply on main thread.
- **All background tasks get a `CancellationToken`. Mods MUST honor it.**
  Long-running unkillable work is an anti-pattern.
- **Continuations default to the main thread.** Mods don't have to think
  about marshaling unless they explicitly want to chain background work.
- **`description` is required** — used for diagnostics, perf logging, and
  UI surfaces (see below). "Doing stuff" is not a description.

### Composition with UI

The threading primitive doesn't know about UI. But the ui mod can provide
a convenience helper that wraps a `Task` with a "Processing…" overlay:

```csharp
// In a mod:
await ui.RunWithProgress("Compiling dispatch graph",
    executor.Run("Compiling dispatch graph",
                 (ct, progress) => CompileGraph(ct, progress)));
```

ui's helper subscribes to `IProgress<float>`, shows a modal/non-modal
indicator, hides on completion. Threading and UI compose; neither knows
about the other's internals.

### Lifecycle interaction

- All in-flight background tasks are cancelled on `WorldUnloading` and
  `ApplicationShuttingDown`. Mods that started a long-running task
  should not assume it survives a phase transition.
- The kernel waits a short bounded grace period (e.g., 2s) for cancellations
  to settle on shutdown. After that, it abandons remaining tasks rather
  than blocking the game close.

### What threading is NOT

- Not a replacement for Unity coroutines for time-spread main-thread work
  (frame-paced animations, deferred main-thread updates). Use coroutines
  for those.
- Not for inter-thread shared mutable state. The pattern is: copy in,
  process, copy out. If mods need shared mutable state across threads,
  they're doing it wrong.
- Not a job system. CPU-bound parallelism for tight inner loops uses
  Unity's Job/Burst system if it ever becomes warranted; the executor is
  for coarse-grained background work.

---

## Logging

**Goal**: our codebase's voice clearly audible in its own log file, scope-
tagged, single chronological timeline, no Player.log noise.

### Contract

```csharp
ILoggerFactory                                   // registered first thing in composition root
    ILogger CreateLogger(string scope);          // scope: hierarchical dotted

ILogger
    void Trace/Debug/Info/Warn/Error(string message, params object[] args);
    void Coalesce(string key, string message, Severity sev);
    void CoalesceWarning(string key, string message);

ILogPipeline (internal to factory)
    LevelFilter (per-scope config)
    Coalescer (5s dedup window, summary on flush)
    Sinks: [RotatingFileSink, UmmErrorMirrorSink, OverlaySink (dev), ...]
```

Every service takes `ILoggerFactory` in its constructor and creates its own
scoped logger. **No statics.** No global access pattern.

### Scope naming

Hierarchical, dotted, lower-kebab-case:

```
api.bootstrap
api.consist-topology              (api observer patch / contract impl)
physics.coupler-forces
physics.air                       (physics mod's own scope)
ui.windows
ui.theme
mods.dpu.power-propagation
mods.eta.calculator
```

The mod or component owns its prefix exclusively. Filtering with
`grep '\[mods.dpu' api-*.log` returns *only* the DPU mod's emissions.

### File output

```
Mods\ca.jwsm.railroader.api\logs\
    api-2026-04-26-184912.log          ← current run (active write)
    api-2026-04-26-171533.log          ← previous run
    ...
    latest.log                          ← optional hardlink to current
```

**Rotation rules:**

- ISO-ish timestamp filename (`api-{yyyy-MM-dd-HHmmss}.log`). Natural sort =
  chronological sort.
- On startup: enumerate `api-*.log`, sort descending, delete past N.
  Default `N = 5`. Configurable via mod settings (`maxRetainedLogs`).
- Files **not matching** `api-*.log` are never touched. Rename to preserve
  (e.g. `api-bug-report-2026-04-26.log`) — rotation leaves it alone forever.
- Pattern itself is **not** configurable (would defeat the rename-to-preserve
  trick).

**Failure handling:**

- Rotation runs in try/catch. Delete failure (locked file, perms) logs a
  warning *to the new file* and proceeds. Never blocks game startup.
- File-open failure falls through to UMM logger only. Graceful degradation.

**Convenience:**

- `latest.log` hardlink to active file. Editor pinned to it auto-tails.
- Errors **also** mirror to UMM logger (Player.log) as a courtesy — anyone
  glancing at Player.log who sees an exception wants to know which mod faulted.
  Routine ops/diag logs go to our file alone.

### Coalescer

- Same key + identical message body collapses within a 5-second window.
- Flush emits "Suppressed N over Xs" summary line — predictable cadence,
  not ad-hoc.
- Coalescing is a **pipeline stage**, not a separately-callable utility.
  `logger.Coalesce(...)` routes through the pipeline like any other emission.

---

## Multiplayer

**Goal**: every service knows its authority, every patch knows its class,
every mutation goes through a typed primitive. No `StateManager.IsHost`
sprinkled at call sites.

### Contract

```csharp
IAuthority                                       // injected into every service
    bool IsHost { get; }                         // authoritative for simulation
    bool IsClient { get; }                       // remote follower
    bool IsLocal { get; }                        // single-player (≈ host)
    event Action AuthorityChanged;               // hot-swap (host migration)

IReplicatedStateRegistry
    IReplicatedState<T> Register<T>(string key, T defaultValue);

IReplicatedState<T>
    T Get(VehicleId vehicleId);
    void Set(VehicleId vehicleId, T value);     // host-only (or routed)
    void Observe(VehicleId vehicleId, Action<T> onChange);
    // syncs via game's KVO under the hood

IRequestRouter
    Task<TResult> Send<TRequest, TResult>(TRequest request);   // client → host
    void RegisterHandler<TRequest, TResult>(Func<TRequest, TResult> handler);
    // host registers; clients invoke; framework handles transport
```

### Authority class per service

Every service declaration includes its authority requirement:

```csharp
[ServiceAuthority(AuthorityClass.HostOnly)]
public sealed class DpuAirSimulation : IDpuAirSimulation { ... }

[ServiceAuthority(AuthorityClass.Both)]
public sealed class ConsistTopology : IConsistTopology { ... }

[ServiceAuthority(AuthorityClass.ClientOriginated)]
public sealed class InspectorActions : IInspectorActions { ... }
// client invokes; routes through IRequestRouter to host; host validates + applies
```

Composition root reads the attribute; constructs only services compatible
with current authority. **Host-only service constructed on a client = startup
error**, not silent runtime weirdness.

### Patch class per patch

```csharp
[PatchClass(PatchClass.Simulation)]              // host-only
[PatchClass(PatchClass.View)]                    // both — render-side adjustments
[PatchClass(PatchClass.InputDispatching)]        // client-side, validated by host
```

Same enforcement: composition root only registers patches compatible with
authority. Note that `mods/*` cannot patch at all — this attribute applies to
api observer patches and L2 mods' own patches only.

### Dev-mode "simulated client"

A settings toggle that runs the local instance as if it were a remote client
(authority = Client, host-only services skipped, client-only paths exercised).
Catches "I forgot to gate this on host" bugs at dev time without needing a
second machine.

### Mod parity (hard rule)

**Server and client must run identical mod sets to play together.** No
partial-MP, no graceful degradation, no "best effort." Mismatch on connect
= refuse-to-join, with explicit reason in the rejection message:

```
Cannot join: mod set mismatch.
  Server runs but client is missing:  [mod-a v1.2, mod-b v0.4]
  Client runs but server is missing:  [mod-c v2.0]
  Version mismatches:                 [mod-d server v1.0, client v1.1]
```

The handshake happens at connection time, before any game state syncs.
The same `info.json` `requires` mechanism that declares cross-mod
dependencies also declares MP parity — every mod is automatically
parity-required unless it opts out (rare; only for purely-local mods like
in-game console UX additions).

**Saves inherit the same rule.** A save records the mod set that produced
it; loading refuses if the local mod set isn't a match, with the same
clear-reason UX. This falls out automatically: a save's mod set is just
what the host had when saving.

Why this rule:

- The save format only persists game state, not map content (industries,
  track, scenery come from mod assets every load). Missing a content mod =
  orphan references = corrupt save.
- Mod-divergent simulations diverge silently. A client missing a physics
  mod believes different forces; the host's authoritative state would keep
  overriding, but the client's UI lies.
- "Refuse with clear reason" is honest. "Try to load anyway and pray" is
  the v0 path that produced unrecoverable saves.

This rule is enforced by the api kernel at connection time and at save
load time; mods don't have to opt in.

### State sync — when to use what

- **Game-native data** (CutOut, MU, throttle, reverser, train brake): write
  through `ControlProperties`; vanilla KVO sync handles propagation. No
  `IReplicatedState` wrapper needed.
- **Our extension flags** (DPU enabled, AE smoothing mode, etc.): use
  `IReplicatedState<T>` — wraps the same KVO mechanism but typed and managed.
- **Per-mod settings + persisted state**: persistence layer (host-only on
  save, replicated through host-side application of save data on load).
- **Local view state** (window position, scroll position, favorite list):
  client-local, **not** synced. Persist locally if at all.

---

## Directionality Contract

`IConsistDirection` (a contract in api, implemented in `physics`) is the
**only** legal source of truth for direction, orientation, and signed speeds.

**Forbidden anywhere outside `physics`'s implementation:**

- Reading `BaseLocomotive.FrontIsA` directly
- Computing "is forward" from `velocity` sign
- Multiplying anything by an orientation-correction factor

**Required pattern** for any code that needs direction:

```csharp
// From a feature mod / UI mod / api consumer:
var direction = registry.Get<IConsistDirection>();
float speed   = direction.UnsignedSpeedMph(vehicleId);    // for display
Sign  dir     = direction.DirectionOfTravel(vehicleId);   // for logic
int   orient  = direction.OrientationRelativeToLead(loco); // for fan-out
```

If `IConsistDirection` doesn't expose what you need, **add a primitive** to
the interface (in api) and implement it in `physics` — don't compute it
yourself.

---

## Patch Policy

### Where patches live

| Patch type            | Lives in       | Reviewed against                          |
|-----------------------|----------------|-------------------------------------------|
| Observer / KVO mirror | api            | "exposes data, changes nothing"           |
| Behavior modification | physics        | "additive to vanilla, deliberate"         |
| Cross-cutting hooks   | api            | Must be opt-in by feature mods via contracts |
| Game UI modification  | **forbidden**  | We never touch vanilla UI prefabs         |
| Anything in `mods/*`  | **forbidden**  | If you need a patch, the foundation needs to expose a primitive |

### Default pattern: patches as event publishers

Patches own **no logic**. They shape an event payload from the patched call
and publish to the bus. Mods (foundational or feature) subscribe and decide
what to do.

```csharp
// api/Patches/SomeGameEventPatch.cs
[HarmonyPatch(typeof(GameType), "Method")]
internal static class SomeGameEventPatch
{
    [HarmonyPostfix]
    private static void Postfix(GameType __instance)
    {
        GamePatchBus.Publish(new SomeGameEvent(__instance.Id, __instance.SomeValue));
    }
}
```

`GamePatchBus` is a thin static publisher that the composition root connects
to the real `IEventBus` once it's wired. Until connected, publishes are
no-ops (graceful degradation during bootstrap).

Why this is the default:

- Patches stay trivial — one publish per hook, no logic to test in patch land.
- All decision-making lives in subscribers, where it's testable and DI-able.
- The static patch-state-holder pattern goes away entirely.
- Adding subscribers doesn't require changing patches.

This pattern was identified by reviewing v0 (see
`docs/research/v0-api-review.md`) and is the recommended approach for new
patches in v1.

### Fallback: static handoff (narrow cases only)

When a patch genuinely needs synchronous service access — typically a
**behavior-changing prefix** that returns `false` to skip the original, or
needs to consult authority before deciding what to publish — the legacy
static-handoff pattern is acceptable:

```csharp
// foundational-mod/Patches/SomeBehaviorPatchState.cs
internal static class SomeBehaviorPatchState
{
    internal static IServiceX Service;  // set by composition root
}

// foundational-mod/Patches/SomeBehaviorPatch.cs
[HarmonyPatch(typeof(GameType), "Method")]
internal static class SomeBehaviorPatch
{
    [HarmonyPrefix]
    private static bool Prefix(GameType __instance)
    {
        var svc = SomeBehaviorPatchState.Service;
        if (svc == null) return true;       // not wired yet — defer to vanilla
        return svc.ShouldRun(__instance);   // return false to skip vanilla
    }
}

// foundational-mod/Bootstrap/CompositionRoot.cs
SomeBehaviorPatchState.Service = serviceX;

// foundational-mod/Bootstrap/PatchState.cs (Reset on save unload)
SomeBehaviorPatchState.Service = null;
```

Behavior-changing patches are **L2-only** (physics or ui), never api, never
`mods/*`. So this pattern lives in foundational mods, not in the kernel.

### Authority gating in patches

Patches declare their class via attribute:

```csharp
[PatchClass(PatchClass.Simulation)]              // host-only — simulation patch
[HarmonyPatch(typeof(LocomotiveAirSystem), "UpdateAir")]
internal static class CouplerForceObserverPatch { ... }
```

Composition root only registers patches compatible with current authority.

---

## Service Ownership Inventory

Contracts always live in **api**. The "Owner" column below is the
implementer / provider.

| Contract                  | Layer | Authority         | Implemented in                |
|---------------------------|-------|-------------------|--------------------------------|
| ILoggerFactory            | L1    | Both              | api (kernel)                  |
| IAuthority                | L1    | Both              | api (kernel)                  |
| IEventBus                 | L1    | Both              | api (kernel)                  |
| IServiceRegistry          | L1    | Both              | api (kernel)                  |
| ICommandRegistry          | L1    | Both              | api (kernel)                  |
| IGameLifecycle            | L1    | Both              | api (kernel)                  |
| IPersistenceService       | L1    | Host (writes)     | api (kernel)                  |
| ICacheService             | L1    | Both              | api (kernel)                  |
| IBackgroundExecutor       | L1    | Both              | api (kernel)                  |
| IMainThreadDispatcher     | L1    | Both              | api (kernel)                  |
| IReplicatedStateRegistry  | L1    | Both              | api (kernel)                  |
| IRequestRouter            | L1    | Both (asymmetric) | api (kernel)                  |
| ICouplerForces            | L1    | Both              | physics                       |
| IKinematics               | L1    | Both              | physics                       |
| IAirState                 | L1    | Both              | physics                       |
| IPowerState               | L1    | Both              | physics                       |
| IMassModel                | L1    | Both              | physics                       |
| ITrackProfile             | L1    | Both              | physics                       |
| IConsistTopology          | L1    | Both              | physics                       |
| IConsistDirection         | L1    | Both              | physics                       |
| IControlRequest           | L1    | Host              | api (kernel) or physics — TBD |
| IWindowService            | L1    | Client (view)     | ui                            |
| IBottomBarService         | L1    | Client            | ui                            |
| IThemeService             | L1    | Client            | ui                            |
| IAssetService             | L1    | Client            | ui                            |
| ISurfaceRegistry          | L1    | Client            | ui                            |
| IEta                      | L1    | Both              | mods/eta                      |
| IDurability               | L1    | Both              | mods/durability               |
| IEngineControl            | L1    | Both              | mods/enginecontrol            |
| IDispatch                 | L1    | Both              | mods/dispatch                 |
| IMapModRegistry           | L1    | Both              | mods/mapmodloader             |
| IEditorSession            | L1    | Both              | mods/editor                   |
| IWebChannel               | L1    | Both              | mods/webview                  |

This table is the **canonical answer** for "who implements what." Adding a
new contract requires updating this table.

---

## Phase Plan

Build phases. Each phase produces working software. No phase requires
back-edits to a prior one.

| Phase | Mining targets (ILSPY) | Output |
|-------|------------------------|--------|
| **0. Bootstrap** | UMM lifecycle, Harmony patterns | api kernel: composition root + mod lifecycle + ILoggerFactory + IAuthority + IEventBus + IServiceRegistry + ICommandRegistry |
| **1. Persistence + save lifecycle** | StateManager, save messages, mod data conventions | IPersistenceService with two-tier API + atomic write |
| **2. Physics — kinematics + topology** | Car, BaseLocomotive, EnumerateCoupled, FrontIsA | physics mod scaffolding; IConsistTopology, IConsistDirection, IKinematics |
| **3. Physics — air + power** | LocomotiveAirSystem, BrakeLine, VentedValve, AirConnection, ControlProperties | IAirState, IPowerState, IControlRequest |
| **4. Physics — track + mass + couplers** | Graph, Segment, LocationF, speed limits, mileposts, in-train forces | ITrackProfile, IMassModel, ICouplerForces (the closed-loop substrate) |
| **5. Multiplayer primitives** | KVO sync, PlayerPropertiesManager, Messenger | IReplicatedStateRegistry, IRequestRouter — first migration uses these |
| **6. UI framework** | UI Toolkit basics (we DON'T mine game prefabs — we build our own) | ui mod: IWindowService, IThemeService, IAssetService, ISurfaceRegistry, theme.uss, our own surfaces |
| **7. First feature mod** | (consumes phases 1-6) | Pick one — mods/eta is a strong candidate (multi-role, exercises all three role types) |
| **8. AE primitives** | AutoEngineerPlanner, AutoEngineerPersistence | Read access to AE intent + interception via IControlRequest |
| **9. mods/enginecontrol** | (consumes phases 1-4, 8) | DPU + dynamic brake + AE smoothing — canonical closed-loop example |
| **10+** | (consumes whatever) | mods/dispatch, mods/durability, mods/editor, mods/mapmodloader, mods/webview + web client, mods/console, NPC, etc. |

Each phase **stops, ships, and is reviewed** before the next starts. No
"phase 6 partly done while we start phase 7."

---

## Naming Conventions

### Repo

Single monorepo: `excron/ca.jwsm.railroader`. Top-level peers per the
workspace layout above.

### Namespaces

```
Ca.Jwsm.Railroader.Api.*                    — api kernel impl + observer patches
Ca.Jwsm.Railroader.Api.<Domain>.Contracts   — interfaces (in api project)
Ca.Jwsm.Railroader.Api.<Domain>.Models      — value types / snapshots (in api project)
Ca.Jwsm.Railroader.Physics.*                — physics mod impl
Ca.Jwsm.Railroader.Ui.*                     — ui mod impl
Ca.Jwsm.Railroader.Mods.<Mod>.*             — feature mods
Ca.Jwsm.Railroader.Web.*                    — browser client (separate runtime)
```

### Logging scope names

Hierarchical, dotted, lower-kebab-case:

```
api.<service-name>               (e.g. api.consist-topology)
api.patches.<patch-name>         (e.g. api.patches.couplerforce-observer)
physics.<feature>                (e.g. physics.coupler-forces, physics.air)
ui.<framework-component>         (e.g. ui.windows, ui.theme)
mods.<mod>.<feature>             (e.g. mods.enginecontrol.smoothing)
```

### File extensions

- `.json` — convenience-tier persistence (default)
- `.json.bak` — atomic-write fallback (auto-managed)
- `.json.tmp` — atomic-write in-progress (auto-managed)
- `.log` — log files (rotating)
- `.db` / `.bin` / etc. — power-user-tier files (mod's choice)

### Coalescer keys

Match the scope of the emitting logger; suffix with the event name:

```csharp
logger.Coalesce("lead-fallback", message);    // emitted under api.consist-topology scope
// → grep '\[api.consist-topology\]' for that scope; coalescer key inside
```

---

## Build & Deploy

Same patterns as v0 (these worked):

- `dotnet build` per project; `Directory.Build.props` for shared MSBuild.
- `GameDir` env var or fallback path resolves game assemblies.
- Single PowerShell deploy script that builds + copies to game `Mods/` dir.
- Per-mod include flags (`-IncludeUi`, `-IncludePhysics`, etc.).

Document deploy script as part of phase 0 setup; mods plug into it as added.

---

## Versioning

The monorepo means contracts and consumers move in lockstep — no
inter-project version skew is possible within our own stack. Versioning
becomes important only at two boundaries:

- **api kernel version** (`ApiVersion(major, minor, patch)`) — declared so
  any future foreign UMM mods that consume our contracts can validate.
  Within a major version, contracts are additive.
- **Per-mod `info.json`** — declares mod identity, hard deps
  (`requires: [IFoo]`), and any other manifest metadata. The composition
  root uses this for bootstrap validation.

Cross-major changes require explicit migration path documented per breaking
change.

---

## Open Questions

These deferred decisions need an answer before the relevant phase starts:

- [ ] **Snapshot refresh cadence**: every Update, every FixedUpdate, on
  demand only? Likely FixedUpdate for physics state, Update for view state.
  Decide in phase 2.
- [ ] **Replicated state transport**: KVO for everything, or do we need our
  own RPC channel for non-KVO data? Decide in phase 5.
- [ ] **UI contribution shape**: declarative widget tree, typed contribution
  interfaces per surface, or hybrid? Decide in phase 6.
- [ ] **In-game console UX**: where does our slash-command input live?
  Are we shipping our own console window in `mods/console`, or piggybacking
  on something? (Note: we don't modify the game's console.) Phase 6-7.
- [ ] **Physics intervention model**: observation-only with better math, or
  selective intervention via `ControlProperties`? Where's the line? Decide
  in phase 3-4.
- [ ] **AE smoothing UX**: notch sensitivity, jerk profile, reaction-time
  knob — what's exposed in mod settings? Decide in phase 9.
- [ ] **Save migration**: legacy v0 mod data — readable from v1, ignore
  silently, or refuse to load? Probably ignore silently with warn.

---

## Glossary

- **api / api kernel**: `ca.jwsm.railroader.api`. The L1 controller —
  composition root, foundation services, all cross-mod contracts, observer
  patches. Stays thin and coherent.
- **physics**: `ca.jwsm.railroader.physics`. Required-foundational mod (L2).
  Provides physics ground truth additively to vanilla.
- **ui**: `ca.jwsm.railroader.ui`. Required-foundational mod (L2). Provides
  windowing framework, theme, assets. Owns its own UI surfaces; never
  modifies vanilla UI.
- **web**: `ca.jwsm.railroader.web`. Browser-based map viewer client.
  Different runtime — not a UMM mod, not Harmony-patched. Talks to
  `mods/webview` over WebSocket.
- **mod / feature mod**: `ca.jwsm.railroader.mods/<name>`. L3. Anything in
  `mods/*` — features, UI contributors, command sources.
- **required-foundational mod**: An L2 mod (`physics`, `ui`) that the
  composition root warns or refuses to bootstrap dependents without.
- **canonical mod shape**: A mod can simultaneously be three things —
  consumer (subscribes to bus / streams / contracts), service provider
  (implements an api contract), UI contributor (registers components into
  ui-owned surfaces). Most mods are at least two.
- **closed-loop control**: Discipline rule — mods that write control inputs
  (throttle, brake) must consume the physics streams those inputs affect.
  Enforced via manifest-declared `requires`. Originating incident: v0 AE
  smoothing exploded a 200-car consist's couplers.
- **contribution model**: Mods don't own UI windows. ui owns surfaces;
  mods register components against named surfaces (Equipment column, HUD
  overlay, etc.). Theme drift and asset duplication die structurally.
- **primitive**: A typed contract in api that exposes either game state or
  framework capability. "Primitive" because it composes upward; mods build
  on primitives.
- **lead candidate**: The locomotive in a consist with all of CutOut/MU/DPU
  unselected. Implies the player is driving from this loco.
- **DPU**: Distributed Power Unit. A locomotive in the consist that
  contributes power + air independent of the lead, treated as a remote
  follower.
- **frame-scoped cache**: A cache keyed on `(input, frameCount)` —
  invalidates each frame, hits within a frame.
- **fingerprint short-circuit**: Computing a cheap hash of structural state
  to skip work when nothing relevant changed.
- **the web exception**: The narrow break from the in-process bus/stream
  model — `mods/webview` publishes a WebSocket consumed by the browser
  client, because event-tick cadence renders jerkily for moving entities.
  Scoped to the browser-process boundary; in-process consumers still use
  the bus.
