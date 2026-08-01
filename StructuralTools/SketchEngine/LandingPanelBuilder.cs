using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using StructuralTools.StaircaseEngine;

namespace StructuralTools.SketchEngine;

/// <summary>
/// Builds <see cref="PanelGeometry"/> objects for a <see cref="StairsLanding"/>.
///
/// Algorithm
/// ---------
/// 1. Get the landing boundary loop via
///    <see cref="SketchExtractor.GetLandingBoundary"/>.
/// 2. Project all corners to the landing elevation.
/// 3. Triangulate if there are more than 4 corners (fan from centroid).
/// 4. Shift each panel downward by <c>thickness/2</c> via
///    <see cref="MidSurfaceOffset.ApplyHorizontal"/> so the panel lies at
///    the structural mid-surface of the landing slab.
/// </summary>
public static class LandingPanelBuilder
{
    /// <summary>
    /// Build all panels for the landing.
    /// Returns an empty list (not null) when the boundary is unavailable.
    /// </summary>
    public static List<PanelGeometry> Build(
        StairsLanding landing,
        StairNode node,
        StairParameterContext context,
        List<string> log)
    {
        var panels = new List<PanelGeometry>();
        double z = node.BaseElevation;

        var loop = SketchExtractor.GetLandingBoundary(landing, log);
        if (loop == null)
        {
            log.Add($"[WARN] Landing {landing.Id}: no boundary — panel skipped.");
            return panels;
        }

        // Project all boundary curve start-points to the landing elevation z.
        var pts = new List<XYZ>();
        foreach (var c in loop)
        {
            var p = c.GetEndPoint(0);
            pts.Add(new XYZ(p.X, p.Y, z));
        }

        if (pts.Count < 3)
        {
            log.Add($"[WARN] Landing {landing.Id}: boundary has only {pts.Count} point(s) — skipped.");
            return panels;
        }

        // Validate minimum edge length.
        bool valid = true;
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].DistanceTo(pts[(i + 1) % pts.Count]) < EngineConfig.MinEdgeFt)
            {
                valid = false;
                break;
            }
        }
        if (!valid)
        {
            log.Add($"[WARN] Landing {landing.Id}: boundary has a degenerate edge — skipped.");
            return panels;
        }

        if (pts.Count > 4)
        {
            // Fan-triangulate for non-rectangular landings (L-shape, T-shape, etc.).
            log.Add($"[DEBUG] Landing {landing.Id}: {pts.Count}-sided polygon → fan triangulation.");
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var raw     = new List<XYZ> { pts[0], pts[i], pts[i + 1] };
                var corners = MidSurfaceOffset.ApplyHorizontal(raw, context.ThicknessFt);
                panels.Add(MakePanel(corners, landing, context, $"tri-{i}"));
            }
        }
        else
        {
            var raw     = pts.ToList();
            var corners = MidSurfaceOffset.ApplyHorizontal(raw, context.ThicknessFt);
            panels.Add(MakePanel(corners, landing, context, "land"));
        }

        log.Add($"[INFO] Landing {landing.Id}: {panels.Count} panel(s) at z={z * 304.8:F0}mm " +
                $"(mid-surface offset {context.ThicknessFt / 2 * 304.8:F0}mm).");
        return panels;
    }

    private static PanelGeometry MakePanel(
        List<XYZ> corners,
        StairsLanding landing,
        StairParameterContext context,
        string suffix)
    => new PanelGeometry
    {
        Corners         = corners,
        Thickness       = context.ThicknessFt,
        MaterialId      = context.MaterialId,
        Role            = PanelRole.Landing,
        SourceElementId = landing.Id,
        Label           = $"Landing {landing.Id} ({suffix})"
    };
}
