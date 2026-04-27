# Signals & Dispatch — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Track Topology](track-topology.md)

The signal system is a CTC/ABS hybrid built on two abstractions: a `SignalStorage` host-authoritative `KeyValueObject` that keeps every aspect / direction / occupancy / button as a flat key, and a per-signal MonoBehaviour subclass (`CTCAutoSignal` or `CTCPredicateSignal`) that observes its inputs and recomputes its own aspect on the host. Blocks (`CTCBlock`) own `TrackSpan`s and run a 1-Hz host coroutine that polls car occupancy. Interlockings (`CTCInterlocking`) bundle switches and routes; the player codes them via the panel UI by setting knobs and pressing the code button. **There is no train order / track warrant / DTC system** — vanilla "dispatch" is exactly the panel-CTC interaction (player-as-dispatcher locally, host-as-dispatcher in MP) plus AutoEngineer reading aspects and braking accordingly. Aspects are a closed enum of six values (`Stop`, `Approach`, `Clear`, `DivergingApproach`, `DivergingClear`, `Restricting`); modes are `ABS` and `CTC`.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Track.Signals.SignalStorage` | `Track.Signals/SignalStorage.cs:11` | The KVO surface for every signal/block/switch/direction. Wraps a `KeyValueObject` |
| `Track.Signals.CTCSignal` (abstract) | `Track.Signals/CTCSignal.cs:10` | Base class; observes inputs, calls `CalculateAspect`, writes via `SetSignalAspect` |
| `Track.Signals.CTCAutoSignal` | `Track.Signals/CTCAutoSignal.cs:12` | Standard interlocking/intermediate signal; aspect from blocks + next signal |
| `Track.Signals.CTCPredicateSignal` | `Track.Signals/CTCPredicateSignal.cs:8` | Hand-authored per-head predicates (switch/block/interlocking checks) |
| `Track.Signals.CTCBlock` | `Track.Signals/CTCBlock.cs:12` | A track block; owns spans, polls occupancy, exposes `TrafficFilter` |
| `Track.Signals.CTCInterlocking` | `Track.Signals/CTCInterlocking.cs:13` | Switch sets + routes + outlets; `Code()` = the dispatcher action |
| `Track.Signals.CTCIntermediate` | `Track.Signals/CTCIntermediate.cs:9` | Sequence of blocks+signals between two interlockings (for ABS-style inline sigs) |
| `Track.Signals.CTCPanelController` | `Track.Signals/CTCPanelController.cs:15` | Panel UI; routes button presses to `Code()`; mode toggle |
| `Track.Signals.CTCSwitchMonitor` | `Track.Signals/CTCSwitchMonitor.cs:14` | Mirrors `TrackNode.isThrown` to KVO; flags switches as CTC-controlled |
| `Track.Signals.SignalAspect` | `Track.Signals/SignalAspect.cs:6` | Enum: Stop, Approach, Clear, DivergingApproach, DivergingClear, Restricting |
| `Track.Signals.CTCKeys` | `Track.Signals/CTCKeys.cs:3` | All KVO key string builders (`block:<id>:occupancy`, `signal:<id>:aspect`, …) |
| `Model.AI.AutoEngineerPlanner.UpdateTargets` | `Model.AI/AutoEngineerPlanner.cs:451` | The AI's per-tick consumption of upcoming signal aspects |

---

## SystemMode: ABS vs CTC

Two modes (`Track.Signals/SystemMode.cs`):
- **`ABS`** — Automatic Block Signal. Block-occupancy-driven only. No traffic direction. No interlocking codes route directions; switches still throwable manually but interlocking routes don't propagate. UI knobs that set traffic direction are inoperative.
- **`CTC`** — Centralized Traffic Control. Traffic direction matters; interlockings code routes via outlets that propagate `CTCTrafficFilter` outward through blocks; switches in interlockings get locked.

Mode is stored at `signalStorage["mode"]` (int, 0=ABS, 1=CTC) and observed by every signal, block, and panel controller. The `mode` knob on the master panel writes through `CTCPanelController.OnEnableWithProperties` (`CTCPanelController.cs:101-109`) which subscribes the knob's KVO key (`CTCKeys.Knob("mode") = "knob:mode:position"`) to the storage's `"mode"`.

`CTCPanelController.OnModeDidChange` (`CTCPanelController.cs:123`):
- On `ABS → CTC` or `CTC → ABS` transitions (host only):
  - `ClearAllBlocks()` — sets every `block:<id>:occupancy` to false. Will be re-set within ≤1s by `CTCBlock.UpdateCoroutine`.
  - On `→ ABS`: `ClearAllRoutes()` — clears every block traffic filter to `None` and every interlocking direction to `None`.
  - On `→ CTC`: starts observing all interlocking blocks for the "ctc-bell" audio cue on occupancy.
- All peers: refresh panel group visuals.

---

## Spine: how an aspect gets to a train

```
TrackNode.isThrown      ← player throws switch (or CTC codes it)
   │
   ├─→ SwitchThrownDidChange Messenger
   │
   └─→ CTCSwitchMonitor (host-only):
            UpdateSwitchPositionProperty → SignalStorage.SetSwitchPosition(nodeId, setting)
            block.MarkSwitchNodeThrown(nodeId, isThrown)  (or MarkSwitchNodeUnlocked for CTC)

CTCBlock (host) — UpdateCoroutine 1Hz tick (CTCBlock.cs:104):
   PerformUpdate():
     occupied = _testForceOccupied
              | (thrownSwitchesSetOccupied && _thrownNodeIds.Count > 0)
              | (CTC && _unlockedNodeIds.Count > 0)
              | CheckOccupied()                           ← TrainController.AnyCarsOnSpan(span) for each span
     if occupied changed → SignalStorage.SetBlockOccupied(id, occupied)

SignalStorage writes "block:<id>:occupancy"
   │
   └─→ ObserveBlockOccupancy fires on every peer

CTCSignal (subclass: CTCAutoSignal or CTCPredicateSignal):
   On any observed input change → SetNeedsUpdate()
      → coroutine PerformUpdate (one-frame coalesce)
      → CalculateAspect() → SignalAspect
      → ShowAspect() → SignalStorage.SetSignalAspect(id, aspect)

SignalStorage writes "signal:<id>:aspect"
   │
   ├─→ CTCSignalModelController.DisplayAspect (visual lights/semaphores)
   │
   └─→ AutoEngineerPlanner.UpdateTargets reads CTCSignal.LastShownAspect
       (which mirrors the local KVO read)
