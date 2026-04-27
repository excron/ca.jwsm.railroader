# Industry Performance & Contract Tiers — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Industries & Ops](industries-ops.md), [Reputation](reputation.md), [Economy](economy.md), [Passengers & Timetable](passengers-timetable.md), [Ops Routing](ops-routing.md)

Each industry that `usesContract` carries a tier (1..5) that governs throughput multiplier (`Percent`) and the on-time delivery bonus rate. A 7-entry per-industry rolling history (`_perfHist`) records `InverseLerp(5,1)` of the average open-waybill age each day — that history is the single input to `RollToNextContract` (auto-demote on 3-day failure) and `AvailableContracts` (player-driven promotion/demotion offers). Reputation only matters when an industry has *no* current contract — `ContractMaxStartTier` caps the *first* contract offered. Daily cadence is driven by `TimeDayDidChange` host-side: `UpdatePerformance → DailyReceivables → RollToNextContract → DailyPayables`. Waybills have no TTL — they outlive contracts, are pure key-value blobs on the car, and the only safety net for "industry contract terminated" is `OpsController.ReturnWaybillsFrom`. The whole pipeline is host-only with three escape hatches: the Officer-auth `ModifyContract` message, the sandbox-only `/ops setTier`, and the `_perfHist` KVO blob (HostOnly write).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Industry.PerformanceHistory` | `Model.Ops/Industry.cs:98` | KVO key `_perfHist`, dict<int day, float perf>, capped at 7 entries |
| `Industry.UpdatePerformance(ages, now)` | `Model.Ops/Industry.cs:327` | Daily roll. Calls `CalculatePerformance` → `AddPerformanceHistoryEntry` |
| `Industry.CalculatePerformance` | `Model.Ops/Industry.cs:305` | The `InverseLerp(5,1)` curve. Returns `null` on perfect-but-no-cars edge case |
| `Industry.RollToNextContract` | `Model.Ops/Industry.cs:429` | Daily contract turnover: failing-check, tier change, penalty, broadcast |
| `Industry.IsFailing` (private static) | `Model.Ops/Industry.cs:411` | "3 days, average < 0.5" → demote-by-2 proposal |
| `Industry.ModifyContract(int tier)` | `Model.Ops/Industry.cs:365` | Player-offered tier change; sets `NextContract` |
| `Industry.SetContract(Contract?)` | `Model.Ops/Industry.cs:299` | Auth setter — **also clears performance history** |
| `Contract` (struct) | `Model.Ops/Contract.cs:7` | `Tier` + computed `Percent`/`SpeedBonus`/`TimelyDeliveryBonus` |
| `ContractExtensions.AvailableContracts` | `Model.Ops/ContractExtensions.cs:64` | What the UI offers — diverges based on existing contract / sandbox / no-contract |
| `ContractExtensions.PenaltyForChange` | `Model.Ops/ContractExtensions.cs:121` | $250 × (tierDelta + max(6-days,1)) downgrade fee |
| `ContractExtensions.NumbersForTier` | `Model.Ops/ContractExtensions.cs:108` | The tier→multiplier table (24%/34%/49%/70%/100%) |
| `OpsController.DayDidChange` | `Model.Ops/OpsController.cs:163` | Daily orchestrator (host) |
| `OpsController.UpdatePerformance` (private) | `Model.Ops/OpsController.cs:908` | Walks open waybills, fans out per-industry ages |
| `OpsController.ReturnWaybillsFrom(industry)` | `Model.Ops/OpsController.cs:989` | Tier-0 termination cleanup |
| `OpsController.RewriteWaybills(from,to)` | `Model.Ops/OpsController.cs:947` | Identifier prefix rewrite (Section.ApplyCompleted plumbing) |
| `IndustryUnloader.DailyReceivables` | `Model.Ops/IndustryUnloader.cs:98` | Per-day batch payment for `payPerQuantity` loads |
| `IndustryContext.PayWaybill` | `Model.Ops/IndustryContext.cs:334` | Per-car waybill arrival payment + timely bonus + condition fine |
| `Game.Messages.ModifyContract` | `Game.Messages/ModifyContract.cs:8` | Officer-auth tier-change request |

---

## Spine: the daily orchestration

```
TimeDayDidChange (Messenger; fired by TimeWeather)
   │
   ▼ OpsController.DayDidChange (host-only)            ← OpsController.cs:163
       │
       ├── UpdatePerformance(now)                       ← :908
       │     ├── RebuildPopulations()                   ← :189 (passenger pop. derived from contract)
       │     ├── for each open waybill (PaymentOnArrival > 0):
       │     │      age = now.TotalDays - created.TotalDays - GraceDays
       │     │      record under destination.Industry  AND  origin.Industry (if origin set)
       │     └── for each !ProgressionDisabled industry:
       │            industry.UpdatePerformance(ages-or-empty, now)
       │              └── CalculatePerformance → InverseLerp(5,1) of avg(ages>0.1)
       │                  ↳ if avg→1.0 AND ReceivedCarCount<1 → return null (skip write)
       │              └── AddPerformanceHistoryEntry(now.Day, perf)
       │                    (HostOnly. Ignores duplicate-day. Trims to 7 oldest-removed.)
       │
       ├── for each industry: DailyReceivables(now)     ← Industry.cs:395
       │       └── per IndustryComponent: DailyReceivables(now, ctx)
       │             ↳ IndustryUnloader: drains "unloaded-total-<load.id>" counter,
       │                                 calls ctx.PayLoad(load, total) when ≥ 1.0
       │
       ├── for each industry: RollToNextContract()      ← Industry.cs:429
       │       ├── if Contract & IsFailing(history,Tier,out proposed):
       │       │     proposed = max(0, Tier - 2)
       │       │     if (NextContract?.Tier ?? Tier) > proposed:
       │       │         NextContract = Contract(proposed); annotate " (Low performance.)"
       │       ├── if NextContract:
       │       │     penalty = PenaltyForChange(target, history.Count, …)
       │       │     ApplyToBalance(-penalty, Freight, …, "Tier Change Penalty")
       │       │     if target.Tier == 0:
       │       │         SetContract(null) → ClearPerformanceHistory  (wipes _perfHist + _recvdCars)
       │       │         OpsController.ReturnWaybillsFrom(this) → reroute customer cars to interchange
       │       │     else:
       │       │         SetContract(NextContract) → ClearPerformanceHistory
       │       │     Multiplayer.Broadcast(...)
       │       │     NextContract = null
       │
       └── for each industry: DailyPayables(now)        ← Industry.cs:403
             (per-component fanout — RepairTrack uses this for wages; vanilla unloader is a no-op)
