using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace StructuralTools.StaircaseEngine;

/// <summary>
/// Builds <see cref="AnalyticalPanel"/> elements from <see cref="PanelGeometry"/>
/// descriptions. Each panel's CurveLoop is built from the corners (CCW winding),
/// then material and thickness are set.
/// </summary>
public class AnalyticalModelBuilder
{
    private readonly Document _doc;
    private readonly List<string> _log;

    public AnalyticalModelBuilder(Document doc, List<string> log)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _log = log ?? new List<string>();
    }

    /// <summary>
    /// Create one <see cref="AnalyticalPanel"/> from a <see cref="PanelGeometry"/>.
    /// </summary>
    public AnalyticalPanel CreatePanel(PanelGeometry pg)
    {
        if (pg.Corners.Count < 3)
            throw new InvalidOperationException(
                $"PanelGeometry for {pg.Label} has only {pg.Corners.Count} corners — need at least 3.");

        // ---- Validate all edge lengths BEFORE creating Line objects ------------
        // Revit's ShortCurveTolerance is ~1/256 ft ≈ 0.0039 ft. Use a slightly
        // larger minimum (EngineConfig.MinEdgeFt ≈ 0.012 ft ≈ 3.7 mm) to be safe.
        for (int i = 0; i < pg.Corners.Count; i++)
        {
            XYZ a = pg.Corners[i];
            XYZ b = pg.Corners[(i + 1) % pg.Corners.Count];
            double len = a.DistanceTo(b);
            if (len < EngineConfig.MinEdgeFt)
            {
                // Log all corners so we can diagnose the geometry issue.
                var cornerStrs = pg.Corners.Select((c, i2) =>
                    $"  c{i2} = ({c.X:F3}, {c.Y:F3}, {c.Z:F3})").ToList();
                throw new InvalidOperationException(
                    $"Edge {i}→{(i + 1) % pg.Corners.Count} is {len:F4} ft " +
                    $"(min {EngineConfig.MinEdgeFt} ft) for {pg.Label}.\n" +
                    $"Corners:\n{string.Join("\n", cornerStrs)}");
            }
        }

        // ---- Build CurveLoop from corners -------------------------------------
        var loop = new CurveLoop();
        for (int i = 0; i < pg.Corners.Count; i++)
        {
            XYZ a = pg.Corners[i];
            XYZ b = pg.Corners[(i + 1) % pg.Corners.Count];
            loop.Append(Line.CreateBound(a, b));
        }

        // Create the analytical panel.
        AnalyticalPanel panel = AnalyticalPanel.Create(_doc, loop);

        // Set material.
        if (pg.MaterialId != ElementId.InvalidElementId)
        {
            try { panel.MaterialId = pg.MaterialId; }
            catch (Exception ex)
            {
                _log.Add($"[WARNING] {pg.Label}: could not set material — {ex.Message}");
            }
        }

        // Set thickness.
        try { panel.Thickness = pg.Thickness; }
        catch (Exception ex)
        {
            _log.Add($"[WARNING] {pg.Label}: could not set thickness — {ex.Message}");
        }

        return panel;
    }

    /// <summary>
    /// Create all panels for a list of <see cref="PanelGeometry"/> and return
    /// the created <see cref="AnalyticalPanel"/> elements. Failed panels are
    /// logged and skipped — the returned list may be shorter than the input.
    /// </summary>
    public List<AnalyticalPanel> CreatePanels(List<PanelGeometry> panels)
    {
        var created = new List<AnalyticalPanel>();
        foreach (var pg in panels)
        {
            try
            {
                created.Add(CreatePanel(pg));
            }
            catch (Exception ex)
            {
                _log.Add($"[ERROR] {pg.Label}: creation failed — {ex.Message}");
            }
        }
        return created;
    }
}