```

**Every input → output computation happens on the host.** Clients only consume `SignalStorage` KVO updates as they arrive. The `StateManager.IsHost` early-return guards in `CTCSignal.SetNeedsUpdate`, `CTCBlock.OnEnable`, `CTCInterlocking.OnEnable` etc. enforce this.

---

## `Track.Signals.SignalStorage`

The single host KVO that holds *all* signal-system runtime state. Wraps a `KeyValueObject` component and registers it via the `[RequireComponent(typeof(KeyValueObject))]` + a sibling `GlobalKeyValueObject` (which calls `StateManager.RegisterPropertyObject(globalObjectId, kvo, authReq)`).

**Auth model:** all writes flow through `StateManager.AssertIsHost()` *inside the storage methods* (`SetBlockOccupied`, `SetSignalAspect`, `SetSwitchPosition`, etc.) — so the storage itself enforces host-only writes regardless of how its `GlobalKeyValueObject` is configured. Reads are unauthenticated.

### Key API

```csharp
SystemMode SystemMode { get; set; }                                          // "mode" key

// Blocks
bool       GetBlockOccupied(string blockId);                                 // "block:<id>:occupancy"
void       SetBlockOccupied(string blockId, bool value);                     // HostOnly
IDisposable ObserveBlockOccupancy(string blockId, Action<bool>);
CTCTrafficFilter GetBlockTrafficFilter(string blockId);                      // "block:<id>:direction"
void       SetBlockTrafficFilter(string blockId, CTCTrafficFilter);          // (no AssertIsHost — host-only by convention)
IDisposable ObserveBlockTrafficFilter(string blockId, Action<CTCTrafficFilter>);

// Signals
SignalAspect GetSignalAspect(string signalId);                                // "signal:<id>:aspect"
void         SetSignalAspect(string signalId, SignalAspect aspect);           // HostOnly
IDisposable  ObserveSignalAspect(string signalId, Action<SignalAspect>);

// Switches (signal-system mirror, separate from TrackNode.isThrown)
void         SetSwitchPosition(string switchNodeId, SwitchSetting setting);   // HostOnly. "switch:<nodeId>:position"
IDisposable  ObserveSwitchPosition(string switchNodeId, Action<SwitchSetting>);

// Interlocking direction
SignalDirection GetInterlockingDirection(string interlockingId);              // "il:<id>:direction"
void            SetInterlockingDirection(string interlockingId, SignalDirection); // (no AssertIsHost)
IDisposable     ObserveInterlockingDirection(string interlockingId, Action<SignalDirection>);

// Panel buttons
void       SetButton(string buttonId, bool value);                           // "button:<id>:active"
IDisposable ObserveButton(string buttonId, Action<bool>);

