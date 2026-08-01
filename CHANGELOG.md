# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Removed
- **Staircase → Analytical Model** feature removed entirely: `StaircaseEngine/` directory,
  `SketchEngine/` directory, `Engine/StaircaseEngine.cs`, `Commands.StaircaseToAnalyticalCommand`,
  the `App.cs` ribbon button and separator, and the associated icon resources
  (`Staircase16.png`, `Staircase32.png`). Only the Wall Load Generator remains.

## [1.2.0] - 2026-08-01

Simplified the ribbon UI and removed the fudge factor entirely.

### Changed
- **App.cs**: Removed the custom "Structural Tools" tab. The add-in now adds a single
  panel called **"Alemi's Tools"** to Revit's built-in **Analyze** tab via
  `application.CreateRibbonPanel(Tab.Analyze, "Alemi's Tools")`. This co-locates the
  tools with Revit's other analytical commands.
- **Commands.cs**: `GenerateWallLoadsCommand.Execute` no longer reads or parses a fudge
  value. It calls `engine.Run()` with no arguments.
- **WallLoadEngine.cs**: `Run()` now takes no parameters. The `fudgeMultiplier` parameter
  has been removed from `CreateLoads` and `ProcessWall`. Load magnitude is now strictly
  `area weight × net height` with no conservatism multiplier.
- **README**: Updated to reflect the new Analyze-tab location and removed all
  fudge-factor documentation.

### Removed
- `App.FudgeFactorTextBox` static property.
- `ReadFudgeFactor()` helper in `Commands.cs`.
- `fudgePct` / `fudgeMultiplier` parameters throughout `WallLoadEngine.cs`.
- Fudge-factor line from the summary dialog.

### Why
- The Revit `TextBox` ribbon API is genuinely restrictive (read-only `Name`, throws on
  `ItemText`, no `Prompt`), making an inline input box painful to ship.
- Locating the tools on the built-in Analyze tab puts them next to Revit's own analytical
  commands, where users naturally look for them.

## [1.1.0] - 2026-08-01

Replaced the multi-step TaskDialog flow with native Revit selection.

### Changed
- **WallLoadEngine.Run** now takes a `double fudgePct` parameter and runs a linear flow:
  `PickObjects` (walls) → `PickObject` (host) → `CreateLoads` → summary `TaskDialog`.
  The old multi-step `while(true)` loop with `ShowMainDialog` / `ShowSettingsDialog` /
  `DialogAction` enum is gone.
- **App.cs**: Removed the standalone Settings push button. Added a `RibbonTextBox`
  (`FudgeFactorTextBox`) on the Wall Loads panel — type any percentage (e.g. `7.5` for
  +7.5%, `0` to disable) and click Generate.
- **README**: Updated the Usage section to describe the new native-selection flow.

### Added
- **WallLoadEngine.HostElementFilter** — a new `ISelectionFilter` that allows only Floors
  and Structural Framing members to be picked as the host. Linked elements are excluded.

### Removed
- **`WallLoadSettingsCommand`** — the standalone Settings command is gone. The fudge
  factor is now an inline ribbon TextBox.
- **`WallLoadEngine.OpenSettingsStandalone`**, **`ShowMainDialog`**, **`ShowSettingsDialog`**,
  **`DialogAction`** enum, and the `_selectedWalls` / `_hostElement` / `_applyFudge` /
  `_fudgePctText` instance fields.

## [1.0.1] - 2026-08-01

Fixed-up release addressing the issues found in the initial code review.

### Fixed
- **App.cs**: Icons now load via Pack URI
  (`pack://application:,,,/StructuralTools;component/Resources/...`) instead of
  `File.Exists` on a non-existent disk path. Embedded `<Resource>` items now actually
  appear on ribbon buttons.
- **WallLoadEngine.cs**: Replaced the non-existent `TaskDialog.AddEditOption` /
  `GetEditStringValue` API calls with a TaskDialog command-link based settings dialog
  (presets: Off / +5% / +10% / +20%).
