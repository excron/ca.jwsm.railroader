# MU & DPU Coordination — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Traction](traction.md), [Brakes](brakes.md), [Auto-Engineer](autoengineer.md), [Consist & Integration](consist-integration.md), [Cars & Cargo](cars-cargo.md)

Vanilla Railroader has **MU but not DPU**. MU ("multi-unit") is a one-direction throttle/reverser mirror: each MU-enabled trailing locomotive *pulls* the lead locomotive's settings once a second via `BaseLocomotive.PeriodicUpdateForMu`. There is no broadcast bus, no designated "lead unit," and no fence between MU groups — the master is whichever first non-cut-out locomotive the slave finds in F-search direction along its consist (then R-search). **MU does not mirror brakes** (each loco's brake handle is independent; the trail-unit pattern relies on cut-out + brake pipe instead). **MU implies cut-out** for the air integration to work, and the CarInspector UI enforces this implication on toggle. **AutoEngineer is mutually exclusive with MU on the driven loco**: every iteration of `MaintainSpeed` calls `FixMuCutOutIfNeeded` which clears `Mu` on the AE-driven loco. AE *does* discover MU'd slaves, sums their TE for power-planning, and lets the slaves remain MU'd to the AE'd master. DPU as a first-class concept (front/rear/mid power groups, fenced coordination) does not exist anywhere in vanilla; the word "DPU" is absent from the decompile.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `BaseLocomotive.PeriodicUpdateForMu()` | `Model/BaseLocomotive.cs:144` | The 1 Hz mirror tick. **Slave pulls** from master. Host-only |
| `BaseLocomotive.FindMuSourceLocomotive()` | `Model/BaseLocomotive.cs:194` | F-search then R-search for first non-self, non-tender, non-cut-out loco |
| `BaseLocomotive.FindSourceLocomotive(LogicalEnd)` | `Model/BaseLocomotive.cs:166` | The actual walk; uses `IntegrationSet.NextCarConnected` with `AirAndCoupled` |
| `BaseLocomotive.IsMuEnabled` (getter) | `Model/BaseLocomotive.cs:69` | Reads KVO key `"mu"` (BoolValue) |
| `LocomotiveAirSystem._ShouldDeferToLocomotiveAir` | `Model.Physics/LocomotiveAirSystem.cs:135` | The "trail unit" path: cut-out + MU defers to MU source's air system |
| `AutoEngineer.FixMuCutOutIfNeeded()` | `Model.AI/AutoEngineer.cs:843` | Force-clears MU on AE-driven loco; clears CutOut if solo |
| `AutoEngineer.CachedMuConnectedLocomotives()` | `Model.AI/AutoEngineer.cs:691` | Discovers MU'd slaves whose `FindMuSourceLocomotive() == this` |
| `CarInspector` MU/CutOut toggles | `UI.CarInspector/CarInspector.cs:187, 196` | Two-way implication: MU on → CutOut on; CutOut off → MU off |
| `PropertyChange.Control.Mu` / `.CutOut` | `Game.Messages/PropertyChange.cs:23, 29` | KVO keys `"mu"` (bool) and `"cutOut"` (bool); both Crew-auth (no `_` prefix) |

---

## MU spine: how a slave inherits the master's settings

```
Once per second (host only), per loco, on PeriodicUpdateBody coroutine:
   │  BaseLocomotive.PeriodicUpdate(1f) → PeriodicUpdateForMu()
   ▼
1) Air-system mirror (host AND client):
       if (air is LocomotiveAirSystem la) {
           la.IsMuEnabled = IsMuEnabled;                       // refresh defer cache
           la.UpdateCachedShouldDeferToLocomotiveAir();
       }
   ▼
2) Throttle/reverser mirror (host only, only if IsMuEnabled):
       master = FindMuSourceLocomotive();                       // may be null
       if (master != null) {
           throttle = master.locomotiveControl.AbstractThrottle;        // master's notch %
           cutoff   = CutoffSettingForVelocity(this.velocity);          // SLAVE'S own velocity
           cutoff  *= (FrontIsA == master.FrontIsA ? 1 : -1)
                    *  Mathf.Sign(master.locomotiveControl.AbstractReverser);
           cutoff   = Mathf.CeilToInt(cutoff * 20f) / 20f;              // snap to 5%
           SendPropertyChange(Throttle, throttle);                      // KVO write on SLAVE
           SendPropertyChange(Reverser, cutoff);                        // KVO write on SLAVE
       }
   ▼
3) Slave's KVO observers (BaseLocomotive.cs:364, 373) fire:
       locomotiveControl.AbstractThrottle = throttle
       locomotiveControl.AbstractReverser = cutoff
       ResetIdleTimer();
   ▼
4) Next FixedUpdate: slave's UpdateTractiveEffortWheelState produces TE
   independently from its own _wheelVelocity.
```

(Source: `Model/BaseLocomotive.cs:144-164`.)

Key facts about this spine that surprise:

