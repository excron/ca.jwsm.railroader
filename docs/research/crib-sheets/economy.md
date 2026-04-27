# Economy — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/Game.State/`, `Game.Messages/`, `Model.Ops/`)
**Companions:** [State Manager](state-manager.md), [Industries & Ops](industries-ops.md), [Wear & Durability](wear-durability.md), [Cars, Cargo & Loading](cars-cargo.md), [Request Messages](request-messages.md), [Time & Weather](time-weather.md)

The economy is one int — `_storage["balance"]` — and one append-only `List<Ledger.Entry>` in process memory. **Every credit and debit must funnel through `StateManager.ApplyToBalance`**, which is `[AssertIsHost]`-gated, records to `Ledger`, mutates the KVO-backed `Balance`, and fires `BalanceDidChange`. Loans live on a separate `LoanManager` MonoBehaviour created on `PropertiesDidRestore` for non-Sandbox games; equipment purchase uses one of vanilla's *only two* true request/response message pairs (`LedgerRequest`/`LedgerResponse` is the other). Clients see balance via the `_game` KVO; ledger details come on demand via the request/response pair. There is no `RequestApplyToBalance` — clients cannot directly debit/credit; all economy changes happen host-side from inside ops/loan/purchase code paths.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `StateManager.ApplyToBalance(int, Category, EntityReference?, string memo, int count, bool quiet)` | `Game.State/StateManager.cs:1265` | THE entry point. Host-only. Writes Ledger + Balance + fires BalanceDidChange. |
| `StateManager.CanAfford(int)` | `StateManager.cs:1256` | `Sandbox ? true : Balance >= expense` |
| `StateManager.GetBalance()` / `Balance { get; }` | `StateManager.cs:1298, 150` | Read-only access; reads `_storage.Balance` (KVO `balance`) |
| `Ledger` (instance on StateManager) | `Game.State/Ledger.cs:9`, owned at `StateManager.cs:62` | The append-only entry list + `Category` enum |
| `Ledger.Record(amount, category, payee, memo, count, now)` | `Ledger.cs:69` | Internal, called by ApplyToBalance only. AssertIsHost. |
| `Ledger.EntriesBetween(start, end, out startBal, out endBal)` | `Ledger.cs:82` | Used by FinancePanelBuilder + DailyReport + LedgerRequest |
| `LoanManager` (MonoBehaviour) | `Game.State/LoanManager.cs:12` | Loan state, interest tick, request handler |
| `EquipmentPurchase` (static) | `Game.State/EquipmentPurchase.cs:19` | Purchase + trade-in handlers |
| `BalanceDidChange` (Messenger event) | `Game.Events/BalanceDidChange.cs:6` | Empty struct, broadcast on every nonzero ApplyToBalance |
| `LedgerRequest`/`LedgerResponse` (req/resp pair) | `Game.Messages/LedgerRequest.cs`, `LedgerResponse.cs` | Client → host pull of `[start..end)` entries |
| `RequestLoanDelta` | `Game.Messages/RequestLoanDelta.cs` | Officer-level; client → host loan adjustment |
| `RequestPurchaseEquipment` | `Game.Messages/RequestPurchaseEquipment.cs` | Officer-level; client → host purchase |

---

## Spine: how money moves

```
┌─────────────────────── HOST ONLY ───────────────────────┐
│                                                          │
│  Industry tick (15s game seconds, host-only)             │
│    IndustryContext.PayWaybill(car, waybill)              │  Freight
│    IndustryContext.PayLoad(load, units)                  │  Freight
│    Industry.RollToNextContract → Tier Change Penalty     │  Freight (-)
│  PassengerStop.Loop coroutine                            │
│    StateManager.ApplyToBalance(..., Passenger, ...)      │  Passenger
│  Interchange.SellAndRemove(opsCar)                       │  Equipment (+)
│  InterchangedIndustryLoader.ServeInterchange             │  Fuel (-) [SerializeField]
│  RepairTrack.DailyPayables (midnight)                    │  WagesRepair (-)
│  Industry.DailyPayables / DailyReceivables (midnight)    │  fan-out
│  StateManager.OnDayDidChange → PayAutoEngineerWages      │  WagesAI (-)
│  LoanManager.PayInterestIfNeeded (5s coroutine, every    │
│    5 GAME days)                                          │  Loan (-)
│  Progression.PayToStartPhase                             │  Progression (-)
│  CompanyModeSetup.Setup (initial money seed)             │  Bank (+)
│  EquipmentPurchase.HandleRequest                         │  Equipment (-)
│  /money cheat (Sandbox console)                          │  Bank (+/-)
│                                                          │
│              ALL PATHS converge here:                    │
│    StateManager.ApplyToBalance(amount, category, ...)    │
│      AssertIsHost()                                      │
│      Ledger.Record(...) → ChangedEvent (Messenger)       │
│      Balance += amount  (writes _game KVO key "balance") │
│      Multiplayer.Broadcast(...) unless quiet=true        │
│      Schedule "stamp"/"punch" audio for Freight/Passenger│
│      SendFireEvent(BalanceDidChange) → FireEvent msg     │
│        → HandleFireEvent on every receiver               │
│        → Messenger.Send(BalanceDidChange)                │
│                                                          │
└──────────────────────────────────────────────────────────┘
                           │
                           ▼   (fire-event broadcast, code 0)
                  ALL CLIENTS receive BalanceDidChange
                           │
                           ▼
              UI.BalanceDisplay animates new balance
              FinancePanelBuilder.RebuildOnEvent<BalanceDidChange>
              GoalsPanelBuilder.RebuildOnEvent<BalanceDidChange>
```

**Bypass review:** No vanilla code writes `_storage["balance"]` or `StateManager.Balance` directly other than `ApplyToBalance` and `OnDayDidChange`'s wage path. The only other balance mutator is `Ledger.ReconcileIfNeeded`, which on host load adds a `Bank` "Balance Correction" entry whose `Amount = expectedBalance - sum(entries)` — so the running balance from re-summing equals the persisted Balance (NOT a Balance write; a Ledger entry whose pre-existing Balance becomes correct again on next sum). Patching here lets you audit save divergence.

---

## `Game.State.Ledger`

```csharp
public class Ledger {
    public enum Category {
        Bank, Freight, Passenger, Fuel, Loan, Equipment,
        WagesRepair, Progression, WagesAI, RepairSupplies
    }

    public struct Entry {
        public GameDateTime Date;
        public int Amount;                  // signed; positive = credit, negative = debit
        public Category Category;
        public EntityReference? Payee;      // (EntityType, identifier) — see EntityReference
        public string Memo;
        public int Count;                   // domain-specific: # passenger fares, # cars, # loads
    }

    [StructLayout(Size = 1)]
    public struct ChangedEvent {}           // Messenger event, fires on Record + Clear

    private readonly List<Entry> _entries;  // process-memory, host-authoritative
    private int _startingBalance;           // restored from save
}
```

(Source: `Game.State/Ledger.cs:9-65`.)

### Public API

```csharp
public void Record(int amount, Category, EntityReference? payee,           // 69
                   string memo, int count, GameDateTime now);
public void Clear();                                                       // 76
public IReadOnlyList<Entry> EntriesBetween(GameDateTime start, end,        // 82
                                            out int startBalance,
                                            out int endBalance);
public void PopulateForSave(List<SerializableLedgerEntry> entries);        // 114
public void Load(List<SerializableLedgerEntry> entries, int startingBal);  // 120
public void ReconcileIfNeeded(int expectedBalance);                        // 132
```

### `EntriesBetween` semantics

```csharp
double startSec = start.TotalSeconds, endSec = end.TotalSeconds;
int running = startBalance = _startingBalance;
endBalance = 0;
foreach (Entry e in _entries) {            // ASSUMES _entries are time-ordered
    running += e.Amount;
    if (e.Date.TotalSeconds >= startSec && e.Date.TotalSeconds < endSec) {
        list.Add(e);
        endBalance = running;
    }
    if (e.Date.TotalSeconds < startSec)
        startBalance = running;             // accumulator before window
}
if (list.Count == 0) endBalance = running;  // fallback: window is empty → both endpoints = running
```

**Range is half-open `[start, end)`.** `endBalance` is the balance **after** the last entry in the window — but if the window is empty `endBalance` equals the final running total (which equals the current Balance). `startBalance` is the balance **before** the first entry in the window.

### `Ledger.Record`

```csharp
public void Record(int amount, Category category, EntityReference? payee,
                   string memo, int count, GameDateTime now)
{
    StateManager.AssertIsHost();
    _entries.Add(new Entry(now, amount, category, payee, memo, count));
    Messenger.Default.Send(default(ChangedEvent));
}
```

**`Record` is `public` but `AssertIsHost`-gated.** Mods on host can call `StateManager.Shared.Ledger.Record(...)` directly to add an entry **without** affecting `Balance` — useful for audit trails, but creates ledger/balance divergence that `ReconcileIfNeeded` will only paper over on the next save/load cycle.

### `ReconcileIfNeeded(expectedBalance)`

```csharp
int sum = _entries.Sum(e => e.Amount) + _startingBalance;
if (sum != expectedBalance) {
    Log.Information("Adding initial balance entry to ledger; {actual} vs {expected}", sum, expectedBalance);
    _entries.Add(new Entry(GameDateTime.Zero, expectedBalance - sum, Category.Bank, null, "Balance Correction", 0));
}
```

Called from `StateManager.OnPropertiesDidRestore` (host only, `StateManager.cs:319`). The injected entry is dated `GameDateTime.Zero` — it predates the game timeline and does NOT trigger `BalanceDidChange` (uses `_entries.Add` directly, not `Record`). Spotting "Balance Correction" entries in a save's ledger is a sign of mod-induced divergence or save-format migration.

### Patch candidates

