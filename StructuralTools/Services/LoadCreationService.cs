using Autodesk.Revit.DB;

namespace StructuralTools.Services;

/// <summary>
/// Service for creating line loads on floors from wall information.
/// </summary>
public class LoadCreationService
{
    private readonly Document _doc;
    private readonly Models.WallLoadSettings _settings;

    public LoadCreationService(Document doc, Models.WallLoadSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    /// <summary>
    /// Creates line loads on floors based on collected wall information.
    /// Returns statistics about the operation.
    /// </summary>
    public LoadCreationResult CreateLoads(List<Models.WallInfo> walls, ElementId? loadCaseId)
    {
        var result = new LoadCreationResult();
        var createdLoads = new List<Models.WallLoad>();
        
        // Calculate loads for each wall
        foreach (var wall in walls)
        {
            if (wall.SupportingFloorId == null)
            {
                result.SkippedCount++;
                result.SkippedReasons.Add($"Wall {wall.WallId}: No supporting floor found");
                continue;
            }
            
            var load = CalculateWallLoad(wall, loadCaseId);
            if (load != null)
            {
                createdLoads.Add(load);
            }
            else
            {
                result.SkippedCount++;
                result.SkippedReasons.Add($"Wall {wall.WallId}: Could not calculate load");
            }
        }
        
        // Merge coincident loads if enabled
        if (_settings.MergeCoincidentLoads)
        {
            createdLoads = MergeCoincidentLoads(createdLoads);
        }
        
        // Create the actual loads in Revit
        using (TransactionGroup tg = new TransactionGroup(_doc, "Create Wall Loads"))
        {
            tg.Start();
            
            try
            {
                using (Transaction t = new Transaction(_doc, "Create Line Loads"))
                {
                    t.Start();
                    
                    foreach (var load in createdLoads)
                    {
                        try
                        {
                            CreateLineLoad(load);
                            result.CreatedCount++;
                        }
                        catch (Exception ex)
                        {
                            result.SkippedCount++;
                            result.SkippedReasons.Add($"Failed to create load: {ex.Message}");
                        }
                    }
                    
                    t.Commit();
                }
                
                tg.Assimilate();
            }
            catch
            {
                tg.RollBack();
                throw;
            }
        }
        
        result.TotalWalls = walls.Count;
        return result;
    }

    /// <summary>
    /// Calculates the line load magnitude for a wall.
    /// </summary>
    private Models.WallLoad? CalculateWallLoad(Models.WallInfo wall, ElementId? loadCaseId)
    {
        double density;
        
        if (_settings.UseMaterialDensity)
        {
            // Get density from material
            var materialService = new MaterialService(_doc);
            density = materialService.GetMaterialDensity(wall.MaterialId);
            
            if (density <= 0)
            {
                // Use default density if material not found
                density = _settings.OverrideDensity;
            }
        }
        else
        {
            density = _settings.OverrideDensity;
        }
        
        // Calculate load: density * volume / length = force per unit length
        // Volume is in cubic feet, length in feet
        // Density should be in kN/m³ or k/ft³ depending on project units
        
        var conversionService = new UnitConversionService(_doc);
        double loadMagnitude = density * wall.Volume / wall.Length;
        
        // Convert to appropriate units
        loadMagnitude = conversionService.ToForcePerLength(loadMagnitude);
        
        return new Models.WallLoad
        {
            StartPoint = wall.LocationLineStart,
            EndPoint = wall.LocationLineEnd,
            LoadMagnitude = loadMagnitude,
            LoadCaseType = _settings.LoadCaseType,
            TargetFloorId = wall.SupportingFloorId,
            SourceWallId = wall.WallId,
            Description = $"{_settings.LoadCaseType} load from wall {wall.WallName}"
        };
    }

    /// <summary>
    /// Merges loads that are coincident (within tolerance).
    /// </summary>
    private List<Models.WallLoad> MergeCoincidentLoads(List<Models.WallLoad> loads)
    {
        if (loads.Count <= 1) return loads;
        
        var merged = new List<Models.WallLoad>();
        var used = new bool[loads.Count];
        
        for (int i = 0; i < loads.Count; i++)
        {
            if (used[i]) continue;
            
            var baseLoad = loads[i];
            var totalMagnitude = baseLoad.LoadMagnitude;
            var mergedCount = 1;
            used[i] = true;
            
            for (int j = i + 1; j < loads.Count; j++)
            {
                if (used[j]) continue;
                
                var otherLoad = loads[j];
                
                if (AreLoadsCoincident(baseLoad, otherLoad))
                {
                    totalMagnitude += otherLoad.LoadMagnitude;
                    mergedCount++;
                    used[j] = true;
                }
            }
            
            baseLoad.LoadMagnitude = totalMagnitude;
            baseLoad.Description = $"Merged load from {mergedCount} walls";
            merged.Add(baseLoad);
        }
        
        return merged;
    }

    /// <summary>
    /// Determines if two loads are coincident within tolerance.
    /// </summary>
    private bool AreLoadsCoincident(Models.WallLoad load1, Models.WallLoad load2)
    {
        if (load1.TargetFloorId != load2.TargetFloorId)
            return false;
            
        if (load1.LoadCaseType != load2.LoadCaseType)
            return false;
        
        double tolerance = _settings.TolerancePercent / 100.0;
        
        // Check if start and end points are within tolerance
        double startDist = load1.StartPoint.DistanceTo(load2.StartPoint);
        double endDist = load1.EndPoint.DistanceTo(load2.EndPoint);
        
        double length1 = load1.StartPoint.DistanceTo(load1.EndPoint);
        double length2 = load2.StartPoint.DistanceTo(load2.EndPoint);
        
        double toleranceDistance = Math.Max(length1, length2) * tolerance;
        
        return startDist < toleranceDistance && endDist < toleranceDistance;
    }

    /// <summary>
    /// Creates an actual line load element in Revit.
    /// Note: This is a placeholder - actual implementation depends on available API
    /// </summary>
    private void CreateLineLoad(Models.WallLoad load)
    {
        // Placeholder for actual load creation
        // In a real implementation, this would use the Structural Analysis API
        // to create an AreaLoad or LineLoad element
        
        LoggingService.Info($"Would create load: {load.Description}, magnitude: {load.LoadMagnitude:F2}");
    }
}

/// <summary>
/// Result of a load creation operation.
/// </summary>
public class LoadCreationResult
{
    public int TotalWalls { get; set; }
    public int CreatedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<string> SkippedReasons { get; set; } = new();
    public TimeSpan ElapsedTime { get; set; }
}
