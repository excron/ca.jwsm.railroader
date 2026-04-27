# Track Topology — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Signals & Dispatch](signals-dispatch.md)

The track world is an authored, editor-baked Unity scene graph: `TrackNode`s (`MonoBehaviour`) are joined pairwise by `TrackSegment`s (also `MonoBehaviour`s with bezier curves between two nodes), and every other system addresses positions via the `Location` value type (segment + distance + which-end-the-distance-is-from). A singleton `Graph` indexes everything by string id, decodes which 3-way nodes are *switches* (and which incident segment is the "enter"), runs all forward/back walks, and exposes an A* pathfinder via the `Track.Search` namespace. **Topology is host-shared by virtue of being baked into the scene** — clients see the same nodes/segments because the scene is identical. Switch *position* is host-authoritative and synced through a `SetSwitch` game message; **track-group enable/availability** is the only data-driven runtime topology change, and it is driven by `MapFeature` unlocks (a `MapFeatureManager` host KVO that calls `Graph.SetGroupEnabled`/`SetGroupAvailable` and triggers `Graph.RebuildCollections`).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Track.Graph` (`Shared`) | `Track/Graph.cs:15` | Singleton index of all nodes/segments/spans + walk/lookup API |
| `Track.TrackNode` | `Track/TrackNode.cs:11` | Node MonoBehaviour; holds `isThrown`, optional `turntable`, fires `SwitchThrownDidChange` |
| `Track.TrackSegment` | `Track/TrackSegment.cs:11` | Segment MonoBehaviour; bezier curve, `priority`, `speedLimit`, `trackClass`, `groupId` |
| `Track.Location` (struct) | `Track/Location.cs:6` | `(segment, distance, end)` — universal address. **Not** a Unity object; safe to copy |
| `Track.TrackSpan` | `Track/TrackSpan.cs:11` | A run between two `Location`s (lower/upper); used for blocks, industries |
| `Track.TrackMarker` | `Track/TrackMarker.cs:9` | A point-on-track tag (Generic/Signal/Flare/Crossing/PassengerStop) registered in `Graph` |
| `Track.Search.RouteSearch.FindRoute` | `Track.Search/RouteSearch.cs:128` | A* over (location, node, direction) tuples; consumed by AutoEngineer & spans |
| `Game.Messages.SetSwitch` | `Game.Messages/SetSwitch.cs` | **HostOnly** wire message; flips `TrackNode.isThrown` |
| `Game.Messages.RequestSetSwitch` | `Game.Messages/RequestSetSwitch.cs` | Crew-level client→host request to throw a switch |
| `TrainController.HandleRequestSetSwitch` | `TrainController.cs:1356` | Host-side validator; rejects if car-on-switch or CTC-locked |
| `Game.Progression.MapFeatureManager` | `Game.Progression/MapFeatureManager.cs:18` | Drives runtime topology by toggling `Graph` group enable/availability |

---

## Topology spine: nodes ↔ segments ↔ Graph

```
TrackNode (id, transform, isThrown, [turntable])
   │  appears as a/b on
   ▼
TrackSegment (id, a:TrackNode, b:TrackNode, priority, speedLimit, trackClass, style, groupId, [turntable])
   │  bezier from a → b, sampled by BezierDistanceParameterCache
   │  curve invalidated if either endpoint node moves
   ▼
Graph (singleton, [DefaultExecutionOrder(-1)])
   │  Awake → RebuildCollections (Graph.cs:158)
   │  ├── nodes  : Dict<string, TrackNode>      (by id)
   │  ├── segments: Dict<string, TrackSegment>  (by id)  – filtered by groupId/enabledGroupIds
   │  ├── spans  : Dict<string, TrackSpan>      (by id)
   │  ├── _nodeConnectionsCache: Dict<nodeId, List<TrackSegment>>     ← rebuilt lazily
   │  ├── _decodedSwitchCache:   Dict<nodeId, DecodedSwitchInfo>      ← what's enter/normal/diverging
   │  ├── _cachedReachableSegments: ((seg, end)) → (normal, reversed)
   │  ├── _segmentCurveCache (LineCurve approximations for hit-testing)
   │  └── _curvatureSampleCache: 5-byte curvature samples per segment
   │
   └── fires Messenger.Default.Send(GraphDidRebuildCollections) on any rebuild
```

**Identity rules:**
- Node `id` is unique and globally registered via `IdGenerator.TrackNodes.Add` in `TrackNode.Awake` (`TrackNode.cs:54`). Duplicate ids log error and skip the duplicate (`Graph.cs:215`).
- Segment `id` likewise via `IdGenerator.TrackSegments` (`TrackSegment.cs:88`).
- Spans live as `MonoBehaviour`s anywhere in the hierarchy; `Graph` finds them via `FindObjectsOfType<TrackSpan>()` during `RebuildCollections` (`Graph.cs:187`).
- `TrackMarker`s self-register in `OnEnable` via `Graph.RegisterTrackMarker` (`TrackMarker.cs:100`); if `Graph.HasPopulatedCollections == false`, they queue in `_pendingTrackMarkers` and are flushed at end of `RebuildCollections` (`Graph.cs:193-196`).

### `Graph.RebuildCollections` — the bulldozer

Called from `Awake`, **and** any time:
- `MapFeatureManager` flips a feature graph-group enable (`MapFeatureManager.cs:170`),
- mods/code call it directly.

It clears every cache, re-scans children for nodes/segments, re-runs `FindObjectsOfType<TrackSpan>`, and re-wires `_pendingTrackMarkers`. **Anyone holding cached node/segment references across this call will get stale data** — but because nodes/segments are scene-baked Unity objects, the references typically still resolve. The danger is `_nodeConnectionsCache`/`_decodedSwitchCache` lookups returning *different* segments after a rebuild changes which groupIds are enabled.

`HasPopulatedCollections` flips to true at the end of the first rebuild (`Graph.cs:191`). Code that runs in MonoBehaviour `Awake` of arbitrary scene objects must check this before walking the graph.

### Patch candidates (Graph)

| Method | Why patch |
|---|---|
| `Graph.RebuildCollections` | Postfix to inject mod-side derived data (custom indices, signal/block precomputation). |
| `Graph.AddSegment(TrackSegment, bool)` | Per-segment add hook; respects groupId filtering — runs once per visible segment per rebuild. |
| `Graph.LocationByMoving` | The universal forward/back walk. Patch with care — hot path; called from physics, AI, route search. |
| `Graph.LocationFrom` | Single switch-traversal step. Override here to inject custom switch routing (e.g., disable a switch to AI). |
| `Graph.DecodeSwitchAt` | Decides which incident segment is "enter" and which two are normal/diverging. Cached. |
| `Graph.IsSwitch(node)` | Checks `SegmentsConnectedTo(node).Count == 3` — defines what counts as a switch. |
| `Graph.NodeIsDeadEnd` | Defines bumper placement. Two callers: `TrackObjectManager` (visual bumpers) and `LocationByMoving` (end-of-track handling). |
| `Graph.CalculateFoulingDistance(node)` | Used by AutoEngineer to decide stop-before-fouling distance for switches. Walks until two diverging routes are 4.27 m (14 ft) apart. |
| `Graph.SetGroupEnabled` / `SetGroupAvailable` | Toggle whole groups of segments; triggers `GraphDidChangeEnabledGroups` / `GraphDidChangeAvailableGroups` events. |

---

## `Track.TrackNode`

Per-node MonoBehaviour. Always present in the scene. Holds the *runtime* mutable state (only `isThrown`) and the *static* role flags (`IsCTCSwitch`, `IsCTCSwitchUnlocked`).

```csharp
public string id;                            // 13
public bool flipSwitchStand;                 // 15
[CanBeNull] public Turntable turntable;      // 18
public Action OnDidChangeThrown;             // 20
private bool _isThrown;                      // 22

