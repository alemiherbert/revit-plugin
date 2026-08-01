using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using StructuralTools.StaircaseEngine;

namespace StructuralTools.SketchEngine;

/// <summary>
/// Builds <see cref="PanelGeometry"/> objects for a <see cref="StairsRun"/>.
///
/// Algorithm (per flight group)
/// ----------------------------
/// 1. Get riser curves via <see cref="SketchExtractor.GetSortedRisers"/>.
/// 2. Detect flight / landing groups by comparing consecutive riser spacing
///    to the median tread depth.
/// 3. For each flight group the first riser → leading edge at the group's
///    base elevation; the last riser → trailing edge at the group's top
///    elevation.  Panel corners are ordered CCW.
/// 4. For each landing group the last riser of the preceding flight and the
///    first riser of the following flight form the two opposite edges of a
///    flat quad at the landing elevation.
/// 5. Every panel is shifted by <c>−slabNormal × (thickness/2)</c> via
///    <see cref="MidSurfaceOffset.Apply"/> so it lies at the structural
///    mid-surface of the waist slab.
///
/// Fallback
/// --------
/// If fewer than 2 riser curves are available the method returns <c>null</c>
/// and the caller should fall back to <see cref="StraightEngine"/>.
/// </summary>
public static class RunPanelBuilder
{
    private const double LandingSpacingMultiplier = 2.5;

    /// <summary>
    /// Build all panels for the run.  Returns <c>null</c> when riser curves
    /// are unavailable (caller should fall back to <see cref="StraightEngine"/>).
    /// </summary>
    public static List<PanelGeometry>? Build(
        StairsRun run,
        Document doc,
        StairNode node,
        StairParameterContext context,
        List<string> log)
    {
        // ---- Travel direction from stair path --------------------------------
        XYZ travelDir = GetTravelDir(run, node, log);

        // ---- Riser curves (primary geometry source) -------------------------
        var risers = SketchExtractor.GetSortedRisers(run, travelDir, log);
        if (risers == null || risers.Count < 2)
        {
            log.Add($"[INFO] Run {run.Id}: <2 risers — SketchEngine falling back to StraightEngine.");
            return null;
        }

        // ---- Group risers into flights and landings --------------------------
        // Median spacing = expected tread depth.
        var spacings = Enumerable.Range(0, risers.Count - 1)
            .Select(i => RiserMidpoint(risers[i]).DotProduct(travelDir) -
                         RiserMidpoint(risers[i + 1]).DotProduct(travelDir))
            .Select(Math.Abs)
            .OrderBy(x => x)
            .ToList();

        double medianSpacing = spacings.Count > 0
            ? spacings[spacings.Count / 2]
            : 0;
        double landingThreshold = medianSpacing > 0
            ? medianSpacing * LandingSpacingMultiplier
            : double.MaxValue;

        double baseZ     = node.BaseElevation;
        double topZ      = node.TopElevation;
        double totalRise = topZ - baseZ;

        // Count flight risers (those followed by another riser within tread spacing).
        int flightRiserCount = risers.Count; // will refine below
        double currentZ = baseZ;
        var panels = new List<PanelGeometry>();

        // Walk through consecutive pairs and emit panels.
        int flightStart = 0;  // index of the first riser in the current flight
        for (int i = 0; i < risers.Count; i++)
        {
            bool isLastRiser    = i == risers.Count - 1;
            bool nextIsLanding  = !isLastRiser &&
                Math.Abs(RiserMidpoint(risers[i]).DotProduct(travelDir) -
                         RiserMidpoint(risers[i + 1]).DotProduct(travelDir)) > landingThreshold;

            bool endOfFlight = isLastRiser || nextIsLanding;
            if (!endOfFlight) continue;

            // Build inclined panel for risers[flightStart..i].
            int flightRisers = i - flightStart + 1;
            double flightProportion = (double)flightRisers / risers.Count;
            double flightRise       = totalRise * flightProportion;
            double flightBaseZ      = currentZ;
            double flightTopZ       = currentZ + flightRise;

            var panel = BuildFlightPanel(
                risers[flightStart], risers[i],
                travelDir, flightBaseZ, flightTopZ,
                run, context, $"flight-{flightStart}-{i}", log);

            if (panel != null) panels.Add(panel);
            currentZ = flightTopZ;

            // If this gap is a landing, build a landing quad.
            if (nextIsLanding && i + 1 < risers.Count)
            {
                var landingPanel = BuildLandingQuad(
                    risers[i], risers[i + 1],
                    currentZ, run, context,
                    $"landing-between-{i}-{i + 1}", log);
                if (landingPanel != null) panels.Add(landingPanel);
            }

            flightStart = i + 1;
        }

        if (panels.Count == 0)
        {
            log.Add($"[WARN] Run {run.Id}: SketchEngine produced 0 panels.");
            return null;
        }

        log.Add($"[INFO] Run {run.Id}: SketchEngine produced {panels.Count} panel(s).");
        return panels;
    }

    // ------------------------------------------------------------------
    // Flight panel
    // ------------------------------------------------------------------

