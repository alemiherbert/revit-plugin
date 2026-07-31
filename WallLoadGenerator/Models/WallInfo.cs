using Autodesk.Revit.DB;

namespace WallLoadGenerator.Models;

/// <summary>
/// Contains extracted information from a wall element.
/// </summary>
public class WallInfo
{
    public ElementId ElementId { get; set; }
    public string WallType { get; set; } = string.Empty;
    public string? FamilyName { get; set; }
    public string? TypeName { get; set; }
    
    /// <summary>
    /// Wall base location point
    /// </summary>
    public XYZ BasePoint { get; set; } = XYZ.Zero;
    
    /// <summary>
    /// Wall top location point
    /// </summary>
    public XYZ TopPoint { get; set; } = XYZ.Zero;
    
    /// <summary>
    /// Wall location line start
    /// </summary>
    public XYZ LocationStart { get; set; } = XYZ.Zero;
    
    /// <summary>
    /// Wall location line end
    /// </summary>
    public XYZ LocationEnd { get; set; } = XYZ.Zero;
    
    /// <summary>
    /// Wall thickness
    /// </summary>
    public double Thickness { get; set; }
    
    /// <summary>
    /// Wall height (unconnected or connected to level)
    /// </summary>
    public double Height { get; set; }
    
    /// <summary>
    /// Wall length along location line
    /// </summary>
    public double Length { get; set; }
    
    /// <summary>
    /// Material density if available (in kg/m³ or lbf/ft³)
    /// </summary>
    public double? Density { get; set; }
    
    /// <summary>
    /// Element Id of the material
    /// </summary>
    public ElementId? MaterialId { get; set; }
    
    /// <summary>
    /// True if wall is structural
    /// </summary>
    public bool IsStructural { get; set; }
    
    /// <summary>
    /// True if wall is a curtain wall
    /// </summary>
    public bool IsCurtainWall { get; set; }
    
    /// <summary>
    /// Phase demolished parameter
    /// </summary>
    public ElementId? DemolishedPhaseId { get; set; }
    
    /// <summary>
    /// Level Id the wall is hosted on
    /// </summary>
    public ElementId? LevelId { get; set; }
    
    /// <summary>
    /// Top constraint offset
    /// </summary>
    public double TopOffset { get; set; }
    
    /// <summary>
    /// Base constraint offset
    /// </summary>
    public double BaseOffset { get; set; }
    
    /// <summary>
    /// Volume of the wall
    /// </summary>
    public double Volume { get; set; }
}