public bool isThrown { get; set; }           // 24  — fires SwitchThrownDidChange messenger
public bool IsCTCSwitch { get; set; }        // 45  — set by CTCSwitchMonitor
public bool IsCTCSwitchUnlocked { get; set; } // 47 — set by CTCPanelController
public bool CTCDisplayThrown { get; private set; } // 49 — flipped on thrown change when CTC-unlocked
```

`isThrown.set` (`TrackNode.cs:24-43`):
1. Skips if value unchanged.
2. If `IsCTCSwitchUnlocked`, toggles `CTCDisplayThrown`.
3. Invokes `OnDidChangeThrown` event.
4. Sends `Game.Events.SwitchThrownDidChange` (carrying this node) via `Messenger.Default`.

**There is no KVO for `isThrown` on `TrackNode` itself.** The wire is the `SetSwitch` message (see [MP authority](#mp-authority)). The signal subsystem mirrors thrown into a separate KVO key `switch:<nodeId>:position` via `CTCSwitchMonitor.UpdateSwitchPositionProperty` (`CTCSwitchMonitor.cs:149`) — **only when** the switch participates in an interlocking or block. See [signals-dispatch › CTCSwitchMonitor](signals-dispatch.md#switch-monitoring-ctcswitchmonitor).

### Geometry helpers

```csharp
Vector3 TangentPointAlongSegment(TrackSegment, float d)   // 76
bool    SegmentCanReachSegment(TrackSegment, TrackSegment) // 87 — used by DecodeSwitchAt
```

`SegmentCanReachSegment` returns true iff the two segments' tangent points at `±d` along the node's local forward are >= 0.32 m apart — i.e., the node can route between them. Called twice when classifying a 3-way junction in `Graph.DecodeSwitchAt`.

### Patch candidates

| Method | Why patch |
|---|---|
| `TrackNode.isThrown.set` | Intercept all switch-throw events. Already triggers Messenger; subscribe via `SwitchThrownDidChange` event before patching. |
| `TrackNode.SegmentCanReachSegment` | Override geometric switch detection (e.g., for non-standard 4-way nodes). |

---

## `Track.TrackSegment`

```csharp
public string id;                                    // 27
public TrackNode a, b;                               // 29, 31
public int priority;          // [Range(-2,2)]       // 35  — switch route preference + RouteSearch cost
public int speedLimit;        // [Range(0,45)] mph   // 39  — 0 = use trackClass default
public string groupId;                               // 41
public Style style;           // Standard|Bridge|Tunnel|Yard  // 43
public TrackClass trackClass; // Mainline|Branch|Industrial   // 45
[CanBeNull] public Turntable turntable;              // 48
public bool Available { get; set; } = true;          // 58 — group-controlled
public bool GroupEnabled { get; set; } = true;       // 60 — group-controlled
public bool IsInvisible => turntable != null;        // 62
public BezierCurve Curve { get; }                    // 64 — lazy-built
```

### Mileage / position math

- `GetLength()` → bezier-arc-length via `BezierDistanceParameterCache` (`TrackSegment.cs:204`). Cached after first call.
- `GetPositionRotationAtDistance(distance, end, accuracy, out pos, out rot)` (`TrackSegment.cs:286`) — flips distance if `end == B`, applies accuracy mode, post-translates by `Curve.P0`, and rotates +180° if querying from B-end.
- `GetExpectedSpeedLimit()` returns `speedLimit` if non-zero, else `trackClass`-default: Mainline=35, Branch=25, Industrial=15.
- `LocationFromPoint(Vector3, float radius)` is a brute-force scan (no curve subdivision) — used only when `Graph.TryGetLocationFromPoint` falls back. The faster path is `Graph.TryGetLocationFromGamePoint` which uses the `_segmentCurveCache` `LineCurve` (cached polyline approximation).

### Bezier construction

`CreateBezier()` (`TrackSegment.cs:184`) builds a 4-point cubic from `a.localPosition`, `b.localPosition`, and two interior tangent points. The tangent factor (`BezierTangentFactorForTangents`, `TrackSegment.cs:194`) lerps 0.35→0.41 as the angle between node forwards rises 45°→90°.

`InvalidateCurve()` (`TrackSegment.cs:170`) clears the cached `_curve`, `_boundingBox`, `_posRotCache`. **Called by `Graph.InvalidateNode`** for every segment touching a moved node; also by turntables on rotation.

### Patch candidates

| Method | Why patch |
|---|---|
| `TrackSegment.GetExpectedSpeedLimit` | Mod-side track class speed-limit override. Currently three-tier and hardcoded. |
| `TrackSegment.GetPositionRotationAtDistance` | Per-segment positioning hook; very hot path (called by every car physics tick). |
| `TrackSegment.CreateBezier` / `BezierTangentFactorForTangents` | Curve geometry tuning. Affects all newly-built or invalidated curves. |
| `TrackSegment.LocationFromPoint` | Slow fallback path-to-Location; rarely called but fixable for accuracy. |

### Gotchas

- `IsInvisible` is `true` whenever `turntable != null` — these segments are not drawn by `TrackObjectManager` (they're rendered via the turntable bridge).
- `_posRotCache` is `BezierDistanceParameterCache` — disposable; cleared in `OnDestroy`. Mods that hold long-lived `TrackSegment` references should not retain the underlying bezier data.
- **`speedLimit` is `int` mph in [0, 45]** — you cannot author > 45 mph in the inspector. Mods can write higher values via reflection, but `GetExpectedSpeedLimit` will surface them only if non-zero.

---

## `Track.Location` (the universal address)

```csharp
public readonly TrackSegment segment;
public readonly float distance;              // meters from `end`
public readonly TrackSegment.End end;        // A or B — which end the distance counts from

