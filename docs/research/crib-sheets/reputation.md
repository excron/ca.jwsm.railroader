# Reputation — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Progression](progression.md), [Passengers & Timetable](passengers-timetable.md), [Economy](economy.md), [Wear & Durability](wear-durability.md), [Industries & Ops](industries-ops.md)

A single host-side `MonoBehaviour` (`ReputationTracker`) that runs once per in-game day, weighting four sub-scores into a 0..1 `Reputation` float. The number drives four modifier curves consumed elsewhere (phase cost discount, repair-shop speed bonus, equipment purchase discount, max contract start tier) and is exposed to the UI through the `ReputationUpdated` `FireEvent`-replicated Messenger. All accumulation state lives on the `_reputation` `KeyValueObject` (HostOnly), with prefix-coded sub-keys (`ls-`, `sh-`, `pe--`, plus a few flat keys). Clients see the rolled-up `total`/`report` keys and nothing else.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `ReputationTracker.Shared` | `Game.Reputation/ReputationTracker.cs:38` | Singleton accessor (FindObjectOfType-cached). |
| `ReputationTracker.Reputation` | `Game.Reputation/ReputationTracker.cs:86` | Float 0..1, KVO key `total`. |
| `ReputationTracker.UpdateReputation()` | `Game.Reputation/ReputationTracker.cs:183` | The daily compute. Fires `ReputationUpdated`. **Host-only**. |
| `ReputationTracker.PhaseDiscount/RepairBonus/EquipmentDiscount/ContractMaxStartTier` | `…ReputationTracker.cs:497-587` | Tier tables consumed by progression / repair / purchase / contract systems. |
| `PassengerReputationCalculator.Calculate` | `Game.Reputation/PassengerReputationCalculator.cs:21` | Coverage score: weighted size² of player-served subnetworks. |
| `ReputationReport` | `Game.Reputation/ReputationReport.cs` | Sub-score breakdown, persisted under KVO key `report`. |
| `ReputationUpdated` | `Game.Events/ReputationUpdated.cs` | Empty Messenger struct, FireEvent code 3. |
| `Industry.PerformanceHistory` (KVO `_perfHist`) | `Model.Ops/Industry.cs:98` | Source for freight performance score (per-industry 7-day rolling). |

---

## Spine: how the daily compute happens

```
TimeWeather.Now.Day advances
   │
   ▼ TickCoroutine (host-only, started in OnEnableWithProperties)
ReputationTracker.UpdateReputation()                       ← ReputationTracker.cs:183
   │
   ├── num  = CalculatePassengerNetworkScore()             ← graph search over edge keys
   ├── num2 = CalculatePassengerConditionScore()           ← Σ(carCondition · |offset|) / Σ|offset|
   ├── (RemoveAllEdgeKeys + reset pass counters — see "Reset semantics")
   ├── num3 = CalculateFreightPerformance(now)             ← average of latest per-industry value
   ├── num4 = CalculateSafetyScore(now)                    ← derailments per day, EMA+mean blend
   │
   ▼
ReputationReport with 4 components (ratios 0.3 / 0.1 / 0.4 / 0.3)
   │
   ▼ report.CalculateOverallReputation() = Σ ratio·score
Reputation = num5
Report     = reputationReport
   │
   ▼
StateManager.SendFireEvent(default(ReputationUpdated))     ← StateManager.cs:952
   │
   ▼ on every machine
Messenger.Default.Send(default(ReputationUpdated))         ← StateManager.cs:1006
```

Console log: `"Reputation has changed from X% to Y%"` (or `"Reputation maintained at X%"`).
Analytics post: `"ReputationUpdated"` with overall + 4 sub-scores + previous value (`ReputationTracker.cs:245-253`).

### Reset semantics — high value finding

**`RemoveAllEdgeKeys` runs every day inside `UpdateReputation`** (`ReputationTracker.cs:207`). Every edge key (`pe--A--B`) the player accumulated yesterday is wiped to `Value.Null()`. The passenger network score is therefore *only ever measured against today's traffic since the last UpdateReputation* — there is no rolling-window for edges. Same for `PassengerTotal`/`PassengerCarConditionSum`: they're zeroed (`cs:208-209`).