    private static PanelGeometry? BuildFlightPanel(
        Curve firstRiser, Curve lastRiser,
        XYZ travelDir, double baseZ, double topZ,
        StairsRun run, StairParameterContext context,
        string label, List<string> log)
    {
        XYZ perpDir = new XYZ(-travelDir.Y, travelDir.X, 0).Normalize();

        var (bl, br) = OrderedEndpointsAtZ(firstRiser, perpDir, baseZ);
        var (tl, tr) = OrderedEndpointsAtZ(lastRiser,  perpDir, topZ);

        double width = bl.DistanceTo(br);
        double rise  = topZ - baseZ;

        if (width < EngineConfig.MinEdgeFt || rise < 0 ||
            tl.DistanceTo(tr) < EngineConfig.MinEdgeFt ||
            br.DistanceTo(tr) < EngineConfig.MinEdgeFt)
        {
            log.Add($"[WARN] Run {run.Id} {label}: degenerate flight geometry skipped " +
                    $"(width={width * 304.8:F0}mm, rise={rise * 304.8:F0}mm).");
            return null;
        }

        // CCW order: bottom-left → bottom-right → top-right → top-left
        var rawCorners = new List<XYZ> { bl, br, tr, tl };

        // Shift to mid-surface.
        var corners = MidSurfaceOffset.Apply(rawCorners, context.ThicknessFt);

        log.Add($"[DEBUG] Run {run.Id} {label}: flight panel — " +
                $"width={width * 304.8:F0}mm rise={rise * 304.8:F0}mm " +
                $"midOffset={(context.ThicknessFt / 2 * 304.8):F0}mm.");

        return new PanelGeometry
        {
            Corners         = corners,
            Thickness       = context.ThicknessFt,
            MaterialId      = context.MaterialId,
            Role            = PanelRole.Flight,
            SourceElementId = run.Id,
            Label           = $"Run {run.Id} ({label})"
        };
    }

    // ------------------------------------------------------------------
    // Landing quad (between two consecutive flights in a sketched run)
    // ------------------------------------------------------------------

    private static PanelGeometry? BuildLandingQuad(
        Curve prevLastRiser, Curve nextFirstRiser,
        double z,
        StairsRun run, StairParameterContext context,
        string label, List<string> log)
    {
        // Four corners: two endpoints of each riser projected to z.
        var c4 = new List<XYZ>
        {
            new XYZ(prevLastRiser.GetEndPoint(0).X, prevLastRiser.GetEndPoint(0).Y, z),
            new XYZ(prevLastRiser.GetEndPoint(1).X, prevLastRiser.GetEndPoint(1).Y, z),
            new XYZ(nextFirstRiser.GetEndPoint(1).X, nextFirstRiser.GetEndPoint(1).Y, z),
            new XYZ(nextFirstRiser.GetEndPoint(0).X, nextFirstRiser.GetEndPoint(0).Y, z),
        };

        var ordered = OrderCornersCCW(c4);
        if (ordered.Count < 3)
        {
            log.Add($"[WARN] Run {run.Id} {label}: degenerate landing quad skipped.");
            return null;
        }

        // Flat panel → purely vertical offset.
        var corners = MidSurfaceOffset.ApplyHorizontal(ordered, context.ThicknessFt);

        log.Add($"[DEBUG] Run {run.Id} {label}: landing quad at z={z * 304.8:F0}mm.");

        return new PanelGeometry
        {
            Corners         = corners,
            Thickness       = context.ThicknessFt,
            MaterialId      = context.MaterialId,
            Role            = PanelRole.Landing,
            SourceElementId = run.Id,
            Label           = $"Run {run.Id} ({label})"
        };
    }

    // ------------------------------------------------------------------
    // Geometry utilities
    // ------------------------------------------------------------------

    private static XYZ GetTravelDir(StairsRun run, StairNode node, List<string> log)
    {
        try
        {
            var path = run.GetStairsPath();
            if (path != null)
            {
                var curves = path.ToList();
                if (curves.Count > 0)
                {
                    var first = curves[0].GetEndPoint(0);
                    var last  = curves[^1].GetEndPoint(1);
                    var dir   = last.Subtract(first);
                    if (dir.GetLength() > 0.001)
                        return new XYZ(dir.X, dir.Y, 0).Normalize(); // horizontal component
                }
            }
        }
        catch { /* ignore */ }

        // Fallback: base→top of the node's bounding box (crude).
        log.Add($"[DEBUG] Run {run.Id}: travel direction from bounding box (path unavailable).");
        var bb = run.get_BoundingBox(null);
        if (bb != null)
        {
            var diag = bb.Max.Subtract(bb.Min);
            if (diag.GetLength() > 0.001)
                return new XYZ(diag.X, diag.Y, 0).Normalize();
        }
        return new XYZ(1, 0, 0); // last resort
    }

    private static XYZ RiserMidpoint(Curve r)
        => (r.GetEndPoint(0) + r.GetEndPoint(1)) / 2.0;

    /// <summary>
    /// Return the two riser endpoints ordered by projection onto
    /// <paramref name="perpDir"/> (left = lower, right = higher),
    /// both lifted to <paramref name="z"/>.
    /// </summary>
    private static (XYZ left, XYZ right) OrderedEndpointsAtZ(
        Curve riser, XYZ perpDir, double z)
    {
        var a = riser.GetEndPoint(0);
        var b = riser.GetEndPoint(1);
        if (a.DotProduct(perpDir) > b.DotProduct(perpDir)) (a, b) = (b, a);
        return (new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z));
    }

    /// <summary>
    /// Re-order 4 coplanar points into a convex CCW polygon (viewed from +Z).
    /// Uses the centroid-angle method, which works for any convex quad.
    /// </summary>
    private static List<XYZ> OrderCornersCCW(List<XYZ> pts)
    {
        if (pts.Count != 4) return pts;
        double cx = pts.Average(p => p.X);
        double cy = pts.Average(p => p.Y);
        return pts
            .OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx))
            .ToList();
    }
}