public bool   IsValid;                       // segment != null && 0 ≤ distance ≤ segment.GetLength()
public bool   EndIsA;
public string NodeString;                    // "<fromId>_<toId>_<dist>" — useful for logs/keys

public Vector3    GetPosition(PositionAccuracy = Standard);
public Quaternion GetRotation(PositionAccuracy = Standard);

public Location Clamped();                   // clamps distance to [0, length]
public Location Clamped(out float remainder);// also returns overflow
public float    DistanceTo(TrackNode);
public float    DistanceTo(TrackSegment.End);
public float    DistanceUntilEnd();          // distance to opposite end
public Location Flipped();                   // mirror end+distance
public Location Moving(float d);             // additive on `distance` only — does NOT cross segments
public Location WithEnd(TrackSegment.End desiredEnd);
public SerializableLocation Serializable();

public static bool TryMatchSegment(Location, TrackSegment, out Location matched);  // segment-boundary alias resolver
```

**`Equals` uses 1e-6 tolerance** by default (`Location.cs:175`), or a caller-supplied tolerance overload. `GetHashCode` includes `distance.GetHashCode()` directly — small numeric noise (e.g., from re-projection) can produce different hash codes for `Equals`-equal locations. **Avoid using `Location` as a dictionary key** unless you quantize first; use `Searcher.QuantizedLocation(loc)` (`Track.Search/Searcher.cs:105`) which rounds to 0.1 m.

### `Moving(float)` is local — use `Graph.LocationByMoving` for cross-segment

`Location.Moving(d)` only touches the `distance` field. To advance through switches and across segment boundaries, call `Graph.LocationByMoving(loc, d, checkSwitchAgainstMovement, EndOfTrackHandling)` (`Graph.cs:340`):

- `EndOfTrackHandling.Throw` — default for `stopAtEndOfTrack: false`; raises `EndOfTrack` exception.
- `EndOfTrackHandling.Clamp` — pin to last valid location.
- `EndOfTrackHandling.Unclamped` — let distance run past the end (resulting in invalid Location with `distance > length`).
- `checkSwitchAgainstMovement: true` raises `SwitchAgainstMovement` (carrying the offending `TrackNode`) if the walk crosses a 3-way node from a non-routed direction. AutoEngineer catches this to decide stopping logic.
- 1000-iteration safety limit (`Graph.cs:352`) — throws on runaway.

### `SerializableLocation`

`SerializableLocation { string segmentId; float distance; TrackSegment.End end }` (`SerializableLocation.cs:5`). Used for inspector serialization (TrackMarker, TrackSpan store these in `_lower`/`_upper`). `Graph.MakeLocation(SerializableLocation)` (`Graph.cs:848`) re-resolves to a runtime `Location`.

### Snapshot wire format

`Snapshot.TrackLocation(segmentId, distance, endIsA)` is the multiplayer wire format (`Graph.CreateSnapshotTrackLocation`, `Graph.MakeLocation(Snapshot.TrackLocation)`, `Graph.cs:833-846`). Bool-packed end avoids serializing the enum.

### `Graph.ResolveLocationString` / `LocationToString`

Console-friendly text format `<segmentId>|a|<distance>` or `<segmentId>|b|<distance>` (`Graph.cs:1307, 1343`). Useful for `/teleport`-style commands.

### Patch candidates

| Method | Why patch |
|---|---|
| `Location` is a struct — patch via the consumers (`Graph.GetPositionRotation`, `Graph.LocationByMoving`). |  |

---

## Switch model

A switch is **any 3-segment node** (`Graph.IsSwitch == SegmentsConnectedTo(node).Count() == 3`). There is no dedicated `Switch` MonoBehaviour. The switch *role* of each incident segment is decoded by `Graph.DecodeSwitchAt` and cached in `_decodedSwitchCache`.

```csharp
bool DecodeSwitchAt(TrackNode node, out TrackSegment enter, out TrackSegment a, out TrackSegment b)
```

### Decoding logic (`Graph.cs:525-585`)

1. Take the three incident segments.
2. Use `TrackNode.SegmentCanReachSegment(seg0, seg1)` (geometric tangent check) to find the *enter* segment — the one whose two pairings with the other two are both reachable.
3. With `enter` identified, the remaining two are the diverging branches.
4. **Sort branches into `a` (normal) and `b` (reversed):**
   - If `priority`s differ, higher `priority` wins normal.
   - Else compare `DivergingAngleOf(node, seg)` (`Graph.cs:587`) — the smaller-angle branch is normal.

`enter == segment.NodeForEnd(end)` is the test "approaching the switch from the trailing side." When the switch is entered from `enter`, `LocationFrom` chooses `a` if `!isThrown` else `b`. When entered from `a` or `b`, `LocationFrom` always returns `enter` and ignores `isThrown` — and **if you walk into the diverging branch with the switch set against you and `checkSwitchAgainstMovement: true`, you get `SwitchAgainstMovement`** (`Graph.cs:437-449`).

### Switch-against-movement detection

```csharp
private void CheckSwitchAgainstMovement(TrackSegment seg, TrackSegment nextSegment, TrackNode node)  // 437
{
    SegmentsReachableFrom(nextSegment, ..., out normal, out reversed);
    if (normal != null && reversed != null) {
        bool num  = !node.isThrown && normal != seg;
        bool flag =  node.isThrown && reversed != seg;
        if (num || flag) throw new SwitchAgainstMovement(node);
    }
}
```

Only the **trailing** side of a 3-way is throwable-against; entering from `enter` is always permitted (the switch routes you).

### Throwing a switch — write paths

| Origin | Class / method | Auth |
|---|---|---|
| Player click on switch stand | `SwitchStandClick.Activate` → `RequestSetSwitch` | Crew |
| Player click on map UI | `UI.Map.MapSwitchStand.Click` → `RequestSetSwitch` | Crew |
| AutoEngineer / scripts | `TrainController.TrySetSwitch(nodeId, thrown, requesterUri)` (host-side) → `SetSwitch` | Host |
| CTC interlocking code | `CTCInterlocking.CodeSwitchChanges` → `SetSwitch` (per-switch, with `requester="CTC"`) | Host |
| Console / debug | direct `StateManager.ApplyLocal(new SetSwitch(...))` |  |
| Recovery from `SwitchAgainstMovement` | `TrainController.FixSwitchAgainstMovement` → `SetSwitch` (requester URI is the offending car) | Host (called from physics) |

### `RequestSetSwitch` → `SetSwitch` flow

```
Client (or host):
  StateManager.ApplyLocal(new RequestSetSwitch(nodeId, thrown))
        ↓ (StateManager routes to host)
