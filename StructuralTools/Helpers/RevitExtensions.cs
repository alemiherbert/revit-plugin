using Autodesk.Revit.DB;

namespace StructuralTools.Helpers;

/// <summary>
/// Extension methods for Revit elements.
/// </summary>
public static class RevitExtensions
{
    /// <summary>
    /// Gets the location line of a wall.
    /// </summary>
    public static Line? GetLocationLine(this Wall wall)
    {
        if (wall.Location is LocationCurve locationCurve)
        {
            return locationCurve.Curve as Line;
        }
        return null;
    }

    /// <summary>
    /// Checks if a wall is a curtain wall.
    /// </summary>
    public static bool IsCurtainWall(this WallType wallType)
    {
        return wallType != null && wallType.Kind == WallKind.Curtain;
    }

    /// <summary>
    /// Gets all walls from a document, optionally filtered.
    /// </summary>
    public static IList<Wall> GetWalls(this Document doc, bool includeLinked = false)
    {
        var collector = new FilteredElementCollector(doc);
        var walls = collector.OfCategory(BuiltInCategory.OST_Walls)
            .OfClass(typeof(Wall))
            .Cast<Wall>()
            .ToList();
        
        if (includeLinked)
        {
            var links = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_RvtLinks)
                .ToElements()
                .OfType<RevitLinkInstance>();
            
            foreach (var link in links)
            {
                try
                {
                    var linkDoc = link.GetLinkDocument();
                    if (linkDoc != null)
                    {
                        var linkWalls = new FilteredElementCollector(linkDoc)
                            .OfCategory(BuiltInCategory.OST_Walls)
                            .OfClass(typeof(Wall))
                            .Cast<Wall>();
                        
                        walls.AddRange(linkWalls);
                    }
                }
                catch
                {
                    // Skip problematic links
                }
            }
        }
        
        return walls;
    }

    /// <summary>
    /// Gets all floors from a document.
    /// </summary>
    public static IList<Floor> GetFloors(this Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Floors)
            .OfClass(typeof(Floor))
            .Cast<Floor>()
            .ToList();
    }

    /// <summary>
    /// Gets all stairs from a document.
    /// </summary>
    public static IList<Autodesk.Revit.DB.Stairs> GetStairs(this Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Stairs)
            .OfClass(typeof(Autodesk.Revit.DB.Stairs))
            .Cast<Autodesk.Revit.DB.Stairs>()
            .ToList();
    }

    /// <summary>
    /// Gets the base elevation of a floor.
    /// </summary>
    public static double GetBaseElevation(this Floor floor)
    {
        return floor.get_Parameter(BuiltInParameter.FLOOR_ELEVATION_PARAM)?.AsDouble() ?? 0;
    }

    /// <summary>
    /// Checks if an element is demolished in the active phase.
    /// </summary>
    public static bool IsDemolished(this Element element, Document doc)
    {
        var phaseDemolished = element.DemolishedPhaseId;
        if (phaseDemolished == null) return false;
        
        var activePhase = doc.ActiveView.Phase;
        return activePhase != null && phaseDemolished <= activePhase.Id;
    }

    /// <summary>
    /// Gets the volume parameter value from an element.
    /// </summary>
    public static double GetVolume(this Element element)
    {
        return element.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.AsDouble() ?? 0;
    }
}
