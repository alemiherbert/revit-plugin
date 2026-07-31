using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;

namespace WallLoadGenerator.Models;

/// <summary>
/// Represents a wall load to be created on a floor.
/// </summary>
public class WallLoad
{
    public ElementId WallId { get; set; }
    public ElementId FloorId { get; set; }
    public ElementId LoadCaseId { get; set; }
    
    /// <summary>
    /// Start point of the line load (in model coordinates)
    /// </summary>
    public XYZ StartPoint { get; set; } = XYZ.Zero;
    
    /// <summary>
    /// End point of the line load (in model coordinates)
    /// </summary>
    public XYZ EndPoint { get; set; } = XYZ.Zero;
    
    /// <summary>
    /// Load magnitude in force per unit length (lbf/ft or N/m depending on units)
    /// </summary>
    public double Magnitude { get; set; }
    
    /// <summary>
    /// Load direction vector
    /// </summary>
    public XYZ Direction { get; set; } = XYZ.BasisZ;
    
    /// <summary>
    /// Wall material density (for reference)
    /// </summary>
    public double Density { get; set; }
    
    /// <summary>
    /// Wall thickness used for calculation
    /// </summary>
    public double Thickness { get; set; }
    
    /// <summary>
    /// Wall height used for calculation
    /// </summary>
    public double Height { get; set; }
    
    /// <summary>
    /// Skip reason if this load was not created
    /// </summary>
    public string? SkipReason { get; set; }
    
    public bool IsSkipped => !string.IsNullOrEmpty(SkipReason);
}