- **WallLoadEngine.cs**: Removed the calls to
  `UIControlledApplication.MainWindow.StatusBar`, which does not exist on that type.
- **UnitConversionService**: Corrected the `KnPerMToInternal` fallback constant from
  `v / 0.175126835` (wrong by ~12×) to `v * 0.0685218` (correct: 1 kN/m = 0.0685218
  kip/ft). Also corrected the `InternalDensityToKgM3` fallback to `v / 0.0624279606`.
- **StructuralTools.addin**: Changed `<SupportedBuilds>2027</SupportedBuilds>` to
  `<SupportedBuilds>27.0.0.0</SupportedBuilds>` (proper Revit build-number format).
- **WallLoadEngine.cs**: Added an explicit null-check for `ActiveUIDocument` — fails fast
  with a clear "open a document" message instead of a NullReferenceException stack trace.
- **WallModels.cs**: Converted `WallEntry` from a mutable struct with public fields to a
  `readonly struct` with properties and a validating constructor.
- **WallLoadEngine.cs**: Eliminated the double initialisation of `_materialWeightCache`.

### Added
- **`Services/UnitConversionService.cs`**: Pure static helpers for ft↔m, kN/m↔kip/ft,
  kg/m³↔kN/m³, plus `TryParseInvariant`. Includes documented fallback constants so
  conversions keep working even if the Revit unit API throws.
- **`Services/MaterialService.cs`**: Extracted compound-structure density lookup with
  per-`Material.Id` caching.
- **`Services/GeometryService.cs`**: Extracted opening detection, interval merging,
  sub-curve extraction, and projection helpers as pure static methods. Tolerance constants
  are now public and named.
- **WallLoadEngine.cs**: Wrapped load creation in a `TransactionGroup` with atomic
  rollback if more than 50% of walls error.
- **WallLoadEngine.cs**: All previously silent `catch { }` blocks now log `[DEBUG]`
  entries with the exception type and message.
- **`.gitignore`**: Added standard .NET / Visual Studio / OS ignore patterns.
- **`LICENSE`**: Added MIT license file.
- **Top-level `README.md`**: Real content describing project status, linking to the inner
  README.
- **`CHANGELOG.md`**: This file.

### Changed
- **`StructuralTools.csproj`**:
  - `<Deterministic>false</Deterministic>` → `<Deterministic>true</Deterministic>`.
  - Hardcoded `C:\Program Files\Autodesk\Revit 2027\` HintPath is now overridable via
    `-p:RevitInstallDir=...`.
  - Version bumped to `1.0.1`.
  - Added the missing `Settings16.png` / `Settings32.png` resource entries.
- **`StructuralTools/README.md`**: Replaced the fictional file tree with the actual
  current structure. Added a "How loads are computed" section explaining the algorithm.
- **WallLoadEngine.cs**: Renamed inner variable `wallCount` → `segmentsForThisWall` to
  reflect what it actually counts.
- **WallLoadEngine.cs**: Replaced inline magic numbers (`0.005`, `1e-6`, `1e-9`) with
  named constants on `GeometryService` (`MIN_POINT_DIST_FT`, `INTERVAL_MERGE_TOLERANCE`,
  etc.).

### Removed
- **`Models/WallLoad.cs`** contents (`WallLoad` class): Never instantiated anywhere — the
  engine builds `LineLoad` elements directly.
- **`Models/Settings.cs`**: The `WallLoadSettings`, `LoadCaseType`, and related types
  were never referenced by any code path. Removed.
- **`Models/StaircaseModels.cs`**: Deleted entirely — staircase models were placeholders
  and never wired up.

### Known Limitations
- Load case selection is automatic (matches names containing "dead" or "dl", otherwise
  uses the first case). A user-facing load-case picker is planned.
- No unit-test project yet — the pure services (`UnitConversionService`,
  `GeometryService.MergeIntervals`, `GeometryService.GetSubCurve`) are structured for
  testing but no tests have been written.

## [1.0.0] - 2026-07-31

Initial public release.
