# Structural Tools for Revit 2027

A native Revit 2027 add-in providing structural analysis tools with a clean, integrated ribbon interface.

## Features

### Wall to Line Load Generator
Automatically generates analytical line loads on floors from wall elements:
- **Smart filtering**: Filter by structural/architectural walls, curtain walls, demolished elements
- **Material-aware**: Uses material density or custom override values
- **Load merging**: Combines coincident loads within configurable tolerance
- **Linked model support**: Process walls from linked Revit models
- **Multiple load cases**: Dead Load, Super Dead, Live, Partition, Wind, Seismic

### Staircase to Analytical Model
Convert staircase elements into analytical model components:
- **Component selection**: Stringers, treads, and landings
- **Material assignment**: Steel, concrete, or wood analytical members
- **Geometry preservation**: Option to keep original geometry

## Architecture

```
StructuralTools/
├── App.cs                      # IExternalApplication - Ribbon startup
├── Commands/
│   └── Commands.cs             # IExternalCommand implementations
├── Models/
│   ├── Settings.cs             # WallLoadSettings, StaircaseSettings
│   ├── WallModels.cs           # WallInfo, WallLoad
│   └── StaircaseModels.cs      # StaircaseInfo, AnalyticalMember
├── Services/
│   ├── WallService.cs          # Wall collection and filtering
│   ├── LoadCreationService.cs  # Load calculation and creation
│   ├── MaterialService.cs      # Material density lookup
│   ├── GeometryService.cs      # Geometry calculations
│   ├── UnitConversionService.cs # Unit conversions
│   └── LoggingService.cs       # File-based logging
├── UI/
│   ├── WallLoadGeneratorWindow.cs
│   ├── WallLoadSettingsWindow.cs
│   ├── StaircaseToAnalyticalWindow.cs
│   └── ProgressWindow.cs
├── Helpers/
│   └── RevitExtensions.cs      # Extension methods
└── Resources/
    └── (icons)
```

## Requirements

- **Revit 2027** (targets .NET 10)
- Windows 10/11
- Visual Studio 2022+ or .NET 10 SDK

## Installation

1. **Build the project**:
   ```bash
   dotnet build --configuration Release
   ```

2. **Update Revit API paths** in `StructuralTools.csproj` if your Revit installation is in a different location.

3. **Add icon resources** to the `Resources/` folder:
   - `Generate32.png`, `Generate16.png` - Wall load generator icons
   - `Staircase32.png`, `Staircase16.png` - Staircase converter icons
   - `Logo.png` - Settings/about icon

4. **Deploy the add-in**:
   - Copy `StructuralTools.dll` to `%APPDATA%\Autodesk\REVIT\Addins\2027\StructuralTools\`
   - Copy `StructuralTools.addin` to `%APPDATA%\Autodesk\REVIT\Addins\2027\`

5. **Restart Revit** - The "Structural Tools" tab will appear in the ribbon.

## Usage

### Wall Load Generator
1. Click **Structural Tools** → **Wall Loads** → **Generate**
2. Configure settings:
   - Select load case type
   - Choose wall types to include
   - Set density (material-based or override)
   - Adjust merge tolerance
3. Click **Generate** to process walls
4. Review results in the progress window

### Staircase Converter
1. Click **Structural Tools** → **Staircase** → **To Analytical**
2. Select stairs (selected or all)
3. Choose components to convert
4. Set analytical material
5. Click **Convert**

## Logging

Logs are written to `%APPDATA%\StructuralTools\Logs\` with daily rotation. Access via:
- Settings window → Advanced → Open Log Folder

## Development

### Building from Source

```bash
cd StructuralTools
dotnet restore
dotnet build
```

### Debugging

The post-build event automatically copies output to the Revit AddIns folder. Attach debugger to `Revit.exe` after launching Revit.

## License

MIT License

## Support

For issues and feature requests, please file an issue on the project repository.
