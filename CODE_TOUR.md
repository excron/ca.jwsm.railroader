# ca.jwsm.railroader — Code Tour

**Status:** parked / experimental (last commit `71e7cc5` — relicense to GPL-3.0; final substantive code work 2026-05-09 on the consist-dynamics experiment).
**Primary language:** C# (net48, Unity 2022.3, Harmony 2 via UnityModManager).
**Size:** ~8.8k C# LOC + ~46k LOC of markdown design docs.

## TL;DR

This repo is the **v1 monorepo rebuild** of a Railroader mod stack — but >95% of it is **architecture documents, README placeholders, and decompiled-game crib sheets**. The only real running code lives in `ca.jwsm.railroader.experiments/`, six clean-room sandbox probes that were used to test specific feasibility questions before the production mods were ever started.

The "production" layer (`ca.jwsm.railroader.api`, `ca.jwsm.railroader.world`, `ca.jwsm.railroader.physics`, `ca.jwsm.railroader.ui`, `ca.jwsm.railroader.web`, the entire `ca.jwsm.railroader.mods/*` tree) contains **zero implementation code** beyond a single 31-line `UIAnchor.cs` stub. Each folder is a `README.md` describing what the mod would do — design without code.

What's real is the experiments. They are well-instrumented, well-documented, and four of them produced durable findings the user wanted to carry forward. The consist-dynamics experiment in particular is a serious piece of 1D physics engineering with a long retrospective in `LESSONS.md`.

---

## What this repo is

The user prefaces the architecture doc with: "v0 lives in `../_reference/` (also archived on GitHub under `<name>-v0`). v1 is a clean-slate rebuild as a single monorepo, starting from a documented contract." ([ARCHITECTURE.md:5-8](ARCHITECTURE.md))

So this is the **post-mortem-driven rewrite** of an earlier Railroader mod stack that had grown unmaintainable. The architecture doc opens with a long "Lessons from v0" section — coupler math floated free of game physics, AE smoothing snapped every coupler on a 200-car consist, `EngineControlService` accumulated unrelated responsibilities, vanilla UI prefab edits were fragile — and every v1 rule traces back to one of those potholes.

The chosen approach was: **write the contracts first, build experiments to validate specific technical questions, then build the production mods on top of validated foundations.** The user got as far as writing the contracts and validating several experiments. They never started the production mods. The git log shows the final ~30 commits are all experiment-side iteration, then a license change and stop.

The "v0" predecessor referenced throughout is a separate repo collection (one repo per assembly: `ca.jwsm.railroader.api`, `ca.jwsm.railroader.mods.physics`, `ca.jwsm.railroader.mods.derailchasm`, etc.) — not present in this clone. The crib-sheets and surveys here were mined from the decompiled game (`Railroader-ILSPY/`) as research input for the v1 design.

This repo is the user's pivot point: they completed the planning + scratch-work, then walked away from Railroader entirely to start Mainline Bound (a physics-honest 3D train sim on Unity 6 HDRP from scratch).

---

## Subsystems

### `ca.jwsm.railroader.experiments/` — the only code that runs

Six clean-room targeted probes. Each builds independently as its own UMM mod, each answers a specific feasibility question, each is allowed to skip kernel discipline. Disciplines [ca.jwsm.railroader.experiments/README.md](ca.jwsm.railroader.experiments/README.md): "Loose by default, strict where it matters."

#### `experiments/consist-dynamics/` (~1,600 LOC C# — the biggest piece of real code)

Status: **parked in working state (2026-05-09)**. Clean-room replacement of vanilla's `IntegrationSet` 4-iteration Gauss-Seidel constraint solver with a per-car arc-length state and an implicit tridiagonal direct solve (Thomas algorithm). Plus a brake-pipe air model as a 1D diffusion field along consist arc length — same tridiagonal pattern.

