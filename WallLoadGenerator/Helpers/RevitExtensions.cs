using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;

namespace WallLoadGenerator.Helpers;

/// <summary>
/// Extension methods for Revit elements.
/// </summary>
public static class RevitExtensions
{
    /// <summary>
    /// Gets all walls from a document, optionally including linked models.
    /// </summary>
    public static List<Wall> GetAllWalls(this Document doc, bool includeLinks = false)
    {
        var walls = new List<Wall>();
        
        var collector = new FilteredElementCollector(doc);
        collector.OfCategory(BuiltInCategory.OST_Walls)
                 .WhereElementIsNotElementType();
        
        walls.AddRange(collector.Cast<Wall>());
        
        if (includeLinks)
        {
            walls.AddRange(GetWallsFromLinks(doc));
        }
        
        return walls;
    }
    
    /// <summary>
    /// Gets walls from all linked models in the document.
    /// </summary>
    private static List<Wall> GetWallsFromLinks(Document doc)
    {
        var linkedWalls = new List<Wall>();
        
        var linkCollector = new FilteredElementCollector(doc);
        linkCollector.OfCategory(BuiltInCategory.OST_RvtLinks)
                     .WhereElementIsNotElementType();
        
        foreach (var linkInstance in linkCollector.Cast<RevitLinkInstance>())
        {
            try
            {
                var linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc == null) continue;
                
                var linkWallCollector = new FilteredElementCollector(linkDoc);
                linkWallCollector.OfCategory(BuiltInCategory.OST_Walls)
                                 .WhereElementIsNotElementType();
                
                linkedWalls.AddRange(linkWallCollector.Cast<Wall>());
            }
            catch
            {
                // Skip problematic links
            }
        }
        
        return linkedWalls;
    }
    
    /// <summary>
    /// Gets all floors from a document.
    /// </summary>
    public static List<Floor> GetAllFloors(this Document doc)
    {
        var collector = new FilteredElementCollector(doc);
        collector.OfCategory(BuiltInCategory.OST_Floors)
                 .WhereElementIsNotElementType();
        
        return collector.Cast<Floor>().ToList();
    }
    
    /// <summary>
    /// Gets all load cases of a specific type.
    /// </summary>
    public static List<Element> GetLoadCases(this Document doc, string loadCaseType = "Dead Load")
    {
        var collector = new FilteredElementCollector(doc);
        collector.OfCategory(BuiltInCategory.OST_LoadCases)
                 .WhereElementIsNotElementType();
        
        return collector.Where(e => 
        {
            var nameParam = e.get_Parameter(BuiltInParameter.LOAD_CASE_NAME);
            return nameParam?.AsString()?.Contains(loadCaseType) ?? false;
        }).ToList();
    }
    
    /// <summary>
    /// Checks if an element is in a demolished phase.
    /// </summary>
    public static bool IsDemolished(this Element element)
    {
        var phaseDemolishedParam = element.get_Parameter(
            BuiltInParameter.PHASE_DEMOLISHED);
        
        if (phaseDemolishedParam == null)
            return false;
            
        var phaseId = phaseDemolishedParam.AsElementId();
        return phaseId != null && phaseId != ElementId.InvalidElementId;
    }
    
    /// <summary>
    /// Gets the level elevation for an element.
    /// </summary>
    public static double GetLevelElevation(this Element element)
    {
        if (element is HostObject host)
        {
            var levelId = host.LevelId;
            if (levelId != null && levelId != ElementId.InvalidElementId)
            {
                var level = element.Document.GetElement(levelId) as Level;
                return level?.Elevation ?? 0;
            }
        }
        return 0;
    }
}
