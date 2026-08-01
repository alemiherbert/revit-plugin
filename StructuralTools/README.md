# Structural Tools for Revit 2027

A native Revit 2027 add-in providing structural analysis tools with a clean, integrated ribbon interface.

## Features

### Wall → Line Load Generator ✅
Automatically generates analytical line loads on a host beam or floor from wall elements:

- **Host or linked models** — pick walls from the current model or from linked Revit models (transforms applied correctly).
- **Material-aware density** — reads each compound-structure layer's structural asset density, falling back to a configurable concrete default.
- **Opening subtraction** — doors, windows, and other inserts are projected onto the wall's location curve and removed from the load profile.
- **Sub-segment generation** — walls are split at opening boundaries so each segment carries its own net-height load.
- **Host projection** — loads are projected onto the host beam's analytical curve or the host floor's analytical surface plane.
- **Atomic rollback** — if more than 50% of walls error, the entire transaction is rolled back so the model isn't left in a half-baked state.

### Staircase → Analytical Model ✅
Converts staircase elements into analytical **panels** (concrete-stair idealisation):

- **Panel-only idealisation** — every component becomes an `AnalyticalPanel`. No analytical members are created. Concrete stairs are wholly panel structures.
- **Runs → slanted analytical panels** — each run becomes one slanted panel: the run's bottom-face (soffit) outline, lifted so the leading edge sits at the run's `BaseElevation` and the trailing edge at its `TopElevation`. Falls back to a synthetic slanted quad from the bounding box if the soffit outline can't be extracted.
- **Landings → flat analytical panels** — each landing's bottom-face outline becomes a flat `AnalyticalPanel`.
- **Dogleg (U-shaped) example** — produces 3 panels: slanted run + flat landing + slanted run, exactly matching the conventional structural idealisation.
- **All stair types supported** — straight, L-shaped, U-shaped, Z-shaped, spiral, winder, split, three-quarter turn. Each type simply produces a different combination of slanted run panels + flat landing panels.
- **Non-destructive** — the original stair geometry is preserved. Analytical panels are created alongside the physical stair.
- **Native selection** — uses Revit's green Modify contextual tab with Finish/Cancel. Only Stairs elements are clickable.

#### How each stair type decomposes into panels

| Type | Run panels | Landing panels | Notes |
|---|---|---|---|
| Egress Stairs - Single | 1 slanted | 0 | Single run |
| Straight Stairs | 1 slanted | 0 | Single run |
| Straight Stairs - Landing | 2 slanted | 1 | Mid-landing |
| Split Straight Stairs | 3+ slanted | 1+ | Bifurcated |
| L-Shaped Straight Stairs | 2 slanted | 1 | 90° turn |
| L-Shaped Curved Stairs | 2 slanted | 1 | 90° turn, curved runs |
| L-Shaped Winder Stairs | 2 slanted | 0 | 90° turn, winder treads |
| U-Shaped (Dogleg) Straight Stairs | 2 slanted | 1 | 180° turn — classic 3-panel idealisation |
| U-Shaped Straight Stairs - Landing | 2 slanted | 1 | 180° turn |
| U-Shaped Curved Stairs | 2 slanted | 1 | 180° turn, curved runs |
| U-Shaped Winder Stairs | 2 slanted | 0 | 180° turn, winder treads |
| Z-Shaped Winder Stairs | 2 slanted | 0 | Offset turn, winder treads |
| Three-Quarter Turn Straight Stairs | 3 slanted | 2 | 270° turn |
| Spiral Stairs - Open Risers | 1 slanted (curved) | 0 | Helical soffit outline |

## Architecture

```
StructuralTools/
├── App.cs                          # IExternalApplication — ribbon setup (Analyze tab → Alemi's Tools)
├── Commands/
│   └── Commands.cs                 # IExternalCommand implementations
├── Engine/
│   ├── WallLoadEngine.cs           # Wall-load orchestrator (selection, load creation)
│   └── StaircaseEngine.cs          # Thin orchestrator: pick → classify → build → connect → create → BCs → commit
├── Models/
│   └── WallModels.cs               # WallEntry (readonly struct), LoadResult
├── Services/                       # Wall-load services
│   ├── UnitConversionService.cs    # Pure ft↔m, kN/m↔kip/ft, kg/m³↔kN/m³ helpers
│   ├── MaterialService.cs          # Compound-structure density lookup with caching
│   └── GeometryService.cs          # Opening detection, interval merge, sub-curve extraction, projection
├── StaircaseEngine/                # Staircase converter — strategy-based pipeline
│   ├── EngineConfig.cs             # Tolerances and fallback constants
│   ├── PanelGeometry.cs            # PanelGeometry (corners, thickness, material, role), PanelRole enum
│   ├── StairGraph.cs               # StairNode, StairNodeType, RunTag, StairGraph (topology)
│   ├── StairClassifier.cs          # Build graph, classify runs (Straight/Curved/Winder), detect branching
│   ├── StairParameterExtractor.cs  # Read waist thickness + concrete material from stair type
│   ├── StairParameterContext.cs    # Cached material + thickness passed to all engines
│   ├── IEngineStrategy.cs          # Strategy interface + EngineRouter
│   ├── StraightEngine.cs           # Landings + straight runs (4-corner inclined quad)
│   ├── CurvedEngine.cs             # Arc tessellation with shared XYZs (zero drift between segments)
│   ├── WinderEngine.cs             # Triangle/quad mix based on inner radius
│   ├── ConnectivityResolver.cs     # Snap shared edges between adjacent panels
│   └── AnalyticalModelBuilder.cs   # Build CurveLoop, create AnalyticalPanel, set material+thickness
└── Resources/
    └── *.png                       # Ribbon icons (embedded WPF resources)
```