The `ls-<id>` (last-served) keys are NOT reset — they persist for 10 days (see `IncludeInNetwork` below). The `sh-<id>` (stop history) keys are pure logging, never read by the score (only used in `Log.Debug`). The `derailments` array is trimmed in place by `GetDerailmentHoursAndTrim` (5-day window).

**Practical implication for mods:** if you want to alter passenger network behavior, you cannot rely on persistent edge bookkeeping in `_reputation`; you have to either (a) add your own KVO object, or (b) intercept *before* the daily wipe.

---

## `Game.Reputation.ReputationTracker`

Singleton `GameBehaviour` registered against the `_reputation` `KeyValueObject`.

### KVO object & key map

```csharp
private const string ObjectId  = "_reputation";              // 24
private const string LastDayKey = "lastDay";                 // 28
private const string ReputationKey = "total";                // 30
private const string ReputationReportKey = "report";         // 32
private const string KeyDerailmentHours = "derailments";     // 34
```

| Key | Type | Reset cadence | Purpose |
|---|---|---|---|
| `total` | float | overwritten daily | Public `Reputation` value (0..1). |
| `report` | dict | overwritten daily | `ReputationReport` blob (4 components). |
| `lastDay` | int | overwritten daily | Last `TimeWeather.Now.Day` we ran the update. |
| `pass-total` | int | wiped daily in `UpdateReputation` | Σ \|offset\| from `PassengerStopServed` events. |
| `pass-cc` | float | wiped daily | Σ \|offset\|·carCondition. |
| `derailments` | int[] | trimmed (>5 days old dropped) on each compute | Hours-since-epoch of each `CarDidDerail`. |
| `ls-<stopId>` | int | persistent (10-day reference) | Seconds-since-epoch the stop was last served (positive offset only). |
| `sh-<stopId>` | int[] | persistent | Hours-since-epoch list of distinct services (>4 hours apart). **Never read by scoring.** |
| `pe--<idA>--<idB>` | bool | wiped daily | Edge served by player today (key sorted by `string.Compare > 0` — see `KeyForPassengerStopEdge`). |

```csharp
public static string KeyForPassengerStopEdge(string fromId, string toId) {  // 401
    if (string.Compare(fromId, toId, StringComparison.Ordinal) < 0) {
        // swap so larger id comes first
        var t = toId; var t2 = fromId;
        fromId = t; toId = t2;
    }
    return "pe--" + fromId + "--" + toId;
}
```

**Edge key oddity:** the swap puts the *larger* string first. The `--`-split parser `PassengerStopEdgeKeyContains` (`cs:413`) requires `array.Length == 3` — so any stop identifier containing `"--"` will silently break inclusion checks. (None of the vanilla identifiers do.)

### Property registration (auth)

```csharp
private void Awake() {                                       // 110
    KeyValueObject keyValueObject = base.gameObject.AddComponent<KeyValueObject>();
    StateManager.Shared.RegisterPropertyObject("_reputation", keyValueObject, AuthorizationRequirement.HostOnly);
    _keyValueObject = keyValueObject;
}
```

The whole object is registered `HostOnly` via `AuthorizationRequirement.HostOnly` — every key on `_reputation` is host-write. Clients read via the standard property-object snapshot path. Save/load is automatic: the `_reputation` `KeyValueObject` is saved with the world. There is **no custom `OnPropertiesDidRestore`** — the object is rehydrated by the generic `PropertyObjectManager` snapshot apply. (See [save-load](save-load.md) for the property-object lifecycle.)

### Tick loop

```csharp
private IEnumerator TickCoroutine() {                        // 156
    if (LastUpdatedDay > Now.Day) {
        Log.Warning("LastUpdatedDay {} > {}; resetting.", LastUpdatedDay, Now.Day);
        LastUpdatedDay = Now.Day;
    }
    while (true) {
        if (LastUpdatedDay < Now.Day) {
            try { UpdateReputation(); LastUpdatedDay = Now.Day; }
            catch (Exception e) { Log.Error(e, "Error updating reputation"); }
        }
        yield return TimeWeather.WaitForNextDay();
    }
}
```

`WaitForNextDay()` polls `TimeWeather.Now.Day` every 5 game-time seconds (`TimeWeather.cs:138`). The host re-checks `LastUpdatedDay < Day` each wake — a single coroutine, no `TimeAdvanced` Messenger subscription. Catches every `Exception` per-sub-score (each in its own try/catch with default-to-0 fallback inside `UpdateReputation`); a thrown sub-score doesn't sink the day.

