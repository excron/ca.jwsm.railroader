# Console Commands — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [UI Vanilla](ui-vanilla.md)

The console is a drop-down `/`-prefixed REPL bound to backquote (Game/ToggleConsole) plus a top-right toolbar button, and it doubles as the player's chat. Anything typed without a leading `/` is broadcast as a `Say` IGameMessage; anything with a `/` is dispatched through one of two parallel registries inside `ConsoleCommandHandler`. There is **no permission system at the console layer** — every host-only command guards itself by checking `StateManager.IsHost` *inside* its own `Execute`/handler method, and the registries themselves don't know which commands are host-only, sandbox-only, or hidden. Only Assembly-CSharp's own commands are auto-discovered (`Assembly.GetExecutingAssembly()`); mod-defined commands need reflection or Harmony to register. There is a hand-rolled secondary catalog of commands inside `_HandleSlashCommand` that bypasses both registries entirely (`/help`, `/log`, `/sysstats`, `/time`, `/temult`, `/tut`, `/report`).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `UI.Console.Console` (`shared`) | `UI.Console/Console.cs` | Window MonoBehaviour. Hotkey toggle, line buffer, `OnUserInput` event. |
| `UI.Console.ConsoleCommandHandler` | `UI.Console/ConsoleCommandHandler.cs` | Owns both command registries; subscribes to `Console.OnUserInput`; runs `Tokenize` + dispatch + chat fallback. |
| `UI.Console.IConsoleCommand` | `UI.Console/IConsoleCommand.cs` | Legacy single-method interface. `string Execute(string[])`. Marked with `[ConsoleCommand("/name", description)]`. |
| `UI.Console.CommandProcessor` | `UI.Console/CommandProcessor.cs` | Newer subcommand-based dispatcher. Activates handler types on each call; uses `Convert.ChangeType` to bind arg types. |
| `[ConsoleCommandHandler]` + `[ConsoleSubcommand]` | `UI.Console/ConsoleCommandHandlerAttribute.cs`, `ConsoleSubcommandAttribute.cs` | New-style command class + subcommand methods. |
| `[ConsoleDefaultCommand]` | `UI.Console/ConsoleDefaultCommandAttribute.cs` | Marks a method to run when no subcommand is given. |
| `Console` (global) | `Console.cs` (top-level, no namespace) | Static `Log(string)` shortcut to `UI.Console.Console.shared.AddLine`. Also defines `string.ConsoleEscape()` extension. |
| `GameInput.InputToggleConsole` | `UI/GameInput.cs:378` | Reads `Game/ToggleConsole` action (default backquote). |

---

## Architecture spine: how a typed line becomes a result

```
Player presses ` (backquote)               ← GameInput "Game/ToggleConsole"
   │
   ▼
Console.Update → Console.Expand            ← UI.Console/Console.cs:88, 135
   │  enables "Console" InputActionMap (history-up/down)
   │  ExpandedConsole.Focus → input field active
   ▼
ExpandedConsole.OnInputSubmit              ← ExpandedConsole.cs:131
   │  push to _history (max 10), call back to Console
   ▼
Console.HandleUserInput(line)              ← Console.cs:194 — try/catch wraps everything
   │  fires OnUserInput event
   ▼
ConsoleCommandHandler.OnConsoleUserInput   ← ConsoleCommandHandler.cs:64
   │  Trim. Empty? bail.
   │  Starts with "/"? → Tokenize → HandleSlashCommand
   │  Otherwise → Truncate(512) → StateManager.ApplyLocal(new Say(null, line))   ← chat path
   ▼
_HandleSlashCommand(comps)                  ← ConsoleCommandHandler.cs:164
   │  1. CommandProcessor.ProcessCommand(comps)        ← new-style /command subcommand
   │  2. TryGetCommandForSlash(comps[0])               ← legacy IConsoleCommand registry (with prefix-completion)
   │  3. Hand-rolled switch: /help, /log, /sysstats, /time, /temult, /tut, /tutorial, /report
   │  4. fallback: "Command not recognized."
   ▼
