# Time & Weather — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Audio](audio.md)

Time-of-day in Railroader is a thin static (`Game.TimeWeather`) over a `GameDateTime` epoch + Unity-time delta + multiplier, with replication via the `SetTimeOfDay` IGameMessage and an out-of-band `TimeAdvanced` Messenger event. **Weather is delegated entirely to the third-party Enviro asset** (`EnviroManager.instance`); there is no first-party simulation of clouds, precipitation, snow, or wind. The vanilla "weather state" Railroader replicates is a single `weatherId` int (KVO key on `_game`) selecting one of seven Enviro presets. **There is no fog/visibility AI gating, no audio reaction to weather, no rain wetness affecting brakes** — `EnviroMicrosplatIntegration` only feeds shader globals (snow level, wetness, puddles, stream-flow). The system is host-authoritative, intermittently replicated (~1× per replicated time-step), and minimal enough that this doc's primary purpose is to tell modders what is and isn't there.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Game.TimeWeather` (static) | `Game/TimeWeather.cs` | Now/multiplier/sun/weather facade. **All consumers go through this** |
| `Game.GameDateTime` (struct) | `Game/GameDateTime.cs` | `(Day, Hours)` value type; full arithmetic + comparison |
| `Game.Events.TimeAdvanced` | `Game.Events/TimeAdvanced.cs` | Empty Messenger struct, sent on each `SetTimeOfDay` apply |
| `Game.Events.Time{Day,Hour,Minute}DidChange` | `Game.Events/*.cs` | Coarse-grained Messenger events emitted by `TimeObserver` polling |
| `Game.State.TimeObserver` | `Game.State/TimeObserver.cs` | 1 Hz poll loop that fans `TimeAdvanced` into Day/Hour/Minute changes |
| `Game.Messages.SetTimeOfDay` | `Game.Messages/SetTimeOfDay.cs` | `[MinimumAccessLevel(Officer)]` IGameMessage, `float TimeOfDay` (TotalSeconds) |
| `Game.Messages.WaitTime` | `Game.Messages/WaitTime.cs` | `[MinimumAccessLevel(Trainmaster)]` "skip ahead by N hours" message |
| `GameStorage["weatherId"]` | `Game.State/GameStorage.cs:518` | KVO int key on `_game`; 0..6 selects Enviro preset (default `cloudy2` = 2) |
| `Effects.EnviroSynchronizer` | `Effects/EnviroSynchronizer.cs` | 5 Hz writer of `TimeWeather.Now` → `EnviroManager.Time` and global fog height |
| `Effects.ClockDriver` (`Instance`) | `Effects/ClockDriver.cs` | Hour-of-day on/off scheduler for VFX (e.g., night-only emitters) |
| `Effects.HeadlightController.SunLevel` | `Effects/HeadlightController.cs:58` | Reads `TimeWeather.SunLevel` for headlight day/night intensity |

---

## Time spine: how `TimeWeather.Now` advances and replicates

```
Host gameplay loop drives time
   │
   ▼
StateManager.SequenceTimeMultiplier (~1 Hz, see StateManager.cs:1316-1334)
   │  WaitTime coroutine OR free-running multiplier loop
   │  ApplyLocal(new SetTimeOfDay((float)timeCursor.TotalSeconds))         ← per "step" (1 game-hour or fraction)
   ▼
StateManager.HandleSetTimeOfDay (case in big switch around StateManager.cs:824)
   │  TimeWeather.Now = new GameDateTime(setTimeOfDay.TimeOfDay)            ← writes epoch
   │  Messenger.Default.Send(default(TimeAdvanced))                          ← coarse signal
   │  Console.Log("Time: " + TimeWeather.TimeOfDayString)
   ▼
TimeObserver.CheckForChange (registered for TimeAdvanced + 1s WaitForSecondsRealtime poll)
   │  Compares Day/Hours/Minutes ints since last tick
   ▼
Messenger fans out: TimeDayDidChange / TimeHourDidChange / TimeMinuteDidChange (all empty structs)

Between SetTimeOfDay applies:
   TimeWeather.Now keeps advancing locally because GameDateTimeForTime computes:
       ((Time.time - epochUnityTime) / 3600) * TimeMultiplier + epochGameDateTime.TotalHours
   (TimeWeather.cs:27-34) — so each client interpolates between SetTimeOfDay messages
```

### `Game.TimeWeather` internals

```csharp
private struct TimeState {
    public float UnityTime;
    public GameDateTime EpochGameDateTime;
    public float TimeMultiplier;
    public GameDateTime GameDateTimeForTime(float unityTime) {
        float h = (unityTime - UnityTime) / 3600f;                            // unity-hours since epoch
        float total = EpochGameDateTime.TotalHours + h * TimeMultiplier;
        float wrap = Mathf.Repeat(total, 24f);
        return new GameDateTime(Mathf.RoundToInt((total - wrap) / 24f), wrap);
    }
}

public static GameDateTime Now {                                              // 39
    get => _timeSate.GameDateTimeForTime(Time.time);
    set { _timeSate.UnityTime = Time.time; _timeSate.EpochGameDateTime = value; }
}
public static float TimeMultiplier {                                          // 53
    get => _timeSate.TimeMultiplier;
    set { MarkTime(); _timeSate.TimeMultiplier = value; }
}
public static DateTime StartDateTime => new DateTime(1940, 4, 1);             // 66 — game world calendar epoch
public static float SunLevel => Enviro==null ? 1f : Mathf.InverseLerp(0.3f, 0.5f, Enviro.solarTime);  // 70
public static int WeatherId { get; set; }                                     // 84..115 — Enviro preset index
public static Dictionary<string,int> WeatherIdLookup => new() { ... };        // 118
public static void Reset();                                                   // 128 → Now = Zero
public static void MarkTime();                                                // 133 → Now = Now (snapshots epoch)
public static IEnumerator WaitForNextDay();                                   // 138 — 5s polled wait
public static IEnumerator WaitForNextHour();                                  // 152 — 5s polled wait
```

**Critical:** `TimeMultiplier` setter calls `MarkTime()` first, which snapshots `Now` to the new epoch before the multiplier changes. This avoids time jumps when changing speed. Setting `Now =` similarly snapshots `Time.time` as the new `UnityTime`.

**Where `TimeMultiplier` actually comes from:** stored on `_game` KVO under key `"timeMultiplier"` (default 2f) via `GameStorage.TimeMultiplier` (`GameStorage.cs:153`). But `StateManager.OnPropertiesDidRestore` *unconditionally* sets `TimeWeather.TimeMultiplier = 1f` (`StateManager.cs:282`) at restore — **the GameStorage value is dead-code, only `1f` is used in production unless something else writes the static post-restore**. Verify before relying on the storage value. `GameStorage.ObserveTimeMultiplier` exists (`GameStorage.cs:494`) but no observer registration uses it (the only `Observe`s registered in `OnPropertiesDidRestore` are `GameMode`, `WeatherId`, `BrakeForce`, `BrakeForceHandbrake`).

### Wall-clock formatting

`GameDateTime.TimeString` produces `"H:MM"`. `ConsoleTimeString` produces `" H:MM"` (right-aligned hours). `TopRightArea.UpdateCoroutine` (`UI/TopRightArea.cs:90-119`) shows seconds at a granularity that scales with `TimeMultiplier`:

| TimeMultiplier rounded | Seconds rounding |
|---|---|
| 1 | 1 s |
| 2 | 2 s |
| 3..6 | 5 s |
| ≥7 | 10 s |

Modders displaying time should generally use `GameDateTime.TimeString()` for HH:MM, never reformat from `Hours` directly.

---

## Replication & authority

### Wire format

```csharp
[MinimumAccessLevel(AccessLevel.Officer)]
public struct SetTimeOfDay(float timeOfDay) : IGameMessage {                  // Game.Messages/SetTimeOfDay.cs
    [Key(0)] public float TimeOfDay = timeOfDay;
}

[MinimumAccessLevel(AccessLevel.Trainmaster)]
public struct WaitTime : IGameMessage {                                       // Game.Messages/WaitTime.cs
    [Key(0)] public float Hours;
}
```

`SetTimeOfDay.TimeOfDay` is `(float)TotalSeconds` — note the lossy cast for long-duration saves. After ~194 days game-time at second precision, `float` mantissa overflow starts dropping seconds. Vanilla never accumulates that; modders running ultra-long sessions should be aware.

### Who writes `SetTimeOfDay`?

| Site | Reason |
|---|---|
| `StateManager.WaitTimeCoroutine` (`StateManager.cs:1316-1334`) | `WaitTime` request from UI/console; advances `timeCursor` in 1-hour steps, each `ApplyLocal`s a `SetTimeOfDay`. Also calls `Industry.TickAll(secondsRemaining / TimeMultiplier)` per step |
| `RollingStock.BuilderPhotoController` (`BuilderPhotoController.cs:46`) | Hard-sets noon (`43200f`) for the photo studio scene |

The free-running advancement (when not in `WaitTime`) flows through the **same** `SetTimeOfDay` channel — re-read of `StateManager.cs` shows `ApplyLocal(new SetTimeOfDay(…))` is the only path that updates `TimeWeather.Now` outside of save-restore.

### Snapshot-restore

```csharp
internal void ApplySnapshotMap(Snapshot.Map snapshotMap) {                    // StateManager.cs:1228
    TimeWeather.Now = new GameDateTime(snapshotMap.Day, snapshotMap.TimeOfDay);
}
```

On save load, `TimeWeather.Now` is set directly without firing `TimeAdvanced`. **Subscribers waiting on the Messenger event won't see the load-time write.** Hook `Messenger.Default.Register<MapDidLoadEvent>` or `PropertiesDidRestore` instead.

### Per-tick interpolation, not per-tick replication

Between `SetTimeOfDay` applies (which happen once per `WaitTime` step, or whenever the host's free-run loop re-broadcasts), every client computes `Now` from `Time.time` locally using the cached epoch and multiplier. So a brief packet loss does **not** stall game-time on the client; it just diverges, then snaps back when the next `SetTimeOfDay` lands. Drift is bounded by the multiplier × packet interval.

### Weather replication

```csharp
public static int WeatherId {                                                 // TimeWeather.cs:84
    get { … look up Enviro.Weather.targetWeatherType in Settings.weatherTypes; … }
    set { … Enviro.Weather.ChangeWeather(weatherTypes[value]); … }
}
```

Storage:

```csharp
public IDisposable ObserveWeatherId(Action<int> action) =>                   // GameStorage.cs:518
    _gameKeyValueObject.Observe("weatherId",
        v => action(v.IntValueOrDefault(TimeWeather.WeatherIdLookup["cloudy2"])));
```

Wired up in `StateManager.OnPropertiesDidRestore`:

```csharp
_observers.Add(_storage.ObserveWeatherId(value => { TimeWeather.WeatherId = value; }));  // StateManager.cs:278-281
```

So weather flow is: KVO `_game["weatherId"]` write → observer fires on every client → `TimeWeather.WeatherId` setter → `Enviro.Weather.ChangeWeather(preset)`. `weatherId` lives on the `_game` KVO object; auth follows `_game` defaults (which from the wear-durability survey is `MinimumLevelTrainmaster` for unprefixed keys — verify against `GameStorage.AuthorizationRequirementForPropertyWrite`).

**Default weather:** `cloudy2` (id 2) when the key is null.

**Available weather IDs** (`TimeWeather.WeatherIdLookup`):

| Key | Id |
|---|---|
| `clear` | 0 |
| `cloudy1` | 1 |
| `cloudy2` | 2 |
| `fog` | 3 |
| `rain` | 4 |
| `cloudy3` | 6 |

Note the gap at 5. The lookup is an open dictionary; presumably the asset has a 5th preset that's intentionally unmapped. The setter validates against `0 <= value < weatherTypes.Count` and bails with a `Log.Error` otherwise — so writing `weatherId = 99` is safe but no-ops.

The `/weather <name>` console command (`UI.Console.Commands/WeatherCommand.cs`) is the player-facing entry; it calls `StateManager.ApplyLocal(new PropertyChange("_game", "weatherId", new IntPropertyValue(value)))`.

### Auth summary

| Action | Required level | Channel |
|---|---|---|
| `SetTimeOfDay` | `Officer` | `IGameMessage` |
| `WaitTime` | `Trainmaster` | `IGameMessage` |
| Set `weatherId` | follows `_game` default (likely Trainmaster) | KVO |

---

## Time-driven systems

| Consumer | Reads | Effect |
|---|---|---|
| `Effects.EnviroSynchronizer.UpdateCoroutine` (`EnviroSynchronizer.cs:43`) | `TimeWeather.Now` every 0.2s | Pushes to `EnviroManager.Time.SetDateTime(...)`; renders global reflection probe if >10 game-min jump |
| `Effects.HeadlightController` | `TimeWeather.SunLevel` per Update | Lerps headlight intensity day/night via `EmissiveLightProfile` |
| `Effects.ClockDriver.Instance.Schedule(onHour, offHour, Action<bool>)` | `TimeWeather.Now.Hours` each 0.5s | Hour-of-day boolean scheduler. `ClockDrivenVisualEffect` uses it for night-only VFX |
| `RollingStock.ClockController` (in-cab clock) | `TimeWeather.Now.Hours` per Update | Drives `GaugeBehaviour` for hour/minute/second hands |
| `UI.TopRightArea` clock | `TimeWeather.Now.Hours` + `TimeMultiplier` per 0.05s | Onscreen HH:MM:SS display |
| `UI.TimeWindow` | `TimeWeather.Now` | Wait/Sleep buttons send `WaitTime` |
| `Game.DailyReport.DailyReportGenerator` | `TimeWeather.WaitForNextHour()` | Daily report tick |
| `Model.AI.AutoEngineer.SequenceTimeMultiplier` (`AutoEngineer.cs:1042-1084`) | `TimeWeather.TimeMultiplier` | AI-engineer step durations scale with multiplier |
| `Model.AI.AutoOiler` (`AutoOiler.cs:121`) | `TimeWeather.TimeMultiplier` | Records run duration |
| `Game.HostManager` (`HostManager.cs:671`) | `TimeWeather.Now` | Per-tick host reporting |
| Persistence (`WorldStore.cs:177`, `Game.State/AuditManager.cs`) | `TimeWeather.Now` | Stamps saves and audit entries |
| Network alerts (`Multiplayer.cs:170, 184`) | `TimeWeather.Now.TotalSeconds` | Alert timestamps |

**No audio component reacts to time-of-day.** No bell, whistle, ambient layer changes day vs. night. Day/night affects emissive headlight intensity (visual) and `ClockDriver`-scheduled VFX (visual) only.

---

## Weather effects (the visual side)

`Enviro.EnviroMicrosplatIntegration` (`Enviro/EnviroMicrosplatIntegration.cs`) reads from `EnviroManager.instance.Environment.Settings`:

```csharp
Shader.SetGlobalFloat("_Global_SnowLevel",   EnviroManager.instance.Environment.Settings.snow);
Shader.SetGlobalFloat("_Global_WetnessParams", … wetness …);
Shader.SetGlobalFloat("_Global_PuddleParams", EnviroManager.instance.Environment.Settings.wetness);
Shader.SetGlobalFloat("_Global_StreamMax",   EnviroManager.instance.Environment.Settings.wetness);
```

These are **shader-only** signals consumed by Microsplat terrain shaders. They do **not** feed into:

- Wheel adhesion / friction (`TrainPhysics`)
- Brake performance
- Visibility (no fog distance enforcement against AI)
- Audio (no rain/wind ambient layer)
- Train physics in any form

If a mod wants weather to affect gameplay, it must read `EnviroManager.instance.Environment.Settings.{wetness,snow,…}` and apply effects manually. There is no first-party event for "weather changed" beyond the KVO observer on `weatherId` (which only fires on preset *swap*, not on per-frame interpolation between presets).

`EnviroSynchronizer.UpdateGlobalFogHeight` (`EnviroSynchronizer.cs:87`) updates `EnviroManager.instance.Fog.Settings.globalFogHeight` to track the camera's ground position + `additionalFogOffset`. So fog *position* is camera-tracked; fog *density/visibility* is preset-driven.

### Empty stub classes

Note that `Game.Settings.EnviroSettingsApplicator` and `Enviro.EnviroMirrorPlayer`/`EnviroMirrorServer` exist but are empty (`UpdateSetting` is a no-op). `EnviroSettingChanged` is an empty Messenger struct. These look like wired-up future work that wasn't completed.

---

## Patch candidates

### Time

| Method | Why patch |
|---|---|
| `TimeWeather.Now` setter | Inject custom epoch logic; emit a synthetic `TimeAdvanced` on save-restore (vanilla doesn't). |
| `TimeWeather.TimeMultiplier` setter | Hook to add custom scaling logic; useful since `GameStorage.TimeMultiplier` observer is unwired. |
| `StateManager` `SetTimeOfDay` handler (~`StateManager.cs:824`) | Veto, observe, or transform host-broadcast time updates. |
| `StateManager.WaitTimeCoroutine` (`StateManager.cs:1316`) | Modify how `WaitTime` requests advance (e.g., simulate intermediate ticks more granularly). |
| `TimeObserver.CheckForChange` | Add new periodic events (e.g., `TimeQuarterHourDidChange`). The existing Day/Hour/Minute fan-out is at 1 Hz polling — patch here rather than re-polling separately. |

### Weather

| Method | Why patch |
|---|---|
| `TimeWeather.WeatherId` setter | Intercept preset switches. The reflection chain into Enviro means you can substitute the lookup or inject your own `EnviroWeatherType`. |
| `GameStorage.ObserveWeatherId` | Or just register your own observer; doesn't require patching. |
| `EnviroSynchronizer.UpdateCoroutine` | Override how `EnviroManager.Time` is driven (e.g., decouple from game time). |
| `EnviroMicrosplatIntegration.Update` (`Update` not shown — find via Glob) | Add new shader globals. |

### Time-driven scheduling

| Method | Why patch |
|---|---|
| `ClockDriver.Schedule(onHour, offHour, Action)` | Subscribe — no patching needed. The class is `Instance`-singleton. |
| `ClockDriver.UpdateLoop` | Replace 0.5 s polling with event-driven if you need finer granularity. |

---

## Gotchas

- **`TimeMultiplier` storage is unwired in production.** `GameStorage.TimeMultiplier` is read-write but `StateManager.OnPropertiesDidRestore` overwrites the static to `1f` and never re-subscribes. Modifying `_game["timeMultiplier"]` does nothing unless you also write `TimeWeather.TimeMultiplier` directly.
- **Save-restore writes `TimeWeather.Now` without firing `TimeAdvanced`.** `TimeObserver.CheckForChange` polls at 1 Hz and will catch the change one tick later via `Day/Hour/Minute did change` events. But subscribers strictly listening for `TimeAdvanced` will miss the load.
- **`SetTimeOfDay.TimeOfDay` is a `float` (seconds).** Single-precision float mantissa rounds at ~16M (`2^24`). Floors out at second precision around game-day 194. Long-running sessions risk losing minute-precision updates; use `GameDateTime.TotalSeconds` (double) when computing locally.
- **`GameDateTime` `==` compares with `0.001`s tolerance.** This is by design (avoids float-equality pitfalls), but means two `GameDateTime` values created from `(int day, float hours)` may unexpectedly compare equal across small float noise. For exact equality, compare `TotalSeconds` directly.
- **`TimeWeather.Now` setter** snapshots `Time.time` as the new `UnityTime` epoch. So setting `Now` mid-game effectively resets the time-since-epoch counter. The `MarkTime()` helper does the same (`Now = Now`).
- **Weather preset 5 is missing from `WeatherIdLookup`.** Setter accepts it via `WeatherCommand` only if you bypass the dictionary; the setter validates against `weatherTypes.Count` so it might or might not be playable depending on the Enviro asset.
- **Weather effects are shader-only.** No wheel adhesion penalty in rain, no brake fade in heat, no anything physics-side. Rain audio is also absent — there is no per-machine rain ambient layer in `AudioController.Group`.
- **`/weather` console command bypasses the `_game` access-control check.** It calls `StateManager.ApplyLocal(new PropertyChange(…))` which then enters the normal auth pipeline. So clients with insufficient permissions still get the console command rejected at the property-change level, but the `WeatherCommand` itself doesn't pre-validate.
- **`WaitTime` ticks Industry but not most physics.** `WaitTimeCoroutine` (`StateManager.cs:1316`) calls `Industry.TickAll(num / timeMultiplier)` per `SetTimeOfDay` step but does not advance car physics, AI engineer state, or wear odometers (those rely on `MovementInfo` which only fires on actual `Time.deltaTime` motion). So 6-hour `Wait` advances economy but trains don't move during the wait — they snap to wherever they were. (Auto-engineer's clock-bound logic via `SequenceTimeMultiplier` is the closest thing to wait-aware AI but it doesn't move trains during a wait.)
- **`TimeAdvanced` fires on every `SetTimeOfDay` apply, including the host's own re-broadcast of free-running time.** It is **not** rate-limited at the Messenger layer. Subscribers should expect O(1 Hz) frequency in normal play, much higher during `WaitTime` (one per simulated game-hour, i.e., up to ~24 events for a 6-hour skip).
- **`Enviro` is the entire weather system.** No standalone fog/skybox/lighting code exists in the Railroader codebase apart from `EnviroSynchronizer` and the `EnviroMicrosplatIntegration` shader globals. Modders wanting custom weather must talk to `EnviroManager.instance` directly.
- **`StartDateTime` is `1940-04-01`.** This is the in-fiction calendar epoch (`TimeWeather.cs:66`). `EnviroSynchronizer` adds elapsed game-hours to this `DateTime` and pushes year/month/day to Enviro for sun-position math. Changing it shifts solar declination / day length.

---

## What is **not** here

For modders coming from other sims, things to *not* spend time looking for:

- **Seasons.** No season system. The single in-game year ticks via `EnviroSynchronizer` advancing `DateTime` from 1940-04-01 forever. Visual sun position changes (Enviro computes) but no first-party "is it winter" code exists.
- **Wind / temperature / barometric pressure / humidity.** Not modelled. Not exposed.
- **Precipitation accumulation.** Enviro renders rain particles per preset; nothing accumulates anywhere.
- **Lightning / thunder.** None.
- **Weather forecast.** None — `weatherId` is a single instantaneous value; modders wanting forecasts must implement them.
- **Time zones.** Game world is single-timezone. `GameDateTime.Hours` is universal across all stations.
- **DST.** Not modelled.
- **Sunrise/sunset events.** No Messenger event. Subscribe to `TimeHourDidChange` and check `TimeWeather.SunLevel` crossing thresholds, or use `ClockDriver.Schedule(onHour, offHour, …)` if you can hard-code times.
- **Day/night audio swap.** No vanilla path. Audio is preset-by-class only.
- **Multiplayer time skew compensation.** Clients interpolate `Now` from local `Time.time` between `SetTimeOfDay` applies; no smoothing or skew correction is applied. `NetworkTime` exists but is solely for `ScheduledAudioPlayer` and physics-tick alignment, not game-time.

---

## Cross-references

- Audio reactions to time-of-day: **none in vanilla**. See [Audio › multiplayer summary](audio.md#multiplayer-summary) for the full list of audio paths and their replication mode.
- `NetworkTime` (used for audio scheduling), see `Network/NetworkTime.cs` and [Audio › `ScheduledAudioPlayer`](audio.md#scheduledaudioplayer--the-only-networked-audio).
- Wear / oil / hotbox have no time-of-day dependency. See [Wear & Durability](wear-durability.md).
- The host-only filter at the IGameMessage layer (`AccessLevel.Officer` for `SetTimeOfDay`, `Trainmaster` for `WaitTime`) is enforced by `StateManager.ApplyLocal`. See `multiplayer-vanilla-survey.md` for the full access-control model.