| Method | Why patch |
|---|---|
| `StateManager.ApplyToBalance` | Single chokepoint for *all* economy. Prefix to veto/cap, postfix to mirror to a mod-side ledger or analytics. |
| `Ledger.Record` | Catch every entry (including the Balance Correction path). |
| `Ledger.EntriesBetween` | Inject synthetic entries for UI display without persisting. |
| `Ledger.ReconcileIfNeeded` | Detect/repair divergence — useful for mod-economies that maintain parallel state. |
| `Ledger.PopulateForSave` / `Ledger.Load` | Migrate / reshape on-disk format. |

### MP authority

- `Ledger.Record` AssertIsHost.
- `Ledger` instance lives on `StateManager.Shared.Ledger` — accessible from anywhere on host but mutators throw on clients.
- Clients see entries only via `LedgerRequest`/`LedgerResponse` (see below). Their local `_entries` list is repopulated by `Ledger.Load(...)` from a `LedgerResponse` payload.

### Gotchas

- **`_entries` is assumed time-ordered but `Record` does not enforce it.** A `now` value out of order (e.g., from a buggy `WaitTime` patch or test harness) corrupts `EntriesBetween`'s running-balance computation.
- **`Clear()` fires `ChangedEvent` but does NOT reset `_startingBalance`.** A subsequent `EntriesBetween` will return all-empty results with `startBalance = endBalance = _startingBalance`. There's no "reset to zero balance" path.
- **`Count` is overloaded per category.** `Freight` uses count = number of cars (`PayWaybill` passes 1), `Passenger` uses count = number of passenger fares, `Equipment` (purchase) passes 0. The DailyReport sums `count` for Freight + Passenger to display "X freight deliveries, Y passenger fares" — patches that change a category's count semantics break the report.
- **`Ledger.Category.RepairSupplies` exists in the enum but has ZERO call sites in vanilla.** `Ledger.cs:22` defines it; no `ApplyToBalance(..., RepairSupplies, ...)` anywhere. `DailyReportGenerator.StringForCategory` formats it as "Repair Supplies" — but the category never appears in any save. Either dead code from a planned feature, or reserved for mods.
- **`Ledger.Category.Fuel` only appears via `InterchangedIndustryLoader.ledgerCategory` `[SerializeField]`** — i.e., the asset author picks it per loader prefab. So whether coal/diesel charges land in `Fuel` vs `RepairSupplies` vs anything else is a Unity-scene decision, not a code constant. Map mods that re-author InterchangedIndustryLoader prefabs determine the bucketing.

---

## `SerializableLedgerEntry` (wire format)

```csharp
[MessagePackObject(false)]
public struct SerializableLedgerEntry {
    [Key("date")]  public int Date;                              // (int)entry.Date.TotalSeconds — TRUNCATES sub-second
    [Key("amt")]   public int Amount;
    [Key("cat")]   public Ledger.Category Category;              // serialized as int
    [Key("payee")] public SerializableEntityReference? Payee;
    [Key("memo")]  public string Memo;
    [Key("count")] public int Count { get; set; }                // note: property, not field
}
```

(Source: `Game.State/SerializableLedgerEntry.cs:5-44`.)

**Key naming uses strings, not ints.** Most other Railroader MessagePack types use `[Key(0)]`-style numeric keys; this one uses `[Key("date")]` etc. Mixing the two on `[MessagePackObject(false)]` (explicit-key mode) is allowed but unusual — be careful when patching MessagePack resolvers.

`Date` is `int` (seconds since epoch in game time). Sub-second precision is dropped on save — replays will quantize. The `Date.TotalSeconds` cast is `(int)` not `Mathf.RoundToInt`, so it truncates toward 0.

`Count` is the only field declared as a *property* (auto-property). MessagePack handles it identically, but reflection-based patches that walk fields-only will miss it.

---

## `StateManager.ApplyToBalance` (the front door)

```csharp
public void ApplyToBalance(int amount, Ledger.Category category, EntityReference? payee,
                           string memo = null, int count = 0, bool quiet = false)
{
    AssertIsHost();
    if (amount != 0) {
        Ledger.Record(amount, category, payee, memo, count, TimeWeather.Now);
        int balance = Balance;
        int newBalance = balance + amount;
        Log.Information("ApplyToBalance: {current} + {amount} = {result}", balance, amount, newBalance);
        Balance = newBalance;                                               // KVO write, host-only
        BalanceDidChange evt = default(BalanceDidChange);
        if (!quiet)
            Multiplayer.Broadcast(amount > 0
                ? $"Received payment of {amount:C0}. Balance is now {newBalance:C0}."
                : $"Sent payment of {amount:C0}. Balance is now {newBalance:C0}.");
        string sound = category switch {
            Ledger.Category.Passenger => "punch",
            Ledger.Category.Freight   => "stamp",
            _                          => null,
        };
        if (sound != null) {
            ScheduledAudioPlayer.HostPlaySoundNotification(sound);
            StartCoroutine(SendFireEventDelayed(evt, 1f));                  // delay 1s so audio leads
        } else {
            SendFireEvent(evt);                                             // immediate
        }
    }
}
```

(Source: `Game.State/StateManager.cs:1265-1296`.)

### Side effects in order

1. **`AssertIsHost`** throws `Exception("Not host")` on clients. Hard fail.
2. **Zero-amount no-op.** `if (amount != 0)` skips everything — no ledger entry, no balance write, no broadcast, no event. Useful for "is this a real transaction" gate but breaks `count` accumulation.
3. **`Ledger.Record`** is called BEFORE the balance mutation. If a patch on `Record` throws, `Balance` is not updated and clients see no change.
4. **`Balance` setter** writes `_game` KVO key `balance` with the `Balance != value` short-circuit (`GameStorage.cs:146-148`) — a redundant `Balance = Balance` is a true no-op (no KVO write, no broadcast).
5. **`Multiplayer.Broadcast`** if `!quiet`. The chat message reaches all players via the global broadcast channel.
6. **Audio scheduling** for Freight/Passenger only. `ScheduledAudioPlayer.HostPlaySoundNotification` sends a network event to all clients to play the named sound. The delayed-1s `BalanceDidChange` fire-event lets the audio play before the UI updates.
7. **`SendFireEvent(BalanceDidChange)`** wraps as `FireEvent(eventCode=0)` and `ApplyLocal`s it. Other clients receive the FireEvent, run `HandleFireEvent(0)` which calls `Messenger.Send(default(BalanceDidChange))` — UI reacts on every machine.

### `quiet` flag conventions

| Caller | quiet | Why |
|---|---|---|
| `IndustryContext.PayWaybill` (Freight) | `true` | Coalesced via `OpsController.AddCoalescedPaymentAnnouncement` (one batched broadcast per second instead of one per car) |
| `IndustryContext.PayLoad` (Freight) | `true` | Same coalescing |
| `Industry.RollToNextContract` penalty (Freight -) | `true` | Custom message in the contract roll log |
| `Interchange.SellAndRemove` (Equipment +) | `true` | Custom "X has been sold for Y" broadcast |
| `InterchangedIndustryLoader.ServeInterchange` (Fuel -) | `true` | Custom "Ordered X cars at Y for Z" broadcast |
| `RepairTrack.DailyPayables` (WagesRepair -) | `true` | Custom "Paid X wages for shop crew" broadcast |
| `LoanManager.HandleOffsetLoan` (Loan +/-) | `true` | Custom loan-modified broadcast |
| `PassengerStop` payment (Passenger +) | `true` | Coalesced |
| `StateManager.PayAutoEngineerWages` (WagesAI -) | `false` | Standard "Sent payment of X" broadcast |
| `LoanManager.PayInterestIfNeeded` (Loan -) | `false` | Standard "Sent payment of X" broadcast — interest is silent in chat? Yes, it just shows balance change |
| `EquipmentPurchase.HandleRequest` (Equipment -) | `false` | Standard "Sent payment of X" broadcast plus custom "X ordered a shiny new Y" message |
| `Progression.PayToStartPhase` (Progression -) | `false` | Standard broadcast plus custom phase message |
| `CompanyModeSetup.Setup` (Bank +) | `false` | "Received payment of X. Balance is now Y" — players see initial seed |
| `MoneyCommand` /money cheat (Bank +/-) | `false` | Sandbox cheat |

**Pattern:** `quiet:true` means a custom or coalesced broadcast comes from elsewhere; `quiet:false` means the generic stamped balance-change message is what the player sees.

### Patch candidates

| Method | Why patch |
|---|---|
| `StateManager.ApplyToBalance` (instance method) | Sole chokepoint. Prefix returning early on a custom rule = veto. Postfix to mirror to mod-side analytics. |
| `StateManager.SendFireEvent<T>` | Add custom event codes — but **note the hardcoded switch** (`StateManager.cs:952-981`); supports only `BalanceDidChange`, `ProgressionStateDidChange`, `RequestRejected`, `ReputationUpdated`. New events need a new branch + matching `HandleFireEvent` case. |
| `Ledger.Record` | Cleaner than ApplyToBalance if you only care about the entry, not the balance/audio/broadcast side effects. |
| `Ledger.ChangedEvent` Messenger subscriber | Lighter touch than patching: subscribe to be notified after every Record (including Clear). **Not** sent for the silent ReconcileIfNeeded path. |

### MP authority

- `ApplyToBalance` has `AssertIsHost` first line — clients calling it (e.g., from a misrouted patch) throw immediately.
- `Balance` KVO key is on `_game` (the `GameStorage`), which uses default `HostOnly` for unrecognized keys (`GameStorage.cs:598-607`). `balance` matches none of the special prefixes → HostOnly.
- Clients observe `Balance` updates via `_game` KVO observation (e.g., `GameStorage.ObserveBalance`-style), but the canonical UI path is the `BalanceDidChange` Messenger event raised by `FireEvent` propagation.
- Mods that need per-client visibility must subscribe to `Messenger.Default.Register<BalanceDidChange>` AND read `StateManager.Shared.GetBalance()` — the event payload is empty.

### Gotchas

