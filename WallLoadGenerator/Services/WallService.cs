using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;
using WallLoadGenerator.Models;

namespace WallLoadGenerator.Services;

/// <summary>
/// Service for collecting and extracting wall information.
/// Optimized for handling thousands of walls efficiently.
/// </summary>
public class WallService
{
    private readonly Document _doc;
    private readonly Settings _settings;

    public WallService(Document doc, Settings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    /// <summary>
    /// Collects all walls matching the current settings.
    /// </summary>
    public List<WallInfo> CollectWalls()
    {
        var walls = new List<WallInfo>();
        
        // Collect walls from main model
        var collector = new FilteredElementCollector(_doc);
        collector.OfCategory(BuiltInCategory.OST_Walls)
                 .WhereElementIsNotElementType();
        
        foreach (var element in collector)
        {
            if (element is not Wall wall) continue;
            
            if (!ShouldIncludeWall(wall)) continue;
            
            var wallInfo = ExtractWallInfo(wall);
            if (wallInfo != null)
                walls.Add(wallInfo);
        }
        
        // Optionally collect from linked models
        if (_settings.IncludeLinkedModels)
        {
            walls.AddRange(CollectWallsFromLinks());
        }
        
        LoggingService.Info($"Collected {walls.Count} walls");
        return walls;
    }

    /// <summary>
    /// Determines if a wall should be included based on settings.
    /// </summary>
    private bool ShouldIncludeWall(Wall wall)
    {
        // Check structural flag
        bool isStructural = wall.StructuralUsage != StructuralType.NonStructural;
        
        if (isStructural && !_settings.IncludeStructural)
            return false;
        
        if (!isStructural && !_settings.IncludeArchitectural)
            return false;
        
        // Check curtain wall
        if (_settings.IgnoreCurtainWalls && IsCurtainWall(wall))
            return false;
        
        // Check demolished phase
        if (_settings.IgnoreDemolished)
        {
            var demolishedPhaseId = wall.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsElementId();
            if (demolishedPhaseId != null && demolishedPhaseId != ElementId.InvalidElementId)
                return false;
        }
        
        // Check minimum height
        var heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
        if (heightParam != null)
        {
            double height = heightParam.AsDouble();
            if (height < _settings.MinWallHeight)
                return false;
        }
        
        return true;
    }

    /// <summary>
    /// Checks if wall is a curtain wall.
    /// </summary>
    private bool IsCurtainWall(Wall wall)
    {
        var wallType = wall.WallType;
        if (wallType == null) return false;
        
        return wallType.Kind == WallKind.Curtain;
    }

    /// <summary>
    /// Extracts detailed information from a wall element.
    /// </summary>
    private WallInfo? ExtractWallInfo(Wall wall)
    {
        try
        {
            var info = new WallInfo
            {
                ElementId = wall.Id,
                WallType = wall.WallType?.Name ?? "Unknown",
                FamilyName = wall.FamilyName,
                TypeName = wall.Name,
                IsStructural = wall.StructuralUsage != StructuralType.NonStructural,
                IsCurtainWall = IsCurtainWall(wall),
                LevelId = wall.LevelId,
            };
            
            // Get location curve
            if (wall.Location is LocationCurve lc && lc.Curve is Line line)
            {
                info.LocationStart = line.GetEndPoint(0);
                info.LocationEnd = line.GetEndPoint(1);
                info.Length = line.Length;
            }
            
            // Get thickness
            var thicknessParam = wall.get_Parameter(BuiltInParameter.WALL_THICKNESS);
            info.Thickness = thicknessParam?.AsDouble() ?? 0;
            
            // Get height
            var heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            info.Height = heightParam?.AsDouble() ?? 0;
            
            // Get offsets
            var topOffsetParam = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
            info.TopOffset = topOffsetParam?.AsDouble() ?? 0;
            
            var baseOffsetParam = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
            info.BaseOffset = baseOffsetParam?.AsDouble() ?? 0;
            
            // Get volume
            var volumeParam = wall.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
            info.Volume = volumeParam?.AsDouble() ?? 0;
            
            // Get material
            var materialId = wall.GetMaterialIds(false).FirstOrDefault();
            if (materialId != null && materialId != ElementId.InvalidElementId)
            {
                info.MaterialId = materialId;
                
                if (_settings.UseMaterialDensity)
                {
                    info.Density = GetMaterialDensity(materialId);
                }
            }
            
            // Get demolished phase
            var demolishedPhaseParam = wall.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
            info.DemolishedPhaseId = demolishedPhaseParam?.AsElementId();
            
            return info;
        }
        catch (Exception ex)
        {
            LoggingService.Warning($"Failed to extract wall info for {wall.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets material density from material element.
    /// </summary>
    private double? GetMaterialDensity(ElementId materialId)
    {
        if (materialId == ElementId.InvalidElementId)
            return null;
            
        var material = _doc.GetElement(materialId) as Material;
        if (material == null)
            return null;
            
        // Try to get density property
        // Note: Revit API doesn't directly expose density, 
        // this would need to be retrieved from material properties or extended data
        return null;
    }

    /// <summary>
    /// Collects walls from linked Revit models.
    /// </summary>
    private List<WallInfo> CollectWallsFromLinks()
    {
        var linkedWalls = new List<WallInfo>();
        
        var linkCollector = new FilteredElementCollector(_doc);
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
                
                foreach (var element in linkWallCollector)
                {
                    if (element is not Wall wall) continue;
                    
                    if (!ShouldIncludeWall(wall)) continue;
                    
                    var wallInfo = ExtractWallInfo(wall);
                    if (wallInfo != null)
                        linkedWalls.Add(wallInfo);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warning($"Failed to process link {linkInstance.Name}: {ex.Message}");
            }
        }
        
        return linkedWalls;
    }
}