### Scoring formulas

#### Network coverage — `CalculatePassengerNetworkScore` (cs:277)

Builds a `PassengerReputationCalculator.Stop` graph from `PassengerStop.FindAll().Where(IncludePassengerStop)`. A stop is included if either:
- `!ProgressionDisabled && IncludeInNetwork(ps, now)` (i.e., served in the last 10 days), OR
- the stop appears in any current `pe--…` edge key (so a fresh edge can pull a never-yet-served stop into the calc).

Then delegates to `PassengerReputationCalculator.Calculate(stops, edgeKeys)`.

```csharp
public static float Calculate(IEnumerable<Stop> stopsIn, HashSet<string> playerVisitedEdges) {
    List<Stop> stops = stopsIn.OrderBy(ps => ps.Neighbors.Count).ToList();
    int count = stops.Count;
    HashSet<string> countedEdges = new();
    float num = 0f;
    while (stops.Count > 0) {
        var first = stops[0];
        var hit = new HashSet<string>();
        SearchFrom(first, hit);
        num += Mathf.Pow((float)hit.Count / count, 2f);    // (subnet-size / total)²
    }
    return Mathf.Pow(num, 0.5f);                            // sqrt at the end
    // SearchFrom walks neighbors that are still in `stops` and only follows edges that
    // (a) appear in playerVisitedEdges and (b) haven't been counted yet — DFS removing
    // stops as it visits.
}
```

**Subtleties:**
- The "size of subnet" includes **only stops connected by edges the player actually served**. An isolated served stop (a `ls-` exists but no edge to a neighbor) contributes 0 to `hit.Count` because `hit.Add(stop.Id)` only fires when an edge succeeds — an unreachable singleton produces `0/count = 0` and adds 0² to `num`.
- **The outer `while` loop bug:** the loop tests `stops.Count > 0` but `SearchFrom(first, hit)` only removes `first` and any stops reached *via served edges*. Stops with no served edges are removed from `stops` list, but contribute 0 to `num`. A stop is removed regardless of whether it was reached by an edge. (`stops.Remove(stop)` runs unconditionally on entry to `SearchFrom`.)
- **Sort order matters and is unusual:** stops are sorted *ascending by neighbor count* — least-connected first. So the first SearchFrom origin is a "leaf" stop, which limits the reachable subnet size. This appears to be a deliberate scoring penalty for sprawling sparse networks: leaf stops can only earn small subnets, big hubs get processed late once their leaves are gone.
- **Score scale:** maximum is `sqrt(1²) = 1` only when **one** subnet covers all stops (`hit.Count == count`). Splitting into two equal halves yields `sqrt(0.5² + 0.5²) ≈ 0.707`. Quartering: `sqrt(4·0.25²) = 0.5`. So coverage scoring punishes fragmentation harshly.
- **No edge-direction:** edge key normalizes `(A,B)` and `(B,A)` to the same key, so traveling either direction credits the edge.

#### Network inclusion (`IncludeInNetwork` / `LastServed`)

```csharp
public bool IncludeInNetwork(PassengerStop passengerStop, GameDateTime now) {  // 374
    GameDateTime? gameDateTime = LastServed(passengerStop.identifier);
    if (!gameDateTime.HasValue) return false;
    GameDateTime gameDateTime2 = now.AddingDays(-10f);
    return gameDateTime.Value > gameDateTime2;
}
```

10-day cutoff. **`LastServed` is also exposed on `PassengerStop.LastServed` — the only public reputation read used by the location-detail UI** (`Model.Ops/PassengerStop.cs:211` → `UI.CompanyWindow/LocationsPanelBuilder.cs:188-206`).

#### Passenger condition score — `CalculatePassengerConditionScore` (cs:303)

```csharp
return passengerCarConditionSum / passengerTotal;  // weighted average of car.Condition
```

Accumulated by `PassengerStopServed(identifier, offset, carCondition)` (cs:347): for every served event (load OR unload, positive or negative offset), `pass-total += |offset|` and `pass-cc += |offset|*carCondition`. So the score is "average car-condition weighted by passenger-count of every service event today."

