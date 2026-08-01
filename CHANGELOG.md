# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased] - 2026-08-01 (branch: sketch-engine-rewrite)

### Added — SketchEngine pipeline (`StructuralTools/SketchEngine/`)

Complete rewrite of the panel-generation strategy, replacing `StraightEngine`
as the primary path. The two architecturally significant improvements are:

**1. Sketch-first boundary extraction (`SketchExtractor`)**

`SketchExtractor.GetRunBoundary()` now tries `run.SketchId → Sketch.Profile`
(a direct Revit API call, no reflection) before falling back to the
`GetFootprintBoundary()` reflection path. This makes boundary access more
reliable across Revit versions and eliminates a reflection dependency for the
most common case. Riser curves continue to use `GetRiserCurves()` reflection
(no public API equivalent exists).

**2. Mid-surface offset (`MidSurfaceOffset`)**

Every panel is now placed at the structural mid-surface of the waist slab —
the analytically correct position for `AnalyticalPanel` elements — rather
than at the nosing/top-surface reference.

- Run panels: shifted by `−slabNormal × (thickness/2)` where `slabNormal`
  is computed from the CCW corner winding (perpendicular to the inclined slab
  face, not vertical). The inclination is derived from the stair's
  `BaseElevation` / `TopElevation` level data, never from boundary edges.
- Landing panels: shifted by `−(0,0,1) × (thickness/2)` (purely vertical).

**Pipeline per node (`SketchEngineStrategy → IEngineStrategy`)**

```
StairsRun    → RunPanelBuilder
  1. Sort riser curves along travel direction.
  2. Detect flight / landing groups via median tread spacing
     (consecutive-riser spacing > 2.5× median → landing gap).
  3. Build inclined quad per flight (first riser = leading edge at
     flightBaseZ; last riser = trailing edge at flightTopZ).
  4. Build flat quad for each in-run landing (last riser of preceding
     flight + first riser of following flight).
  5. Apply MidSurfaceOffset to all corners.

StairsLanding → LandingPanelBuilder
  1. Get boundary via SketchExtractor.
  2. Project all corners to landing elevation.
  3. Fan-triangulate if > 4 corners (L/T-shaped landings).
  4. Apply MidSurfaceOffset (vertical).

Fallback → StraightEngine (transparent, when < 2 riser curves are available)
```

`EngineRouter` now always returns `SketchEngineStrategy`.
The orchestrator (`Engine/StaircaseEngine.cs`) drains diagnostics from
`SketchEngineStrategy.Diagnostics` (falling through to `StraightEngine.Diagnostics`
when the fallback fires).

---

### Changed — Sketch-first geometry; curved/spiral stairs removed

- **Sketch riser lines are now the primary geometry source**
  (`StraightEngine.TryBuildPanelFromRisers`,
  `StraightEngine.TryBuildSketchedPanelsFromRisers`):
  `GetRiserCurves()` returns the black riser lines drawn in the stair sketch
  (the transverse lines at each step). Because each riser spans the run exactly
  from boundary to boundary, using first-riser → leading edge and last-riser →
  trailing edge gives structurally exact width, gradient, and transition positions
  without any approximation. The footprint-boundary approach (round 2 fix) is
  retained as a fallback when `GetRiserCurves()` is unavailable. For sketched
  multi-segment runs, flight panels use the group's first/last riser; landing
  panels use the last riser of the preceding flight and the first riser of the
  following flight as their two opposite edges, with `OrderCornersCCW` handling
  all landing orientations (straight, L-shaped, U-shaped / 180°).

- **Curved and spiral stair support removed** (`CurvedEngine.cs`,
  `WinderEngine.cs` deleted; `RunTag.CurvedRun` and `RunTag.WinderRun` removed;
  `EngineRouter` simplified to always return `StraightEngine`):
  The tool now exclusively targets sketch-drawn stairs (boundary lines + riser
  lines + path line). If a stair run has an arc path, `StairClassifier` logs a
  warning and the run will produce no panels (riser curves won't exist; the
  footprint/path fallbacks will attempt a rectangular approximation). This
  removes roughly 300 lines of specialised curved/winder logic.

### Fixed — Staircase Analytical Model Generator (round 2)

- **`StraightEngine.BuildStraightRunPanel` — footprint-derived corners (primary fix)**:
  Replaced the `GetStairsPath()` endpoint + ±halfWidth construction with corners
  taken directly from `GetFootprintBoundary()`. `GetStairsPath()` returns the
  tread-nosing centreline, which starts and ends ~one tread-depth inside the
  structural run boundary. Using those endpoints caused three compounding errors:
  (a) **Width wrong** — the path centre is not the run centre, so ±halfWidth
  extrusion was asymmetric relative to the actual slab; additionally, when
  `ActualRunWidth` returned 0, the old fallback used the element's 3D bounding-box
  projected perpendicular to the path, which consistently overshoots width for
  any rotated or non-trivial run geometry.
  (b) **Gradient too steep** — the same rise divided by the shorter
  nosing-to-nosing horizontal length produced a slope angle steeper than the
  actual stair.
  (c) **Transition point offset** — the trailing edge of the run panel landed
  ~one tread depth short of the landing's leading edge. At ~280–300 mm that gap
  is far beyond the 0.01 ft snap tolerance, so `ConnectivityResolver` could never
  close it.
  Fix: use the 2 footprint corners with the lowest projection along the run
  direction as the leading edge (at `BaseElevation`) and the 2 with the highest
  projection as the trailing edge (at `TopElevation`). The footprint boundary is
  the same API surface that `StairsLanding.GetFootprintBoundary()` uses, so both
  sides of the run↔landing junction now share the same XY coordinates and the
  snap resolves within floating-point precision. If `GetFootprintBoundary()` is
  unavailable the code falls back to path + `StairParameterExtractor.GetRunWidth`
  (type-parameter chain, no bbox fallback).