Host: TrainController.HandleRequestSetSwitch(setSwitch, sender)        // TrainController.cs:1356
        ↓
Host: TrainController.TrySetSwitch(nodeId, thrown, requesterUri, out err)   // 1364
        ├─ CanSetSwitch(node, thrown, out foundCar)                        // 1791
        │     └─ false if a car is on the switch and would be derailed by the throw
        ├─ if (node.IsCTCSwitch && !node.IsCTCSwitchUnlocked) → reject "Switch is CTC controlled"
        └─ StateManager.ApplyLocal(new SetSwitch(nodeId, thrown, Now, requesterUri))   // HostOnly broadcast
                ↓
        StateManager → all peers: HandleSetSwitch                              // 1433
                ↓
                node.isThrown = setSwitch.Thrown
                AuditManager.RecordSwitchAction(...) on host
                // node.isThrown.set fires SwitchThrownDidChange messenger
```

**Two-stage validation:** `CanSetSwitch` runs host-side and checks both the immediate switch position AND whether the car's two `WheelBounds` (front/rear) lie on the segment that would *become* the routed branch. The function logic (`TrainController.cs:1791-1823`):

```csharp
public bool CanSetSwitch(TrackNode node, bool thrown, out Car foundCar)
{
    if (node.isThrown == thrown) return true;          // no-op throw always allowed
    if (!CarOnSwitch(node, null, out foundCar)) return true;  // no car at switch
    graph.DecodeSwitchAt(node, out _, out a, out b);
    Car car = foundCar;
    bool num  = !thrown;       // throwing to Normal → must lie on `a`
    bool flag =  thrown;       // throwing to Reversed → must lie on `b`
    return (num && IsOnSegment(a)) || (flag && IsOnSegment(b));
    // IsOnSegment(seg) = car.WheelBoundsF.segment == seg || car.WheelBoundsR.segment == seg
}
```

`CarOnSwitch` (`TrainController.cs:1825`) checks both `CarWheelBoundsOver(node)` (axle straddling the node point) and a 4-meter `Location`-radius scan into both diverging segments.

### CTC lock gating

If a switch has `IsCTCSwitch == true` (assigned by `CTCSwitchMonitor` based on whether the node is in any active interlocking's `switchSets`) AND `IsCTCSwitchUnlocked == false`, manual throws via `TrySetSwitch` are rejected. To unlock, send `RequestSetSwitchUnlocked` (Crew auth) — see [signals-dispatch › Switch unlock](signals-dispatch.md#switch-locking-and-unlocking).

### Patch candidates

| Method | Why patch |
|---|---|
| `TrainController.HandleRequestSetSwitch` | Inject extra access checks on incoming requests (e.g., role-based switch ownership). |
| `TrainController.TrySetSwitch` | Add side-effects on host-side throw (e.g., emit a custom event, record analytics). |
| `TrainController.CanSetSwitch` | Gate by mod-defined criteria; relax to allow throws with cars on the switch (rough handling). |
| `TrainController.HandleSetSwitch` | Postfix to react to all switch state changes (host AND clients). Subscribe to `SwitchThrownDidChange` instead if you only need the message. |
| `Graph.DecodeSwitchAt` | Replace switch-classification logic (e.g., support 4-way wyes). Cached — invalidate `_decodedSwitchCache` if you patch. |
| `TrainController.FixSwitchAgainstMovement` | Currently permissive (just throws the switch under the car) — patch to make it punitive. |

### Gotchas

- **`SegmentsConnectedTo` filters out turntable nodes from cache** (`Graph.cs:198-201`). The `_nodeConnectionsCache` skip ensures turntables never appear as bouncy false 3-ways.
- **Switch decoding is cached per-node id** (`_decodedSwitchCache`). If a mod adds/removes segments at runtime, call `Graph.OnNodeDidChange(node)` to invalidate.
- **`isThrown` does not write any KVO key directly.** Only `CTCSwitchMonitor.UpdateSwitchPositionProperty` mirrors it into `switch:<nodeId>:position`, and only for switches that participate in CTC. Mods that need to observe arbitrary switch state should use `SwitchThrownDidChange` Messenger.
- **No event on segment-list change.** When a mod runtime-adds a segment via `Graph.AddSegment`, only the connection cache is invalidated. There is no `SegmentDidAdd` Messenger; subscribers must hook `RebuildCollections` or invoke graph rebuilds manually.

---

## Routing & A* (`Track.Search`)

The path-finding namespace. Used by AutoEngineer, by `TrackSpan` to materialize point-lists between two locations, by `TeleportLoadingIndustry`, and by anyone who needs "is there a route from X to Y?".

### `RouteSearch.Step`

```csharp
public readonly struct Step {
    public readonly Location       Location;        // where the step ends
    [CanBeNull] public readonly TrackNode Node;     // null when step is mid-segment
    public readonly StepDirection  Direction;       // Out or Back (relative to train heading)
    public readonly float          Distance;        // segment cost contribution
    public readonly StepFlag       Flags;           // EnterCTCSwitch | SearchLimit
    public Vector3 Position { get; }                // world position
}
```

A route is a `List<Step>`. Each step records either a node (junction-stop) or a mid-segment location (start, end, or cleared-switch resumption). Adjacent steps share a `TrackSegment` you can recover via `graph.SegmentCommonToNodes(stepA.Node, stepB.Node)` (used by `GraphRouteSearchExtension.FindRoute → List<TrackSegment>`).

### `HeuristicCosts`

Tuning surface for A*. Two presets:

```csharp
public struct HeuristicCosts {
    public int DivergingRoute;        // bonus per diverging branch taken
    public int ThrowSwitch;           // bonus per switch that needs throwing
    public int ThrowSwitchCTCLocked;  // bonus per CTC-locked switch (effectively forbidden if huge)
    public int CarBlockingRoute;      // bonus per blocking car encountered
    public static HeuristicCosts Zero;
    public static HeuristicCosts AutoEngineer = { 20, 10, 1000, 5000 };
}
```

`HeuristicCosts.Zero` is used by `TrackSpan` (just find *any* route between lower/upper) and by simple distance queries. `AutoEngineer` heavily penalizes locked CTC and blocking cars.

### `Searcher` internals (`Track.Search/Searcher.cs:12`)

- Wraps `AStar<SearchState>` (in `Helpers/`).
- Quantizes locations to 0.1 m for closed-list equality (`QuantizedLocation`).
- Seeds with **two** initial states at the origin: one facing forward, one facing back (`Searcher.cs:84-88`). When `trainLength > 0.1`, the back-facing seed is moved by `-trainLength` and flipped — so the search "knows" the train extends behind the head end and won't try to back over its own length.
- `_mustClearSwitches = trainLength > 0.1` — when set, neighbor expansion through a switch only allows continuing in the routed direction; backing out is suppressed except as part of a `ClearSwitch` follow-up.
- `ClearSwitch` (`Track.Search/ClearSwitch.cs:5`) is a deferred `(node, distance)` pair queued when the search passes through a switch with `_mustClearSwitches`. The search must defer expansion until the train has fully cleared the switch (i.e., all coupled cars are past it) before it considers throwing the switch behind itself.
- `CarBlockingRoute` cost: scans the proposed segment for `EnemyCarAt` checks. If a car in `_checkForCarsImpasse` is found, the route truncates at that point. Otherwise, each blocking-car encounter adds `_costs.CarBlockingRoute`.

### Cost model (`Searcher.GetCostToTraverseSegment`)

```csharp
segmentCost = segment.GetLength() * (1 + (-segment.priority) / 5);   // higher priority → cheaper
branchCost  = 0;
if (isDivergingRoute)                       branchCost += DivergingRoute;
if (isDivergingRoute != isThrown)           branchCost += isCTCLockedSwitch ? ThrowSwitchCTCLocked : ThrowSwitch;
```

Higher `priority` (range -2..+2) makes a segment cheaper to traverse; default 0. The "diverging != thrown" term penalizes routes that require throwing a switch (since throwing carries a real-world cost). Combined with `isCTCLockedSwitch`, this is how AutoEngineer "knows" not to plot through a locked CTC switch unless desperate.

### `Graph.FindRoute` extensions

`Track.Search/GraphRouteSearchExtension.cs`:

```csharp
List<TrackSegment> FindRoute(this Graph, Location, Location);                 // 10 — segment list, HeuristicCosts.Zero
List<TrackSegment> FindRoute(this Graph, TrackNode, TrackNode);                // 42 — picks the segment closest to the other endpoint
bool TryFindDistance(this Graph, Location, Location, out totalDistance, out traverseTimeSeconds);  // 55
```

`TryFindDistance`'s `EstimateSecondsToTraverse` uses `GetExpectedSpeedLimit()` per segment — the canonical "how long should this take?" formula, used by Ops scheduling.

### Materializing a route as a polyline: `RouteSearchPoints.FindPoints`

`Track.Search/RouteSearchPoints.cs:9` — given two `Location`s and a step distance, produces a `List<Vector3>` of game-space points along the route, plus optionally the list of segments traversed. **This is what `TrackSpan` uses internally** to build its cached point list (`TrackSpan.cs:318`).

```csharp
graph.FindPoints(start, end, step:10f, name:"foo", outPoints, [outSegments]);
```

Internally calls `FindRoute` with `HeuristicCosts.Zero` and `maxIterations=200`. **A `TrackSpan` whose lower→upper requires more than 200 search iterations will silently fail with an empty point list** and an error log "Search failed after Xs."

### Patch candidates

| Method | Why patch |
|---|---|
| `RouteSearch.FindRoute` (extension) | Add custom search modes (e.g., scenic-route preference). The `Searcher` instance is constructed fresh each call. |
| `Searcher.GetCostToTraverseSegment` | Replace the cost model. Hot — runs per-neighbor. |
| `Searcher.CheckForCars` | Modify how blocking cars are detected/scored. |
| `HeuristicCosts.AutoEngineer` (static prop) | Static getter returning a struct literal — replace via Harmony for global AI behavior tweaks. |
| `RouteSearchPoints.FindPoints` | The 200-iteration limit is hardcoded in the `FindRoute` call inside (line 13). Patch to raise the limit for very long spans. |

### Gotchas

- The `_origin.Flipped()` back-seed means routes can return *paths that start by going backwards* if that's cheaper. Consumers must check `Step[0].Direction` and `Step[0].Location` against the input.
- `SearchLimit` is decremented per step; when it goes negative (`p.SearchLimit < 0`), the goal check returns true regardless of destination match (`Searcher.cs:121-127`). This is how AutoEngineer caps lookahead distance — a search limit step with `SearchLimit` flag set bumps `searchLimit2 = 2000f` (effectively "scan 2 km past the limit-marker switch").
- **The 5000-iteration default in `RouteSearch.FindRoute` is generous but not infinite.** Massive yards or scenic routes may exceed it. AutoEngineer doubles down if the first try fails (`AutoEngineerPlanner.cs:1517`).
- `Searcher` allocates `List<SearchState>` and pool-borrows lots of `List<ClearSwitch>` per search; not GC-free but bounded. Don't run `FindRoute` per-frame from a mod.

---

## `Track.TrackSpan`

A persistent run between two locations on the graph. Used for: signal blocks (parented under `CTCBlock`), industry track segments, passenger platforms, anywhere "a length of track" is a first-class authoring concept.

```csharp
public string id;                                   // 13
[SerializeField, HideInInspector] private SerializableLocation _lower, _upper;  // 19, 23
public Location? lower { get; set; }                 // 35  — invalidates cache on set
public Location? upper { get; set; }                 // 52
public float Length { get; }                          // 69 — sum of segment between cached points
public bool IsValid { get; }                          // 101 — both endpoints non-null and valid

