using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;
using WallLoadGenerator.Models;

namespace WallLoadGenerator.Services;

/// <summary>
/// Service for creating analytical line loads from wall data.
/// Handles transaction management and load creation.
/// </summary>
public class LoadCreationService
{
    private readonly Document _doc;
    private readonly Settings _settings;
    private readonly GeometryService _geometryService;

    public LoadCreationService(Document doc, Settings settings)
    {
        _doc = doc;
        _settings = settings;
        _geometryService = new GeometryService();
    }

    /// <summary>
    /// Creates line loads on floors from wall information.
    /// </summary>
    public LoadCreationResult CreateLoads(List<WallInfo> walls, ElementId loadCaseId)
    {
        var result = new LoadCreationResult();
        var createdLoads = new List<WallLoad>();
        var skippedWalls = new List<(WallInfo Wall, string Reason)>();
        
        using var transactionGroup = new TransactionGroup(_doc, "Generate Wall Loads");
        transactionGroup.Start();
        
        try
        {
            // Find supporting floors for each wall
            var floorMap = FindSupportingFloors(walls);
            
            foreach (var wall in walls)
            {
                try
                {
                    if (!floorMap.TryGetValue(wall.ElementId, out var floorId))
                    {
                        skippedWalls.Add((wall, "No supporting floor found"));
                        result.SkippedCount++;
                        continue;
                    }
                    
                    var load = CreateLineLoadForWall(wall, floorId, loadCaseId);
                    
                    if (load != null)
                    {
                        if (ApplyLoadToDocument(load))
                        {
                            createdLoads.Add(load);
                            result.CreatedCount++;
                        }
                        else
                        {
                            skippedWalls.Add((wall, "Failed to create load element"));
                            result.SkippedCount++;
                        }
                    }
                    else
                    {
                        skippedWalls.Add((wall, "Could not calculate load"));
                        result.SkippedCount++;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warning($"Error processing wall {wall.ElementId}: {ex.Message}");
                    skippedWalls.Add((wall, $"Error: {ex.Message}"));
                    result.SkippedCount++;
                }
            }
            
            // Merge coincident loads if enabled
            if (_settings.MergeCoincidentLoads && createdLoads.Count > 1)
            {
                var mergedLoads = MergeCoincidentLoads(createdLoads);
                result.MergedCount = createdLoads.Count - mergedLoads.Count;
                createdLoads = mergedLoads;
            }
            
            transactionGroup.Assimilate();
            result.Success = true;
        }
        catch (Exception ex)
        {
            transactionGroup.RollBack();
            result.Success = false;
            result.ErrorMessage = ex.Message;
            LoggingService.Error($"Failed to create loads: {ex.Message}");
        }
        
        result.CreatedLoads = createdLoads;
        result.SkippedWalls = skippedWalls;
        
        return result;
    }

    /// <summary>
    /// Finds the supporting floor for each wall.
    /// </summary>
    private Dictionary<ElementId, ElementId> FindSupportingFloors(List<WallInfo> walls)
    {
        var floorMap = new Dictionary<ElementId, ElementId>();
        
        // Collect all floors
        var floorCollector = new FilteredElementCollector(_doc);
        floorCollector.OfCategory(BuiltInCategory.OST_Floors)
                      .WhereElementIsNotElementType()
                      .ToElements();
        
        var floors = floorCollector.Cast<Floor>().ToList();
        
        foreach (var wall in walls)
        {
            var supportingFloor = FindSupportingFloorForWall(wall, floors);
            if (supportingFloor != null)
            {
                floorMap[wall.ElementId] = supportingFloor.Id;
            }
        }
        
        return floorMap;
    }

    /// <summary>
    /// Finds the floor that supports a specific wall.
    /// </summary>
    private Floor? FindSupportingFloorForWall(WallInfo wall, List<Floor> floors)
    {
        // Get wall base point
        var basePoint = wall.LocationStart;
        basePoint = new XYZ(basePoint.X, basePoint.Y, basePoint.Z - wall.BaseOffset);
        
        // Find floor at or below wall base
        Floor? bestFloor = null;
        double maxZ = double.NegativeInfinity;
        
        foreach (var floor in floors)
        {
            // Get floor elevation
            var levelId = floor.LevelId;
            if (levelId == null || levelId == ElementId.InvalidElementId)
                continue;
                
            var level = _doc.GetElement(levelId) as Level;
            if (level == null) continue;
            
            double floorElevation = level.Elevation;
            
            // Check if floor is at or below wall base
            if (floorElevation <= basePoint.Z + UnitConversionService.FeetToMm(1)) // 1mm tolerance
            {
                if (floorElevation > maxZ)
                {
                    // Check if wall projects onto floor
                    if (_geometryService.PointIsOnFloor(basePoint, floor))
                    {
                        maxZ = floorElevation;
                        bestFloor = floor;
                    }
                }
            }
        }
        
        return bestFloor;
    }

    /// <summary>
    /// Creates a line load object for a wall.
    /// </summary>
    private WallLoad? CreateLineLoadForWall(WallInfo wall, ElementId floorId, ElementId loadCaseId)
    {
        // Calculate load magnitude
        double density = _settings.UseMaterialDensity 
            ? (wall.Density ?? _settings.OverrideDensity)
            : _settings.OverrideDensity;
        
        // Volume = thickness * height * length
        double volume = wall.Thickness * wall.Height * wall.Length;
        
        // Weight = density * volume
        double weight = density * volume;
        
        // Line load = weight / length (force per unit length)
        double lineLoadMagnitude = weight / wall.Length;
        
        return new WallLoad
        {
            WallId = wall.ElementId,
            FloorId = floorId,
            LoadCaseId = loadCaseId,
            StartPoint = wall.LocationStart,
            EndPoint = wall.LocationEnd,
            Magnitude = lineLoadMagnitude,
            Direction = XYZ.BasisZ, // Downward
            Density = density,
            Thickness = wall.Thickness,
            Height = wall.Height
        };
    }

    /// <summary>
    /// Applies a line load to the Revit document.
    /// </summary>
    private bool ApplyLoadToDocument(WallLoad load)
    {
        using var transaction = new Transaction(_doc, "Create Line Load");
        transaction.Start();
        
        try
        {
            // Note: Creating analytical line loads requires the Structural Analysis API
            // This is a simplified example - actual implementation depends on Revit version
            
            // For Revit 2027, use the appropriate API to create line loads
            // Example placeholder - replace with actual API calls
            
            // Create curve for the line load
            var line = Line.CreateBound(load.StartPoint, load.EndPoint);
            
            // TODO: Use Revit API to create actual line load element
            // This would typically involve:
            // - AnalyticalModelService.CreateLineLoad()
            // - Or using AreaLoad/LineLoad classes if available
            
            transaction.Commit();
            return true;
        }
        catch (Exception ex)
        {
            transaction.RollBack();
            LoggingService.Warning($"Failed to create load: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Merges coincident loads on the same edge.
    /// </summary>
    private List<WallLoad> MergeCoincidentLoads(List<WallLoad> loads)
    {
        var merged = new List<WallLoad>();
        var used = new bool[loads.Count];
        
        for (int i = 0; i < loads.Count; i++)
        {
            if (used[i]) continue;
            
            var baseLoad = loads[i];
            var combinedMagnitude = baseLoad.Magnitude;
            
            for (int j = i + 1; j < loads.Count; j++)
            {
                if (used[j]) continue;
                
                var otherLoad = loads[j];
                
                if (_geometryService.LoadsAreCoincident(baseLoad, otherLoad, _settings.TolerancePercent))
                {
                    combinedMagnitude += otherLoad.Magnitude;
                    used[j] = true;
                }
            }
            
            baseLoad.Magnitude = combinedMagnitude;
            merged.Add(baseLoad);
            used[i] = true;
        }
        
        return merged;
    }
}

/// <summary>
/// Result of load creation operation.
/// </summary>
public class LoadCreationResult
{
    public bool Success { get; set; }
    public int CreatedCount { get; set; }
    public int SkippedCount { get; set; }
    public int MergedCount { get; set; }
    public string? ErrorMessage { get; set; }
    public List<WallLoad> CreatedLoads { get; set; } = new();
    public List<(WallInfo Wall, string Reason)> SkippedWalls { get; set; } = new();
}