```

`DayDidChange` is a one-shot per-day handler — there is **no** retroactive catch-up if the host loses time. If `TimeWeather.Now.Day` advances by 2 between ticks (e.g., long pause + time warp), only one orchestration fires. `Industry.PerformanceHistory` `Day` keys are integer game-days, so the dictionary may end up with a 1-day gap; the `IsFailing` "last 3" view doesn't care about gaps.

### Order of operations matters

`DailyReceivables` is called **before** `RollToNextContract`. So an `IndustryUnloader` that just received its final batch under tier 5 gets paid for it *first*, then the contract may demote. Conversely, if the contract terminates today and `ReturnWaybillsFrom` clears car waybills, those cars don't get paid via `OnCompleteWaybill` because their waybill is rewritten or nulled.

`UpdatePerformance` is called before either — the day's performance is computed against open waybills that may or may not have just been delivered. Cars whose waybill flipped `Completed=true` during yesterday's events have `PaymentOnArrival=0` (set in `IndustryComponent.OnCompleteWaybill:175`) and are dropped by the `w.PaymentOnArrival > 0` filter, so completed-but-not-yet-cleared waybills don't pollute the average.

---

## `Model.Ops.Industry.PerformanceHistory` — the 7-day rolling cap

```csharp
private const string PerformanceHistoryKey = "_perfHist";  // 34
private const int PerformanceHistoryLimit = 7;             // 36

public IReadOnlyDictionary<int, float> PerformanceHistory   // 98
{
    get => _keyValueObject["_perfHist"].DictionaryValue
                 .Select(kv => new KVP<int,float>(int.Parse(kv.Key), kv.Value.FloatValue))
                 .ToDictionary(...);
    private set => _keyValueObject["_perfHist"] = Value.Dictionary(
        value.ToDictionary(kv => kv.Key.ToString(), kv => Value.Float(kv.Value)));
}
```

**Wire format:** dictionary with `string` keys (decimal-formatted day numbers) and `float` values. Reading is O(N) per call — every getter rebuilds the dictionary. Mods that need frequent reads should cache.

```csharp
private void AddPerformanceHistoryEntry(int day, float performance)  // 336
{
    StateManager.DebugAssertIsHost();
    var d = new Dictionary<int, float>(PerformanceHistory);
    if (d.ContainsKey(day)) {
        Log.Warning("History already contains entry for day {day} -- ignoring", day);
        return;                                                     // ← duplicate-day no-op
    }
    d[day] = performance;
    while (d.Count > 7) d.Remove(d.Keys.Min());                     // ← oldest-day trim
    PerformanceHistory = d;
}
```

**Trim policy:** by day-key min, *not* insertion order. If a mod injects an arbitrary day key (e.g., `Day=999999` to "lock" a value), it will displace real history forever.

```csharp
private void ClearPerformanceHistory()                              // 353
{
    StateManager.DebugAssertIsHost();
    _keyValueObject["_perfHist"] = Value.Null();
    ReceivedCarCount = 0;
}
```

**Called by `SetContract`** — every contract change wipes both `_perfHist` and `_recvdCars`. Including promotions. So a fresh tier-2 contract starts with an empty history regardless of how well you performed at tier 1. This is why `AvailableContracts`'s `list.Count < 3` and `< 5` clauses exist — to prevent immediate re-promotion before there's enough new evidence.

### `_perfHist` save/load

The `_perfHist` key lives on the per-industry `KeyValueObject` registered by `Industry.Awake` via `IndustryStorageHelper`'s `RegisterPropertyObject(industry.identifier, kvObject, helper)` (`IndustryStorageHelper.cs:82`). All industry KVO keys go through `IndustryStorageHelper.AuthorizationRequirementForPropertyWrite` — which is **HostOnly for everything except `extraScheduled`** (Trainmaster). So `contract`, `nextContract`, `_perfHist`, `_recvdCars`, `storage`, and the `unloaded-total-<load.id>` counters are all host-write.

Snapshot/restore is the standard property-object path (no custom `OnPropertiesDidRestore`) — the dictionary survives intact. **Empty-dictionary represents "no history" identically to `Value.Null`** because the getter calls `.DictionaryValue` which yields an empty dict for `Null`.

---

## `Industry.UpdatePerformance` — the daily roll

```csharp
private float? CalculatePerformance(IReadOnlyList<float> waybillAgesInDays, GameDateTime now)  // 305
{
    waybillAgesInDays = waybillAgesInDays.Where(age => age > 0.1f).ToList();   // ← filter very-fresh
    float num;
    if (waybillAgesInDays.Count == 0) num = 1f;                                 // empty = perfect
    else {
        float value = waybillAgesInDays.Average();
        num = Mathf.InverseLerp(5f, 1f, value);                                 // ← THE CURVE
    }
    if (num > 0.99f && ReceivedCarCount < 1) {                                  // ← perfect-but-empty
        Log.Information("…null - not enough cars received");
        return null;                                                            // skip the write
    }
    return num;
}

