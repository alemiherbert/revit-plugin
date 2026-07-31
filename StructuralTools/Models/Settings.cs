namespace StructuralTools.Models;

/// <summary>
/// Types of load cases supported by the wall load generator.
/// </summary>
public enum LoadCaseType
{
    DeadLoad,
    SuperDeadLoad,
    LiveLoad,
    PartitionLoad,
    WindLoad,
    SeismicLoad
}

/// <summary>
/// Settings for wall load generation.
/// </summary>
public class WallLoadSettings
{
    public LoadCaseType LoadCaseType { get; set; } = LoadCaseType.DeadLoad;
    public bool IncludeStructural { get; set; } = true;
    public bool IncludeArchitectural { get; set; } = true;
    public bool IncludeLinkedModels { get; set; } = false;
    public bool MergeCoincidentLoads { get; set; } = true;
    public bool IgnoreCurtainWalls { get; set; } = true;
    public bool IgnoreDemolished { get; set; } = true;
    public bool UseMaterialDensity { get; set; } = true;
    public double OverrideDensity { get; set; } = 24.0; // kN/m³
    public double TolerancePercent { get; set; } = 2.0;
    
    /// <summary>
    /// Gets the effective density based on settings.
    /// </summary>
    public double EffectiveDensity => UseMaterialDensity ? 0 : OverrideDensity;
}

/// <summary>
/// Settings for staircase to analytical model conversion.
/// </summary>
public class StaircaseSettings
{
    public bool IncludeStringers { get; set; } = true;
    public bool IncludeTreads { get; set; } = true;
    public bool IncludeLandings { get; set; } = true;
    public bool CreateAnalyticalMembers { get; set; } = true;
    public bool PreserveGeometry { get; set; } = true;
    public string AnalyticalMaterial { get; set; } = "Structural Steel";
}
