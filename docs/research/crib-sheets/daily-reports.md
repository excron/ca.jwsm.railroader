# Daily Reports — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Economy](economy.md) · [Console Commands](console-commands.md) · [Time & Weather](time-weather.md) · [Progression](progression.md) · [Save/Load](save-load.md)

The "Daily Report" is a single Markdown blob that the **host** regenerates once a game-day at 18:00, stores on a HostOnly KVO object (`_dailyReport.report`), and pushes to clients via the normal property-change channel. The Company Window's *Railroad* tab renders the latest blob through `Markroader` → `TMPMarkupRenderer`. There is **no history** — each generation overwrites the previous report. The whole subsystem lives in one 297-line file (`Game.DailyReport/DailyReportGenerator.cs`) plus one helper method on `RepairTrack`. It is the **only** place in the codebase that aggregates ledger entries by `Ledger.Category` for display, and it is the **only** place `Ledger.Category.RepairSupplies` ever appears in user-facing text — see [Economy › dead RepairSupplies enum value](economy.md#gotchas).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `DailyReportGenerator` (`GameBehaviour`) | `Game.DailyReport/DailyReportGenerator.cs:19` | Entire system. Singleton via `FindObjectOfType` |
| `DailyReportGenerator.GenerateReport(GameDateTime)` | `…/DailyReportGenerator.cs:149` | The composer. Calls all `Add*Section` methods, writes `report` KVO |
| `DailyReportGenerator.GenerateIfItsTime()` | `…/DailyReportGenerator.cs:120` | Time-gate guard; the *only* path that updates `LastGenerated` |
| `DailyReportGenerator.GenerateReportNow()` | `…/DailyReportGenerator.cs:144` | Forced generation; **does not** update `LastGenerated`. Bound to `/report` and Inspector context-menu |
| `DailyReportGenerator.LatestReportMarkup` (string get/private set) | `…/DailyReportGenerator.cs:61` | Public read of the `_dailyReport.report` KVO value |
| `DailyReportGenerator.Observe(Action)` | `…/DailyReportGenerator.cs:290` | Subscribe to `_dailyReport.report` key changes (the UI's only hook) |
| `DailyReportGenerator.StringForCategory(Ledger.Category)` | `…/DailyReportGenerator.cs:272` | The single canonical Category→display-name map (also used by the Finance tab) |
| `RepairTrack.DailyReportSummary(GameDateTime)` | `Model.Ops/RepairTrack.cs:509` | Per-shop one-liner. The only `*DailyReportSummary` method in vanilla |
| `RailroadPanelBuilder.BuildDailyReportSection` | `UI.CompanyWindow/RailroadPanelBuilder.cs:64` | The single UI consumer; renders Markroader→TMP markup |
| `/report` command | `UI.Console/ConsoleCommandHandler.cs:213-215` | Hand-rolled switch case → `GenerateReportNow()`. Hidden from `/help` |

---

## Spine: trigger timing and write path

```
Host only:
   ┌─ TickCoroutine (started in OnEnableWithProperties)         // DailyReportGenerator.cs:111
   │     while (true) { GenerateIfItsTime(); yield TimeWeather.WaitForNextHour(); }
   │
   └─ Messenger<TimeAdvanced> register                          // DailyReportGenerator.cs:93
         delegate { GenerateIfItsTime(); }

Both paths converge:
   GenerateIfItsTime()                                          // :120
       now = TimeWeather.Now
       if (now.TimeForDailyEvent(LastGenerated, hourOfDay=18))  // GameDateTime.cs:191
           publishTime = now.WithHours(18f)                     // snap to 18:00 of today
           GenerateReport(publishTime)                          // :149
           LastGenerated = publishTime                          // KVO write
       else
           Debug.Log("Daily Report: Is NOT time …")             // verbose every hour

GenerateReport(publishTime):                                    // :149
   reportStart = publishTime.AddingDays(-1)                     // 24-hr window
   ledger = StateManager.Shared.Ledger.EntriesBetween(start, publishTime, ...)
   sb = new StringBuilder("# Daily Report\n{start} to {publishTime}\n\n")
   AddWheelReportSection (Operations + Outstanding Waybills)    // :208
   AddFinanceSection (Balance + per-Category totals)            // :234
   AddInventorySection (coal, diesel-fuel)                      // :184
   AddRepairSection (per-shop DailyReportSummary)               // :171
   LatestReportMarkup = sb.ToString()                           // → KVO write triggers client sync
   Multiplayer.Broadcast("A new daily report is available.")    // → Alert console toast
```

**The two trigger paths are independent and both armed.** If the simulation is running normally, the coroutine's hourly `WaitForNextHour()` (a 5-second polled coroutine, see [Time & Weather › WaitForNextHour](time-weather.md)) will check first. If the player uses `WaitTime` (sleep/skip), `SetTimeOfDay`, or any code path that fires `TimeAdvanced`, the Messenger handler races against the coroutine. The `TimeForDailyEvent` guard plus the `LastGenerated.Day <= now.Day` check prevent double-generation, **but** the second-firing of the day still calls `GenerateReport(now.WithHours(18))` and the no-op decision is logged. There is no rate-limit on `TimeAdvanced` (`time-weather.md` notes it can fire 24× during a 6-hour `WaitTime`); only the `Day` guard prevents double work.

`TimeForDailyEvent` semantics (`GameDateTime.cs:191`):

```csharp
public bool TimeForDailyEvent(GameDateTime last, int hourOfDay)
{
    if (Day <= last.Day) return false;          // not yet a new day vs LastGenerated
    return Hours >= (float)hourOfDay;           // and we're past 18:00
}
```

Edge cases:
- **First boot (LastGenerated = `GameDateTime.Zero`):** `Day=0`. The first `Day > 0 && Hours >= 18` evaluation will fire; in a fresh new game the player typically starts before 18:00 of Day 1, so the report waits for in-game evening. The placeholder UI text is `# Daily Report\nDaily reports are compiled at 6pm.` (`RailroadPanelBuilder.cs:71`).
- **Skipping multiple days at once:** Only one report is generated per `TimeAdvanced` event. If `WaitTime(48h)` runs through two evenings, only the *latest* report survives — the intermediate day's data is lost. (See "Gotchas".)
- **Time set backward:** `Day <= last.Day` blocks generation forever once you've crossed an 18:00. There is no resync until in-game `Day` exceeds the persisted `LastGenerated.Day`.
- **`/report` cheat:** Bypasses `TimeForDailyEvent` entirely; ledger window still spans `publishTime - 1 day`, so two `/report` calls within seconds produce *identical* reports (they sample the same window). It does NOT update `LastGenerated`, so the next scheduled run will still fire normally.
- **Inspector context-menu** (`[ContextMenu("Generate Report Now")]` at `:143`) — same behaviour as `/report`. Editor-only; visible only on `DailyReportGenerator` MonoBehaviour selection.

---

## `DailyReportGenerator` — full surface

```csharp
public class DailyReportGenerator : GameBehaviour                     // :19

    private const string ObjectId          = "_dailyReport";          // :25
    private const int    ReportHourOfDay   = 18;                      // :27
    private const string LastGeneratedKey  = "lastGenerated";         // :31
    private const string ReportKey         = "report";                // :33

    public  static DailyReportGenerator Shared { get; }               // :37 FindObjectOfType
    private static GameDateTime         Now    => TimeWeather.Now;    // :35

    public  string                LatestReportMarkup { get; private set; }   // :61
    private GameDateTime          LastGenerated      { get; set; }           // :49

    private void Awake()                                              // :73
    private void OnDestroy()                                          // :80
    protected override void OnEnableWithProperties()                  // :88  HOST-ONLY arms coroutine + msg
    protected override void OnDisable()                               // :100 stops coroutine, unregister msg

    private IEnumerator   TickCoroutine()                             // :111 hourly poll
    private void          GenerateIfItsTime()                         // :120 the time-gate
    [ContextMenu("Generate Report Now")]
    public  void          GenerateReportNow()                         // :144
    private void          GenerateReport(GameDateTime publishTime)    // :149

    private void          AddRepairSection   (StringBuilder, GameDateTime)            // :171
    private void          AddInventorySection(StringBuilder)                          // :184
    private static void   AddWheelReportSection(StringBuilder, IReadOnlyList<Ledger.Entry>)  // :208
    private static void   AddFinanceSection  (GameDateTime, GameDateTime, StringBuilder, IReadOnlyList<Ledger.Entry>, int, int)  // :234

    public  static string StringForCategory(Ledger.Category)          // :272
    public  IDisposable   Observe(Action onChange)                    // :290 keyobserver on "report"
```

### KVO storage

The component does `gameObject.AddComponent<KeyValueObject>()` in `Awake` (`:75`), then `StateManager.Shared.RegisterPropertyObject("_dailyReport", kvo, AuthorizationRequirement.HostOnly)`. This means:

- Object id `_dailyReport` (note leading underscore — HostOnly by convention; see [Economy › Wire format](economy.md) and [Couplers › KVO key naming](couplers.md#kvo-key-naming)).
- Two keys ever live on it:
  - `lastGenerated` — `GameDateTime` packed via `KeyValueValue()` extension. Default `GameDateTime.Zero`.
  - `report`         — `string`, the entire Markdown blob.
- Auth: `HostOnly` (clients cannot write). Enforced through the standard `IPropertyAccessControlDelegate` path.
- Persistence: handled by the generic `PropertyObjectManager.PopulateSnapshotForSave` (`Game.State/PropertyObjectManager.cs:69`) — every registered property object is auto-included. **The latest report blob and `lastGenerated` are saved with the world.** No special save/restore code in `DailyReportGenerator`.
- MP delivery: `RestoreProperties(snapshot.Properties)` in `StateManager.PopulateFromRemoteSnapshot` (`StateManager.cs:1171`) gives a joining client the current report immediately. From then on, KVO change events propagate updates.

### Unity / GameBehaviour lifecycle

`DailyReportGenerator : GameBehaviour` ⇒ `OnEnable` registers a restore callback. `OnEnableWithProperties` (`:88`) is the right place to read `LastGenerated` (the KVO has been restored by then) and is the only method that arms the coroutine + Messenger registration. **Both arming actions are gated by `StateManager.IsHost`** — clients run `Awake`/`OnEnable` but never start ticking.

`OnDisable` (`:100`) calls `Messenger.Default.Unregister(this)` (no key — clears all subscriptions for this object) and stops the coroutine. `OnDestroy` (`:80`) unregisters the property object if `StateManager.Shared != null` (the null check guards against shutdown ordering).

There is **no `[CreateAssetMenu]`, no scene-asset reference, no manager that explicitly instantiates this class** in the decompiled code I can see. Singleton resolution is `FindObjectOfType<DailyReportGenerator>()` (`:43`). The component must be present in the loaded scene. If it's missing, `Shared` returns `null` and `/report` will NRE.

---

## Section composers

### `AddWheelReportSection` (Operations + Outstanding Waybills)

```csharp
private static void AddWheelReportSection(StringBuilder sb, IReadOnlyList<Ledger.Entry> ledgerEntries)  // :208
{
    OpsController shared = OpsController.Shared;
    sb.AppendLine("## Operations");
    int passengerCount = ledgerEntries.Sum(e => (e.Category == Ledger.Category.Passenger) ? e.Count : 0);
    int freightCount   = ledgerEntries.Sum(e => (e.Category == Ledger.Category.Freight)   ? e.Count : 0);
    sb.AppendLine("- " + passengerCount.Pluralize("passenger fare"));
    sb.AppendLine("- " + freightCount.Pluralize("freight delivery"));
    sb.AppendLine("### Outstanding Waybills");
    foreach (Area area in shared.Areas) {
        if (!area.Industries.All(i => i.ProgressionDisabled)) {            // skip fully-locked areas
            int waybillCars = shared.CarsInArea(area).Count(c => {
                Waybill? wb = c.Waybill;
                return wb.HasValue && !wb.Value.Completed;
            });
            if (waybillCars != 0)
                sb.AppendLine("- " + area.name + ": " + waybillCars.Pluralize("car"));
        }
    }
}
```

**Counting source:** Passenger and freight counts come straight from `Ledger.Entry.Count` — i.e. the `int count` argument passed to `Ledger.Record(...)`. This crib relies on every revenue producer (passenger stops, industry contracts) recording `count` correctly. See [Economy › `Ledger.Record`](economy.md). If a mod adds a custom revenue category that uses `Category.Passenger` or `Category.Freight`, those counts will roll into this report.

**Outstanding waybill query:** Live snapshot at report time, not delta. The number is "currently outstanding," NOT "added today." `IOpsCar.Waybill` (nullable struct) is checked for `HasValue && !Completed`.

**Pluralize** — `Core.PluralizeExtensions.Pluralize(int, string)` (`Railroader-ILSPY/Core/Core/PluralizeExtensions.cs`): trailing `y`→`ies` (except `ay`), trailing `x`→`xes`, else `+s`. So `"car".Pluralize(2)` → `"2 cars"`; `"waybill"` ends in `y`-after-`l` so 2 → `"2 waybillies"` — but `waybill` doesn't appear here. `"passenger fare"` and `"freight delivery"` do — `delivery` ends `y`-not-`ay` → `deliveries`. Confirm with the extension if you add new nouns.

### `AddFinanceSection` (Balance + per-Category)

```csharp
private static void AddFinanceSection(GameDateTime reportStart, GameDateTime publishTime,
                                       StringBuilder sb, IReadOnlyList<Ledger.Entry> ledger,
                                       int startBalance, int endBalance)              // :234
{
    sb.AppendLine("## Finance");
    Dictionary<Ledger.Category, int> bucket = new();
    foreach (var e in ledger) {
        if (!bucket.TryGetValue(e.Category, out var v)) v = 0;
        bucket[e.Category] = v + e.Amount;
    }
    int delta = endBalance - startBalance;
    string deltaStr = delta == 0 ? "no change" : $"{(delta<0?"-":"+")}{Mathf.Abs(delta):$##}";
    sb.AppendLine($"Balance: {endBalance:C0} ({deltaStr})");
    foreach (var (cat, amt) in bucket)
        sb.AppendLine($"- {amt:C0} {StringForCategory(cat)}");
}
```

**Quirks:**
- Format string `"$##"` is unusual — `Mathf.Abs(delta)` is an int, formatted with `$##`. The leading `$` is a literal, `##` is a digit-grouping placeholder; the result is something like `$1234` (no thousands separator, no decimals). Compare to the per-category line which uses `:C0` (currency, zero decimals — uses culture, e.g. `$1,234`). **The two formatting styles are inconsistent within a single section.** This is the only `:$##` format string for currency in the daily report.
- Categories are emitted in `Dictionary` enumeration order — effectively insertion order, which is the order ledger entries are first seen in the time window. **Not sorted by name, magnitude, or enum value.** Successive day's reports may list categories in different orders.
- A category with **zero net** (purchase + sale that cancel out) will still appear as `$0 Equipment`. Unless its sum is exactly zero AND it never appeared in the bucket at all (in which case it's skipped). Patch surface for "hide net-zero rows."
- `StringForCategory` throws `ArgumentOutOfRangeException` for unknown values (`:286`). If a mod extends the enum (it can't directly — `enum` is closed — but if the dictionary somehow contained a different int value via reflection), this would crash report generation.

**`StringForCategory` is shared with the Finance tab** (`UI.CompanyWindow/FinancePanelBuilder.cs:94`). Patching it changes both surfaces simultaneously.

### `AddInventorySection` (Fuel)

```csharp
private void AddInventorySection(StringBuilder sb)                                    // :184
{
    OpsController shared = OpsController.Shared;
    sb.AppendLine("## Fuel Inventory");
    string[] fuelLoadIds = new[] { "coal", "diesel-fuel" };
    foreach (Industry industry in shared.AllIndustries) {
        if (industry.ProgressionDisabled) continue;
        foreach (IndustryComponent c in industry.VisibleComponents) {
            if (c is IndustryUnloader { orderLoads: false, load: var load } iu
                && fuelLoadIds.Contains(load.id))
            {
                float qty = industry.Storage.QuantityInStorage(load);
                sb.AppendLine("- " + iu.DisplayName + ": " + load.QuantityString(qty));
            }
        }
    }
}
```

**Hardcoded surfaces:**
- Two fuel ids: `"coal"` and `"diesel-fuel"`. **Adding a third fuel type (e.g. `"oil"`, `"propane"`) requires patching this method or replacing it whole.** No data-driven extension point.
- Component filter: `IndustryUnloader` with `orderLoads = false`. `orderLoads = false` means "this unloader does *not* schedule incoming car orders" — i.e. it's a passive consumer site (e.g. a fuel rack at a yard) rather than a dispatching customer. Inverting the boolean would surface industries that *do* generate orders for those fuels.
- Pattern uses C# 9 property pattern with destructuring (`{ orderLoads: false, load: var load }`), which constrains the patch surface — Harmony prefixes that re-bind `orderLoads` need to do it on the field directly, not via a re-thrown event.
- **`industry.VisibleComponents`** filters out `IsVisible = false` components, which currently means `trackSpans.Length == 0 || ProgressionDisabled` (`IndustryComponent.cs:66-76`). Components that haven't unlocked yet *or* have no track span won't show in the report.

### `AddRepairSection` (Shops)

```csharp
private void AddRepairSection(StringBuilder sb, GameDateTime publishTime)             // :171
{
    OpsController shared = OpsController.Shared;
    sb.AppendLine("## Shops");
    foreach (Industry i in shared.AllIndustries.Where(i => !i.ProgressionDisabled))
        foreach (RepairTrack rt in i.VisibleComponents.OfType<RepairTrack>())
            sb.AppendLine("- " + rt.DailyReportSummary(publishTime));
}
```

The line format is fixed at the bullet level — `RepairTrack.DailyReportSummary` returns the entire post-bullet string including the "DisplayName: " prefix (`RepairTrack.cs:512`).

### `RepairTrack.DailyReportSummary(GameDateTime now)`

```csharp
public string DailyReportSummary(GameDateTime now)                                    // RepairTrack.cs:509
{
    var sb = new StringBuilder();
    sb.Append(DisplayName + ": ");
    var ctx = this.CreateContext(TimeWeather.Now, 0f);                                // NOTE: uses TimeWeather.Now, NOT the `now` arg
    var awaiting = EnumerateCarsActual(ctx).Where(NeedsRepair).ToList();
    if (awaiting.Count > 0) {
        int avg = Mathf.FloorToInt(awaiting.Average(c => c.Condition) * 100f);
        sb.Append($"{awaiting.Count.Pluralize("car")} awaiting repair, average {avg}%");
    } else {
        sb.Append("No cars awaiting repair");
    }
    sb.Append(".");
    float qty = ctx.QuantityInStorage(repairPartsLoad);
    sb.Append(" " + repairPartsLoad.QuantityString(qty) + ".");
    return sb.ToString();
}
```

**Bug:** the `GameDateTime now` parameter is **dead — never used**. The method derives its context from `TimeWeather.Now` (the live clock at the moment of call, which during normal scheduled generation equals `publishTime` to within seconds — but during `/report` could differ from a "report at 18:00" semantic). Probably harmless in practice but misleading.

**Live snapshots, not deltas:** "cars awaiting repair" is a current snapshot (queue depth at generation time), not "cars repaired this day." Same for `QuantityInStorage(repairPartsLoad)` — it's the current parts inventory.

See [Wear & Durability › `Model.Ops.RepairTrack`](wear-durability.md#modelopsrepairtrack-industry-side-repair) for `NeedsRepair`, `EnumerateCarsActual`, repair-parts mechanics.

---

## UI: `RailroadPanelBuilder.BuildDailyReportSection`

```csharp
private static void BuildDailyReportSection(UIPanelBuilder builder)                   // :64
{
    DailyReportGenerator shared = DailyReportGenerator.Shared;
    builder.AddObserver(shared.Observe(builder.Rebuild));                             // KVO subscribe
    string text = shared.LatestReportMarkup;
    if (string.IsNullOrEmpty(text))
        text = "# Daily Report\nDaily reports are compiled at 6pm.";
    string tmp = TMPMarkupRenderer.Render(Parser.Parse(text));                        // Markroader pipeline
    builder.AddTextArea(tmp, link => Debug.Log("Unhandled link clicked: " + link)).Width(400f);
}
```

**Surface facts:**
- Lives inside the **Company Window → Railroad tab**, right column of an HStack (left column is Reputation). `CompanyWindow.cs:129` registers the tab.
- Persistent panel — not modal. Players may have it open or not when generation happens.
- `builder.AddObserver(shared.Observe(builder.Rebuild))` subscribes to the `_dailyReport.report` key (the `Observe` extension passes `callInitial: false` — see `:295`). When the host writes a new report, the KVO change propagates to clients, the observer fires, the panel rebuilds, the new text renders. **No UI event is fired** — the link is purely the KVO observer.
- The link-click handler is a no-op (`Debug.Log`). **Hyperlinks in the report are functional in TMP markup but unhandled** by the panel — clicking does not navigate to entities. If you generate a report containing `<link>` tags, the link string lands in the lambda but goes nowhere. (Compare to `EntityReference.Text()` used by Finance ledger which produces clickable entity hyperlinks; the daily report does NOT use `EntityReference` anywhere — see "Hyperlinks" below.)
- Width fixed at `400f`. No vertical scroll wrapper visible at this layer (`AddTextArea` may handle internally — depends on UIPanelBuilder).
- **Toast on update:** every `GenerateReport` call fires `Multiplayer.Broadcast("A new daily report is available.")` (`:167`). `Multiplayer.Broadcast` (`Network/Multiplayer.cs:181`) creates an `Alert(AlertStyle.Console, AlertLevel.Info, …)` and either presents it locally (single-player) or `Host.SendToAll(alert)` (multiplayer). The toast appears regardless of whether the player has the Railroad tab open. Calling `/report` triggers the toast too.

### Markroader pipeline

`Markroader.Parser.Parse(string) → List<Element>` then `Markroader.TMPMarkupRenderer.Render(elements)` → TMP-formatted string. Same pipeline used by `ReleaseNotesTextBox` (`UI/ReleaseNotesTextBox.cs`), `GuideWindow`, and `CreditsMenu`. So the Markdown subset supported is whatever those screens accept: at minimum H1/H2/H3 (the report uses `#`, `##`, `###`), bullets `- `, plain text. The report never emits links, code blocks, or images.

---

## Hyperlink / EntityReference usage in report formatting

**None in vanilla.** The daily report does not call `EntityReference.Text()` or emit `<link>` tags. All entity references in the report are by *name* only:

- `Industry.DisplayName` → bare text (Repair section, via `RepairTrack.DailyReportSummary`).
- `Industry.VisibleComponents` → unloader's `DisplayName` (Inventory section).
- `Area.name` → bare text (Outstanding Waybills).

This means **clicking on a yard name or industry name in the report does nothing.** The link-click lambda in `BuildDailyReportSection` exists to handle Markroader-generated links if they were present, but the generator never produces any.

**Patch opportunity:** Wrap names with `<link="entityref:industry-id">DisplayName</link>` in your `Add*Section` patches. The link-click lambda in `BuildDailyReportSection` would then need replacement to route the link string somewhere useful (`CompanyWindow.NavigateTo*` or similar). Cross-link to a future `hyperlink-entityref.md` once it lands.

---

## Console command `/report`

Routed via the *hand-rolled `switch` statement* in `ConsoleCommandHandler.HandleCommand`, NOT through the `[ConsoleCommand]` reflection registry. See [Console Commands › hand-rolled commands](console-commands.md):

```csharp
case "/report":                                               // ConsoleCommandHandler.cs:213-215
    DailyReportGenerator.Shared.GenerateReportNow();
    return null;
```

Properties:
- **No description, no `/help` entry** (the hand-rolled switch bypasses the `_commands` dictionary and `Help()` enumerates only that dictionary).
- **No access-level gate.** Any client typing `/report` would call `Shared.GenerateReportNow()` *locally* — but on a client, `Shared` is the same component (single scene MonoBehaviour) and `GenerateReportNow → GenerateReport → LatestReportMarkup setter → KVO write` would **fail** at the KVO write because `_dailyReport` is `HostOnly`. The write would be rejected by `IPropertyAccessControlDelegate`. The client would see no change. **No request message exists** for "ask host to regenerate." This is a host-only cheat in practice.
- Returns `null` → no console echo. The Multiplayer.Broadcast toast is the only feedback.
- Bypasses `TimeForDailyEvent`. Does not update `LastGenerated`. Window is fixed at `[Now - 24h, Now]`.

---

## ProgressionDisabled integration

The report consults `ProgressionDisabled` in **three places**, all on the producer side, never on the report-generator-internal side:

| Section | Filter | Effect |
|---|---|---|
| `AddRepairSection` (`:175`) | `i.ProgressionDisabled` (Industry-level) | Hides shops in industries that haven't unlocked |
| `AddInventorySection` (`:192`) | `industry.ProgressionDisabled` (Industry-level) | Hides fuel inventories in locked industries |
| `AddWheelReportSection` (`:219`) | `area.Industries.All(i => i.ProgressionDisabled)` | Skips area-level waybill summary if **all** industries in the area are locked |

Plus implicit: `industry.VisibleComponents` filters `IsVisible = false` components, which itself returns `false` if `ProgressionDisabled` (`IndustryComponent.cs:66`).

**Notably absent:** Finance section does NOT filter ledger entries by `ProgressionDisabled`. A locked industry that somehow recorded a ledger entry would appear in `AddFinanceSection` totals. This is fine in practice because the Industry can't accept cars while disabled (see [Progression › cascade](progression.md)), so it never records ledger entries.

See [Progression › `IProgressionDisablable`](progression.md) for the full unlock cascade.

---

## Save / Load — historical reports

**Only the latest report survives.** The `_dailyReport` KVO holds:
- `report` (string) — the most-recent `GenerateReport` output; overwritten in place at every generation.
- `lastGenerated` (GameDateTime) — timestamp of the most-recent scheduled generation.

Both keys are auto-persisted by the generic `PropertyObjectManager.PopulateSnapshotForSave` path (`Game.State/PropertyObjectManager.cs:69`). Restored by `RestoreProperties` on host load and by `PopulateFromRemoteSnapshot` on client connect.

There is **no array, no list, no per-day archive**. Players who want history must screenshot or copy the textarea content. The single `_dailyReport` object id, the two known keys, the absence of any "history" or "archive" code anywhere in the file all confirm this.

**Save-restore behaviour:**
- Loading a save mid-day will **immediately** display the report from the save (whatever the host last generated before save). Both single-player and joining clients see this through the same KVO restore path.
- The first scheduled generation after load uses `LastGenerated` from the save, so the day-rollover guard works correctly across save-load.
- `/report` after load works immediately.

---

## Multiplayer authority

| Surface | Auth | Notes |
|---|---|---|
| `_dailyReport.report` write | HostOnly | Set via `RegisterPropertyObject(... HostOnly)` (`:76`) |
| `_dailyReport.lastGenerated` write | HostOnly | Same registration |
| `TickCoroutine`, `TimeAdvanced` Messenger handler | Host-only execution | Both gated by `if (StateManager.IsHost)` in `OnEnableWithProperties` (`:90`) |
| `GenerateReportNow()` (public) | No gate | Anyone can call. On client, the resulting KVO write is rejected silently |
| `/report` console command | No gate | Same: client invocation is a no-op via auth rejection |
| Latest report on client | Read-only via KVO sync | Client gets snapshot at connect and KVO change events thereafter |
| `Multiplayer.Broadcast(...)` toast | Host issues, all see | Routes through `Host.SendToAll(alert)` if multiplayer (`Multiplayer.cs:184-192`) |

There is **no request-message infrastructure** for daily reports. Clients cannot ask the host to regenerate. If a mod needs that, define a request message routed to a host-side handler that calls `Shared.GenerateReportNow()`.

The audit trail object id `audit` (similarly registered HostOnly at `AuditManager.cs:51`) is the only other comparable single-component HostOnly KVO; daily reports follow the same pattern.

---

## Patch candidates

| Method | Why patch |
|---|---|
| `DailyReportGenerator.GenerateReport(GameDateTime)` | The composer. **Postfix** is the right place to append custom sections to the StringBuilder before the `LatestReportMarkup =` line happens — but `text` is captured in a local before the assignment. Easier: prefix or transpiler. Or: reflection-grab `_keyValueObject` and append to the report after the call returns (then write `_keyValueObject["report"] = newValue`). |
| `DailyReportGenerator.AddFinanceSection` (private static) | Add custom currencies / categories. Replace `StringForCategory` for renaming. |
| `DailyReportGenerator.AddInventorySection` | Add new fuel types, or surface non-fuel inventory. Hardcoded `["coal", "diesel-fuel"]` is the obvious target. |
| `DailyReportGenerator.AddWheelReportSection` (private static) | Add per-area or per-train operational metrics. Note: `static`, so `__instance` is unavailable. |
| `DailyReportGenerator.AddRepairSection` | Replace per-shop summary; add a "Workshop budget" line, etc. |
| `DailyReportGenerator.StringForCategory` | Rename categories, localize. **Affects Finance tab too** (`FinancePanelBuilder.cs:94`). |
| `DailyReportGenerator.GenerateIfItsTime` | Change the daily cadence (e.g. 12-hour reports, weekly summaries). Be careful: `TimeForDailyEvent` is on `GameDateTime`, also called nowhere else. |
| `DailyReportGenerator.OnEnableWithProperties` | Inject your own coroutines / Messenger registrations alongside vanilla's. Keep host-gating. |
| `RepairTrack.DailyReportSummary(GameDateTime)` | Add per-shop metrics (parts on order, average wait time). Note: `now` parameter is dead-code; safe to ignore. |
| `RailroadPanelBuilder.BuildDailyReportSection` | Replace UI rendering — add scroll, alternative formatting, replace the link-click handler to make hyperlinks work. |
| `Multiplayer.Broadcast` (call site at `:167`) | Suppress or replace the broadcast toast. If you only want to gate it, prefix `Multiplayer.Broadcast` and inspect the message string for the daily-report sentinel. |
| Custom `_dailyReport` keys | Free real estate: add `myMod.section` keys to the same KVO object. They'll auto-persist + auto-sync alongside vanilla's two keys. Auth follows the parent's `HostOnly` registration. |

### Intercepting the daily rollover (cleanest hooks)

For mods that want to **observe** the daily-report event (e.g., to compute their own daily summary), the cleanest path is `DailyReportGenerator.Shared.Observe(callback)` (`:290`). The callback fires whenever the `report` KVO key changes — which means once per generation, after the new text is fully composed. Works on host AND clients. No `Harmony` patch required.

For mods that want to **intercept before composition** (e.g., to mutate ledger entries before they're aggregated, or to gate generation entirely), the only seam is a Harmony prefix on `GenerateReport` (returns `false` to skip vanilla, returns `true` to allow). There is no Messenger event fired *before* generation.

There is **no `DailyReportWillGenerate` / `DailyReportDidGenerate` Messenger event** in vanilla. Adding one is a strict improvement.

---

## Gotchas

- **The `_dailyReport.report` payload is unbounded.** It's a single `Value.String` written every day; in a long-running save its on-disk size is exactly today's report length. But: if a mod patches `GenerateReport` to *append* to the existing report instead of overwriting, the value grows unbounded and slows snapshot serialization.
- **`/report` can be called repeatedly within seconds and produces identical output.** The window is `[Now - 24h, Now]`; the ledger changes only when income/expense fires, not when time passes. Two `/report` calls one second apart return the same finance section.
- **Multi-day skips lose intermediate days.** `WaitTime(72h)` fires `TimeAdvanced` many times, but the `Day > LastGenerated.Day` guard means *one* report covers the most recent 24-hour window only. Days N+1 and N+2 never get a report. The skipped days' ledger entries will be partially included in the surviving report's window (only the most recent 24h).
- **`RepairTrack.DailyReportSummary`'s `GameDateTime now` parameter is unused.** All time reads come from `TimeWeather.Now`. If you patch the method and rely on `now`, you'll get the value the report-generator passed (snap-to-18:00) — but vanilla itself ignores it.
- **Inventory section is hardcoded to `coal` and `diesel-fuel`.** Mods adding new fuel types (`oil`, `bunker-c`, etc.) won't appear without a patch.
- **Finance dictionary order is nondeterministic** in principle, though .NET 8 / Mono's `Dictionary<TKey, TValue>` preserves insertion order in practice. Two consecutive days can list `Freight, Passenger, Fuel` vs `Passenger, Freight, Fuel` depending on which entry came first that day.
- **Currency formatting inconsistency:** the balance line uses `$##` (no thousands separator), per-category lines use `:C0` (culture-aware, with separator). Visual mismatch in the finished report.
- **`Multiplayer.Broadcast` on every generation.** This means `/report` produces a console toast for everyone in the server, including yourself. The string is hardcoded — if you want to suppress for your own mod, intercept at `Multiplayer.Broadcast`.
- **Links in the report don't navigate.** The link-click lambda is `Debug.Log("Unhandled link clicked: " + link)`. Even if you produce TMP `<link>` tags, they're cosmetically clickable but functionally inert.
- **`StringForCategory` throws on unknown values.** Closed-enum switch with `_ => throw new ArgumentOutOfRangeException(...)`. If a future version adds a Category and you patch only the producer side, the report blows up. Always patch `StringForCategory` together with `Ledger.Category` extensions.
- **Singleton via `FindObjectOfType`.** Heavy on first call after load, cached after. If a mod destroys and re-creates the component, `_instance` won't refresh until set to `null` — there's no public reset.
- **No host vs client local-call distinction.** A client typing `/report` runs `GenerateReportNow → GenerateReport` locally, builds the StringBuilder, then `LatestReportMarkup = …` triggers a KVO write that will be rejected by the HostOnly auth delegate. Wasted CPU; no error surfaced.
- **The toast string `"A new daily report is available."` is hardcoded** — not localized via any `Strings` table.
- **`OnDestroy` does NOT save the latest report.** The KVO blob is persisted only via the standard save path; if the host crashes between `LatestReportMarkup =` and the next save, the blob is lost.
- **`Awake` registers the property object before `OnEnableWithProperties` arms ticking.** Restoring from snapshot during scene load will populate `lastGenerated` and `report` before the host coroutine starts. `LastGenerated` is read inside `GenerateIfItsTime`, so the first tick after load correctly compares against the saved value.
- **`Now.WithHours(18f)` is the *publish* time, not the generation time.** If `WaitForNextHour()` fires at game-time 19:30 (because the player skipped through 18:00), the report's `publishTime` is snap-back to 18:00 of `now`'s day, the ledger window is 18:00 yesterday → 18:00 today. The 90 minutes of activity between 18:00 and 19:30 are *included* (their ledger entries exist with timestamps before `publishTime`). But ops/inventory/repair snapshots use `TimeWeather.Now` (i.e. 19:30) — so finance is "today through 18:00" but inventory is "right now (19:30)." A mild semantic split.
- **No safeguard against `Ledger.Shared` being null** at `Generate` time. If the report fires before the ledger is constructed (e.g. very early scene init), NRE. In practice `OnEnableWithProperties` runs after `RestoreNotifier`'s priority chain, which sequences things properly.

---

## Cross-references

- **Ledger entries / categories / wire format:** see [Economy › Ledger](economy.md). The `RepairSupplies` enum value is dead in vanilla but `StringForCategory` does map it to "Repair Supplies" — that mapping is the *only* user-facing reference to the dead category.
- **`/report` console command + hand-rolled vs registered command pattern:** see [Console Commands › hand-rolled commands](console-commands.md).
- **`TimeAdvanced` Messenger event, `TimeForDailyEvent`, `WaitForNextHour` polling:** see [Time & Weather › TimeAdvanced + WaitForNextHour](time-weather.md). Note: vanilla does NOT use the more granular `TimeDayDidChange` event for daily reports — it polls hourly.
- **`ProgressionDisabled` cascade:** see [Progression › cascade](progression.md).
- **`Industry.VisibleComponents` / `IndustryComponent.IsVisible`:** see [Industries / Ops](industries-ops.md) (and the matching `cars-cargo.md` for `IOpsCar.Waybill`).
- **`RepairTrack.NeedsRepair`, repair-parts mechanics, the `repairPartsLoad` field:** see [Wear & Durability › `Model.Ops.RepairTrack`](wear-durability.md#modelopsrepairtrack-industry-side-repair).
- **HostOnly KVO + `RegisterPropertyObject` pattern + snapshot persistence:** see [Save / Load](save-load.md) and [Access Control](access-control.md).
- **Markroader pipeline (also used by Release Notes, Guide, Credits):** no dedicated crib sheet; entry points are `Markroader.Parser.Parse` and `Markroader.TMPMarkupRenderer.Render`.
- **`EntityReference` and clickable hyperlinks:** vanilla daily report uses neither. A future `hyperlink-entityref.md` should cross-back here as a non-consumer.
