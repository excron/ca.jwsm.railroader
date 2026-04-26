# Railroader Mod Stack — Architecture (v1)

## Preamble

This document captures the architectural decisions for the v1 rebuild of the
Railroader mod stack. v0 (currently in `_reference/`) was built incrementally
and accreted a lot of correct patterns alongside several layering mistakes that
became expensive to retrofit. v1 starts clean from a documented contract.

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
  → v1 makes "feature mods consume api primitives" the only legal pattern for
  game-behavior changes.

- **Per-tick simulation patches lived alongside data-exposing patches** in
  `api/host/Patches/`. The DPU air patch (changes physics) and a hypothetical
  KVO-mirror patch (exposes state) were treated as the same kind of thing.
  → v1 splits **observer patches** (api host) from **behavior patches**
  (feature mods). Different lifecycles, different review criteria.

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
  → v1 enforces hierarchical dotted scope names: `api.host.engine-control.tick`,
  `mods.dpu.air-postfix`.

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
  → v1 ships a small `theme.uss` from day one. Inline styles are the
  exception, not the default.

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
- `_reference/` deletes itself when the GitHub repos are archived (post-v1).

**Rules for ILSPY:**

- Read-only reference for game internals.
- Use `Grep`/`Read` to mine; don't try to compile against it.
- Each phase's mining produces notes in this doc or in phase-specific design
  notes — don't carry implementation knowledge in your head between sessions.

---

## Layer Model

```
L0  Game                               (Assembly-CSharp, read-only)
       ▲
       │ Harmony patches + KVO
       │
L1  Foundation primitives              ┐
       Logging, Authority, Persistence │  ca.jwsm.railroader.api
       Coalescing, Replication         │  (everything is one composition root)
                                       │
L2  Physics state                      │
       IPhysicsState aggregate         │
                                       │
L3  API contracts                      │
       Domain interfaces (consist,     │
       inspector, equipment, etc.)     ┘
       ▲
       │
L4  UI framework                       ca.jwsm.railroader.ui
       Windowing, theme, parts         (peer of api)
       ▲
       │
L5  Consumer mods                      ca.jwsm.railroader.mods\*
       Feature mods (DPU, AE, ETA)
       UI mods (windows, gauges)
```

### Layer rules

- **Dependencies flow downward only.** L5 depends on L1-L4; L4 depends on
  nothing in L5; L1-L3 depend on L0 only.
- **No cross-mod L5→L5 dependencies.** Two consumer mods communicate
  through L1-L3 contracts (via the service registry), never by referencing
  each other. If they need shared state, that state belongs in L1-L3 or in
  a shared "primitive mod" both depend on.
- **L1-L3 know nothing about L5 features.** Adding a new feature mod
  doesn't require api/host changes. If it does, the api primitive surface
  is wrong.
- **Patches that change game behavior live in L5.** Patches that observe
  game state for primitive exposure live in L1/L4 (api/host).

### Smell tests for layer placement

Use these when deciding where new code goes:

1. **"Would multiple unrelated mods break if I deleted this?"** → L1-L3.
2. **"Is this *deciding* something about the train?"** (DPU power, AE
   smoothing, ETA) → L5 feature mod.
3. **"Does this just *expose* game state in a typed way?"** → L1-L3.
4. **"Does this change what the game does at the physics level?"** → L5.
5. **"Does this render a window?"** → L4 framework if generic, L5 if
   specific.

---

## Three Foundations

These three primitives are load-bearing for every mod. Built first, used
everywhere, never retrofitted.

### § 1. Logging

**Goal**: our codebase's voice clearly audible in its own log file, scope-
tagged, single chronological timeline, no Player.log noise.

#### Contract