1. **Slave does the work, not master.** The slave pulls from the master. There is no "MU bus broadcast." If you patch the master, no MU-related code runs there. To see all MU traffic, patch `PeriodicUpdateForMu`.
2. **Air-system mirror runs even on the *client*.** Steps 1 (the `la.IsMuEnabled = IsMuEnabled; la.UpdateCachedShouldDeferToLocomotiveAir()` block) executes outside the `IsHost` gate at `BaseLocomotive.cs:151`. This is necessary because clients render brake-pipe behavior; the deferral cache must be in sync.
3. **Throttle is mirrored verbatim** (`master.AbstractThrottle` → slave's throttle). For diesel, this is `notch / 8f` — round-trips cleanly through the slave's `DieselLocomotiveControl.AbstractThrottle.set` which re-rounds to int. For steam, this is the master's regulator 0..1 — copied as-is to the slave's regulator.
4. **Reverser is *recomputed*, not mirrored.** The slave runs `CutoffSettingForVelocity(this.velocity)` (its own velocity, not the master's), then signs it by `(orientation parity) * sign(master.AbstractReverser)`. Snaps to 5% increments via `CeilToInt(cutoff * 20f) / 20f`. **For diesel, `CutoffSettingForVelocity` always returns `1f` (`DieselLocomotive.CutoffSettingForVelocity`)**, so the diesel slave receives `±1.0` — and the diesel reverser setter `Round`s it back to `{-1, 0, +1}`. For steam, the slave gets a velocity-appropriate cutoff value, so a high-speed steam slave automatically shortens cutoff like the lead does — **without copying the lead's cutoff position**.
5. **Orientation flip is automatic.** `(FrontIsA == master.FrontIsA) ? 1 : -1` handles a backwards-coupled slave: if the slave is reversed in the consist relative to the master, the cutoff sign flips and the slave pushes/pulls the right way.
6. **1 Hz cadence = 0..1 s lag.** Master throttle change → slave throttle change is delayed by up to a second. Brake/notch coordination during slack run-out is too slow to track in real time at this cadence.
7. **No master-loss handling.** If `FindMuSourceLocomotive()` returns null (e.g., no other non-cut-out loco), the slave keeps its last-written throttle and reverser — it does *not* idle-down. Until something else writes those KVO keys, the slave will keep producing TE based on whatever the last mirror tick set.

---

## `BaseLocomotive.FindMuSourceLocomotive` — master selection algorithm

```csharp
private BaseLocomotive FindSourceLocomotive(LogicalEnd searchDirection)         // 166
{
    bool stop = false;
    int? num = base.set.IndexOfCar(this);
    if (!num.HasValue) throw new Exception("Couldn't find car in set");
    int carIndex = num.Value;
    LogicalEnd fromEnd = (searchDirection == LogicalEnd.A) ? LogicalEnd.B : LogicalEnd.A;
    Car car;
    while (!stop && (car = base.set.NextCarConnected(ref carIndex, fromEnd,
                          IntegrationSet.EnumerationCondition.AirAndCoupled,
                          out stop)) != null)
    {
        if (car == this || car.Archetype == CarArchetype.Tender) continue;
        if (!car.IsLocomotive || !(car is BaseLocomotive bl)) return null;     // ← BAILS if non-loco found
        if (!bl.locomotiveControl.air.IsCutOut) return bl;                     // ← MASTER FOUND
    }
    return null;
}

[CanBeNull]
internal BaseLocomotive FindMuSourceLocomotive()                                // 194
{
    LogicalEnd a = EndToLogical(End.F);
    LogicalEnd b = EndToLogical(End.R);
    return FindSourceLocomotive(a) ?? FindSourceLocomotive(b);
}
```

(Source: `Model/BaseLocomotive.cs:166-200`.)

### Walk semantics

- `set.NextCarConnected(ref idx, fromEnd, AirAndCoupled, out stop)` walks the slave's `IntegrationSet` linearly. The `Predicate` for `AirAndCoupled` is `endGear.IsAirConnected && endGear.IsCoupled` (`IntegrationSet.cs:901`). **The walk stops at the first car whose forward end gear is NOT both air-connected AND coupled.** Air-only or coupled-only edges break the search.
- The walk skips:
  - `car == this` (self).
  - `car.Archetype == CarArchetype.Tender` (tenders are passthroughs).
- The walk **bails to `null`** the moment it sees any non-locomotive non-tender car. So a freight car between the slave and the lead aborts the search.
- Returns the **first non-cut-out locomotive** encountered. Cut-out locos are walked *through* — they're treated as inert for master-selection purposes.

### Direction priority

`FindMuSourceLocomotive()` always tries **F first**, then R as a fallback. If the slave has eligible masters in both directions, **F wins**. There is no concept of "which way the engineer is facing" — it's purely body-relative. This is the closest vanilla gets to a "lead unit" notion, and it's accidental.

### DPU-relevant consequences

- **Order-dependent, no user-selectable lead.** Two non-cut-out diesels at opposite ends of a long consist with a third middle MU+cut-out unit: the middle one mirrors whichever end-loco is found in the F-search direction first.
- **Air integrity gates the search.** If the slave's anglecocks are open *anywhere* between it and the master (e.g., a closed anglecock at car #3 in a 6-car consist), `FindMuSourceLocomotive` returns null and the slave keeps its last-written throttle.
- **Cut-out locos are transparent.** A line of three cut-out + MU'd locos behind a single non-cut-out loco all find the same master (the one non-cut-out loco), regardless of distance, so long as `AirAndCoupled` holds end-to-end.
- **A loco between two cut-out neighbors is its own master** if it isn't cut-out — but `FindMuSourceLocomotive` never returns *self*, so a self-only-eligible loco gets `null` and doesn't mirror.

### Patch candidates (FindMuSourceLocomotive)

| Method | Why patch |
|---|---|
| `BaseLocomotive.FindMuSourceLocomotive` | Replace the F-then-R fallback with explicit "lead unit" lookup (e.g., consult a mod-side `_dpuLeadId` KVO key). |
| `BaseLocomotive.FindSourceLocomotive(LogicalEnd)` | Tweak the predicate (e.g., allow walking past freight cars; allow uncoupled but air-connected edges; cross-set walk for separated DPU). |
| `IntegrationSet.NextCarConnected` / `Predicate` | Add a new `EnumerationCondition` value (e.g., `EnumerationCondition.MuFenced`) that respects mod-side fence boundaries. Note: the enum is not `[Flags]`, so adding a value requires a new switch arm. |

---

## What MU mirrors, and what it does NOT

| Control | Mirrored? | How |
|---|---|---|
| **Throttle** (`Control.Throttle`) | ✓ | Verbatim copy of `master.AbstractThrottle` (PeriodicUpdateForMu line 156) |
| **Reverser** (`Control.Reverser`) | ✓ (**recomputed**) | Slave runs its own `CutoffSettingForVelocity(this.velocity)` × orientation-parity × `sign(master.AbstractReverser)` |
| **Train brake** (`Control.TrainBrake`) | ✗ | Each loco's handle is independent. Trail-unit pattern uses CutOut to disable the slave's handle. |
| **Locomotive brake** (`Control.LocomotiveBrake`) | ✗ | Same. CutOut zeros it forcibly per-tick (`LocomotiveAirSystem.cs:87`). |
| **Bail-off** (`-0.1f` sentinel) | ✗ | Independent per loco. |
| **Hand brake** (`Control.Handbrake`) | ✗ | Per-car, never MU'd. |
| **Bleed valve** (`Control.Bleed`) | ✗ | Per-car. |
| **Cut-out** (`Control.CutOut`) | ✗ | The MU implication runs in CarInspector, not in `PeriodicUpdateForMu`. |
| **Idle** (`Control.Idle`) | ✗ | Per-loco timer; the slave's `ResetIdleTimer()` fires from the throttle/reverser KVO observer fan-out, so MU'd slaves stay non-idle as long as the master is moving the throttle. |
| **Compressor** (`Control.Compressor`) | ✗ | Per-loco air-system state. |
| **Horn / Bell** | ✗ | Per-loco. |
| **Headlight** / **Cylinder cocks** | ✗ | Per-loco. |
| **Whistle / sander / dynamic brake** | n/a | Don't exist in vanilla. |

**The fact that brakes are NOT mirrored is the load-bearing asymmetry.** See [Brakes › MU coordination](brakes.md#mu-coordination). The trail-unit pattern compensates: cut-out + MU = slave's brake handle is dead, slave's *triple valve* responds to brake-pipe pressure on its own (just like a freight car), throttle/reverser come from the master.

### The trail-unit pattern in one paragraph

A correctly-configured trail unit has:
- **`mu = true`** → throttle and reverser mirror the lead.
- **`cutOut = true`** → train-brake and locomotive-brake handles are zeroed every tick (`LocomotiveAirSystem.cs:85-91`); the loco's own brake-pipe service from the cab is disabled. The loco still has a `CarAirSystem`-level triple valve which responds to brake-pipe pressure changes from the lead's brake handle, applying brake cylinder pressure normally.
- **Brake pipe still feeds through.** With anglecocks open and hoses connected end-to-end, the lead's train-brake reduction propagates car-to-car (including through cut-out trail units) and the trail unit's brake cylinder fills proportionally.

This is the closest vanilla gets to "DPU" today: power from multiple locos, brake coordination via the train-brake pipe alone.

---

## CarInspector MU/CutOut UI — the two-way implication

```csharp
// UI.CarInspector/CarInspector.cs:187
builder.AddField("Cut Out", builder.AddToggle(
    () => carControlProperties[PropertyChange.Control.CutOut],
    delegate(bool cutOut) {
        carControlProperties[PropertyChange.Control.CutOut] = cutOut;
        if (!cutOut) {                                                   // Clearing CutOut...
            carControlProperties[PropertyChange.Control.Mu] = false;     // ...also clears MU
            builder.Rebuild();
        }
    }));

// UI.CarInspector/CarInspector.cs:196
builder.AddField("MU", builder.AddToggle(
    () => carControlProperties[PropertyChange.Control.Mu],
    delegate(bool mu) {
        carControlProperties[PropertyChange.Control.Mu] = mu;
        if (mu) {                                                        // Enabling MU...
            carControlProperties[PropertyChange.Control.CutOut] = true;  // ...also forces CutOut on
            builder.Rebuild();
        }
    }));
```

(Source: `UI.CarInspector/CarInspector.cs:184-205`.)

The implication is **enforced only by this UI handler**. Direct KVO writes (e.g., from a mod, from a scripted scenario, from a misbehaving client) can absolutely set `Mu=true, CutOut=false`. The consequences:

- `LocomotiveAirSystem._ShouldDeferToLocomotiveAir` requires `IsCutOut && IsMuEnabled` (`LocomotiveAirSystem.cs:146`); a `Mu=true, CutOut=false` loco does **not** defer to its master's air system.
- The MU mirror (`PeriodicUpdateForMu`) runs regardless of cut-out state — so the slave's throttle/reverser still get overwritten every second.
- The slave's brake handle is *not* zeroed (no cut-out → no per-tick zeroing in `UpdateAir`). So the slave can be putting power down while *also* applying its own brakes if a player twiddled them — no interlock prevents this.

**The UI implication is the only "guard rail."** A mod that exposes its own MU toggle (e.g., the user's DPU experiment) should re-implement the same forcing if it wants vanilla-faithful behavior, OR explicitly diverge with eyes open.

### MP authority for the toggles

| KVO key | String | Auth |
|---|---|---|
| `Control.Mu` | `"mu"` | Crew + train-crew check (no `_` prefix → default auth at `Car.cs:3146`) |
| `Control.CutOut` | `"cutOut"` | Crew + train-crew check |

(Both via `Car.AuthorizationRequirementForPropertyWrite`. See [Cars & Cargo › KVO key auth](cars-cargo.md).) Any train-crew client can flip MU/CutOut on any loco assigned to its crew. **There is no `RequestMuToggle` message; the KVO write itself is the request.**

---

## Auto-Engineer + MU mutual exclusion

Single chokepoint:

```csharp
private void FixMuCutOutIfNeeded()                                              // AutoEngineer.cs:843
{
    if (Locomotive.IsMuEnabled) {
        _log.Information("Disabling MU.");
        Locomotive.ControlProperties[PropertyChange.Control.Mu] = false;
        Say("Turning off MU.");
    }
    if (Locomotive.ControlProperties[PropertyChange.Control.CutOut].BoolValue
        && CountCoupledLocomotives() == 1)
    {
        _log.Information("Disabling Cut-Out.");
        Locomotive.ControlProperties[PropertyChange.Control.CutOut] = false;
        Say("Cutting in - we're the only engine.");
    }
}
```

Called from three places:
- `AutoEngineer.MaintainSpeed()` loop iteration (`AutoEngineer.cs:745`) — runs every loop iteration (~0.5 s cadence).
- Plus two other state-machine entry points (`AutoEngineer.cs:539, 564, 579`) at mode transitions.

### Concrete consequences

1. **AE'd loco cannot be a slave.** If a player toggles MU on the AE'd loco, AE clears it within ~0.5 s. The player intent is lost; chat shows "Turning off MU."
2. **AE'd loco can be a *master*.** AE drives `_control.Throttle/.Reverser` on its own loco; other locos that have `Mu=true` and `FindMuSourceLocomotive() == AE'dLoco` will mirror those writes via `PeriodicUpdateForMu`. AE's `CachedMuConnectedLocomotives` (next section) discovers them and accounts for their TE in power planning.
3. **AE auto-cuts-in solo locos.** If the AE'd loco is the only loco in the consist and somehow `CutOut=true`, AE clears it and announces "Cutting in - we're the only engine." This protects against scenarios where a player mistakenly cut out their only loco then started AE.
4. **AE never touches OTHER locos' MU/CutOut.** Only `Locomotive` (its own loco). MU'd slaves keep their MU+CutOut state.

### `CachedMuConnectedLocomotives` — slave discovery from the master side

```csharp
private IEnumerable<BaseLocomotive> CachedMuConnectedLocomotives()              // 691
{
    if (_cachedLocomotives != null) return _cachedLocomotives;
    _cachedLocomotives = new List<BaseLocomotive>();
    foreach (Car item in CachedCoupled()) {
        if (item is BaseLocomotive bl) {
            if (bl == Locomotive)                                               // self
                _cachedLocomotives.Add(bl);
            else if (bl.IsMuEnabled && bl.FindMuSourceLocomotive() == Locomotive)
                _cachedLocomotives.Add(bl);
        }
    }
    return _cachedLocomotives;
}
```

(Source: `Model.AI/AutoEngineer.cs:691-713`.)

- `CachedCoupled()` returns `Locomotive.EnumerateCoupled().ToList()` — the full physically-coupled consist.
- For each loco in that consist: include it if it's *self*, OR if it's MU-enabled AND its `FindMuSourceLocomotive()` resolves back to *self*.
- The check is *back-pointer* style: a slave with `Mu=true` whose F→R search would land on the AE'd loco counts. A slave whose nearest non-cut-out master is some *other* loco (e.g., a third uninvolved loco between AE'd loco and the slave) does not count.
- Used in `MaxTractiveEffortAtVelocity` summation (`AutoEngineer.cs:630`): `CachedMuConnectedLocomotives().Sum(l => l.MaxTractiveEffortAtVelocity(locomotiveMph))`. So AE plans braking distance and notch-up timing with the full MU'd power available.
- Cache invalidated by `InvalidateCachedCars()` (line 715) which is called once per `MaintainSpeed` iteration (line 744).

### How a DPU mod would need to coexist with AE

If a DPU mod adds its own slave-driving (e.g., per-group throttle splits) and wants to ride alongside AE:

- **Option A: ride on top of AE.** Keep `Mu=true` on the slaves. AE's `CachedMuConnectedLocomotives` will find them, and the slaves will receive AE's throttle via `PeriodicUpdateForMu`. The DPU mod then *post-processes* the slave's `_tractiveEffort` (e.g., postfix `BaseLocomotive.UpdateTractiveEffortWheelState` to scale or cap per group). Minimal disruption to vanilla AE; full transparent.
- **Option B: replace MU on the AE'd loco.** Use a new mod-side KVO key (e.g., `"jwsm.dpu.role"`) and patch `BaseLocomotive.PeriodicUpdateForMu` to also process this key. The AE'd loco can then be *both* "AE-driven" and "DPU-leader" without `FixMuCutOutIfNeeded` interfering. The slaves still need `Mu=false` (so AE's `CachedMuConnectedLocomotives` *won't* find them — power planning will under-count), or you patch `CachedMuConnectedLocomotives` to also include DPU members.
- **Patch `FixMuCutOutIfNeeded` to be DPU-aware.** Bail early if `Locomotive.ControlProperties["jwsm.dpu.role"].StringValue == "leader"`. Risky — breaks AE's solo-loco safety.

The cleanest hook is **Option A + power-cap postfix**. AE remains in charge of the master's notch; the DPU mod biases or zeroes individual slaves' contribution.

---

## DPU as future work — the hookup surface

This is the deep-dive section. Vanilla has no DPU; below is the patch surface a DPU mod would need.

### What "DPU" would mean

Real-world distributed power: the consist has multiple powered locomotive groups (typically lead at the front, remote at the rear, sometimes mid-consist) that coordinate throttle, dynamic brake, and (independently) train-brake reductions across the consist via radio-linked control. Common configurations:
- **Front + Rear:** classic two-group DPU.
- **Front + Mid + Rear:** three-group distributed power, common on heavy trains over grades.
- **Fence configurations:** lead group can be commanded to a different throttle/brake state than remote group ("fence enabled"), allowing the engineer to push uphill from the rear while idling the lead, or vice versa.

Vanilla has none of this. MU is a single-master, single-mirror system; everything else is per-loco-independent.

### What state would the inspector DPU checkbox actually mutate?

The user's experiment (`ca.jwsm.railroader.experiments/ui-toolkit-hud/src/CarInspectorClone.cs:391`) is currently a no-op visual checkbox (`BuildToggleValue(false)`). To wire it to *something real*, the minimum surface is:

1. **A new mod-side KVO key on the loco.** Suggested: `"jwsm.dpu.role"` (string: `"none" | "lead" | "remote"`) or `"jwsm.dpu.group"` (int: 0 = lead, 1+ = remote groups). Non-`_` prefix → Crew+trainCrew auth, same as the vanilla `mu` key.
2. **A handler/observer on the host** that re-implements (or augments) `PeriodicUpdateForMu` to honor the role. The vanilla path can stay intact for the cut-out trail-unit pattern; the DPU role layers on top.
3. **A discovery method** parallel to `FindMuSourceLocomotive` that respects DPU groups — e.g., a remote-group loco mirrors the *same-group lead* rather than the F-search-first non-cut-out loco.

Because the user's clone runs a UI Toolkit panel parallel to the vanilla CarInspector, the toggle should write *both* the new mod-side key *and* call into a mod-side handler. There is no need to patch the vanilla CarInspector at all — the existing MU/CutOut toggles can stay, and the DPU toggle augments.

### Where the split logic would live

For a DPU implementation, three layers need cooperation:

| Layer | Vanilla type | DPU concern |
|---|---|---|
| **Master selection** | `BaseLocomotive.FindMuSourceLocomotive` | Group-aware: a remote loco finds its same-group lead, not just any non-cut-out loco |
| **Throttle/reverser distribution** | `BaseLocomotive.PeriodicUpdateForMu` | Per-group throttle (lead at notch 8, remote at notch 4 for fence config) |
| **Brake distribution** | `LocomotiveAirSystem.UpdateAir` and the brake pipe propagation | Optional: independent train-brake commands per group; brake-pipe flow coordination from rear-of-train |

For Layer 1 (group-aware master), the cleanest patch is `BaseLocomotive.FindMuSourceLocomotive` — replace it (Harmony prefix returning a custom value) to consult a mod-side cache like:

```csharp
// pseudocode
[HarmonyPrefix, HarmonyPatch(typeof(BaseLocomotive), "FindMuSourceLocomotive")]
static bool Prefix(BaseLocomotive __instance, ref BaseLocomotive __result) {
    var role = __instance.ControlProperties[ModKeys.DpuRole].StringValue;
    if (role == "remote") {
        __result = FindSameGroupLead(__instance);                     // mod-side walk
        return false;
    }
    return true;        // fall through to vanilla
}
```

For Layer 2 (per-group throttle), patch `PeriodicUpdateForMu` postfix to reapply a fence offset:

```csharp
// pseudocode
[HarmonyPostfix, HarmonyPatch(typeof(BaseLocomotive), "PeriodicUpdateForMu")]
static void Postfix(BaseLocomotive __instance) {
    if (!StateManager.IsHost) return;
    if (!__instance.IsMuEnabled) return;
    var fenceOffset = ModFenceState.OffsetFor(__instance);            // e.g., -0.5
    if (fenceOffset == 0) return;
    var raw = __instance.locomotiveControl.AbstractThrottle;
    var adjusted = Mathf.Clamp01(raw + fenceOffset);
    __instance.SendPropertyChange(PropertyChange.Control.Throttle, adjusted);
}
```

Layer 3 is the hard one. There is no rear-of-train air source in vanilla — the brake pipe is a single chain. For a faithful DPU brake-pipe assist, the closest hook is `LocomotiveAirSystem.UpdateAir` on each remote-group loco: when the local cut-in state allows, *also* feed the brake pipe from this loco's main reservoir (currently only the lead does this via `_mainReservoirToBrakeLine.ValveAutomaticBrake`). Patching this would require care — see [Brakes › MU coordination](brakes.md#mu-coordination) for the warning that propagation lag is currently single-source.

### What MP authority would a DPU command need?

- **Per-loco DPU role/group writes:** Crew + trainCrew (parity with `mu`). KVO key, no leading underscore.
- **Fence-state command:** This is more like a "consist intent" than a per-loco state. Two options:
  - Store on the lead loco as a KVO key (e.g., `"jwsm.dpu.fence"` int). Crew auth. Each remote pulls the lead's value during its `PeriodicUpdateForMu` postfix.
  - Define a `RequestDpuFence` IGameMessage with `MinimumAccessLevel(AccessLevel.Crew)` and `[HostOnly]` execution. Host validates the requester is on the consist's crew, writes lead's KVO, broadcasts.
- **Independent throttle per group:** No new auth; remote locos already accept throttle KVO writes from Crew. The DPU host code writes them (similar to AE writing throttle).

**Use AE's `LocomotiveControlHelper.Throttle` setter (`Model/LocomotiveControlHelper.cs:14`) rather than `ControlProperties[Throttle] = ...` directly.** The helper clamps to `[0,1]`; the raw `ControlProperties` setter does not, and out-of-range values can OOB the diesel `_notchToPowerPercent` array (see [Traction › gotchas](traction.md#gotchas-diesel)).

### Existing helpers a DPU implementation could lean on

| Helper | File | DPU use |
|---|---|---|
| `LocomotiveControlAdapter` (abstract) | `RollingStock/LocomotiveControlAdapter.cs` | Write throttle/reverser/brakes via `AbstractThrottle`/`AbstractReverser`/etc. setters; uniform across diesel/steam |
| `LocomotiveControlHelper` | `Model/LocomotiveControlHelper.cs:14, 26` | `Throttle.set` / `Reverser.set` clamp + emit PropertyChange; safe writer |
| `BaseLocomotive.SendPropertyChange(Control, float)` | inherited from `Car` | Direct KVO write that triggers the local + broadcast pipeline |
| `Car.EnumerateCoupled()` / `EnumerateAirOpen()` | `Model/Car.cs` | Walk the consist; `EnumerateAirOpen` respects open anglecocks (use for brake-pipe-aware DPU group discovery) |
| `IntegrationSet.NextCarConnected(ref idx, fromEnd, condition, out stop)` | `Model.Physics/IntegrationSet.cs:875` | The same primitive `FindSourceLocomotive` uses; mod can pass a custom-walk loop |
| `IntegrationSet.EnumerationCondition` enum | `Model.Physics/IntegrationSet.cs:56` | `Coupled`, `AirConnected`, `AirAndCoupled` — adding a fourth would require a new switch arm in `Predicate` |
| `AutoEngineer.CachedMuConnectedLocomotives` | `Model.AI/AutoEngineer.cs:691` | Pattern for "discover slaves whose master is me." Mod can adapt to DPU groups. |
| `BaseLocomotive.MaxTractiveEffortAtVelocity(absMph)` | abstract | Per-loco TE oracle for power-planning across DPU groups |
| `BaseLocomotive.CutoffSettingForVelocity(velocityMps)` | virtual | Steam: cutoff oracle. Diesel returns 1f. Per-loco-velocity-aware cutoff for DPU members. |
| `LocomotiveAirSystem.IsCutOut` / `IsMuEnabled` (settable) | `LocomotiveAirSystem.cs:38, 70` | Direct host-side state; the `PeriodicUpdateForMu` air mirror keeps these in sync with KVO each second |

### Concrete patch points (DPU surface, condensed)

| Patch target | Type | Why |
|---|---|---|
| `BaseLocomotive.FindMuSourceLocomotive` | Prefix | Replace master-selection with group-aware lookup |
| `BaseLocomotive.PeriodicUpdateForMu` | Postfix | Apply fence offset to throttle, override reverser per group |
| `BaseLocomotive.PeriodicUpdate(float)` | Override | Bump the cadence above 1 Hz if needed (caution: brake-pipe propagation also uses 1 Hz at host) |
| `AutoEngineer.CachedMuConnectedLocomotives` | Postfix | Add DPU-group members so AE plans power across the whole DPU set |
| `AutoEngineer.FixMuCutOutIfNeeded` | Prefix | Bail when AE'd loco is a DPU lead, to preserve player intent |
| `LocomotiveAirSystem._ShouldDeferToLocomotiveAir` | Prefix | If extending the cut-out + MU pattern, decide whether DPU remotes also defer |
| `LocomotiveAirSystem.UpdateAir` | Postfix | (Advanced) feed brake pipe from remote-group main reservoir |
| `IntegrationSet.NextCarConnected` / `Predicate` | Prefix | (Caution) add a new `EnumerationCondition` for DPU-fence-aware walking |
| Custom KVO key handlers | Observer | `"jwsm.dpu.role"` / `"jwsm.dpu.group"` / `"jwsm.dpu.fence"` (Crew auth) |

### Gotchas (DPU planning)

- **1 Hz mirror lag.** Vanilla MU's 1-second cadence is too slow for live coordination during slack run-out (slack reverses in fractions of a second). A DPU mod that wants tighter coordination should patch `PeriodicUpdate` cadence OR drive remote throttles directly from a master-side `Update`/`FixedUpdate` loop.
- **MU implies CutOut implies dead brake handle.** A DPU "remote" that doesn't take cut-out keeps its own brake handle live — engineers in multiplayer can fight the lead. Decide policy: either mirror the brake setting too (deviating from vanilla MU) or auto-cut-out remotes (parity with MU pattern).
- **Brake pipe is single-source.** No second-source air feed exists; a 50-car DPU train still has the same head-to-tail brake-pipe lag as a single-source 50-car train. See [Brakes › MU coordination](brakes.md#mu-coordination), which calls this out as a vanilla limitation.
- **`FindMuSourceLocomotive` aborts on non-loco non-tender cars.** A DPU configuration with freight cars between groups (front group + 30 freight + rear group) **cannot use the vanilla `AirAndCoupled` walk** — it'll hit the freight cars and bail. The mod's group-aware lookup must walk past freight (or use `set.IndexOfCar` + direct group-id lookup, ignoring the linear walk).
- **Set-split on uncouple.** When a DPU consist splits (e.g., player uncouples mid-train to drop cars), `IntegrationSet.Split` runs and the two halves get separate `IntegrationSet` instances. `FindMuSourceLocomotive` only walks within `this.set`, so a split DPU consist immediately stops mirroring. Mod-side group state (`jwsm.dpu.group`) survives, but cross-set master discovery will fail. Decide policy: re-establish groups on `IntegrationSet.Tick` re-scan, or accept that splitting breaks DPU.
- **AE's `FixMuCutOutIfNeeded` runs every iteration of `MaintainSpeed`.** Even if you preserve player MU intent via a DPU role, running AE on a DPU lead still risks AE's solo-loco-cut-in clear if `CountCoupledLocomotives() == 1` somehow returns 1. Audit `CountCoupledLocomotives` semantics if your DPU mod virtualizes the consist.
- **The slave's reverser is recomputed per-tick from its own velocity.** This is fine for MU but for DPU with mid-consist locos, the slave's local velocity may differ from the master's (during slack stretch/compression). For diesel this doesn't matter (cutoff = 1). For steam DPU, the recomputed cutoff at the slave may differ from the lead's cutoff — engineers may want the lead's *actual* cutoff mirrored, not a local-velocity oracle. To do that, mirror `master.locomotiveControl.AbstractReverser` directly with the orientation flip and skip the `CutoffSettingForVelocity` recomputation.
- **No "headlight to direction" coupling.** AE auto-sets headlights based on direction (`AutoEngineer.cs:754`); DPU should probably do the same on remotes facing the train.
- **The Inspector DPU checkbox the user prototyped is currently a `BuildToggleValue(false)` no-op** (`CarInspectorClone.cs:399`). When wiring it for real, write to a mod-side `ControlProperties` key that you've registered as a non-HostOnly Crew-auth string/bool. Don't reuse `Control.Mu` — the AE auto-clear will fight you.

---

## Cross-cutting

### With Brakes ([brakes.md](brakes.md))

- **MU does not mirror brakes.** The brake handle is per-loco. Trail-unit pattern relies on cut-out (slave's brake handles dead) + brake-pipe propagation. See [Brakes › MU coordination](brakes.md#mu-coordination).
- **Cut-out forcibly zeros the slave's brake settings every tick** at `LocomotiveAirSystem.cs:85-91`. UI changes to a cut-out loco's brake handle are visible for one frame and then zeroed. No way to fight it without patching `UpdateAir`.
- **Brake-pipe propagation is single-source from the lead's main reservoir.** A DPU mod that wants rear-of-train assist must add a second source — see [Brakes › MU coordination](brakes.md#mu-coordination)'s explicit call-out about this gap.
- **Bail-off (`-0.1f` sentinel)** is per-loco. MU does not propagate it. A DPU mod could decide to mirror it across the group.

### With the cut-out mechanic

- **MU implies CutOut, but CutOut does not imply MU.** Cutting out a loco for maintenance (engine failure, etc.) without enabling MU is a valid state — the loco is inert and rolls along.
- The `_ShouldDeferToLocomotiveAir` defer (`LocomotiveAirSystem.cs:135`) requires *both*. Cut-out alone doesn't activate the defer; that loco is treated as an isolated air system.
- Cut-out disables the loco's brake handle but **does not disable its triple valve.** The car still brakes from brake-pipe reductions. This is the property that makes the trail-unit pattern work.
- See [Brakes › cut-out](brakes.md#cut-out-locomotive-isolation) and [Cars & Cargo › load.{n} prefix-auth + MU implies CutOut](cars-cargo.md) for the auth/wire details.

### With Auto-Engineer ([autoengineer.md](autoengineer.md))

- **AE owns one loco's throttle/reverser. MU on the AE'd loco is auto-cleared.** See [Auto-Engineer › `FixMuCutOutIfNeeded`](autoengineer.md#patch-candidates).
- **AE plans power across MU'd slaves** via `CachedMuConnectedLocomotives` (line 691). Slaves remain MU'd to the AE'd master; AE's `MaxTractiveEffort` summation includes them.
- **AE drives via `LocomotiveControlHelper.Throttle.set`** which clamps to `[0,1]`. The MU mirror then writes the slave's KVO with that same value.
- A DPU mod riding alongside AE should patch *after* `PeriodicUpdateForMu` (postfix) to bias slave throttles, so AE's master writes propagate first, then DPU policy applies.

### With Consist & Integration ([consist-integration.md](consist-integration.md))

- **MU master discovery walks `this.set` only.** No cross-set walking. A separated DPU consist (e.g., split mid-train) breaks the mirror immediately.
- **`IntegrationSet.NextCarConnected` is the canonical walk primitive** used by `FindSourceLocomotive`. The `EnumerationCondition.AirAndCoupled` predicate is the gating constraint.
- **`Set.IndexOfCar(this)` returning null throws** — this only happens if the loco isn't yet wired into a set (mid-creation, mid-destruction). `PeriodicUpdateForMu` doesn't catch this; rely on the vanilla coroutine startup ordering.

### With Cars & Cargo ([cars-cargo.md](cars-cargo.md))

- **`Mu` and `CutOut` are non-prefixed KVO keys** (`"mu"`, `"cutOut"`). Default Crew + trainCrew auth via `Car.AuthorizationRequirementForPropertyWrite`.
- **There is no `RequestMu` / `RequestCutOut` message.** Clients write the KVO key directly; the standard PropertyChange pipeline handles auth, broadcast, and observer fan-out.

---

## Gotchas (MU-specific)

- **`PeriodicUpdateForMu` runs the air mirror block even on clients.** Step 1 in the spine (the `la.IsMuEnabled = IsMuEnabled; la.UpdateCachedShouldDeferToLocomotiveAir()` block) executes outside the `IsHost` gate. Patches that add side effects to step 1 will fire client-side; patches to step 2 (the throttle/reverser write) only fire host-side.
- **Ghost locos (`if (ghost) yield break`) never run `PeriodicUpdateForMu`.** The coroutine `PeriodicUpdateBody` (`BaseLocomotive.cs:108`) bails for ghosts. If your mod adds simulated ghost locos, MU won't activate on them.
- **The reverser snap to 0.05 (`Mathf.CeilToInt(num * 20f) / 20f`) means the slave's reverser KVO is always a multiple of 0.05.** For diesel this is irrelevant (the int-round in `DieselLocomotiveControl.AbstractReverser.set` collapses to {-1, 0, +1}). For steam this means the slave can never run cutoff at, say, 0.42 — it'll always be 0.45.
- **Throttle is mirrored as a *float*, not a notch.** For two diesel locos in MU, the master's `0.875` (notch 7) is sent to the slave, which rounds to notch 7 on the way in. No precision loss because the master's value is already an exact multiple of 1/8. **But for diesel-master + steam-slave (or vice versa), there's a units mismatch:** steam regulator is continuous 0..1, diesel throttle is `notch / 8`. A steam loco MU'd to a diesel will have its regulator slammed to discrete 1/8 steps. Not realistic, but works mechanically.
- **`ResetIdleTimer` fires on every MU-driven throttle/reverser observer.** A slave perpetually mirroring a non-zero throttle is perpetually marked non-idle, even if it's not actually moving (e.g., wheels slipping on grease). The `idle` KVO will never flip true.
- **`SendPropertyChange` writes via `StateManager.ApplyLocal`** (the `KeyValueObject` setter) which means the slave's local KVO update fires *immediately* (same tick as the mirror), and the broadcast to other clients goes via the host's PropertyChange pipeline. So MU latency to the *host's view of the slave* is effectively 0; latency to *other clients* is the standard PropertyChange broadcast latency (well below 1 s).
- **`FindMuSourceLocomotive` returns null silently.** No log line. If a mod's DPU lookup fails the same way, plan to log explicitly — debugging "why isn't my slave mirroring" otherwise requires patching to inject diagnostics.
- **The `PeriodicUpdateBody` coroutine starts on `OnEnable`** (standard Unity pattern) and is keyed to `WaitForSeconds(1f)`. There's no jitter-randomization across locos — every MU'd loco in the world ticks at approximately the same phase, all within one Unity frame. For 50 MU'd locos in a single big consist, that's 50 `FindMuSourceLocomotive` walks compressed into one frame each second. Not a perf problem at vanilla scale but worth knowing if a mod multiplies the cadence.
- **`UpdateCachedShouldDeferToLocomotiveAir` rebuilds the defer cache every second.** If you patch `_ShouldDeferToLocomotiveAir` to add cost, expect it to be called once per loco per second on host (and once per loco per second on client). Cheap; just be aware.

---

## Cross-references

- [Traction](traction.md) — TE pipeline, `BaseLocomotive` core, how each loco's `_tractiveEffort` is computed independently. The "DPU experiment guidance" call-out at [traction.md › DPU experiment guidance](traction.md#dpu-experiment-guidance-call-out) is a teaser; this sheet is the depth.
- [Brakes](brakes.md) — Why MU doesn't mirror brakes, the cut-out brake-handle zeroing, the trail-unit pattern explained. See [Brakes › MU coordination](brakes.md#mu-coordination) for the brake-pipe-propagation gap that any DPU rear-air-source mod must address.
- [Auto-Engineer](autoengineer.md) — `FixMuCutOutIfNeeded`, `CachedMuConnectedLocomotives`, AE's solo-loco safety. The mutual-exclusion mechanism in detail.
- [Cars & Cargo](cars-cargo.md) — KVO auth model, the `mu`/`cutOut` keys' Crew auth.
- [Consist & Integration](consist-integration.md) — `IntegrationSet`, `NextCarConnected`, the walk primitive `FindMuSourceLocomotive` consumes.
- [Couplers](couplers.md) — `EndGear.IsCoupled` / `IsAirConnected` are the predicates used by `AirAndCoupled`. Auto-uncouple paths explain when a MU master walk would suddenly fail.
