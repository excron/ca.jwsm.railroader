# ca.jwsm.railroader.experiments

Sandbox for **clean-room targeted probes**. Each subfolder is one experiment that answers a specific feasibility question without dragging the production stack into the unknown.

This is the architectural equivalent of a "spike" or "scratch" project — bounded risk, no discipline pressure, easy delete. Without it, exploration either pollutes main code (technical debt accretes) or doesn't happen at all (lost learning).

## When to make an experiment

- "Does technology X work for our use case?" → experiment, not a main-stack PR
- "What does this design feel like before we commit?" → experiment
- "v0 had this idea but the implementation was broken — does the idea hold up with a clean implementation?" → experiment

## When NOT to make an experiment

- "We've decided this is the right approach" → build it in the main stack
- "I want to fix a bug" → fix it where it lives
- "I want a new feature" → land it in `mods/`

## Discipline (loose by default, strict where it matters)

Experiments are deliberately loose. Production rules that *normally* apply don't:

- ✅ Can use any tech (UI Toolkit, code-built uGUI, anything)
- ✅ Can skip kernel primitives (no `api` dependency required)
- ✅ Can patch directly (no waiting for foundational mods)
- ✅ Can ship "good enough" with no tests
- ✅ Can stop without graduating to production

But the **moral** rules still apply — these don't bend even in experiments:

- ❌ No vanilla UI prefab modification (replacement via suppression is fine)
- ❌ No vanilla asset distribution (don't ship vanilla sprites/fonts/code)
- ❌ Not for production deploy (never enter the real distribution path / player Mods/ folders)

## Shape adherence

Experiments **adhere to production shape** when they touch areas we've designed for the main stack. This makes findings transfer cleanly without architecture mismatches:

- **Themes** go in `Themes/` as **source files** (USS for UI Toolkit experiments). No inline styling in C#.
- **Assets** go in `Assets/` as source files.
- Folder layout mirrors what the production mod would look like.
- Naming follows main-stack conventions.

A "shape-respecting" experiment teaches us patterns that work *in our system*, not patterns that work *somewhere else*. The small overhead pays for itself when findings migrate.

## Lifecycle

1. **Question** — README states what we're trying to learn
2. **Build** — minimal scaffold to answer it
3. **Observe** — what works? what doesn't? gotchas? perf? feel?
4. **Decide** — works → migrate **insights** (not code) into main-stack design; doesn't → document why and either delete or freeze as a "we tried this" artifact
5. **Cleanup** — delete the folder, or leave with a status note in its README marking it complete

The experiment's **code itself never gets promoted** to production. Production code is rewritten cleanly, informed by what the experiment proved possible. Same posture as our "no copy-paste from v0" rule.

## Completed experiments (frozen reference)

- **[ui-toolkit-hud](ui-toolkit-hud/)** — validated UI Toolkit + programmatic + JSON-theme as the production UI tech. Built clones of vanilla's HUD and CarInspector with charcoal palette, exercised additive-value extensions (dynamic brake slider, DPU toggle, coupler-forces pill strip). Status: COMPLETE (2026-04-26). See its README's "STATUS: COMPLETE" section for findings, decisions, and gotchas worth not repeating.

## Layout

Each experiment is a self-contained folder:

```
ca.jwsm.railroader.experiments/
├── README.md                          ← this doc
├── ui-toolkit-hud/                    ← first experiment
│   ├── README.md                      ← question + approach + status
│   ├── *.csproj                       ← own project, builds independently
│   ├── info.json                      ← UMM manifest
│   ├── Themes/                        ← USS theme files (source)
│   ├── Assets/                        ← any other static resources (source)
│   └── src/                           ← C# code
└── (future experiments)/
```

## Reference

See `..\ARCHITECTURE.md` workspace layout + the *Experiments* section.
