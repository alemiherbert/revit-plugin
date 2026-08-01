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

## Architecture

```
StructuralTools/
├── App.cs                          # IExternalApplication — ribbon setup (Analyze tab → Alemi's Tools)
├── Commands/
│   └── Commands.cs                 # IExternalCommand implementations
├── Engine/
│   └── WallLoadEngine.cs           # Wall-load orchestrator (selection, load creation)
├── Models/
│   └── WallModels.cs               # WallEntry (readonly struct), LoadResult
├── Services/                       # Wall-load services
│   ├── UnitConversionService.cs    # Pure ft↔m, kN/m↔kip/ft, kg/m³↔kN/m³ helpers
│   ├── MaterialService.cs          # Compound-structure density lookup with caching
│   └── GeometryService.cs          # Opening detection, interval merge, sub-curve extraction, projection
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
2. **Pass 1 — walls in this model**:
   - A green **Modify** contextual tab appears.
   - Only Wall elements in the current document are clickable.
   - Click or box-select walls. Press **Finish (✓)** when done.
   - Press **Cancel (✗)** to abort the command entirely.
   - Press **Finish (✓)** with nothing selected to skip this pass (linked-walls-only workflow).
3. **Pass 2 — walls in linked models** (optional):
   - A second green **Modify** contextual tab appears.
   - Click or box-select walls inside linked models. Press **Finish (✓)** when done.
   - Press **Cancel (✗)** to skip — no linked walls will be included. This is not an error.
4. Revit then enters host-selection mode:
   - Only Floors and Structural Framing members (beams) are clickable — everything else is greyed out.
   - Pick the host beam or floor slab. Press **Finish (✓)** or **Cancel (✗)**.
5. Loads are created in a single transaction. A summary dialog shows how many segments were created, any errors, and the first 20 log entries.

> **Why two passes?** Revit's `ObjectType.LinkedElement` picker — required for selecting elements inside linked models — does not expose host-document elements for selection. `ObjectType.Element` is used for the host pass and `ObjectType.LinkedElement` for the linked pass. Duplicate walls picked in both passes are silently ignored.

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