bool Contains(Location loc);                          // 206 — point-on-route check
bool Contains(Vector3 point, float radius);           // 246 — proximity check
IReadOnlyCollection<Vector3>      GetPoints();        // 266 — cached polyline
IReadOnlyCollection<TrackSegment> GetSegments();      // 273 — cached segment list
Vector3 GetCenterPoint();                             // 279 — _cachedPoints[count/2]
Mesh    BuildMesh(Matrix4x4);                         // 346 — collider mesh from polyline
void    SwapUpperLower();                             // 382
[ContextMenu] void NormalizeUpperLower();             // 153 — fix common authoring inversions
```

### Caching

`UpdateCachedPointsIfNeeded` (`TrackSpan.cs:297`):
- Skips if cache populated.
- Bails (with one-shot warning) if either endpoint is null/invalid and `Graph.HasPopulatedCollections` is true.
- Otherwise calls `Graph.FindPoints(lower.Clamped(), upper.Clamped(), step:10f, name, _cachedPoints, _cachedSegments)`.
- Sums distances into `_length`.

The cache is invalidated by setter on `lower`/`upper` only. **Span cache is NOT auto-invalidated by switch throws or by `Graph.RebuildCollections`** — if a mod changes the routing between lower and upper, span point lists may stale until `InvalidateCache()` is called explicitly.

### Patch candidates

| Method | Why patch |
|---|---|
| `TrackSpan.Contains(Location)` | Override block-membership logic. Used by `CTCBlock.CheckOccupied` indirectly via `TrainController.CarsOnSpan`. |
| `TrackSpan.UpdateCachedPointsIfNeeded` | Only place where the polyline is computed. Postfix to inject custom data. |
| `TrackSpan.InvalidateCache` | Hook for "when does this span's geometry need re-resolving" — call from your own switch-throw observer if mods need spans to follow switches. |

### Gotchas

- **`TrackSpan` is NOT registered with `Graph` until found via `FindObjectsOfType<TrackSpan>` during `RebuildCollections`** (`Graph.cs:186-190`). Spans created at runtime won't appear in `Graph.spans` until next rebuild — call `Graph.RebuildCollections()` after.
- `NormalizeUpperLower` flips ends so that lower→upper is monotonic in segment-distance terms. Authoring tools call this on any inspector edit; mods that programmatically set lower/upper should call it too to avoid empty `_cachedPoints` after `FindPoints`.

---

## `Track.TrackMarker`

A point-on-track tag. Five subtypes in `TrackMarkerType`: `Generic`, `Signal`, `Flare`, `Crossing`, `PassengerStop`. Used by AutoEngineer for crossing/flare lookahead, by `CTCAutoSignal`/`CTCPredicateSignal` indirectly (markers carry references to `CTCSignal` components on the same GameObject), and by `PassengerStop` for routing.

```csharp
public string         id;               // 11
public TrackMarkerType type;            // 13
[SerializeField, HideInInspector] private SerializableLocation _location;
public Location? Location { get; set; } // 27 — fires OnLocationChanged event
public CTCSignal Signal { get; }         // 64 — only non-null if type==Signal
public PassengerStop PassengerStop { get; } // 80
public Graph.PositionRotation? PositionRotation { get; }
public event Action OnLocationChanged;
```

Self-registers in `OnEnable` (`TrackMarker.cs:99`) via `Graph.RegisterTrackMarker(this)`. The `Graph._trackMarkers` dictionary is keyed by **segment id**, then `HashSet<TrackMarker>` per segment.

### Iteration: `Graph.EnumerateTrackMarkers`

```csharp
IEnumerable<TrackMarker> EnumerateTrackMarkers(Location start, float distance, bool sameDirection)  // Graph.cs:1121
```

Walks forward (or back, if `distance < 0`) through segments via `LocationFrom` and yields markers in order of `DistanceTo(end)`. The `sameDirection` flag filters markers whose `Location.end` matches `start.end` — used to skip signals facing away from the train (signals are direction-protected).

This is the canonical way to ask "what's coming up?" for any AI/UI lookahead.

### Patch candidates

| Method | Why patch |
|---|---|
| `Graph.EnumerateTrackMarkers` | Add filtering or merging of mod-defined marker types. |
| `TrackMarker.OnLocationChanged` event | Subscribe to react to marker movement (rare; markers are usually authored). |

### Gotchas

- **`Graph.MarkerForId` is a linear scan** through every marker on every segment (`Graph.cs:1106-1119`). O(N). Don't call this in a tight loop; cache the lookup yourself.
- Markers re-registering without first un-registering can leave stale entries in `_trackMarkers`. The `OnDisable` handler iterates all segment-keyed sets to remove (`Graph.cs:1097`) — works but O(segments).

---

## Turntables

A turntable is a `Turntable` MonoBehaviour with `subdivisions` stop indices around its `radius`. It owns a single mutable `TrackSegment` (the bridge) whose `a`/`b` nodes are reassigned on each rotation to the appropriate two `TrackNode`s in its `nodes` list. **The bridge segment has `IsInvisible = true`** (because `turntable != null`) and is never drawn by `TrackObjectManager`.

### Core concepts

- `Angle` (degrees) — current bridge rotation around the turntable's local Y axis.
- `StopIndex` — `int?`, set when angle aligns with a slot (within 0.1°/index); null while moving.
- `IsLined` — `StopIndex.HasValue`; bridge segment is connected to a real outside node.
- When NOT lined, bridge `a`/`b` point to `_freeA`/`_freeB` (transient nodes attached only to the bridge), so navigation through the bridge fails — `Graph.SegmentsReachableFrom` returns null because `_freeA`/`_freeB` only appear on the bridge itself.

### `Turntable.SegmentsReachableFrom`

`Graph.SegmentsReachableFrom` defers to `segment.turntable.SegmentsReachableFrom(segment, end, out other)` (`Graph.cs:474-478`). Always returns a single `normal`, never a `reversed` — turntables can't be "switched against."

### Authority — `TurntableController` / `Transmitter` / `Receiver`

```
HOST                                                     CLIENT
  TurntableController (drives _speed from controlLever)
  ├── FixedUpdate sets turntable.SetAngle(...)
  ├── turntable.UpdateSegmentIndex(isMoving)             ← bridge a/b reassigned, segment curve invalidated
  └── TurntableTransmitter (host only)
        sends TurntableUpdateAngle (per 0.2s while moving)
        sends TurntableUpdateStopIndex (on lock-in)
                                                         TurntableReceiver
                                                         ├── 300-tick (~5s) display delay
                                                         ├── lerp angle between frames
                                                         └── apply StopIndex when its tick reaches displayTick