// CTC switch unlocks (array)
void       SetSwitchIdUnlocked(string nodeId, bool unlocked);                // "unlockedSwitchIds" (Array<string>)
IDisposable ObserveUnlockedSwitchIds(Action<string[]>);
```

### Key naming (`Track.Signals/CTCKeys.cs`)

| Generator | Format | Example |
|---|---|---|
| `Knob(knobId)` | `knob:<id>:position` | `knob:mode:position` |
| `BlockOccupancy(blockId)` | `block:<id>:occupancy` | `block:M1:occupancy` |
| `BlockTrafficFilter(blockId)` | `block:<id>:direction` | `block:M1:direction` |
| `SignalAspect(signalId)` | `signal:<id>:aspect` | `signal:S101:aspect` |
| `SwitchPosition(nodeId)` | `switch:<id>:position` | `switch:N42:position` |
| `Button(buttonId)` | `button:<id>:active` | `button:CB-1:active` |
| `InterlockingDirection(ilId)` | `il:<id>:direction` | `il:IL5:direction` |
| `unlockedSwitchIds` (literal) | array of strings | — |

### Patch candidates

| Method | Why patch |
|---|---|
| `SignalStorage.SetSignalAspect` | Single chokepoint for every aspect change. Postfix to log/emit Messenger event for mods. **Note:** `AssertIsHost` runs first; patching the prefix to bypass would break MP. |
| `SignalStorage.SetBlockOccupied` | Same pattern for occupancy. |
| `SignalStorage.SetBlockTrafficFilter` / `SetInterlockingDirection` | These do NOT call `AssertIsHost` — currently host-only by convention. If patching from a mod, add the assert back. |
| `SignalStorage.GetSignalAspect` etc. | Read-side overrides for fully-mocked signal logic. |

### Gotchas

- **Block `direction` and interlocking `direction` setters lack `AssertIsHost`** — the only thing keeping clients from writing is convention (and the global KVO's `MinimumLevelCrew` falling back to a Crew check that everyone passes). Mods that need to be sure they're not racing should add their own host check.
- **`SetSwitchIdUnlocked` rebuilds the entire array every call** (`SignalStorage.cs:160-176`). For a yard with many CTC switches, batch your unlocks.
- The KVO observers fire on every peer, but the *initial* call (`callInitial: true` default) only delivers the current value. Code that relies on history (e.g., "did we just go from clear to occupied?") must remember the previous value itself.

---

## Block model: `CTCBlock`

A block is a `MonoBehaviour` parented under a `CTCInterlocking` or `CTCIntermediate`. It owns one or more `TrackSpan`s as children (auto-discovered via `GetComponentsInChildren<TrackSpan>()`, `CTCBlock.cs:74`).

```csharp
public string id;                                             // 14
public bool   thrownSwitchesSetOccupied = true;               // 16
private bool _testForceOccupied;                              // 28
public bool   IsOccupied { get; }                              // 32 — reads SignalStorage
public CTCIntermediate Intermediate { get; }                  // 44
public CTCInterlocking Interlocking { get; }                  // 46
public CTCTrafficFilter TrafficFilter { get; set; }           // 50
private TrackSpan[] Spans { get; }                            // 74 — cached children
```

### Occupancy detection — `PerformUpdate` (`CTCBlock.cs:123`)

Runs every 1 second on the host (random 0.1–0.5s initial offset to spread load across blocks). Sets occupied if:

1. `_testForceOccupied` (testing only).
2. `thrownSwitchesSetOccupied && _thrownNodeIds.Count > 0` — any switch within this block has been thrown ("thrown switches set occupied" defensive policy).
3. CTC mode AND `_unlockedNodeIds.Count > 0` — any CTC switch within this block has been unlocked (treat unlocked = occupied to enforce safe lockup).
4. `CheckOccupied()` — `TrainController.Shared.AnyCarsOnSpan(span)` for each owned span.

Writes only when state changes (`if (IsOccupied != flag2)` `CTCBlock.cs:151`).

### `TrafficFilter` (CTC mode only)

Block-level direction setting: `None`, `Right`, `Left`, or `Any`. Set by `CTCInterlocking.SetTrafficFilterFrom` when coding a route. Used by `CTCAutoSignal.DirectionMatches` and `CTCBlock.CanSetDirection`.

```csharp
public bool CanSetDirection(CTCDirection propagateDirection, CTCTrafficFilter newTrafficFilter)
```

A block's filter can be overwritten when:
- Current is `Left` and propagation is `Left` (continuing same direction).
- Current is `Right` and propagation is `Right`.
- Current is `None` or `Any` (resetting or interlocking-internal).

Otherwise rejected — this is how opposing-direction route attempts fail.

### Switch monitoring hooks

```csharp
void MarkSwitchNodeThrown(string nodeId, bool isThrown);                 // 261 — called by CTCSwitchMonitor
void MarkSwitchNodeUnlocked(string nodeId, bool unlocked);              // 269
bool DependsUponSwitchPosition(TrackNode switchNode);                    // 277 — geometric check
```

`DependsUponSwitchPosition` checks whether the switch's world position lies within any of the block's spans (with 1m radius). This is how `CTCSwitchMonitor.ObserveSwitches` figures out which block to attribute a switch to.

### `CarsInBlock()` and span queries

`CarsInBlock()` returns a `HashSet<Car>` by union'ing `TrainController.CarsOnSpan(span)` over all owned spans.

### Patch candidates

| Method | Why patch |
|---|---|
| `CTCBlock.CheckOccupied` | Custom occupancy detection (e.g., include hand-cars not tracked by `TrainController`). |
| `CTCBlock.PerformUpdate` | Modify the per-tick policy (e.g., disable thrown-switch-sets-occupied default). |
| `CTCBlock.CanSetDirection` | Allow opposing-direction route setting (rough handling). |
| `CTCBlock.MarkSwitchNodeThrown` / `MarkSwitchNodeUnlocked` | Inject custom side-effects on switch state changes within blocks. |
| `CTCBlock.thrownSwitchesSetOccupied` (public field) | Disable the safety policy per block in inspector or via Harmony. |

### Gotchas

- **The 1Hz update means occupancy can lag a moving train by up to 1 second.** Combined with the random 0.1-0.5s initial stagger, a fast train entering a block sees `IsOccupied = false` for ~1s after entry. The thrown-switches-set-occupied default is a conservative compensator.
- `CTCBlock.UpdateCoroutine` only runs on the host (`CTCBlock.cs:84`). Clients see only KVO updates. If a mod runs a custom signal logic on a client, it must pull `IsOccupied` via the KVO observer, NOT `CheckOccupied`.
- `Intermediate` and `Interlocking` are mutually exclusive — a block has at most one. `IsCTC => Storage.SystemMode == SystemMode.CTC` (`CTCBlock.cs:48`) is a per-block helper that walks up to storage.
- **`Spans` is cached lazily but never invalidated.** Adding/removing `TrackSpan` children at runtime won't be picked up until the block reloads.

---

## Signal aspects & heads

```csharp
public enum SignalAspect {
    Stop,                  // 0  – red
    Approach,              // 1  – yellow (block ahead may be Stop)
    Clear,                 // 2  – green
    DivergingApproach,     // 3
    DivergingClear,        // 4
    Restricting,           // 5  – ABS-style "proceed at restricted speed"
}
```

Maps to physical heads via `SignalAspectForHeads(head0, head1, head2)` (`CTCSignal.cs:176`):

| head0 | head1 | head2 | Result |
|---|---|---|---|
| Green | * | * | Clear |
| Yellow | * | * | Approach |
| Red | Green | * | DivergingClear |
| Red | Yellow | * | DivergingApproach |
| Red | Red | non-Red | Restricting |
| Red | Red | Red | Stop |

`SemaphoreHeadController.Aspect` is `Red`/`Yellow`/`Green` — the per-head primitive.

### `SignalHeadConfiguration`

```csharp
public enum SignalHeadConfiguration { Single, Double, Triple }
public static int IntHeadCount(this SignalHeadConfiguration config);
```

Set per-signal in inspector. Drives `CTCSignalModelController.Configure(headCount)` and constrains `interlockingRouteMapping.Count` (one per head).

### `CTCSignalModelController`

Per-signal MonoBehaviour that drives the visual semaphores/lights. Receives `DisplayAspect(aspect, signalId)` callback when the signal's KVO changes (`CTCSignal.cs:80-83`). Pure visual; no logic. Mods replacing visuals can subclass or sub in.

---

## `CTCSignal` (abstract base)

```csharp
public string id;                                       // 20
public SignalHeadConfiguration headConfiguration;       // 22
public CTCDirection direction;                          // 27 — left or right; the heading this signal protects
public CTCSignalModelController modelController;        // 29
internal SignalAspect LastShownAspect { get; private set; }   // 36 — host's last computed aspect (mirrored for read)
public CTCInterlocking Interlocking { get; protected set; }   // 42
public CTCIntermediate Intermediate { get; protected set; }   // 40
public bool IsIntermediate => Interlocking == null;     // 44
public SignalAspect CurrentAspect => Storage.GetSignalAspect(id);
```

`Awake` finds `Interlocking` and `Intermediate` via `GetComponentInParent`. **A signal must be parented under one or the other.** A signal directly under storage with neither parent ends up `head = SemaphoreHeadController.Aspect.Yellow` (a permissive fallback in `CTCAutoSignal._CalculateAspect:165`).

### Update plumbing

```csharp
protected void SetNeedsUpdate();      // schedules one-frame coalesce
protected abstract SignalAspect CalculateAspect(out StopReason stopReason);
private   IEnumerator PerformUpdate();   // yield null then ShowAspect(CalculateAspect())
private   void  ShowAspect(SignalAspect);  // writes Storage and updates LastShownAspect