Result string returned → Console.AddLine(text)        ← shown locally only
```

**Three things to internalize:**

1. The two registries are checked in order — `CommandProcessor` first (handler/subcommand style), then `_commands` (legacy `IConsoleCommand`). If both define `/foo`, the new-style wins.
2. Output from a command is a *return string*. Returning `null` or whitespace prints nothing. There's no streaming output API; commands that need to emit multiple lines build a `StringBuilder` and return it joined.
3. Output is **local-only**. `Console.AddLine` only writes to the local console window — it doesn't replicate. A host running `/info` on a client's selected car shows the result only on the host's screen. Mod-side feedback to the calling client across MP requires using `Say` or a custom IGameMessage.

---

## `UI.Console.Console` — the host MonoBehaviour

```csharp
public static Console shared { get; }                       // FindObjectOfType, cached
public event Action<bool>   OnFocusedChanged;               // expanded show/hide and input-field focus
public event Action<string> OnUserInput;                    // user submitted line (raw, untrimmed)
public void AddLine(string text);
public void AddLine(string text, GameDateTime gameDateTime);
public void Toggle();
public void Expand();
public void Collapse();
```

Awake-time wiring (`Console.cs:63`):
- `inputActions.FindActionMap("Console", throwIfNotFound: true)` — separate action map only enabled while console is expanded; provides HistoryUp/HistoryDown.
- `expanded.ConfigureInputActions(inputActions)` — gives `ExpandedConsole` access to those.
- Subscribes to `window.OnShownDidChange` to fan-out via `OnFocusedChanged`.

Update loop (`Console.cs:86`):
- Polls `GameInput.shared.InputToggleConsole` every frame. **No keyboard events; no IMGUI.**
- 0.1s debounce (`_lastCollapsed`) to avoid the closing-then-reopening flicker on `Esc` paths.

`AddLine` is the only output API. Internally, every line is a `Console.Entry { GameDateTime Timestamp, string Text }` pushed to both the `ExpandedConsole` (full scrollback, 100-line limit) and the `CollapsedConsole` (4 most recent, 4s expiry). The expanded view wraps each entry as `<style=ConsoleTime>{ts}</style><style=ConsoleIndent><style=ConsoleText>{text}</style></style>\n` — TMP rich-text styles.

### Output sinks

| API | File | Notes |
|---|---|---|
| `UI.Console.Console.shared.AddLine(string [, GameDateTime])` | `Console.cs:102` | Direct path. |
| `Console.Log(string)` (global) | `Console.cs:5` | Null-safe shortcut; used by code that runs before/after the console exists. |
| `string.ConsoleEscape()` | `Console.cs:14` | Wraps in `<noparse>…</noparse>` so user-controlled text doesn't render TMP markup. **Use whenever echoing user/network strings**; vanilla forgets in many command outputs. |

### Patch candidates (Console)

| Method | Why patch |
|---|---|
| `Console.HandleUserInput` | Single chokepoint pre-dispatch. Prefix to log every command typed; postfix is useless because dispatch runs synchronously inside `OnUserInput?.Invoke`. |
| `Console.AddLine(string, GameDateTime)` | Intercept every line shown. Useful for piping to a mod log, filtering noise, or de-dupe. |
| `Console.Expand` / `Console.Collapse` | Hook console open/close — same effect via subscribing to `OnFocusedChanged`, but patching catches the imperative `Toggle` path too. |

---

## Two parallel registries

### Legacy: `IConsoleCommand` + `[ConsoleCommand]`

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public class ConsoleCommandAttribute : Attribute {                  // ConsoleCommandAttribute.cs
    public string CommandName  { get; }                              // includes the leading "/"
    public string Description  { get; }                              // null hides from /help
    public ConsoleCommandAttribute(string commandName, string description);
}

public interface IConsoleCommand {                                   // IConsoleCommand.cs
    string Execute(string[] components);                             // components[0] = "/cmd", parts space-tokenized
}
```

Discovery (`ConsoleCommandHandler.RegisterAllConsoleCommands`):
```csharp
foreach (Type item in from t in executingAssembly.GetTypes()
        where t.GetCustomAttributes(typeof(ConsoleCommandAttribute), inherit: true).Length != 0
        select t)
{
    IConsoleCommand command = (IConsoleCommand)Activator.CreateInstance(item);
    Register(command);                                                // _commands[command.CommandName()] = command
}
```

- Singleton instance per type — `Activator.CreateInstance` runs once at handler `Awake`. Field state on the command persists across invocations.
- `CommandName()` reads the attribute back via reflection (`ConsoleCommandExtensions.cs:7`) — the dictionary key is *exactly* what the attribute says, including the leading `/`.
- `[ConsoleCommand]` is `AllowMultiple = true`, but `Register` just stores by name, so multiple attributes on the same type means later ones overwrite the slot (the *type*, not its name). No vanilla type uses multiple `[ConsoleCommand]`.
- **Prefix completion**: `TryGetCommandForSlash` (`ConsoleCommandHandler.cs:149`) accepts any unique prefix. `/rep` finds `/repair` if no other command starts with `rep`. The new-style `CommandProcessor.ProcessCommand` does *not* do prefix completion — it requires an exact match on `parts[0]`.

### New: `[ConsoleCommandHandler]` + `[ConsoleSubcommand]` + `[ConsoleDefaultCommand]`

```csharp
[ConsoleCommandHandler("ops", "Management of freight and passenger operations.")]    // no leading "/"
public struct OpsCommand
{
    [ConsoleSubcommand(null, "Move cars to their destinations.")]
    private static string Sweep(string query) { ... }              // subcommand "sweep"

    [ConsoleSubcommand("list", "List all industries…")]
    private static string ListCommand(string query) { ... }        // explicit override of method-name → "list"

    [ConsoleSubcommand(null, "Set the contract tier for an industry.")]
    private string SetTier(string industryId, int tier) { ... }    // arity-typed args: int tier
}
```

Discovery (`CommandProcessor.RegisterHandlers`, `CommandProcessor.cs:14`):
- Type must carry `[ConsoleCommandHandlerAttribute]`. Command name is `attribute.Command.ToLower()` (no leading `/`); but the user types `/ops`.
- One `[ConsoleDefaultCommand]` method per type runs when no subcommand is given.
- `[ConsoleSubcommand]` methods can be instance or static, public or non-public — `BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic`. **Public methods are not scanned** (see "Gotchas").
- Subcommand name = attribute `Name` if provided, else `ConsoleSubcommandAttribute.ParseMethodName(method.Name)` which kebab-cases CamelCase: `PassWaiting` → `pass-waiting`, `SetTier` → `set-tier`.
- A new instance of the handler type is created **per invocation** via `Activator.CreateInstance` (`CommandProcessor.cs:149`). State on the handler doesn't persist across calls. (Vanilla works around this by using `static` methods — every vanilla `[ConsoleSubcommand]` method is `private static` except `OpsCommand.SetTier`.)

