# Progression / Campaign / Map Features — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [State Manager](state-manager.md), [Save/Load](save-load.md), [Industries & Ops](industries-ops.md), [Track Topology](track-topology.md)

Progression is the campaign / chapter system. A scene authors a tree of `Progression > Section > DeliveryPhase` MonoBehaviours under a parent that the `ProgressionManager` discovers; the active progression (selected by setup id, e.g. `"ewh"`) is wired into the `_progression` KVO. Each `Section` advances by paying its `DeliveryPhase.cost` (host-routed via the `ProgressionStartPhase` Officer-auth message) and then either auto-completes (no deliveries) or hands off to a `ProgressionIndustryComponent` that orders cars and waits for delivery. Section unlock toggles `MapFeature`s on a sibling `MapFeatureManager`, which writes the `mapFeatures` KVO and then enables/disables `Track.Graph` groups, GameObjects, and `IProgressionDisablable` industries/passenger stops. **Both `_progression` and `mapFeatures` are HostOnly KVOs but they are restored through *different code paths* — `mapFeatures` is special-cased in `StateManager.PopulateFromRemoteSnapshot` to fire its observer chain explicitly, while `_progression` rides the generic `RestoreProperties` fan-out.**

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Game.Progression.Progression` | `Game.Progression/Progression.cs:18` | Per-progression root; owns `Sections[]`, observes `_progression` KVO |
| `Game.Progression.ProgressionManager` | `Game.Progression/ProgressionManager.cs:11` | Picks the active progression by id, registers `_progression` KVO |
| `Game.Progression.Section` | `Game.Progression/Section.cs:9` | A campaign chapter; holds `DeliveryPhase[]`, prerequisites, MapFeature unlock arrays |
| `Section.DeliveryPhase` | `Game.Progression/Section.cs:12` | One purchase-step within a section: `cost`, `deliveries[]`, `industryComponent` |
| `Game.Progression.MapFeature` | `Game.Progression/MapFeature.cs:6` | Identifier + manifest of things to enable when unlocked (track groups, gameobjects, areas, industries) |
| `Game.Progression.MapFeatureManager` | `Game.Progression/MapFeatureManager.cs:18` | Owns the `mapFeatures` KVO; applies feature-enable diffs to the world |
| `Model.Ops.ProgressionIndustryComponent` | `Model.Ops/ProgressionIndustryComponent.cs:11` | The "deliver N cars to here" industry component used by `DeliveryPhase` |
| `Game.Progression.SetupDescriptor` | `Game.Progression/SetupDescriptor.cs:9` | Per-`setupId` scene preset: starting balance, spawn point, car placements |
| `Game.Progression.InterchangeTransfer` | `Game.Progression/InterchangeTransfer.cs:6` | One-shot waybill rewrite on section-completion |
| `Game.Messages.ProgressionStartPhase` | `Game.Messages/ProgressionStartPhase.cs:8` | The `Officer`-auth IGameMessage that pays for a phase |
| `Progression.HandlePayToStartPhase` | `Game.Progression/Progression.cs:402` | Host-side handler dispatched from `StateManager.Handle` (l.730) |
| `StateManager.HandleSnapshotMapFeatures` | `Game.State/StateManager.cs:1205` | Special pre-restore step that hydrates `mapFeatures` KVO |
| `Model.Ops.IProgressionDisablable` | `Model.Ops/IProgressionDisablable.cs:3` | One-property interface (`bool ProgressionDisabled`) implemented by `Industry`, `IndustryComponent`, `PassengerStop` |
| `Game.Events.ProgressionStateDidChange` | `Game.Events/ProgressionStateDidChange.cs:6` | Empty Messenger struct; UI rebuild trigger |

---

## Spine: section flow

```
NewGameMenu.SelectProgressionId("ewh")              ← UI.Menu/NewGameMenu.cs:127
   │
   ▼
NewGameSetup { ProgressionId = "ewh", SetupId = "ewh-steam" }
   │
   ▼
StateManager.ApplyNewGameSetup(setup)               ← StateManager.cs:397
   │  _gameSetupPropertyPresets += ("_progression","progression",Value.String("ewh"))
   │  if SetupDescriptor found && Cars==0 && Balance==0:
   │    StartCoroutine(CompanyModeSetup.Setup(...))  ← 5s delay then place starter cars
   │
   ▼
Snapshot ingest (PopulateFromRemoteSnapshot, StateManager.cs:1131)
   │  HandleSnapshotMapFeatures(properties)          ← SPECIAL case  (l.1205)
   │  ApplySetupPropertyPresets(properties)          ← injects "_progression":"progression"="ewh"  (l.1214)
   │  RestoreProperties(properties)                  ← generic fan-out, _progression KVO loaded here (l.1233)
   │  RestoreNotifier.NotifyDidRestore()             ← fires PropertiesDidRestore + GameBehaviour.OnEnableWithProperties priority chain
   │
   ▼
ProgressionManager.OnEnableWithProperties           ← ProgressionManager.cs:50, EnablePriority=100
   │  reads _progression["progression"]
   │  if Sandbox && set: warn + ignore
   │  if not set: every ProgressionIndustryComponent.ProgressionDisabled = true; bail
   │  else: _current = matching Progression child; _current.Configure(_keyValueObject)
   │
   ▼
Progression.Configure(kvo)                          ← Progression.cs:52
   │  Sections = GetComponentsInChildren<Section>()
   │  CheckSections()                                ← warn if any ProgressionIndustryComponent shared by 2+ phases
   │  observe ALL keys of _progression KVO → UpdateSectionStates()
   │  if Host: enableFeaturesAtStart → mapFeatureManager.SetFeatureEnabled(f, true)
   │  Messenger.Register<GameModeDidChange, ProgressionStateDidChange> → UpdateSectionStates()
   │  UpdateSectionStates()