```csharp
ILoggerFactory                                   // registered first thing in composition root
    ILogger CreateLogger(string scope);          // scope: hierarchical dotted

ILogger
    void Trace/Debug/Info/Warn/Error(string message, params object[] args);
    void Coalesce(string key, string message, Severity sev);
    void CoalesceWarning(string key, string message);
    // structured: takes object[] for KV expansion, not just strings

ILogPipeline (internal to factory)
    LevelFilter (per-scope config)
    Coalescer (5s dedup window, summary on flush)
    Sinks: [RotatingFileSink, UmmErrorMirrorSink, OverlaySink (dev), ...]
```

Every service takes `ILoggerFactory` in its constructor and creates its own
scoped logger. **No statics.** No global access pattern.

#### Scope naming

Hierarchical, dotted, lower-kebab-case:

```
api.host.bootstrap
api.host.consist-topology
api.host.dpu-air
mods.ui.equipment
mods.ui.engine-control
mods.dpu.power-propagation
mods.ae.smoothing-controller
```

The mod or component owns its prefix exclusively. Filtering with
`grep '\[mods.dpu' api-*.log` returns *only* the DPU mod's emissions.

#### File output

```
Mods\ca.jwsm.railroader.api\logs\
    api-2026-04-26-184912.log          ← current run (active write)
    api-2026-04-26-171533.log          ← previous run
    api-2026-04-26-160203.log
    api-2026-04-26-152441.log
    api-2026-04-26-150815.log
    api-2026-04-26-143022.log          ← oldest kept
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

#### Coalescer

- Same key + identical message body collapses within a 5-second window.
- Flush emits "Suppressed N over Xs" summary line — predictable cadence,
  not ad-hoc.
- Coalescing is a **pipeline stage**, not a separately-callable utility.
  `logger.Coalesce(...)` routes through the pipeline like any other emission.

---

### § 2. Physics State

**Goal**: ONE source of truth for game physical state. Everything above
api/host reads through it. Never `Car.velocity` in a feature mod.

#### Contract

```csharp
IPhysicsState                                    // top-level aggregate
    IConsistTopology      Topology { get; }
    IConsistDirection     Direction { get; }
    IKinematics           Kinematics { get; }
    IAirState             Air { get; }
    IPowerState           Power { get; }
    IMassModel            Mass { get; }
    ITrackProfile         Track { get; }

IConsistTopology
    GetGroups(VehicleId selected) → ConsistSnapshot
    // groups + lead detection (flag-state-based)
    // frame-scoped cache built in

IConsistDirection
    Sign DirectionOfTravel(VehicleId selected)
    int OrientationRelativeToLead(VehicleId loco)        // ±1
    float SignedSpeedMph(VehicleId loco)                 // in lead's frame
    float UnsignedSpeedMph(VehicleId loco)               // magnitude only

IKinematics
    KinematicsSnapshot Get(VehicleId loco)
    // signed/unsigned mph, m/s, accel, position

IAirState
    AirSnapshot Get(VehicleId loco)
    // BP/BC/MR/ER psi, valve states, distributed pressures

IPowerState
    PowerSnapshot Get(VehicleId loco)
    // rated TE, applied TE, throttle, reverser, dynamic brake notch + applied

IMassModel
    MassSnapshot Get(VehicleId selected)
    // total mass, length, per-car weights, axle count, distribution

ITrackProfile
    TrackSnapshot Get(VehicleId loco)
    // current segment, N-segment lookahead with grade + speed limit
    // configurable lookahead distance
```

#### Strict rules

- **No consumer above api/host calls `Car.*`, `LocomotiveAirSystem.*`,
  `BaseLocomotive.*`, etc. directly.** If they need the data, it goes through
  `IPhysicsState`. If `IPhysicsState` doesn't expose it, that's a primitive
  gap to fill, not a workaround.
- All snapshots are **immutable** (`readonly` fields, no setters).
- All snapshots are **frame-scoped**: refreshed on a defined cadence (likely
  FixedUpdate for physics-tied data, Update for view-tied data). Caches
  invalidate per-frame; consumers see consistent within-frame views.
- **Patches that observe game state to make this layer possible live in
  api/host/Patches.** Patches that change game behavior live in feature mods.
- **Caching is the default**, not an opt-in. Every primitive that reads game
  state caches per frame.

#### Naming decision

`IPhysicsState` (state-of-physics, what the layer exposes) preferred over
`IPhysics` (would imply we *do* physics; we don't, the game does). Subordinate
interfaces are `IConsistTopology`, `IConsistDirection`, etc. — read as "a view
on the consist's X."

---

### § 3. Multiplayer

**Goal**: every service knows its authority, every patch knows its class,
every mutation goes through a typed primitive. No `StateManager.IsHost`
sprinkled at call sites.

#### Contract

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

#### Authority class per service

Every service declaration includes its authority requirement:

```csharp
[ServiceAuthority(AuthorityClass.HostOnly)]
public sealed class DpuAirSimulation : IDpuAirSimulation { ... }