protected void UpdateOnChange<T>(Func<string, Action<T>, IDisposable> observeAction, string itemId)
```

Subclasses register their input observers in `OnEnable`. Each observer calls `SetNeedsUpdate()` when the input changes; the next-frame coalesce keeps recompute O(1) per frame regardless of input flutter.

### Patch candidates (base)

| Method | Why patch |
|---|---|
| `CTCSignal.ShowAspect` | The single write chokepoint for any signal. Postfix to broadcast custom Messenger events. |
| `CTCSignal.PerformUpdate` | Replace the coalescing or add post-aspect side-effects. |
| `CTCSignal.SignalAspectForHeads` (static) | Replace the head→aspect mapping. Affects every CTCSignal subclass. |

---

## `CTCAutoSignal`

The standard signal class. Drives aspect from a list of "blocks I'm protecting" plus an optional next-signal lookup (via `Interlocking` route mapping or `Intermediate` traversal).

```csharp
public List<CTCBlock> blocks;                       // 14 — blocks immediately past this signal
public List<int> interlockingRouteMapping;          // 17 — one entry per head, indices into Interlocking.routes
```

### `OnEnable` observer wiring (`CTCAutoSignal.cs:20-74`)

For interlocking signals, observers are registered for:
- The interlocking's `Direction`.
- Every `SwitchSetting` of every switch in every `SwitchSet`.
- For each `interlockingRouteMapping[i]`: blocks AND the next signal beyond, via `Interlocking.BlockAndNexSignal(routeIndex, direction)`.

For intermediate signals (no interlocking parent):
- `NextSignal(this, Left)` and `NextSignal(this, Right)` — both directions, since intermediates can be on bi-directional track.

Plus always: every `block` in the `blocks` list — its occupancy and traffic filter.

### `CalculateAspect` (`CTCAutoSignal.cs:102-168`)

```
1. For each protected block:
     if traffic_filter conflicts with my direction → Stop (OpposingDirection)
     if occupied                                     → Stop (Occupied)
2. If Interlocking-controlled:
     Use interlockingRouteMapping[0] for head, [1] for head2
     Each head = AspectForBlockAndNextSignal(blocks, nextSignal, lined)
3. Else if Intermediate:
     If next interlocking signal is "against" → Red
     Else head = AspectForBlockAndNextSignal(null, NextSignal(this, direction), true)
4. Else (orphan): head = Yellow (permissive fallback)
5. Return SignalAspectForHeads(head, head2, Red)
```

### `AspectForBlockAndNextSignal` (the cascade)

```csharp
private SemaphoreHeadController.Aspect AspectForBlockAndNextSignal(IReadOnlyCollection<CTCBlock> nextBlocks, CTCSignal nextSignal, bool lined)
{
    if (!lined)                                              return Red;       // route not lined
    if (nextBlocks?.Any(b => b.IsOccupied))                  return Red;       // first block occupied
    if (nextSignal == null || !nextSignal.isActiveAndEnabled) return Yellow;   // no next → caution
    return AspectDisplayedBySignal(nextSignal) switch {
        Stop                => Yellow,                       // approach a Stop → Yellow
        Approach            => Green,                        // approach Yellow → Green (cleared two ahead)
        Clear               => Green,
        DivergingApproach   => Yellow,
        DivergingClear      => Yellow,
        Restricting         => Yellow,
        _ => throw …
    };
}
```

This is the standard 3-aspect cascade: Stop ahead = Yellow here, Yellow ahead = Green here, Green ahead = Green here. Diverging aspects are conservatively treated as Yellow (you're heading toward a switch).

### Direction matching

```csharp
private static bool DirectionMatches(CTCDirection signalDirection, CTCTrafficFilter trafficFilter)
{
    return trafficFilter switch {
        None  => false,                          // no traffic allowed
        Right => signalDirection == CTCDirection.Right,
        Left  => signalDirection == CTCDirection.Left,
        Any   => true,                           // ABS mode default
    };
}
```

In ABS mode, `TrafficFilter` defaults to `Any` for every block, so direction never blocks the aspect. In CTC, `Any` is essentially never set (Interlocking.Code sets `Left` or `Right`); blocks revert to `None` after train passes (`CTCIntermediate.BlockBecameUnoccupied`, see below).

### Patch candidates

| Method | Why patch |
|---|---|
| `CTCAutoSignal._CalculateAspect` | Full aspect logic. Replace for completely custom signal rules. |
| `CTCAutoSignal.AspectForBlockAndNextSignal` | The cascade table. Modify for 4-aspect (Limited, Medium) signaling. |
| `CTCAutoSignal.DirectionMatches` | Custom traffic-direction matching. |

---

## `CTCPredicateSignal`

Hand-authored alternative to `CTCAutoSignal`. Each head has a list of `Predicate`s (all must pass for the head to attempt to display). Predicate types:

```csharp
public enum PredicateType {
    Switch,                            // node has setting X
    Block,                             // every block in list is unoccupied
    InterlockingTrafficDirection,      // interlocking direction is X (CTC only; ABS ignored)
    InterlockingTrafficDirectionIsNot, // interlocking direction is not X (with optional switch override)
    AlwaysFalse,                       // dead head
}
```

Per head: `predicates` (List<Predicate>) AND `nextSignal` (cascade to compute the actual color).

`CalculateAspect` (`CTCPredicateSignal.cs:105-134`):
```
For each head i (0..count-1):
   if (all predicates satisfied):
      if next signal == null or disabled: → Yellow
      else if next signal aspect == Stop:  → Yellow
      else                                  → Green
   else: Red
Return SignalAspectForHeads(head0, head1, head2)
```

Coarser than `CTCAutoSignal`'s cascade — only Yellow or Green for "satisfied," not full 3-aspect logic. Useful for static junction signals where the route geometry is the only variable.

### Patch candidates

| Method | Why patch |
|---|---|
| `CTCPredicateSignal.IsSatisfied` (Predicate overload) | Add new predicate types. |
| `CTCPredicateSignal.CalculateAspect` | Override the head→aspect logic. |

---

## `CTCInterlocking`

Bundles a set of switches with named routes that map to outlets. The unit of "the dispatcher chose route N and direction Left."

```csharp
public string id;                                  // 64
public string displayName;                         // 66
public List<SwitchSet>  switchSets;                // 68
public List<Outlet>     outlets;                   // 70
public List<Route>      routes;                    // 72
private SignalStorage   _storage;                  // 76

public IReadOnlyList<CTCBlock> Blocks { get; }     // 84 — childen
public SignalDirection Direction { get; set; }     // 96 — proxy to storage
```

### Inner types

```csharp
[Serializable] public struct SwitchSet     { public List<TrackNode> switchNodes; }
[Serializable] public struct Route         { public List<SwitchFilter> switchFilters; public int outletLeft; public int outletRight; }
[Serializable] public struct Outlet        { public CTCDirection direction; public List<CTCBlock> blocks; public CTCSignal nextSignal; }