```

```
UpdateSectionStates()                               ← Progression.cs:231
   ├── for each Section: Unlocked = id in unlockedSectionIds
   ├── for each Section: Available = !Unlocked && all prerequisites Unlocked
   │                     PaidCount     = _progression["paid-"+id].IntValue
   │                     FulfilledCount= _progression["fulfilled-"+id].IntValue
   ├── per-section MapFeature dict aggregate:
   │     enableFeaturesOnAvailable[i].id → section.Available
   │     enableFeaturesOnUnlock[i].id    → section.Unlocked
   │     disableFeaturesOnUnlock[i].id   → !section.Unlocked
   ├── mapFeatureManager.SetFeatureEnables(dict)    ← single batched write to mapFeatures["features"]
   └── per-section: ProgressionIndustryComponent toggling
         ActivePhaseIndexForSection(s):
           null if !Available || (Fulfilled==Paid)
           else PaidCount-1
         ProgressionDisabled = (this component's phase != ActivePhaseIndex)
         if active: ConfigureIndustry(s, phaseIndex) ← wires onComplete + KVO
   └── if any ProgressionIndustryComponent flipped: Messenger.Send(IndustriesDidChange)
```

```
Player clicks "Start Phase" in Goals UI            ← UI.CompanyWindow/GoalsPanelBuilder.cs:130
   │  StateManager.ApplyLocal(new ProgressionStartPhase(id, phaseIndex))
   ▼
StateManager.Handle (l.730) → Progression.HandlePayToStartPhase
   │  HOST ONLY guard
   │  → PayToStartPhase(section, phaseIndex, sender)             ← Progression.cs:191
   │     AssertIsHost
   │     if !section.Available: throw
   │     if section.PaidCount != phaseIndex: throw "not next in sequence"
   │     cost = CostForPhase(phase, out discount)                ← reputation discount
   │     if Balance < cost: throw DisplayableException("Insufficient balance")
   │     SetPaidDeliveriesForSectionId(id, phaseIndex+1)         ← writes _progression["paid-"+id]
   │     ApplyToBalance(-cost, Ledger.Category.Progression, ...)
   │     if phase.deliveries.Length == 0:                        ← cosmetic / unlock-only phase
   │       Multiplayer.Broadcast(...)
   │       PhaseCompleted(section, phaseIndex)                   ← straight to fulfilment
   │     else:
   │       UpdateSectionStates()
   │       SendFireEvent(ProgressionStateDidChange)
   │       Multiplayer.Broadcast("ordered cars...")
   │       (DeliveryPhase.industryComponent now Configure'd by UpdateSectionStates → orders cars on next tick)
```

```
ProgressionIndustryComponent.Service(ctx)          ← per-tick per-industry, host-side
   │  (orders cars in OrderCars(); on Service, increments _progression["indRecv"] dict per delivery tag)
   │  CheckForCompletion(ctx) when all tags hit count → _onComplete()
   ▼
PhaseCompleted(section, phaseIndex)                 ← Progression.cs:371
   │  HOST ONLY guard
   │  if FulfilledCount >= phaseIndex+1: log + bail (idempotency)
   │  FulfilledCount = phaseIndex+1; write _progression["fulfilled-"+id]
   │  if FulfilledCount == PhaseCount:                            ← entire section now done
   │     UnlockedSectionIds += section.identifier  (writes _progression["unlocked"] string array)
   │     section.ApplyCompleted()                                 ← all child InterchangeTransfer.Apply() → OpsController.RewriteWaybills
   │  Multiplayer.Broadcast(...)
   │  UpdateSectionStates()                                       ← cascades MapFeature toggles to mapFeatureManager
   │  SendFireEvent(ProgressionStateDidChange)
```

The `_progression["unlocked"]` write echoes through the `_keyValueObject.ObserveKeyChanges` registered in `Configure` (l.58), which calls `UpdateSectionStates()` *again*. Coalescing happens because the work is idempotent and runs synchronously inside the KVO observer.

---

## `Game.Progression.Progression`

The per-`Progression` MonoBehaviour. Each Progression node in the scene defines one possible campaign (vanilla ships exactly one: `identifier = "ewh"`). A `Progression` is selected by `ProgressionManager` based on `_progression["progression"]`, whose value comes from `NewGameSetup.ProgressionId`.

### State fields & properties

```csharp
public string identifier;                                                 // 20 — selection key
public MapFeatureManager mapFeatureManager;                               // 22 — sibling reference
[FormerlySerializedAs("enableAtStart")]
[SerializeField] private MapFeature[] enableFeaturesAtStart;              // 26 — host-only init
private KeyValueObject _keyValueObject;                                   // 28 — points at _progression
public Section[] Sections { get; private set; }                           // 36
public static Progression Shared => _instance;                            // 38 — singleton (set in Configure)
private const string KeyUnlocked = "unlocked";                            // 34
```

### `_progression` KVO key map

| Key | Type | Read/Write | Source |
|---|---|---|---|
| `progression` | string | Set by `ApplySetupPropertyPresets`; read by `ProgressionManager.OnEnableWithProperties` | The campaign id (e.g. `"ewh"`). One-shot — never rewritten in vanilla |
| `unlocked` | string array | Read+write via `Progression.UnlockedSectionIds` getter/setter (l.40) | List of completed section identifiers |
| `paid-<sectionId>` | int | `GetPaidDeliveriesForSectionId` / `SetPaidDeliveriesForSectionId` (l.117) | Count of phases the player has paid for in this section |
| `fulfilled-<sectionId>` | int | `GetFulfilledDeliveriesForSectionId` / `SetFulfilledDeliveriesForSectionId` (l.127) | Count of phases whose deliveries actually completed |
| `indRecv` | dict<string,int> | Written by `ProgressionIndustryComponent.IncrementReceived`; read in `CountReceived` | Per-delivery-tag received-car counter (the tag `<sectionId>.<phaseIdx>.<deliveryIdx>` is the key) |

`indRecv` lives on `_progression` even though it's a per-industry counter — `ProgressionIndustryComponent` is handed the `Progression`'s KVO via `Configure`. **All progression state is in this single KVO**, including the per-industry receipt counter. This is unusual — most industry-side state lives on the industry's own KVO.

### Public API

```csharp
public void Configure(KeyValueObject kvo)              // 52
public void Unconfigure()                              // 81
public void Advance()                                  // 137 — debug ContextMenu, advances first available section
public void Advance(Section section)                   // 151 — Asserts host, throws if !Available, increments paid then PhaseCompleted
public void Revert(Section section)                    // 167 — clears paid/fulfilled/unlocked recursively (Revert anything that depended on it)
public void HandlePayToStartPhase(string id, int phaseIndex, IPlayer sender)  // 402 — IGameMessage entrypoint
public int CostForPhase(Section.DeliveryPhase phase, out float discountPercent) // 433 — applies ReputationTracker.PhaseDiscount()
```

### `PayToStartPhase` validation

```csharp
private void PayToStartPhase(Section section, int phaseIndex, IPlayer sender)
{
    StateManager.AssertIsHost();
    if (!section.Available) throw new Exception("Section is not available");
    if (section.PaidCount != phaseIndex) throw new Exception("phaseIndex is not next in sequence");
    int cost = CostForPhase(section.deliveryPhases[phaseIndex], out _);
    if (StateManager.Shared.GetBalance() < cost) throw new DisplayableException("Insufficient balance");
    SetPaidDeliveriesForSectionId(section.identifier, phaseIndex + 1);
    StateManager.Shared.ApplyToBalance(-cost, Ledger.Category.Progression, null, section.displayName);
    // ...
}
```

`HandlePayToStartPhase` (l.402) wraps this in try/catch — `DisplayableException`'s `DisplayMessage` is forwarded back to the sender via `Multiplayer.SendError(sender, msg)`. **Other exceptions surface as the generic `"Unable to start phase"`** — no console message stack.

`Advance()` and `Advance(Section)` (l.137,151) are debug entry points (the parameterless overload is wired as `[ContextMenu("Debug: Advance")]`). They bypass payment entirely.

`Revert(Section)` (l.167) is the inverse — clears `paid-<id>`, `fulfilled-<id>`, removes from `unlocked` array, recursively reverts any section that depended on this one. **No UI exposes Revert** in vanilla; only callable from code or a debug inspector.

### Patch candidates

| Method | Why patch |
|---|---|
| `Progression.HandlePayToStartPhase(string, int, IPlayer)` | The IGameMessage handler. Prefix to gate (e.g. require additional auth, custom prereqs); postfix to log/broadcast |
| `Progression.PayToStartPhase` (private) | Inner pay-and-validate; prefix to override cost or tweak balance check |
| `Progression.PhaseCompleted(Section, int)` | The "phase done" callback. Postfix to spawn rewards, fire mod events |
| `Progression.UpdateSectionStates()` | Where MapFeature aggregates and ProgressionIndustryComponent enable/disable get computed. Postfix to layer additional unlocks |
| `Progression.CostForPhase(DeliveryPhase, out float)` | Replace cost / discount formula |
| `Progression.Configure(KeyValueObject)` | Inject mod-side observers; mod sections added at runtime by `GetComponentsInChildren<Section>()` get picked up if the GameObject is parented under the Progression before Configure runs |
| `Progression.Advance(Section)` | Debug shortcut — patch only if exposing a "skip phase" cheat |
| `ProgressionManager.OnEnableWithProperties` | The selection logic. Patch to inject mod-defined `Progression` siblings, override the sandbox-ignore behavior, or add a default fallback |

### MP authority

- `Progression` itself is host-side logic — `_keyValueObject` is HostOnly (`MinimumLevelHostOnly` via `RegisterPropertyObject(_, _, AuthorizationRequirement.HostOnly)` in `ProgressionManager.Awake`).
- The only client→host path is `ProgressionStartPhase` (`[MinimumAccessLevel(AccessLevel.Officer)]`). **Officer is the gate** — Crew and Dispatcher cannot start phases.
- `Advance(Section)` and `Revert(Section)` call `StateManager.AssertIsHost()` directly. They are not exposed as IGameMessages; clients calling them throw.
- Every `_progression` KVO write happens host-side; clients receive `PropertyChange` broadcasts and re-run `UpdateSectionStates()` via the `ObserveKeyChanges` observer.

### Related Messenger / KVO events

| Event | Type | Source | Consumers |
|---|---|---|---|
| `Game.Events.ProgressionStateDidChange` | Messenger struct (empty) | `Progression.PhaseCompleted`, `Progression.PayToStartPhase`, `Progression.Revert` (via `StateManager.SendFireEvent`) | `GoalsPanelBuilder` rebuild trigger; `Progression.UpdateSectionStates` re-runs |
| `Game.Events.GameModeDidChange` | Messenger struct | `GameStorage.mode` setter | Re-runs `UpdateSectionStates` (e.g., switching to Sandbox in-game) |
| `Game.Events.IndustriesDidChange` | Messenger struct | Sent if any `ProgressionIndustryComponent.ProgressionDisabled` flipped during `UpdateSectionStates` (l.331) | Industries-Ops UI rebuild |
| `_progression` keys | KVO | All host writes | `Progression._keyChangeObserver` → `UpdateSectionStates` |

### Gotchas

- **`Progression.Shared` is set inside `Configure`, not `Awake`/`OnEnable`.** During the small window between `ProgressionManager.OnEnableWithProperties` and `Configure` finishing, `Progression.Shared` is null. UI code that reads `Progression.Shared` before `PropertiesDidRestore` will crash; `GoalsPanelBuilder.Build` defends with a "Milestones not available. Please quit and reload this save." message.
- **No-progression sandbox forces every `ProgressionIndustryComponent.ProgressionDisabled = true`** (`ProgressionManager.cs:62-66`). This is the only way `ProgressionIndustryComponent`s get disabled in sandbox — vanilla `Progression` never runs in sandbox mode at all.
- **`_progression` KVO survives even with no `Progression` configured.** `ProgressionManager.Awake` always registers the KVO; `OnEnableWithProperties` may bail without ever calling `Configure`. The KVO sits there with no observer.
- **`PrerequisitesMet` is a flat `All` over `prerequisiteSections`** (l.366). There's no "any-of" or weighted prereq — for ORs, use a single intermediate section with no deliveries (cost-only) chained from the OR-targets.
- **`ApplySetupPropertyPresets` runs *before* `RestoreProperties`** (`StateManager.cs:1214` then 1233 in the snapshot ingest spine). When loading an existing save, the snapshot's `_progression["progression"]` overrides the preset (preset is queued only on new game; `_gameSetupPropertyPresets` is empty on load). On new game, the preset injects the value into the snapshot dictionary which is then handed to `RestoreProperties` — so the save's `_progression` is fully populated by the time `ProgressionManager.OnEnableWithProperties` reads it.
- **`UnlockedSectionIds` setter rebuilds the entire array** every call (`UnlockedSectionIds = UnlockedSectionIds.Concat(new[]{id}).ToHashSet()` at l.389). Any subscriber to `_progression["unlocked"]` sees a full-array PropertyChange broadcast on every section completion.
- **`Section.PaidCount` and `Section.FulfilledCount` are recomputed from KVO every `UpdateSectionStates` call** (l.244-245). The setters on the Section properties just store on the local MonoBehaviour for UI consumption — the source of truth is `_progression`.
- **`Revert(section)` is recursive** but only via the public `Revert` (l.167) which calls `RevertHelper` then `UpdateSectionStates`. The recursion at l.181 calls `Revert` (not `RevertHelper`), so each recursive call also calls `UpdateSectionStates` and broadcasts `ProgressionStateDidChange` — chatty if you revert the root of a deep prerequisite tree.
- **`CheckSections()` warns about shared `ProgressionIndustryComponent`s** (l.89). Two phases pointing at the same industry component will log an error at `Configure` time. The system still runs but `UpdateSectionStates`'s `dictionary2[ic] = -1` initial then per-phase overwrite means only one phase's `ProgressionDisabled` state will stick per tick — racing.

---

## `Game.Progression.ProgressionManager`

`GameBehaviour` (subclass of `MonoBehaviour`) — uses `RestoreNotifier.RegisterForRestore(EnablePriority=100, this, OnEnableWithProperties)` so it runs **near the front** of the post-restore sequence (higher priority = earlier; see [save-load › RestoreNotifier priorities](save-load.md#restorenotifier-priorities)). This ensures `Progression.Configure` runs before any subscriber that depends on `Progression.Shared`.

```csharp
private Progression[] _progressions;                                       // 13
private KeyValueObject _keyValueObject;                                    // 15
private Progression _current;                                              // 17
public const string ObjectId = "_progression";                             // 19
public const string KeyProgression = "progression";                        // 21
protected override int EnablePriority => 100;                              // 23

private void Awake()                                                       // 25
{
    _progressions = GetComponentsInChildren<Progression>();
    _keyValueObject = gameObject.AddComponent<KeyValueObject>();
    StateManager.Shared.RegisterPropertyObject("_progression", _keyValueObject, AuthorizationRequirement.HostOnly);
}
```

`Awake` registers the `_progression` KVO unconditionally — even in sandbox mode, even with no Progression children. The `OnDestroy` unregisters.

`OnEnableWithProperties` (l.50):
```csharp
string progressionKey = _keyValueObject["progression"].StringValue;
bool flag = !string.IsNullOrEmpty(progressionKey);
if (StateManager.IsSandbox && flag) { Log.Warning(...); flag = false; }
if (!flag) {
    Log.Information("No progression specified.");
    foreach (var c in FindObjectsOfType<ProgressionIndustryComponent>())
        c.ProgressionDisabled = true;     // belt-and-suspenders disable
} else {
    _current = _progressions.FirstOrDefault(p => p.identifier == progressionKey);
    if (_current == null) Log.Error("RR-546 Couldn't find progression {key}", progressionKey);
    else _current.Configure(_keyValueObject);
}
```

### Patch candidates

| Method | Why patch |
|---|---|
| `ProgressionManager.OnEnableWithProperties` | Inject mod progressions, override sandbox handling, fall back to a default progression if id is unknown |
| `ProgressionManager.Awake` | Pre-register additional KVO objects; **must run before** `RegisterPropertyObject("_progression", …)` to avoid duplicate-id conflict |

### Gotchas

- **`_progressions = GetComponentsInChildren<Progression>()` runs in `Awake`** (l.27). Mod-injected `Progression` MonoBehaviours added after `Awake` are not picked up. Add them as children before `Awake`, or patch `Awake`/`OnEnableWithProperties` and re-run the discovery.
- **Sandbox + non-empty `progression` is silently downgraded** to "no progression" with a warning. A custom progression that *should* run in sandbox cannot do so without patching `OnEnableWithProperties`.
- **`EnablePriority = 100`** is the highest in the codebase that I saw. Mods that need to run *before* progression configures must register with priority > 100 via `RestoreNotifier.RegisterForRestore`.

---

## `Section`

A scenario chapter. MonoBehaviour parented under a `Progression`. Discovered via `GetComponentsInChildren<Section>()` in `Progression.Configure`.

```csharp
public string identifier;                                                  // 42
public string displayName;                                                 // 45
[TextArea] public string description;                                      // 48
public Section[] prerequisiteSections;                                     // 52
public DeliveryPhase[] deliveryPhases;                                     // 55
public MapFeature[] enableFeaturesOnUnlock;                                // 57
public MapFeature[] enableFeaturesOnAvailable;                             // 59
public MapFeature[] disableFeaturesOnUnlock;                               // 61
public bool Unlocked { get; set; }                                         // 63 — set by UpdateSectionStates
public bool Available { get; set; }                                        // 65
public int  PaidCount { get; set; }                                        // 67
public int  FulfilledCount { get; set; }                                   // 69
public int  PhaseCount => deliveryPhases.Length;                           // 71
public InterchangeTransfer[] InterchangeTransfers { get; private set; }    // 73 — found in Awake
```

### `DeliveryPhase`

```csharp
[Serializable] public class DeliveryPhase {                                // 12
    public int cost;
    public Delivery[] deliveries;                                          // empty array = unlock-only "purchase" phase
    public ProgressionIndustryComponent industryComponent;                 // null means deliveries[] should be empty
}
```

### `Delivery`

```csharp
[Serializable] public class Delivery {                                     // 24
    public enum Direction { LoadToIndustry, LoadFromIndustry }
    public CarTypeFilter carTypeFilter;                                    // accepted car types for ordering
    public int count;                                                      // how many cars of this delivery
    public Load load;                                                      // commodity
    public Direction direction;
}
```

`LoadToIndustry`: empty cars are ordered to the industry, *loaded* there externally (or pre-loaded), then unloaded by the `ProgressionIndustryComponent`. `LoadFromIndustry`: empties ordered, *loaded* by the industry component, then ordered away.

### `ApplyCompleted`

```csharp
public void ApplyCompleted()                                               // 80
{
    foreach (var t in InterchangeTransfers) t.Apply();
}
```

`InterchangeTransfer.Apply` (`InterchangeTransfer.cs:14`) calls `OpsController.Shared.RewriteWaybills(from.Industry.identifier, to.Industry.identifier)` — global rewrite of every existing waybill from one industry to another. This is the "you've completed the section, now route the freight to the new industry" mechanism. **Side effect on every car in the world**; not scoped to anything.

### Patch candidates

| Method | Why patch |
|---|---|
| `Section.ApplyCompleted` | Run mod-side hooks on section completion (one-shot at `PhaseCompleted` when `FulfilledCount == PhaseCount`) |
| Override `Section` | Subclass and add fields/Awake logic; vanilla `Progression.Configure` calls `GetComponentsInChildren<Section>()` so subclasses are picked up |

### Gotchas

- **`Section.PaidCount`/`FulfilledCount` are *not* the source of truth.** They're cached from `_progression["paid-<id>"]` / `["fulfilled-<id>"]` in `UpdateSectionStates`. Writing them directly only updates UI; the next `UpdateSectionStates` call overwrites.
- **`Section.Unlocked` / `Available`** ditto — derived properties.
- **`InterchangeTransfer.Apply` calls `RewriteWaybills` globally**, retroactively rewriting waybills already on cars. If a player is running a load and a section completes mid-trip, that car's destination changes. This is intentional but surprising.
- **`prerequisiteSections` references must be in the same `Progression`** (since they're MonoBehaviour references resolved at scene-bake time). Cross-progression prereqs aren't supported.

---

## `MapFeature`

A unit of unlockable content. Identifier-keyed; manifest of side effects.

```csharp
public string identifier;                                                  // 9
public string displayName;                                                 // 12
public string description;                                                 // 14
public bool defaultEnableInSandbox;                                        // 16
public MapFeature[] prerequisites;                                         // 20 — declarative; NOT enforced by MapFeatureManager
public string[] trackGroupsEnableOnUnlock;                                 // 24 — Graph.SetGroupEnabled
public string[] trackGroupsAvailableOnUnlock;                              // 27 — Graph.SetGroupAvailable
public GameObject[] gameObjectsEnableOnUnlock;                             // 31 — SetActive(unlocked)
public Area[] areasEnableOnUnlock;                                         // 34 — every Industry + PassengerStop in area gets ProgressionDisabled = !unlocked
public Industry[] unlockExcludeIndustries;                                 // 37 — exempted industries within the areas
public Industry[] unlockIncludeIndustries;                                 // 40 — extra industries outside the areas
public IndustryComponent[] unlockIncludeIndustryComponents;                // 42
public bool Unlocked { get; set; }                                         // 56 — set by MapFeatureManager.UpdateFeatureForUnlocked
```

`MapFeature.Unlocked` is set by `MapFeatureManager`, never by the feature itself.

### `prerequisites` is declarative-only

The `prerequisites` array is **not consulted by `MapFeatureManager`** — it's purely for content-author hygiene. Sequencing is enforced by which `Section` enables which feature.

### Patch candidates

| Method | Why patch |
|---|---|
| Subclass `MapFeature` | Add custom side-effect fields; integrate with mod systems by patching `MapFeatureManager.UpdateFeatureForUnlocked` to read your fields |

---

## `MapFeatureManager`

The hub between `Progression` and the world. Owns the `mapFeatures` HostOnly KVO. Singleton via `MapFeatureManager.Shared` (lazy `FindObjectOfType`). `[DefaultExecutionOrder(-1)]` so `Awake` runs before normal MonoBehaviours.

### Lifecycle

```csharp
private void Awake()                                                       // 66
{
    _features = GetComponentsInChildren<MapFeature>();
    var kvo = gameObject.AddComponent<KeyValueObject>();
    StateManager.Shared.RegisterPropertyObject("mapFeatures", kvo, AuthorizationRequirement.HostOnly);
    _keyValueObject = kvo;
}
```

`OnEnable` is empty. `OnDisable` disposes the `_keyChangeObserver`. The observer is *only* wired by `HandleSnapshotProperties` — meaning **before snapshot ingest, mapFeatures KVO writes are not observed**.

### `mapFeatures` KVO key map

| Key | Type | Notes |
|---|---|---|
| `features` | `dict<string, bool>` | `featureId → unlocked`. Single fat key — every feature lives here |

`featureId` is `MapFeature.identifier`. Bool true = unlocked, false (or absent) = locked.

### `HandleSnapshotProperties` — the special-case ingest

```csharp
public void HandleSnapshotProperties(Dictionary<string, Value> properties, SetValueOrigin origin)  // 97
{
    _keyValueObject.ResetData(properties, origin);
    UpdateCachedEnabledFeatures();             // _cachedFeatureEnables = current FeatureEnables
    _keyChangeObserver?.Dispose();
    _keyChangeObserver = _keyValueObject.Observe("features", delegate
    {
        Dictionary<string, bool> newValue = FeatureEnables;
        HandleFeatureEnablesChanged(_cachedFeatureEnables, newValue, initial: false);
        _cachedFeatureEnables = FeatureEnables;
    }, callInitial: false);
    HandleFeatureEnablesChanged(new Dictionary<string, bool>(), _cachedFeatureEnables, initial: true);
}
```

Called by `StateManager.HandleSnapshotMapFeatures` at the **very front** of `PopulateFromRemoteSnapshot` (`StateManager.cs:1165`), *before* `HandleSnapshotSwitches`/`HandleSnapshotCars`/etc. **`mapFeatures` is the only KVO restored this early in the snapshot ingest.** This ordering matters because:

1. `HandleFeatureEnablesChanged` may call `Graph.SetGroupEnabled/Available` — track topology must be reconciled before cars/switches restore so cars land on enabled track segments.
2. The areas/industries `ProgressionDisabled` toggle must propagate before `RestoreProperties` populates per-industry KVOs (some industry behavior is gated by `ProgressionDisabled`).

After this special call, `mapFeatures` is *also* in the generic `RestoreProperties` fan-out — but `PropertyObjectManager.RestoreProperties` calls `kvo.ResetData(dict, origin)` again. Because the data is identical, the diff in `HandleFeatureEnablesChanged` is empty (`oldValue == newValue` check at l.140 short-circuits).

### Mixed Local/Remote origin pattern

```csharp
MapFeatureManager.Shared.HandleSnapshotProperties(
    properties: PropertyValueConverter.SnapshotToRuntime(value),
    origin: (!IsHost) ? SetValueOrigin.Remote : SetValueOrigin.Local);   // StateManager.cs:1211
```

This is the same pattern as the generic `RestoreProperties`: host uses `Local` origin (which echoes through the `OnSetValueLocal` → `PropagateSetValueLocal` → broadcast chain), client uses `Remote` (no echo). Inside `TransactionScope`, the host's resulting PropertyChange sends are batched into the snapshot-restore Transaction.

**Why call `HandleSnapshotProperties` separately when `RestoreProperties` would also fan it in?** Because `MapFeatureManager` needs to do *more than just store the values* — it needs to wire the `_keyChangeObserver` and run `HandleFeatureEnablesChanged` with the diff against the *initial* (empty) state. The generic `RestoreProperties` only calls `kvo.ResetData(dict, origin)` and won't trigger the side-effect computation.

This is **the single non-obvious "extra step" in the snapshot ingest** for mods to know about. If you write a mod system with a similar "I need to react to bulk state ingest, not just per-key writes," follow this template:
1. Register your KVO normally.
2. Add an explicit `HandleSnapshotProperties` method.
3. Patch `StateManager.PopulateFromRemoteSnapshot` to call it inside the `TransactionScope`, before `RestoreProperties`.

### `HandleFeatureEnablesChanged` — the side-effect machine

```csharp
private void HandleFeatureEnablesChanged(Dictionary<string, bool> oldValue, Dictionary<string, bool> newValue, bool initial)  // 112
```

Per `MapFeature` in `_features`:
- Resolve "current value" — `newValue[id]` if present, else `(defaultEnableInSandbox && IsSandbox)` as fallback.
- Resolve "old value" — same logic against `oldValue` (for `initial: true`, oldValue is empty so all features compare as default-fallback).
- If unchanged on a non-initial call: skip.
- Else add to `enabled` or `disabled` set.

Then:
1. **Graph groups:** for each enabled feature, `UpdateFeatureGraphGroups(unlocked: true, graph)`; for each disabled, `unlocked: false`. Calls `Graph.SetGroupEnabled` and `Graph.SetGroupAvailable` per group string. If any group changed, `Graph.RebuildCollections()` + `MapFeatureChangedGraph` Messenger event + scheduled `TrackObjectManager.Instance.Rebuild()` next frame.
2. **Industries / passenger stops:** `UpdateFeatureForUnlocked` walks `areasEnableOnUnlock`'s industries + passenger stops, plus `unlockIncludeIndustries`/`unlockIncludeIndustryComponents`, minus `unlockExcludeIndustries`. Sets `IProgressionDisablable.ProgressionDisabled = !unlocked` on each. Calculates `externallyExcluded` to handle "this industry should stay disabled because it's exclusively in *another* feature that's still locked."
3. **GameObjects:** `gameObject.SetActive(unlocked)` for each in `gameObjectsEnableOnUnlock`.
4. If any industry's `ProgressionDisabled` flipped, schedule `IndustriesDidChange` Messenger send next frame.

### `SetFeatureEnabled` / `SetFeatureEnables`

```csharp
public void SetFeatureEnabled(string featureId, bool unlocked)             // 211
public void SetFeatureEnabled(MapFeature feature, bool unlocked)           // 221  AssertIsHost
public void SetFeatureEnables(Dictionary<string, bool> featureEnables)     // 288
```

All host-only. `SetFeatureEnables` is the batch path used by `Progression.UpdateSectionStates` — single KVO write to `mapFeatures["features"]`, observers see one PropertyChange. The single-feature setters do read-modify-write per call, so calling `SetFeatureEnabled` in a loop generates N PropertyChanges. **Use `SetFeatureEnables` for batch updates.**

`MapFeatureManager.SetFeatureEnabled` is also called directly by `Progression.Configure` for `enableFeaturesAtStart` (host-only loop).

### Patch candidates

| Method | Why patch |
|---|---|
| `MapFeatureManager.HandleFeatureEnablesChanged` | The diff applicator. Postfix to add side effects beyond the vanilla list (e.g., enable a custom subsystem when a feature unlocks) |
| `MapFeatureManager.UpdateFeatureForUnlocked` | Per-feature application. Patch to handle a custom `MapFeature` subclass with extra unlock targets |
| `MapFeatureManager.UpdateFeatureGraphGroups` | If you want to add custom graph-group dispatch (e.g., a mod-side track manager that mirrors Graph) |
| `MapFeatureManager.HandleSnapshotProperties` | Hook the ingest entry point — the natural spot to react to bulk feature state on load/late-join |
| `MapFeatureManager.SetFeatureEnables` | Intercept batch writes; e.g., to layer in mod-side feature derivations |
| `MapFeatureManager.Awake` | Re-discover features after Awake (vanilla discovers in `GetComponentsInChildren` once) |

### Related Messenger events

| Event | Source | Consumers |
|---|---|---|
| `MapFeatureChangedGraph` | After `Graph.RebuildCollections()` (l.189) | `TrackObjectManager.Rebuild()` scheduled next frame; signals subsystem |
| `IndustriesDidChange` | After feature flip caused industry `ProgressionDisabled` change (l.201) | Industries-Ops UI, Locations panel |

### Gotchas

- **`_keyChangeObserver` is only wired in `HandleSnapshotProperties`.** Before snapshot ingest, KVO writes go through unobserved. If you patch in mod-side feature toggles before snapshot ingest, they won't trigger `HandleFeatureEnablesChanged`.
- **`defaultEnableInSandbox` only takes effect when the key is missing** from `FeatureEnables`. Once the host writes `features["myFeature"] = false` once, the default no longer applies. **Sandbox saves accumulate explicit-false entries** as the player toggles things off via the Map Features UI.
- **`SetFeatureEnabled` requires host** — the assertion is at l.223. The Map Features UI in sandbox is host-only-visible (`ShouldShowMapFeatures = IsSandbox && IsHost` at `SettingsPanelBuilder.cs:45`).
- **`Graph.SetGroupEnabled`/`SetGroupAvailable` only change `enabledGroupIds`/`availableGroupIds` lists** — they don't trigger rebuild. `MapFeatureManager` explicitly calls `Graph.RebuildCollections()` after.
- **`UpdateFeatureForUnlocked` mutates `_progressionDisablabledSet`** to track "I've touched this disablable" — used to detect whether `ProgressionDisabled` actually changed. The set is process-global on the manager; it persists across feature toggles. Mod-injected disablables added later won't be in the set on first toggle.
- **`externallyExcluded` is computed from `_features.Where(!Unlocked || disabledFeatures.Contains)`**. Industries that are in `unlockIncludeIndustries` of a *locked* feature are excluded from being unlocked by *other* features. This ensures a single source-of-truth feature owns each industry.
- **`HandleFeatureEnablesChanged` runs synchronously inside the KVO observer.** Each PropertyChange to `mapFeatures.features` triggers a full re-walk of `_features`, plus possibly `Graph.RebuildCollections()`. Patches that call `SetFeatureEnabled` rapidly will pay this cost per write — use `SetFeatureEnables` instead.
- **`MapFeatureManager.Shared` lazy-finds via `FindObjectOfType`.** Returns null if no MapFeatureManager exists in scene (e.g., a custom mod scene). Always null-check.
- **There is no "hide the UI for currently-locked features" filter** — `BuildMapFeatures` shows all `AvailableFeatures`. In sandbox the user can toggle anything regardless of progression state.

---

## `ProgressionIndustryComponent` (cross-link)

Documented in detail in [Industries & Ops › ProgressionIndustryComponent](industries-ops.md#progressionindustrycomponent--industrycomponent).

Summary as it pertains to progression:
- One `ProgressionIndustryComponent` per `DeliveryPhase` that has deliveries.
- `Progression.UpdateSectionStates` calls `Configure(section, phaseIndex, phase, onComplete, _progression-kvo)` only on the *active* phase's component. Other phases' components get `ProgressionDisabled = true`.
- `OrderCars(ctx)` (called by Industry tick) places orders for the missing count.
- `Service(ctx)` (per-tick) loads/unloads cars whose waybills match `<sectionId>.<phaseIdx>.<deliveryIdx>` tag. On load completion, increments `_progression["indRecv"][tag]` and orders away.
- `CheckForCompletion` invokes `_onComplete` (which is `() => PhaseCompleted(section, phaseIndex)`).
- All counters are on the `_progression` KVO, **not on the industry's KVO**.

---

## `SetupDescriptor`

Per-`setupId` scene preset for a new game. Found via `FindObjectsOfType<SetupDescriptor>().FirstOrDefault(sd => sd.identifier == setupId)` in `StateManager.GetSetupDescriptor`.

```csharp
public string identifier;                                                  // 31 — selection key
public int initialMoney;                                                   // 33
public SpawnPoint spawnPoint;                                              // 35 — camera default position
public CarPlacement[] placements;                                          // 37
public bool showTutorial;                                                  // 39

[Serializable] public class CarPlacement {
    public string[] carIdentifier;                                         // car definition ids; one cut
    public TrackMarker marker;                                             // location to place at
    public bool wreck;                                                     // → derailment=0.5, condition=0.7
    [Range(0,1)] public float oiled = 1f;
    [Range(0,1)] public float loadPercent;
    public Load load;                                                      // for engines/tenders: fuel/water; otherwise the loaded commodity
}
```

`SetupDescriptor` is **independent of `Progression`** — they're paired by convention (e.g., `progressionId="ewh"` pairs with `setupId="ewh-steam"` per `NewGameMenu.SelectProgressionId` at `NewGameMenu.cs:130`), but you could mix and match.

`CompanyModeSetup.Setup(trainController, setupDescriptor)` (`Game.State/CompanyModeSetup.cs:21`) is the coroutine that applies it:
1. `WaitForSeconds(5f)` — gives the world time to finish loading.
2. `ApplyToBalance(initialMoney, …)` if > 0.
3. For each `CarPlacement`: `trainController.PlaceTrain(location, descriptors, …)` with the per-cut descriptors mutated for `oiled`, `wreck`, and `load`.

**Only triggered for new games on host** — guarded by `_trainController.Cars.Count == 0 && _storage.Balance == 0` in `ApplyNewGameSetup` (`StateManager.cs:405`). Loaded saves skip this entirely.

### Gotchas

- **The 5-second delay is hardcoded** — no config. Mods that depend on starter cars existing must wait at least 5s after `MapDidLoadEvent`.
- **`SetupDescriptor` is *not* host-replicated.** Clients never see it; they receive the resulting cars via the snapshot. So mod-side hooks into setup must run host-only.
- **`wreck = true` writes per-car KVO directly via `CarDescriptor.Properties`** — `_derailment = 0.5`, `_condition = 0.7`. These are the HostOnly per-car wear keys (see [wear-durability › KVO-backed properties](wear-durability.md#kvo-backed-properties-hostonly-prefix-_-or-oiledhotbox)).
- **`SetupDescriptor.identifier` is stored in `_game["setupId"]`** (`GameStorage.SetupId`). On reload, `GetSetupDescriptor` re-locates the matching scene object, but `CompanyModeSetup.Setup` doesn't re-run (Cars > 0).

### Patch candidates

| Method | Why patch |
|---|---|
| `CompanyModeSetup.Setup` | Add starter equipment beyond the descriptor; or replace the placement logic |
| `StateManager.GetSetupDescriptor` | Inject mod-defined SetupDescriptors not parented in the scene |
| `StateManager.ApplyNewGameSetup` | Pre-empt or augment the new-game setup flow |

---

## `IProgressionDisablable`

```csharp
public interface IProgressionDisablable {                                  // Model.Ops/IProgressionDisablable.cs
    bool ProgressionDisabled { get; set; }
}
```

Implementers in vanilla:
- `Model.Ops.Industry` (`Industry.cs:38`) — `IsVisible`, `Service`, daily report all gate on this
- `Model.Ops.IndustryComponent` (`IndustryComponent.cs:78`) — per-component disable
- `Model.Ops.PassengerStop` (`PassengerStop.cs:151`) — passenger stops not in unlocked features go dark

Set only by `MapFeatureManager.UpdateFeatureForUnlocked` (l.260) and `ProgressionManager.OnEnableWithProperties`'s no-progression bail (l.65). **Not KVO-backed** — purely a runtime field. Re-derived from the `mapFeatures` KVO state on every snapshot ingest (host-side path) and feature change.

This is the **mechanism by which a feature unlock cascades into "this industry now exists"**. Industries are always present in the scene; `ProgressionDisabled` controls whether they show up in UI, accept service, contribute to reputation, etc.

### Patch candidates

| Method | Why patch |
|---|---|
| `Industry.ProgressionDisabled` setter (auto-property) | Patch property setter to fire mod events |
| Implementations in mod types | Add new implementers; `MapFeatureManager.UpdateFeatureForUnlocked` casts to `IProgressionDisablable` so any new implementer benefits if surfaced through `unlockIncludeIndustryComponents` or via patch |

---

## `ReputationTracker` interaction

`ReputationTracker.PhaseDiscount()` (`Game.Reputation/ReputationTracker.cs:519`) is called by `Progression.CostForPhase` to compute the discount. Tiered by `Reputation`:

| Reputation | Discount |
|---|---|
| > 0.95 | 25% |
| > 0.90 | 20% |
| > 0.85 | 15% |
| > 0.80 | 10% |
| > 0.70 | 5% |
| else | 0% |

Reputation itself is computed daily from passenger network coverage (30%), passenger condition (10%), freight performance (40%), safety (30% derailments). See `ReputationTracker.UpdateReputation` (l.183).

**Discount applies only to the cost paid via `ApplyToBalance` — the balance check uses the discounted amount** (`Progression.cs:206-210`).

---

## `ProgressionStartPhase` IGameMessage

```csharp
[MinimumAccessLevel(AccessLevel.Officer)]                                  // ProgressionStartPhase.cs:6
[MessagePackObject(false)]
public struct ProgressionStartPhase : IGameMessage
{
    [Key(0)] public string SectionIdentifier { get; set; }
    [Key(1)] public int    PhaseIndex { get; set; }
}
```

Registered for MessagePack via `[Union(410, typeof(ProgressionStartPhase))]` on `IGameMessage` (`IGameMessage.cs:48`).

Dispatched at `StateManager.cs:730`:
```csharp
Game.Progression.Progression.Shared.HandlePayToStartPhase(
    progressionStartPhase.SectionIdentifier, progressionStartPhase.PhaseIndex, sender);
```

### MP authority

- **Officer or higher** can send it (`MinimumAccessLevel(Officer)`). Trainmaster can; Crew/Dispatcher cannot.
- The host-side handler (`HandlePayToStartPhase`) early-returns `if (!StateManager.IsHost)`. Defense in depth — message-level auth would already have rejected the message at `HostManager.RoutingForMessage` if a non-host tried to handle it.
- **Failure feedback:** wrapped in try/catch. `DisplayableException.DisplayMessage` is forwarded via `Multiplayer.SendError(sender, message)`. Generic exceptions get `"Unable to start phase"`. Other clients see no error.
- `Multiplayer.Broadcast(...)` (l.215, 222) sends a chat-style notification to all players on success.

### Patch candidates

| Method | Why patch |
|---|---|
| Add patch on `Progression.HandlePayToStartPhase` | The chokepoint for any message-driven progression intent |
| Patch `StateManager.Handle` (around l.730) | Add new IGameMessage types adjacent to `ProgressionStartPhase` (vanilla has no extension hook for new messages) |

---

## Save / load survival

`_progression` and `mapFeatures` are both registered KVOs and **ride the snapshot for free** via `PropertyObjectManager.PopulateSnapshotForSave`. See [save-load › Route 1 KVO objects](save-load.md#route-1--kvo-objects-in-snapshot).

### What's serialized

| KVO | Keys | Purpose on load |
|---|---|---|
| `_progression` | `progression`, `unlocked` array, `paid-<id>` ints, `fulfilled-<id>` ints, `indRecv` dict | Restored before `ProgressionManager.OnEnableWithProperties` runs; observed by `Progression.Configure`'s `ObserveKeyChanges` |
| `mapFeatures` | `features` dict | Special-cased ingest first thing in snapshot replay; observed by `MapFeatureManager._keyChangeObserver` (wired during the special ingest itself) |

### Migration

Single migration in `WorldStore.Migrate(Snapshot)` (`WorldStore.cs:101`) related to progression:

```csharp
if (value2["mode"].IntValue == 1 && snapshot.Properties.TryGetValue("_progression", out var value3))
    value3["progression"] = new StringPropertyValue("ewh");
```

**Translation:** any company-mode save with a `_progression` properties dict gets `progression = "ewh"` forcibly set. This is the migration that ensured pre-`ewh` saves worked when the campaign got its identifier. **It runs on every load, unconditionally** — overwriting any other progression id in a company-mode save. If a future build adds new progressions, this migration will need to be updated to skip non-default ids. As of vanilla, only `"ewh"` exists, so the rewrite is a no-op for current saves.

`mapFeatures` has no migration. Per-feature defaults (`defaultEnableInSandbox`) only apply when the key is missing — once a save has any `features` dict, all toggles are explicit.

### Cross-mod gotcha

Because `_progression` is HostOnly, **a save loaded with a host that has different `Progression`/`Section` definitions** (e.g., mod-altered prereqs) can land in inconsistent states:
- `Progression.UpdateSectionStates` sets `Available` based on prerequisite-section graph at runtime.
- If the saved `unlocked` list contains an id that no longer exists in `Sections`, `SectionForId` returns null → `hashSet.Contains(section)` evaluates false against a null → quietly excluded.
- `paid-`/`fulfilled-` for orphaned section ids stay in the KVO forever — they're never garbage-collected. Save-bloat over many mod swaps.

---

## Sandbox vs scenario

| Concern | Sandbox | Scenario (Company mode) |
|---|---|---|
| `_progression["progression"]` | Empty/unset; warned and ignored if set | Required (default `"ewh"`) |
| `Progression.Configure` | Never called (ProgressionManager bails) | Called for matching Progression child |
| `ProgressionIndustryComponent.ProgressionDisabled` | All set to `true` (l.65) | Set per active phase |
| `MapFeatureManager` | Always present + KVO registered | Same |
| Map Features UI tab | Visible to host (`ShouldShowMapFeatures = IsSandbox && IsHost`) | Hidden |
| `MapFeature.defaultEnableInSandbox` | Applied as default when key missing in `features` dict | Ignored (sandbox-only flag) |
| `enableFeaturesAtStart` (Progression) | N/A (Progression not configured) | Host applies in `Configure` (l.62-69) |
| Section UI | "Milestones not available" (Progression.Shared null) | `GoalsPanelBuilder` populated |
| `ProgressionStartPhase` message | Would no-op on `Progression.Shared` null | Routed to handler |
| `SetupDescriptor` | Not used (no progression id selected in NewGameMenu) | Required for setup, optional for fresh sandbox |
| `CompanyModeSetup.Setup` | Skipped (no setupDescriptor) | Runs on new game |

**Sandbox-mode `MapFeatureManager` still actively gates industries and track groups** based on `defaultEnableInSandbox` and any host-toggled values. This is the only system where sandbox-vs-scenario meaningfully diverges in *what runs* — most other systems just have looser checks.

---

## MP authority summary

| Action | Who | Path |
|---|---|---|
| Pay for a phase (`ProgressionStartPhase`) | Officer+ client → host | IGameMessage `MinimumAccessLevel(Officer)` |
| Direct `Progression.Advance(section)` | Host only | `AssertIsHost`; called from `[ContextMenu]` only |
| Direct `Progression.Revert(section)` | Host only | Calls `RevertHelper` which writes HostOnly KVO |
| `MapFeatureManager.SetFeatureEnabled(*)` | Host only | `AssertIsHost` |
| `_progression` KVO writes | Host only | `RegisterPropertyObject(..., HostOnly)` |
| `mapFeatures` KVO writes | Host only | Same |
| Map Features UI toggle | Host (sandbox only) | UI gated by `ShouldShowMapFeatures` |
| Read `Progression.Shared.Sections[i]` | Anyone | Sections are scene MonoBehaviours, per-machine |
| Read `Section.Available`/`Unlocked`/etc. | Anyone | Computed from KVO on every change |

**There is no client-side `Revert` or `Advance` IGameMessage.** Hosts can debug-skip; clients can only pay (and only if Officer).

---

## Mod patch points (high-value)

### Custom progression sections

The cleanest path:
1. Define a new `Progression` MonoBehaviour with a unique `identifier` parented at the same scene root as the vanilla `Progression`.
2. Patch `ProgressionManager.Awake` (postfix) to re-run `_progressions = GetComponentsInChildren<Progression>()` after vanilla's call, picking up your new node.
3. Define `Section` children with prereqs, `MapFeature[]`, and `DeliveryPhase[]` arrays.
4. Reference `MapFeature` instances from the existing `MapFeatureManager` (the manager auto-discovers via `GetComponentsInChildren<MapFeature>()`).

**Easier alternative:** patch `Progression.Configure` postfix to inject extra `Section` children at runtime, then re-call `UpdateSectionStates`. The `_progression` KVO will populate `paid-`/`fulfilled-` keys for any new section id automatically.

### Custom unlock conditions

Vanilla unlock = "all phases fulfilled." To add a custom condition (e.g., reputation > X, time > Y, custom achievement), patch `Progression.PrerequisitesMet`:

```csharp
[HarmonyPatch(typeof(Progression), "PrerequisitesMet")]
class PrereqPatch {
    static void Postfix(Section section, ref bool __result) {
        if (__result && MyMod.HasCustomGate(section)) __result = false;
    }
}
```

For a mid-section gate (don't allow `PayToStartPhase` even when Available), patch `Progression.HandlePayToStartPhase` prefix and `Multiplayer.SendError(sender, "...")` + return.

### Custom map features

Two routes:
1. **New `MapFeature` GameObject:** Add as a child of `MapFeatureManager` *before* `Awake` runs. The vanilla manager discovers them via `GetComponentsInChildren<MapFeature>()`. Reference standard track-group ids and existing `Area`s.
2. **Subclass `MapFeature` for new side effects:** Add new fields. Patch `MapFeatureManager.UpdateFeatureForUnlocked` to read your fields and apply mod-side effects.

### Custom IProgressionDisablable

Implement the interface on a mod MonoBehaviour. To get auto-disabled by a `MapFeature`:
- Add it as an `IndustryComponent` and reference via `MapFeature.unlockIncludeIndustryComponents`.
- Or patch `MapFeatureManager.UpdateFeatureForUnlocked` to walk your mod's registry and apply `ProgressionDisabled` matching the feature state.

### Hooking section completion

The cleanest signal:
```csharp
Messenger.Default.Register<ProgressionStateDidChange>(this, _ => {
    foreach (var section in Progression.Shared.Sections) {
        if (section.Unlocked && !_seenUnlocked.Contains(section.identifier)) {
            _seenUnlocked.Add(section.identifier);
            OnSectionUnlocked(section);
        }
    }
});
```

Or patch `Progression.PhaseCompleted` postfix for a per-phase callback:
```csharp
[HarmonyPatch(typeof(Progression), "PhaseCompleted")]
class PhaseCompletedPatch {
    static void Postfix(Section section, int phaseIndex) {
        MyMod.OnPhaseDone(section, phaseIndex);
    }
}
```

### Hooking map feature changes

```csharp
MapFeatureManager.Shared.ObserveFeaturesChanged(() => {
    foreach (var feat in MapFeatureManager.Shared.AvailableFeatures) {
        // feat.Unlocked is the runtime flag
    }
}, callInitial: true);
```

`ObserveFeaturesChanged` (`MapFeatureManager.cs:280`) returns an `IDisposable` — wraps the `_keyValueObject.Observe("features", ...)` call. **This only fires after `HandleSnapshotProperties` has wired the underlying `_keyChangeObserver`** — i.e., after snapshot ingest. Subscribe in `PropertiesDidRestore` or via `RestoreNotifier.RegisterForRestore`.

### Custom IGameMessages for progression

Vanilla has no extension hook in `StateManager.Handle`. Options:
1. Patch `StateManager.Handle` to add a branch for your message type.
2. Use `PropertyChange` on `_progression` directly (HostOnly so client→host writes get rejected with the corrective bounce-back from `HostHandlePropertyChangeRejected`) — not viable for client-initiated actions.
3. Define a mod-side request flow using your own KVO with non-HostOnly auth (e.g., `MinimumLevelOfficer`), and have the host observe and translate to `_progression` writes.

---

## Cross-references

- [State Manager § snapshot/late-join](state-manager.md#snapshot--late-join) — where `HandleSnapshotMapFeatures` and `RestoreProperties` live in the ingest spine.
- [State Manager § dispatcher chain](state-manager.md#the-dispatch-chain-statemanagerhandle-statemanagercs482) — `ProgressionStartPhase` dispatch at l.730.
- [Save/Load § Restore order summary](save-load.md#restore-order-summary) — where mapFeatures and `_progression` land in the load order.
- [Save/Load § Migration](save-load.md#migration) — the single `progression="ewh"` migration.
- [Save/Load § Route 1 KVO objects](save-load.md#route-1--kvo-objects-in-snapshot) — pattern for mod-added persistent state that mirrors `_progression`.
- [Industries & Ops › ProgressionIndustryComponent](industries-ops.md#progressionindustrycomponent--industrycomponent) — full coverage of the per-component delivery logic.
- [Track Topology § Group enable/availability](track-topology.md) — how `Graph.SetGroupEnabled/Available` propagates to displayed track.
- [Track Topology › `MapFeature` + scene baking](track-topology.md) — group-id wiring on track segments.