#### Argument parsing (CommandProcessor)

```csharp
for (int i = 0; i < parameters.Length; i++) {
    string text = parts[i + argStart];                          // raw token
    array[i] = Convert.ChangeType(text, parameterInfo.ParameterType);
}
```

`Convert.ChangeType` handles `int`, `float`, `string`, `bool` (via "True"/"False" — case-sensitive in .NET; `"true"`/`"false"` lowercase still works because `Convert.ToBoolean` is case-insensitive). Anything else: `Activator` cast attempt, then exception → typed error message.

Type → user-facing label in errors (`GetUsageString`):
- `int` → `"integer"`, `float` → `"float"`, `string` → `"string"`, `bool` → `"true/false"`, anything else → `Type.Name.ToLower()`.

**Argument arity is strict.** `parts.Length - argStart < parameters.Length` returns the usage string. If too many tokens are passed, the extras are silently ignored (the loop only iterates `parameters.Length` times).

#### Default-command fallback for shape

```csharp
if (!handler.Subcommands.TryGetValue(text, out var value)) {
    if (handler.DefaultHandler != null
        && handler.DefaultHandler.GetParameters().Length == parts.Length - 1)
    {
        return InvokeHandlerMethod(parts, command, null, handler, handler.DefaultHandler, 1);
    }
    return "Unknown subcommand.\nUsage: " + GetUsageString(command);
}
```

If the second token *isn't* a registered subcommand, the dispatcher checks whether the default handler's parameter count matches the remaining args (counting from `parts[1]`). If so, it invokes the default with `parts[1..]` as args. This is how a command can have a "no-arg" default *and* a "single-positional-arg" default at the same time — you'd need two defaults, but vanilla never does this.

### `[ConsoleDefaultCommand]`

```csharp
[AttributeUsage(AttributeTargets.Method)]
public class ConsoleDefaultCommandAttribute : Attribute { }
```

Single per type. **No vanilla command uses it.** All vanilla `[ConsoleCommandHandler]` types rely on subcommands or the legacy `IConsoleCommand` interface for the no-arg case.

---

## Tokenization

```csharp
private static List<string> Tokenize(string line)                   // ConsoleCommandHandler.cs:90
```

- Whitespace separates tokens.
- Both `'` and `"` start a quoted run; matched by *the same* character. `"hello world"` and `'hello world'` work; `"hello' world"` keeps the `'` inside the `"…"` until the closing `"`.
- **No backslash escaping.** You cannot put a `"` inside a `"…"` quoted run.
- Empty quoted runs (e.g., `""`) emit an empty string token (the `if (text.Length > 0)` flush is skipped at the closing quote — actually it does add via the `list.Add(text)` inside the loop unconditionally).
- Trailing unmatched quote: the rest of the line is consumed into a single token (no error).
- The leading `/` is part of the first token (e.g., `/repair`).

`SetLoadCommand` and `RecharterCommand` rely on quoted strings (`/recharter SOU "Southern Railway"`).

---

## Vanilla command catalog

Two flat tables: legacy (`IConsoleCommand`) and new-style (`[ConsoleCommandHandler]`). Then the hand-rolled commands defined in `_HandleSlashCommand`'s switch.

### Legacy `[ConsoleCommand]` / `IConsoleCommand`

| Command | Description | File | Args (positional) | Auth gate | Sandbox-only | Hidden? |
|---|---|---|---|---|---|---|
| `/air` | (null — hidden) | `AirCommand.cs` | `<value>` (any float, ignored) | `IsHost` | no | yes |
| `/airtest` | (null) | `AirTestCommand.cs` | none | none in code (assumes selected loco) | no | yes |
| `/crew` | Train crew management. | `CrewCommand.cs` | `list\|create\|delete\|join\|leave [name…]` | none — sends `Request*` IGameMessages | no | no |
| `/fill` | Fill car | `FillCommand.cs` | `<car> [percent=1.0]` | `IsSandbox` | yes | no |
| `/get` | Get key value data. | `GetKeyValueCommand.cs` | `<id> [key]` | none | no | no |
| `/info` | Car information | `InfoCommand.cs` | `<car>` | `IsHost` | no | no |
| `/loadgame` | (null) | `LoadGameCommand.cs` | `[saveName…]` (no arg = list saves) | `IsHost` | no | yes |
| `/mapfeature` | (null) | `MapFeatureCommand.cs` | `<feature> <true\|false>` | `IsSandbox` | yes | yes |
| `/mode` | (null) | `ModeCommand.cs` | `[normal\|company\|sandbox]` | `IsHost` | no | yes |
| `/money` | (null) | `MoneyCommand.cs` | `[cheat <amount>]` | `cheat` requires `IsSandbox` | partial | yes |
| `/pc` | Place consist | `PlaceConsistCommand.cs` | `<carString> [segmentId]` | `!= Company` (sandbox or unset) | no (rejects Company) | no |
| `/progression` | (null) | `ProgressionCommand.cs` | `[advance\|revert <id>]` | none | no | yes |
| `/recharter` | Change the reporting mark and name of your railroad. | `RecharterCommand.cs` | `<mark> "<name>"` | `IsHost` | no | no |
| `/repair` | (null) | `RepairCommand.cs` | none (uses selected car) | `IsHost && IsSandbox` | yes | yes |
| `/rerail` | (null) | `RerailCommand.cs` | none (uses selected car) | none in code; sends `Rerail` IGameMessage | no | yes |
| `/savegame` | (null) | `SaveGameCommand.cs` | `<saveName…>` | `IsHost` | no | yes |
| `/set` | Set key value data. | `SetKeyValueCommand.cs` | `<id> <key> <value>` | `IsSandbox` (unless id == `$config`) | partial | no |
| `/setload` | Set car load | `SetLoadCommand.cs` | `<car\|*> <identifier\|empty> [amount]` | `IsSandbox` | yes | no |
| `/speed` | (null) | `SpeedCommand.cs` | `<mph>` | `IsHost` | no | yes |
| `/stats` | Toggle stats display. | `FPSToggle.cs` | `<off\|fps\|ms>` | none | no | no |
| `/tp` | Teleport | `TeleportCommand.cs` | `<place\|car>` | none (camera-only) | no | no |
| `/terrain` | (null) | `TerrainCommand.cs` | `rebuild\|density [trees] [detail]\|stresstest <speed>` | none | no | yes |
| `/wait` | Wait for a number of game hours. | `WaitCommand.cs` | `<hours\|hh:mm>` | none — sends `WaitTime` IGameMessage | no | no |
| `/weather` | (null) | `WeatherCommand.cs` | `<weatherKey>` | none — sends `PropertyChange("_game","weatherId",…)` | no | yes |

