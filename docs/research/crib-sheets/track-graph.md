# Track Graph & Routing — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/Track/`, `Track.Search/`, plus `Core/Core/AStar.cs`)
**Companions:** [Track Topology](track-topology.md) (the upper layer — nodes, segments, spans, switches as authored), [Signals & Dispatch](signals-dispatch.md), [Auto-Engineer](autoengineer.md), [Tile Loading & Bardo](tile-loading-bardo.md), [Floating Origin](floating-origin.md)

The Graph is the *machinery* under the topology sheet: a `[DefaultExecutionOrder(-1)]` `MonoBehaviour` singleton that indexes every authored `TrackNode`/`TrackSegment`/`TrackSpan`/`TrackMarker` (by walking `GetComponentsInChildren` at `Awake`), exposes the canonical forward/back walk (`LocationByMoving`), the canonical position-resolver (`GetPositionRotation`/`GetPosition`), the canonical switch-decoder (`DecodeSwitchAt`), and the closest-point query (`TryGetLocationFromGamePoint`). All routing in the game flows through `Track.Search.Searcher` (an `AStar<SearchState>` wrapper that pumps the Graph's neighbor expansion, applies `HeuristicCosts`, and threads `ClearSwitch` follow-ups for trains long enough to need to clear a switch behind themselves before throwing it). Two facade extension classes live on top of `Searcher`: `RouteSearch.FindRoute` (returns `List<RouteSearch.Step>`, the canonical low-level search), and `RouteSearchPoints.FindPoints` (turns a route into a polyline, **with a hardcoded `maxIterations=200` cap** that limits how long a `TrackSpan` may be). The graph itself is **per-machine but identical-by-construction** in MP — every peer loads the same scene, so every `Graph.Shared` indexes the same nodes/segments; runtime mutations to topology happen only via `MapFeature` group toggling (which on the host fans out to every peer's `Graph` only if every peer also receives the feature unlock — see [MP authority](#multiplayer-authority)).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Track.Graph` (`Shared`) | `Track/Graph.cs:15` | Singleton; `[DefaultExecutionOrder(-1)]`, `Awake → RebuildCollections` |
| `Graph.RebuildCollections()` | `Track/Graph.cs:161` | Clears all caches, re-scans children, re-finds all spans, flushes pending markers, fires `GraphDidRebuildCollections` |
| `Graph.PositionRotation` (struct) | `Track/Graph.cs:35` | `(Vector3 Position, Quaternion Rotation)`; **game space** when produced from a `Location` |
| `Graph.GetPosition(Location)` | `Track/Graph.cs:747` | Game-space coordinate for a `Location` |
| `Graph.GetPositionRotation(Location, accuracy)` | `Track/Graph.cs:719` | Pos + rot tuple; clamps loc, projects past-end via `PositionRotation.Project(remainder)` |
| `Graph.LocationByMoving(start, dist, checkSwitchAgainstMovement, EndOfTrackHandling)` | `Track/Graph.cs:340/350` | The universal walk; 1000-iteration safety cap |
| `Graph.LocationFrom(seg, end, checkSwitchAgainstMovement)` | `Track/Graph.cs:407` | One step across a node; raises `SwitchAgainstMovement` |
| `Graph.DecodeSwitchAt(node, out enter, out a, out b)` | `Track/Graph.cs:525` | Geometric switch decode; cached in `_decodedSwitchCache` |
| `Graph.TryGetLocationFromGamePoint(gamePos, radius, out Location)` | `Track/Graph.cs:650` | World→Location closest-segment scan over `_segmentCurveCache` polylines |
| `Graph.SetGroupEnabled(id, enabled)` / `SetGroupAvailable(id, enabled)` | `Track/Graph.cs:1177` / `1197` | Mutate `enabledGroupIds`/`availableGroupIds`; fires `GraphDidChangeEnabledGroups`/`Available` |
| `Graph.NodeIsDeadEnd(node, out direction)` | `Track/Graph.cs:598` | Cached |
| `Graph.CalculateFoulingDistance(node)` | `Track/Graph.cs:1229` | Walks both diverging legs of a switch until ≥4.27m apart (14ft); used by AutoEngineer |
| `Graph.EnumerateTrackMarkers(start, distance, sameDirection)` | `Track/Graph.cs:1121` | Yields markers in distance order along the walk |
| `Track.Search.Searcher` | `Track.Search/Searcher.cs:12` | Internal A* glue; constructed per-search |
| `Track.Search.RouteSearch.FindRoute(...)` (extension) | `Track.Search/RouteSearch.cs:128/135` | Public search API; returns `List<Step>` and `Metrics` |
| `Track.Search.RouteSearchPoints.FindPoints(...)` (extension) | `Track.Search/RouteSearchPoints.cs:9` | Route → polyline; **hardcoded `maxIterations=200`** |
| `Track.Search.HeuristicCosts` (struct) | `Track.Search/HeuristicCosts.cs:3` | `Zero` and `AutoEngineer` presets |
| `Track.Search.RouteSearch.Step` (readonly struct) | `Track.Search/RouteSearch.cs:27` | Per-step record (Location, Node, Direction, Distance, Flags) |
| `Track.Search.RouteSearch.StepFlag` (`[Flags]`) | `Track.Search/RouteSearch.cs:19` | `None=0, EnterCTCSwitch=1, SearchLimit=2` |
| `Track.Search.GraphRouteSearchExtension.TryFindDistance(...)` | `Track.Search/GraphRouteSearchExtension.cs:55` | `(distance, traverseTimeSeconds)` — the canonical "how long?" |
| `Core.AStar<TPosition>.Search(...)` | `Core/Core/AStar.cs:130` | Generic A* used by `Searcher`; `LastGCosts` static is **for debug only** and is overwritten every search |

---

## Build spine: scene → Graph

```
Scene loads (TrackNodes, TrackSegments, TrackSpans, TrackMarkers, MapFeatures)
   │
   ▼
Graph.Awake  ← [DefaultExecutionOrder(-1)] runs early
   │  RebuildCollections()
   │      nodes.Clear / segments.Clear / spans.Clear / cache clears
   │      foreach TrackNode in GetComponentsInChildren<TrackNode>:    AddNode(n)
   │      foreach TrackSegment in GetComponentsInChildren<TrackSegment>:
   │          if groupId set:
   │              segment.GroupEnabled = enabledGroupIds.Contains(groupId)
   │              if !GroupEnabled: SKIP (segment is invisible to Graph this rebuild)
   │              segment.Available  = availableGroupIds.Contains(groupId)
   │          else:
   │              GroupEnabled = true; Available = true
   │          segments[id] = segment
   │      foreach segment: AddConnection(segment.a, segment); AddConnection(segment.b, segment)
   │           ↑  AddConnection skips if node.turntable != null (turntables register separately)
   │      foreach TrackSpan in FindObjectsOfType<TrackSpan>:           AddSpan(s)
   │      HasPopulatedCollections = true
   │      foreach TrackMarker in _pendingTrackMarkers: RegisterTrackMarker(m)
   │      _pendingTrackMarkers.Clear()
   │      Messenger.Default.Send(default(GraphDidRebuildCollections))
   ▼
TrainController.GraphDidRebuildCollections (host-only) at TrainController.cs:2268
   – walks Cars; cars whose Location.segment isn't in the rebuilt graph become "lost"
   – emits a LostCarCuts entry per cut and broadcasts RemoveCars
   – this is the only "what happens when topology changes underfoot" reaction
```

**Critical timing:** `[DefaultExecutionOrder(-1)]` ensures `Graph.Awake` runs before sibling `MonoBehaviour.Awake`s in the same scene load. But inside `Awake` itself, `GetComponentsInChildren<TrackNode>()` only walks the Graph's *own* children. If your mod adds nodes/segments to a different parent transform (or via `Instantiate` after `Awake`), they won't be picked up unless you call `Graph.AddNode/AddSegment` directly OR call `Graph.RebuildCollections()` after parenting them under the Graph's root.

`HasPopulatedCollections` (`Graph.cs:135`) flips `true` at the end of the first rebuild. Code that runs during arbitrary `Awake` and tries to `Graph.Shared.GetSegment(...)` must check this — markers handle this themselves via `_pendingTrackMarkers` queueing (`RegisterTrackMarker` deferral at `Graph.cs:1078`).

### `RebuildCollections` callers

| Caller | When | Note |
|---|---|---|
| `Graph.Awake` | Once at scene load | Initial build |
| `MapFeatureManager.HandleFeatureEnablesChanged` | Any feature unlock/lock that changes a track group | `MapFeatureManager.cs:170`. Followed by `Messenger.Send(MapFeatureChangedGraph)` and a delayed `TrackObjectManager.Instance.Rebuild()` (which calls `RebuildCollections` *again* via `TrackObjectManager.Rebuild` → `_graph.RebuildCollections()` at `TrackObjectManager.cs:251`). So a single feature unlock causes **two** rebuilds inside one frame's worth of work. |
| `TrackObjectManager.Rebuild` | After `MapFeatureManager` schedules `DelayedRebuildTrack` (1 frame) | Rebuilds the visual track meshes and re-runs `Graph.RebuildCollections` |
| `OpsController.RebuildCollections` | Internal to OpsController; **NOT** Graph (same name, different class) | False positive in grep |

There is **no public `RebuildCollections` event other than `GraphDidRebuildCollections`** Messenger. `MapFeatureChangedGraph` Messenger fires after the rebuild, but only when a feature actually changed; a manual `Graph.RebuildCollections()` does not fire it.

### What `RebuildCollections` invalidates

Cleared at the top:
- `nodes`, `segments`, `spans` dicts
- `_nodeConnectionsCache` (node id → list of incident segments)
- `_nodeIsDeadEndCache` (node id → optional direction)
- `_cachedReachableSegments` ((segment, end) → (normal, reversed))
- `_decodedSwitchCache` (node id → decoded switch info)

**NOT cleared:**
- `_segmentCurveCache` (the `LineCurve` polyline approximations used by `TryGetLocationFromGamePoint`).
- `_curvatureSampleCache` (per-segment 5-byte curvature samples).
- `_trackMarkers` (segment-id-keyed marker hash sets) — markers are *not* re-registered; only `_pendingTrackMarkers` is flushed.

Per-node invalidation lives in `InvalidateNode(node)` (`Graph.cs:270`):

```csharp
public void InvalidateNode(TrackNode node)
{
    foreach (TrackSegment item in SegmentsConnectedTo(node))
    {
        item.InvalidateCurve();
        _segmentCurveCache.Invalidate(item.id);
        _curvatureSampleCache.Remove(item.id);
    }
    _nodeIsDeadEndCache.Remove(node.id);
    _nodeConnectionsCache.Remove(node.id);
}
```

Called from `AddSegment(..., invalidateNodes: true)` and `OnNodeDidChange(node)` (the public event invalidator). **Throwing a switch does NOT call `OnNodeDidChange`** — `isThrown.set` (`TrackNode.cs:24`) only fires `OnDidChangeThrown` and `SwitchThrownDidChange`. Caches that depend on `isThrown` (none of the Graph caches do — `DecodedSwitchInfo` is purely geometric) are unaffected.

`OnNodeDidChange` is only called by external code (e.g., `CTCSwitchMonitor.UpdateSwitchesForCTC` to invalidate after `IsCTCSwitch` flag changes). The `NodeDidChange` C# event on Graph is consumed by `TrackObjectManager` and a few visual systems.

### Patch candidates (build/cache)

| Method | Why patch |
|---|---|
| `Graph.RebuildCollections` | Postfix to inject mod-side derived data (custom indices, signal/block precomputation, alternate routing graphs). Run after `Messenger.Send(GraphDidRebuildCollections)` to ensure all vanilla consumers have settled. |
| `Graph.AddSegment(TrackSegment, bool invalidateNodes)` | Per-segment add; runs once per visible (group-enabled) segment per rebuild. Patch to filter or stamp metadata. |
| `Graph.InvalidateNode` | Per-node cache wipe. Patch to invalidate mod-defined caches in lockstep. |
| `Graph.OnNodeDidChange` | Public invalidator. Subscribe to `NodeDidChange` event rather than patching. |
| `Graph.AddNode` | Per-node add. The string-id collision check logs an error and skips; mods that runtime-add nodes should use the `IdGenerator.TrackNodes.Next()` path to get a fresh id. |
| `Graph.AddSpan` (private) | Per-span add; called from `RebuildCollections` only. **Spans created at runtime won't be in `Graph.spans`** until next rebuild — call `Graph.RebuildCollections()` after parenting. |

---

## `Graph.PositionRotation` and `GetPosition(Location)`

```csharp
public struct PositionRotation(Vector3 position, Quaternion rotation)
{
    public Vector3 Position = position;
    public Quaternion Rotation = rotation;
    public PositionRotation Project(float distance)
    {
        if (distance == 0f) return this;
        return new PositionRotation(Position + Rotation * (Vector3.forward * distance), Rotation);
    }
}
```

(`Graph.cs:35-54`.) Plain value type, no equality, no constants. Returned by `Graph.GetPositionRotation` and `TrackMarker.PositionRotation`.

### `GetPositionRotation` (`Graph.cs:719`)

```csharp
public PositionRotation GetPositionRotation(Location loc, PositionAccuracy accuracy = PositionAccuracy.Standard)
{
    loc = loc.Clamped(out var remainder);
    TrackSegment segment = loc.segment;
    if (segment.turntable != null)
        return segment.turntable.GetPositionRotation(loc).Project(remainder);
    segment.GetPositionRotationAtDistance(loc.distance, loc.end, accuracy, out var position, out var rotation);
    return new PositionRotation(position, rotation).Project(remainder);
}
```

**`PositionRotation.Position` is in game space** (the canonical, persistent coordinate system). To render in Unity world space, call `WorldTransformer.GameToWorld(position)` or `position.GameToWorld()` extension. The `Track.Graph.PositionRotation` reference in [floating-origin › key entry points](floating-origin.md#key-entry-points-at-a-glance) is exactly this struct.

`Clamped(out remainder)`: if the input `Location.distance` is negative or past the segment length, the location is clamped to `[0, length]` and the overflow is fed into `Project(remainder)` (extends straight forward along the rotation by `remainder` meters). This means **calling `GetPosition` on an "invalid" overshooting Location returns a sensible answer** — but the result is no longer on-rail; the projection assumes straight-line continuation. Useful for end-of-track stop placement and for "ghost positions" past the end.

`PositionAccuracy` (in `Track/PositionAccuracy.cs`) — modes `Standard`, `Precise`, etc. — controls how `BezierDistanceParameterCache` resolves the parameter for a distance. Hot path; `Standard` does an interpolated lookup and is the default everywhere.

### `GetPosition(Location)`

```csharp
public Vector3 GetPosition(Location loc) => GetPositionRotation(loc).Position;
```

Convenience wrapper. `Location.GetPosition(accuracy)` (the `Location` struct method, `Location.cs:79`) is similar but goes directly through `TrackSegment.GetPositionRotationAtDistance` — it does NOT honor the turntable bridge re-routing in `Graph.GetPositionRotation`, so for a Location on a turntable bridge segment you must use `Graph.GetPosition(loc)`, not `loc.GetPosition()`.

### `GetPositionDirection(Location)` (`Graph.cs:741`)

```csharp
public PositionDirection GetPositionDirection(Location loc) {
    var pr = GetPositionRotation(loc);
    return new PositionDirection(pr.Position, pr.Rotation * Vector3.forward);
}
```

`PositionDirection` is a sibling struct (`Graph.cs:56`) — `(Position, Direction)` instead of `(Position, Rotation)`. Used where only the heading vector matters (e.g., dead-end direction in `NodeIsDeadEnd`).

### Patch candidates (positioning)

| Method | Why patch |
|---|---|
| `Graph.GetPositionRotation` | Hot path; called by every car physics tick, every `Step.Position` access, every Location → Unity coord. Patching is risky. Prefer patching `TrackSegment.GetPositionRotationAtDistance` if you only need to alter per-segment math. |
| `Graph.GetPosition` | Trivially forwards; not worth patching. |
| `PositionRotation.Project` | Affects how clamped locations extrapolate past segment ends. Patch to clamp instead of extrapolate. |

### Performance notes

- `GetPositionRotation` is non-allocating in the fast path (segment cache hit + segment.GetPositionRotationAtDistance). The `try/catch/finally` block adds a small constant overhead on Mono; `try` with no `throw` is essentially free on .NET Standard 2.1.
- `loc.Clamped(out remainder)` allocates nothing (struct return).
- `Project(0)` short-circuits; pay attention if you find yourself synthesizing locations that always land exactly on the end.
- A cold curve (`TrackSegment._curve` not yet built) triggers `CreateBezier` + `BezierDistanceParameterCache` build on first call. After that, lookups are interpolated table reads.

---

## `Graph.LocationByMoving` — the universal walk

```csharp
public Location LocationByMoving(Location start, float distance, bool checkSwitchAgainstMovement = false, bool stopAtEndOfTrack = false)
public Location LocationByMoving(Location start, float distance, bool checkSwitchAgainstMovement, EndOfTrackHandling endOfTrackHandling)
```

(`Graph.cs:340/350`.) Single most-called Graph mutator-of-cursor. Takes a starting `Location`, a signed distance, and walks across switches. The first overload converts `stopAtEndOfTrack` to `Throw` or `Clamp`; the second adds `Unclamped` (let distance run past end → produces an invalid Location with `distance > length`).

```csharp
public enum EndOfTrackHandling { Throw, Clamp, Unclamped }
```

(`Graph.cs:17-22`.)

### Algorithm (the actual loop)

```
num = 1000;             ← safety cap
location = start;
flag = (distance < 0); if (flag) { location = location.Flipped(); distance = -distance; }
remaining = distance;
while (remaining > 0):
    untilEnd = location.DistanceUntilEnd();
    if (remaining < untilEnd):
        location = location.Moving(remaining); remaining = 0
    else:
        remaining -= untilEnd
        end = (location.EndIsA ? End.B : End.A)
        next = LocationFrom(location.segment, end, checkSwitchAgainstMovement)
        if (!next.HasValue):
            switch endOfTrackHandling:
                Throw     → throw new EndOfTrack()
                Clamp     → return new Location(location.segment, location.segment.GetLength(), location.end)
                Unclamped → next = location.Moving(untilEnd + remaining); remaining = 0
        location = next.Value
    num--; if (num <= 0) throw new Exception("Maximum iterations reached at " + location);
return flag ? location.Flipped() : location;
```

(`Graph.cs:350-405`.)

**1000-iteration safety cap.** A walk that tries to traverse 1000 segments without reaching the requested distance throws. For typical 50–200m segments, that's a 50–200km hard ceiling — far more than any vanilla use case. Mods doing very long forward walks should beware.

**`checkSwitchAgainstMovement: true`** routes through `LocationFrom` → `CheckSwitchAgainstMovement`. Throws `SwitchAgainstMovement(node)` when crossing a 3-way node from a non-routed direction (e.g., entering the diverging branch when the switch is set to normal). AutoEngineer catches this in its lookahead `Search` to truncate planning at fouling distance; player-issued moves catch it for "switch against movement" UI feedback.

**Negative distance (back-walk)**: implemented as `Flipped → forward walk → Flipped`. The result is in the same `end` orientation as the input (because of the second flip). Subtle: the *intermediate* walks see the flipped location, so `checkSwitchAgainstMovement` is evaluated against the **back-walk** direction.

### `LocationFrom(segment, end, checkSwitchAgainstMovement)` (`Graph.cs:407`)

```csharp
public Location? LocationFrom(TrackSegment seg, TrackSegment.End end, bool checkSwitchAgainstMovement = false)
{
    TrackNode trackNode = ((end == TrackSegment.End.B) ? seg.b : seg.a);
    SegmentsReachableFrom(seg, end, out var normal, out var reversed);
    TrackSegment trackSegment;
    if (normal != null && reversed == null) {
        trackSegment = normal;
        if (checkSwitchAgainstMovement) CheckSwitchAgainstMovement(seg, trackSegment, trackNode);
    } else {
        if (!(normal != null) || !(reversed != null)) return null;       // dead end
        trackSegment = (trackNode.isThrown ? reversed : normal);          // throw decides
    }
    bool flag = trackSegment.a == trackNode;
    Location value = new Location(trackSegment, 0f, (!flag) ? TrackSegment.End.B : TrackSegment.End.A);
    if (Math.Abs(value.DistanceUntilEnd()) < 0.1f) throw new Exception("DistanceUntilEnd is zero");
    return value;
}
```

Single switch step. Returns `null` at a true dead end (no incident segments past `end`). Returns a Location anchored at distance 0 on the next segment, with `end` set so the *opposite* end is the heading direction. Throws if the next segment has a near-zero length (`< 0.1m`) — defensive; would otherwise produce a stuck cursor.

### `SegmentsReachableFrom(segment, end, out normal, out reversed)` (`Graph.cs:472`)

The cache layer. Looks up `((segment, end))` in `_cachedReachableSegments`. On miss:
1. Get the node at `end`.
2. Collect all other segments incident to that node into `_segmentsReachableFromOthers` (a single reusable scratch list).
3. If 2+ others: `DecodeSwitchAt(node)`. If `enter == segment`, return `(a, b)`. Else (we entered from a or b), return `(enter, null)`.
4. Else: return the single other (or null at dead end).

**Cache exclusion:** `_cachedReachableSegments` is NOT populated when either the next segment is a turntable bridge OR the node is a turntable node. This keeps stale routing from getting cached during turntable rotation.

### `CheckSwitchAgainstMovement` (`Graph.cs:437`)

```csharp
private void CheckSwitchAgainstMovement(TrackSegment seg, TrackSegment nextSegment, TrackNode node)
{
    SegmentsReachableFrom(nextSegment, (node == nextSegment.b) ? End.B : End.A, out var normal, out var reversed);
    if (normal != null && reversed != null) {
        bool num  = !node.isThrown && normal != seg;
        bool flag =  node.isThrown && reversed != seg;
        if (num || flag) throw new SwitchAgainstMovement(node);
    }
}
```

Called *after* picking the next segment, looking *back* through the next segment's reachable-set to confirm `seg` is the routed branch back. Only triggers from the trailing side of a 3-way (`normal != null && reversed != null` requires nextSegment to also see two reachables back). Catches the case "next segment routes back to a different branch" — i.e., we just walked through a switch in the diverging direction.

### Patch candidates (walk)

| Method | Why patch |
|---|---|
| `Graph.LocationByMoving` (both overloads) | The universal walk. Hot path. Replace to inject custom routing (e.g., a mod that wants to forbid certain segments). Must preserve the `checkSwitchAgainstMovement` exception flow — many callers rely on it. |
| `Graph.LocationFrom` | One step. Patch to inject "magic switch" overrides (e.g., AI throws this switch for me). Bypassing `isThrown` here makes the train walk through a switch you haven't actually thrown. |
| `Graph.SegmentsReachableFrom` | Cache-aware reachable-segments. Patch to add custom topology (e.g., 4-way wyes). Affects routing AND switch decoding. |
| `Graph.CheckSwitchAgainstMovement` | Switch-against detection. Patch to allow rough-handling-of-switches. |

### Gotchas

- **Negative-distance walks `Flip → walk → Flip`** — the input cursor end is preserved in the result. If you need the cursor to actually face backward at the end (e.g., for a "back up by N meters and use this as the new heading"), call `.Flipped()` on the result yourself.
- **`Unclamped` produces an invalid Location.** `.IsValid` returns false because `distance > segment.GetLength()`. Most consumers (including `GetPositionRotation`) handle this via `Clamped(out remainder)` and `Project(remainder)`, but `EnumerateTrackMarkers` and `LocationFrom` will misbehave.
- **`SwitchAgainstMovement` thrown mid-walk leaves the cursor at the offending node.** Catch it and use `e.Node` for context. This is an exception used as control flow — it appears in production code paths and is not an error.
- **`EndOfTrack` similarly carries no payload.** Catchers know what they were walking from.
- **The 1000-iteration cap** is a hard cap. A walk through extremely fragmented track (many tiny segments) can hit it before the requested distance is exhausted.

---

## Switch decoding: `DecodeSwitchAt`

The geometric switch-classifier; cached per-node id.

```csharp
public bool DecodeSwitchAt(TrackNode node, out TrackSegment enter, out TrackSegment a, out TrackSegment b)
```

(`Graph.cs:525-585`.) Returns `false` if the node has != 3 incident segments. Otherwise:

1. Pull the three incident segments via `SegmentsConnectedTo(node)`.
2. Use `TrackNode.SegmentCanReachSegment(seg0, seg1)` (`TrackNode.cs:87`) — geometric tangent reachability — for each of the three pairings. The pair that **both** other segments can reach defines `enter` (the trailing/single side); the remaining two are diverging branches `a`/`b`.
3. **Sort `a` and `b`:**
   - If `a.priority != b.priority`, higher `priority` wins normal slot `a` (priority range `[-2, +2]`).
   - Else use `DivergingAngleOf(node, seg)` (`Graph.cs:587`) — the smaller-angle branch is normal.
4. Cache `DecodedSwitchInfo` (`Graph.cs:24`) by node id.

### `TrackNode.SegmentCanReachSegment` (`TrackNode.cs:87`)

```csharp
public bool SegmentCanReachSegment(TrackSegment a, TrackSegment b)
{
    Vector3 vector  = TangentPointAlongSegment(a, 1f);
    Vector3 vector2 = TangentPointAlongSegment(b, 1f);
    return Vector3.SqrMagnitude(vector - vector2) > 0.1f;
}
```

`TangentPointAlongSegment(seg, d)` (`TrackNode.cs:76`) returns a point 1 m along the segment, *signed by which side of the node the segment leaves toward* (it picks `+forward` or `-forward` based on which is closer to the other endpoint of the segment). So if both segments leave on the *same* tangent side of the node, their 1-m points are nearly identical and `SegmentCanReachSegment` returns `false` (the two segments are co-linear continuations through the node — not a switchable pairing).

Two segments are "reachable" iff `Vector3.SqrMagnitude > 0.1` → `magnitude > sqrt(0.1) ≈ 0.316 m`. (The track-topology sheet says `>= 0.32 m`; the actual test is `> sqrt(0.1) m`.)

### Cache invalidation

`_decodedSwitchCache` is cleared by `RebuildCollections` (top of method). It is **not** cleared by `InvalidateNode` directly — `OnNodeDidChange(node)` only calls `InvalidateNode`, not `GraphDidChange()` (which clears the decoded-switch and reachable-segments caches). The single internal call to `GraphDidChange()` is from `Graph.AddSegment(string id, TrackNode a, TrackNode b)` (the runtime-add overload at `Graph.cs:301`). **Mods that runtime-modify topology must call `GraphDidChange()` (private — patch needed) or `RebuildCollections()` to clear `_decodedSwitchCache`.**

### Patch candidates

| Method | Why patch |
|---|---|
| `Graph.DecodeSwitchAt` | Replace switch classification (e.g., support 4-way wyes by walking three pairings differently). Cached — invalidate `_decodedSwitchCache` if patching dynamically. |
| `TrackNode.SegmentCanReachSegment` | Override the geometric reachability test (e.g., for non-standard node geometry). Lower-level than `DecodeSwitchAt`. |
| `Graph.IsSwitch` | Defines what counts as a switch. Currently `SegmentsConnectedTo(node).Count() == 3`. Patch to count 4-way nodes if your mod adds them. |

---

## Group enabling: `SetGroupEnabled` / `SetGroupAvailable`

```csharp
public bool SetGroupEnabled(string groupId, bool groupEnabled);     // Graph.cs:1177 — fires GraphDidChangeEnabledGroups
public bool SetGroupAvailable(string groupId, bool groupAvailable); // Graph.cs:1197 — fires GraphDidChangeAvailableGroups
```

Both manipulate the `enabledGroupIds`/`availableGroupIds` `List<string>` fields (which are public, mutable, and serialized into the scene) by re-creating the list with set semantics. Returns `true` iff the count changed.

**Neither method calls `RebuildCollections`.** `MapFeatureManager.HandleFeatureEnablesChanged` (`MapFeatureManager.cs:159-172`) accumulates a `flag3` over all `UpdateFeatureGraphGroups` calls and explicitly invokes `RebuildCollections` once if any group changed. Mods calling `SetGroupEnabled` directly must call `RebuildCollections` themselves.

### Group semantics

- `enabledGroupIds`: a segment whose `groupId` is **not** in this list is skipped during `Graph.AddSegment` (`Graph.cs:225-232`). I.e., the segment doesn't exist in the graph at all.
- `availableGroupIds`: sets `segment.Available = true/false`. Segments with `Available=false` exist in the graph (visible in `Graph.Segments`, walkable by `LocationByMoving`) but flagged for UI/picking (e.g., player can see the track but `IPickable` colliders are off).

A segment with empty `groupId` (`null` or `""`) is always enabled and available (`Graph.cs:235-238`).

### MP authority caveat

`SetGroupEnabled`/`SetGroupAvailable` are `public` and have **no host check**. A client calling them only mutates its local Graph; nothing broadcasts. The propagation channel is the `mapFeatures.features` HostOnly KVO observed by `MapFeatureManager` on every peer, which independently calls these methods locally on every peer. **Mods that toggle groups directly must broadcast their own state somehow** (e.g., a custom HostOnly KVO key + observer on every peer) or stick to the MapFeature pipeline.

### Patch candidates

| Method | Why patch |
|---|---|
| `Graph.SetGroupEnabled` | Auto-call `RebuildCollections` (currently the caller's responsibility). |
| `Graph.SetGroupAvailable` | Same, plus: dirty mod-side picking caches. |

---

## Marker registration & enumeration

Per-segment hash sets, keyed by segment id.

```csharp
private readonly Dictionary<string, HashSet<TrackMarker>> _trackMarkers;
private readonly HashSet<TrackMarker> _pendingTrackMarkers;
public void RegisterTrackMarker(TrackMarker tm);    // Graph.cs:1076
public void UnregisterTrackMarker(TrackMarker tm);  // Graph.cs:1097
public TrackMarker MarkerForId(string id);          // Graph.cs:1106 — O(N)
public IEnumerable<TrackMarker> EnumerateTrackMarkers(Location start, float distance, bool sameDirection); // Graph.cs:1121
```

`RegisterTrackMarker`: if `Graph.HasPopulatedCollections == false`, queues into `_pendingTrackMarkers` (flushed at the end of `RebuildCollections`). Otherwise inserts into the segment-keyed bucket. Logs warning + skips if `marker.Location` is null/invalid.

`UnregisterTrackMarker` (`Graph.cs:1097`): walks **every** segment-keyed set (`O(segments)`), removing the marker. Cheap if there are few segments; on a yard map with hundreds of segments this is non-trivial.

`EnumerateTrackMarkers` (`Graph.cs:1121-1175`):
- Walks segments via `LocationFrom` until cumulative distance exceeds `distance`.
- For each visited segment, looks up `_trackMarkers[segment.id]`, filters markers within range, sorts by `DistanceTo(end)`.
- `sameDirection: true` filters markers whose `Location.end` matches the cursor's `end` (used to skip signals facing away).
- Negative distance: flips the cursor first.

**No exception handling for `EndOfTrack`/`SwitchAgainstMovement`.** `EnumerateTrackMarkers` calls `LocationFrom(segment, end)` with `checkSwitchAgainstMovement: false` (the default), so it walks freely across switches — but a true dead end terminates the iteration cleanly via the `if (!location.HasValue) break;` (`Graph.cs:1145`). Mods consuming this enumerable should not assume the iteration completed any specific distance.

### Gotchas

- **`MarkerForId` is O(N) over all markers.** Don't call in tight loops. Cache the lookup.
- **Marker re-register without unregister leaves stale entries.** `OnDisable` should unregister; if a mod moves a marker to a different segment, it must `Unregister → Register` (or just call `Register` again — but the prior entry stays in the old segment's set, since `Register` doesn't dedupe across segments).

---

## Routing: `Track.Search`

The path-finding namespace. Lives outside `Track/` so it can be replaced wholesale without touching topology.

### `RouteSearch.Step` (`RouteSearch.cs:27`)

```csharp
public readonly struct Step
{
    public readonly Location Location;
    [CanBeNull] public readonly TrackNode Node;     // null when step is a Location-only marker (start/end/cleared-switch)
    public readonly StepDirection Direction;        // Out or Back, relative to train heading
    public readonly float Distance;                 // segment distance contribution (NOT cost)
    public readonly StepFlag Flags;
    private readonly Graph _graph;
    public Vector3 Position { get; }                // game-space; uses Node.transform.GamePosition() OR _graph.GetPosition(Location)
}
```

**Two constructors:**
- `Step(Location, StepDirection, float distance, Graph, StepFlag)` — Node is null. Used for start, destination, mid-segment cleared-switch resumption.
- `Step(Location, [NotNull] TrackNode, StepDirection, float distance, Graph, StepFlag)` — Node-anchored step (passed through a junction).

`Position` (`RouteSearch.cs:42`):

```csharp
public Vector3 Position => Node != null ? Node.transform.GamePosition() : _graph.GetPosition(Location);
```

Node-anchored steps use the node's transform `GamePosition` (i.e., `transform.position - WorldTransformer.GameToWorld(0)`); Location-only steps go through `Graph.GetPosition`. Both return game-space coordinates.

`Step` equality is `(Node == other.Node) && QuantizedLocationsEqual(Location, other.Location) && Direction == other.Direction`. Quantization is to 0.1 m via `Searcher.QuantizedLocation`. Two Steps that resolve to "the same place" within 0.1 m will compare equal.

`WithLocation(newLocation, newDistance)` — return a new Step with replaced location/distance, preserving Direction/Graph/Flags.

`HasFlag(StepFlag flag)` — bitwise AND check.

### `StepFlag` (`RouteSearch.cs:19`) — **the complete enum**

```csharp
[Flags]
public enum StepFlag
{
    None           = 0,
    EnterCTCSwitch = 1,    // entering a CTC-locked switch from the trailing side
    SearchLimit    = 2,    // step landed at a limitSwitchIds-listed switch
}
```

Just two flags. `EnterCTCSwitch` is set in `Searcher.FlagsForNode` (`Searcher.cs:284`) when entering a switch that is `IsCTCSwitch && !IsCTCSwitchUnlocked`, and the entry is via the `enter` segment (only the trailing side gets this flag — entering from `a` or `b` means you're being routed *through* the switch, not against it).

`SearchLimit` is set when the step's node id is in the `limitSwitchIds` HashSet passed to `FindRoute`. AutoEngineer uses this to plant "search limit" markers (e.g., at known-blocking foul switches). When a `SearchLimit`-flagged step is added to the open list, the search limit is **reset to `2000f`** (`Searcher.cs:527`) — i.e., "scan another 2 km past this point regardless of how much budget remained." This effectively makes the search continue past such markers with a generous lookahead. The `IsGoal` check returns `true` when `SearchLimit < 0` (`Searcher.cs:123`), so a `SearchLimit` step that isn't followed by another reset will eventually terminate the search (with `Step.Flags & SearchLimit` set on the last step).

**Note:** `SearchLimit` does NOT mean "the search hit its iteration cap." `RouteSearch.FindRoute` returns `false` and produces an empty `routeStepsOut` if `maxIterations` is exhausted; there's no flag for that. The two `StepFlag`s are both about *position* in the topology, not about search execution.

### `StepDirection` (`StepDirection.cs:3`)

```csharp
public enum StepDirection { Out, Back }
```

`Out` = same direction as the train heading at search start. `Back` = reversed (from the back-seed). The seed pair (`Searcher.cs:83-88`) puts both directions in the open set; the cheaper wins.

### `Cost` (`Cost.cs:5` — internal, used by AStar)

```csharp
internal readonly struct Cost(float distanceTraveled, float heuristicCost) : IComparable<Cost>
{
    public readonly float distanceTraveled;
    private readonly float heuristicCost;
    private readonly float totalCost;        // distanceTraveled + heuristicCost
    public int CompareTo(Cost other) => totalCost.CompareTo(other.totalCost);
}
```

Internal A* cost. Not directly used by `Searcher` (which uses A* `Node.Cost` floats); included for completeness.

---

## `HeuristicCosts` — the tuning surface

```csharp
public struct HeuristicCosts
{
    public int DivergingRoute;        // bonus per diverging branch taken
    public int ThrowSwitch;           // bonus per switch that needs throwing
    public int ThrowSwitchCTCLocked;  // bonus per CTC-locked switch (effectively forbidden if huge)
    public int CarBlockingRoute;      // bonus per blocking car encountered

    public static HeuristicCosts Zero       => default(HeuristicCosts);
    public static HeuristicCosts AutoEngineer => new HeuristicCosts {
        DivergingRoute       = 20,
        ThrowSwitch          = 10,
        ThrowSwitchCTCLocked = 1000,
        CarBlockingRoute     = 5000
    };
}
```

(`Track.Search/HeuristicCosts.cs:3-22`.) Two presets — that's it. Mods wanting custom presets must construct `HeuristicCosts` literals.

### What each cost means

Inside `Searcher.GetCostToTraverseSegment` (`Searcher.cs:139`):

```csharp
segmentCost = segment.GetLength() * (1 + (-segment.priority) / 5f);
branchCost  = 0;
if (isDivergingRoute)                         branchCost += DivergingRoute;
if (isDivergingRoute != isThrown)             branchCost += isCTCLockedSwitch ? ThrowSwitchCTCLocked : ThrowSwitch;
```

- **Segment base cost** = bezier arc length × `(1 + (-priority)/5)`. Priority `+2` → 0.6× (cheaper); priority `0` → 1× (default); priority `-2` → 1.4× (penalized). High-priority mainlines are *strongly* preferred.
- **`DivergingRoute`** is a per-branch bonus for *taking* the diverging side, regardless of throw state. Discourages diverging routes when a normal-side option exists. AutoEngineer: `+20`.
- **`ThrowSwitch`** is paid when `isDivergingRoute != isThrown` — i.e., the search route requires the switch to be in a *different* state than it currently is. AutoEngineer: `+10` (small — throwing is normal during routing).
- **`ThrowSwitchCTCLocked`** is paid in place of `ThrowSwitch` if the switch is `IsCTCSwitch && !IsCTCSwitchUnlocked`. AutoEngineer: `+1000` — large enough to prefer a long detour, but not infinite (the AI will route through a CTC-locked switch if there's no other path). Note: this is paid even if the switch is *already in the right position* — wait, no, it's only paid when `isDivergingRoute != isThrown` (the throw is needed). A CTC switch already in the routed direction costs nothing here.
- **`CarBlockingRoute`** is added per blocking car encountered in `CheckForCars` (`Searcher.cs:376`). AutoEngineer: `+5000` — prefer to detour around blocking cars unless there's literally no alternative.

### `HeuristicCosts.Zero` consumers

- `RouteSearchPoints.FindPoints` (`RouteSearchPoints.cs:13`) — span polylines need *some* path, no preference.
- `GraphRouteSearchExtension.FindRoute(Location, Location)` (the one returning `List<TrackSegment>`) (`GraphRouteSearchExtension.cs:13`).
- `GraphRouteSearchExtension.TryFindDistance` (`GraphRouteSearchExtension.cs:58`).

### `HeuristicCosts.AutoEngineer` consumers

- `AutoEngineerPlanner.UpdateWaypointRouteIfNeeded` (`AutoEngineerPlanner.cs:1513`) — both the reconnaissance pass (`checkForCars: false`) and the real pass (`checkForCars: true`).

### Patch candidates

| Method | Why patch |
|---|---|
| `HeuristicCosts.AutoEngineer` (static prop) | Replace via Harmony getter to globally re-tune AI behavior. |
| `HeuristicCosts.Zero` (static prop) | Replace to add slight cost preferences to "any route" searches (carefully — might break TrackSpan caching). |
| `Searcher.GetCostToTraverseSegment` | Replace the whole cost model. Hot — runs per-neighbor expansion. |

---

## `RouteSearch.FindRoute` — the public API

```csharp
public static List<Step> FindRoute(this Graph graph, Location start, Location end,
    HeuristicCosts heuristicCosts,
    bool   checkForCars            = false,
    float  trainLength             = 0f,
    float  trainMomentum           = 0f,
    int    maxIterations           = 5000,
    HashSet<Car> checkForCarsIgnored = null,
    bool   enableLogging           = false);

public static bool FindRoute(this Graph graph, Location start, Location end,
    HeuristicCosts heuristicCosts,
    List<Step> routeStepsOut,
    out Metrics metrics,
    bool   checkForCars            = false,
    float  trainLength             = 0f,
    float  trainMomentum           = 0f,
    int    maxIterations           = 5000,
    HashSet<Car> checkForCarsIgnored = null,
    HashSet<Car> checkForCarsImpasse = null,
    HashSet<string> limitSwitchIds = null,
    bool   enableLogging           = false);
```

(`RouteSearch.cs:128/135`.) The first overload allocates a list and returns it; the second writes into a caller-provided list and returns success. Both construct a fresh `Searcher` per call.

### Parameters

- `heuristicCosts` — see [HeuristicCosts](#heuristiccosts--the-tuning-surface).
- `checkForCars` — when true, scans each candidate segment via `TrainController.CheckForCarAtLocation` for blocking cars. False is much faster (skips the entire `CheckForCars` pipeline).
- `trainLength` — > 0.1 enables `_mustClearSwitches` mode. The back-seed is moved by `-trainLength` (clamped) and flipped, so the search "knows" the train extends behind the head end. Switches the search passes through become deferred `ClearSwitch` follow-ups (the search must wait until the train clears the switch before it can throw it).
- `trainMomentum` — added as the back-seed's `OneTimeCost`. AutoEngineer sets this to `2 × stoppingDistance + 50m` (`AutoEngineerConfig.momentumFactor=2`, `momentumOffset=50`). The back-seed pays this cost up front, biasing the search toward NOT reversing direction unless the savings exceed the momentum cost.
- `maxIterations` — A* outer-loop cap. Default 5000. **The 200 in `RouteSearchPoints.FindPoints` is a separate hardcoded value.**
- `checkForCarsIgnored` — cars treated as not-blocking (e.g., the searching consist's own cars).
- `checkForCarsImpasse` — cars that **truncate** the route (the search returns at the car's position, not a "blocked" empty). AutoEngineer puts the `Couple` target car here.
- `limitSwitchIds` — switch node ids that, when reached, mark the step `SearchLimit` (and reset SearchLimit budget to 2000m).
- `enableLogging` — flips a `_enableLogging` field. The actual logging is gated by `[Conditional("LOG_ROUTE_SEARCH")]` (`Searcher.cs:130`); in shipped builds, `LogRoute` calls compile out entirely.

### `Metrics`

```csharp
public struct Metrics
{
    public int   Iterations;     // A* iterations consumed
    public float Distance;       // sum of step.Distance
}
```

(`RouteSearch.cs:12`.)

### Search seeds

`Searcher.Search` (`Searcher.cs:81`):

```csharp
List<SearchState> seeds = new List<SearchState>
{
    new SearchState(_origin, StepDirection.Out, 0f, 0f, _graph, float.PositiveInfinity)
};
Location backLocation = (_trainLength > 0.1f
    ? _graph.LocationByMoving(_origin, -_trainLength, false, EndOfTrackHandling.Clamp).Flipped()
    : _origin.Flipped());
seeds.Add(new SearchState(backLocation, StepDirection.Out, 0f, _trainMomentum, _graph, float.PositiveInfinity));
```

Two seeds: forward-facing at origin, back-facing at origin (or at -trainLength for long trains, with momentum cost loaded).

**Note: both seeds use `StepDirection.Out`.** The `Direction` is the *seed*'s heading; the result `Step.Direction` is propagated through neighbor expansion, with `FlipDirection` applied to the deferred `ClearSwitch` follow-up. So a returned route can have `Step.Direction == Back` for steps reached via a reversal, but the seed list uses both seeds as `Out`.

### Goal test

```csharp
private bool IsGoal(SearchState p)
{
    if (p.SearchLimit < 0f) return true;
    return EqualsDirectionless(p.Step.Location, _destination);
}
```

(`Searcher.cs:121`.) Two terminations: `SearchLimit < 0` or destination match (segment + quantized distance, ignoring end orientation). The SearchLimit termination means "the search ran out of budget within the imposed limit" — it returns success but the route ends at the limit, not the destination.

### Neighbor expansion (`Searcher.GetNeighbors`, `Searcher.cs:154`)

Two cases based on whether the current Step has a Node:

**Step at a node** (mid-route, just walked through a junction):
- `DecodeSwitchAt(node)`. If switch:
  - `_mustClearSwitches` mode: if last segment was `enter`, expand to both `normal` and `diverging`. Else (entered from `a` or `b`), expand only to `enter` (don't back into the diverging branches that share the switch).
  - Else: expand to all three (the search can pick any branch).
- Else: 2-way junction → expand to the one other segment.

**Step at a Location** (mid-segment, including start):
- If the Location's segment IS the destination's segment AND the destination is "ahead" → emit a direct-to-destination step.
- If `_mustClearSwitches` and no prior step (this is the back-seed initial expansion) → `AddNeighboursUnderTrain`.
- Else: emit a node-step at the far end of the segment.

`AddNodeFromSegment` (`Searcher.cs:237`):
- Skips if the candidate segment contains the previous node OR equals the previous segment (don't go back).
- Computes `isDivergingRoute = isSwitch && divergingSegment == segment` (taking the diverging branch).
- Computes `isCTCLockedSwitch = isSwitch && thisNode.IsCTCSwitch && !thisNode.IsCTCSwitchUnlocked`.
- Calls `GetCostToTraverseSegment(segment, isDivergingRoute, thisNode.isThrown, isCTCLockedSwitch, out segmentCost, out branchCost)`.
- If `_mustClearSwitches && enteringFromADivergingBranchOfThisSwitch && goingBackToEnter`: queue a `ClearSwitch(thisNode, _trainLength)` follow-up.
- Add the neighbor.

`AddNeighboursUnderTrain` (`Searcher.cs:304`): for the back-seed in `_mustClearSwitches` mode, walks back along the train length, queueing `ClearSwitch` for any switch the train is currently sitting on (so the search can't throw a switch with cars on it). The walk uses 0.001m steps and decrements `num` by `(distance + 0.001f)` each loop — looks fragile but robust against typical segment lengths.

### `ClearSwitch` follow-ups (`AddStepToOpenList`, `Searcher.cs:460`)

For each pending `ClearSwitch` whose `ClearDistance <= stepDistance` (i.e., we've moved past the point where the train's tail clears the switch), emit a **multistep neighbor** sequence:
1. A `SearchState` at the cleared switch's far point (ClearDistance meters past the current step's location).
2. A flipped-direction `SearchState` back at the switch node (to allow reversing direction once the switch is clear).

These are added via `ctx.AddMultistepNeighbor()`, which `AStar.Search` consumes by sequencing them: the cumulative cost of (cleared step → switch flip) is paid as a unit, and `cameFrom` chains them so the resulting route includes both as adjacent steps.

This is how the search produces routes like "go forward 50m to clear the switch, then back through it" without modeling the train's physical extent at every node expansion.

### `CheckForCars` (`Searcher.cs:376`)

Called from `AddNodeToOpenList`/`AddLocationToOpenList` when `_checkForCars` is true.

- If `_costs.CarBlockingRoute == 0`: short-circuit (no cost, no scan).
- Resolves `start` to the segment of `end` via `Location.TryMatchSegment` (collapse boundary-aliases).
- Walks 1m at a time from start to end, calling `EnemyCarAt(loc, 1f)` (which calls `TrainController.Shared.CheckForCarAtLocation`).
- For each unique blocking car id (deduped):
  - If in `_checkForCarsImpasse`: truncate the route at the previous probe point, return `true` (route ends here).
  - Else: add `_costs.CarBlockingRoute` to `extraCost`.

**Probe spacing:** for distance > 2m, samples at `1 + max(1, floor((distance - 2) / 5))` points along the segment. Sparse — a 50m segment gets ~11 samples (~5m apart). A car shorter than 5m between probe points may be missed. Vanilla cars are typically much longer, so this is fine.

### Heuristic

`CalculateHeuristicCost(Vector3)` (`Searcher.cs:540`):

```csharp
private float CalculateHeuristicCost(Vector3 stepPosition)
    => (stepPosition - _graph.GetPosition(_destination)).magnitude;
```

Straight-line game-space distance. Admissible (never overestimates) when costs are in length units and the train can travel in straight lines through obstacles — but `branchCost` (DivergingRoute, ThrowSwitch, etc.) inflates costs above pure distance, so the heuristic is **technically inadmissible** in `HeuristicCosts.AutoEngineer` mode. In practice this means A* may return a non-optimal route (cheaper-cost route exists but search settled for first-found goal). For routing in a real-world track network, the difference is usually small.

`SearchState.OneTimeCost` is added to the heuristic for the back-seed (loading the `trainMomentum` upfront). Doesn't affect admissibility argument since it's a constant offset on one seed.

### Quantization

`QuantizedLocation(loc) = (loc.segment.id, RoundToInt(loc.distance * 10), loc.end)` — 0.1 m precision, end-aware. Used as the closed-list equality key. `EqualsDirectionless` (`Searcher.cs:550`) ignores `end` — used for goal matching.

### Patch candidates

| Method | Why patch |
|---|---|
| `RouteSearch.FindRoute` (extension, both overloads) | Replace the public API. Constructs a `Searcher` per call — no shared state. |
| `Searcher.Search` | Replace the entire A* invocation (e.g., use a different algorithm — Dijkstra, BFS, weighted-A*, beam search). |
| `Searcher.GetCostToTraverseSegment` | Cost model. Hot. |
| `Searcher.GetNeighbors` | Topology expansion. Patch to inject custom adjacency (e.g., teleport portals). |
| `Searcher.CheckForCars` | Blocking-car detection — replace if mods need different "blocked" semantics (e.g., a coupled car is okay but a derailed car is not). |
| `Searcher.CalculateHeuristicCost` | Heuristic. Replace for non-distance metrics (e.g., elevation-aware). |

---

## `RouteSearchPoints.FindPoints` — route → polyline

```csharp
public static void FindPoints(this Graph graph, Location start, Location end, float step, string name,
    List<Vector3> output, [CanBeNull] List<TrackSegment> segmentsOut = null)
```

(`RouteSearchPoints.cs:9`.) Internal helper used by `TrackSpan.UpdateCachedPointsIfNeeded` (`TrackSpan.cs:318`), `AutoEngineerPlanner.IsUnderTrain` (`AutoEngineerPlanner.cs:1899`), and `TeleportLoadingIndustry` (`TeleportLoadingIndustry.cs:154`).

**Hard-coded params:**
- `HeuristicCosts.Zero` — any route will do.
- `checkForCars: false`, `trainLength: 0f`, `trainMomentum: 0f` — geometric search only.
- **`maxIterations: 200`** (`RouteSearchPoints.cs:13`). Hardcoded; not exposed.

**Failure mode:** if the search returns 0 steps (route not found OR `maxIterations` exhausted), logs `"<name>: Search failed after Xs"` and returns with empty `output`. `TrackSpan` consumers treat empty `_cachedPoints` as "this span is broken" and bail (with one-shot warnings).

**A `TrackSpan` lower→upper that requires more than 200 search iterations silently gets an empty point list.** For typical CTC blocks this is plenty (a few segments + maybe one switch), but very long custom spans — or spans crossing many fragmented yard segments — may exceed it. AutoEngineer's `IsUnderTrain` path uses spans of `_maximumLength` (consist length) and is generally fine.

Output polyline:
- For each pair of adjacent steps, compute the segment between them and call `AddPoints(loc, distance, step, graph, output)`.
- `AddPoints` walks the segment in `step`-meter increments, calls `Graph.GetPosition(loc)` at each, and appends to `output`. Deduplicates against the last point if within 0.001m.
- `segmentsOut` (if non-null) gets each visited `TrackSegment`.

`step` is typically 5m (`AutoEngineerPlanner.IsUnderTrain`) or 10m (`TrackSpan`, `TeleportLoadingIndustry`).

### Patch candidates

| Method | Why patch |
|---|---|
| `RouteSearchPoints.FindPoints` | Raise the 200-iteration limit (only place it appears); add custom `HeuristicCosts`; switch to `checkForCars` for routing through occupied track. |
| `RouteSearchPoints.AddPoints` (private) | Modify the polyline sampling cadence. |

---

## `Graph.TryGetLocationFromGamePoint` — point→Location

```csharp
public bool TryGetLocationFromWorldPoint(Vector3 worldPosition, float radius, out Location output);
public bool TryGetLocationFromGamePoint(Vector3 gamePosition, float radius, out Location output);
```

(`Graph.cs:644/650`.) `TryGetLocationFromWorldPoint` subtracts the Graph's own `transform.position` first, then defers to the game-space version. **The Graph's transform position is a per-machine local offset**; in MP, the world↔game transform may differ between peers (see [Floating Origin](floating-origin.md)) — so the world-point version uses the Graph's transform as the reference, NOT `WorldTransformer.GameToWorld`. For mods sending positions over the wire, always work in game space.

Algorithm:
1. Iterate every segment in `Graph.segments`.
2. For each, call `TryGetLocationFromPoint(segment, gamePos, radius, out)`. This:
   - Bounding-box filter: skips if `segment.BoundingBoxContains(queryPoint, radius)` is false.
   - Pulls the cached `LineCurve` polyline approximation from `_segmentCurveCache` (with its position offset).
   - For each `LineSegment` in the polyline, computes `ClosestPointTo(localPos)` and checks the magnitude.
   - Tracks the closest segment + its closest point.
   - Resolves the cumulative arc length to the chosen point and constructs a clamped `Location`.
3. Across all segments that returned a hit, picks the one whose `GetPosition(loc)` is closest to the query point.

**O(N) over segments.** No spatial index. On a large map (thousands of segments), this is slow — used for click-to-place picks, not in-update queries.

### Patch candidates

| Method | Why patch |
|---|---|
| `Graph.TryGetLocationFromGamePoint` | Add a spatial-index pre-filter (e.g., bounding-box quadtree) for performance. |
| `Graph.TryGetLocationFromWorldPoint` | Wrap world-space queries (rarely needed). |
| `Graph.TryGetLocationFromPoint` (private) | Per-segment hit test. The polyline approximation is the bottleneck. |

---

## Caching summary — what stales and when

| Cache | Field | Cleared by | Notes |
|---|---|---|---|
| Node lookup | `nodes` | `RebuildCollections` | string id → TrackNode |
| Segment lookup | `segments` | `RebuildCollections` | string id → TrackSegment |
| Span lookup | `spans` | `RebuildCollections` | string id → TrackSpan |
| Per-node connections | `_nodeConnectionsCache` | `RebuildCollections`, `InvalidateNode(node)` | node id → List<TrackSegment>; turntable nodes excluded |
| Dead-end detection | `_nodeIsDeadEndCache` | `RebuildCollections`, `InvalidateNode(node)` | node id → optional `Vector3?` direction |
| Reachable-from | `_cachedReachableSegments` | `RebuildCollections`, `GraphDidChange()` | `(segment, end) → (normal, reversed)`; turntables NOT cached |
| Switch decoding | `_decodedSwitchCache` | `RebuildCollections`, `GraphDidChange()` | node id → DecodedSwitchInfo |
| Curve polyline | `_segmentCurveCache` (`SegmentCache`) | `InvalidateNode(node)` only (not `RebuildCollections`) | per-segment LineCurve approx for hit-testing |
| Curvature samples | `_curvatureSampleCache` | `InvalidateNode(node)` only | 5-byte per-segment curvature |
| Markers | `_trackMarkers` | Not cleared by Rebuild — `_pendingTrackMarkers` only flushed | Markers re-register via OnEnable; if a marker is destroyed mid-rebuild it stays in the dict |
| Turntable controllers | `_cachedTurntableControllers` | Never (built lazily once, never invalidated) | `FindObjectsOfType<TurntableController>()` first call only |

**Critical:** `_segmentCurveCache` and `_curvatureSampleCache` survive `RebuildCollections`. A rebuild that *removes* a segment leaves its cache entry — harmless (won't be queried), but the entry occupies memory. A rebuild that *keeps* a segment with a different geometry... but segments don't change geometry without `InvalidateNode`. Should be fine.

**`_cachedTurntableControllers` is never invalidated.** A turntable destroyed at runtime stays in the cache, returning a destroyed `TurntableController` reference. Accessing `TurntableControllerForId` may return a destroyed object. Rare in practice (turntables are scene-baked).

---

## Performance — graph-walk costs

Empirical-ish notes from reading the code; not benchmarked.

| Operation | Cost | Hot path? |
|---|---|---|
| `Graph.GetPosition(loc)` | 1 dict lookup, segment cache hit, ~10-line bezier eval | **Yes** — every car physics tick, every step.Position |
| `Graph.LocationByMoving(loc, dist)` | Loop: 1 LocationFrom per segment crossed; up to 1000 iter cap | **Yes** — physics, AI, route search |
| `Graph.LocationFrom(seg, end)` | 1 SegmentsReachableFrom (cache hit) + Location constructor | Yes |
| `Graph.SegmentsReachableFrom(seg, end)` | 1 dict lookup for cache; on miss: SegmentsConnectedTo + DecodeSwitchAt | Cache hit cheap; miss expensive (DecodeSwitchAt) |
| `Graph.DecodeSwitchAt(node)` | 1 dict lookup; on miss: 3 SegmentCanReachSegment calls + DivergingAngleOf x2 | Miss = ~6 vector ops + 2 GetPosition calls — moderate |
| `Graph.SegmentsConnectedTo(node)` | Cache hit O(1); miss O(segments) — full segment scan | Miss expensive on large maps |
| `Graph.TryGetLocationFromGamePoint` | O(segments) bounding-box + polyline closest-point | **Slow** — click handlers only |
| `Graph.MarkerForId(id)` | O(markers) | Slow — cache externally |
| `Graph.EnumerateTrackMarkers(start, dist, sameDirection)` | O(segments traversed × markers per segment) + sort | Moderate |
| `RouteSearch.FindRoute` (5000 iter cap) | A* with ~5000 segment expansions worst-case | Slow — never per-frame |
| `RouteSearchPoints.FindPoints` (200 iter cap) | A* + per-step polyline sampling | Cached by TrackSpan |
| `Graph.RebuildCollections` | O(nodes) + O(segments) + O(spans) + O(pending markers) | Per scene-load + per MapFeature change — moderate spike |
| `Graph.CalculateFoulingDistance(node)` | Loop: ~20 LocationByMoving + GetPosition pairs | Moderate; called by AutoEngineer per-route-tick |
| `Graph.CurvatureAtLocation(loc)` | 1 dict lookup, 1 array index | Cheap (cached) |
| `Graph.GradeAtLocation(loc)` | 1 GetPositionRotation | Same as GetPosition |

**Don't run `RouteSearch.FindRoute` per-frame from a mod.** The `Searcher` constructor allocates several lists; the A* search allocates a `PriorityQueue` and dictionaries (`AStar.Search` at `Core/Core/AStar.cs:130-141`). Cached per-search; not GC-free.

---

## Multiplayer authority

The graph is **identical-by-construction** across peers, not synced.

| Aspect | Sync mechanism |
|---|---|
| Nodes & segments (existence, geometry, ids) | Scene-baked. Every peer loads identical scene → identical Graph contents. |
| Switch position (`TrackNode.isThrown`) | `SetSwitch` HostOnly message; observed locally on every peer. The `Graph` itself doesn't store switch state — `TrackNode` does. See [track-topology › Switch model](track-topology.md#switch-model). |
| Group enable/availability | `mapFeatures.features` HostOnly KVO observed by every peer; each peer's `MapFeatureManager.UpdateFeatureGraphGroups` calls `SetGroupEnabled`/`SetGroupAvailable` locally. **`Graph.SetGroupEnabled` itself has no host check** — direct calls from a client only mutate that client's view. |
| `TrackNode.IsCTCSwitch` | Per-peer, computed by each peer's own `CTCSwitchMonitor` from interlocking sets in the scene. Same input → same output. |
| `TrackNode.IsCTCSwitchUnlocked` | Per-peer field set from `unlockedSwitchIds` HostOnly KVO. |
| Turntable angle / stopIndex | `TurntableUpdateAngle` / `TurntableUpdateStopIndex` HostOnly. The bridge segment's `a`/`b` reassignment happens on each peer when their `Turntable.UpdateSegmentIndex` runs. |
| Track markers (existence + Location) | Authored. No runtime sync. Mods adding markers must add them on every peer. |
| Spans (existence + lower/upper) | Authored. Span endpoints are `SerializableLocation` in the inspector. |
| Position queries (`GetPosition`, `GetPositionRotation`) | Pure function of graph contents; same input → same output on every peer. |
| Routing (`RouteSearch.FindRoute`) | Pure function of graph + (host-side) switch positions. AutoEngineer runs only on host; clients never call FindRoute for routing. |

### What if tiles aren't loaded?

**The graph is always fully populated, regardless of tile state.** `Graph` builds from scene `MonoBehaviour`s, not from `MapManager` tile data. The tile system (`Map.Runtime.MapManager`) loads only **terrain heightmaps** — the track GameObjects are scene-baked and exist in memory from scene load (modulo `MapFeature` group enable/availability for unlocked content).

So: a client with no terrain tiles loaded *can still* `GetPosition(loc)` correctly, *can still* `LocationByMoving` across switches, *can still* `FindRoute`. Only the **visual** track meshes (built by `TrackObjectManager` from `Graph.Segments`) and the terrain underneath are tile-dependent. See [tile-loading-bardo › per-machine state](tile-loading-bardo.md) for the per-machine, no-MP-sync model.

**Cross-link:** `Graph.GetPosition(loc)` returns a coordinate in the canonical game space. The position is *always* valid; whether the tile underneath is *loaded* (and thus whether a Unity raycast at that position would hit terrain) is independent. [Floating origin](floating-origin.md#per-machine-offset-no-mp-sync) is also per-machine; combining a `Graph.GetPosition` result with `WorldTransformer.GameToWorld` gives the local Unity world position.

### Patch candidates (MP)

| Method | Why patch |
|---|---|
| `Graph.SetGroupEnabled`/`SetGroupAvailable` | Add a host check + broadcast for direct mod toggles outside `MapFeature`. |
| `Graph.RebuildCollections` | Postfix to broadcast a custom "graph synced" event for mod listeners. |

---

## Patch points: routing extension recipes

### Custom routing (replace `FindRoute` for some callers)

Two approaches:

1. **Patch `RouteSearch.FindRoute`** (extension method). Affects every caller (TrackSpan, AutoEngineer, etc.). Risky — invariants you may not see.
2. **Insert at higher level**: patch `AutoEngineerPlanner.UpdateWaypointRouteIfNeeded` to swap in your routing call before/after the vanilla one. Doesn't affect TrackSpan polylines.

Either way, your replacement must produce a `List<RouteSearch.Step>` whose `Position` chain forms a continuous path. `Step` constructors require a `Graph` reference for the deferred Position calculation; reuse `Graph.Shared`.

### Custom heuristics

Construct a `HeuristicCosts` literal and pass it to `RouteSearch.FindRoute`. To affect AutoEngineer specifically, patch `HeuristicCosts.AutoEngineer` (a static getter — Harmony postfix returning your modified struct). To affect TrackSpan polylines, patch `HeuristicCosts.Zero` (carefully — TrackSpan caches the result).

For per-segment heuristic deltas (e.g., "this bridge costs 2× more"), patch `Searcher.GetCostToTraverseSegment` directly.

### Alternative pathfinding algorithms

Replace `Searcher.Search` with your own implementation. The contract: given the constructor params, fill `stepsOut` with a path from start to destination, return success bool, set `iterationCount`. The internal `AStar<SearchState>` invocation can be entirely replaced — nothing in the rest of the codebase introspects A* state.

Be aware of `_mustClearSwitches` semantics: if `trainLength > 0.1`, your replacement must:
- Add the back-seed at `-trainLength` (or some equivalent extension).
- Not allow throwing a switch until the train clears it (or accept that vanilla AutoEngineer will reject the resulting route).
- Emit `ClearSwitch`-style follow-ups (or just don't model the train length and accept routing imperfections).

For waypoint routes (no train length), all of this is moot.

### Exposing graph topology to mod UIs

The Graph is `public` and `Graph.Shared` is freely accessible. Mods can:
- Iterate `Graph.Nodes` / `Graph.Segments` (the IEnumerable properties).
- Walk the graph via `LocationByMoving` / `LocationFrom` / `SegmentsConnectedTo`.
- Subscribe to `Graph.NodeDidChange` (C# event), `GraphDidRebuildCollections` (Messenger), `GraphDidChangeEnabledGroups` (Messenger), `GraphDidChangeAvailableGroups` (Messenger).
- Render via the polyline approximation (`_segmentCurveCache.CachedLineCurve` — but this is private; use `TrackSegment.Curve.GetPoint(t)` for rendering instead).

For UI overlays of the graph (mini-maps, route previews):
- `WorldTransformer.GameToWorld(graphPos)` to convert game-space → Unity world for projection.
- `TrackSegment.Curve` is the public BezierCurve; sample at uniform `t` for a coarse polyline.
- `Graph.EnumerateTrackMarkers` for picking up signals/flares for overlay rendering.

The vanilla example is `UI.Map.MapBuilder` — uses `Graph.Shared` extensively to render the strategic map.

---

## Gotchas summary (graph-internals-specific)

- **`Graph.Shared` is `FindObjectOfType<Graph>()` lazy** (`Graph.cs:119-129`). Cached after first call; not invalidated. If a scene reload destroys the Graph and creates a new one, `_graph` may point to a destroyed object until next access (which finds and caches the new instance).
- **`AStar.LastGCosts` is a static `public Dictionary<TPosition, float>`** (`AStar.cs:128`) overwritten on every `AStar.Search` call. It's a debug exposure of the most-recent search's g-scores. **Not thread-safe; not search-instance-aware.** Don't read this from a mod expecting "the most recent FindRoute's g-scores" — any other system running A* (e.g., a different Searcher) overwrites it.
- **`Graph.RebuildCollections` runs twice per `MapFeature` unlock** — once in `MapFeatureManager.HandleFeatureEnablesChanged` (immediate), once in `TrackObjectManager.Rebuild` (deferred 1 frame). This is harmless but means subscribers to `GraphDidRebuildCollections` may fire twice.
- **`SegmentCache` (the curve polyline cache) is invalidated by `InvalidateNode` but NOT by `RebuildCollections`.** A rebuild that drops a segment leaves its polyline cached. A rebuild that re-adds a segment with the same id will see the stale polyline still present. In practice, segments don't change geometry across rebuilds (they're scene-baked references), so this is fine — but if a mod replaces a segment's bezier control points at runtime, it must call `Graph.InvalidateNode(seg.a)` AND `InvalidateNode(seg.b)`.
- **`PrefabInstanceReleaseOnDestroy` and `PrefabInstancer` are GPU instancing helpers** (`Track/PrefabInstancer.cs`) for visual ties/tieplates — NOT part of the routing graph. Don't confuse them with `Graph` internals.
- **`HeuristicCosts.AutoEngineer` is a static getter returning a fresh struct each call** (`HeuristicCosts.cs:15-21`). Patching the property is straightforward (Harmony postfix on the getter). Patching the *fields* of the returned struct is impossible — they're values, not references.
- **`Searcher` is an `internal` class** (`Searcher.cs:12`); mods can't directly construct it. Use `RouteSearch.FindRoute` extension.
- **`SearchState` is also `internal`** (`SearchState.cs:8`). Patching `Searcher.GetNeighbors` requires reflecting on `SearchState`.
- **`Graph.Lerp(a, b, p)` is geometric, not topological.** It computes `Vector3.Lerp(GetPosition(a), GetPosition(b), p)` and finds the on-segment Location closest to that. **It does not produce points on the route from a to b** — only on the cross-segment-or-not interpolation. If a and b are on different routes (e.g., parallel tracks), the result may be on either or neither.
- **`Graph.GetDistanceBetweenClose(a, b)`** uses straight-line distance unless a.segment == b.segment. For nearby Locations on different segments (e.g., two cars facing each other across a switch), it underestimates true track distance. Use `TryFindDistance` for actual route distance.
- **`Graph.LocationOrientedToward(loc, target)` flips loc** if its forward direction points away from target. Useful for "make this Location face this way" but the result loses the original `end` orientation.
- **`Graph.ClosestLocationFacing(a, b, target)` picks whichever of `a`/`b` is closer to target,** then potentially flips it. Used by `MapBuilder` and `AutoEngineerDestinationPicker` for "click on the map → which end of the segment is the destination?" decisions.
- **The `LocationByMoving` 1000-iteration cap** is a hard ceiling. For unusual cases (mod adds 1m segments everywhere), this could be hit before the requested distance. The error message includes the current location; catch and decide.
- **`SegmentsConnectedTo` cache miss is O(segments)** (`Graph.cs:451-470`). On a fresh rebuild, the first query for each node walks the whole `segments` dict to find incident segments. Cumulative cost is O(N²) over N nodes if you query them all before any get cached — but since `RebuildCollections` builds `_nodeConnectionsCache` directly via `AddConnection` (`Graph.cs:181-185`), this only matters if the cache is invalidated mid-frame.
- **`Graph.GetCurvatureSamples` builds via `SampleCurvature` which calls `GetPositionRotation` 6 times per segment.** Cached on first call. Cold-cache cost on a large map is moderate; warm-cache is constant-time.
- **`Graph.SampleCurvatureUncached` (static)** uses static buffers `SampleCurveUncachedArray`/`SampleCurveUncachedPosRot`. **Not thread-safe.** Calling concurrently from coroutines that await frames between is fine; from threads it's a race.

---

## Cross-references

- **Authored topology layer (nodes, segments, spans, switch ID semantics):** [track-topology](track-topology.md). The "track-topology" sheet is the *user-visible* surface; this sheet is the *machinery underneath*.
- **`TrackNode.isThrown` propagation, `RequestSetSwitch`/`SetSwitch`, `CanSetSwitch`:** [track-topology › Switch model](track-topology.md#switch-model).
- **`TrackSpan` consumers (CTC blocks, industries):** [track-topology › TrackSpan](track-topology.md#tracktrackspan), [signals-dispatch › Block model](signals-dispatch.md#block-model-ctcblock).
- **`HeuristicCosts.AutoEngineer` consumer (`AutoEngineerPlanner.UpdateWaypointRouteIfNeeded`):** [autoengineer › Waypoint routing](autoengineer.md#waypoint-routing-autoengineerplannerrouteloop-and-friends).
- **`RouteSearch.Step.Flags.EnterCTCSwitch` consumer:** [autoengineer › Waypoint routing](autoengineer.md#waypoint-routing-autoengineerplannerrouteloop-and-friends) (`hashSet.Add(item.Node.id)` to know which switches are CTC-locked on the route).
- **`HeuristicCosts.AutoEngineer.ThrowSwitchCTCLocked = 1000`:** [signals-dispatch › Player-as-dispatcher vs AI-dispatcher](signals-dispatch.md#player-as-dispatcher-vs-ai-dispatcher).
- **`PassengerStopTimetableLogic.MilesBetweenPassengerStops`** (a graph-distance call site that **caches forever** without invalidation on group/topology change): [passengers-timetable](passengers-timetable.md). If a `MapFeature` unlock adds new track between two passenger stops, the cached miles will be wrong until reload. This is graph-related but lives in the passenger system.
- **`Graph.PositionRotation` is game-space:** [floating-origin › Game space vs World space](floating-origin.md). Convert via `WorldTransformer.GameToWorld(pos)` for Unity world coords.
- **Tiles vs graph (graph is fully built regardless of tile loading state, on every machine):** [tile-loading-bardo › per-machine, no-MP-sync](tile-loading-bardo.md).
- **Curvature → curve damage:** `Graph.CurvatureAtLocation` feeds `Car.ApplyCurvatureToModel` via `TrainMath.DamageForSpeed` — see [wear-durability › DamageForSpeed](wear-durability.md#modelphysicstrainmath-damage-formulas).
- **Grade → steam locomotive performance and AI throttle planning:** `Graph.GradeAtLocation` consumed by `AutoEngineerPlanner.Search` (averaged), and by steam tractive-effort code.
- **`AStar<TPosition>` generic in Core:** `Core/Core/AStar.cs`. Reusable for non-track searches if a mod needs custom A* on its own state space.