public enum CodeFailureReason { None, BlockOccupied, NoRoute, RouteSetAgainst }
```

A `SwitchSet` is a *group of switches that move together* — the operator codes one filter per switch set, not per switch (e.g., a crossover has two switches that always throw together).

A `Route` defines, for a given combination of switch settings, which outlet you reach when going Left vs. Right.

An `Outlet` is "the next block(s) and the next signal beyond this interlocking, in this direction."

### `Code()` — the dispatcher action

```csharp
public bool Code(SignalDirection direction, List<(TrackNode, SwitchSetting)> switchSettings, out CodeFailureReason reason)  // 175
```

Sequence (host only):
1. **Reset** — `CodeDirection(None)`. Clears the current direction (clears traffic filters on outlet).
2. **Throw switches** — `CodeSwitchChanges(switchSettings, …)`:
   - Reject if any internal block is occupied (`reason = BlockOccupied`).
   - Reject if any switch has a car on it (`reason = BlockOccupied`).
   - For each switch: if current `isThrown != desired`, send `SetSwitch` message via `StateManager.ApplyLocal(new SetSwitch(nodeId, thrown, Now, "CTC"))`.
3. **Set direction** — `CodeDirection(direction)`:
   - Find `RouteForCurrentSwitchSettings()` — first route in `routes` whose `switchFilters` are satisfied by current node positions.
   - Get outlet for direction.
   - `SetTrafficFilterFrom(blocks, propagateDirection, trafficFilter, isResetting=false, …)`:
     - Check that the next interlocking's traffic isn't against us (`IsNextInterlockingTrafficAgainst`).
     - For each outlet block, dry-run `block.TrySetDirection(propagateDirection, trafficFilter, dryRun:true)` — bail if any rejects.
     - Then real-set.
   - Set every internal block to the new direction filter.
   - Write `Direction = direction` (KVO).

Failure modes (return `false` with reason):
- `NoRoute` — current switch settings don't satisfy any defined route.
- `BlockOccupied` — internal block has cars or a car is on a switch we'd throw.
- `RouteSetAgainst` — outlet block traffic filter conflicts with the propagating direction.

### `IsLined` (`CTCInterlocking.cs:420`)

```csharp
private bool IsLined(Route route)
```

Tests whether the current node positions match the route's `switchFilters` (Normal / Reversed / None=any). True iff every non-None filter matches its switch set. **Note:** `route.switchFilters.Count` may be less than `switchSets.Count`; missing entries default to `None` (`CTCInterlocking.cs:426`).

### `BlockBecameUnoccupied` (Intermediate's helper for direction cleanup)

Defined on `CTCIntermediate`, called by storage observers. When a block becomes unoccupied, scans neighbors in both directions and clears their `TrafficFilter` to `None` if they had `Left` or `Right` set (i.e., revoke the route after the train has passed).

Currently invoked indirectly via `CTCInterlocking.UpdateObservedRouteBlocks` watching the outlet block occupancy and re-coding `None` direction (`CTCInterlocking.cs:127-136`):

```csharp
private void ObserveRouteBlock(CTCBlock block) {
    _routeBlockObservers.Add(_storage.ObserveBlockOccupancy(block.id, occupied => {
        if (occupied) CodeDirection(SignalDirection.None, out _);
    }));
}
```

Wait — this re-codes to `None` *when a block becomes occupied*, not unoccupied. **The intent is that as soon as a train enters a block past the interlocking, the interlocking's outbound direction is cleared** (so the dispatcher can reuse it). The block-becomes-unoccupied cleanup is in `CTCIntermediate.BlockBecameUnoccupied` (currently it appears only `CTCIntermediate` has this, and it isn't wired to anything in the decompile we have — likely intermediate signals' route-clear logic).

### Patch candidates

| Method | Why patch |
|---|---|
| `CTCInterlocking.Code` | The dispatcher entry point. Patch to reject codes by mod policy (e.g., schedule conflicts). |
| `CTCInterlocking.CodeSwitchChanges` | Per-switch throw orchestration. |
| `CTCInterlocking.SetTrafficFilterFrom` | Direction propagation logic — modify to add multi-block reservation. |
| `CTCInterlocking.RouteForCurrentSwitchSettings` | Current-settings → route mapping; patch to add fuzzy matching. |
| `CTCInterlocking.IsLined` | Switch-filter matching predicate. |

---

## `CTCIntermediate`

A linear chain of blocks and intermediate signals between two interlockings. Used to model multi-block ABS-style territory.

```csharp
public List<CTCBlock>  blocks;                  // 12 — left to right
public List<CTCSignal> signals;                 // 15 — left to right, count = blocks.Count - 1
public CTCSignal nextSignalLeft;                // 17 — beyond the leftmost block
public CTCSignal nextSignalRight;               // 19
```

### Methods

```csharp
CTCBlock  GetAdjacentTo(CTCBlock block, CTCDirection direction);     // 21
CTCSignal NextSignal(CTCSignal from, CTCDirection direction);        // 42
CTCSignal NextExternalSignalForDirection(CTCDirection);              // 60
bool      IsNextInterlockingSignalAgainst(CTCSignal from, IDiagnosticCollector); // 70
CTCBlock  BlockAtEnd(CTCDirection direction);                        // 101
void      BlockBecameUnoccupied(CTCBlock block);                     // 111 — see above
```

`NextSignal(from, direction)` walks the `signals` list in the direction's increment (Right=+1, Left=-1), returns the first signal in that direction with `signal.direction == direction`. If it falls off the end, returns the external signal beyond. This is how `CTCAutoSignal` chains its cascade.

`IsNextInterlockingSignalAgainst` is the plumbing that prevents an intermediate signal from showing Green if the next interlocking has its direction set against you.

---

## Switch monitoring: `CTCSwitchMonitor`

A `GameBehaviour` (`Track.Signals/CTCSwitchMonitor.cs:14`) that runs once per signal storage tree. Its job is to glue the topology layer (`TrackNode.isThrown`) to the signal layer (`SignalStorage` switch keys + `CTCBlock.MarkSwitchNode*`).

### `ObserveSwitches` (`CTCSwitchMonitor.cs:92`, host only)

For every switch node in the graph (`graph.Nodes.Where(n => graph.IsSwitch(n))`):
1. Find which non-interlocking blocks contain it (via `CTCBlock.DependsUponSwitchPosition`).
2. If exactly one block, set:
   ```csharp
   node.OnDidChangeThrown = () => {
       UpdateSwitchPositionProperty(node);   // mirror to storage["switch:<id>:position"]
       block.MarkSwitchNodeThrown(node.id, node.isThrown);   // bump occupancy
   };
   ```
3. If no block contains it but interlockings depend on it:
   ```csharp
   node.OnDidChangeThrown = () => {
       UpdateSwitchPositionProperty(node);
       foreach (block in dependentInterlockingBlocks)
           block.MarkSwitchNodeUnlocked(node.id, node.IsCTCSwitchUnlocked);
   };
   ```
4. Otherwise clear the handler.

### `UpdateSwitchesForCTC` (`CTCSwitchMonitor.cs:177`)

In CTC mode: every switch that participates in any active interlocking gets `IsCTCSwitch = true` (and `Graph.OnNodeDidChange(node)` fires to invalidate caches). All other switches → `IsCTCSwitch = false`.

In ABS mode: all switches → `IsCTCSwitch = false` (regardless of interlocking membership).

This is what makes `IsCTCSwitch` a *runtime* property: it depends on the current `SystemMode` and which interlockings are active.

### `MapFeatureChangedGraph` / `CTCFeatureChange` reactions

`OnEnableWithProperties` (`CTCSwitchMonitor.cs:48`) registers Messenger handlers for `MapFeatureChangedGraph` and `CTCFeatureChange` to re-run `UpdateSwitchesForCTC` and `ObserveSwitches`. So if a `MapFeature` unlock enables a new interlocking GameObject, switch-CTC status updates within ≤1 frame.

### Patch candidates

| Method | Why patch |
|---|---|
| `CTCSwitchMonitor.ObserveSwitches` | Customize which blocks "own" which switches. |
| `CTCSwitchMonitor.UpdateSwitchesForCTC` | Modify CTC scope (e.g., always-CTC mode). |
| `CTCSwitchMonitor.UpdateSwitchPositionProperty` | Per-throw side-effect injection. |

### Gotchas

- **The host overwrites `node.OnDidChangeThrown` directly** (`CTCSwitchMonitor.cs:113, 132`). Mods that set `OnDidChangeThrown` for their own purposes will be wiped on the next `ObserveSwitches` pass. Subscribe to `SwitchThrownDidChange` Messenger instead.
- `node.IsCTCSwitch` is a per-peer field set by `CTCSwitchMonitor`. Clients run their own monitor (it's a `GameBehaviour`, not host-only) — so the host-side cache and client-side cache should match if both peers see the same scene + `MapFeature` state.
- A switch in a block AND in an interlocking gets the block-handler (case 1 wins, case 2 skipped). This means it gets `MarkSwitchNodeThrown` (occupancy on throw), not `MarkSwitchNodeUnlocked`.

---

## Switch locking and unlocking

Per-switch CTC unlock state is stored in `SignalStorage["unlockedSwitchIds"]` as an array of node id strings. The setter:

```csharp
SignalStorage.SetSwitchIdUnlocked(string nodeId, bool unlocked)
```

Reads the current array, removes the id (always), then conditionally re-adds. **No `AssertIsHost`** in the storage method itself — but the only callers go through `RequestSetSwitchUnlocked → TrainController.HandleRequestSetSwitchUnlocked` (host-side) `→ CTCPanelController.SetSwitchUnlocked` `→ Storage.SetSwitchIdUnlocked`.

`CTCPanelController.HandleUnlockedSwitchIdsDidChange` (`CTCPanelController.cs:367`) observes the array and synchronizes `TrackNode.IsCTCSwitchUnlocked` for every switch in every interlocking's switch sets, firing `OnDidChangeThrown` when the unlock state changes (which causes the visual stand to update via `CTCDisplayThrown` in `TrackNode.isThrown.set`).

### `RequestSetSwitchUnlocked` flow

```
Player right-clicks CTC switch → SwitchStandClick.ShowContextMenu (SwitchStandClick.cs:61)
   → "Lock Switch" or "Unlock Switch" button
   → StateManager.ApplyLocal(new RequestSetSwitchUnlocked(nodeId, !IsCTCSwitchUnlocked))
        ↓
