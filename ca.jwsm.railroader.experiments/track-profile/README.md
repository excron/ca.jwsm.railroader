# Experiment: Track in Profile

A track-in-profile (gradient) display. UI Toolkit panel sitting to the right
of the HUD, showing what's behind/under/ahead of the train as a graded
chart with milepost, switch, signal, station, and industry annotations.

This is the in-game version of the track profile that shipped in the v0
web client, redesigned to nail the gradient display the web version
didn't quite get right.

## Layout

```
+-----+-------------------------------------------------------+
|     |                                                       |
|+1.5 | ......... white track-grade line .........            |
|+1.0 |                              ___                      |
|+0.5 | === [VVVVVVVV]   ===========    ===                   |
|-0.5 |              \___/                  \                 |
|-1.0 |                                       \___            |
|-1.5 |                                                       |
|     |                                                       |
+-----+-------------------------------------------------------+
       <-200ft-> [-- consist --] <----- ~1 mi lookahead ----->
```

- **Y-axis is grade %** (Option B), centered on 0%. Gridlines at ±0.5%,
  ±1%, ±1.5%, ±2%. The 0% gridline is slightly more prominent than the
  others — it's the chart's anchor and where the consist row sits.
- **White track line, 3px**, drawn via UI Toolkit's `Painter2D`. Sampled
  at ~1px cadence across the visible window.
- **Variant-2 fill**: grey area between the track line and the 0%
  reference line, on both sides. Communicates "deviation from flat"
  visually.
- **Vehicle row** centered on the 0% gridline. The track line passes
  *behind* the consist boxes — when the train passes a crest or sag,
  the line under the consist visibly bobs at the appropriate end.
- **Annotation layer**: mileposts (ticks at chart bottom), switches (◆),
  signals (color-coded dots at chart top), stations (vertical accent
  line + name above), industries (square + name below), end-of-line.
- **Side-scale** on the left, fixed-width column with grade labels next
  to each gridline.

## Direction model

Vehicles are static in the panel; the world moves around them.

- Consist always sits at the left of the chart area.
- Track ahead (in current direction of travel) always extends rightward.
- Behind-consist sliver (~200 ft) on the left of the consist.
- On reverser flip: the loco/tender markers move from the right end of
  the consist box to the left end. The consist box itself doesn't
  relocate; lookahead direction stays rightward. (Cars never jump
  around on the panel.)

This matches engineer's-perspective mental model: "what's ahead is to
my right, regardless of which way I'm pointed."

## Dev controls

A small dev-mode toolbar sits at top-left of the screen with:

- **Position slider** — scrub train head along the route.
- **Reversed toggle** — flip the consist's intra-train orientation.
- **Readout** — current head ft, mi, grade %, direction.

Hotkeys:

- **←/→** — scrub by 50 ft per held tick.
- **Shift+←/→** — scrub by 500 ft per held tick.
- **R** — toggle reverser direction.

The dev bar is intentionally outside the production panel so the
layout we're tuning isn't polluted by dev affordances.

## Data source

Static JSON at `data/sample-route.json`. Hand-authored 5-mile route with
varied terrain (flats, gentle climb, steep climb, crest, descent, rolling
hills). Annotations placed at meaningful spots (every 0.5 mi milepost,
3 switches, 4 signals, 2 stations, 2 industries, 1 end-of-line).

When the live-data adapter lands, this file goes away (or stays as a
debug fallback). The rendering code is data-source-agnostic by design.

## Why this exists

Validates a few things in isolation before a production-data version
lands in `ca.jwsm.railroader.ui`:

- Whether the Variant-2 fill reads cleanly across themes (charcoal first;
  dark-purple and warm-gray to follow).
- Whether centering the consist on the 0% line — with the grade line
  passing behind it — produces the "train bobbing over a crest" feel we
  want, or if it just looks confused.
- Whether the annotation density at typical lookahead (1 mi) is readable
  or cluttered.
- How the chart behaves at 1080p / 1440p / 4K and at non-1× UI scales.
- Whether the `Painter2D`-based draw approach scales to ~1px-cadence
  sampling across a multi-thousand-pixel-wide panel without noticeable
  cost.

## File map

```
track-profile/
├── ca.jwsm.railroader.experiments.track-profile.csproj
├── info.json                 # UMM manifest
├── README.md                 # this file
├── Themes/
│   └── charcoal.json         # color palette
├── data/
│   └── sample-route.json     # hand-authored test dataset
└── src/
    ├── ExperimentEntry.cs    # UMM bootstrap + dev controls + hotkeys
    ├── Theme.cs              # JSON theme loader (mirrors HUD experiment's)
    ├── RouteData.cs          # JSON data loader + GradeAt() lookup
    ├── TrackProfilePanel.cs  # top-level composition + view-window math
    ├── ChartView.cs          # gridlines + Variant-2 fill + track line + side-scale
    ├── VehicleRow.cs         # consist rectangles + labels
    └── AnnotationLayer.cs    # mileposts / switches / signals / stations / industries / EOL
```