- **`AssertIsHost` throws on client** — no graceful drop. Always gate ApplyToBalance calls with `if (!StateManager.IsHost) return;` host-side, or wrap in try/catch if you must call from indeterminate context.
- **`amount == 0` is silently dropped — including the count.** A patch logging "5 cars delivered for $0" must not rely on `ApplyToBalance(0, Freight, ..., count: 5)` — call `Ledger.Record` directly instead.
- **Rounding / int truncation.** Amount is `int`. All vanilla pay sites use `Mathf.RoundToInt`/`CeilToInt`/`FloorToInt` — no fractional cents. Mods doing fractional accumulation must round explicitly.
- **`SendFireEvent` recurses through ApplyLocal.** It builds `FireEvent(eventCode=0)` and runs the local handler immediately, then sends. So the local Messenger.Send happens synchronously inside the ApplyToBalance call on host, but for clients the BalanceDidChange arrives async after network round-trip.
- **`ScheduledAudioPlayer.HostPlaySoundNotification` is host-only** — clients hear "stamp"/"punch" via the network-broadcast scheduled audio. If host has audio muted, network event still fires.
- **The `:C0` format depends on `CurrencySymbolHelper.SetCurrencySymbol("$")`** at `StateManager.Awake:207`. Mods that switch currency must intercept `Awake` AND patch the existing entries' display formatting (the `:C0` is locale-dependent; a mod setting symbol to `€` mid-game leaves persisted ledger entries displaying as the new symbol since formatting is at-render).
- **The 1-second audio-vs-event delay applies only to Freight/Passenger.** All other categories fire `BalanceDidChange` immediately. UI elements that animate balance changes will see a 1s delay between Freight payments and the displayed balance — by design.
- **No anti-overflow.** `Balance + amount` can wrap at `int.MaxValue` (~$2.1B) or `int.MinValue`. Vanilla economies are nowhere near; mods that print money must clamp.

---

## `Ledger.Category` enum — every category and where it's used

Sources cross-checked against `git grep "Ledger.Category."`:

| Category | Enum value | Producers (sites that emit) | Display string |
|---|---|---|---|
| `Bank` | 0 | `CompanyModeSetup.Setup` opening balance; `MoneyCommand` /money cheat (Sandbox); `Ledger.ReconcileIfNeeded` Balance Correction (silent — no ApplyToBalance) | `"Bank"` |
| `Freight` | 1 | `IndustryContext.PayWaybill` (waybill completion); `IndustryContext.PayLoad` (load consumption from `IndustryUnloader.DailyReceivables`); `Industry.RollToNextContract` Tier Change Penalty | `"Freight"` |
| `Passenger` | 2 | `PassengerStop` payment (`PassengerStop.cs:1021`) | `"Passenger"` |
| `Fuel` | 3 | `InterchangedIndustryLoader.ServeInterchange` (when `[SerializeField] ledgerCategory == Fuel`) — typical for diesel-fuel and similar | `"Fuel"` |
| `Loan` | 4 | `LoanManager.PayInterestIfNeeded` (interest charge); `LoanManager.HandleOffsetLoan` (principal change) | `"Loan"` |
| `Equipment` | 5 | `EquipmentPurchase.HandleRequest` (-); `Interchange.SellAndRemove` (+, sale via "sell" tag) | `"Equipment"` |
| `WagesRepair` | 6 | `RepairTrack.DailyPayables` (shop crew wages) | `"Wages: Shop"` |
| `Progression` | 7 | `Progression.PayToStartPhase` (campaign milestone payment) | `"Milestones"` |
| `WagesAI` | 8 | `StateManager.PayAutoEngineerWages` (`OnDayDidChange` midnight) — at $5/hour of cumulative `UnbilledAutoEngineerRunDuration` | `"Wages: Engineer"` |
| `RepairSupplies` | 9 | **NONE in vanilla** — defined but unused | `"Repair Supplies"` |

(Display strings: `Game.DailyReport/DailyReportGenerator.cs:276-285`.)

### Adding a category

You can't extend the enum without recompiling `Assembly-CSharp`. **Workaround:** repurpose `Memo` + `Payee.EntityType` for sub-categorization within an existing enum, or use `RepairSupplies` (currently unused — but reserve at your own risk if vanilla ever wires it up).

`DailyReportGenerator.StringForCategory` is a `switch` expression — adding a category via Harmony requires patching that method too, plus `FinancePanelBuilder.GroupEntries`'s `KeyForEntry` and any per-category logic in `BalanceDidChange` consumers.

---

## `BalanceDidChange` Messenger event

```csharp
[StructLayout(Size = 1)]
public struct BalanceDidChange {}                    // Game.Events/BalanceDidChange.cs
```

Empty struct. Subscribers must call `StateManager.Shared.GetBalance()` themselves to read the new value.

### Producer

`StateManager.ApplyToBalance` builds the event, queues either an immediate or delayed `SendFireEvent(evt)`. `SendFireEvent` (`StateManager.cs:952`) maps the event type to an int code via hardcoded switch:

```csharp
public void SendFireEvent<TEvent>(TEvent evt)
{
    AssertIsHost();
    int eventCode = evt switch {
        BalanceDidChange         => 0,
        ProgressionStateDidChange => 1,
        RequestRejected           => 2,
        ReputationUpdated         => 3,
        _                         => throw ArgumentOutOfRangeException
    };
    ApplyLocal(new FireEvent(eventCode));
}
```

The `FireEvent` IGameMessage is then sent through the normal client-broadcast path, and every receiver runs `HandleFireEvent(eventCode)` which `Messenger.Send`s the corresponding event locally.

**This is the only reason the host's own UI sees `BalanceDidChange` — the host's `LocalGameClient` echoes the FireEvent back through Handle which calls HandleFireEvent which sends the Messenger event.** A patch that bypasses ApplyLocal here would break host UI.

### Consumers (vanilla)

| Subscriber | File | Purpose |
|---|---|---|
| `BalanceDisplay` | `UI/BalanceDisplay.cs:26` | Animates the on-screen number |
| `FinancePanelBuilder` | `UI.CompanyWindow/FinancePanelBuilder.cs:24` | `RebuildOnEvent<BalanceDidChange>()` → re-renders balance + ledger scroll |
| `GoalsPanelBuilder` | `UI.CompanyWindow/GoalsPanelBuilder.cs:134` | Same — rebuilds when balance changes (e.g., to enable Pay buttons) |

### Patch candidates

| Method | Why patch |
|---|---|
| `StateManager.SendFireEvent<TEvent>` | Add new event codes (must update `HandleFireEvent` too) |
| `Messenger.Default.Register<BalanceDidChange>` (mod) | Subscribe rather than patch — clean, runs after vanilla UI |
| `StateManager.HandleFireEvent` | Catch the dispatch on every machine, not just the host |

### Gotchas

- **No payload.** The event tells you "something changed" — read `GetBalance()` to find what.
- **Fires for amount == 0?** No — `ApplyToBalance` short-circuits zero amounts before the SendFireEvent call.
- **Coalesced on host but not on clients.** Each ApplyToBalance call generates one FireEvent broadcast to clients. Host-side, multiple subscribers see the Messenger event; the network sends one FireEvent per call. A burst of 100 `PayWaybill` calls = 100 wire messages even though host UI only repaints on the next frame.
- **Delayed by 1s for Freight/Passenger.** UI animations for these categories lag behind the actual balance write by 1 second so audio leads.

---

## `LedgerRequest` / `LedgerResponse` (the request/response pair)

One of only two true req/resp message pairs in vanilla. (The other is `AutoEngineerWaypointRouteRequest`/`Response`.)

```csharp
[MinimumAccessLevel(AccessLevel.Passenger)]
[MessagePackObject(false)]
public struct LedgerRequest(float start, float end) : IGameMessage {
    [Key(0)] public float Start = start;
    [Key(1)] public float End   = end;
}

[HostOnlyAuthorizationRule]
[MessagePackObject(false)]
public struct LedgerResponse(List<SerializableLedgerEntry> entries,
                             int startBalance, int endBalance) : IGameMessage {
    [Key(0)] public List<SerializableLedgerEntry> Entries;
    [Key(1)] public int StartBalance;
    [Key(2)] public int EndBalance;
}
```

(Sources: `Game.Messages/LedgerRequest.cs`, `LedgerResponse.cs`.)

### Wire roles

- `LedgerRequest`: **Passenger-level auth** — anyone in the lobby can request the ledger. Sent by client.
- `LedgerResponse`: `[HostOnlyAuthorizationRule]` — only the host can emit. Targeted reply via `StateManager.SendTo(sender, ...)` rather than broadcast.

### Spine

```
Client UI (FinancePanelBuilder.RequestLedgerEntries):
   if not host AND _lastRequestedEntries < unscaledTime - 5f:
       StateManager.ApplyLocal(new LedgerRequest((float)start.TotalSeconds,
                                                  (float)end.TotalSeconds));
       _lastRequestedEntries = unscaledTime;

   // returns local Ledger.EntriesBetween IMMEDIATELY
   // (showing the previously-cached data; the new response will trigger a rebuild)

Network → Host StateManager.Handle:
   if (gameMessage is LedgerRequest && IsHost):
       int startBalance, endBalance;
       IReadOnlyList<Entry> source = Ledger.EntriesBetween(
           new GameDateTime(req.Start), new GameDateTime(req.End),
           out startBalance, out endBalance);
       SendTo(sender, new LedgerResponse(
           source.Select(e => new SerializableLedgerEntry(e)).ToList(),
           startBalance, endBalance));

Network → Client StateManager.Handle:
   if (gameMessage is LedgerResponse && !IsHost):
       Ledger.Load(response.Entries, response.StartBalance);    // wipes _entries, replaces
       Messenger.Default.Send(default(LedgerRequestResponseReceived));
```

(Sources: `StateManager.cs:598-648`, `UI.CompanyWindow/FinancePanelBuilder.cs:153-164`.)

### Critical: `Ledger.Load` wipes the local ledger on every response

```csharp
public void Load(List<SerializableLedgerEntry> entries, int startingBalance = 0)
{
    if (entries == null) entries = new List<SerializableLedgerEntry>();
    _entries.Clear();                                            // ← drops everything
    _startingBalance = startingBalance;
    _entries.AddRange(entries.Select(e => new Entry(e)));
    Log.Information("Loaded {count} ledger entries.", _entries.Count);
}
```