[ServiceAuthority(AuthorityClass.Both)]
public sealed class ConsistTopologyService : IConsistTopology { ... }

[ServiceAuthority(AuthorityClass.ClientOriginated)]
public sealed class InspectorActionService : IInspectorActions { ... }
// client invokes; routes through IRequestRouter to host; host validates + applies
```

Composition root reads the attribute; constructs only services compatible
with current authority. **Host-only service constructed on a client = startup
error**, not silent runtime weirdness.

#### Patch class per patch

```csharp
[PatchClass(PatchClass.Simulation)]              // host-only
[PatchClass(PatchClass.View)]                    // both — render-side adjustments
[PatchClass(PatchClass.InputDispatching)]        // client-side, validated by host
```

Same enforcement: composition root only registers patches compatible with
authority.

#### Dev-mode "simulated client"

A settings toggle that runs the local instance as if it were a remote client
(authority = Client, host-only services skipped, client-only paths exercised).
Catches "I forgot to gate this on host" bugs at dev time without needing a
second machine.

#### State sync — when to use what

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

## Persistence Contract

### Layout

```
Mods\ca.jwsm.railroader.mods.<mod>\persist\
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
IPersistenceService                              // L1, lives in api host
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

- `Save` — tied to current game save. Orphaned when save is deleted (we don't
  auto-clean; player can wipe by hand).
- `Global` — per-install, shared across saves. Mod settings, install-level
  caches.

No `World` scope yet — defer until a real use case appears.

### File extension

- Default: `.json` (Newtonsoft serialization).
- Power-user tier supports any extension via `GetFilePath(key, ext)`.

---

## Directionality Contract

`IConsistDirection` (under `IPhysicsState.Direction`) is the **only** legal
source of truth for direction, orientation, and signed speeds.

**Forbidden anywhere outside `IPhysicsState` implementation:**

- Reading `BaseLocomotive.FrontIsA` directly
- Computing "is forward" from `velocity` sign
- Multiplying anything by an orientation-correction factor

**Required pattern** for any code that needs direction:

```csharp
// From a feature mod / UI mod / api consumer:
float speed = physics.Direction.UnsignedSpeedMph(vehicleId);    // for display
Sign dir = physics.Direction.DirectionOfTravel(vehicleId);      // for logic
int orient = physics.Direction.OrientationRelativeToLead(loco); // for fan-out
```

If `IConsistDirection` doesn't expose what you need, **add a primitive** to
the interface — don't compute it yourself.

---

## Patch Policy

### Where patches live

| Patch type            | Lives in       | Reviewed against            |
|-----------------------|----------------|------------------------------|
| Observer / KVO mirror | api/host       | "exposes data, changes nothing" |
| Behavior modification | feature mod    | "specific feature, gated by mod presence" |
| Cross-cutting hooks   | api/host       | Must be opt-in by feature mods via service interfaces |

### Static handoff pattern

Patches are static (Harmony requirement). To reach injected services, use
the established pattern:

```csharp
// api/host/Patches/SomeFeaturePatchState.cs
internal static class SomeFeaturePatchState
{
    internal static IServiceX Service;  // set by composition root
}

// api/host/Patches/SomeFeaturePatch.cs
[HarmonyPatch(typeof(GameType), "Method")]
internal static class SomeFeaturePatch
{
    [HarmonyPostfix]
    private static void Postfix(GameType __instance)
    {
        var svc = SomeFeaturePatchState.Service;
        if (svc == null) return;       // composition root hasn't wired yet
        // ... use svc.X, svc.Y ...
    }
}

// host/Bootstrap/HostCompositionRoot.cs
SomeFeaturePatchState.Service = serviceX;

// host/Bootstrap/HostPatchState.cs (Reset on save unload)
SomeFeaturePatchState.Service = null;
```

### Authority gating in patches

Patches declare their class via attribute:

```csharp
[PatchClass(PatchClass.Simulation)]              // host-only — simulation patch
[HarmonyPatch(typeof(LocomotiveAirSystem), "UpdateAir")]
internal static class DpuAirPatch { ... }
```

Composition root only registers patches compatible with current authority.

---

## Service Ownership Inventory

| Service                   | Layer | Authority         | Owner mod                              |
|---------------------------|-------|-------------------|----------------------------------------|
| ILoggerFactory            | L1    | Both              | api host                               |
| IAuthority                | L1    | Both              | api host                               |
| IPersistenceService       | L1    | Host (writes)     | api host                               |
| IReplicatedStateRegistry  | L1    | Both              | api host                               |
| IRequestRouter            | L1    | Both (asymmetric) | api host                               |
| IPhysicsState             | L2    | Both              | api host                               |
| IConsistTopology          | L3    | Both              | api host (via IPhysicsState)           |
| IConsistDirection         | L3    | Both              | api host (via IPhysicsState)           |
| IKinematics, IAirState... | L3    | Both              | api host (via IPhysicsState)           |
| IControlRequest           | L3    | Host              | api host                               |
| IVehicleInspector         | L3    | Both              | api host                               |
| IEquipment                | L3    | Both              | api host                               |
| IWindowService            | L4    | Client (view)     | ca.jwsm.railroader.ui                  |
| IBottomBarService         | L4    | Client            | ca.jwsm.railroader.ui                  |
| IThemeService             | L4    | Client            | ca.jwsm.railroader.ui                  |
| IDpuService               | L5    | Both              | mods.dpu                               |
| IAeSmoothingController    | L5    | Host              | mods.ae                                |
| IWaypointNavigation       | L5    | Both              | mods.eta                               |
| (UI windows / parts)      | L5    | Client            | mods.ui                                |

This table is the **canonical answer** for "who owns what." Adding a new
service requires updating this table.

---

## Phase Plan

Build phases. Each phase produces working software. No phase requires
back-edits to a prior one.

| Phase | Mining targets (ILSPY) | Output |
|-------|------------------------|--------|
| **0. Bootstrap** | UMM lifecycle, Railloader status, Harmony patterns | Skeleton repos with composition root + service registry + ILoggerFactory + IAuthority |
| **1. Persistence + save lifecycle** | StateManager, save messages, mod data conventions | IPersistenceService with two-tier API + atomic write |
| **2. Physics — kinematics + topology** | Car, BaseLocomotive, EnumerateCoupled, FrontIsA | IConsistTopology, IConsistDirection, IKinematics |
| **3. Physics — air + power** | LocomotiveAirSystem, BrakeLine, VentedValve, AirConnection, ControlProperties | IAirState, IPowerState, IControlRequest |
| **4. Physics — track + mass** | Graph, Segment, LocationF, speed limits, mileposts | ITrackProfile, IMassModel |
| **5. Multiplayer primitives** | KVO sync, PlayerPropertiesManager, Messenger | IReplicatedStateRegistry, IRequestRouter — first migration uses these |
| **6. UI framework** | UIPanelBuilder, CarInspector (shape ref) | ca.jwsm.railroader.ui peer with windows/parts/theme |
| **7. First feature mod (DPU)** | MultipleUnitController, MU/CutOut interaction | mods.dpu — air patch + power propagation, consuming primitives |
| **8. AE primitives** | AutoEngineerPlanner, AutoEngineerPersistence | Read access to AE intent + interception via IControlRequest |
| **9. AE smoothing mod** | (consumes phases 1-4, 8) | mods.ae — smoothing controller with anticipatory braking + power |
| **10+** | (consumes whatever) | ETA, Equipment window, Inspector window, NPC, Dispatch, etc. |