public void UpdatePerformance(IReadOnlyList<float> waybillAgesInDays, GameDateTime now)  // 327
{
    float? num = CalculatePerformance(waybillAgesInDays, now);
    if (num.HasValue) AddPerformanceHistoryEntry(now.Day, num.Value);
}
```

### The `InverseLerp(5, 1)` curve

`Mathf.InverseLerp(5f, 1f, avg)`:

| avg age (days) | performance |
|---|---|
| ≥ 5 | 0.00 |
| 4 | 0.25 |
| 3 | 0.50 |
| 2 | 0.75 |
| ≤ 1 | 1.00 |

Note `(5,1)` is `(a,b)` reversed (a > b). `Mathf.InverseLerp` clamps to [0,1] regardless, so a 0.5-day-old waybill → 1.0 (capped, *not* extrapolated). Negative ages (clock skew) → 1.0 too.

The `> 0.1f` pre-filter means brand-new waybills (issued same tick as the daily roll) don't drag the average toward 1.0 — they're just dropped. This intentionally prevents "spam fresh waybills to inflate score" — you have to actually deliver the old ones.

### `OpsController.UpdatePerformance` — the age accountant

```csharp
private void UpdatePerformance(GameDateTime now)                    // OpsController.cs:908
{
    RebuildPopulations();
    var enumerable = GetOpenWaybills()
        .Select(t => t.Waybill).Where(w => w.PaymentOnArrival > 0);  // ← excludes autodest/sell/0-pay
    var industryToWaybillAges = new Dictionary<Industry, List<float>>();
    foreach (var w in enumerable) {
        float ageInDays = now.TotalDays - w.Created.TotalDays - w.GraceDays;
        Record(w.Destination);
        if (w.Origin.HasValue) Record(w.Origin.Value);                // ← double-counts origin AND dest
        void Record(OpsCarPosition pos) {
            var industry = IndustryComponentForPosition(pos).Industry;
            if (!industryToWaybillAges.TryGetValue(industry, out var v))
                industryToWaybillAges.Add(industry, v = new List<float>());
            v.Add(ageInDays);
        }
    }
    var empty = new List<float>();
    foreach (var ind in AllIndustries.Where(i => !i.ProgressionDisabled)) {
        if (!industryToWaybillAges.TryGetValue(ind, out var ages)) ages = empty;
        ind.UpdatePerformance(ages, now);
    }
}
```

**Critical asymmetry:** every open waybill is recorded against BOTH origin and destination industry — so an industry that only ships things still gets a per-waybill age recorded against it (origin side), regardless of whether it received anything. The performance curve doesn't care which role the industry played in any specific waybill.

**Grace days are subtracted from age** — a 2-day-grace waybill that's 4 days old has effective age 2, scoring `InverseLerp(5,1,2) = 0.75`. `CalculateGraceDays` (`OpsController.cs:678`) gives 2 days for >40 mi, 1 for >20 mi, 0 for shorter.

**Receiving zero open waybills (or all zero-payment) yields perf=1.0 each day** — but only if `ReceivedCarCount >= 1`. If you've never received a car AND your average is perfect (because there are no waybills), you get `null` (no entry). So a brand-new contract with no traffic at all logs nothing — by design, so `IsFailing`'s "3 days < 0.5" never triggers on stillborn contracts.

### Edge cases (what the curve does to weird input)

| `waybillAgesInDays` | `>0.1` filter result | avg | curve | written |
|---|---|---|---|---|
| `[]` | `[]` | n/a | 1.0 | depends on `ReceivedCarCount` (null if 0) |
| `[0.05, 0.05]` | `[]` | n/a | 1.0 | depends on `ReceivedCarCount` |
| `[2, 4]` | `[2, 4]` | 3 | 0.5 | always |
| `[10]` | `[10]` | 10 | 0 (clamped) | always |
| `[NaN]` | `[NaN]` (passes `> 0.1`? NaN > 0.1 is `false`) | filtered out | 1.0 | depends |
| `[float.PositiveInfinity]` | passes | inf | InverseLerp produces 0 | always |
| `[-1]` (clock skew) | filtered (negative not > 0.1) | n/a | 1.0 | depends |

`Mathf.InverseLerp(5, 1, NaN)` returns 0 in Unity (Mathf clamps weird ranges). But because `>0.1` filters NaN first, the loop never sees it inside the average path. Avg over an empty list is normally NaN, but the `Count == 0` branch short-circuits to 1.0 first. **So no NaN can leak through to `_perfHist`** in vanilla. A mod that injects via `AddPerformanceHistoryEntry` directly could.

### Patch candidates

| Method | Why patch |
|---|---|
| `Industry.CalculatePerformance` | Replace the InverseLerp(5,1) curve. Note: it filters `> 0.1` first AND has the perfect-but-empty `null` short-circuit. |
| `Industry.UpdatePerformance` | Wrap to add custom score components (e.g., load-handling penalties, train-length bonuses). |
| `Industry.AddPerformanceHistoryEntry` (private) | Final write before KVO. Patch to enforce a floor, change trim policy (e.g., FIFO insertion order), or extend cap beyond 7. |
| `Industry.ClearPerformanceHistory` (private) | Suppress the wipe-on-`SetContract` to allow rolling history across promotions. |
| `OpsController.UpdatePerformance` (private) | Change which waybills count, how grace days apply, the origin-AND-dest double counting. |
| `OpsController.DayDidChange` | Change the daily orchestration order (e.g., RollToNextContract before DailyReceivables for "you don't get paid for late deliveries on demote day"). |
| `Industry.PerformanceHistory` setter | Direct write — handy for cheats / debug. HostOnly. |

---

## `Industry.IsFailing` — the auto-demote check

```csharp
private static bool IsFailing(IReadOnlyDictionary<int,float> performanceHistory,    // 411
                              int currentTier, out int proposedTier)
{
    proposedTier = currentTier;
    var list = performanceHistory.OrderByDescending(kv => kv.Key)
                                 .Select(kv => kv.Value).Take(3).ToList();
    if (list.Count < 3) return false;                       // ← need 3 days
    if (list.Average() >= 0.5f) return false;
    proposedTier = Mathf.Max(0, currentTier - 2);           // ← demote by 2
    return true;
}
```

- **3-day window**: most-recent 3 entries by `Day` key, regardless of whether those days are consecutive.
- **Threshold is strict `>= 0.5`** — exactly 0.5 *passes* (not failing).
- **Demote by 2 tiers minimum** — tier 5 → 3, tier 4 → 2, tier 3 → 1, tier 2/1 → 0 (terminate). No middle ground; `IsFailing` cannot propose a 1-tier demote.
- **Tier 0 termination via failing path**: demote-by-2 from tier 1 produces `max(0, -1) = 0` → contract terminated, history wiped, `ReturnWaybillsFrom` runs.
- **Returns `false` when `Contract` is null** (caller checks `contract.HasValue` first at `:435`).

The proposed tier is reconciled with any `NextContract` already set: if the player has already queued a downgrade *lower* than the failing-proposal, the player's choice wins (`:438` — `(NextContract?.Tier ?? Contract.Tier) > proposed`). Practically this means the auto-demote can't *raise* a player-queued downgrade.

---

## `Industry.RollToNextContract`

```csharp
public void RollToNextContract()                            // 429
{
    Contract? contract = Contract;
    Contract? contract2 = NextContract;
    var performanceHistory = PerformanceHistory;
    string text = "";
    if (contract.HasValue && IsFailing(performanceHistory, contract.Value.Tier, out var proposedTier)) {
        if ((contract2?.Tier ?? contract.Value.Tier) > proposedTier) {
            text = " (Low performance.)";
            contract2 = new Contract(proposedTier);
        }
    }
    if (contract2.HasValue) {
        int count = performanceHistory.Count;                // ← days-of-history at decision time
        int num = this.PenaltyForChange(contract2.Value.Tier, count, out _, out _);
        string text2 = "";
        if (num > 0) {
            ApplyToBalance(-num, Freight, …, "Tier Change Penalty", quiet: true);
            text2 = $" ({num:C0} Penalty)";
        }
        string text3;
        if (contract2.Value.Tier == 0) {
            SetContract(null);
            text3 = "has been terminated";
            OpsController.Shared.ReturnWaybillsFrom(this);   // ← cleanup
        } else {
            SetContract(contract2);                           // ← also clears _perfHist (!)
            text3 = $"is now at Tier {contract2.Value.Tier}";
        }
        Multiplayer.Broadcast($"Contract with {Hyperlink.To(this)} {text3}.{text}{text2}");
        NextContract = null;
    }
}
```

### Penalty math

```csharp
public static int PenaltyForChange(this Industry industry, int targetTier, int days,  // ContractExtensions.cs:121
                                   out int tierChangeComponent, out int ageComponent)
{
    int num = industry.Contract?.Tier ?? 0;
    int num2 = 250;                                          // ← $250 base unit
    int num3 = num - targetTier;                             // delta
    if (num3 <= 0) { tierChangeComponent=0; ageComponent=0; return 0; }   // ← upgrades free
    days = Mathf.Max(1, days);
    tierChangeComponent = num2 * num3;                       // $250 × tier delta
    ageComponent = num2 * Mathf.Max(6 - days, 1);            // ages 6..1, floored at 1
    return tierChangeComponent + ageComponent;
}
```

- **Upgrades are free** — `num3 <= 0` returns 0 penalty.
- **Penalty has two components**: tier-delta × $250 + age-discount × $250.
- **`days`** is `performanceHistory.Count` at penalty time. So a fresh-contract demote (1 day of history) pays 5×$250 = $1,250 age plus tier-delta. After 6+ days of history, the age component is $250 (floor).
- Tier 5→0 at 0 days: `(5×250) + (6×250) = $2,750`. Tier 5→0 after 6+ days: `(5×250) + (1×250) = $1,500`.
- **Penalty applied via `ApplyToBalance(-num, Ledger.Category.Freight, …, quiet: true)`** — quiet means no `BalanceDidChange` broadcast hyperlink in console; the user only sees the broadcast string from `RollToNextContract` itself.

### MP authority

`RollToNextContract` is invoked only from `OpsController.DayDidChange` which is `if (StateManager.IsHost)` gated. There is no client-callable path. `SetContract` writes the `contract` KVO key (HostOnly via `IndustryStorageHelper`).

---

## `Contract` (struct) and the tier table

```csharp
public readonly struct Contract                              // Contract.cs:7
{
    public readonly int Tier;
    public const int TimelyDeliveryMaxDays = 2;
    public float Percent     => ContractExtensions.NumbersForTier(Tier).percent;
    public float SpeedBonus  => ContractExtensions.NumbersForTier(Tier).speedBonus;
}
```

### NumbersForTier

```csharp
public static (float percent, float speedBonus) NumbersForTier(int tier)  // ContractExtensions.cs:108
{
    return tier switch {
        1 => (0.24f, 0f),
        2 => (0.34f, 0f),
        3 => (0.49f, 0f),
        4 => (0.70f, 0f),
        5 => (1.00f, 0f),
        _ => throw new ArgumentException("Invalid tier"),    // ← throws on 0 or 6+
    };
}
```

**Tier 0 is illegal here.** `Contract(0)` is constructible, but calling `Percent`/`SpeedBonus` on it throws. Vanilla never does — tier 0 = "contract terminated" lives only as the transient `NextContract` value during `RollToNextContract`, after which `SetContract(null)` is called instead. **A mod that resurrects a tier-0 contract via `industry.Contract = new Contract(0)` will throw on the next `GetContractMultiplier()` call.**

**`SpeedBonus` is unused** — every tier has 0. The field is wired through `Contract.SpeedBonus` but no consumer reads it.

### TimelyDeliveryBonus

```csharp
public int TimelyDeliveryBonus(int days, int basePayment)   // Contract.cs:35
{
    float num = Tier switch {
        2 => 4f, 3 => 6f, 4 => 8f, 5 => 10f, _ => 0f,        // ← tier 1 has no bonus
    };
    return Mathf.RoundToInt(basePayment * (days switch {
        0 => num, 1 => num/2f, 2 => num/4f, _ => 0f,         // ← only days 0/1/2 pay anything
    } / 100f));
}
```

| Tier \ Days late | 0 | 1 | 2 | 3+ |
|---|---|---|---|---|
| 1 | 0% | 0% | 0% | 0% |
| 2 | 4% | 2% | 1% | 0% |
| 3 | 6% | 3% | 1.5% | 0% |
| 4 | 8% | 4% | 2% | 0% |
| 5 | 10% | 5% | 2.5% | 0% |

**`days`** is `Mathf.FloorToInt(now.DaysSince(waybill.Created))` from `IndustryContext.PayWaybill:342` — **NOT** age-minus-grace. So a 2-day-grace + 2-days-old waybill at tier 5 still pays `10%/4 = 2.5%` bonus, not `10%` (the grace days don't help the bonus tier; they only help the performance score).

**Const `TimelyDeliveryMaxDays = 2`** is unused — the actual cutoff lives in the `days switch` literal.

### Patch candidates (Contract)

| Method | Why patch |
|---|---|
| `ContractExtensions.NumbersForTier` | Change tier multipliers (24/34/49/70/100). Watch the tier-0 throw. |
| `Contract.TimelyDeliveryBonus` | Change bonus rates or extend the days window beyond 2. |
| `ContractExtensions.PenaltyForChange` | Change downgrade penalty math; modify the upgrades-free invariant. |
| `Contract.FromPropertyValue` / `PropertyValue` | Wire format — extend if your contract carries more state than `Tier`. |

---

## `ContractExtensions.AvailableContracts` — the player offers

```csharp
public static List<Contract> AvailableContracts(this Industry industry)  // ContractExtensions.cs:64
{
    if (StateManager.Shared.GameMode == GameMode.Sandbox)
        return MakeContracts(0, 5);                          // ← sandbox: all 6 (incl. tier 0!)

    Contract? contract = industry.Contract;
    if (contract.HasValue) {
        Contract value = contract.Value;
        var list = industry.PerformanceHistory
                          .OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        float num = (list.Count == 0) ? 0f : list.Average();
        int tier = value.Tier;
        int num2 = (num > 0.9f) ? ((num > 0.95f) ? tier+2 : tier+1)
                                 : ((num > 0.7f) ? tier : tier-1);
        int num3 = num2;
        if (list.Count < 3) num3 = Mathf.Min(num3, tier);          // ← evidence gate
        if (list.Count < 5) num3 = Mathf.Min(num3, tier + 1);      // ← max +1 in first 5 days
        return MakeContracts(0, num3);
    }
    int endTier = ReputationTracker.Shared.ContractMaxStartTier();  // ← 1/2/3 from rep
    return MakeContracts(0, endTier);
}