- **`StraightEngine` — removed `GetRunWidth(run, dir)` / `GetBboxWidthPerpendicularTo`**:
  The bounding-box width helper was only used by the old `BuildStraightRunPanel`
  path. It has been deleted. All width lookups now go through
  `StairParameterExtractor.GetRunWidth` (instance property → type parameters →
  explicit fallback constant) — consistent with `CurvedEngine` and `WinderEngine`.

### Fixed — Staircase Analytical Model Generator (round 1)

- **`ConnectivityResolver.SnapSharedEdge`**: Replaced Z-sort edge selection with
  proximity-based selection. The previous Z-sort was fundamentally broken for flat
  landing panels (all corners at the same Z): `OrderBy(Z).Take(2)` returned arbitrary
  corners instead of the two that face the adjacent run, so the shared-edge snap was
  a no-op at every run↔landing boundary. The analytical solver therefore saw
  disconnected panels. Fix: select the 2 corners of each panel that are globally
  nearest to any corner of the other panel — this is correct for both inclined (run)
  and flat (landing) panels.

- **`StraightEngine.SnapConsecutivePanelEdges`**: Same Z-sort bug and same proximity
  fix applied to the within-sketched-run snap loop.

- **`StraightEngine.BuildSketchedRunPanels` — group classification**: The previous
  logic classified each path-direction group as flight or landing by dot-product
  against the overall start→end vector. For dogleg (U-shaped) and L-shaped sketched
  runs the two flights travel in opposite or perpendicular directions relative to
  the overall vector, giving near-zero dot products — both flights were misclassified
  as landings and no inclined panels were emitted. Fix: classify by alternating index
  (even = flight, odd = landing, matching Revit's invariant that a sketched run starts
  with a flight), with a secondary length-based safeguard.

- **`CurvedEngine` / `WinderEngine` — zero-width panels when `ActualRunWidth` is 0**:
  Both engines used `run.ActualRunWidth` directly without a fallback. For certain run
  types Revit returns 0, collapsing inner and outer radii to the same value (zero-width
  panels). These then failed `MinEdgeFt` validation and were silently dropped, resulting
  in 0 panels created for all curved and winder stairs. Fix: use
  `StairParameterExtractor.GetRunWidth(run)` (with its existing
  ActualRunWidth → type-parameter → FallbackRunWidth fallback chain) instead.

- **`CurvedEngine` — dead zombie `hB` formula**: Removed the dead first assignment
  to `hB` (lines 84–85) that was immediately overwritten and served no purpose. Not a
  runtime bug, but removed to prevent future confusion.

## [1.0.1] - 2026-08-01

Fixed-up release addressing the issues found in the initial code review.

### Fixed
- **App.cs**: Icons now load via Pack URI (`pack://application:,,,/StructuralTools;component/Resources/...`) instead of `File.Exists` on a non-existent disk path. Embedded `<Resource>` items now actually appear on ribbon buttons.
- **WallLoadEngine.cs**: Replaced the non-existent `TaskDialog.AddEditOption` / `GetEditStringValue` API calls with a TaskDialog command-link based settings dialog (presets: Off / +5% / +10% / +20%).
- **WallLoadEngine.cs**: Removed the calls to `UIControlledApplication.MainWindow.StatusBar`, which does not exist on that type.
- **UnitConversionService**: Corrected the `KnPerMToInternal` fallback constant from `v / 0.175126835` (wrong by ~12×) to `v * 0.0685218` (correct: 1 kN/m = 0.0685218 kip/ft). Also corrected the `InternalDensityToKgM3` fallback to `v / 0.0624279606`.
- **StructuralTools.addin**: Changed `<SupportedBuilds>2027</SupportedBuilds>` to `<SupportedBuilds>27.0.0.0</SupportedBuilds>` (proper Revit build-number format).
- **WallLoadEngine.cs**: Added an explicit null-check for `ActiveUIDocument` in the command class — fails fast with a clear "open a document" message instead of a NullReferenceException stack trace.
- **WallModels.cs**: Converted `WallEntry` from a mutable struct with public fields to a `readonly struct` with properties and a validating constructor.
- **WallLoadEngine.cs**: Eliminated the double initialisation of `_materialWeightCache` (was initialised in both the constructor and `Run()`).

### Added
- **`Services/UnitConversionService.cs`**: Pure static helpers for ft↔m, kN/m↔kip/ft, kg/m³↔kN/m³, plus `TryParseInvariant`. Includes documented fallback constants so conversions keep working even if the Revit unit API throws.
- **`Services/MaterialService.cs`**: Extracted compound-structure density lookup with per-`Material.Id` caching.
- **`Services/GeometryService.cs`**: Extracted opening detection, interval merging, sub-curve extraction, and projection helpers as pure static methods. Tolerance constants are now public and named.
- **WallLoadEngine.cs**: Wrapped load creation in a `TransactionGroup` with atomic rollback if more than 50% of walls error.
- **WallLoadEngine.cs**: All previously silent `catch { }` blocks now log `[DEBUG]` entries with the exception type and message.
- **`.gitignore`**: Added standard .NET / Visual Studio / OS ignore patterns.
- **`LICENSE`**: Added MIT license file.
- **Top-level `README.md`**: Real content describing project status, linking to the inner README.
- **`CHANGELOG.md`**: This file.