### New-style `[ConsoleCommandHandler]` / `[ConsoleSubcommand]`

| Command | Description | File | Subcommands | Auth gate |
|---|---|---|---|---|
| `/ctc` | (null — hidden from /help) | `CTCCommand.cs` | `reset` (no args; clears all routes & blocks), `clear-markers` (no args) | none |
| `/ops` | Management of freight and passenger operations. | `Model.Ops/OpsCommand.cs` | `sweep <query>` (`AssertIsHost` via call path, but client falls back to `RequestOps` IGameMessage), `pass-offset <stop> <origin> <dest> <int offset>` (`AssertIsHost`), `pass-waiting <stop>`, `pass-stops` (no args), `list <query>`, `set-tier <industryId> <int tier>` (`IsSandbox`), `find-waybills <query>` | mostly `AssertIsHost` (some via direct check, some host-implicit) |

### Hand-rolled in `_HandleSlashCommand` switch (bypasses both registries)

These do **not** appear in `/help` and are not in `_commands` or `_processor._handlers`. They live in a `switch` statement at `ConsoleCommandHandler.cs:174`.

| Command | Args | Action | Auth gate |
|---|---|---|---|
| `/help` | none | Builds a sorted dictionary from both registries (descriptions only — null-description commands are omitted) | none |
| `/log carsets` | exactly `carsets` | `TrainController.Shared.LogCarSets()` (debug log dump) | none — but trivial |
| `/sysstats` | none | Returns CPU + RAM + GPU + VRAM string | none |
| `/time` | `[hh:mm]` | No arg: returns `TimeWeather.TimeOfDayString`. With arg: parses via `WaitCommand.TryParseHours`, sends `SetTimeOfDay`. | none in code; relies on `SetTimeOfDay` IGameMessage's own auth |
| `/temult` | `[float]` | Get/set `Car.TractiveForceMultiplier` (static). Local-only — not networked. | none |
| `/tut` / `/tutorial` | `[chapter [page]]` | `TutorialManager.Shared.HandleConsoleCommand(args)` — opens tutorial, optionally jumps to chapter/page. | none |
| `/report` | none | `DailyReportGenerator.Shared.GenerateReportNow()` | none |

**Critical:** `/temult` mutates `Car.TractiveForceMultiplier` directly with no host check, no sandbox check, and no networking. On a client, it changes only that client's local `Car.TractiveForceMultiplier` static. This is a *de facto* always-available client-side cheat for tractive effort.

**Critical:** `/log carsets` does no input validation other than `comps[1] == "carsets"`; it's reachable by any client and logs to the local Serilog/Debug log.

### Total: 24 legacy + 2 new-style + 7 hand-rolled = **33 vanilla commands**.

### Sandbox-only enforcement summary

| Command | Sandbox check site | Bypass risk |
|---|---|---|
| `/repair` | `RepairCommand.Execute:13` (`IsHost && IsSandbox`) | Hard-gated |
| `/fill` | `FillCommand.Execute:21` | Hard-gated |
| `/setload` | `SetLoadCommand.Execute:27` | Hard-gated |
| `/mapfeature` | `MapFeatureCommand.Execute:18` | Hard-gated |
| `/money cheat` | inside the `cheat` branch only — bare `/money` always works | `/money` always usable to get balance |
| `/set` | inside non-`$config` branch only | **`/set $config <field> <value>` is NOT sandbox-gated** — see below |
| `/pc` | `PlaceConsistCommand.Execute:40` (`!= Company`) | Hard-gated |
| `/ops set-tier` | `IsSandbox` check inside method | Hard-gated |

### Host-only enforcement summary

