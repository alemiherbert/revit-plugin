using Autodesk.Revit.DB;

namespace StructuralTools.StaircaseEngine;

/// <summary>
/// The structural role of an analytical panel in the stair idealisation.
/// </summary>
public enum PanelRole
{
    /// <summary>An inclined flight of stairs.</summary>
    Flight,
    /// <summary>A flat landing platform.</summary>
    Landing,
    /// <summary>A winder tread (triangular or near-triangular panel).</summary>
    Winder
}

/// <summary>
/// Immutable geometry description of one analytical panel, ready for
/// <see cref="Autodesk.Revit.DB.Structure.AnalyticalPanel"/> creation.
/// </summary>
public class PanelGeometry
{
    /// <summary>3 or 4 corners, in consistent CCW winding (normal faces up/out).</summary>
    public List<XYZ> Corners { get; set; } = new();

    /// <summary>Structural waist thickness in feet (Revit internal units).</summary>
    public double Thickness { get; set; }

    /// <summary>Concrete material ID.</summary>
    public ElementId MaterialId { get; set; } = ElementId.InvalidElementId;

    /// <summary>What kind of stair component this panel represents.</summary>
    public PanelRole Role { get; set; }

    /// <summary>The source StairsRun or StairsLanding element ID.</summary>
    public ElementId SourceElementId { get; set; } = ElementId.InvalidElementId;

    /// <summary>Human-readable label for logging.</summary>
    public string Label { get; set; } = string.Empty;
}
