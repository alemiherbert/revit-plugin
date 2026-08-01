using Autodesk.Revit.DB;

namespace StructuralTools.StaircaseEngine;

/// <summary>
/// Cached material + thickness resolved once per batch and passed to all
/// engine strategies. Avoids running <see cref="FilteredElementCollector"/>
/// and parameter lookups per panel.
/// </summary>
public class StairParameterContext
{
    /// <summary>Concrete material ID (resolved once via <c>GetConcreteMaterial</c>).</summary>
    public ElementId MaterialId { get; set; } = ElementId.InvalidElementId;

    /// <summary>Waist thickness in feet (resolved once via <c>GetWaistThickness</c>).</summary>
    public double ThicknessFt { get; set; } = EngineConfig.FallbackWaistDepth;
}