Each command does its own `if (!StateManager.IsHost) return "…";`. There is no centralized auth. **A client running a host-only command receives the rejection string locally** — the request never leaves the client. The exception is `/ops sweep` which, on a client, falls back to `StateManager.ApplyLocal(new RequestOps(...))` — sending an IGameMessage to the host (which is itself auth-gated by the message's `[MinimumAccessLevel]`).

---

## Hidden / non-obvious findings

1. **`/help` discovery is description-driven.** Any command whose `[ConsoleCommand]` description is `null` (or `[ConsoleCommandHandler]` description is `null`/empty) is silently omitted from `/help`. Vanilla hides 17 of the 24 legacy commands and 1 of the 2 new-style commands this way. The full list of `/help`-listed vanilla commands: `/crew`, `/fill`, `/get`, `/info`, `/ops`, `/pc`, `/recharter`, `/set`, `/setload`, `/stats`, `/tp`, `/wait`. The other 21 are reachable but undocumented.

2. **`/set $config <field> <value>` is unguarded.** `SetKeyValueCommand` uses reflection to write a field on `TrainController.Shared.config` (the singleton `Config`). The sandbox check only runs on the *non-$config* branch (`SetKeyValueCommand.cs:46`). Any client can mutate any public field on `Config` (curves like `wearPerMileForCondition`, `damageForCollisionMph`, etc.) on their local machine via this. Not networked, but it changes physics tuning curves on the calling machine immediately. Discoverable via `/get $config` (which reads through `KeyValueObjectForString` returning null for `$config`, so actually `/get $config` returns "Object not found" — only the *write* path handles it specially).

3. **`/temult` is a never-gated client-side tractive-effort cheat.** Listed above; worth restating because nothing in code prevents a client from `/temult 100` and dragging trains around. The mutation is to a local static so nothing replicates, but the client's own physics will lie about its train's pulling power. In MP, host-side physics still controls the actual sim.

4. **`/log carsets` is a hidden debug command.** Only string `/log carsets` triggers anything; `/log` alone or with any other subcommand returns `"No such subcommand."`. Patch surface for adding more `/log` subcommands.

5. **Duplicate command names — last-write-wins or first-register-wins.** Legacy `_commands[name] = command` *overwrites*, but the iteration order is `Assembly.GetTypes()` — alphabetical-ish but not guaranteed. New-style `CommandProcessor.RegisterHandlers` *logs an error and skips* the second registration (`CommandProcessor.cs:23`). Two commands sharing `/foo` between the two registries is silently won by the new-style registry (it's checked first in `_HandleSlashCommand`).

6. **Legacy commands implemented as `struct` get boxed every call.** `[StructLayout(LayoutKind.Sequential, Size = 1)]` is applied to `SetLoadCommand`, `FillCommand`, `GetKeyValueCommand`, `InfoCommand`, `PlaceConsistCommand`, `SetKeyValueCommand`, `TeleportCommand`, `RecharterCommand`, `OpsCommand`. The legacy flow boxes once at registration (kept in `_commands` as `IConsoleCommand`); the new-style `Activator.CreateInstance` allocates per-call. Negligible perf, but useful to know if you're patching `Activator.CreateInstance` for handler injection.

7. **`Tokenize` does not handle escapes.** No way to type a literal `"` inside a quoted string, no `\n`, no `\t`. If a command needs newlines or quotes in its args, the only workaround is to not quote at all and have the command join `comps[1..]`.

8. **The chat fallback strips control character `\x1B` (Esc).** Actually `OnInputChanged` in `ExpandedConsole.cs:148-160` clears the input field *before* submit on `` ` `` and `\x1B`, so those never reach `OnInputSubmit`.

9. **Backquote toggle has a 0.1s anti-flicker debounce** (`Console.cs:95`). If you patch the toggle, account for `_lastCollapsed`. Backquote is also routed through a *separate* pathway: when the input field has text `` ` ``, `OnInputChanged` calls `DismissDebounced` → `Console.Collapse`. So the user can press backquote to close even when the input field is focused (the Unity input system would otherwise consume the key as text).

10. **`Console.HandleUserInput` swallows exceptions.** All exceptions from the `OnUserInput` handler chain are caught and logged (`Console.cs:200`). A misbehaving subscriber (e.g., a mod-added handler) can fail silently. Inside `_HandleSlashCommand`, exceptions are also caught (`HandleSlashCommand` wraps `_HandleSlashCommand` in try/catch and returns `"Unhandled error."`).

11. **`Console.shared` uses `FindObjectOfType` lazily.** The console MonoBehaviour is scene-placed (in the in-game scene). In the main menu scene, `Console.shared` is null. `Console.Log` (the global) null-checks; `UI.Console.Console.shared.AddLine` directly does not.

12. **`OnFocusedChanged` fires from three places**: `window.OnShownDidChange`, `Expand()` (always with `true`), and `SetFocusedDelayed` (one-frame-delayed input-field focus change). Subscribers get duplicate true→true notifications; debounce in your handler.

13. **The `Console` action map is enabled only while expanded.** Anything bound under `Console/...` (HistoryUp, HistoryDown — vanilla) is dead while collapsed. If you add console-only inputs, mirror this pattern via `_consoleActionMap.Enable()` / `Disable()` calls in `Expand`/`Collapse`.

---

## Output rendering

Lines flow into two views:

- **`ExpandedConsole`** — full scrollback in a `TMP_Text` + `ScrollRect`. 100-line cap (`LineCountLimit`); when exceeded, oldest line is removed by string-trimming on the next `\n`. Auto-scrolls to bottom only if user is already at bottom (`scrollRect.verticalNormalizedPosition < 1E-05f`). Each entry is wrapped in `<style=ConsoleTime>{ts}</style><style=ConsoleIndent><style=ConsoleText>{text}</style></style>\n`. The TMP styles are defined in the prefab's TMP Style Sheet — modders can override fonts/colors there.
- **`CollapsedConsole`** — bottom-of-screen ticker; up to 4 lines (`maxVisible`), each expires after 4s (`durationVisible`) of unscaled time. Lines are individual `TMP_Text` instances drawn from `ConsoleLinePool`.

Multi-line returns from a command are sent as **a single entry** containing embedded `\n`. The `_lineCount` accounting in `ExpandedConsole` increments by 1 regardless of newline count, so one command returning 200 lines pushes only one of the 100 line-cap slots (but visually shoves lots of text). Similarly `CollapsedConsole` shows one ticker entry no matter how tall.

`ConsoleEscape` (`Console.cs:14`) is the canonical user-string sanitizer. `ReputationTracker.cs:243` and `StateManager.cs:1038` use it for chat messages; most command outputs do not.

---

## Argument typing — when used

| Layer | Typing | Validation |
|---|---|---|
| Legacy `IConsoleCommand.Execute(string[])` | None — handler must `int.TryParse` / `float.TryParse` / etc. itself | None — handler returns error string on bad input |
| New-style `[ConsoleSubcommand]` | `Convert.ChangeType(string, ParameterInfo.ParameterType)` | Throws on conversion failure → caught and turned into `"Can't interpret 'X' as integer for parameter 'Y'.\nUsage: …"` |
| Hand-rolled switch (`_HandleSlashCommand`) | None | Inline `int.TryParse` etc.; some commands skip validation entirely |

`Convert.ChangeType` supports primitive number types, bool, string, char, and any `IConvertible`-implementing type. Enums are NOT directly supported; you'd get a runtime exception. (Vanilla `OpsCommand.SetTier` uses `int`, the ones that take strings just take strings.)

---

## MP authority surface

The console layer **has no auth model of its own**. Three patterns coexist:

1. **Local-only side effects** — `/temult`, `/stats`, `/tp` (camera move), `/get`, `/sysstats`, `/log carsets`. Any client. No network traffic.
2. **`if (!StateManager.IsHost) return "…"` early-exit** — `/repair`, `/info`, `/speed`, `/recharter`, `/savegame`, `/loadgame`, `/mode`, `/air`. Client typing the command sees the rejection text; nothing reaches the host.
3. **Send an IGameMessage and let the message's auth handle it** — `/wait` (`WaitTime`), `/weather` (`PropertyChange`), `/time` (`SetTimeOfDay`), `/crew` (`RequestCreateTrainCrew`, etc.), `/rerail` (`Rerail`), `/ops sweep` (`RequestOps` for clients), `/setload` (writes via `SetLoadInfo` which is host-side). Auth comes from `[MinimumAccessLevel(...)]` on the message struct or from host-side `AssertIsHost`.

The chat path (`Say`) is `[MinimumAccessLevel(AccessLevel.Passenger)]` — anyone can chat, characterId is null (origin = sender's player).

**Asymmetry:** when a client runs a host-only command, the client sees `"Only available on host."` (or similar) but the host sees nothing. Useful for telemetry: patch the early-exit string returns to also `Log.Information` so the host can audit clients trying restricted commands, but vanilla doesn't.

---

## Mod-defined commands

### What vanilla supports

- Define a class implementing `IConsoleCommand` with `[ConsoleCommand("/name", "desc")]`, **OR** a class with `[ConsoleCommandHandler("name", "desc")]` containing methods marked `[ConsoleSubcommand]` / `[ConsoleDefaultCommand]`.
- **Discovery only scans `Assembly.GetExecutingAssembly()` (Assembly-CSharp).** Classes defined in mod DLLs are NOT auto-discovered.

### What mods actually have to do

Three options, in increasing order of cleanness:

1. **Reflect into `_commands` and `_processor`.** Both fields are `private` on `ConsoleCommandHandler` and `_handlers` is `private` on `CommandProcessor`. Reflection gets the dictionary and inserts the mod-side instance. Brittle but minimal.

2. **Harmony postfix on `ConsoleCommandHandler.RegisterAllConsoleCommands`** to add mod commands after vanilla discovery. Cleaner; runs once at console init. Requires waiting for the in-game scene to load (`Console.shared` becomes non-null).

3. **Subscribe to `Console.shared.OnUserInput` directly** and intercept lines before vanilla dispatch. Simplest for one-off slash commands but bypasses `/help`, prefix completion, and tokenization.

The Railloader/SMR ecosystem typically wraps approach (1) or (2) into a small "mod console API" library; mods consume that rather than touching reflection directly. (Mod-side patterns are out of scope for this sheet — see the API mod's own console-extension surface.)

### Patch candidates (registration & dispatch)

| Method | Why patch |
|---|---|
| `ConsoleCommandHandler.RegisterAllConsoleCommands` | Postfix to register mod commands. Runs once at `Awake`. |
| `CommandProcessor.RegisterHandlers(Assembly)` | Call manually with your mod's assembly. Vanilla calls it once with Assembly-CSharp; nothing prevents subsequent calls. |
| `ConsoleCommandHandler._HandleSlashCommand` | Prefix to intercept commands before dispatch (e.g., add a permission layer). Postfix to log all invocations. The catch in `HandleSlashCommand` swallows exceptions, so postfix on the inner method is more reliable than on the outer. |
| `ConsoleCommandHandler.OnConsoleUserInput` | Catches *every* line including chat. Prefix lets you redirect chat to a custom handler. |
| `ConsoleCommandHandler.Tokenize` | Replace tokenizer (e.g., to add escape support). Static method. |
| `CommandProcessor.ProcessCommand` | Override new-style dispatch. Returns `bool` — return `false` to fall through to legacy registry. |
| `Console.AddLine` | Intercept all output (e.g., file-log every console line). |
| `Console.HandleUserInput` | Single chokepoint for all user input post-`OnUserInput`. |

### Intercepting vanilla commands

To replace a vanilla command's behavior without unregistering it:

- For legacy: Harmony prefix on the *vanilla command type's* `Execute(string[])` method, returning your replacement string and `false` to skip the original.
- For new-style: Harmony prefix on the specific subcommand method (e.g., `OpsCommand.Sweep`). Note these are mostly `private static` — Harmony handles non-public fine.

To shadow the lookup itself:
- Patch `ConsoleCommandHandler.TryGetCommandForSlash` to substitute your `IConsoleCommand` instance.
- Patch `CommandProcessor.HandleCommand` (private) to inject your handler info.
- Or just register your command with the same name *first* and rely on the new-style registry being checked before legacy.

To add a *subcommand* to a vanilla `[ConsoleCommandHandler]` type (e.g., add `/ops cancel`): patch `CommandProcessor.RegisterHandlers` to scan an additional assembly for methods carrying a custom attribute, then mutate the existing `CommandHandlerInfo.Subcommands` dictionary. There's no vanilla support for distributed subcommand registration.

---

## Console keybind

| Action | Default | Source |
|---|---|---|
| Toggle console | Backquote (`` ` ``) | Unity InputAction `Game/ToggleConsole`; rebindable via the standard Preferences input-rebind UI. `GameInput.cs:515`. |
| History up | `Console/HistoryUp` (default Up arrow while focused) | `ExpandedConsole.ConfigureInputActions` |
| History down | `Console/HistoryDown` (default Down arrow while focused) | same |
| Submit | Enter (TMP_InputField default) | `inputField.onSubmit` |
| Close (focused, empty cmd) | Backquote (text=="`") or Esc (text=="\x1B") | `ExpandedConsole.OnInputChanged` |

Also reachable via the top-right toolbar button (`UI/TopRightArea.cs:171` → `Console.shared.Toggle()`).

---

## Cheats / debug commands

There is **no compile-time gating, no `DEBUG`/`DEVELOPMENT_BUILD` preprocessor wrapping**, and no central "cheats enabled" preference. Every console command ships in every build. Sandbox-mode is the only runtime gate, applied per-command. The `/repair`, `/fill`, `/setload`, `/money cheat`, `/mapfeature`, `/pc` (effectively), and `/set` commands self-gate to sandbox; everything else is always available.

`Cheat`-flavored: `/money cheat <amount>`, `/temult <multiplier>`. Only the former is sandbox-gated.

---

## Cross-cutting types referenced

| Type | Used by | Purpose |
|---|---|---|
| `Game.State.StateManager` | nearly every command | `IsHost`, `IsSandbox`, `ApplyLocal`, `Storage` accessor |
| `Model.TrainController` | `/repair`, `/info`, `/pc`, `/setload`, `/recharter`, `/speed`, `/air`, `/airtest`, `/log carsets`, `/temult` | `Shared`, `SelectedCar`, `SelectedLocomotive`, `CarForString`, `graph`, `PrefabStore` |
| `Model.Ops.OpsController` | `/info`, `/setload` (industry path), `/ops *` | `Shared`, `IndustryForId`, `AllIndustries` |
| `Game.Messages.*` IGameMessage | `/wait`, `/weather`, `/time`, `/crew`, `/rerail`, `/ops sweep`, `/mode` | Authoritative state-mutation channel |
| `KeyValue.Runtime.IKeyValueObject` | `/get`, `/set` | Generic property bag access |
| `Game.TimeWeather` | `/weather`, `/wait`, `/time` | `WeatherIdLookup`, `Now`, `TimeOfDayString`, `WithHours` |
| `Game.Persistence.WorldStore`, `SaveManager` | `/loadgame`, `/savegame` | Save enumeration / load |
| `Game.Progression.Progression` | `/progression` | Section advance/revert |
| `Cameras.CameraSelector` | `/tp` | `JumpToPoint`, `FollowCar`, `FollowTrack` |
| `Character.SpawnPoint` | `/tp`, `/info` | `SpawnPoint.All` enumerable |
| `Map.Runtime.MapManager` | `/terrain` | `RebuildAll`, `Instance` |
| `Track.Signals.Panel.CTCPanelController` | `/ctc` | `ClearAllRoutes`, `ClearAllBlocks` |
| `UI.Tutorial.TutorialManager` | `/tut` | `HandleConsoleCommand` |
| `Game.DailyReport.DailyReportGenerator` | `/report` | `GenerateReportNow` |
| `Helpers.Hyperlink` | output formatting | `Hyperlink.To(car)` for clickable car refs |

---

## Gotchas

- **`Console.shared` is null in the main-menu scene.** Console is scene-placed in-game only. Mod code that runs on Railloader load must wait for the in-game scene before subscribing.
- **`/help` ordering is alphabetical-by-key**, dictionary keys include the leading `/`. `/recharter` sorts after `/ops` but before `/repair`. Mod commands inserted with off-pattern names sort accordingly.
- **No streaming output.** A long-running command must build its result and return it as a single string. Async commands have to call `Console.AddLine` themselves and return `null` from `Execute`.
- **`Console.AddLine` is local-only.** A host running `/info` shows the result only to the host, even though the data is host-authoritative. To replicate output to a calling client, the command must use `Say` or a custom message.
- **The chat path doesn't echo locally.** `OnConsoleUserInput` sends `Say` via `StateManager.ApplyLocal` which routes through `StateManager.HandleCharacterMessage` (`StateManager.cs:1019`). The `Console.Log` write happens in the message handler, not in the input handler — so even the sender gets their own chat back via the message round-trip.
- **`Convert.ChangeType` with `bool`** parses `"true"`/`"false"` (case-insensitive). It does NOT parse `"1"`/`"0"` as bool — those throw. `1` parses as `int` though.
- **Subcommand names are lowercased on registration**, but `Tokenize` doesn't lowercase tokens. `ProcessCommand` lowercases `parts[0]` (the command) and `text = parts[1].ToLower()` for the subcommand match (`CommandProcessor.cs:96, 115`). Args after the subcommand are *not* lowercased. So `/ops sweep MyCar` passes `"MyCar"` to `Sweep` unchanged.
- **`ConsoleCommandAttribute` carries `AllowMultiple = true`** but vanilla uses single, and the registration path only stores by the *type* not the *name*. Multiple `[ConsoleCommand]` attributes on one type would only register the type once under the last-iterated attribute's name (the foreach `t.GetCustomAttributes(...)` is checked for any, then `Activator.CreateInstance(item)` runs once per type, and `Register` writes to `_commands[command.CommandName()]` — `CommandName()` only reads `Attribute.GetCustomAttribute` which returns the *first* attribute). Net effect: only the first attribute's name registers; others are dead.
- **`CommandProcessor.PublicCommandsAndDescriptions()` filters by `string.IsNullOrEmpty(Description)`.** `null`-description commands are hidden from `/help` regardless of registry. To force a hidden command to appear, give it any non-null description.
- **`Tokenize`'s quote handling has a subtle bug.** When closing a quote, it adds the (possibly empty) `text` to the list and resets — even if `text == ""`. So `/foo ""` produces `["/foo", ""]`. Argument validation that does `string.IsNullOrEmpty(comps[1])` will catch it, but `Convert.ChangeType("", typeof(int))` throws → typed error. Vanilla never hits this because no command takes a quoted-empty argument intentionally.
- **`CollapsedConsole` uses `Time.unscaledTime`**, so ticker entries fade based on real time even when the game is paused. The expanded view doesn't time-stamp by real time — it uses `TimeWeather.Now` (in-game time).
- **`/pc` syntax is dense.** The arg string is letter codes from a static dict (`a`=`ls-462-p18` (4-6-2 P-18 steam loco), `c`=caboose, `d`=gondola02, etc.) interleaved with optional digit prefixes (1-99 multiplier). Example: `/pc 5x` = 5 boxcar01s; `/pc na3x` = engine + tender (auto-appended) + 3 boxcars. The auto-tender insertion (`PlaceConsistCommand.cs:95`) is per-engine.
- **`/recharter` only renames cars where `car.Ident.ReportingMark == oldMark && car.IsOwnedByPlayer`.** Cars owned by other players or with non-matching reporting marks are silently skipped. The truncation limits (mark = 6 chars, name = 50) are enforced by `Truncate`.
- **`/setload *` only iterates `SelectedTrain.Where(IsFreight)`** — passenger cars and locomotives are excluded from the wildcard target. Single-car form (`/setload <car> ...`) accepts any car or industry.
- **`Activator.CreateInstance(handler.HandlerType)` is called for every new-style invocation** (`CommandProcessor.cs:149`). Don't put expensive constructor work on a `[ConsoleCommandHandler]` type — vanilla uses static methods to dodge this.
- **The console is the only place TMP rich-text from arbitrary code paths reaches the screen unfiltered.** Every `Console.Log(externalString)` is a TMP-injection vector unless wrapped in `ConsoleEscape`. Worth grepping mod code for.

---

## Cross-references

- Console as a `Window` host and toast-output sink: see [UI Vanilla › `UI.Console.Console`](ui-vanilla.md#uiconsoleconsole--drop-down-console).
- `/repair` interaction with the wear toggle: see [Wear & Durability › Gotchas](wear-durability.md#gotchas).
- `/wait` and `/time` IGameMessage flow: see [Time & Weather](time-weather.md) (`SetTimeOfDay`, `WaitTime`).
- `/weather` and `weatherId` KVO: see [Time & Weather › `weatherId`](time-weather.md).
- `/setload` and `/fill` car-load semantics: see [Cars & Cargo](cars-cargo.md).
- `/savegame` / `/loadgame` and `WorldStore` save discovery: see [Save & Load](save-load.md).
- `/get` / `/set` and the KVO key-value model: see [Settings & Preferences › KVO](settings-preferences.md) and individual system sheets for per-object key maps.
