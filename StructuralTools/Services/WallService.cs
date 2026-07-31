using Autodesk.Revit.DB;

namespace StructuralTools.Services;

/// <summary>
/// Service for collecting and processing wall elements.
/// </summary>
public class WallService
{
    private readonly Document _doc;
    private readonly Models.WallLoadSettings _settings;

    public WallService(Document doc, Models.WallLoadSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    /// <summary>
    /// Collects all walls matching the current settings.
    /// </summary>
    public List<Models.WallInfo> CollectWalls()
    {
        var walls = new List<Models.WallInfo>();
        
        // Collect walls from main document
        var collector = new FilteredElementCollector(_doc);
        var wallClass = new ElementClassFilter(typeof(Wall));
        
        if (_settings.IgnoreDemolished)
        {
            var phaseFilter = new PhaseFilter(_doc.ActiveView.GenPhaseFilterId);
            collector.WherePasses(phaseFilter);
        }
        
        foreach (Wall wall in collector.WherePasses(wallClass).ToElements())
        {
            if (ShouldIncludeWall(wall))
            {
                walls.Add(ExtractWallInfo(wall));
            }
        }
        
        // Optionally collect from linked models
        if (_settings.IncludeLinkedModels)
        {
            walls.AddRange(CollectFromLinks());
        }
        
        return walls;
    }

    /// <summary>
    /// Determines if a wall should be included based on settings.
    /// </summary>
    private bool ShouldIncludeWall(Wall wall)
    {
        // Check structural flag
        if (!_settings.IncludeStructural && wall.StructuralUsage != StructuralUsage.NonStructural)
            return false;
            
        if (!_settings.IncludeArchitectural && wall.StructuralUsage == StructuralUsage.NonStructural)
            return false;
        
        // Check curtain wall
        if (_settings.IgnoreCurtainWalls && wall.WallType.IsCurtainWall)
            return false;
        
        // Check demolished
        if (_settings.IgnoreDemolished)
        {
            var phaseCreated = wall.CreatedPhaseId;
            var phaseDemolished = wall.DemolishedPhaseId;
            var activePhase = _doc.ActiveView.Phase;
            
            if (phaseDemolished != null && phaseDemolished <= activePhase?.Id)
                return false;
        }
        
        return true;
    }

    /// <summary>
    /// Extracts relevant information from a wall element.
    /// </summary>
    private Models.WallInfo ExtractWallInfo(Wall wall)
    {
        var info = new Models.WallInfo
        {
            WallId = wall.Id,
            WallName = wall.Name ?? "Unnamed",
            IsStructural = wall.StructuralUsage != StructuralUsage.NonStructural,
            IsCurtainWall = wall.WallType.IsCurtainWall,
            HostDocument = _doc,
            IsLinked = false
        };
        
        // Get material
        if (wall.GetMaterialIds(false).Count > 0)
        {
            info.MaterialId = wall.GetMaterialIds(false)[0];
        }
        
        // Get geometry
        var location = wall.Location as LocationCurve;
        if (location?.Curve is Line line)
        {
            info.LocationLineStart = line.GetEndPoint(0);
            info.LocationLineEnd = line.GetEndPoint(1);
            info.Length = line.Length;
            
            // Calculate normal direction
            var direction = line.Direction;
            info.NormalDirection = XYZ.BasisZ.CrossProduct(direction).Normalize();
        }
        
        // Get volume and height
        if (wall.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED) is Parameter volParam)
        {
            info.Volume = volParam.AsDouble();
        }
        
        if (wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM) is Parameter heightParam)
        {
            info.Height = heightParam.AsDouble();
        }
        
        // Find supporting floor
        info.SupportingFloorId = FindSupportingFloor(wall);
        
        return info;
    }

    /// <summary>
    /// Finds the floor that supports this wall.
    /// </summary>
    private ElementId? FindSupportingFloor(Wall wall)
    {
        var basePoint = wall.GetLocationLine()?.GetEndPoint(0);
        if (basePoint == null) return null;
        
        // Search for floors at or below wall base
        var floorCollector = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Floors);
        
        foreach (Floor floor in floorCollector.ToElements())
        {
            // Simple check - could be enhanced with more sophisticated geometry intersection
            var floorTop = floor.get_Parameter(BuiltInParameter.FLOOR_ELEVATION_PARAM)?.AsDouble() ?? double.MinValue;
            var wallBase = basePoint.Z;
            
            if (Math.Abs(floorTop - wallBase) < 0.5) // Within 6 inches tolerance
            {
                return floor.Id;
            }
        }
        
        return null;
    }

    /// <summary>
    /// Collects walls from linked Revit models.
    /// </summary>
    private List<Models.WallInfo> CollectFromLinks()
    {
        var linkedWalls = new List<Models.WallInfo>();
        
        var linkCollector = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_RvtLinks);
        
        foreach (RevitLinkInstance link in linkCollector.ToElements())
        {
            try
            {
                var linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                
                var linkWallCollector = new FilteredElementCollector(linkDoc).OfCategory(BuiltInCategory.OST_Walls);
                
                foreach (Wall wall in linkWallCollector.ToElements())
                {
                    if (ShouldIncludeWall(wall))
                    {
                        var info = ExtractWallInfo(wall);
                        info.IsLinked = true;
                        info.HostDocument = linkDoc;
                        linkedWalls.Add(info);
                    }
                }
            }
            catch
            {
                // Skip problematic links
                continue;
            }
        }
        
        return linkedWalls;
    }
}