```

The `controlLever` ContinuousControl is on a `KeyValueObject` (key `"controlLever"`) — `MinimumLevelCrew` auth via the `GlobalKeyValueObject` wrapping. **Both host and client write the lever locally; the host is the only one whose `TurntableController.FixedUpdate` actually moves the bridge.** Clients get the visual via `TurntableReceiver.MoveBetween`.

### `Turntable.TryGetCarBlockingMovement`

Checks if a car has wheel-bounds over either bridge endpoint, OR is coupled across the gap to a car at the bridge end (`Turntable.cs:282-303`). The `CanContinueMoving` test (`TurntableController.cs:177`) further checks the *next* slot the bridge will rotate into and aborts if a car is on the pit side too.

### Patch candidates

| Method | Why patch |
|---|---|
| `Turntable.SetAngle` | Intercept all rotation events. |
| `TurntableController.FixedUpdate` | Modify rotation speed / acceleration model. |
| `Turntable.TryGetCarBlockingMovement` / `TurntableController.CanContinueMoving` | Permissive turntable mods (allow rotating with a car). |
| `TurntableReceiver.UpdatePosition` | Smoothing tweaks for client-side lerp. |

### Gotchas

- **`Turntable` is not a `TrackNode` itself.** It owns nodes via the `nodes` list (one per subdivision). Each of those nodes has `node.turntable == this`, which is the flag everyone uses to detect turntable nodes.
- The bridge segment is named `"<turntable.id>-Bridge"` and styled `Bridge`. Don't try to look it up by id; call `Graph.TurntableControllerForId(turntableId)` and walk to it.
- `TurntableReceiver.HandleUpdateAngle` is called via reflection from the Game.Messages dispatcher — see the `TurntableUpdateAngle` / `TurntableUpdateStopIndex` messages.

---

## Curve geometry, grade, speed limits

### Curvature

- Per-segment 5-byte curvature samples (`Graph._curvatureSampleCache`, `Graph.cs:107`).
- `Graph.CurvatureAtLocation(loc, resolution)` — Interpolate (per-fifth) or Segment (max).
- `Graph.SampleCurvature(segment)` (`Graph.cs:1030`) computes via `TrackMath.CalculateCurveDegrees(posRotA, posRotB)`. The math (in `Track/TrackMath.cs:9`) intersects two tangent lines and computes a chord-based degree-of-curvature value. Returns a byte 0..255 (typical mainline 0..3, sharp branch 8..12).

### Grade

`Graph.GradeAtLocation(loc)` (`Graph.cs:1066`) — reads the `eulerAngles.x` of the rotation at the location, normalizes to [-180, 180], and multiplies by 1.746 to convert degrees to percent grade (small-angle: 1° ≈ 1.746%).

### Speed limit

- Per-segment integer mph in `[0, 45]`. 0 means "use class default."
- `TrackSegment.GetExpectedSpeedLimit` resolves to mph.
- AutoEngineer uses this in `UpdateTargets` (`AutoEngineerPlanner.cs:451`).
- **No per-direction speed limits** — same value both ways.

### Patch candidates

| Method | Why patch |
|---|---|
| `Graph.CurvatureAtLocation` | Curvature override; consumed by `Car.ApplyCurvatureToModel` for damage/derailment. See [wear-durability › DamageForSpeed](wear-durability.md#modelphysicstrainmath-damage-formulas). |
| `Graph.GradeAtLocation` | Grade override; affects steam locomotive performance + AI throttle planning. |
| `TrackSegment.GetExpectedSpeedLimit` | Per-class speed limit override. |

---

## Multiplayer authority summary

| Topology aspect | Where it lives | Sync method |
|---|---|---|
| Nodes & segments (existence, geometry) | Scene | Identical scene loaded by every peer. No runtime sync. |
| `TrackNode.isThrown` | Per-peer field on `TrackNode` | `SetSwitch` HostOnly message; `RequestSetSwitch` is the Crew client→host channel |
| `TrackNode.IsCTCSwitch` | Per-peer field, computed by `CTCSwitchMonitor` from interlocking sets | Implicit (every peer runs the same `CTCSwitchMonitor` logic against the shared scene) |
| `TrackNode.IsCTCSwitchUnlocked` | Per-peer field set from `SignalStorage` `unlockedSwitchIds` array key | KVO observer on the signal storage's KVO |
| Group enable/availability | `Graph.enabledGroupIds` / `availableGroupIds` (List<string> on `Graph` MonoBehaviour) | Driven by `MapFeatureManager` (`mapFeatures` HostOnly KVO `features` dict key) |
| Turntable angle / stopIndex | `Turntable.Angle`, `Turntable.StopIndex` | Host runs `TurntableController.FixedUpdate`; transmits via `TurntableUpdateAngle` / `TurntableUpdateStopIndex` messages |
| Track marker existence/location | Authored | None (scene-baked) |

**Switch authority is the only "interactive" topology authority.** Every other topology mutation flows through map features (host-controlled progression unlocks) or scripts.

### Wire messages

| Message | Auth | File |
|---|---|---|
| `RequestSetSwitch(nodeId, thrown)` | Crew | `Game.Messages/RequestSetSwitch.cs` |
| `RequestSetSwitchUnlocked(nodeId, unlocked)` | Crew | `Game.Messages/RequestSetSwitchUnlocked.cs` |
| `SetSwitch(nodeId, thrown, tick, requester)` | HostOnly | `Game.Messages/SetSwitch.cs` |
| `TurntableUpdateAngle` / `TurntableUpdateStopIndex` | HostOnly (sent from `TurntableTransmitter`) | `Game.Messages/` |

### Related Messenger / KVO events

| Event | Type | Fired by |
|---|---|---|
| `Game.Events.SwitchThrownDidChange(TrackNode)` | Messenger struct | `TrackNode.isThrown.set` (`TrackNode.cs:40`) |
| `Game.Events.GraphDidRebuildCollections` | Messenger struct | end of `Graph.RebuildCollections` |
| `Game.Events.GraphDidChangeEnabledGroups` | Messenger struct | `Graph.SetGroupEnabled` (when set actually changes) |
| `Game.Events.GraphDidChangeAvailableGroups` | Messenger struct | `Graph.SetGroupAvailable` |
| `Game.Events.MapFeatureChangedGraph` | Messenger struct | `MapFeatureManager` after RebuildCollections (`MapFeatureManager.cs:189`) |
| `Game.Events.CTCFeatureChange` | Messenger struct | `CTCMapFeatureTarget.SetEnabled` — see [signals-dispatch](signals-dispatch.md) |
| `TrackMarker.OnLocationChanged` | C# event | `TrackMarker.Location.set` |

### Audit trail

Every switch throw goes through `AuditManager.Shared.RecordSwitchAction(nodeId, "Throw Reversed"|"Throw Normal", requesterUri)` on the host (`TrainController.cs:1445`). The requester URI is `EntityReference.URI(EntityType.Player, playerId)` for player throws, `EntityReference.URI(EntityType.Car, carId)` for `FixSwitchAgainstMovement`, and `"CTC"` for interlocking-driven throws.

---

## Map mods → topology pipeline

Map mods are JSON+AssetBundle (per `map-mods-vanilla-survey.md`). The runtime side has two distinct concerns:

### Terrain — `Map.Runtime.MapManager`

`Map.Runtime/MapManager.cs:18` is the only consumer of map data files. It loads heightmap tiles from `StreamingAssets/Maps/<directoryName>/`, slices into 500m square tiles, and builds Unity `TerrainData` lazily based on camera position. **None of this is connected to the track topology.** Track nodes/segments are authored as scene GameObjects, not loaded from map data.

That said, `MapManager` is what positions everything in world space — `WorldTransformer.GameToWorld` and friends resolve the offset between game-coordinate (track) space and world space. `Graph.GetPosition(loc)` returns *game* coordinates; `WorldTransformer.GameToWorld(pos)` is needed for Unity raycasts and rendering.

### Track — `MapFeature` + scene baking

Track is *baked into the scene*, not loaded from map data. The dynamic part is **track group enabling**:

```
MapFeature (MonoBehaviour, child of MapFeatureManager root)
  identifier:                    string                  ← unique key
  trackGroupsEnableOnUnlock:     string[]                ← Graph.SetGroupEnabled(true) per id when unlocked
  trackGroupsAvailableOnUnlock:  string[]                ← Graph.SetGroupAvailable(true) per id when unlocked
  gameObjectsEnableOnUnlock:     GameObject[]            ← SetActive(true) when unlocked
  areasEnableOnUnlock, unlockExcludeIndustries, …        ← industry/area unlocks (out of scope)