**A `LedgerRequest` for a 2-day window followed by a `LedgerResponse` REPLACES the client's full `_entries` list** with only that 2-day window. If a mod or different UI requests a wider window later, it'll get the wider list. The client never holds the full ledger — only the most recently requested slice.

The `_startingBalance` for the slice is `startBalance` (the running total *before* the window) — so `EntriesBetween` calls on client-side data will correctly compute balances *within* the loaded slice. Outside the slice → garbage.

### `Game.Events.LedgerRequestResponseReceived`

```csharp
[StructLayout(Size = 1)]
public struct LedgerRequestResponseReceived {}
```

Empty marker. Triggered after `Ledger.Load`. `FinancePanelBuilder.BuildLedgerScrollContent` subscribes via `RebuildOnEvent<LedgerRequestResponseReceived>()` and re-renders.

### Caching / throttle

`FinancePanelBuilder._lastRequestedEntries` is a static float — global 5-second throttle across the entire UI. Multiple panel rebuilds within 5s reuse the locally-cached `_entries`. **Mods displaying ledger data from clients should respect this throttle or DOS the host.**

### Patch candidates

| Method | Why patch |
|---|---|
| `StateManager.Handle` LedgerRequest branch (`StateManager.cs:643-648`) | Customize the entries returned (e.g., filter sensitive payees per requesting player). |
| `Ledger.Load` | Detect mod-initiated wipes; preserve mod-side parallel state. |
| `FinancePanelBuilder.RequestLedgerEntries` | Change throttle policy or window size. |

### MP authority

- `LedgerRequest`: `[MinimumAccessLevel(AccessLevel.Passenger)]` — minimum auth in the game. **Even passive observers can pull the ledger.** Mod servers wanting to hide finance from low-level players must patch the handler.
- `LedgerResponse`: `[HostOnlyAuthorizationRule]` — host-only emit. Trying to inject `LedgerResponse` from a client patch fails the auth check.
- The host runs `Ledger.EntriesBetween` synchronously in `Handle` — large windows on a long-running game are an O(entries) scan. Vanilla uses 2-day windows.

### Gotchas

- **The throttle is global static, not per-window.** Two open Finance panels (somehow) share the throttle. The sandboxed multi-window mod use case is unsupported.
- **Host always sees its own `Ledger` directly** — no LedgerRequest/Response round-trip. `FinancePanelBuilder.RequestLedgerEntries` early-returns when `StateManager.IsHost` (sends nothing, reads `shared.Ledger.EntriesBetween` directly). UI on host always sees fresh data; clients see ≤5s stale data.
- **`LedgerResponse` does not carry the request's start/end** — the client trusts that `Entries` and `(startBalance, endBalance)` correspond to the window it asked for. A mod that intercepts and rewrites `LedgerResponse` to send a wider window confuses `EntriesBetween` math.
- **`LedgerRequest` Date fields are floats** — `GameDateTime.TotalSeconds` is a `double` but the message is a `float`. Precision loss on long games (>~16M game seconds = ~190 days at 1× scale). Vanilla typically uses 2-day windows so it doesn't matter.

---

## `Game.State.LoanManager`

```csharp
public class LoanManager : MonoBehaviour {
    private const float InterestPercent             = 0.1f;          // 10% per period
    private const int   InterestPaymentIntervalDays = 5;             // every 5 GAME days

    public  int LoanAmount { get; }                  // delegates to GameStorage
    private GameDateTime? NextInterestDate { get; }  // GameStorage KVO "loanNextInterestDate"
    private int LoanNextInterestOffset { get; }      // GameStorage KVO "loanNextInterestOffset"

    public static bool CanRequestLoanChange =>
        StateManager.CheckAuthorizedToSendMessage(new RequestLoanDelta(0));
}
```

(Source: `Game.State/LoanManager.cs:12-243`.)

### Lifecycle

`LoanManager` is added by `StateManager.OnPropertiesDidRestore` (`StateManager.cs:312-316`) **only if `GameMode != GameMode.Sandbox`**:

```csharp
if (GameMode != GameMode.Sandbox) {
    _loanManager = base.gameObject.AddComponent<LoanManager>();
    _loanManager.Configure(_storage);
}
```

In Sandbox, `StateManager.LoanManager` is `null` — UI and mods must null-check. Destroyed in `OnMapWillUnload` (`StateManager.cs:339-343`).

### Interest tick

```csharp
private IEnumerator UpdateCoroutine() {
    StateManager.AssertIsHost();
    while (base.enabled) {
        PayInterestIfNeeded();
        yield return new WaitForSeconds(5f);                          // 5 REAL seconds
    }
}

private void PayInterestIfNeeded() {
    if (NextInterestDate is GameDateTime due && due <= TimeWeather.Now) {
        int loanAmount = LoanAmount;
        int interest = CalculateInterestPayment(loanAmount);
        StateManager.Shared.ApplyToBalance(-interest, Ledger.Category.Loan, null);
        NextInterestDate = due.AddingDays(5f);
        LoanNextInterestOffset = 0;
    }
}
```

**5-real-second poll, fires when game time crosses `NextInterestDate`.** With default `TimeMultiplier = 2f` (so 1 real second = 2 game seconds), the 5 real-second poll evaluates roughly every 10 game seconds.

`CalculateInterestPayment(loanAmount) = round(loanAmount * 0.1) + LoanNextInterestOffset`. The offset is a fudge factor reset to 0 after each interest payment; used to balance partial-period loan modifications (see `CalculateNextInterestOffset` below).

### Loan delta — origination, paydown

```csharp
public void HandleOffsetLoanRequest(int delta, IPlayer sender) {
    if (!StateManager.IsHost) return;
    try {
        HandleOffsetLoan(delta);
    } catch (DisplayableException ex) {
        Multiplayer.SendError(sender, ex.DisplayMessage);
    } catch (Exception ex) {
        Multiplayer.SendError(sender, "Unable to adjust loan.");
    }
}

private void HandleOffsetLoan(int delta) {
    AssertIsHost();
    if (delta == 0) return;
    int balance = StateManager.Shared.GetBalance();
    int loanAmount = LoanAmount;

    if (delta < 0) {                                                  // pay down
        delta = -Min(-delta, loanAmount);                              // can't pay more than owed
        if (balance < -delta)
            throw new DisplayableException(
                $"Insufficient balance to pay down loan {-delta:C0}; balance is {balance:C0}.");
    } else {                                                           // borrow
        int approved = ApprovedLoanAmount();                           // = ValueOfAssets()
        if (approved - loanAmount < delta)
            throw new DisplayableException(
                $"Insufficient capital to finance loan. Loan limit is {approved:C0}, current is {loanAmount}.");
    }

    var nextInterestDate = NextInterestDate;
    LoanNextInterestOffset = CalculateNextInterestOffset(loanAmount, loanAmount + delta, now, nextInterestDate);
    LoanAmount += delta;
    int newAmount = LoanAmount;

    NextInterestDate = nextInterestDate
                       ?? (newAmount > 0 ? now.StartOfDay.AddingDays(5f) : null);

    StateManager.Shared.ApplyToBalance(delta, Ledger.Category.Loan, null, null, 0, quiet: true);
    Multiplayer.Broadcast($"Loan increased/paid down ... Interest payment of {N:C0} due in {interval}.");
}
```

(Source: `LoanManager.cs:152-188`.)

### Approved loan amount

```csharp
public  int ApprovedLoanAmount() => ValueOfAssets();
private int ValueOfAssets() =>
    TrainController.Shared.Cars
        .Where(EquipmentPurchase.CarCanBeSold)                         // owned, not Tender
        .Sum(EquipmentPurchase.TradeInValueForCar);                    // 25..75% of BasePrice
```

Approved = sum of trade-in values across all player-owned, non-tender cars. Each car's trade-in is `lerp(0.25, 0.75, condition * RepairCap) * BasePrice`.

So a fleet of damaged cars supports a smaller loan than a fresh fleet — and the loan limit shrinks as cars degrade. **There is no minimum loan capacity.** A new game with one $5000 car at full condition supports a $3750 loan.

### Interest offset math (fairness)

```csharp
public static int CalculateNextInterestOffset(int existing, int newAmount, GameDateTime now,
                                              GameDateTime? maybeExistingNextInterestDate, int existingOffset)
{
    if (!maybeExistingNextInterestDate.HasValue) return 0;
    GameDateTime due = maybeExistingNextInterestDate.Value;
    if (due < now) return 0;                                          // overdue → no offset
    int daysUntilDue = floor(due.DaysSince(now.StartOfDay));
    int daysSinceLastPayment = 5 - daysUntilDue;
    int interestEarnedSoFarOnExisting = round(daysSinceLastPayment * 0.02f * existing);   // 2%/day proration
    int interestExpectedSoFarOnNew    = round(daysSinceLastPayment * 0.02f * newAmount);
    return existingOffset + (interestEarnedSoFarOnExisting - interestExpectedSoFarOnNew);
}
```

**Effect:** If you borrow more mid-period, the next interest payment is *increased* by the prorated interest the new amount should have accrued so far. If you pay down mid-period, the offset *reduces* the next payment by the prorated savings. This makes the 10%-per-5-days math fair regardless of when in the period you adjust.

`0.02f * 5 = 0.1f` — the 2% per day adds up to the 10% per 5 days. The prorated math is exact for full days; partial-day adjustments lose the partial-day fraction.

### Payoff broadcast

When `LoanAmount` reaches zero after paydown:
- "Loan paid down by {amount} to $0. Congratulations!"
- `NextInterestDate = null` (interest tick stops doing anything).

When loan goes back up from zero:
- New `NextInterestDate = now.StartOfDay.AddingDays(5f)`.

### `RequestLoanDelta` message

```csharp
[MinimumAccessLevel(AccessLevel.Officer)]
public struct RequestLoanDelta(int delta) : IGameMessage {
    [Key(0)] public int Delta { get; set; }
}
```

(Source: `Game.Messages/RequestLoanDelta.cs`.)