Host: TrainController.HandleRequestSetSwitchUnlocked  (TrainController.cs:1386)
   → reject if !node.IsCTCSwitch
   → CTCPanelController.Shared.SetSwitchUnlocked(nodeId, unlocked)
        → SignalStorage.SetSwitchIdUnlocked(nodeId, unlocked)   (KVO write, broadcast)
   → AuditManager.RecordSwitchAction(nodeId, "Lock"|"Unlock", playerUri)
        ↓
All peers: HandleUnlockedSwitchIdsDidChange (CTCPanelController.cs:367)
   → for each switch in interlocking switch sets:
       node.IsCTCSwitchUnlocked = (in unlocked set)
       fire OnDidChangeThrown if changed
        ↓
   CTCSwitchMonitor's handler (if registered for that node):
       block.MarkSwitchNodeUnlocked(nodeId, unlocked)
        ↓
   Block's PerformUpdate may now flag occupied (since CTC + unlocked switch sets occupied)
```

### Patch candidates

| Method | Why patch |
|---|---|
| `TrainController.HandleRequestSetSwitchUnlocked` | Add unlock-policy gating (e.g., role-based). |
| `CTCPanelController.SetSwitchUnlocked` | Direct host-side unlock; bypass request pipeline. |
| `CTCPanelController.HandleUnlockedSwitchIdsDidChange` | React to bulk unlock-state changes. |

---

## CTC switch coding

When the dispatcher operates a switch knob on the panel and presses Code, the flow is:

```
CTCPanelKnob.ControlOnValueChanged (Track.Signals/CTCPanelKnob.cs:226)
   → StateManager.ApplyLocal(new PropertyChange(objectId=storage, key=knob:<id>:position, IntPropertyValue))
        ↓
   storage["knob:<id>:position"] write — reaches every peer

CTCPanelButton observed: button:<id>:active (when player presses Code)
   → CTCPanelController.OnButtonPropertyChange (CTCPanelController.cs:203, host only)
   → CTCPanelController.Code(button) (CTCPanelController.cs:219)
        ├── PanelGroupsForInterlockingId(button.interlockingId)
        ├── switchSettings = (panel switch knob current settings) per switch
        └── switch SystemMode:
              CTC: Interlocking.Code(direction, switchSettings, out reason)
              ABS: Interlocking.CodeSwitchChanges(switchSettings, out reason)
