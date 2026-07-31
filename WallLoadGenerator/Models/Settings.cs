namespace WallLoadGenerator.Models;

/// <summary>
/// User settings for wall load generation.
/// </summary>
public class Settings
{
    /// <summary>
    /// Selected load case category
    /// </summary>
    public LoadCaseType LoadCaseType { get; set; } = LoadCaseType.DeadLoad;
    
    /// <summary>
    /// Include structural walls
    /// </summary>
    public bool IncludeStructural { get; set; } = true;
    
    /// <summary>
    /// Include architectural (non-structural) walls
    /// </summary>
    public bool IncludeArchitectural { get; set; } = true;
    
    /// <summary>
    /// Include walls from linked models
    /// </summary>
    public bool IncludeLinkedModels { get; set; } = false;
    
    /// <summary>
    /// Merge coincident loads on the same edge
    /// </summary>
    public bool MergeCoincidentLoads { get; set; } = true;
    
    /// <summary>
    /// Ignore curtain walls
    /// </summary>
    public bool IgnoreCurtainWalls { get; set; } = true;
    
    /// <summary>
    /// Ignore demolished walls
    /// </summary>
    public bool IgnoreDemolished { get; set; } = true;
    
    /// <summary>
    /// Use material density from wall type
    /// </summary>
    public bool UseMaterialDensity { get; set; } = true;
    
    /// <summary>
    /// Override density value (kN/m³ or lbf/ft³)
    /// </summary>
    public double OverrideDensity { get; set; } = 24.0;
    
    /// <summary>
    /// Tolerance percentage for merging loads
    /// </summary>
    public double TolerancePercent { get; set; } = 2.0;
    
    /// <summary>
    /// Minimum wall height to consider (feet or meters)
    /// </summary>
    public double MinWallHeight { get; set; } = 0.5;
    
    /// <summary>
    /// Create loads only on supporting floors
    /// </summary>
    public bool OnlyOnSupportingFloors { get; set; } = true;
    
    /// <summary>
    /// Skip walls without material assigned
    /// </summary>
    public bool SkipWallsWithoutMaterial { get; set; } = false;
    
    /// <summary>
    /// Verbosity level for progress reporting
    /// </summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Normal;
}

/// <summary>
/// Available load case types
/// </summary>
public enum LoadCaseType
{
    DeadLoad,
    LiveLoad,
    WindLoad,
    SeismicLoad,
    SuperDeadLoad,
    PartitionLoad
}

/// <summary>
/// Log verbosity levels
/// </summary>
public enum LogLevel
{
    Minimal,
    Normal,
    Detailed,
    Debug
}