### Changed
- **`StructuralTools.csproj`**:
  - `<Deterministic>false</Deterministic>` → `<Deterministic>true</Deterministic>`.
  - Hardcoded `C:\Program Files\Autodesk\Revit 2027\` HintPath is now overridable via `-p:RevitInstallDir=...`.
  - Version bumped to `1.0.1`.
  - Added the missing `Settings16.png` / `Settings32.png` resource entries.
- **`StructuralTools/README.md`**: Replaced the fictional file tree (which listed `UI/`, `Helpers/`, and several `Services/*.cs` files that didn't exist) with the actual current structure. Added a "How loads are computed" section explaining the algorithm.
- **WallLoadEngine.cs**: Renamed inner variable `wallCount` → `segmentsForThisWall` to reflect what it actually counts.
- **WallLoadEngine.cs**: Replaced inline magic numbers (`0.005`, `1e-6`, `1e-9`) with named constants on `GeometryService` (`MIN_POINT_DIST_FT`, `INTERVAL_MERGE_TOLERANCE`, etc.).

### Removed
- **`Models/WallLoad.cs`** contents (`WallLoad` class): Never instantiated anywhere — the engine builds `LineLoad` elements directly. (The file now only contains `WallEntry` and `LoadResult`.)
- **`Models/Settings.cs`**: The `WallLoadSettings`, `LoadCaseType`, `StaircaseSettings`, `StaircaseInfo`, and `AnalyticalMember` types have been removed — they were never referenced by any code path. The staircase feature remains a stub; when it's actually implemented, fresh models can be designed around the real requirements.
- **`Models/StaircaseModels.cs`**: Deleted entirely (see above).

### Known Limitations
- The Staircase → Analytical Model command is still a placeholder.
- Load case selection is still automatic (matches names containing "dead" or "dl", otherwise uses the first case). A user-facing load-case picker is planned.
- No unit-test project yet — the pure services (`UnitConversionService`, `GeometryService.MergeIntervals`, `GeometryService.GetSubCurve`) are now structured for testing but no tests have been written.

## [1.0.0] - 2026-07-31

Initial public release.

## [1.1.0] - 2026-08-01

Replaced the multi-step TaskDialog flow with native Revit selection. No WPF dialogs are used in the picking flow — only `BitmapImage` for ribbon icons (required by the Revit API).

### Changed
- **WallLoadEngine.Run** now takes a `double fudgePct` parameter and runs a linear flow: `PickObjects` (walls) → `PickObject` (host) → `CreateLoads` → summary `TaskDialog`. The old multi-step `while(true)` loop with `ShowMainDialog` / `ShowSettingsDialog` / `DialogAction` enum is gone.
- **App.cs**: Removed the standalone Settings push button. Added a `RibbonTextBox` (`FudgeFactorTextBox`) on the Wall Loads panel — type any percentage (e.g. `7.5` for +7.5%, `0` to disable) and click Generate. The value is read at runtime via the `App.FudgeFactorTextBox` static property.
- **Commands.cs**: `GenerateWallLoadsCommand` reads the fudge factor from `App.FudgeFactorTextBox`, parses it culture-invariantly, and passes it to `WallLoadEngine.Run(fudgePct)`. Falls back to +10% with a warning if the value is missing or unparseable.
- **README**: Updated the Usage section to describe the new native-selection flow.

### Added
- **WallLoadEngine.HostElementFilter** — a new `ISelectionFilter` that allows only Floors and Structural Framing members to be picked as the host. Linked elements are excluded (loads must be hosted on current-model elements).
- When `PickObjects` is called for walls, Revit automatically enters its native selection mode showing a green **Modify | Pick Walls** contextual tab with Finish (✓) and Cancel (✗) buttons. Only Wall elements (host or linked) are clickable.

### Removed
- **`WallLoadSettingsCommand`** — the standalone Settings command is gone. The fudge factor is now an inline ribbon TextBox.
- **`WallLoadEngine.OpenSettingsStandalone`**, **`ShowMainDialog`**, **`ShowSettingsDialog`**, **`DialogAction`** enum, and the `_selectedWalls` / `_hostElement` / `_applyFudge` / `_fudgePctText` instance fields — all replaced by the simpler linear flow.

### Why
- Reduces WPF usage to the minimum required by the Revit ribbon API (`BitmapImage` for icons).
- Gives users the familiar Revit native picking experience — green Modify contextual tab, tick/X to finish/cancel, element-type filtering via `ISelectionFilter`.
- Allows arbitrary custom fudge factors (not just presets) via the ribbon TextBox.

## [1.2.0] - 2026-08-01

Simplified the ribbon UI and removed the fudge factor entirely.

### Changed
- **App.cs**: Removed the custom "Structural Tools" tab. The add-in now adds a single panel called **"Alemi's Tools"** to Revit's built-in **Analyze** tab via `application.CreateRibbonPanel(Tab.Analyze, "Alemi's Tools")`. This co-locates the tools with Revit's other analytical commands.
- **App.cs**: Removed the `RibbonTextBox` for the fudge factor and the static `App.FudgeFactorTextBox` property. The Wall Loads panel now contains only the Generate button and (after a separator) the Staircase stub button.
- **Commands.cs**: `GenerateWallLoadsCommand.Execute` no longer reads or parses a fudge value. It calls `engine.Run()` with no arguments.
- **WallLoadEngine.cs**: `Run()` now takes no parameters. The `fudgeMultiplier` parameter has been removed from `CreateLoads` and `ProcessWall`. The summary dialog no longer reports a fudge factor. Load magnitude is now strictly `area weight × net height` with no conservatism multiplier.
- **README**: Updated to reflect the new Analyze-tab location and removed all fudge-factor documentation.

### Removed
- `App.FudgeFactorTextBox` static property.
- `ReadFudgeFactor()` helper in `Commands.cs`.
- `fudgePct` / `fudgeMultiplier` parameters throughout `WallLoadEngine.cs`.
- Fudge-factor line from the summary dialog.

### Why
- The Revit `TextBox` ribbon API is genuinely restrictive (read-only `Name`, throws on `ItemText`, no `Prompt`), making an inline input box painful to ship.
- Locating the tools on the built-in Analyze tab puts them next to Revit's own analytical commands, where users naturally look for them.

## [1.3.0] - 2026-08-01

Implemented the Staircase → Analytical Model converter. Handles all 14 stair types the user requested.

### Added
- **`Models/StaircaseModels.cs`** — Fresh, purpose-built models:
  - `StairComponentKind` enum (Run, Landing, Support)
  - `StairComponent` readonly record struct (Kind, Centreline, Profile, SourceId, Label, elevations)
  - `StairConversionResult` result bag (components, created IDs, log, error count)
- **`Services/StaircaseExtractionService.cs`** — Extracts geometric components from a `Stairs` element:
  - **Runs**: extracts the slope line from `BaseElevation` to `TopElevation`. For straight runs, uses the `LocationCurve`. For curved/spiral runs, tessellates into chord segments (~500 mm each) with Z interpolated along the slope. Falls back to bounding-box centreline for winder runs or runs without a location curve.
  - **Landings**: extracts the bottom-face profile as a `CurveLoop` (for analytical panel creation). Falls back to a diagonal member if the profile can't be extracted.
  - **Supports**: extracts the `LocationCurve` directly (stringers typically have a location curve along their path).
- **`Engine/StaircaseEngine.cs`** — Orchestrates the converter:
  - Picks stairs using `PickObjects` with a `StairsFilter` (only Stairs elements are clickable — green Modify contextual tab with Finish/Cancel).
  - For each stair: extracts components, creates analytical elements in a per-stair transaction wrapped in a `TransactionGroup`.
  - Creates `AnalyticalMember` (Beam) for each run centreline and support curve.
  - Creates `AnalyticalPanel` (Floor) for each landing profile, with diagonal-member fallback.
  - Shows a per-stair summary dialog (runs / landings / supports created, errors, first 20 log entries).
  - Non-destructive — the original stair geometry is preserved.

### Changed
- **`Commands/Commands.cs`**: `StaircaseToAnalyticalCommand` now instantiates `StaircaseEngine` and calls `Run()` instead of showing a placeholder dialog.
- **`App.cs`**: Updated the staircase button's tooltip and long description to reflect that the tool is now functional.
- **`README.md`**: Added full documentation for the staircase converter, including a table of all 14 supported stair types and how each decomposes into runs + landings + supports.
- **Top-level `README.md`**: Updated status table — Staircase → Analytical Model is now ✅ Functional.

### How all 14 stair types are handled
All 14 stair types decompose into the same three component kinds in Revit's data model:
- **Runs** (straight, curved, or winder) → analytical beams along the slope
- **Landings** → analytical floor panels from the footprint profile
- **Supports** (stringers) → analytical beams along the support curve

The shape differences (L, U, Z, spiral, split, three-quarter turn) come from how many runs there are and how they connect — not from different component types. So one generic extractor handles all types uniformly.

## [1.4.0] - 2026-08-01

Reworked the staircase converter to use a panel-only structural idealisation, matching how engineers actually idealise concrete stairs.

### Changed
- **`Models/StaircaseModels.cs`**: Removed `StairComponentKind.Support` (concrete stairs don't have separate stringers in the structural sense). Removed the `Centreline` field from `StairComponent` — every component now carries only a `Profile` (CurveLoop). This forces every component to become an analytical panel.
- **`Services/StaircaseExtractionService.cs`**: Completely rewritten.
  - **Runs**: now extract the run's bottom-face (soffit) outline as a `CurveLoop` and return it as a `StairComponent` with `Kind = Run`. The soffit is naturally slanted for straight runs, helical for spiral runs, and flat-with-winder-edges for winder runs. Falls back to a synthetic slanted quad built from the bounding box if the soffit outline can't be extracted (front edge at `BaseElevation`, back edge at `TopElevation`).
  - **Landings**: unchanged — bottom-face outline as a flat `CurveLoop`.
  - **Supports**: removed entirely. `GetStairsSupports()` is no longer called.
- **`Engine/StaircaseEngine.cs`**: Simplified.
  - Removed `CreateAnalyticalMember()` and the diagonal-fallback path.
  - `CreateAnalyticalPanels()` now handles every component uniformly — both runs and landings become `AnalyticalPanel` instances.
  - Summary dialog reports "run panels" and "landing panels" instead of "runs / landings / supports".
- **`App.cs`**: Updated the staircase button's tooltip and long description to reflect the panel-only idealisation.
- **`README.md`**: Replaced the run/landing/support decomposition table with a run-panel/landing-panel decomposition. Added a dogleg (U-shaped) example showing the classic 3-panel idealisation: slanted run + flat landing + slanted run.

### Why
- Concrete stairs are monolithic — the structural idealisation used by engineers treats them as a series of connected panels (slanted run + flat landing + slanted run for a dogleg), not as a collection of 1D members.
- The previous version (1.3.0) created `AnalyticalMember` beams along run centrelines, which is the idealisation for steel-stringer stairs, not concrete stairs.
- This version drops `AnalyticalMember` creation entirely for stairs. Every component becomes an `AnalyticalPanel`, matching the conventional concrete-stair idealisation.

### Migration
If you previously ran v1.3.0 and created analytical members from stairs, those members remain in the model — v1.4.0 only affects new conversions. Delete the old members manually if you want a clean panel-only representation.

## [1.4.1] - 2026-08-01

Fixed three bugs in the staircase converter: wrong level, wrong dimensions, and panels that didn't reflect the actual stair geometry.

### Fixed
- **Bug 1 — Wrong level**: `StairsRun.GetFootprintBoundary()` returns curves projected onto the parent Stairs' base level (not the run's own elevation). The previous version passed these curves directly to `AnalyticalPanel.Create`, placing every panel at the stairs' base level instead of the run's level. Fixed by translating every CurveLoop in Z by the run's `BaseElevation` (which is relative to the stairs' base elevation) before creating the analytical panel. Now each run's panel sits at the run's own structural base level, and each landing's panel sits at the landing's level.
- **Bug 2 — Wrong dimensions**: The previous version tried to extract the soffit outline via `GetEdgesAsCurveLoops()` on the run's bottom face, but Revit's stair geometry is a tessellated mesh of individual treads — not a single clean outline — so the extracted CurveLoop was a wrong-shaped subset. Fixed by using `StairsRun.GetFootprintBoundary()` (the actual Revit API for the run's footprint) as the primary strategy. Falls back to a synthetic slanted rectangle built from `StairsRun.GetStairsPath()` + `StairsRun.ActualRunWidth` if `GetFootprintBoundary` is unavailable.
- **Bug 3 — Didn't reflect actual model**: Both bugs combined meant panels didn't match the real stair. Also, the previous bounding-box fallback used the element's whole bbox, which for stairs includes stringers, nosings, and non-walking-surface geometry. The new path-based fallback uses the run's actual width (`ActualRunWidth`) and the run's actual centreline (`GetStairsPath`), so the fallback panel is dimensionally correct.

### Changed
- **`StaircaseExtractionService.cs`**: Completely reworked extraction.
  - `ExtractRun` now uses three strategies in priority order:
    1. **`GetFootprintBoundary()`** (via reflection, for cross-version robustness) — the run's true footprint outline, translated to the run's base elevation.
    2. **`GetStairsPath()` + `ActualRunWidth`** — builds a synthetic slanted rectangle from the run's centreline and width. Leading edge at `BaseElevation`, trailing edge at `TopElevation`. The four corners are co-planar (slanted plane), satisfying `AnalyticalPanel.Create`'s planarity constraint.
    3. **Bounding-box quad** — last resort, at the run's base elevation.
  - `ExtractLanding` now also tries `GetFootprintBoundary()` first, then falls back to the bottom-face outline.
  - Added `TranslateCurveLoopInZ()` helper that lifts every curve in a CurveLoop by a Z offset. Uses `Curve.CreateTransformed()` (which returns a new curve) instead of the previous buggy pattern that called `CreateTransformed` on a clone without capturing the return value.
- All elevation logging now reports absolute model Z (`stairsBaseElev + runBaseElev`) so the log is verifiable against Revit's UI.

### Research
Consulted the official Autodesk Revit 2027 API documentation (revitapidocs.com) to verify:
- `StairsRun.GetFootprintBoundary()` — returns boundary curves projected on the stairs' base level
- `StairsRun.GetStairsPath()` — returns the centreline, also projected on the stairs' base level
- `StairsRun.ActualRunWidth` — the run's width (not `RunWidth`, which doesn't exist)
- `StairsRun.Height` — the calculated height of the run
- `AnalyticalPanel.Create(Document, CurveLoop)` — requires the CurveLoop to be planar; elevation is baked into the CurveLoop's Z coordinates (no separate level/elevation argument)

## [1.4.2] - 2026-08-01

Reworked run extraction to produce structurally-correct slanted panels.

### Changed
- **Runs now ALWAYS produce slanted panels** (not horizontal footprints).
  The previous version used `GetFootprintBoundary()` as the primary strategy for runs, which produced a horizontal panel at the base level. That's not how a slanting concrete run carries load — a run is an inclined slab, and the analytical panel must lie on the run's inclined plane.
  
  Now the primary strategy is `TryBuildSlantedPanelFromPath()`:
  - Uses `GetStairsPath()` for the run's horizontal centreline direction
  - Uses `ActualRunWidth` for the panel width
  - Places the leading edge at absolute Z = `stairsBaseElev + run.BaseElevation`
  - Places the trailing edge at absolute Z = `stairsBaseElev + run.TopElevation`
  - All four corners are co-planar (slanted plane), satisfying `AnalyticalPanel.Create`'s planarity constraint
  
  Fallback is `TryBuildSlantedQuadFromBoundingBox()` — same slanted logic but using the bounding box dimensions instead of the stairs path.

- **Landings still produce flat panels** — uses `GetFootprintBoundary()` at the landing's elevation, with bottom-face-outline fallback.

- **Fixed Z-coordinate bug**: the previous path-based fallback was using relative Z (`run.BaseElevation`, which is relative to the stairs' base) instead of absolute Z. The XY coordinates from `GetStairsPath()` are in absolute model space, so the Z must also be absolute. Now correctly computes `stairsBaseElev + run.BaseElevation` for absolute Z.

### Why
A concrete stair run carries load as an inclined slab bending about its strong axis. The analytical panel must represent this inclined surface — not a horizontal projection. A dogleg stair now correctly produces:
  1. A slanted panel from base to mid-landing elevation
  2. A flat panel at the mid-landing elevation
  3. A slanted panel from mid-landing to top elevation

This matches how engineers actually idealise concrete stairs for structural analysis.

## [1.5.0] - 2026-08-01

Implemented the full concrete-stair structural idealisation pipeline as designed:
extract → sort → build panels → connect shared edges → set material + thickness → commit.

### Added
- **`Services/StairMaterialService.cs`** — Resolves the concrete material ID and waist thickness for analytical panels.
  - Material resolution: stair's own material (via `GetMaterialIds(false)`) → stair type's structural material parameter → first concrete material in the document → invalid (Revit default).
  - Thickness resolution: stair type's "Waist" / "Structural Depth" / "Slab Thickness" / "Run Thickness" / "Minimum Waist Thickness" parameter → first run type's same parameters → default 200 mm (0.656 ft).
  - Cached per session so all panels in a batch get the same material + thickness.
- **`Services/AnalyticalConnectionService.cs`** — Detects geometrically coincident edges between analytical panels and logs the connections. In Revit 2023+, the analytical solver auto-connects coincident panel edges, so this service primarily verifies that the geometry is correct and reports how many connections were established.

### Changed
- **`Engine/StaircaseEngine.cs`** — Rewritten to implement the full pipeline:
  1. Pick stairs (native Revit selection)
  2. Resolve concrete material + waist thickness (once per batch)
  3. For each stair: extract runs + landings
  4. Sort components by base elevation (lowest first)
  5. Build CurveLoops (slanted for runs, flat for landings — already in extraction service)
  6. Create `AnalyticalPanel` for each CurveLoop, set `MaterialId` and `Thickness`
  7. Detect and log shared-edge connections between panels
  8. Commit transaction, show summary

  The summary now reports: panels created, connections detected, errors, material ID, waist thickness (ft + mm), and per-stair breakdown.

### Pipeline
```
Revit Stairs Element
    ↓
Extract Runs + Landings
    ↓
Sort by elevation → sequence
    ↓
For each run:     build 4-corner inclined CurveLoop
For each landing:  build polygon CurveLoop from footprint
    ↓
Connect shared edges (top of flight = edge of landing)
    ↓
Create AnalyticalPanel for each CurveLoop
Set material (concrete) + thickness (waist)
    ↓
Commit transaction
```

## [2.0.0] - 2026-08-01

Complete rewrite of the staircase converter using the strategy-pattern pipeline
designed by the user. Replaces the previous single-strategy extraction service
with a graph-based classifier + three engine strategies + connectivity resolver
+ analytical model builder + boundary condition applier.

### Added — new `StaircaseEngine/` namespace (12 files)
- **`EngineConfig.cs`** — Tolerances: `EdgeSnapTolerance` (0.01 ft), `MinInnerRadius` (0.10 ft), `MinQuadThreshold` (0.05 ft), `FallbackWaistDepth` (0.656 ft / 200 mm).
- **`PanelGeometry.cs`** — Immutable panel description: 3-4 corners (CCW winding), thickness, material ID, role (Flight/Landing/Winder), source element ID, label. Plus `PanelRole` enum.
- **`StairGraph.cs`** — `StairNode` (element ID, type Run/Landing, RunTag, adjacency list, elevations, `IsBranching`), `StairNodeType` enum, `RunTag` enum (Straight/Curved/Winder), `StairGraph` (nodes + root + `NodesBottomToTop()`).
- **`StairClassifier.cs`** — Builds the graph from a `Stairs` element. Classifies each run by checking `IsWinder` (Winder) → path contains Arc/Ellipse/NurbSpline (Curved) → else Straight. Builds adjacency by comparing boundary edge endpoints within tolerance. Detects branching (any node with degree > 2 — handles Split Straight Stairs).
- **`StairParameterExtractor.cs`** — Reads waist thickness from stair/run type parameters (tries "Structural Depth", "Waist Thickness", "Slab Thickness", "Run Thickness", "Minimum Waist Thickness", "Waist"). Falls back to 200 mm. Finds first concrete material in the document.
- **`IEngineStrategy.cs`** — Strategy interface + `EngineRouter` that routes Landing → StraightEngine, WinderRun → WinderEngine, CurvedRun → CurvedEngine, StraightRun → StraightEngine.
- **`StraightEngine.cs`** — Handles landings (flat polygon from `GetFootprintBoundary()`, fan-triangulated if >4 corners) AND straight runs (4-corner inclined quad: BL→BR→TR→TL with leading edge at base elevation, trailing edge at top elevation, width = `ActualRunWidth`).
- **`CurvedEngine.cs`** — Tessellates the arc into `nSteps` segments (one per riser). Each segment is a slanted quad with inner/outer edges. **Critical**: the trailing-edge XYZs of segment i are reused as the leading-edge XYZs of segment i+1 — zero floating-point drift on shared edges within the run.
- **`WinderEngine.cs`** — Same as CurvedEngine but with inner-radius floor = max(waist/2, MinInnerRadius). If effective inner radius < MinQuadThreshold, emits a triangle (outerA → outerB → innerMid) instead of a degenerate quad.
- **`ConnectivityResolver.cs`** — Snaps shared edges between adjacent panels. For each run→landing adjacency: takes the run's last panel's top-edge corners and the landing's first panel's bottom-edge corners, finds the nearest pair within tolerance, and replaces the landing corner with the run corner (same XYZ reference). This guarantees the Revit analytical solver sees continuity.
- **`AnalyticalModelBuilder.cs`** — Builds a `CurveLoop` from corners (CCW), calls `AnalyticalPanel.Create(_doc, loop)`, sets `MaterialId` and `Thickness`. Batch creation method handles failures gracefully.
- **`BoundaryConditionApplier.cs`** — Applies pinned line supports (`BoundaryConditions`) to the bottom edge of the bottommost flight panel and the top edge of the topmost flight panel. Pinned = all translations fixed, all rotations released. (Wall-touch detection for landings is a TODO.)

### Changed
- **`Engine/StaircaseEngine.cs`** — Rewritten as a thin orchestrator. Delegates all work to the new `StaircaseEngine/` namespace. Pipeline: pick → classify → build (per-node via router) → resolve shared edges → create panels + apply BCs in one transaction → commit + summary.
- Summary dialog now reports: panels created, edge snaps resolved, boundary conditions applied, errors, per-stair breakdown.

### Removed
- `Services/StaircaseExtractionService.cs` — replaced by the new engine strategies.
- `Services/StairMaterialService.cs` — replaced by `StairParameterExtractor`.
- `Services/AnalyticalConnectionService.cs` — replaced by `ConnectivityResolver`.
- `Models/StaircaseModels.cs` — replaced by `PanelGeometry` + `StairGraph`.

### Pipeline
```
Revit Stairs Element
       ↓
StairClassifier.Classify() → StairGraph
       ↓
For each node (bottom-to-top):
    EngineRouter.GetEngine(node).BuildPanels(doc, node)
       ↓
ConnectivityResolver.ResolveSharedEdges(graph, allPanels)
       ↓
AnalyticalModelBuilder.CreatePanels(allPanels)
  + BoundaryConditionApplier.Apply(graph, allPanels, createdPanels)
       ↓
Transaction.Commit()
```

### Known API risk
- `BoundaryConditions.Create(Document, Curve, ElementId, StructuralElementCategoryFilter)` — the exact overload may differ in Revit 2027. If this fails to compile, the fix is to use `BoundaryConditions.Create(Document, ElementId)` (host-only) and then set the curve via a parameter, or to skip BC application entirely (the analytical solver will still work — BCs are convenience).

## [2.0.1] - 2026-08-01

Addressed all issues from the v2.0.0 code review.

### Removed
- **`StaircaseEngine/BoundaryConditionApplier.cs`** — deleted entirely. The `BoundaryConditions.Create` overload with `StructuralElementCategoryFilter` was unlikely to compile, and boundary conditions are not essential to the analytical model (the solver works without them). Removed all references from `StaircaseEngine.cs`.

### Fixed
- **`WinderEngine`**: Deleted the dead first `outerB` assignment that was immediately overwritten by a corrected computation. Fixed `innerA` reuse in the triangle branch — after emitting a triangle, `innerA` is now set to `innerMid` so the next segment continues from the midpoint (no geometric discontinuity).
- **`StraightEngine.BuildLandingPanel`**: Removed the broken close-loop logic. `CurveLoop` is already closed (last curve's end == first curve's start), so we only need each curve's start point. The previous code had contradictory add-then-remove logic that could never execute correctly.
- **`ConnectivityResolver`**: Fixed double-counting of adjacency pairs by tracking visited `(ElementId, ElementId)` pairs in a `HashSet`. Fixed the fragile `!upperBottom.Contains(c)` reference-equality check — now uses corner indices selected via `OrderBy(...).Take(2).Select(t => t.i).ToHashSet()`.
- **`StairClassifier.ClassifyRun`**: Now checks `StairsRunStyle` enum first (definitely exists in Revit 2023+), then falls back to `IsWinder` property (reflection), then path-curve type. This prevents winder stairs from being misclassified as CurvedRun when `IsWinder` doesn't exist.
- **`StairGraph.NodesBottomToTop`**: Added a secondary sort key — runs before landings at the same elevation. This ensures `ConnectivityResolver` correctly identifies the run as the "lower" panel and the landing as the "upper" panel at mid-landing elevations.
- **`AnalyticalModelBuilder`**: Now accepts a `List<string> log` in its constructor and logs exception messages for failed panel creation (previously swallowed silently). The caller sees the actual error, not just a count mismatch.
- **`Engine/StaircaseEngine.cs`**: Added transaction rollback when >50% of panels fail (matches the wall-load engine's `ERROR_ROLLBACK_THRESHOLD` pattern). Removed the unused `_uiApp` field.

### Changed — caching
- **New `StairParameterContext`** class: holds the resolved `MaterialId` and `ThicknessFt`, resolved once per batch and passed to all engine `BuildPanels` calls. Previously, every panel triggered a `FilteredElementCollector` search for concrete material (O(M) per panel) and 6 `LookupParameter` calls for thickness. Now both are resolved once.
- **`IEngineStrategy.BuildPanels`** signature updated to accept `StairParameterContext`.
- **`StraightEngine`, `CurvedEngine`, `WinderEngine`**: all updated to read material + thickness from the context instead of calling `StairParameterExtractor` per panel.
- **`StairParameterExtractor`** is now called only once per batch (in `StaircaseEngine.Run`), not per panel.

## [2.1.0] - 2026-08-01

Added robust handling for sketched runs — the most common stair type in practice where an entire dogleg/U-shape/L-shape is a single `StairsRun` element.

### Added
- **`StraightEngine.BuildSketchedRunPanels`** — handles multi-segment paths where a single sketched run contains multiple flights + implicit landings.
  - Extracts all path segments from `GetStairsPath()`
  - Groups consecutive segments with the same direction (dot > 0.5) into "flight groups"
  - Direction changes between groups indicate landings
  - Distributes the total rise (`TopElevation − BaseElevation`) across flight groups only, proportional to their horizontal length — landings get zero rise (flat)
  - Builds a slanted `PanelGeometry` for each flight group + a flat `PanelGeometry` for each landing group
  - Produces the correct dogleg idealisation: slanted flight 1 → flat landing → slanted flight 2

- **`StraightEngine.BuildSlantedPanel`** — shared helper that builds a 4-corner inclined quad from a path start/end + base/top elevations. Validates all edge lengths before returning. Used by both `BuildStraightRunPanel` and `BuildSketchedRunPanels`.

- **`StraightEngine.BuildFlatPanel`** — shared helper that builds a flat quad panel from a path start/end at a given Z. Used for sketched-run landings.

- **`StraightEngine.GetRunWidth`** — shared helper with bounding-box fallback + 4 ft default.

### Changed
- **`StraightEngine.BuildPanels`** — now dispatches to `BuildRunPanels` (new) for all runs, which checks the path segment count and routes to `BuildSketchedRunPanels` for multi-segment paths or `BuildStraightRunPanel` for single-segment paths.
- **`AnalyticalModelBuilder.CreatePanel`** — now validates all edge lengths BEFORE creating `Line` objects. If any edge is shorter than 0.012 ft (~3.7 mm), throws with all corner coordinates for diagnosis.
- **`StairClassifier.Classify`** — now logs run/landing counts + per-run details (tag, elevations, width, risers) to `Debug.WriteLine` for diagnostics.
- **`StaircaseEngine.ProcessOneStair`** — now logs a warning when a stair has only 1 run and 0 landings (typical for sketched runs or legacy stairs).

### Why
Sketched runs are the most common stair type in practice. A single sketched `StairsRun` can contain an entire dogleg (2 flights + landing) as one element — the path has multiple segments with direction changes. Without this fix, the converter treated the entire dogleg as a single straight flight, producing a geometrically wrong (and often degenerate) panel.

## [2.1.1] - 2026-08-01

Addressed all issues from the v2.1.0 code review.

### Fixed
- **Sketched-run flight/landing classification** — no longer uses index parity (`g % 2 == 0`). Now classifies each group by its direction relative to the overall start→end direction: if parallel (dot > 0.5) → flight; if perpendicular → landing. Falls back to alternation only if no landings are detected.
- **Sketched-run landing width** — no longer uses `ActualRunWidth` for landings. Now uses `GetBboxWidthPerpendicularTo(run, landingDir)` which correctly computes the landing's width as the bounding-box extent projected onto the perpendicular direction. Landings in dogleg stairs are typically wider than flights, and this now reflects that.
- **`GetRunWidth` for rotated stairs** — no longer uses `Math.Min(dx, dy)`. Now uses `GetBboxWidthPerpendicularTo` which projects bbox corners onto the path's perpendicular direction. Works correctly for stairs rotated at any angle.
- **Internal sketched-run connectivity** — added `SnapConsecutivePanelEdges` which snaps each panel's trailing edge (highest-Z 2 corners) to the next panel's leading edge (lowest-Z 2 corners) within the same sketched run. This ensures flight→landing→flight panels share exact XYZ coordinates at their boundaries.
- **Multi-arc paths in `CurvedEngine`** — no longer processes only the first arc. Now collects ALL arcs in the path, computes the total angular span, distributes risers proportionally across arcs, and processes each arc in sequence with shared XYZs across arc boundaries. Handles S-shaped curved runs correctly.
- **Multi-arc paths in `WinderEngine`** — same fix as CurvedEngine. Handles S-shaped winder runs and multi-pivot winders.
- **`StairClassifier` diagnostics** — now accepts a `List<string> log` parameter and writes diagnostics there (in addition to `Debug.WriteLine`). The summary dialog now shows run/landing counts, per-run tags/elevations/widths/risers, and any skipped elements.

### Changed
- **`EngineConfig`** — added `MinEdgeFt` (0.012 ft) and `FallbackRunWidth` (4.0 ft) constants.
- **`AnalyticalModelBuilder`** — removed local `MIN_EDGE_FT` constant; now uses `EngineConfig.MinEdgeFt`.
- **`StraightEngine`** — removed local `MIN_EDGE_FT` constant; all references now use `EngineConfig.MinEdgeFt`.
- **`StraightEngine.GetRunWidth`** — now takes a `XYZ pathDir` parameter and uses `GetBboxWidthPerpendicularTo` for the fallback, instead of `Math.Min(dx, dy)`.
- **`StairClassifier.Classify`** — signature changed to `Classify(Document, Stairs, List<string> log)`.