```

`CTCPanelKnob` enforces position quantization (snaps to 0.0/0.5/1.0 for signals = Left/None/Right; 0.0/1.0 for switches and mode). The auth model: panel knob writes use `PropertyChange` (default `MinimumLevelCrew` for the storage's KVO), so any Crew-level player can set knobs. The button press then triggers host-only code execution.

### Patch candidates

| Method | Why patch |
|---|---|
| `CTCPanelController.Code` | Reject codes by mod policy (route reservation, schedules). |
| `CTCPanelController.OnButtonPropertyChange` | Pre-button hook (button is fired on any peer's press; only host runs `Code`). |
| `CTCPanelKnob.MessageForValueChange` | Customize knob → wire-message conversion. |

---

## AutoEngineer signal consumption

AutoEngineer reads aspects via the topology-side lookahead. Single relevant call: `Graph.EnumerateTrackMarkers` filtered to `TrackMarkerType.Signal`, where each marker's `.Signal` is the `CTCSignal` MonoBehaviour on the same GameObject.

### Lookahead in `Search` → `result.NextSignal`

`AutoEngineerPlanner.Search` (`AutoEngineerPlanner.cs:~2114` in the available-distance walk) iterates markers in `EnumerateTrackMarkers(start, availableDistance, sameDirection: true)` and stores the first `Signal` marker as `result.NextSignal = new Found<CTCSignal>(signal, location, distance)`.

### `UpdateTargets` consumption (`AutoEngineerPlanner.cs:493-558`)

```csharp
SignalAspect lastShownAspect = item.LastShownAspect;
switch (lastShownAspect) {
case Stop:
    if (item.IsIntermediate && (distance < 40 && IsStopped() || _stopAndProceedSignalId == item.id)) {
        _stopAndProceedSignalId = item.id;
        SetSignalSpeedRestriction(15, null, distance, item, distanceLimited: true);  // 15 mph past
    } else if (item.IsIntermediate) {
        AddTarget(0, distance - 15, "Stop and Proceed Signal");                      // stop 15m before
    } else {
        AddContextualOrder(ContextualOrder.OrderValue.PassSignal, item.id);
        SetSignalSpeedRestriction(0, null, distance, item);                          // absolute stop
    }
    break;
case Clear:
case DivergingClear:
    if (distance <= 200) SetSignalSpeedRestriction(null, null, distance, item);     // no restriction
    break;
case Approach:
case DivergingApproach:
    if (distance <= 200) {
        // 15 mph at distance, 25 mph approach speed (lerping between)
        SetSignalSpeedRestriction(15, 25, distance + (distance to next signal), item);
    }
    break;
case Restricting:
    if (distance <= 200) SetSignalSpeedRestriction(15, null, distance, item, distanceLimited: true);
    break;
}

if (StateManager.Shared.Storage.AICallSignals != 0 && distance < 100 && /* not manual */)
    CallSignalIfNeeded(item, lastShownAspect);
