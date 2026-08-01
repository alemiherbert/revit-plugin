using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using StructuralTools.StaircaseEngine;

namespace StructuralTools.SketchEngine;

/// <summary>
/// Topology-aware engine strategy that uses the Revit stair element's own
/// run / landing decomposition (via <c>GetStairsRuns()</c> /
/// <c>GetStairsLandings()</c>) and derives panel geometry from the sketch
/// riser curves and boundary loop rather than from path endpoints or
/// bounding-box approximations.
///
/// Pipeline per <see cref="StairNode"/>
/// -------------------------------------
/// <list type="bullet">
///   <item><c>StairsRun</c>  → <see cref="RunPanelBuilder"/></item>
///   <item><c>StairsLanding</c> → <see cref="LandingPanelBuilder"/></item>
/// </list>
///
/// Every produced panel is shifted to the structural mid-surface of the
/// waist slab by <see cref="MidSurfaceOffset"/>.
///
/// Fallback
/// --------
/// When <see cref="RunPanelBuilder"/> returns <c>null</c> (fewer than 2
/// riser curves available) the node is silently forwarded to the existing
/// <see cref="StraightEngine"/> so no run is ever silently dropped.
/// </summary>
public sealed class SketchEngineStrategy : IEngineStrategy
{
    /// <summary>
    /// Per-call diagnostics; cleared at the start of each <see cref="BuildPanels"/>
    /// invocation. Drained by the orchestrator after each node.
    /// </summary>
    public static List<string> Diagnostics { get; } = new();

    private static readonly StraightEngine _fallback = new();

    public List<PanelGeometry> BuildPanels(
        Document doc, StairNode node, StairParameterContext context)
    {
        Diagnostics.Clear();
        var log = Diagnostics;

        return node.SourceElement switch
        {
            StairsLanding landing => HandleLanding(landing, node, context, log),
            StairsRun run         => HandleRun(run, doc, node, context, log),
            _ => FallbackPanels(doc, node, context, log, "unknown element type")
        };
    }

    // ------------------------------------------------------------------
    // Landing
    // ------------------------------------------------------------------

    private static List<PanelGeometry> HandleLanding(
        StairsLanding landing,
        StairNode node,
        StairParameterContext context,
        List<string> log)
    {
        var panels = LandingPanelBuilder.Build(landing, node, context, log);
        if (panels.Count == 0)
            log.Add($"[WARN] Landing {landing.Id}: LandingPanelBuilder returned 0 panels.");
        return panels;
    }

    // ------------------------------------------------------------------
    // Run
    // ------------------------------------------------------------------

    private static List<PanelGeometry> HandleRun(
        StairsRun run,
        Document doc,
        StairNode node,
        StairParameterContext context,
        List<string> log)
    {
        var panels = RunPanelBuilder.Build(run, doc, node, context, log);

        if (panels != null)
            return panels;

        // RunPanelBuilder returned null → riser curves unavailable.
        log.Add($"[INFO] Run {run.Id}: falling back to StraightEngine.");
        return FallbackPanels(doc, node, context, log, "riser curves unavailable");
    }

    // ------------------------------------------------------------------
    // Fallback
    // ------------------------------------------------------------------

    private static List<PanelGeometry> FallbackPanels(
        Document doc,
        StairNode node,
        StairParameterContext context,
        List<string> log,
        string reason)
    {
        log.Add($"[INFO] Node {node.ElementId}: using StraightEngine fallback ({reason}).");
        try
        {
            var fallbackPanels = _fallback.BuildPanels(doc, node, context);
            log.AddRange(StraightEngine.Diagnostics);
            StraightEngine.Diagnostics.Clear();
            return fallbackPanels;
        }
        catch (Exception ex)
        {
            log.Add($"[ERROR] StraightEngine fallback threw: {ex.Message}");
            return new List<PanelGeometry>();
        }
    }
}
