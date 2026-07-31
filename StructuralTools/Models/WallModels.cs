using Autodesk.Revit.DB;

namespace StructuralTools.Models;

/// <summary>
/// Represents information extracted from a wall for load generation.
/// </summary>
public class WallInfo
{
    public ElementId WallId { get; set; }
    public string WallName { get; set; } = string.Empty;
    public bool IsStructural { get; set; }
    public bool IsCurtainWall { get; set; }
    public ElementId? MaterialId { get; set; }
    public double Volume { get; set; } // in cubic feet (Revit internal units)
    public double Length { get; set; } // in feet (Revit internal units)
    public double Height { get; set; } // in feet (Revit internal units)
    public XYZ LocationLineStart { get; set; } = XYZ.Zero;
    public XYZ LocationLineEnd { get; set; } = XYZ.Zero;
    public XYZ NormalDirection { get; set; } = XYZ.BasisZ;
    public ElementId? SupportingFloorId { get; set; }
    public bool IsLinked { get; set; }
    public Document? HostDocument { get; set; }
    
    /// <summary>
    /// Gets the wall's base point for load placement.
    /// </summary>
    public XYZ BasePoint => new XYZ(
        (LocationLineStart.X + LocationLineEnd.X) / 2,
        (LocationLineStart.Y + LocationLineEnd.Y) / 2,
        LocationLineStart.Z);
}

/// <summary>
/// Represents a line load to be created on a floor.
/// </summary>
public class WallLoad
{
    public XYZ StartPoint { get; set; } = XYZ.Zero;
    public XYZ EndPoint { get; set; } = XYZ.Zero;
    public double LoadMagnitude { get; set; } // kN/m or k/ft depending on units
    public LoadCaseType LoadCaseType { get; set; } = LoadCaseType.DeadLoad;
    public ElementId? TargetFloorId { get; set; }
    public ElementId SourceWallId { get; set; }
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// Creates a description for the load.
    /// </summary>
    public string GetDescription()
    {
        return $"{LoadCaseType} load from wall {SourceWallId}";
    }
}