#### Freight performance — `CalculateFreightPerformance` (cs:327)

```csharp
var industries = OpsController.Shared.Areas
    .SelectMany(a => a.Industries)
    .Where(ind => ind.IncludeInFreightPerformance(now))      // !ProgressionDisabled && HasActiveContract
    .ToList();
if (industries.Count == 0) return 1f;                         // no industries = perfect score
var withHistory = industries.Where(ind => ind.PerformanceHistory.Count > 0)
                            .ToDictionary(ind => ind, ind => ind.PerformanceHistory.OrderByDescending(kv => kv.Key).First().Value);
return withHistory.Count == 0 ? 1f : withHistory.Values.Average();
```

Per-industry, takes the **single most-recent day's** performance value (not an average over the rolling 7-day history), then averages those across industries. `Industry.PerformanceHistory` is the `_perfHist` KVO key (host-only, dict<dayInt,float>, capped at 7 entries — `Industry.cs:346-349`). Performance computed daily by `Industry.UpdatePerformance` from waybill ages: `Mathf.InverseLerp(5f, 1f, avgAgeDays)` — so 1-day-old waybills score 1, 5-days score 0 (`Industry.cs:305-325`).

**Edge case:** `if (num > 0.99f && ReceivedCarCount < 1) return null;` — an industry that has perfect waybill ages but received zero cars contributes nothing. Otherwise the lack of history defaults the entire freight component to `1.0` (perfect).

#### Safety — `CalculateSafetyScore` (cs:314)

```csharp
float totalHours = now.AddingDays(-5f).TotalHours;
List<int> derPerDay = DerailmentHoursToDerailmentsPerDay(GetDerailmentHoursAndTrim(totalHours), 5, totalHours);
return CalculateSafetyScoreFromDerailmentsPerDay(derPerDay);
```

5-day window. `GetDerailmentHoursAndTrim` reads the `derailments` int[] (Hours-since-epoch), drops anything older than `now - 5d`, writes the trimmed list back, and returns it.

```csharp
public static float CalculateSafetyScoreFromDerailmentsPerDay(List<int> dpd) {  // 322
    return CalculateSafetyScoreFromIndividualDayScores(
        dpd.Select(c => Mathf.InverseLerp(5f, 0f, c)).ToArray());
    // 0 derailments = 1.0; 5 derailments in a day = 0.0; 5+ also 0.
}

public static float CalculateSafetyScoreFromIndividualDayScores(float[] sd) {   // 470
    float a = CalculateEma(sd, 0.333f);          // EMA but bootstrapped with .Average()
    float b = sd.Average();
    return Mathf.Lerp(a, b, 0.5f);                // 50/50 EMA-and-mean blend
    static float CalculateEma(float[] values, float alpha) {
        float num = values[0];
        num = values.Average();                   // !!! immediately overwrites with mean
        for (int i = 1; i < values.Length; i++)
            num = alpha * values[i] + (1f - alpha) * num;
        return num;
    }
}
```