private static List<Contract> MakeContracts(int startTier, int endTier)
{
    startTier = Mathf.Clamp(startTier, 0, 5);
    endTier = Mathf.Clamp(endTier, 1, 5);                    // ← endTier floors at 1
    var list = new List<Contract>(1 + endTier - startTier);
    for (int i = startTier; i <= endTier; i++)
        list.Add(new Contract(i));
    return list;
}
```

### The `MakeContracts(0, N)` quirk

**Every offer includes tier 0** as the first entry — that's the "terminate contract" choice in the UI. `MakeContracts(0, 5)` produces `[Contract(0), Contract(1), Contract(2), Contract(3), Contract(4), Contract(5)]`. Sandbox mode shows all six. `endTier` is clamped to a **min of 1**, so even with `ContractMaxStartTier()` returning 0 (impossible in vanilla — minimum return is 1) you'd still see `[0, 1]`.

### Per-history-length performance-driven cap

For an existing-contract industry:

| `avg(history)` | proposed cap |
|---|---|
| > 0.95 | `tier + 2` |
| > 0.90 | `tier + 1` |
| > 0.70 | `tier` |
| ≤ 0.70 | `tier - 1` |

Then capped by:
- `<3 days history`: cap ≤ current tier (no promotion possible)
- `<5 days history`: cap ≤ current tier + 1 (max one promotion)

**Bug surface:** thresholds `> 0.95` is checked first, then `> 0.90`. So `avg = 0.95` exactly hits the `tier + 1` branch (not `+2`). Strict `>`, like the reputation tier breakpoints. **`0.7` exactly drops you to "demote-eligible"** because the second `(num > 0.7f)` is also strict.

### `ContractMaxStartTier` (no-contract path)

```csharp
public int ContractMaxStartTier()                            // ReputationTracker.cs:575
{
    float r = Reputation;
    if (r > 0.95f) return 3;
    if (r > 0.90f) return 2;
    return 1;
}
```

So a new industry with no contract can be offered:
- **Reputation > 0.95**: tiers 0, 1, 2, 3
- **Reputation > 0.90**: tiers 0, 1, 2
- **Otherwise**: tiers 0, 1

(Plus the "0" terminate option that's always present.) **Reputation has zero influence once an industry has any contract** — promotions/demotions thereafter are entirely driven by `_perfHist`.

### `Industry.ModifyContract(int)`

```csharp
public void ModifyContract(int modifyTier)                   // Industry.cs:365
{
    StateManager.AssertIsHost();
    if (modifyTier == 0) {
        if (Contract.HasValue) NextContract = new Contract(0);  // queue terminate
        else NextContract = null;
        return;
    }
    if (modifyTier == Contract?.Tier) {
        NextContract = null;                                    // cancel pending change
        return;
    }
    foreach (Contract item in this.AvailableContracts())
        if (item.Tier == modifyTier) { NextContract = item; break; }
}
```

Validates that the requested tier is in `AvailableContracts()`. **No-op silently** if the requested tier isn't available. Sets `NextContract` only — actual application waits until `RollToNextContract` runs at the next day-rollover.

**Wire path:** UI → `StateManager.ApplyLocal(new ModifyContract(industryId, tier))` (`LocationsPanelBuilder.cs:104`) → `MinimumAccessLevel(AccessLevel.Officer)` auth check → host's `StateManager.HandleGameMessage` (`StateManager.cs:604-609`) → `industry.ModifyContract(tier)`.

**Officer-auth, not Host-only.** This is one of the few message-mediated paths into ops state; client Officers can drive contracts.

### Sandbox-only `/ops setTier`

```csharp
[ConsoleSubcommand(null, "Set the contract tier for an industry.")]
private string SetTier(string industryId, int tier)         // OpsCommand.cs:109
{
    if (!StateManager.IsSandbox) return "Only available in sandbox.";
    var industry = …;
    if (tier > 0) industry.Contract = new Contract(tier);   // ← direct setter, no NextContract
    else          industry.Contract = null;
}
```

Direct writes to `Industry.Contract` (the setter is internal but accessible from the same assembly). **Skips `RollToNextContract`** — no penalty, no broadcast, no `_perfHist` clear via `SetContract` (because the setter at `:80` is the assignment-only setter — only `SetContract(...)` calls `ClearPerformanceHistory`). So this is a "raw poke" that leaves history intact.

Wait — actually, `industry.Contract = ...` uses the property setter at `Industry.cs:80` which only writes the `contract` KVO key. **It does NOT call `ClearPerformanceHistory`**. The console command is bypassing the wipe that `SetContract(...)` would normally trigger. Useful for testing; surprising for mods that observe `contract` KVO and assume `_perfHist` was just reset.

### Patch candidates

| Method | Why patch |
|---|---|
| `ContractExtensions.AvailableContracts` | Replace the offer-set logic. Sandbox returns 0..5 directly here. |
| `ContractExtensions.MakeContracts` | Change inclusion of tier-0 in offer lists. |
| `Industry.ModifyContract` | Add side effects on `NextContract` queue (e.g., custom UI confirmation, dependent industry notifications). |
| `ReputationTracker.ContractMaxStartTier` | Change reputation thresholds for no-contract starts. |
| `Industry.Contract` setter | Hook to fire mod events on contract change (cleaner than KVO observe for typed access). |
| `Industry.SetContract` | If you want to preserve history across contract changes, intercept here to skip `ClearPerformanceHistory`. |

---

## Daily payment cadence

Two distinct payment paths exist.

### Path A — `IndustryContext.PayWaybill` (per-car, immediate)

Fired from `IndustryComponent.OnCompleteWaybill` (`IndustryComponent.cs:172`) when a car arrives at its waybilled destination during the regular `Service` tick.

```csharp
public void PayWaybill(IOpsCar car, Waybill waybill)         // IndustryContext.cs:334
{
    GameDateTime now = Now;
    int paymentOnArrival = waybill.PaymentOnArrival;
    int num = waybill.ConditionFineForCarCondition(car.Condition);    // damage fine
    int num2 = 0;
    if (_industry.HasActiveContract(now)) {
        int days = Mathf.FloorToInt(now.DaysSince(waybill.Created));
        num2 = _industry.Contract.Value.TimelyDeliveryBonus(days, paymentOnArrival);
    }
    int num3 = paymentOnArrival + num2 - num;
    if (num3 != 0) {                                                  // ← nothing to do at zero
        _industry.ApplyToBalance(num3, Ledger.Category.Freight, …, count: 1, quiet: true);
        _controller.AddCoalescedPaymentAnnouncement(car, _industry, paymentOnArrival,
            new[]{ (num2,"timely"), (-num,"damage") }, num3);
    }
}
```

After payment, `OnCompleteWaybill` zeroes `PaymentOnArrival` and sets `Completed = true` (`IndustryComponent.cs:175-177`), then `Industry.ReceivedCarCount++`. **The `ReceivedCarCount` is the only counter that gates the perfect-but-empty `null` short-circuit in `CalculatePerformance`.**

**Asymmetry:** `ConditionFineForCarCondition` is `floor(payment * Lerp(0, 0.75, InverseLerp(0.95, 0, condition)))` (`Waybill.cs:75`). So a car at ≤ 0.95 condition starts losing money; at 0% condition, you forfeit 75% of payment. Damage fine **applies even without a contract** (no `HasActiveContract` gate around `num`). Timely bonus only applies *with* a contract.

**Failed contract = no failed payment.** There is no path that pays out reduced amounts for "failed" contracts. The only mechanism that reduces income from contract failure is `RollToNextContract`'s `PenaltyForChange` debit — a single lump sum at tier-change time.

**Coalesced announcement** (`OpsController.AddCoalescedPaymentAnnouncement`): aggregates per-industry over a 1-second window in `PeriodicUpdate` → `AnnounceCoalescedPayments` (`OpsController.cs:128, 1166`). So multiple-car deliveries in one moment produce one rolled-up "Payment for delivery of N cars" message, not one per car.

### Path B — `IndustryUnloader.DailyReceivables` (daily-batch)

```csharp
public override void DailyReceivables(GameDateTime now, IIndustryContext ctx)  // IndustryUnloader.cs:98
{
    float num = ctx.CounterIncrement(KeyUnloadedTotal, 0f);   // peek (delta=0)
    if (!(num < 1f)) {
        ctx.PayLoad(load, num);                                // batch payment
        ctx.CounterClear(KeyUnloadedTotal);
    }
}
```

```csharp
public void PayLoad(Load load, float units)                  // IndustryContext.cs:357
{
    int num = Mathf.RoundToInt(load.payPerQuantity * units);
    if (num != 0) {
        _industry.ApplyToBalance(num, Freight, …, count: round(units / Nominal), quiet: true);
        Multiplayer.Broadcast($"Payment from {…} for delivery of {…}: {num:C0}");
    }
}
```

**Counter key**: `"unloaded-total-<load.id>"` on the industry KVO. Accumulated via `ctx.CounterIncrement` inside `IndustryUnloader.Service` whenever `load.payPerQuantity > 0`. Daily threshold: `>= 1.0` units (smaller fractional loads carry over to next day).

**This payment path has no condition fine, no timely bonus, no contract gate.** It's a flat `payPerQuantity * units` rate. The contract multiplier already affects upstream consumption (`carUnloadRate * contractMultiplier`), so a low-tier industry pays you less because it consumes less per day.

### Path C — `OnCompleteWaybill` for zero-payment waybills

If `PaymentOnArrival == 0`, `PayWaybill` does nothing (`num3 != 0` gate). But the waybill still flips `Completed = true` and `ReceivedCarCount++` still fires. **Autodest cars (tag `"autodest"`) and progression-spawned cars are zero-payment** (see [ops-routing](ops-routing.md) and [industries-ops](industries-ops.md)). They count toward `ReceivedCarCount` (so they prevent the perfect-but-empty `null` skip) but contribute nothing to income.

### Sub-day cadence

`Industry.TickCoroutine` runs every 5 game seconds with `Tick(15f, shouldService=tick%3==0)` — so `Service` runs every 15 game-seconds, but `CheckForCompleted` runs every 5. **Waybills can complete multiple times per game-day — the daily cadence is only for `DailyReceivables`/`RollToNextContract`/`UpdatePerformance`.**

### Patch candidates (payment)

| Method | Why patch |
|---|---|
| `IndustryContext.PayWaybill` | Add custom payment modifiers (e.g., contract-tier-aware payment, custom load-type multipliers). |
| `IndustryContext.PayLoad` | Modify daily-batch payment for `payPerQuantity` loads. |
| `Waybill.ConditionFineForCarCondition` | Replace damage-fine curve. |
| `Contract.TimelyDeliveryBonus` | Replace timely-bonus table or extend window past 2 days. |
| `IndustryComponent.OnCompleteWaybill` | The chokepoint that calls `PayWaybill` and updates `ReceivedCarCount`. Patch to add custom completion side-effects. |
| `IndustryUnloader.DailyReceivables` | Add per-day side-effects on top of `PayLoad`. |

---

## Waybill aging — they don't TTL

A `Waybill` is a struct serialized to the `ops.waybill` key on each car's KVO ([ops-routing › waybill section](ops-routing.md#waybills--auto-destination--ops-tags)).

```csharp
public struct Waybill {                                       // Waybill.cs
    public GameDateTime Created;
    public OpsCarPosition? Origin;
    public OpsCarPosition Destination;
    public int PaymentOnArrival;
    public bool Completed;
    public readonly string Tag;
    public int GraceDays;
}
```

**There is no automatic expiration.** A car with a waybill keeps it until:
1. Manually cleared via `car.SetWaybill(null)` (UI, console, or autodest cycle).
2. The destination industry's contract terminates → `OpsController.ReturnWaybillsFrom` rewrites the waybill (player-owned: autodest; foreign: cleared to interchange).
3. `Section.ApplyCompleted` triggers `InterchangeTransfer.Apply` → `OpsController.RewriteWaybills(fromPrefix, toPrefix)` rewrites prefix-matching destinations/origins (progression unlock).
4. `OnCompleteWaybill` flips `Completed = true` (but the waybill struct stays on the car — see below).

**Completed waybills linger.** After arrival, the waybill struct remains on the car with `Completed = true` and `PaymentOnArrival = 0` until something else replaces it. `OpsController.GetOpenWaybills` filters `where !tuple.Waybill.Completed` (`:904`), and `UpdatePerformance` then filters `where w.PaymentOnArrival > 0`. So completed waybills are invisible to performance computation but remain on the car for inspection.

**The only thing that "ages" is the per-day delta between `Created` and `now`** — used by `UpdatePerformance` (gated by `PaymentOnArrival > 0`, so completed/autodest/sell-tagged waybills don't drag down score) and by `Contract.TimelyDeliveryBonus` (per-arrival, no cutoff).

**Tied to contract lifecycle?** Only via `ReturnWaybillsFrom` which cleans up after tier-0 termination. A contract demotion (tier 5 → 3) does NOT touch the waybills; in-flight cars continue to deliver under the new tier's terms (and potentially earn lower `Percent`-derived storage caps, but the per-waybill `PaymentOnArrival` was set at spawn time and doesn't update).

### `OpsController.RewriteWaybills` (progression's foot-gun)

```csharp
public void RewriteWaybills(string fromPrefix, string toPrefix)  // OpsController.cs:947
{
    foreach (Car car in TrainController.Cars) {
        Waybill? waybill = car.GetWaybill(this);
        if (waybill.HasValue && waybill.Value.Origin.HasValue
            && waybill.Value.Origin.Value.Identifier.StartsWith(fromPrefix)) {
            // …Origin = ResolveRewritten(value.Origin.Value.Identifier);
        }
        if (waybill.HasValue && waybill.Value.Destination.Identifier.StartsWith(fromPrefix)) {
            // …Destination = ResolveRewritten(...)
        }
        // Same for autodest.Empty + autodest.Load
    }

    OpsCarPosition ResolveRewritten(string identifier) =>
        ResolveOpsCarPosition(identifier.Replace(fromPrefix, toPrefix));     // ← non-anchored
}
```

**Inner `Replace` is non-anchored** (replaces all occurrences, not just at start). For typical `industry.subcomponent` identifiers this works because the prefix only appears once, but a poorly-chosen prefix that occurs in subcomponent suffixes would corrupt identifiers. See [ops-routing › RewriteWaybills foot-gun](ops-routing.md#opscontrollerrewritewaybills).

### Patch candidates (waybill aging)

| Method | Why patch |
|---|---|
| `OpsController.GetOpenWaybills` | Change which waybills count as "open" (vanilla: `!Completed`). |
| `OpsController.UpdatePerformance` | Change the `PaymentOnArrival > 0` filter to include autodest in scoring. |
| `OpsController.ReturnWaybillsFrom` | Customize tier-0 termination cleanup. |
| `OpsController.RewriteWaybills` | Add anchored matching, or extend to other waybill fields. |

---

## "autodest" tag and the payment-0 mechanic

Cross-link from [industries-ops › autodest tag](industries-ops.md#tags-and-autodest) and [economy › PayWaybill flow](economy.md). Brief recap here for completeness.

`CarExtensions.SetWaybillAuto` (`CarExtensions.cs:156`) creates a waybill with `tag="autodest"`, `paymentOnArrival=0`, `graceDays=0`. The `"autodest"` tag is a string constant `CarExtensions.TagAutodest = "autodest"`. The `"sell"` tag is the analogous mechanism for selling cars at an interchange.

**Effect on this system:**
- `OpsController.UpdatePerformance` filters `w.PaymentOnArrival > 0` — autodest waybills don't influence `_perfHist`.
- `IndustryContext.PayWaybill` skips the ledger write when total payment is 0 — no income, no announcement.
- `IndustryComponent.OnCompleteWaybill` still increments `ReceivedCarCount` — so autodest deliveries help an industry escape the "perfect-but-empty" `null` short-circuit, even though they pay nothing. **A pure-autodest day with no real contracts WILL log a `1.0` performance** if any autodest car arrived. This is probably not intentional — feels like a leak between the autodest and contract performance subsystems.

---

## MP authority on contract state

| Action | Who | Mechanism |
|---|---|---|
| Read `Contract` / `NextContract` / `PerformanceHistory` | Anyone | KVO snapshot, all-machines |
| Set `industry.Contract` directly | Host only | `_internal_` setter; `IndustryStorageHelper.AuthorizationRequirementForPropertyWrite("contract")` returns `HostOnly` |
| Set `NextContract` via `ModifyContract` message | **Officer** | `[MinimumAccessLevel(AccessLevel.Officer)]` on `Game.Messages.ModifyContract` |
| Run `RollToNextContract` | Host only | Called from `OpsController.DayDidChange` host-gated |
| Run `UpdatePerformance` | Host only | Same |
| Run `ClearPerformanceHistory` | Host only | `StateManager.DebugAssertIsHost()` inside |
| Run `AddPerformanceHistoryEntry` | Host only | Same |
| `/ops setTier` | Host only + Sandbox only | Direct call into `industry.Contract` setter |

**Officer-auth `ModifyContract` is the only client→host channel.** It only writes `NextContract`, never `Contract` directly — so the actual tier change waits for the host's daily roll. There is no client-driven path to force an immediate tier flip.

**Per-key auth** lives in `IndustryStorageHelper.AuthorizationRequirementForPropertyWrite` (`:226`):

```csharp
public AuthorizationRequirementInfo AuthorizationRequirementForPropertyWrite(string key)
{
    var req = (key == "extraScheduled") ? AuthorizationRequirement.MinimumLevelTrainmaster
                                         : AuthorizationRequirement.HostOnly;
    return req;
}
```

Every key on the industry KVO is HostOnly **except** `extraScheduled` (Trainmaster — for "schedule extra interchange service"). So `_perfHist`, `contract`, `nextContract`, `_recvdCars`, `storage`, `unloaded-total-*`, `init`, `lastServiced`, `interchangeDisabled`, `warnings` — all host-write.

**No `RequestRollContract`/`RequestSetPerformance` messages exist.** Mods that want client-driven contract logic must either:
1. Define a new `IGameMessage` with appropriate auth.
2. Patch `Industry.RollToNextContract` host-side and trigger via custom event.

---

## Save/load — contract state persistence

All contract state lives on the industry's `KeyValueObject`, which is registered via `StateManager.Shared.RegisterPropertyObject(industry.identifier, kvObject, helper)` in `IndustryStorageHelper`'s constructor. Standard property-object snapshot path applies — no custom `OnPropertiesDidRestore` on `Industry` itself.

**Persisted keys (subset relevant to contracts):**
- `contract` (dict with `tier`)
- `nextContract` (dict with `tier`)
- `_perfHist` (dict with day-string → float performance)
- `_recvdCars` (int; null when 0)
- `init` (string; current `Application.version`, used to gate `Initialize` re-runs)
- `unloaded-total-<load.id>` (float; carried across saves)

`OpsController.PostRestoreProperties` runs after world load (`StateManager.cs:1176`) — it does:
1. `CheckLoads()` — validates load IDs on cars.
2. `CheckWaybills()` — re-resolves waybill positions; broken waybills are reset (host-side: player-owned → autodest, foreign → fresh interchange waybill).
3. `RebuildPopulations()` — passenger pop based on contract.
4. `CheckServiceInterchanges()`.

**`Industry.InitializeIfNeeded`** is called on the first `TickCoroutine` iteration (`Industry.cs:188`). Compares `Application.version` against the `init` KVO key; if different, re-runs `IndustryComponent.Initialize` for every component (used to seed initial storage, etc.). Mid-save `Application.version` migration triggers re-init — so a mod that adds a new component should call `Initialize` itself for first-load consistency, or rely on the version bump.

**Load-time contract behavior:**
- `_perfHist` is restored as-is, but `lastDay` (in `Reputation`) is restored separately. `ReputationTracker.TickCoroutine` runs `UpdateReputation` immediately if `lastDay < Now.Day` — so a save loaded after midnight may compute a reputation update against a snapshotted-pre-midnight `_perfHist`.
- `OpsController.DayDidChange` is event-driven (Messenger `TimeDayDidChange`). On load, the listener registers in `OnEnable`. **There's no catch-up trigger at load time** — if the saved game's day matches the current day, no daily orchestration runs until the next day-rollover. Loading a save in the middle of a day is safe.

### Patch candidates (save/load)

| Method | Why patch |
|---|---|
| `OpsController.PostRestoreProperties` | Add custom post-load validation for contract state. |
| `Industry.InitializeIfNeeded` | Hook for one-time setup of mod-added contract data, version-gated. |

---

## Cross-cutting: fields and types touched

| Type | Used in | Notes |
|---|---|---|
| `Contract` (struct) | Industry, ContractExtensions, IndustryContext | Tier 0 unsupported in `NumbersForTier` (throws). `SpeedBonus` field is dead. |
| `Waybill` (struct) | Car KVO `ops.waybill`, OpsController, IndustryComponent, IndustryContext | No TTL. `Completed=true` lingers on car. |
| `OpsCarPosition` (struct) | Waybill.Origin/Destination, autodest, Industry.Contains | Resolved via `IOpsCarPositionResolver.ResolveOpsCarPosition`. |
| `IIndustryContext` | Per-component per-tick handle | Carries the industry's KVO + helpers; the only legit interface for mod components. |
| `Ledger.Category.Freight` | All contract payments + penalties | Single category for contract income, autodest pays here too. |
| `_reputation.total` (KVO float) | `ContractMaxStartTier` reads | Only consulted for no-contract industries. |

---

## Gotchas

- **`Industry.SetContract` clears `_perfHist`.** Including on promotions. Three-day evidence requirement (`AvailableContracts.list.Count < 3` clause) effectively traps you at the new tier for a minimum of 3 days. Mods that want continuous history must skip `ClearPerformanceHistory` in `SetContract`.
- **`industry.Contract = …` (the setter) does NOT clear history.** Console `/ops setTier` and `Industry.ModifyContract`'s search loop both use the setter indirectly. Direct setter writes leak history across tiers; `SetContract(...)` writes wipe it. **Behavioral split between setter and method with the same effective purpose.**
- **`Contract(0)` throws on `Percent`/`SpeedBonus` access.** Only ever appears as a transient `NextContract` during `RollToNextContract`. A mod that lingers tier-0 contracts will crash `GetContractMultiplier()`.
- **`SpeedBonus` is dead.** Field exists, getter returns 0 for every tier. No consumer reads it. Free space for mod use.
- **`TimelyDeliveryMaxDays = 2` const is unused.** The actual cutoff lives in the `days switch` literal in `TimelyDeliveryBonus`. Patching the const does nothing.
- **Penalty is calculated against `performanceHistory.Count`, not days-since-contract-start.** Industries with promoted-then-demoted history have history wiped by `SetContract` — so the next failure-driven demote happens against `Count == 1` and pays the maximum age penalty. This punishes "bouncing" between tiers.
- **`IsFailing` requires exactly 3 entries in `_perfHist`.** With history wipes on `SetContract`, the soonest a freshly-set contract can auto-demote is day 3 of operation (assuming consecutive days). Players have a guaranteed 2-day grace per contract-change.
- **`UpdatePerformance` records ages against BOTH origin and destination.** A "shipping-only" industry's `_perfHist` is fed by waybills it issues; a "receiving-only" industry's is fed by waybills addressed to it. Industries that both ship and receive get both recorded. **Origin-recorded waybills count even after the cars have left the industry's tracks.** A waybill that takes 30 days to deliver from your industry to a customer hammers your `_perfHist` for those 30 days.
- **Grace days subtracted from age for performance, NOT from `days` for timely bonus.** A 2-day grace + 2-day-old waybill scores `InverseLerp(5,1,0) = 1.0` for performance but pays `bonus/4` (2 days, not 0) for timely.
- **`UpdatePerformance` filters waybills with `PaymentOnArrival > 0`.** Autodest, sell, and progression-spawned waybills are invisible. **But `ReceivedCarCount++` fires for autodest deliveries too** — so an industry receiving only autodest cars escapes the perfect-but-empty `null` short-circuit and logs `1.0` daily. Asymmetric: autodest helps the score-write gate but doesn't contribute to score.
- **`_perfHist` trim removes by `Day` key min.** A mod that injects out-of-order day keys (huge or negative) breaks the rolling window.
- **`AddPerformanceHistoryEntry` silently no-ops on duplicate-day key.** If you call it twice in one game-day, the second call is dropped (with a warning). Time-warp mods that re-roll the same day will lose mid-day entries.
- **The `> 0.95` / `> 0.90` / `> 0.70` / `> 0.95` thresholds in `AvailableContracts` and `ContractMaxStartTier` are STRICT.** Exact-equal averages drop one bucket lower than they look.
- **`MakeContracts(0, …)` always includes tier 0** as the "terminate" option. UI dropdown shows it; mods that expose contract pickers should account for it.
- **`AvailableContracts` reads `industry.PerformanceHistory` ONCE** at the top — fine, but if you patch the property to compute lazily, account for that.
- **`OpsController.DayDidChange` order is fixed: UpdatePerformance → DailyReceivables → RollToNextContract → DailyPayables.** Performance computed *before* receivables means the day's deliveries influence tomorrow's score, not today's. Tier change happens *after* receivables, so today's batch payment uses the old tier multiplier (which already affected the in-tick `Service` rate).
- **No catch-up on missed days.** `OpsController.DayDidChange` is a Messenger handler; a single fire per day-rollover. Skip-days = skip-rolls.
- **Sandbox `AvailableContracts` returns `[Contract(0..5)]`** ignoring history entirely. Patches that add custom tiers should branch on `IsSandbox`.
- **`Industry.Contract` getter calls `Contract.FromPropertyValue` which defaults missing `tier` to 1.** A malformed-but-non-null `contract` value yields tier 1, not null. Subtle save-corruption recovery behavior.
- **`Industry.usesContract = false`** (set in editor per-industry) makes `HasActiveContract` always return false but `GetContractMultiplier` returns 1.0 (not 0). So non-contract industries operate at full capacity always. They never receive `_perfHist` entries because `IncludeInFreightPerformance` requires `HasActiveContract`.
- **`ReceivedCarCount` decrements never** in vanilla — it's monotonically incremented in `OnCompleteWaybill` and reset to 0 only by `ClearPerformanceHistory` (i.e., on `SetContract`). Long-lived contracts accumulate large counts that don't matter for any logic but are visible in saves.
- **`Industry.ModifyContract(modifyTier == Contract.Tier)` clears `NextContract`** — useful as a "cancel pending change" handle. The UI doesn't expose this directly; calling `ModifyContract` with the current tier is the cancel verb.
- **The 5-second tick spread (`yield return new WaitForSeconds(UnityEngine.Random.Range(0f, 15f));`)** randomizes industry ticks at startup so they don't all `Service` simultaneously. Per-industry independent seed; no synchronized behavior.

---

## Init order

1. `Industry.Awake` — creates KVO, `IndustryStorageHelper` registers it via `StateManager.Shared.RegisterPropertyObject` (HostOnly auth).
2. World load / snapshot apply — `_perfHist`, `contract`, `nextContract`, etc. populated.
3. `OpsController.PostRestoreProperties` runs `CheckWaybills` etc.
4. `Industry.OnEnableWithProperties` — host starts `TickCoroutine`.
5. First `TickCoroutine` iteration after a 0–15s random delay — `InitializeIfNeeded` runs.
6. Subsequent `Tick(15f, …)` every 5 game-seconds.
7. **First `TimeDayDidChange` Messenger fire after midnight** — `OpsController.DayDidChange` runs the orchestration. No catch-up on day skips.

---

## Cross-references

- Tier-table consumers (`PhaseDiscount`, `RepairBonus`, `EquipmentDiscount`, `ContractMaxStartTier`): see [reputation › Tier tables](reputation.md#tier-tables).
- Waybill struct, autodest tag, `RewriteWaybills` foot-gun: see [ops-routing › Waybills & autodest](ops-routing.md).
- `Ledger.Category.Freight` and the broader payment pipeline: see [economy › ApplyToBalance flow](economy.md).
- `RepairTrack` industry component (the only vanilla `DailyPayables` consumer): see [wear-durability › RepairTrack](wear-durability.md#modelopsrepairtrack-industry-side-repair).
- Passenger contract scoring (PBO bonus): see [passengers-timetable › PBO bonus](passengers-timetable.md).
- Industry tick spine and the broader `IndustryComponent` taxonomy: see [industries-ops](industries-ops.md).
- `IndustryStorageHelper` save/load auth pattern: see [save-load › property-object lifecycle](save-load.md).