Officer-level — most lobby players can't take out loans on someone else's railroad. `LoanManager.RequestLoanDelta(int)` (`LoanManager.cs:239`) is the helper that wraps `StateManager.ApplyLocal(new RequestLoanDelta(delta))`. Used by `FinancePanelBuilder` Loan/Pay buttons.

### Patch candidates

| Method | Why patch |
|---|---|
| `LoanManager.HandleOffsetLoan` | Single chokepoint for loan changes — modify limits, add fees. |
| `LoanManager.PayInterestIfNeeded` | Replace interest tick (e.g., variable rates, term loans). |
| `LoanManager.CalculateInterestPayment` | Replace fixed 10% with curve / variable rate. |
| `LoanManager.CalculateNextInterestOffset` | Replace fairness proration. |
| `LoanManager.ApprovedLoanAmount` / `ValueOfAssets` | Custom collateral models (e.g., include track length, industry contracts). |
| `EquipmentPurchase.TradeInValueForCar` | Indirectly affects loan limit — see Equipment section. |
| `LoanManager.UpdateCoroutine` | Change tick cadence (currently 5 real seconds). |

### MP authority

- `LoanManager` MonoBehaviour is added on `OnPropertiesDidRestore` for **all** non-Sandbox players (host AND clients) — `StateManager.cs:312-316` runs on every machine. **But** the `_updateCoroutine` only starts on the host (`LoanManager.cs:84`: `if (StateManager.IsHost && _updateCoroutine == null && _gameStorage != null)`).
- KVO keys (`loanAmount`, `loanNextInterestDate`, `loanNextInterestOffset`) live on `_game` → HostOnly per `GameStorage.AuthorizationRequirementForPropertyWrite` (the default `HostOnly` fallback). Clients see updates via KVO observation.
- `CanRequestLoanChange` is exposed for UI gating — short-circuits the auth check for the Loan/Pay buttons.

### Gotchas

- **`LoanManager == null` in Sandbox.** `FinancePanelBuilder.BuildLoanSection` early-returns if null (`FinancePanelBuilder.cs:38-41`). Mods adding loan-aware UI must null-check.
- **5-real-second polling is a hard tick.** With `TimeMultiplier > 5`, an interest payment due at game-time T may fire several seconds late in real time. Pausing the game (TimeMultiplier near 0) means interest never advances — but `Now` doesn't advance either, so it's consistent.
- **Interest is paid even if you can't afford it.** `PayInterestIfNeeded` calls `ApplyToBalance(-interest, Loan, null)` unconditionally — your balance can go negative. There's NO "default" or "auto-foreclosure" mechanic in vanilla. `CanAfford` is checked only by *callers* like `EquipmentPurchase` and `RepairTrack` wages, not by interest.
- **`HandleOffsetLoan` can throw `DisplayableException`** which is sent to the requesting client via `Multiplayer.SendError`. Other exceptions get a generic "Unable to adjust loan." Mod patches should preserve the `DisplayableException` semantics — vanilla's UX depends on the user-facing message.
- **The interest-offset math uses `Mathf.RoundToInt`** — small loans + frequent toggling can accumulate ±$1 rounding drift between expected and actual interest. Not a meaningful bug but a clue when reconciling logs.
- **`NextInterestDate.AddingDays(5f)`** uses float arithmetic via `GameDateTime`. Effectively integer days but the API takes float — fractional days do work but aren't used.
- **`LoanNextInterestOffset` is host-only KVO and can be NEGATIVE.** `CalculateInterestPayment` clamps to 0 and logs an error if the sum is negative — protects against the edge case where rapid repayments produce a refund that exceeds the next payment.
- **`ApprovedLoanAmount() < LoanAmount` is possible.** If you take a max loan then damage your fleet, the loan stays — `HandleOffsetLoan(positive delta)` checks `approved - loanAmount < delta`; if approved drops below loanAmount, you can only pay down (delta < 0), not borrow more. Vanilla has no auto-call mechanism.
- **`StartIfNeeded` is called in both `Configure` and `OnEnable`** — disabling the LoanManager component pauses interest ticking; re-enabling resumes (the coroutine restarts).
- **Default values for un-set KVO**: `LoanAmount` defaults to 0, `NextInterestDate` defaults to `null`, `LoanNextInterestOffset` defaults to 0 — fresh saves work without explicit initialization.

---

## `Game.State.EquipmentPurchase` (purchase + sell + trade-in)

```csharp
public static class EquipmentPurchase {
    public static void HandleRequest(IPlayer sender, RequestPurchaseEquipment request);   // 21
    public static int  PurchasePriceForCarPrototype(CarDefinition prototype, out int discount);   // 71
    public static int  TradeInValueForCar(Car car);                                         // 78
    public static bool CarCanBeSold(Car car);                                               // 84
}
```

(Source: `Game.State/EquipmentPurchase.cs:19-118`.)

### `RequestPurchaseEquipment`

```csharp
[MinimumAccessLevel(AccessLevel.Officer)]
public struct RequestPurchaseEquipment(List<string> prototypeIds) : IGameMessage {
    [Key(0)] public List<string> PrototypeIds;
}
```

(Source: `Game.Messages/RequestPurchaseEquipment.cs`.)

Officer-level. Sent by `EquipmentWindow.Purchase` (`UI.Equipment/EquipmentWindow.cs:309`):

```csharp
private void Purchase(CatalogEntry selectedEntry, int quantity) {
    StateManager.ApplyLocal(new RequestPurchaseEquipment(
        Enumerable.Repeat(selectedEntry.CarDefinitionInfo.Identifier, quantity).ToList()));
    _window.CloseWindow();
}
```

A list of N copies of the same prototype identifier; if buying 5 boxcars, the list has 5 copies.

### `HandleRequest` flow

```csharp
public static void HandleRequest(IPlayer sender, RequestPurchaseEquipment request)
{
    var shared = TrainController.Shared;
    var prefabStore = shared.PrefabStore;
    var stateManager = StateManager.Shared;

    // 1. Build descriptors (also fans out tenders if locomotive has TenderIdentifier)
    List<CarDescriptor> list = CarDescriptorsFromRequest(request, prefabStore);

    // 2. Ask interchange placement for tracks
    Interchange chosenInterchange;
    var tracksAndCars = shared.FindTracksForCars(list, out chosenInterchange);
    if (tracksAndCars == null) {
        Multiplayer.Broadcast("No tracks available for purchase.");
        Log.Error("Unable to find space for purchase: {descriptors}", list);
        return;                                             // ← NO REFUND CHECK; nothing was charged
    }

    // 3. Check funds (Sandbox always returns true)
    int total = list.Sum(d => PurchasePriceForCarPrototype(d.DefinitionInfo.Definition, out _));
    if (!stateManager.CanAfford(total)) {
        Multiplayer.Broadcast($"Not enough funds for purchase. Balance {stateManager.Balance:C0} is less than {total:C0}.");
        return;                                             // ← Balance not yet touched, safe abort
    }

    // 4. Place trains (one PlaceTrain per assigned track)
    foreach (var (loc, descriptors) in tracksAndCars)
        shared.PlaceTrain(loc, descriptors, null, 0.25f);   // initialFuelWaterPercent = 25%

    // 5. Charge
    Hyperlink hyperlink  = Hyperlink.To(sender);
    string    name       = list[0].DefinitionInfo.Metadata.Name;
    Hyperlink hyperlink2 = Hyperlink.To(chosenInterchange.Industry);
    Multiplayer.Broadcast($"{hyperlink} ordered a shiny new {name} delivered to {hyperlink2}.");
    StateManager.Shared.ApplyToBalance(-total, Ledger.Category.Equipment, null, name);
}
```

### Key points

- **Tender auto-add**: `CarDescriptorsFromRequest` (`EquipmentPurchase.cs:93`) calls `Definition.TryGetTenderIdentifier(out var tenderIdentifier)` for each prototype. If the prototype is a steam loco with a tender, the tender's `CarDescriptor` is appended. The `total` cost includes the tender — but **`DefinitionChecker` enforces `Tender.BasePrice == 0`** (`DefinitionChecker.cs:65-68`), so tenders are free. Effective cost = locomotive price.
- **`owned: true` in properties dictionary** — every purchased car is marked player-owned via the `"owned"` KVO key in CarDescriptor.Properties. `Car.IsOwnedByPlayer` reads this key (`Car.cs:901`).
- **Reporting mark = `StateManager.RailroadMark`** — purchased cars use the player's railroad mark (`EquipmentPurchase.cs:95`). Foreign-road cars from interchanges use `OpsController.ForeignRoads` (random non-player mark).
- **`forceSequential = true`** for owned cars — `RoadNumberAllocator.AllocateRoadNumber` increments sequentially based on `Definition.BaseRoadNumber` (see `TrainController.AllocateRoadNumber`).
- **No refund on placement failure.** Steps 1-2 build descriptors and find tracks. If FindTracksForCars returns null, the function returns *before* charging — but any side effects from prefab-loading the descriptors have already happened. The `PlaceTrain` itself can throw mid-loop (caught at line 51, broadcast as error) — in which case some cars may be placed AND the player has been charged for ALL of them. **Partial-purchase risk if PlaceTrain throws.**
- **`initialFuelWaterPercent = 0.25f`** for purchases — locomotives arrive at 25% fuel/water, not full. `TrainController.PlaceTrain` applies this via `ApplyInitialSlotContents`.
- **`CanAfford` checked AFTER FindTracksForCars succeeds.** A failed track search returns silently; a failed afford check broadcasts. Players who can't afford are told; players whose interchange is full get a vague "No tracks available."

### `PurchasePriceForCarPrototype`

```csharp
public static int PurchasePriceForCarPrototype(CarDefinition prototype, out int discount)
{
    int basePrice = prototype.BasePrice;
    discount = Mathf.FloorToInt(basePrice * ReputationTracker.Shared.EquipmentDiscount());
    return basePrice - discount;
}
```

`ReputationTracker.EquipmentDiscount()` (`ReputationTracker.cs:545`) — tiered:

| Reputation | Discount |
|---|---|
| > 0.99 | 10% |
| > 0.95 | 7% |
| > 0.9 | 5% |
| > 0.85 | 3% |
| > 0.8 | 2% |
| > 0.7 | 1% |
| ≤ 0.7 | 0% |

So a `BasePrice = 50000` car at perfect rep costs $45000.

### `TradeInValueForCar`

```csharp
public static int TradeInValueForCar(Car car) {
    float pct = Mathf.Lerp(0.25f, 0.75f, car.Condition * car.RepairCap);
    return Mathf.RoundToInt(car.Definition.BasePrice * pct);
}
```

- `Condition * RepairCap` — a car at condition 1.0 with `RepairCap = 0.5` (5+ overhauls overdue) has effective 0.5 → trade-in 50% of `BasePrice`.
- A condition-0 car or `RepairCap = 0` car still gets 25% — trade-in floor.
- Used in two places: loan collateral (`LoanManager.ValueOfAssets`) and `Interchange.SellAndRemove`.

### `CarCanBeSold`

```csharp
public static bool CarCanBeSold(Car car) =>
    car.IsOwnedByPlayer && car.Archetype != CarArchetype.Tender;
```

- Must be player-owned (the `owned` KVO key).
- Tenders are exempt — they have `BasePrice = 0` per `DefinitionChecker` rule, and they're inseparable from their engine via `Car.RequiresConnectionToEnd(End.F)`. Selling a tender independently makes no sense.

### Selling — the `ops.sell-dest` mechanic

There is **no `Sell` request message**. Selling flows entirely through the existing waybill system:

1. **UI: `BuilderExtensions.AddSellDestination`** (`UI.CompanyWindow/BuilderExtensions.cs:274-332`) — dropdown picker on the inspector's Equipment panel. Auth-gated by `StateManager.CheckAuthorizedToChangeProperty(car.id, "ops.waybill")`. On selection:
   ```csharp
   car.SetWaybill(new Waybill(TimeWeather.Now, null, item, 0, completed: false, "sell", 0));
   ```
   Writes a `Waybill` with `Tag = "sell"` and `PaymentOnArrival = 0` to the car's `ops.waybill` KVO key.
2. **The KVO key `ops.sell-dest`** (`Car.KeyOpsSellDestination = "ops.sell-dest"`, `Car.cs:461`) is in `Car.OfficerPrefixes` (`Car.cs:473`) — so writes to `ops.sell-dest` require Officer auth.
3. **However**, the actual UI in `BuilderExtensions.AddSellDestination` writes `ops.waybill` (Trainmaster + train-crew), NOT `ops.sell-dest`. The `ops.sell-dest` key appears reserved/legacy — it's defined and Officer-gated but no vanilla code reads it. The "sell-dest" naming probably refers to the *concept* of a destination tagged "sell"; the on-the-wire implementation just uses a tagged Waybill.
4. **`Interchange.ServeInterchange`** processes the car (`Interchange.cs:121`):
   ```csharp
   if (waybill.Destination.Equals(this) && waybill.Tag == "sell" && item.IsOwnedByPlayer)
       SellAndRemove(item);
   ```
5. **`Interchange.SellAndRemove(opsCar)`** (`Interchange.cs:179-191`):
   ```csharp
   int sale = EquipmentPurchase.TradeInValueForCar(car);
   shared.RemoveCarSmart(car.id);
   StateManager.Shared.ApplyToBalance(sale, Ledger.Category.Equipment, null, displayName, 0, quiet: true);
   Multiplayer.Broadcast($"{car.DisplayName} has been sold for {sale:C0}.");
   ```

### Patch candidates (Equipment / Selling)

| Method | Why patch |
|---|---|
| `EquipmentPurchase.HandleRequest` | Modify the entire purchase flow — refund logic, multi-interchange selection, custom validations. |
| `EquipmentPurchase.PurchasePriceForCarPrototype` | Custom pricing curves (e.g., quantity discounts). |
| `EquipmentPurchase.TradeInValueForCar` | Custom trade-in formula — affects both selling AND loan collateral. |
| `EquipmentPurchase.CarCanBeSold` | Allow tender sales, restrict by mod-defined tags. |
| `Interchange.SellAndRemove` (private) | Side effects on sale (analytics, notifications). |
| `BuilderExtensions.AddSellDestination` | UI customization. |
| `ReputationTracker.EquipmentDiscount` | Reputation-driven pricing curve. |
| `TrainController.PlaceTrain` (purchase path) | Override placement strategy. |
| `TrainPlacementHelper.FindTracksForCars` | Override track-selection algorithm. |

### MP authority

- `RequestPurchaseEquipment`: `[MinimumAccessLevel(AccessLevel.Officer)]`. Officer = third-from-top access tier.
- `EquipmentPurchase.HandleRequest` is host-only (called from `StateManager.Handle` only when `IsHost`).
- `ops.waybill` writes for "sell" go through `Car.AuthorizationRequirementForPropertyWrite` — which is Trainmaster + train-crew check (default for non-prefixed keys). So a Trainmaster-level player can sell cars they're crewing.
- `ops.sell-dest` would be Officer if anything wrote it — but nothing does in vanilla.
- `Interchange.SellAndRemove` runs host-side from inside `Interchange.ServeInterchange` (host-only ops tick).

### Gotchas

- **Tender's `BasePrice == 0` is enforced by `DefinitionChecker`, not by purchase code.** A mod ship­ping a tender with `BasePrice > 0` will charge the player for the tender on purchase. The DefinitionChecker WARNs but doesn't reject.
- **`PurchasePriceForCarPrototype` reads `ReputationTracker.Shared.EquipmentDiscount()` per item, not once per request.** Same discount for all items in a single purchase, but redundant lookups.
- **The "shiny new {name}" broadcast uses `list[0].DefinitionInfo.Metadata.Name`** — only the FIRST car's name. A purchase of "5 boxcars and a locomotive" might say "ordered a shiny new Boxcar" if boxcar is first. Confusing for mixed orders.
- **`PlaceTrain` failure mid-loop charges for ALL cars but only places SOME.** Catch block (`EquipmentPurchase.cs:51-57`) logs and re-throws — but the throw escapes via the outer try/catch at line 64, which logs again. Net effect: balance is not deducted (the throw happens before `ApplyToBalance` at line 62). Actually safe — the broadcast fires, `ApplyToBalance` doesn't.
- **`FindTracksForCars` walks `EnabledInterchanges` in `OpsController._enabledInterchanges` order** — first-fit, not optimal. The `chosenInterchange` is the first one with capacity. Players cannot pick a destination interchange.
- **`CompanyModeSetup.Setup` shares the `owned: true` pattern via `StateManager.DescriptorsForIdentifiers`** (`StateManager.cs:428-450`) — the same code path that `EquipmentPurchase` uses. Initial fleet is "purchased" in the same sense. The `oiled` field gets overwritten per placement.
- **`EquipmentWindow.ShouldShow`** filter: `info.Definition.VisibleInPlacer && info.Definition.BasePrice > 0`. So `BasePrice == 0` cars are invisible in the purchase UI even if `VisibleInPlacer = true`. Tenders are excluded by both checks.
- **The "sell" tag is hardcoded as a string literal** in `BuilderExtensions.AddSellDestination`, `Interchange.ServeInterchange`, AND `CarExtensions.TagSell = "sell"` — three sources of truth. Use the constant when patching.
- **`ops.sell-dest` is dead code in vanilla.** Defined as `Car.KeyOpsSellDestination`, included in `Car.OfficerPrefixes`, but nothing reads or writes it. The implementation uses the regular `ops.waybill` key with `Tag = "sell"`. A mod could repurpose `ops.sell-dest` for legacy compatibility or to sidestep the train-crew auth check on `ops.waybill`.

---

## BasePrice mechanics on `CarDefinition`

The single field `CarDefinition.BasePrice` (int, dollars) drives:

1. **Purchase price** — `EquipmentPurchase.PurchasePriceForCarPrototype` returns `BasePrice * (1 - reputationDiscount)`.
2. **Trade-in/sell value** — `EquipmentPurchase.TradeInValueForCar` returns `BasePrice * lerp(0.25, 0.75, condition*RepairCap)`.
3. **Loan collateral** — `LoanManager.ValueOfAssets` sums trade-in across player-owned cars.
4. **RepairTrack throughput** — `RepairTrack.NormalizedCostValue(car) = max(0, (BasePrice - 1000) / 34000)` (`RepairTrack.cs:389-392`). Used as the input to `Config.repairSpeedForNormalizedCost.Evaluate(...)` — **expensive cars repair more slowly** per the curve. See [Wear & Durability › RepairTrack](wear-durability.md#modelopsrepairtrack-industry-side-repair).
5. **Equipment window sort order** — `EquipmentWindow.RebuildModel` orders by `Archetype.PlacerOrder()` then by `BasePrice` (`EquipmentWindow.cs:315`).
6. **Equipment window visibility** — `EquipmentWindow.ShouldShow` requires `BasePrice > 0` (`EquipmentWindow.cs:331`).

### `DefinitionChecker` rules

- `Freight` car with `BasePrice > 0` AND CarType not in `["FB", "FL", "HM", "HT", "TM", "XM"]` → Warning (`DefinitionChecker.cs:58-61`). Only those 6 freight car types are "priced" — passenger cars, MOW, etc. should be free.
- `Tender.BasePrice > 0` → Warning ("Tender price should be zero").
- `SteamLocomotive.BasePrice == 0` → Warning ("Base price of locomotive is zero.").

These are *warnings* — they don't block the asset.

### Patch candidates

| Method | Why patch |
|---|---|
| `CarDefinition.BasePrice` (field) | Mutate at runtime to apply mod-side pricing. **Caution:** this is the SerializedField on a ScriptableObject; mutation persists to the asset in editor. At runtime it's fine. |
| `EquipmentPurchase.PurchasePriceForCarPrototype` | Apply quantity discounts, surge pricing, mod-tax. |
| `EquipmentPurchase.TradeInValueForCar` | Custom depreciation. |
| `RepairTrack.NormalizedCostValue` (private static) | Decouple repair speed from base price. |
| `DefinitionChecker.CheckCar` / `CheckSteamLocomotive` | Remove or extend the priced-car-type list. |

### Gotchas

- **`BasePrice` is `int`** — fractional dollars not representable. Reputation discount uses `Mathf.FloorToInt` so a $99 BasePrice with 1% discount discounts $0, paying $99.
- **`(BasePrice - 1000) / 34000` in `NormalizedCostValue`** clamps to ≥ 0. So cars with `BasePrice < 1000` all have `NormalizedCost = 0` — same repair speed.
- **A `BasePrice = 0` freight car** can't be purchased (UI hides it) and trades in for `0.25 * 0 = 0` and supplies `0` loan collateral. Effectively unsellable, free, valueless.

---

## Daily payment / wages tick — TimeWeather interaction

See [Time & Weather](time-weather.md) for `TimeWeather`, `GameDateTime`, `TimeDayDidChange`, and `WaitTime`.

Three midnight tickers fire on `TimeDayDidChange` (host-only):

### 1. `OpsController.DayDidChange` (`OpsController.cs:163`)

```csharp
GameDateTime now = TimeWeather.Now.WithHours(0f);
foreach (Industry i in AllIndustries) i.DailyReceivables(now);   // pays e.g. IndustryUnloader.payPerQuantity batches
foreach (Industry i in AllIndustries) i.RollToNextContract();    // tier change penalty
foreach (Industry i in AllIndustries) i.DailyPayables(now);      // shop wages
```

- `DailyReceivables` → `IndustryComponent.DailyReceivables` virtual → `IndustryUnloader.DailyReceivables` calls `ctx.PayLoad(load, total)` → `IndustryContext.PayLoad` calls `_industry.ApplyToBalance(num, Freight, ...)` (`IndustryContext.cs:362`). Payment is in *Freight* category, batched daily, broadcast as "Payment from {industry} for delivery of {load} ({units}): {amount:C0}".
- `RollToNextContract` (`Industry.cs:429`) — applies `PenaltyForChange(newTier, daysAtCurrent, ...)`. Penalty in Freight category, quiet broadcast, custom message.
- `DailyPayables` → `RepairTrack.DailyPayables` (`RepairTrack.cs:89`) — pays `PayDue` rounded up; if `!CanAfford`, sets `PaidCurrent = false` (shop closes for the day).

### 2. `StateManager.OnDayDidChange` → `PayAutoEngineerWages` (`StateManager.cs:1450`)

```csharp
private void PayAutoEngineerWages() {
    float unbilled = _storage.UnbilledAutoEngineerRunDuration;
    int hours = Mathf.FloorToInt(unbilled / 3600f * 5f);                   // $5 per game-hour
    float quarterHours = hours / 5f;
    float remainder = unbilled - quarterHours * 3600f;
    if (hours > 0) {
        ApplyToBalance(-hours, Ledger.Category.WagesAI, null);             // standard broadcast
        Multiplayer.Broadcast($"Paid {hours:C0} for {quarterHours:F1} hours of engineer services.");
        _storage.UnbilledAutoEngineerRunDuration = remainder;              // keep partial hours
    }
}
```

**Wait — `hours` is the dollar amount, not the hour count.** `unbilled / 3600f * 5f` = (game-seconds / 3600) * 5 = game-hours * 5 = dollars. The variable name is misleading. `quarterHours = hours / 5f` recovers `game-hours`. Charge: $5 per game-hour of cumulative AutoEngineer running time.

### 3. `ReputationTracker.TickCoroutine` (`ReputationTracker.cs:156`)

Not strictly midnight-driven — uses `TimeWeather.WaitForNextDay()` async. Updates `Reputation` once per game-day. **Reputation feeds back into**:
- `EquipmentDiscount` → purchase price.
- `RepairBonus` → `RepairTrack.cs` repair throughput.
- `PhaseDiscount` → `Progression.PayToStartPhase` pricing.
- `ContractMaxStartTier` → `Industry.AvailableContracts`.

So reputation mediates between operations performance and economy.

### `LoanManager.UpdateCoroutine` (5 real seconds, NOT day-driven)

`PayInterestIfNeeded` polls every 5 real seconds; fires when game-time crosses `NextInterestDate`. Interest payments land in `Loan` category, NOT batched by day.

### `PassengerStop` payment (continuous, NOT day-driven)

`PassengerStop.Loop` coroutine pays per-passenger as they board (`PassengerStop.cs:1021`). Passenger category, quiet, coalesced via `OpsController.AddCoalescedPaymentAnnouncement`.

### Patch candidates (Daily / Time)

| Method | Why patch |
|---|---|
| `OpsController.DayDidChange` | Wrap daily rollover (e.g., add custom DailyTaxes step). |
| `Industry.RollToNextContract` | Custom penalty schedule. |
| `RepairTrack.DailyPayables` | Custom wage policy. |
| `StateManager.OnDayDidChange` / `PayAutoEngineerWages` | Custom AI wages or other midnight charges. |
| `LoanManager.UpdateCoroutine` | Tick cadence — but pure-timer, time-multiplier-aware would be a separate change. |
| `Messenger.Default.Register<TimeDayDidChange>` (mod-side) | Add new midnight tickers. Order is registration-order — vanilla registrations happen during `OnEnable`. |

### Gotchas

- **Midnight is exact game time `00:00:00`** (per `TimeDayDidChange` semantics). Multiple tickers fire in registration order — `OpsController.DayDidChange` runs before `StateManager.OnDayDidChange` because OpsController registers earlier (hosted from its `OnEnable`, which runs before `StateManager`'s post-restore observers). Mod-added tickers register at `Awake` or later, after StateManager.
- **`WaitTime` (fast-forward) ticks all industries** via `Industry.TickAll(dt / TimeMultiplier)` per simulated hour, but skips midnight rollovers — daily payables only fire when actual `TimeDayDidChange` fires. WaitTime advances the clock in 1-hour `SetTimeOfDay` jumps, so midnight CROSSED during a `WaitTime` does fire `TimeDayDidChange` (one fire per crossed midnight).
- **`TimeMultiplier` near 0 freezes the economy.** `Industry.TickCoroutine` continues but `RateToValue(rate, dt)` returns 0 (`IndustryComponent.cs:128`), so no production, no wages, no payment accrual. Loan interest is unaffected (TimeMultiplier-independent timer).
- **`RecordAutoEngineerRunDuration` is called from BOTH `AutoEngineer.cs:1088` AND `AutoOiler.cs:121`** — the AutoOiler scales by `TimeMultiplier` before calling. AutoEngineer doesn't. Net effect: oiler-time accumulates faster than engineer-time at high TimeMultiplier (or maybe the engineer is meant to be in real seconds and oiler in game seconds — opaque from decompile).

---

## Repair payments (cross-link)

Full coverage in [Wear & Durability › RepairTrack](wear-durability.md#modelopsrepairtrack-industry-side-repair). Economy-side recap:

- **Source:** `RepairTrack.DailyPayables` charges `WagesRepair` (shop crew); `RepairTrack.Service` consumes `repair-parts` Load from storage (no direct ledger entry — the parts were paid for at purchase via `InterchangedIndustryLoader` in `Fuel`/`RepairSupplies` category).
- **Wage formula:** `payPerRepairUnit = 50 * payRateMultiplier / (1 + repairBonus)`. `repairBonus` comes from `ReputationTracker.RepairBonus()` — high reputation → cheaper repair.
- **`PayDue` accumulates** in `RateState` KVO; charged on `DailyPayables`. If `!CanAfford(PayDue)`, `PaidCurrent = false` → shop closes for the day, `PayDue` carries forward.
- **No refund** for incomplete repair work — wages owed regardless.

Cross-link: see [Wear › Repair payments](wear-durability.md#modelopsrepairtrack-industry-side-repair) for the per-unit math and pay-rate multiplier UI.

---

## Industry payments (cross-link)

Full coverage in [Industries & Ops › PayWaybill](industries-ops.md#waybill-completion-default) and [PayLoad](industries-ops.md#concrete-industrycomponent-types). Economy-side recap:

- **Waybill payment:** `IndustryContext.PayWaybill` (`IndustryContext.cs:334-355`):
  ```
  payment = waybill.PaymentOnArrival
          + Contract.TimelyDeliveryBonus(daysSince, payment)         // tier 2..5
          - Waybill.ConditionFineForCarCondition(car.Condition)      // 0..75% penalty
  ```
  Charged in `Freight` category, count = 1, quiet (coalesced via `OpsController.AddCoalescedPaymentAnnouncement`).

- **Load payment (`payPerQuantity`):** `IndustryContext.PayLoad` (`IndustryContext.cs:357`) — daily-batched via `IndustryUnloader.DailyReceivables`. Freight category.

- **`IndustryUnloader.DailyReceivables`** flushes `KeyUnloadedTotal` counter at midnight (`IndustryUnloader.cs:98-106`).

- **Auto-routing payment 0:** Cars with `Waybill.Tag == "autodest"` always have `PaymentOnArrival = 0`. `OpsController.PayWaybill` short-circuits when `PaymentOnArrival == 0` AND tier bonus is 0 — but the condition fine still applies (negative payment for arriving damaged on a $0 autodest waybill is **possible** mathematically, but `if (num3 != 0)` guards the `ApplyToBalance` call and condition fine on 0 payment is also typically 0). Safe in practice.

- **Tier change penalty:** `Industry.RollToNextContract` (`Industry.cs:455`) — Freight category, quiet, custom broadcast.

Cross-link: see [Industries › Payment formula](industries-ops.md#payment-formula) for the freight rate `50 + 4*sqrt(miles) + 0.25*tons`.

---

## MP authority — host owns the economy, clients see snapshots

| State | Host | Client |
|---|---|---|
| `Balance` | Read+write via `_game` KVO key `balance`; `ApplyToBalance` mutates | Read-only via KVO observe; `BalanceDidChange` Messenger event |
| `Ledger._entries` | Authoritative in-memory list | Cached slice from last `LedgerResponse`; wiped+replaced per response |
| `Ledger._startingBalance` | From save load | From `LedgerResponse.StartBalance` (slice's pre-window balance) |
| `LoanAmount`, `NextInterestDate`, `LoanNextInterestOffset` | Read+write via `_game` KVO | Read-only via KVO observe |
| `LoanManager` MonoBehaviour | Exists if non-Sandbox; `_updateCoroutine` running | Exists if non-Sandbox; `_updateCoroutine` NOT started |
| `BasePrice`, `TradeInValueForCar`, etc. | Computed on demand | Same — these are pure functions over static data + KVO |
| `RailroadMark`, `RailroadName` | Set by `ApplyNewGameSetup`; `_game` KVO | Read-only via KVO |

### What clients can do

- **Pull ledger:** `LedgerRequest` (Passenger auth) → host sends `LedgerResponse` → client `Ledger.Load` replaces local cache.
- **Request loan change:** `RequestLoanDelta` (Officer auth) → host runs `LoanManager.HandleOffsetLoan`. Failure broadcast via `Multiplayer.SendError`.
- **Request equipment purchase:** `RequestPurchaseEquipment` (Officer auth) → host runs `EquipmentPurchase.HandleRequest`. Failure broadcast via `Multiplayer.Broadcast`.
- **Set sell waybill:** Direct KVO write to `ops.waybill` with `Tag = "sell"` (Trainmaster + train-crew auth). Eventually processed by host's interchange tick.
- **Modify contract tier:** `ModifyContract` (Officer auth) → host's `Industry.ModifyContract`.
- **Set repair multiplier:** `SetRepairMultiplier` (Trainmaster auth via `RepairTrack.HandleSetMultiplier`).
- **Read balance / loan / KVO state:** Free, KVO observation.

### What clients **cannot** do

- Directly call `ApplyToBalance`. AssertIsHost throws.
- Directly write `_game["balance"]`. HostOnly KVO auth rejects; corrective PropertyChange snaps client back.
- Directly add `Ledger.Entry`. AssertIsHost on Record.
- Trigger midnight rollover. `TimeDayDidChange` is local to each machine but `OpsController.DayDidChange`'s gate `if (StateManager.IsHost)` prevents non-host execution.

### Sandbox vs Company-mode

| Behavior | Sandbox | Non-Sandbox (Tutorial / Career) |
|---|---|---|
| `LoanManager` exists | NO (`StateManager.cs:312`) | Yes |
| `CanAfford(any)` | true (`StateManager.cs:1258`) | `Balance >= expense` |
| `MoneyCommand /money cheat` | Allowed | Rejected |
| `OpsCommand setTier` | Allowed | Rejected |
| Other ledger flow | Same — payments still fire | Same |

`CanAfford` returning true in Sandbox means `EquipmentPurchase` lets you buy infinity equipment; `Progression.PayToStartPhase` doesn't block; `LoanManager` doesn't exist so loans aren't a path to overdraft. Balance can go very negative in Sandbox if mods cause expenses without revenue.

---

## Patch points for custom income/expense streams

### Pattern 1: Direct call from a host-side hook

```csharp
[HarmonyPatch(typeof(SomeMonoBehaviour), "SomeMethod")]
[HarmonyPostfix]
static void Patch_SomeMethod(SomeMonoBehaviour __instance) {
    if (!StateManager.IsHost) return;
    StateManager.Shared.ApplyToBalance(
        amount: 100,
        category: Ledger.Category.Bank,           // or repurpose Freight, etc.
        payee: new EntityReference(EntityType.Car, __instance.id),
        memo: "ModName: per-event reward",
        count: 1,
        quiet: false);
}
```

### Pattern 2: Custom request message

For a client→host charge or pay request:

1. Define `RequestModName(...) : IGameMessage` with `[MinimumAccessLevel(...)]`.
2. Add to `MessagepackSupport` registration if needed (vanilla auto-discovers `IGameMessage`s via `IGameMessage` Union — confirm at boot).
3. Patch `StateManager.Handle` postfix to dispatch the new type.
4. Handler calls `ApplyToBalance` host-side.

### Pattern 3: Custom payee via `EntityReference`

`EntityReference` is `(EntityType, string identifier)`. The `EntityType` enum has values for known types; mods adding new entities can use any `EntityType` value as a tag and disambiguate by `identifier`. The Memo field is freeform — recommended for mod-specific tags. The Daily Report and Finance panel both display `Payee?.Text() ?? Memo`.

### Loan modifications

- **Replace interest formula:** Patch `LoanManager.CalculateInterestPayment`. Vanilla is `round(loanAmount * 0.1f) + offset`.
- **Variable rates / term loans:** Patch `LoanManager.PayInterestIfNeeded` and `HandleOffsetLoan`. Maintain mod-side state in a separate KVO object (register via `StateManager.RegisterPropertyObject`).
- **Custom collateral:** Patch `LoanManager.ValueOfAssets` to include track length, contract value, etc.
- **Auto-call / foreclosure:** Subscribe to `BalanceDidChange`, check if balance went negative + threshold, force `LoanAmount` reduction or trigger repossession (would need a custom RemoveCar flow).

### Custom currencies

The `Balance` is `int`. To support multiple currencies:
- Maintain mod-side KVO objects for parallel currencies; register via `RegisterPropertyObject` with `HostOnly` auth.
- Mirror reads: present a "base + extras" balance in UI by patching `BalanceDisplay.UpdateBalance`.
- Mirror writes: patch `ApplyToBalance` postfix to also debit/credit the parallel currencies based on category.
- Currency conversion: free function on host; expose to clients via mod request messages.
- The `:C0` format string is locked to `$` per `CurrencySymbolHelper.SetCurrencySymbol("$")` at `StateManager.Awake:207`. To change, patch Awake or call `CurrencySymbolHelper.SetCurrencySymbol(newSymbol)` post-init. Persisted ledger entries display with the *current* symbol (no per-entry symbol storage).

### Custom income streams (host-side ticker)

- Subscribe `Messenger.Default.Register<TimeDayDidChange>(this, OnDay)` for daily, `<TimeAdvanced>` for sub-daily, or run your own coroutine on a tracked GameObject.
- Always gate `if (!StateManager.IsHost) return;`.
- Use `quiet: true` if you have a custom broadcast; `quiet: false` for default "Received payment" message.

### Custom expense streams

- Same pattern. `ApplyToBalance(-amount, ...)` debits.
- For "shop closes if can't afford" semantics, mirror `RepairTrack.DailyPayables`: check `CanAfford` first, set a "PaidCurrent" flag in mod KVO, broadcast a "service stopped" message if false.

### Hidden gates / pitfalls when adding economy

- **Sandbox makes `CanAfford` always true** — mods that gate features by `CanAfford` give Sandbox players unlimited access. Use `if (StateManager.IsSandbox || stateManager.CanAfford(x))` if Sandbox-aware.
- **`amount == 0` is a no-op** — to log a zero-cost event, call `Ledger.Record` directly (not `ApplyToBalance`).
- **`ApplyToBalance` can recurse** — if your patch on it calls back into ApplyToBalance, you re-enter. Vanilla doesn't, but mod-on-mod stacking can. Wrap in `_inApplyToBalance` flag if needed.
- **`Multiplayer.Broadcast` formatting uses the `IFormattable :C0` operator** — locale-dependent. Players with European locales see `1.000 $` instead of `$1,000`. The `CurrencySymbolHelper.SetCurrencySymbol` overrides this to `$` globally; non-`$` mods need to manage this carefully.
- **Init order:** `LoanManager` is created in `OnPropertiesDidRestore` (Sandbox check). Mods needing it must subscribe to `PropertiesDidRestore` and check `_loanManager != null` after.
- **Save/load round-trip:** `Ledger.Load` is called only on client receipt of `LedgerResponse` AND on host save-load via `SaveManager.Load → ... → Ledger.Load`. The `_startingBalance` at host load equals the persisted starting balance — and `ReconcileIfNeeded` may inject a Balance Correction entry to align. Mod ledger entries persisted before a vanilla ReconcileIfNeeded run get reconciled away.
- **`_entries` mutations bypass the `ChangedEvent`** for the ReconcileIfNeeded path — direct `_entries.Add` doesn't fire. Patches that subscribe to `ChangedEvent` won't see the Balance Correction entry. Patch `ReconcileIfNeeded` postfix if you need to.

---

## Cross-references

- [State Manager](state-manager.md#applylocal-and-the-handle-dispatcher) — `ApplyLocal` dispatch; `Handle`'s if/else for `RequestLoanDelta`, `RequestPurchaseEquipment`, `LedgerRequest`, `LedgerResponse`.
- [Industries & Ops § Industry payments](industries-ops.md#waybill-completion-default) — `PayWaybill`, `PayLoad`, contract penalty, autodest tag-0 payment.
- [Industries & Ops § Interchange.SellAndRemove](industries-ops.md#modelopsinterchange-the-foreign-road-portal) — the "sell" tag flow on interchange tick.
- [Wear & Durability § RepairTrack](wear-durability.md#modelopsrepairtrack-industry-side-repair) — `WagesRepair` charge cadence; `BasePrice`-driven repair speed; how RepairCap affects trade-in.
- [Cars, Cargo & Loading § Player ownership / waybills](cars-cargo.md#player-ownership) — `owned` KVO key set by EquipmentPurchase; `Waybill` struct; the `ops.waybill` auth chain.
- [Request Messages § Per-message catalog](request-messages.md) — `LedgerRequest`/`LedgerResponse`, `RequestLoanDelta`, `RequestPurchaseEquipment` auth + handler locations.
- [Time & Weather § TimeDayDidChange](time-weather.md) — midnight tick semantics; `WaitTime` fast-forward and how it crosses midnight.
- [Save / Load § Snapshot](save-load.md) — `PopulateSnapshotForSave` includes `ledgerEntries: List<SerializableLedgerEntry>` alongside the snapshot dict.