```

`MapFeatureManager.HandleFeatureEnablesChanged` (`MapFeatureManager.cs:112`):
1. Diffs the host's `mapFeatures._game["features"]` dict against cache.
2. For each newly-enabled feature, calls `Graph.SetGroupEnabled(groupId, true)` for each id in `trackGroupsEnableOnUnlock`.
3. If any group changed, calls `Graph.RebuildCollections()` and sends `MapFeatureChangedGraph` Messenger event.
4. Schedules a delayed `TrackObjectManager.Instance.Rebuild()` to re-mesh the track visuals.

**The "groupId" on each segment is the join key.** A scene authored with `groupId = "Mainline-East"` on certain segments lets a `MapFeature` toggle the visibility/availability of that group via the matching id string.

### Patch candidates (Map → Topology)

| Method | Why patch |
|---|---|
| `MapFeatureManager.HandleFeatureEnablesChanged` | Inject mod-defined features that trigger group changes outside the unlock pipeline. |
| `MapFeatureManager.UpdateFeatureGraphGroups` | Per-feature group-application — mod features could feed dynamically-discovered groupIds here. |
| `Graph.SetGroupEnabled` / `SetGroupAvailable` | Direct group toggling; bypasses MapFeature entirely. **NOT host-synced**; a client calling this only changes its local view. Combine with a HostOnly KVO write if you need MP. |

### Gotchas

- **`Graph` group lists (`groupIds`, `enabledGroupIds`, `availableGroupIds`) are public `List<string>` fields on the `Graph` MonoBehaviour** — mutable and serializable. They get baked into the scene's group settings on save. Mod mutations at runtime are not persisted.
- `Graph.SetGroupEnabled` / `SetGroupAvailable` only *toggle membership in the lists*; they don't trigger `RebuildCollections`. The caller (`MapFeatureManager`) explicitly calls `RebuildCollections` after when groups changed.
- The `availableGroupIds` distinction is "the group is *visible* but maybe not *interactable*" (e.g., player can see the track but the Pickable colliders are off). `enabledGroupIds` controls whether the segments are even in `Graph.segments` after rebuild — `AddSegment` skips segments whose groupId is not in `enabledGroupIds` (`Graph.cs:225-238`).

---

## Cross-references to Signals & Dispatch

- `CTCBlock` consumes `TrackSpan`s and `TrainController.AnyCarsOnSpan` for occupancy: see [signals-dispatch › Block model](signals-dispatch.md#block-model-ctcblock).
- `TrackNode.IsCTCSwitch` / `IsCTCSwitchUnlocked` are owned by `CTCSwitchMonitor` and `CTCPanelController` respectively: see [signals-dispatch › CTCSwitchMonitor](signals-dispatch.md#switch-monitoring-ctcswitchmonitor).
- `Graph.CalculateFoulingDistance` is used by AutoEngineer to decide stop-before-fouling at switches: see [signals-dispatch › AutoEngineer interaction](signals-dispatch.md#autoengineer-signal-consumption).
- `TrackMarker` carries `CTCSignal` references for AutoEngineer's signal lookahead: see [signals-dispatch › Signal lookahead](signals-dispatch.md#autoengineer-signal-consumption).
- Switch-throw audit trail (`AuditManager.RecordSwitchAction`) and the `requester="CTC"` attribution: [signals-dispatch › CTC switch coding](signals-dispatch.md#ctc-switch-coding).

## Cross-references to Couplers & Wear

- Train position math (`Car.WheelBoundsF/R`, `Car.LocationA/B`) lives on `Car`; `CanSetSwitch` reads `WheelBounds` to decide if a throw would derail. See [couplers › cut lever](couplers.md#cut-lever-pipeline-player-driven-uncouple) for the consist position model. (Future: a `consist-integration.md` crib sheet will document `Car.WheelBounds`/`LocationA`/`LocationB` and the `IntegrationSet` position projection.)
