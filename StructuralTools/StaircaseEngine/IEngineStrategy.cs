using Autodesk.Revit.DB;
using StructuralTools.SketchEngine;

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
///
/// Primary engine: <see cref="SketchEngineStrategy"/> — derives panel geometry
/// from riser curves and the run/landing boundary; offsets every panel to the
/// structural mid-surface of the waist slab.
///
/// <see cref="SketchEngineStrategy"/> falls back to <see cref="StraightEngine"/>
/// internally when riser curves are unavailable, so this router always returns
/// the sketch engine.
/// </summary>
public static class EngineRouter
{
    public static IEngineStrategy GetEngine(StairNode node) =>
        new SketchEngineStrategy();
}