Key files:
- [src/Patches/SuppressVanillaPhysics.cs](ca.jwsm.railroader.experiments/consist-dynamics/src/Patches/SuppressVanillaPhysics.cs) — three Harmony prefix-false patches on `TrainController.FixedUpdate`, `Car.FixedUpdate`, `BaseLocomotive.FixedUpdate`. Vanilla's physics tick is wholesale suppressed; our solver runs from the postfix.
- [src/Patches/GraphFastPath.cs](ca.jwsm.railroader.experiments/consist-dynamics/src/Patches/GraphFastPath.cs) — prefix patch on `Graph.LocationByMoving` with an O(1) within-segment fast path. Affects every caller in the game (vanilla's own `PositionWheelBoundsFront`, AE planner, signaling, dispatch). Big perf lever.
- [src/Solver/ImplicitChainSolver.cs](ca.jwsm.railroader.experiments/consist-dynamics/src/Solver/ImplicitChainSolver.cs) — the implicit semi-implicit Euler integrator. Tridiagonal in `dv_i`, solved by Thomas in one O(N) sweep. Coupler model is dead-zone with hard walls (zero force inside ±4 cm slack, near-rigid spring outside).
- [src/Solver/AirPipeSolver.cs](ca.jwsm.railroader.experiments/consist-dynamics/src/Solver/AirPipeSolver.cs) — 1D pressure diffusion implicit-tridiagonal. Writes back to `car.air.BrakeCylinder.Pressure` so vanilla's HUD pill bar shows our state.
- [src/State/ManagedConsist.cs](ca.jwsm.railroader.experiments/consist-dynamics/src/State/ManagedConsist.cs) — per-consist parallel-array SoA state.
- [src/Driver/ConsistDriver.cs](ca.jwsm.railroader.experiments/consist-dynamics/src/Driver/ConsistDriver.cs) — postfix entry, registry sync, dispatch.
- [LESSONS.md](ca.jwsm.railroader.experiments/consist-dynamics/LESSONS.md) — substantial retrospective: what worked, the 20% Burst-optimization-ceiling validation, the tier-list for shipping ("six-tier resume list"), found-bug in own survey doc (`Car.Weight` is in pounds, not short tons), FPS numbers up to 600 cars / 3 consists.

The user's own one-line architecture summary from the README: "Vanilla owns the world's existence and grouping. We own all motion, all brake-pipe air, and the brake force." ([README.md:118-119](ca.jwsm.railroader.experiments/consist-dynamics/README.md))

#### `experiments/track-switches/` (~1,400 LOC)

Phase 1+ scaffold for "real 3-way switches and double-slips" in Railroader's track network. Status: in-world tooling working; first topology entry adds a left-diverging spur off `Nwe2` in East Whittier. Final commit `18381c1` (2026-05-09) was the scaffold; never finished.

Key files:
- [src/ExperimentEntry.cs](ca.jwsm.railroader.experiments/track-switches/src/ExperimentEntry.cs) — F7 toggle overlay, F8 dump nearest TrackNodes/Segments, F9 re-apply `data/topology.json`, Shift+Click pick.
- [src/TopologyApplier.cs](ca.jwsm.railroader.experiments/track-switches/src/TopologyApplier.cs) — applies JSON topology spec to live `Graph` with full `TrackObjectManager.Rebuild` (partial rebuild filters out new descriptors, so heavy rebuild is required).
- [src/Patches.cs](ca.jwsm.railroader.experiments/track-switches/src/Patches.cs), [src/MultiStateSwitch.cs](ca.jwsm.railroader.experiments/track-switches/src/MultiStateSwitch.cs), [src/MultiStateSwitchStand.cs](ca.jwsm.railroader.experiments/track-switches/src/MultiStateSwitchStand.cs), [src/ThreeWaySwitchRenderer.cs](ca.jwsm.railroader.experiments/track-switches/src/ThreeWaySwitchRenderer.cs) — the multi-state switch experiment scaffolding.
- [data/topology.json](ca.jwsm.railroader.experiments/track-switches/data/topology.json) — JSON-driven topology injection.

Notable READ-survey finding embedded in README: trackwork is 100% procedural math in vanilla. No authored frog/point-rail/diamond/turnout meshes — just `Mesh/Rail.asset` extruded along curves by `SwitchGeometry.Calculate`. Producing 3-way and slip geometry is "a code problem, not an asset problem." ([README.md:18-49](ca.jwsm.railroader.experiments/track-switches/README.md))

#### `experiments/track-profile/` (~1,700 LOC)

In-game track-in-profile (gradient) display, a UI Toolkit panel sitting to the right of the HUD. Live game-state-driven (30 Hz refresh). Annotations: mileposts, switches as ◆, signals as color-coded dots, stations + industry spans, end-of-line. Re-implementation of the v0 web-client track profile, redesigned to nail the gradient display.

Key files:
- [src/TrackProfilePanel.cs](ca.jwsm.railroader.experiments/track-profile/src/TrackProfilePanel.cs) — top-level composition.
- [src/ChartView.cs](ca.jwsm.railroader.experiments/track-profile/src/ChartView.cs) — gridlines, Variant-2 fill, track line via `Painter2D`.
- [src/LiveRouteSampler.cs](ca.jwsm.railroader.experiments/track-profile/src/LiveRouteSampler.cs) — live route projection + grade sampling.
- [src/AnnotationLayer.cs](ca.jwsm.railroader.experiments/track-profile/src/AnnotationLayer.cs) — mileposts/switches/signals/stations/industries.
- [src/ExperimentEntry.cs](ca.jwsm.railroader.experiments/track-profile/src/ExperimentEntry.cs) — UMM bootstrap, dev hotkeys, GalaSoft `Messenger<CanvasScaleChanged>` subscription.
- [Themes/charcoal.json](ca.jwsm.railroader.experiments/track-profile/Themes/charcoal.json) — JSON theme tokens.

#### `experiments/ui-toolkit-hud/` (~2,000 LOC)

**STATUS: COMPLETE (2026-04-26)** — all four phases passed visual acceptance gates. Validated UI Toolkit + programmatic + JSON-theme as the production UI tech for the v1 rebuild. Frozen as a high-value reference artifact, not deleted. ([README.md:1-51](ca.jwsm.railroader.experiments/ui-toolkit-hud/README.md))

Key files:
- [src/HudClone.cs](ca.jwsm.railroader.experiments/ui-toolkit-hud/src/HudClone.cs) — clone of vanilla's `LocoControlsUI` 450×160 panel with charcoal palette + dynamic-brake slider extension + coupler-forces pill strip.
- [src/CarInspectorClone.cs](ca.jwsm.railroader.experiments/ui-toolkit-hud/src/CarInspectorClone.cs) — `ConsistInspectorPanel` replication + DPU toggle extension.
- [src/InspectorClone.cs](ca.jwsm.railroader.experiments/ui-toolkit-hud/src/InspectorClone.cs) — flagged in README as "built for the wrong inspector" — kept as a "we built the wrong one" artifact, lesson logged.
- [src/RuntimeDumper.cs](ca.jwsm.railroader.experiments/ui-toolkit-hud/src/RuntimeDumper.cs) — F8 dumps everything, F9 dumps by name fragment. Notable infrastructure worth migrating in spirit (the user flagged it for a future `mods/console`).
- [Themes/charcoal.json](ca.jwsm.railroader.experiments/ui-toolkit-hud/Themes/charcoal.json), [Themes/dark-purple.json](ca.jwsm.railroader.experiments/ui-toolkit-hud/Themes/dark-purple.json), [Themes/warm-gray.json](ca.jwsm.railroader.experiments/ui-toolkit-hud/Themes/warm-gray.json) — palette as JSON tokens loaded at startup and applied via `IStyle` setters.

#### `experiments/diesel-exhaust-vfx/` (~1,300 LOC) and `experiments/diesel-exhaust-smoke-vfx/` (sibling)

VFX Graph replacement for vanilla's diesel exhaust. The "Why this exists" section is the punchline: vanilla's exhaust is **additive-blended**, which mathematically cannot produce dark output — no `colorGradient` tweak fixes this. The experiment ships an **alpha-blended** replacement asset and patches it in.

Key files:
- [src/DieselExhaustControllerPatch.cs](ca.jwsm.railroader.experiments/diesel-exhaust-vfx/src/DieselExhaustControllerPatch.cs) — Harmony prefix on `DieselExhaustParticleController.OnEnable` swaps `__instance.visualEffect.visualEffectAsset` to the loaded bundle's asset before the controller wraps it.
- [src/SmokeColorDarkenPatch.cs](ca.jwsm.railroader.experiments/diesel-exhaust-vfx/src/SmokeColorDarkenPatch.cs) — postfix on `SmokeStartColor()` that multiplies RGB by `DarkenFactor = 0.15f` to compensate for vanilla's additive-gradient authoring.
- [src/BrakeEmissionDriver.cs](ca.jwsm.railroader.experiments/diesel-exhaust-vfx/src/BrakeEmissionDriver.cs), [src/WheelsetBrakeEmissionPatch.cs](ca.jwsm.railroader.experiments/diesel-exhaust-vfx/src/WheelsetBrakeEmissionPatch.cs) — additional thermal-glow layer on brake shoes.
- [src/ExhaustPointLightPatch.cs](ca.jwsm.railroader.experiments/diesel-exhaust-vfx/src/ExhaustPointLightPatch.cs), [src/ExhaustFlicker.cs](ca.jwsm.railroader.experiments/diesel-exhaust-vfx/src/ExhaustFlicker.cs) — exhaust stack light flicker.
- `Assets/dieselexhaust.bundle`, `Assets/brakeglow.bundle` — compiled VFX bundles from external Unity authoring project (lives outside this monorepo).

Sister experiment `diesel-exhaust-smoke-vfx/` is the "Six-Way smoke from EmberGen + peeweek shader" variant ([git log e83db45](.)).

### `ca.jwsm.railroader.ui/` — barely a stub

Only the production project with any code at all: a single 31-line file [Shared/UIAnchor.cs](ca.jwsm.railroader.ui/Shared/UIAnchor.cs). A `MonoBehaviour` marker for named slots in UI prefabs that the runtime finds via `GetComponentsInChildren<UIAnchor>()`. The comment says: "This script must exist in BOTH the mod runtime (here) AND the authoring Unity project, with the SAME namespace and type name. Prefabs serialize component references by full type name; if the namespace differs, the runtime's loaded bundle will fail to resolve UIAnchor references."

So this file exists because the prefab authoring project needs a matching type in the deployed mod assembly. Nothing else has been started.

### `ca.jwsm.railroader.api/`, `.world/`, `.physics/`, `.web/` — design-only

All four are `README.md` only. No source, no csproj. Each describes what the foundational mod would own when built: kernel composition + service registry + bus + lifecycle (api), graph/asset catalog/world-load orchestration (world), the additive coupler-force/kinematics/air streams (physics), browser map client over WebSocket (web).

### `ca.jwsm.railroader.mods/` — design-only

All nine subfolders (`console`, `dispatch`, `durability`, `editor`, `enginecontrol`, `eta`, `map`, `mapmodloader`, `webview`) are README-only. Total mod source code in this tree: **0 bytes**.

The `mapmodloader` README is the longest at 6.2 KB; it specifies a clean-room replacement for the retracted RailLoader, with map mods living in a separate `Maps/` folder alongside `Mods/`. Validation policy: code-mod missing → refuse to load, content-only map mod missing → relocate-stranded-vehicles graceful recovery. None of this is implemented. ([ca.jwsm.railroader.mods/mapmodloader/README.md](ca.jwsm.railroader.mods/mapmodloader/README.md))

### `docs/research/crib-sheets/` — 62 reference docs (~46k LOC of markdown)

Decompiled-game reconnaissance. Each file documents one vanilla subsystem (air-system, brakes, couplers, signals-dispatch, autoengineer-planner-deep, save-load, kvo-patterns, etc.) at the level of types, signatures, `file:line` citations, "patch candidates" tables, and "MP authority" notes. This was the input source for the v1 contract design. ([docs/research/crib-sheets/README.md](docs/research/crib-sheets/README.md))

Plus four narrative surveys: `map-mods-vanilla-survey.md`, `multiplayer-vanilla-survey.md`, `physics-vanilla-survey.md`, `v0-api-review.md`.

---

## Entry points

| Path | Kind | Purpose |
|---|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | readme | 1,642-line architecture doc — read FIRST. Layer model, lessons-from-v0, patches policy, lifecycle, persistence, caching, threading, logging, multiplayer, directionality. |
| [Directory.Build.props](Directory.Build.props) | build-tool | Monorepo-wide MSBuild defaults. `net48`, `GAME_DIR` env-var override, default `D:\SteamLibrary\steamapps\common\Railroader`. |
| [ca.jwsm.railroader.experiments/consist-dynamics/src/ExperimentEntry.cs](ca.jwsm.railroader.experiments/consist-dynamics/src/ExperimentEntry.cs) | main | UMM entry for consist-dynamics. `Harmony.PatchAll`, then stand aside. |
| [ca.jwsm.railroader.experiments/consist-dynamics/info.json](ca.jwsm.railroader.experiments/consist-dynamics/info.json) | mod-info | UMM manifest for consist-dynamics. |
| [ca.jwsm.railroader.experiments/track-switches/src/ExperimentEntry.cs](ca.jwsm.railroader.experiments/track-switches/src/ExperimentEntry.cs) | main | UMM entry, F7/F8/F9 hotkeys, auto-applies topology.json once. |
| [ca.jwsm.railroader.experiments/track-profile/src/ExperimentEntry.cs](ca.jwsm.railroader.experiments/track-profile/src/ExperimentEntry.cs) | main | UMM entry, GalaSoft `Messenger<CanvasScaleChanged>` subscription, 30 Hz refresh. |
| [ca.jwsm.railroader.experiments/ui-toolkit-hud/src/ExperimentEntry.cs](ca.jwsm.railroader.experiments/ui-toolkit-hud/src/ExperimentEntry.cs) | main | UMM entry with compile-time phase toggles (`ShowHudClone`, `AddDynamicBrakeSlider`, etc.). |
| [ca.jwsm.railroader.experiments/diesel-exhaust-vfx/src/ExperimentEntry.cs](ca.jwsm.railroader.experiments/diesel-exhaust-vfx/src/ExperimentEntry.cs) | main | UMM entry, loads `dieselexhaust.bundle` + `brakeglow.bundle`, applies Harmony patches. |

There is **no top-level solution file** — each experiment csproj builds independently with `dotnet build`.

---

## Notable Harmony patches (in the experiments — production has none)

| Target | Patch type | Mod | What it does |
|---|---|---|---|
| `TrainController.FixedUpdate` | Prefix-false + Postfix | consist-dynamics | Suppress vanilla physics; postfix runs our solver on same 50 Hz pulse |
| `Car.FixedUpdate` | Prefix-false | consist-dynamics | Pure suppression (anglecocks, brake visuals, mover ticks all die with it — see README's "What's NOT wired") |
| `BaseLocomotive.FixedUpdate` | Prefix-false | consist-dynamics | Pure suppression (engine sound, cab control sweeps all die) |
| `Graph.LocationByMoving` (5-arg) | Prefix with conditional fall-through | consist-dynamics | O(1) within-segment fast path; affects every caller in the game |
| `DieselExhaustParticleController.OnEnable` | Prefix | diesel-exhaust-vfx | Swap `visualEffect.visualEffectAsset` for the alpha-blended replacement |
| `DieselExhaustParticleController.SmokeStartColor` | Postfix | diesel-exhaust-vfx | Multiply RGB by `0.15f` to compensate for additive-gradient authoring |
| Misc | misc | diesel-exhaust-vfx | Point-light, wheelset-brake-emission |
| Track-system patches | misc | track-switches | `Patches.cs` — multi-state switch geometry/state injection |

---

## External dependencies

| Name | Purpose |
|---|---|
| UnityModManager | UMM 0.27.0+ — mod manager runtime |
| 0Harmony | Harmony 2 — runtime patching framework |
| Assembly-CSharp.dll | Railroader's main game DLL (read-only reference, decompiled separately into `Railroader-ILSPY/`) |
| UnityEngine + CoreModule | Unity 2022.3 runtime |
| KeyValue.Runtime, Map.Runtime, Core, Definition | Vanilla Railroader subsystem DLLs |
| GalaSoft.MvvmLight.Messaging | Already bundled inside Assembly-CSharp; experiments subscribe to `Messenger<CanvasScaleChanged>` for UI-scale updates |

No NuGet packages, no third-party libraries shipped. All references resolve against the local Railroader install (path controlled by `GAME_DIR` env var, default `D:\SteamLibrary\steamapps\common\Railroader`).

---

## Other repos in this collection it touches

The architecture doc names a number of sibling repos that exist alongside this one:

- **`Railroader-ILSPY\`** — decompiled game source (existing, separate). Read-only reference; the crib-sheets reference it by `file:line`. ([ARCHITECTURE.md:200-204](ARCHITECTURE.md))
- **`_reference\`** — v0 legacy clones, READ-ONLY (separate folder, not present in this clone): `ca.jwsm.railroader.api`, `ca.jwsm.railroader.ui`, `ca.jwsm.railroader.mods`, `ca.jwsm.railroader.mods.derailchasm`, `ca.jwsm.railroader.mods.physics`, `ca.jwsm.railroader.web`. ([ARCHITECTURE.md:162-168](ARCHITECTURE.md)) Each v0 repo is also GitHub-archived under the `<name>-v0` suffix per the user's own naming convention.
- **Unity authoring project for VFX bundles** — separate Unity 2022.3.62f2 project, separate git repo, not a submodule. Outputs `.bundle` files consumed by `experiments/diesel-exhaust-vfx`. ([experiments/diesel-exhaust-vfx/README.md:269-281](ca.jwsm.railroader.experiments/diesel-exhaust-vfx/README.md))
- **Unity authoring project for UI prefabs** — separate, described in [docs/ui-authoring.md](docs/ui-authoring.md). Outputs `.bundle` files; the `UIAnchor` type must be duplicated by namespace + name in both projects.

No `Mainline Bound` references — the user pivoted *out* of this repo into Mainline Bound, not the other way around.

---

## Parked / broken / aborted

The user explicitly marks his own work as parked or done in several places. Verbatim quotes:

### consist-dynamics — parked in working state

> **Status: parked in working state (2026-05-09).** A complete vanilla replacement would be weeks of additional work. See [`LESSONS.md`](LESSONS.md) for the durable findings, structural arguments, and prioritized resume list.

— [ca.jwsm.railroader.experiments/consist-dynamics/README.md:103](ca.jwsm.railroader.experiments/consist-dynamics/README.md)

Followed by an explicit six-tier resume list (Tier A polish → Tier F "6D spatial Featherstone — *this is where the original Featherstone musing actually lives*").

### ui-toolkit-hud — complete, frozen reference

> ## STATUS: COMPLETE — 2026-04-26
> All four phases passed their visual acceptance gates...
> **This experiment does not get deleted** — it's frozen as a high-value reference artifact.

— [ca.jwsm.railroader.experiments/ui-toolkit-hud/README.md:3-49](ca.jwsm.railroader.experiments/ui-toolkit-hud/README.md)

Self-flagged misstep within ui-toolkit-hud:
> **`InspectorClone.cs`** — built for the wrong inspector (ConsistInspectorPanel is not the vehicle inspector users actually open). Kept here as a "we built the wrong one" artifact. Lesson: confirm the target with a runtime dump *first*, not from a static-scene assumption.

### dispatch — "Full rewrite pending"

> **Full rewrite pending.** The implementation in `_reference/` is being replaced from scratch — don't patch bugs there; the work is to design the new shape on top of the v1 foundations.

— [ca.jwsm.railroader.mods/dispatch/README.md:8](ca.jwsm.railroader.mods/dispatch/README.md)

(And then never started.)

### Entire mods/ + api/ + world/ + physics/ + web/ tree — design-only

Not explicitly marked "parked" but the absence of any source files in the entire production tree, combined with the git log stopping after experiments-only work, makes this the implicit ceiling of the project. The user never wrote a single line of api or production-mod code.

### track-switches — Phase 1+ scaffold

> **Status: Phase 1+: in-world tooling and topology loader.** Overlay, picker, nearest-dump, and a JSON-driven topology loader all working.

— [ca.jwsm.railroader.experiments/track-switches/README.md:88](ca.jwsm.railroader.experiments/track-switches/README.md)

Phases 2-6 (sanity replication → 3-way geometry → cycling switch stand → routing extension → double-slip) never executed.

### Known issues self-flagged in track-switches

> **Save/load not handled.** Topology lives in memory; if you save with a custom segment in place, references to it on reload may become orphaned. Don't save until we get to a phase that addresses this.

— [ca.jwsm.railroader.experiments/track-switches/README.md:179-186](ca.jwsm.railroader.experiments/track-switches/README.md)

---

## Notable findings

1. **The Thomas-algorithm physics rewrite actually beat vanilla in steady-state rolling on 600-car / 3-consist tests.** The user's own LESSONS.md captures the empirical FPS curve: 600 cars / 3 consists / multi-loco rolling → "deterministic 30, no jitter" vs vanilla's "20-50, all over the place." ([LESSONS.md:131-138](ca.jwsm.railroader.experiments/consist-dynamics/LESSONS.md))

2. **The "20% Burst optimization ceiling" hypothesis was validated structurally.** The 20% gain from Burst-compiling vanilla's hot path turned out to be the math-kernel fraction; the other 80% was managed-object access, Unity API, event dispatch, audio side-effects, and the serial 4-iteration sweep. Attack the 80% and you skip the Burst tax. ([LESSONS.md:36-46](ca.jwsm.railroader.experiments/consist-dynamics/LESSONS.md))

3. **DOTS/ECS is unreachable from a mod.** Verified during the experiment: vanilla doesn't ship `Unity.Entities`, cars are baked-in MonoBehaviours, Hybrid Renderer requires authoring-time prefab conversion. ("Tier 5 — Full DOTS/ECS rewrite — Maximum throughput, months of work" — but Not Feasible without source access.) ([LESSONS.md:65-72](ca.jwsm.railroader.experiments/consist-dynamics/LESSONS.md)) → Direct line to the user's pivot to Mainline Bound, which commits to DOTS/ECS from day one.

4. **Found bug in own survey doc.** During consist-dynamics testing the user caught that `docs/research/physics-vanilla-survey.md` said `Car.Weight` was in short tons. It's pounds. Verified via `Car.GravityForce` (Weight/2000) and `IntegrationSet.cs:393, :430` (Weight × 0.453592). ([LESSONS.md:142-147](ca.jwsm.railroader.experiments/consist-dynamics/LESSONS.md))

5. **Vanilla trackwork is 100% procedural.** No authored frog/point/diamond meshes — just one `Rail.asset` extruded by `SwitchGeometry.Calculate`. 3-way and double-slip switches are a code problem, not an asset problem. ([track-switches/README.md:18-49](ca.jwsm.railroader.experiments/track-switches/README.md))

6. **Vanilla diesel exhaust is mathematically locked to "can't be dark."** Additive blending (`dst + src`) can only brighten; the `colorGradient` knob is the wrong abstraction. The fix isn't to author a darker gradient — it's to swap the entire output context to alpha-blended. ([diesel-exhaust-vfx/README.md:60-77](ca.jwsm.railroader.experiments/diesel-exhaust-vfx/README.md))

7. **Vanilla URP ships with Opaque Texture disabled** across all 5 quality levels (`m_RequireOpaqueTexture: 0`). Heat-distortion shaders need it — so any distortion-VFX mod must patch the URP asset at runtime. Cost: ~8 MB per camera at 1080p. ([diesel-exhaust-vfx/README.md:189-214](ca.jwsm.railroader.experiments/diesel-exhaust-vfx/README.md))

8. **The 7-rule v0 lessons section in ARCHITECTURE.md is the project's most valuable artifact.** Each rule is bound to a real failure with a one-paragraph causal story. The user's notes on layering ownership leaks (`EngineControlService` overload, AE smoothing in api/host), the closed-loop control rule (AE smoothing exploded a 200-car consist), and the physics-ground-truth rule (coupler math floating free of game state) read like a how-to-not-rebuild-a-mod-stack manual. ([ARCHITECTURE.md:19-155](ARCHITECTURE.md))

9. **The "no copy-paste from v0" rule is enforced by architecture, not just discipline.** "Anything migrated from `_reference/` to v1 must be **rewritten**, not copy-pasted. Reading legacy code as 'what the answer looked like before' is fine; pasting it forward is how layer violations sneak back in." ([ARCHITECTURE.md:189-196](ARCHITECTURE.md))

10. **The architecture doc never made contact with implementation.** A 1,642-line contract written before any production code, then frozen when the user pivoted. As a design exercise it's impressive; as a working stack it's a graveyard with very nice tombstones.

---

## Build / run

Each experiment is its own UMM mod, builds independently:

```powershell
# Set the game path if it's not at the default Steam library
$env:GAME_DIR = "D:\SteamLibrary\steamapps\common\Railroader"

# Build a single experiment
cd ca.jwsm.railroader.experiments/consist-dynamics
dotnet build

# Deploy via the bundled script (build + copy to Mods/)
.\deploy.ps1
.\deploy.ps1 -Watch    # rebuild + redeploy on src/info.json/csproj save (track-switches, others)
```

The `Directory.Build.props` resolves all Unity DLL references from `$(GameDir)\Railroader_Data\Managed`. The deploy step copies the built DLL + `info.json` + bundled assets into the game's `Mods/<id>/` folder. Launch the game; check `Railroader_Data/Managed/UnityModManager/Log.txt` for the `Active` line.

There is **no top-level solution file**. There is **no production-side build path** because there's no production-side code.

VFX experiments additionally depend on bundles built externally in a separate Unity authoring project (`dieselexhaust.bundle`, `brakeglow.bundle`, `dieselsmoke.bundle`). The build pipeline is: edit `.vfx` in Unity → Tools > Build Diesel Exhaust Bundle → bundle lands in mod's `Assets/` → `dotnet build` deploys.

If reviving any of this: **start with consist-dynamics + LESSONS.md.** That's the densest concentration of validated learning. The ui-toolkit-hud findings would inform any production UI rebuild but the experiment's code itself is by-convention not to be promoted. Production mods would have to be written from the architecture doc's contracts, not copied from anywhere.

---

## See also

- The decompiled game in `Railroader-ILSPY/` (separate clone) — referenced throughout the crib-sheets by `file:line`.
- The v0 collection (`<name>-v0` GitHub archives, also locally under `_reference/`) — what this repo was supposed to replace.
- [docs/research/crib-sheets/](docs/research/crib-sheets/) — 62 reference docs covering the entire game surface; the deepest external artifact in this repo and the source of truth for "is there a patch point for X" questions.
- [ARCHITECTURE.md](ARCHITECTURE.md) — the document this whole repo orbits.
- The user's Mainline Bound project (separate repo) — the eventual destination of all the structural learning here, especially the DOTS-feasibility verdict from the consist-dynamics LESSONS.md and the "vanilla owns existence, we own motion" architectural split (now applicable in reverse: at Mainline Bound, *we* own everything).