**Latent surprise:** the inner `CalculateEma` assigns `num = values[0]` then *immediately* overwrites with `num = values.Average()`. That means the EMA is bootstrapped from the mean, not from the oldest value, then it iterates from index 1 forward (skipping the oldest entry from the recursion entirely — `values[0]`'s influence comes only via the mean bootstrap). Looks like a refactor artifact. The end result: `safety = Lerp(EMA-from-mean, mean, 0.5)`, both anchored on the same mean. Day-0 spike has small effect; recent-day spike has alpha=0.333 weight.

`CarDidDerail` Messenger struct is empty (no payload); the registration is `Messenger.Default.Register<CarDidDerail>(this, delegate { CarDidDerail(); })` (`cs:137-140`). `Car.ApplyDerailmentDelta` is the sole sender, fired once per derailment first-event ([wear-durability › derailment](wear-durability.md#derailment)). One derailment can cascade to multiple cars (auto-uncouple at 0.25), each re-rolling and contributing one `CarDidDerail` send.

### Tier tables

```csharp
public float RepairBonus()                                   // 497
{
    float r = Reputation;
    if (r > 0.95f) return 0.50f;
    if (r > 0.90f) return 0.25f;
    if (r > 0.80f) return 0.10f;
    if (r > 0.70f) return 0.05f;
    return 0f;
}

public float PhaseDiscount()                                 // 519
{
    float r = Reputation;
    if (r > 0.95f) return 0.25f;
    if (r > 0.90f) return 0.20f;
    if (r > 0.85f) return 0.15f;
    if (r > 0.80f) return 0.10f;
    if (r > 0.70f) return 0.05f;
    return 0f;
}

public float EquipmentDiscount()                             // 545
{
    float r = Reputation;
    if (r > 0.99f) return 0.10f;
    if (r > 0.95f) return 0.07f;
    if (r > 0.90f) return 0.05f;
    if (r > 0.85f) return 0.03f;
    if (r > 0.80f) return 0.02f;
    if (r > 0.70f) return 0.01f;
    return 0f;
}

public int ContractMaxStartTier()                            // 575
{
    float r = Reputation;
    if (r > 0.95f) return 3;
    if (r > 0.90f) return 2;
    return 1;
}
```

Reputation ≤ 0.7 yields zero across all four. Tier breakpoints are strict `>` (so `0.7` exactly returns 0). `EquipmentDiscount` uses 0.99 not 0.95 for its top tier — easy to miss; only this one curve goes higher than 0.95.

**Consumers (every read site of every tier table):**

| Tier method | Consumer file:line | Use |
|---|---|---|
| `PhaseDiscount()` | `Game.Progression/Progression.cs:435` (`CostForPhase`) | Multiplies `Section.DeliveryPhase.cost` by `(1 - discount)` for "Start Phase" cost. |
| | `UI.CompanyWindow/RailroadPanelBuilder.cs:48` | Display in Reputation Effects panel. |
| `RepairBonus()` | `Model.Ops/RepairTrack.cs:212` (`EffectiveRepairPerDayPerCar`) | `repairPerDay = (1+bonus)*payRateMultiplier`; `payPerRepairUnit = 50/(1+bonus)` (more reputation → faster + cheaper-per-unit, but daily wages scale with the multiplier). |
| | `Model.Ops/RepairTrack.cs:442` | UI display of the bonus inside the Repair Shop industry detail. |
| | `UI.CompanyWindow/RailroadPanelBuilder.cs:52` | Display. |
| `EquipmentDiscount()` | `Game.State/EquipmentPurchase.cs:74` (`PurchasePriceForCarPrototype`) | `discount = floor(BasePrice * EquipmentDiscount())`; `final = BasePrice - discount`. |
| | `UI.CompanyWindow/RailroadPanelBuilder.cs:50` | Display. |
| `ContractMaxStartTier()` | `Model.Ops/ContractExtensions.cs:91` (`AvailableContracts`, **only** when industry has no current contract) | Caps the max tier of contracts offered to a fresh industry. Existing contracts reference industry's own performance history instead. |
| | `UI.CompanyWindow/RailroadPanelBuilder.cs:54` | Display. |

**Asymmetry:** RepairBonus jumps 5%→10%→25%→50% (geometric-ish), PhaseDiscount steps 5%→10%→15%→20%→25% (arithmetic, fine-grained at high), EquipmentDiscount is shallow with a 0.99 super-tier, ContractMaxStartTier is binary-ish (1 → 2 → 3). Mods replacing one curve should consider whether they want to mirror the others' pattern.

### `ReputationReport` (the persisted breakdown)

```csharp
public struct ReputationReport(List<Component> components) {
    public struct Component {
        public float Ratio;     // weight (0..1, sums roughly to 1.1 in vanilla — see below)
        public string Category; // display name
        public string Text;     // currently always "TODO" in vanilla
        public float Score;     // sub-score (0..1)
    }
    public List<Component> Components;
    public float CalculateOverallReputation() => Σ(ratio * score);
    // Round-trip: FromValue(Value) / ToValue() — dictionary blob persisted under "report" KVO key.
}
```

**Vanilla weights:** Network 0.3 + Condition 0.1 + Freight 0.4 + Safety 0.3 = **1.1**, NOT 1.0. The `CalculateOverallReputation` does NOT normalize by Σratio (`num2 += ratio` accumulates but is never used). So the published `Reputation` value can exceed 1.0 in theory — but every sub-score is `Mathf.Clamp01`'d before being assigned (`cs:231-234`), so the practical max is 1.1. Tier table consumers compare against `> 0.95` etc., so this doesn't break anything in vanilla — but **mods that normalize by sum will subtly raise/lower the resulting `Reputation`**.

The vanilla `Component.Text` is always literally `"TODO"` — it's a free-form display field reserved for future use.

### Patch candidates

| Method | Why patch |
|---|---|
| `ReputationTracker.UpdateReputation` | The whole compute pipeline. Postfix to add custom score components, prefix to alter sub-score sources. Note the `RemoveAllEdgeKeys()` call and pass-counter resets — anything you do that needs raw daily edge data must run before it. |
| `ReputationTracker.PassengerStopServed` (private) | Listening on the Messenger `PassengerStopServed` directly is cleaner than patching this — vanilla writes `pass-total`/`pass-cc` here. Postfix to hook into accumulation. |
| `ReputationTracker.PhaseDiscount/RepairBonus/EquipmentDiscount/ContractMaxStartTier` | Replace with curve. **Patch all four if you change the rep scale**; consumers compare against literal 0.7/0.8/.. thresholds. |
| `ReputationTracker.CalculateSafetyScoreFromIndividualDayScores` (public static) | Exposed for testing — the EMA bootstrap quirk lives here. Replacing fixes that anti-pattern. |
| `ReputationTracker.CalculateSafetyScoreFromDerailmentsPerDay` (public static) | Replace if you want to change the 5-derailments-per-day ceiling. |
| `ReputationTracker.IncludeInNetwork` | Adjust the 10-day stop-active window. |
| `PassengerReputationCalculator.Calculate` (public static) | Replace the entire coverage formula — `ReputationTracker.CalculatePassengerNetworkScore` calls into this directly. |
| `Industry.PerformanceHistory` setter / `AddPerformanceHistoryEntry` | Source of freight performance — patch to store more than 7 days, or to inject smoothing. |
| `Industry.IncludeInFreightPerformance` | Inclusion gate (default: `!ProgressionDisabled && HasActiveContract`). |
| `ReputationReport.CalculateOverallReputation` | The Σ-without-normalize. Patch here for proper weighting. |
| `Car.ApplyDerailmentDelta` | The sole `CarDidDerail` Messenger sender — to suppress, prefix here ([wear-durability › derailment](wear-durability.md#derailment)). |

### MP authority

- `_reputation` KVO is registered `AuthorizationRequirement.HostOnly` (`cs:113`). Every key (`total`, `report`, `lastDay`, `pass-total`, `pass-cc`, `derailments`, `ls-…`, `sh-…`, `pe--…`) is host-write.
- `OnEnableWithProperties` gates the Messenger registrations + tick coroutine on `StateManager.IsHost` (`cs:127`). Clients have a dormant `ReputationTracker` MonoBehaviour that subscribes to nothing and runs no coroutines — they only consume the snapshot.
- `ReputationUpdated` is one of the four `FireEvent`-replicated Messenger events (codes 0=BalanceDidChange, 1=ProgressionStateDidChange, 2=RequestRejected, **3=ReputationUpdated**), see `StateManager.SendFireEvent` at `Game.State/StateManager.cs:952` and `HandleFireEvent` at `:991`. Cross-ref [events-catalog › FireEvent table](events-catalog.md).
- Snapshot path: standard property-object save. The host's `_reputation` is captured in the world snapshot and applied client-side via `PropertyObjectManager`. Clients can read `Reputation`, `Report`, `LastServed`, etc., but writing any key from a client triggers an authorization rejection.

### Related Messenger / KVO events

| Event | Type | Direction | Notes |
|---|---|---|---|
| `Game.Events.PassengerStopServed` (struct(string, int, float)) | Messenger | Local-only on host (sent in `PassengerStop.LoadCar`/`UnloadCar` → `FirePassengerStopServed`) | Drives `PassengerCondition` accumulation + `ls-`/`sh-` writes. **`offset = -1` path:** when an unload removes a passenger whose destination is *not* the current stop (i.e., an unwanted-destination dump), `FirePassengerStopServed(-1, car.Condition)` is emitted (`PassengerStop.cs:689`) — the `ReputationTracker` adds it as `\|offset\| = 1` to `pass-total` AND `pass-cc` with full weight. So an off-route passenger drop *increases* the denominator and weights average condition the same as a successful drop. **`offset > 0` is the only path that updates `ls-`/`sh-` and the edge keys** (`cs:352-366`). Cross-ref [passengers-timetable › PassengerStop work loop](passengers-timetable.md). |
| `Game.Events.PassengerStopEdgeMoved` (struct(string, string)) | Messenger | Local-only on host | Sets `pe--A--B = true`. Fired in `UnloadCar` when `LastStopIdentifier ≠ identifier`. May synthesize multi-edge sends from a path search (`PassengerStop.cs:911-921` — see "Path-search edge fanout" below). |
| `Game.Events.CarDidDerail` (empty struct) | Messenger | Local on host | Appends `Mathf.FloorToInt(Now.TotalHours)` to `derailments` array. |
| `Game.Events.ReputationUpdated` (empty struct) | FireEvent (code 3) → Messenger | Host-broadcast → all machines | UI rebuild trigger (`RailroadPanelBuilder.cs:30` `RebuildOnEvent<ReputationUpdated>`). |
| KVO `_reputation.total` (float) | KVO | Host-write, all-read | Public reputation. No vanilla observer subscribes — `RebuildOnEvent<ReputationUpdated>` is the canonical UI hook. Mods that want continuous tracking should subscribe to the KVO key for safety against future weight changes. |
| KVO `_reputation.report` (dict) | KVO | Host-write, all-read | The breakdown. |

### Path-search edge fanout

When a passenger-bearing car arrives and `LastStopIdentifier ≠ identifier`, `FirePassengerStopEdgeMoved(value.LastStopIdentifier)` runs (`PassengerStop.cs:672`). Its body:

```csharp
private void FirePassengerStopEdgeMoved(string originIdentifier) {           // 904
    if (neighbors.Any(n => n.identifier == originIdentifier)) {
        Messenger.Default.Send(new PassengerStopEdgeMoved(originIdentifier, identifier));
        return;
    }
    List<string> path = FindPath(this, originIdentifier);
    if (path == null || path.Last() != originIdentifier) {
        Log.Error("Path from {a} to {b} not found - PassengerStopEdgeMoved will not be fired", identifier, originIdentifier);
        return;
    }
    for (int i = 0; i < path.Count - 1; i++)
        Messenger.Default.Send(new PassengerStopEdgeMoved(path[i], path[i + 1]));
}
```

If the previously-served-stop isn't a direct neighbor of the current stop, it walks `FindPath` and emits **one event per intermediate edge** — synthesizing edge credits along the entire route. So a long-haul passenger run lights up every `pe--` key on its path, even stops the train didn't actually stop at. **A single car arrival can therefore set N edge keys.**

### Gotchas

- **`LastUpdatedDay > Now.Day` reset** (`cs:158-162`): if you mess with `TimeWeather` to roll the clock backward, the tracker resets `lastDay` and re-runs the daily compute on the next forward day. Time-warp mods need to be aware.
- **Daily wipe of edges + counters happens *during* compute, not at start of next day.** A mod that listens to `ReputationUpdated` and reads `_reputation` immediately will see the **post-wipe** state. To capture the pre-wipe edge set, patch `UpdateReputation` prefix or hook `RemoveAllEdgeKeys` directly.
- **`sh-<id>` is write-only** in the current code — written in `PassengerStopServed` for logging, never read for any score. Saved with the world. Free space for mod use, but be aware vanilla writes to it on every >4-hour-since-last service.
- **`pass-total`/`pass-cc` accumulate every `PassengerStopServed`, including the offset=-1 (off-route drop) path.** A train that boards passengers correctly (offset>0) and dumps them at the wrong destination (offset=-1 on unload) double-counts that passenger toward both load and unload averages. So the passenger condition score actually reflects "average car condition during passenger handling events," not "average car condition delivered."
- **Network coverage rounds harshly:** with 10 stops and only 1 served subnet of 3 stops, the score is `sqrt((3/10)²) = 0.3`. With 10 stops and 5 served subnets of 2 stops each, the score is `sqrt(5*(2/10)²) = sqrt(0.2) ≈ 0.447`. Many small subnets > one medium subnet — but one big subnet beats many small ones quadratically: 1 subnet of 6 = `sqrt(0.36) = 0.6` > `sqrt(3*(2/10)²) = 0.346`.
- **`CalculatePassengerNetworkScore` uses `playerVisitedEdges` from `GetSetEdgeKeys()` snapshotted ONCE at the top.** Subsequent `PassengerStopServed`/`PassengerStopEdgeMoved` events fired during the compute are **not** included.
- **`Industry.PerformanceHistory` returns `Mathf.InverseLerp(5f, 1f, avgAge)`** — so a 10-day-old waybill maps to 0 (clamped), but very-fresh waybills (<1 day) clamp to 1. Manipulating waybill timing is the only path to influence freight performance.
- **`ContractMaxStartTier` is *only* consulted by industries with no `Contract`** (`ContractExtensions.cs:91`). Existing industries roll their tier from their own `PerformanceHistory` (`ContractExtensions.cs:64-90`). A mod that wants reputation to influence existing-industry tiers must patch `AvailableContracts` itself.
- **`EquipmentDiscount` floor-rounds to integer credits** (`EquipmentPurchase.cs:74` `Mathf.FloorToInt`). For low-priced cars and low reputations, the discount can be 0 in practice (e.g. `floor(1000 * 0.01) = 10`, but `floor(150 * 0.01) = 1`). Display panel shows the percentage, actual savings may differ.
- **`OnDestroy` does `StateManager.UnregisterPropertyObject("_reputation")`** even on clients, but `Awake` registers unconditionally. If a client crashes during snapshot apply and the property object is half-registered, dispose may double-fire or miss — this is theoretical, not observed.
- **`[ContextMenu("Calculate Scores")] TestCalculateScores`** (`cs:269`) — useful debug entry: runs `CalculatePassengerNetworkScore` + `CalculateSafetyScore` and logs results without mutating state. Available in inspector context menu.
- **No console command exists for reputation manipulation.** No `/setReputation` or `/recompute`. Only the inspector context menu. Mods that want a cheat should add a console command (`[ConsoleCommand]`).
- **The `Reputation` setter is `private`** — mods must use reflection or patch `set_Reputation` to inject a value directly. Easier route: patch `UpdateReputation` postfix and overwrite `Reputation`.
- **No `OnPropertiesDidRestore` hook** — restored state goes straight back into the KVO. The first `WaitForNextDay` then sees old `LastUpdatedDay`. If you delete a `_reputation` save section manually, fields reset to defaults (Reputation=0).

### Init order

1. `ReputationTracker.Awake` — `RegisterPropertyObject("_reputation", …, HostOnly)`.
2. World loads / snapshot applies — `_reputation` KVO populated from save.
3. `OnEnableWithProperties` — host registers Messenger handlers + starts `TickCoroutine`.
4. First `TickCoroutine` iteration — if `LastUpdatedDay < Now.Day` runs immediately. **A first-load right after midnight will compute reputation as the first thing on the new day** — patches that depend on industry state being fully initialized must wait for the appropriate later phase.

`PassengerStop`, `Industry`, `Car` — none of which is dependency-ordered relative to `ReputationTracker.Awake`. The tracker is robust to that because all reads happen lazily inside `UpdateReputation`, which doesn't run until `WaitForNextDay` returns.

---

## Cross-references

- `PhaseDiscount`/`CostForPhase` integration: see [progression › ReputationTracker interaction](progression.md#reputationtracker-interaction).
- `PassengerStopServed` and the offset=-1 penalty path: see [passengers-timetable § PassengerStop work loop](passengers-timetable.md).
- `EquipmentDiscount` consumer (`PurchasePriceForCarPrototype`): see [economy › EquipmentPurchase](economy.md).
- `RepairBonus` consumer (`EffectiveRepairPerDayPerCar`): see [wear-durability › RepairTrack](wear-durability.md#modelopsrepairtrack-industry-side-repair).
- `CarDidDerail` Messenger origin: see [wear-durability › derailment](wear-durability.md#derailment).
- `FireEvent` codes catalog (BalanceDidChange/ProgressionStateDidChange/RequestRejected/ReputationUpdated): see [events-catalog › FireEvent-replicated events](events-catalog.md).
- Property-object save/load (auto-snapshotted via `RegisterPropertyObject`): see [save-load › property-object lifecycle](save-load.md).
- Industry `PerformanceHistory` source: see [industries-ops](industries-ops.md).