Each phase **stops, ships, and is reviewed** before the next starts. No
"phase 6 partly done while we start phase 7."

---

## Naming Conventions

### Repos

```
ca.jwsm.railroader.api          — L1-L3 foundation + primitives + composition root
ca.jwsm.railroader.ui           — L4 UI framework (peer of api)
ca.jwsm.railroader.mods\<mod>   — L5 feature/UI consumer mods, one folder each
```

### Namespaces

```
Ca.Jwsm.Railroader.Api.Host.*               — host implementation
Ca.Jwsm.Railroader.Api.<Domain>.Contracts   — interfaces
Ca.Jwsm.Railroader.Api.<Domain>.Models      — value types / snapshots
Ca.Jwsm.Railroader.Ui.Abstractions.*        — UI framework contracts
Ca.Jwsm.Railroader.Ui.Runtime.*             — UI framework impl
Ca.Jwsm.Railroader.Mods.<Mod>.*             — feature mods
```

### Logging scope names

Hierarchical, dotted, lower-kebab-case:

```
api.host.<service-name>          (e.g. api.host.consist-topology)
api.host.patches.<patch-name>    (e.g. api.host.patches.dpu-air)
mods.<mod>.<feature>             (e.g. mods.dpu.power-propagation)
ui.<framework-component>         (e.g. ui.windows, ui.theme)
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
logger.Coalesce("lead-fallback", message);    // emitted under api.host.consist-topology scope
// → grep '\[api.host.consist-topology\]' for that scope; coalescer key inside
```

---

## Build & Deploy

Same patterns as v0 (these worked):

- `dotnet build` per project; `Directory.Build.props` for shared MSBuild.
- `GameDir` env var or fallback path resolves game assemblies.
- Single PowerShell deploy script that builds + copies to game `Mods/` dir.
- Per-mod include flags (`-IncludeUi`, `-IncludeUiManager`, etc.).

Document deploy script as part of phase 0 setup; mods plug into it as added.

---

## Versioning

- API host has a SemVer version (`ApiVersion(major, minor, patch)`).
- Consumer mods declare their **min required api version** in `info.json`.
- Composition root rejects mods with incompatible api version at startup.
- Within a major version, contracts are additive (can add interfaces /
  methods, can't remove or change signatures).
- Cross-major changes require explicit migration path documented per breaking
  change.

---

## Open Questions

These deferred decisions need an answer before the relevant phase starts:

- [ ] **Snapshot refresh cadence**: every Update, every FixedUpdate, on
  demand only? Likely FixedUpdate for physics state, Update for view state.
  Decide in phase 2.
- [ ] **Replicated state transport**: KVO for everything, or do we need our
  own RPC channel for non-KVO data? Decide in phase 5.
- [ ] **In-game log viewer**: phase or feature? Probably a phase 10+
  feature mod (`mods.devconsole`).
- [ ] **AE smoothing UX**: notch sensitivity, jerk profile, reaction-time
  knob — what's exposed in mod settings? Decide in phase 9.
- [ ] **Save migration**: legacy v0 mod data — readable from v1, ignore
  silently, or refuse to load? Probably ignore silently with warn.

---

## Glossary

- **api / api host**: `ca.jwsm.railroader.api`. The foundation repo with
  L1-L3 primitives + composition root + observer patches.
- **ui / ui framework**: `ca.jwsm.railroader.ui`. Peer of api; windowing /
  theming / part registry.
- **mod / feature mod / consumer mod**: `ca.jwsm.railroader.mods\<name>`.
  Anything in L5 — features and UI consumers alike.
- **primitive**: A typed surface in L1-L3 that exposes either game state or
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
