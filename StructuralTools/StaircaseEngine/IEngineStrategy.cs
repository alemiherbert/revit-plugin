using Autodesk.Revit.DB;

namespace StructuralTools.StaircaseEngine;

/// <summary>
/// Strategy interface for building <see cref="PanelGeometry"/> from a
/// <see cref="StairNode"/>. Each engine handles one run type (or landings).
/// </summary>
public interface IEngineStrategy
{
    /// <summary>
    /// Build one or more panels for the given node, using the cached
    /// <paramref name="context"/> for material + thickness.
    /// </summary>
    List<PanelGeometry> BuildPanels(Document doc, StairNode node, StairParameterContext context);
}

/// <summary>
/// Routes a <see cref="StairNode"/> to the appropriate engine.
/// Only sketch-based straight runs and landings are supported.
/// Curved and winder runs have been removed.
/// </summary>
public static class EngineRouter
{
    public static IEngineStrategy GetEngine(StairNode node) =>
        new StraightEngine();
}