## Requirements

- **Revit 2027** (targets .NET 10)
- Windows 10/11
- Visual Studio 2022+ or .NET 10 SDK

## Build

```bash
# Default — assumes Revit is installed at C:\Program Files\Autodesk\Revit 2027
dotnet build --configuration Release

# Override the Revit install path
dotnet build -p:RevitInstallDir="D:\Autodesk\Revit 2027" --configuration Release
```

The post-build event copies `StructuralTools.dll` and `StructuralTools.addin` into
`%APPDATA%\Autodesk\REVIT\Addins\2027\` if that folder exists. On machines without
Revit installed, the build still succeeds — the copy is silently skipped.

## Install

1. Build the project (see above).
2. Verify these files exist in `%APPDATA%\Autodesk\REVIT\Addins\2027\`:
   - `StructuralTools.addin`
   - `StructuralTools\StructuralTools.dll`
   - `StructuralTools\Resources\*.png` *(not required — icons are embedded in the DLL)*
3. Restart Revit. An **Alemi's Tools** panel will appear on Revit's built-in **Analyze** tab.

## Usage

### Wall Load Generator

1. Click **Analyze → Alemi's Tools → Generate Wall Loads**.
2. Revit enters its native wall-selection mode:
   - A green **Modify | Pick Walls** contextual tab appears.
   - Only Wall elements (host model or linked) are clickable — everything else is greyed out.
   - Click or box-select walls.
   - Press **Finish (✓)** to confirm, or **Cancel (✗)** to abort.
3. Revit then enters host-selection mode:
   - Only Floors and Structural Framing members (beams) are clickable.
   - Pick the host beam or floor slab.
   - Press **Finish (✓)** or **Cancel (✗)**.
4. Loads are created in a single transaction. A summary dialog shows how many segments were created, any errors, and the first 20 log entries.

### Staircase Converter

1. Click **Analyze → Alemi's Tools → Staircase To Analytical**.
2. Revit enters its native stair-selection mode:
   - A green **Modify** contextual tab appears.
   - Only Stairs elements are clickable — everything else is greyed out.
   - Click or box-select stairs.
   - Press **Finish (✓)** to confirm, or **Cancel (✗)** to abort.
3. For each stair, the converter:
   - Extracts all runs and landings.
   - Creates one slanted `AnalyticalPanel` for each run (soffit outline, leading edge at base elevation, trailing edge at top elevation).
   - Creates one flat `AnalyticalPanel` for each landing (bottom-face outline).
4. A summary dialog shows per-stair breakdown (run panels / landing panels created) and any errors.
5. The original stair geometry is **preserved** — analytical panels are created alongside it.

#### Concrete-stair idealisation

Concrete stairs are idealised as wholly panel structures — no analytical members are created. A dogleg (U-shaped) stair therefore produces three panels:

```
   slanted         flat          slanted
   run panel  →  landing panel  →  run panel
     /\         __________         /\
    /  \       /          \       /  \
   /    \     /            \     /    \
  base  mid                 mid   top
```

This matches the conventional structural idealisation used for analysis of cast-in-place concrete stairs.

## How loads are computed

For each wall:

1. **Area weight** (kN/m²) = Σ (layer thickness × layer density) across the wall type's compound structure.
2. **Effective height** (m) is determined in this order:
   - `WALL_USER_HEIGHT_PARAM`
   - Largest solid's bounding-box height (from wall geometry)
   - Element bounding-box height
3. **Openings** (doors, windows, etc.) are projected onto the wall's location curve. Their height is clipped to the wall's Z range. Overlapping openings are merged.
4. The wall is split into sub-segments at opening boundaries. For each sub-segment:
   - **Net height** = wall height − Σ opening heights overlapping this segment.
   - **Load magnitude** (kN/m) = area weight × net height.
5. Each sub-segment is projected onto the host element's analytical curve (beam) or surface plane (floor), and a `LineLoad` is created.

## Logging

The summary dialog shows the first 20 log entries. All `[DEBUG]` entries are also written
to `System.Diagnostics.Debug` — capture them with DebugView or by attaching a debugger.

## Development

### Debugging

The post-build event copies output to the Revit AddIns folder. After building, launch
Revit, then in Visual Studio use **Debug → Attach to Process → Revit.exe**.

### Unit-testing the services

`UnitConversionService`, `GeometryService.MergeIntervals`, and `GeometryService.GetSubCurve`
are pure and have no Revit dependency — they can be unit-tested directly by referencing
the assembly from a test project. (A test project is not yet included — contributions welcome.)

## License

MIT — see the top-level [`LICENSE`](../LICENSE).

## Support

For issues and feature requests, please file an issue on the project repository.