```

**Stop and Proceed:** an intermediate (non-interlocking) signal at Stop allows a stopped train within 40m to proceed at 15 mph (pinned via `_stopAndProceedSignalId`). An interlocking signal at Stop is absolute — no proceed.

**`SetSignalSpeedRestriction`:** posts a target speed and target distance. The cruise-control PID uses these to compute throttle/brake.

**`CallSignalIfNeeded` (`AutoEngineerPlanner.cs:741`):** when `AICallSignals` is enabled and the signal is within 100m, the AI engineer "calls" the signal — emits a chat message ("Stop", "Approach", "Clear", etc.) tagged with the signal's hyperlink. Coalesces; only re-calls if the aspect changes.

### `_contextualIgnoreSignalId`

Set by `AutoEngineerContextualOrder.OrderValue.PassSignal` (sent by player override on a Stop signal). Suppresses the next signal lookup once the train approaches a Stop signal it's been authorized to pass.

### Patch candidates

| Method | Why patch |
|---|---|
| `AutoEngineerPlanner.UpdateTargets` (the signal switch block) | Replace AI's signal-aspect-to-action policy. |
| `AutoEngineerPlanner.CallSignalIfNeeded` | Customize/disable signal calling. |
| `AutoEngineerPlanner.Search` (signal marker discovery) | Inject mod-defined signal markers. |

### Gotchas

- **AutoEngineer reads `CTCSignal.LastShownAspect` directly** (`AutoEngineerPlanner.cs:504`) — not through `SignalStorage.GetSignalAspect`. `LastShownAspect` is updated only on the host side in `CTCSignal.ShowAspect`; on a client, `LastShownAspect` is initialized to `Stop` and never updates. **Client-side AutoEngineer running against client-side signal MonoBehaviours would read stale Stop.** This works in practice because AutoEngineer planning runs on the host; clients see the resulting `Orders` via `AutoEngineerPersistence`.
- The `_stopAndProceedSignalId` pinning means a single signal at Stop only triggers one "Stop and Proceed" event per encounter — the train must move past the signal before another Stop signal at the same id triggers again.

---

## Player-as-dispatcher vs AI-dispatcher

**There is no AI dispatcher in vanilla.** The dispatcher role is exclusively played by the host (or a Crew-level player operating the panel UI). Interlockings only respond to:
1. Panel button presses (host-side `Code` call from `CTCPanelController`).
2. Direct script invocations of `CTCInterlocking.Code` (e.g., scenarios, missions).
3. `CTCPanelController.CodeSwitchAndSignal(interlockingId, switchSetting, direction)` for programmatic UI automation.

Trains do not request route lining themselves. AutoEngineer plans to a Waypoint but cannot mutate signals or interlockings — only switches, and only via the same `RequestSetSwitch → TrySetSwitch` channel that respects CTC locks.

If a CTC-locked switch lies on AutoEngineer's path, `HeuristicCosts.AutoEngineer.ThrowSwitchCTCLocked = 1000` makes the search avoid it. If forced through (no alternative), the route step gets `RouteSearch.StepFlag.EnterCTCSwitch` (`Track.Search/Searcher.cs:286`) and AutoEngineer treats it as a hard stop point.

### Multiplayer authority summary

| Action | Who | Channel |
|---|---|---|
| Throw any switch (manual) | Crew | `RequestSetSwitch` → `SetSwitch` (HostOnly) |
| Lock/unlock CTC switch | Crew | `RequestSetSwitchUnlocked` → host calls `Storage.SetSwitchIdUnlocked` |
| Set panel knob position | Crew | `PropertyChange(knob:<id>:position, …)` direct (storage is `MinimumLevelCrew`) |
| Press code button | Crew (everyone sees it; only host acts) | `Storage.SetButton(buttonId, true)` direct |
| Run interlocking `Code()` | **Host only** | Local call from `CTCPanelController.OnButtonPropertyChange` |
| Set block traffic filter | Host (by convention; no assert) | `Storage.SetBlockTrafficFilter` |
| Set interlocking direction | Host (by convention; no assert) | `Storage.SetInterlockingDirection` |
| Set signal aspect | **Host only** (asserted) | `Storage.SetSignalAspect` |
| Set block occupancy | **Host only** (asserted) | `Storage.SetBlockOccupied` |
| Switch occupied/unlocked propagation | Host | `CTCSwitchMonitor` runs only on host (KVO writes broadcast) |

**There are no train-order, track-warrant, or DTC messages in vanilla** — the dispatcher's only output channel is the SignalStorage KVO state (aspects, directions, switch positions). All "dispatch" is signal-mediated.

### Related Messenger / KVO events

| Event | Type | Where |
|---|---|---|
| `Game.Events.CTCFeatureChange` | Messenger struct | `CTCMapFeatureTarget.SetEnabled` (`Track.Signals/CTCMapFeatureTarget.cs:29`) — fires when an interlocking GameObject's CTC feature toggles |
| KVO `signal:<id>:aspect` | int (SignalAspect) | `SignalStorage.SetSignalAspect` |
| KVO `block:<id>:occupancy` | bool | `SignalStorage.SetBlockOccupied` |
| KVO `block:<id>:direction` | int (CTCTrafficFilter) | `SignalStorage.SetBlockTrafficFilter` |
| KVO `il:<id>:direction` | int (SignalDirection) | `SignalStorage.SetInterlockingDirection` |
| KVO `switch:<id>:position` | int (SwitchSetting) | `SignalStorage.SetSwitchPosition` |
| KVO `button:<id>:active` | bool | `SignalStorage.SetButton` |
| KVO `unlockedSwitchIds` | string[] | `SignalStorage.SetSwitchIdUnlocked` |
| KVO `mode` | int (SystemMode) | `SignalStorage.SystemMode.set` |

---

## Gotchas summary

- **The signal subsystem stores nothing in `Graph` or on `TrackNode`.** Everything is in `SignalStorage` (a KVO) plus the per-signal `MonoBehaviour`s' transient state. There's no "signals layer cache invalidation" you need to worry about — observers fire on KVO changes.
- **`SetSignalAspect` and `SetBlockOccupied` are HostOnly via `AssertIsHost`. `SetBlockTrafficFilter` and `SetInterlockingDirection` are host-only by convention only** (no runtime check). Mods directly writing these from a client will succeed locally but the change won't broadcast (since the GlobalKeyValueObject Crew auth might still allow it — verify this by route).
- **AICallSignals is a player setting** read from `StateManager.Shared.Storage.AICallSignals` at decision time. Toggling at runtime affects only future signal encounters; in-flight call coroutines are not cancelled.
- **`CTCBlock.thrownSwitchesSetOccupied` is per-block.** A block authored without this flag will never report occupied just because a switch within it is thrown — only train presence and (in CTC) unlocked switches.
- **Aspect computation is one-frame coalesced (`SetNeedsUpdate` + `yield return null`).** Multiple input changes within a frame produce one `CalculateAspect` call. If you patch `CalculateAspect` and need to track input-change count, do it elsewhere.
- **Signal MonoBehaviours can be culled by `CTCSignalCuller`** (sphere distance <10m for renderers). The signal logic still runs; only visuals are culled. Patches to `CTCSignal.OnEnable` should not assume the renderer is enabled.
- **Diverging aspects always cascade as Yellow** for the next-signal cascade in `CTCAutoSignal.AspectForBlockAndNextSignal`. There's no "diverging clear ahead means clear here" pathway. This is conservative but means trains approaching a junction with all-clear ahead see Yellow.
- **`InterlockingWrapper` (`Track.Signals/InterlockingWrapper.cs`) is a stale-looking helper struct** that wires a `KeyValueObject` directly to `CTCPanelGroup`s and bypasses the request pipeline. Used by tests/scripting (`Code()` enumerator yields one frame between button press and release). Mods authoring tests can leverage this to drive interlockings programmatically.
- **`CTCTests.cs` exists** (in `Track.Signals/`) with a non-trivial test harness; useful as a reference for sequencing `Code()` calls correctly.

---

## Cross-references to Track Topology

- `TrackSpan` ownership and `Graph.FindPoints` for block geometry: see [track-topology › TrackSpan](track-topology.md#tracktrackspan).
- `TrackMarker` registration and `Graph.EnumerateTrackMarkers` (the channel signals reach AutoEngineer through): [track-topology › TrackMarker](track-topology.md#tracktrackmarker).
- `Graph.IsSwitch` / `DecodeSwitchAt` / `CalculateFoulingDistance` (used by AutoEngineer's switch-against detection): [track-topology › Switch model](track-topology.md#switch-model).
- `RequestSetSwitch` flow and `CanSetSwitch` validation: [track-topology › Throwing a switch](track-topology.md#throwing-a-switch--write-paths).
- `SwitchThrownDidChange` Messenger event: [track-topology › TrackNode](track-topology.md#tracktracknode).
- `MapFeature` enabling interlocking GameObjects (which fires `CTCFeatureChange` via `CTCMapFeatureTarget`): [track-topology › Map mods → topology](track-topology.md#map-mods--topology-pipeline).

## Cross-references to other crib sheets

- AutoEngineer planner internals (`Search`, `UpdateTargets`, `Orders`, `AutoEngineerPersistence`): a future `autoengineer.md` crib sheet will detail these. Until then, see `Model.AI/AutoEngineerPlanner.cs` directly.
- Train position math (`Car.WheelBoundsF/R`, `Car.LocationA/B`, span occupancy via `TrainController.AnyCarsOnSpan`): a future `consist-integration.md` crib sheet will document these. The relevant `TrainController` methods are `AnyCarsOnSpan` (`TrainController.cs:370`), `CheckForCarAtLocation` (`TrainController.cs:787`), `CheckForCarAtPoint` (`TrainController.cs:794`), and `CarOnSwitch` (`TrainController.cs:1825`).
- Multiplayer authority model and `IPropertyAccessControlDelegate`: see `docs/research/multiplayer-vanilla-survey.md`.
