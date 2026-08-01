using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using StructuralTools.StaircaseEngine;

namespace StructuralTools.SketchEngine;

/// <summary>
/// Builds <see cref="PanelGeometry"/> objects for a <see cref="StairsRun"/>
/// following the algorithm from the spec document.
///
/// Algorithm
/// ---------
/// <b>Simple run</b> (path is a single line segment — the common case):
///   1. <c>Boundary ← SketchExtractor.GetRunBoundary()</c>
///      (tries <c>run.SketchId → Sketch.Profile</c> first, then reflection).
///   2. Sort boundary corners by projection along travel direction.
///   3. Two lowest-projection corners → leading edge at <c>node.BaseElevation</c>.
///      Two highest-projection corners → trailing edge at <c>node.TopElevation</c>.
///   4. Apply <see cref="MidSurfaceOffset.Apply"/> — shift every corner by
///      <c>−slabNormal × (thickness/2)</c> along the inclined slab normal.
///
/// <b>Sketched multi-flight run</b> (path has multiple segments — dogleg / U-shape
/// modelled as a single <see cref="StairsRun"/>):
///   1. Get riser curves and sort along travel direction.
///   2. Detect flight / landing gaps: consecutive riser spacing &gt; 2.5× median.
///   3. Build one inclined panel per flight (first riser → leading edge, last →
///      trailing edge), and one flat quad per in-run landing.
///   4. Apply <see cref="MidSurfaceOffset.Apply"/> to every panel.
///
/// Fallback
/// --------
/// Returns <c>null</c> when neither the boundary nor riser curves are
/// available.  The caller (<see cref="SketchEngineStrategy"/>) then falls
/// back transparently to <see cref="StraightEngine"/>.
///
/// Note on inclination derivation
/// --------------------------------
/// Per spec: "don't derive the run inclination from the boundary edges."
/// Inclination is always computed from <c>node.BaseElevation</c> /
/// <c>node.TopElevation</c> (the run's built-in level data), never from the
/// XY spread of the boundary polygon.
/// </summary>
public static class RunPanelBuilder
{
    private const double LandingSpacingMultiplier = 2.5;

    /// <summary>
    /// Build all panels for the run.  Returns <c>null</c> on total failure.
    /// </summary>
    public static List<PanelGeometry>? Build(
        StairsRun run,
        Document doc,
        StairNode node,
        StairParameterContext context,
        List<string> log)
    {
        XYZ travelDir = GetTravelDir(run, node, log);

        // Determine whether this is a simple or multi-segment run.
        var pathCurves = SafeGetPath(run);
        bool isMultiSegment = pathCurves.Count > 1;

        if (!isMultiSegment)
            return BuildFromBoundary(run, doc, node, travelDir, context, log);
        else
            return BuildFromRisers(run, node, travelDir, context, log);
    }

    // ==================================================================
    // SIMPLE RUN — boundary-based (spec primary path)
    // ==================================================================

    /// <summary>
    /// Single-flight run: get the plan boundary, lift leading edge to
    /// <c>baseZ</c>, trailing edge to <c>topZ</c>, then offset to mid-surface.
    /// </summary>
    private static List<PanelGeometry>? BuildFromBoundary(
        StairsRun run,
        Document doc,
        StairNode node,
        XYZ travelDir,
        StairParameterContext context,
        List<string> log)
    {
        // Step 1 — get plan boundary (Sketch.Profile primary, reflection fallback)
        var boundary = SketchExtractor.GetRunBoundary(run, doc, log);
        if (boundary == null)
        {
            log.Add($"[WARN] Run {run.Id}: boundary unavailable — trying riser fallback.");
            return BuildFromRisers(run, node, travelDir, context, log);
        }

        // Step 2 — extract corners (start points of each boundary curve)
        var pts = new List<XYZ>();
        foreach (var c in boundary)
            pts.Add(c.GetEndPoint(0));

        if (pts.Count < 4)
        {
            log.Add($"[WARN] Run {run.Id}: boundary has {pts.Count} point(s) — need ≥4. Trying riser fallback.");
            return BuildFromRisers(run, node, travelDir, context, log);
        }

        double baseZ = node.BaseElevation;
        double topZ  = node.TopElevation;

        // Step 3 — sort by travel projection; split into leading (2 lowest)
        // and trailing (2 highest) edge pairs.
        var sorted = pts.OrderBy(p => p.DotProduct(travelDir)).ToList();

        var leadPts  = sorted.Take(2).ToList();
        var trailPts = sorted.TakeLast(2).ToList();

        // Order each edge pair left-to-right (by perpendicular projection).
        XYZ perpDir = PerpDir(travelDir);
        leadPts  = leadPts.OrderBy(p => p.DotProduct(perpDir)).ToList();
        trailPts = trailPts.OrderBy(p => p.DotProduct(perpDir)).ToList();

        // Lift to 3-D: leading edge at baseZ, trailing edge at topZ.
        XYZ bl = new XYZ(leadPts[0].X,  leadPts[0].Y,  baseZ);
        XYZ br = new XYZ(leadPts[1].X,  leadPts[1].Y,  baseZ);
        XYZ tl = new XYZ(trailPts[0].X, trailPts[0].Y, topZ);
        XYZ tr = new XYZ(trailPts[1].X, trailPts[1].Y, topZ);

        // Step 4 — validate minimum edge lengths.
        if (!ValidQuad(bl, br, tl, tr, run.Id, log))
            return BuildFromRisers(run, node, travelDir, context, log);

        // CCW order: bl → br → tr → tl (viewed from outside / above).
        var rawCorners = new List<XYZ> { bl, br, tr, tl };

        // Step 5 — offset to waist mid-surface along inclined slab normal.
        var corners = MidSurfaceOffset.Apply(rawCorners, context.ThicknessFt);

        double width = bl.DistanceTo(br);
        double rise  = topZ - baseZ;
        log.Add($"[INFO] Run {run.Id}: boundary panel — " +
                $"width={width * 304.8:F0}mm rise={rise * 304.8:F0}mm " +
                $"midOffset={(context.ThicknessFt / 2 * 304.8):F0}mm.");

        return new List<PanelGeometry>
        {
            new PanelGeometry
            {
                Corners         = corners,
                Thickness       = context.ThicknessFt,
                MaterialId      = context.MaterialId,
                Role            = PanelRole.Flight,
                SourceElementId = run.Id,
                Label           = $"Run {run.Id} (boundary)"
            }
        };
    }

    // ==================================================================
    // MULTI-FLIGHT RUN — riser-based flight/landing detection
    // ==================================================================

    /// <summary>
    /// Multi-segment (sketched dogleg/U-shape) run: sort all riser curves,
    /// partition into flight groups and in-run landing gaps, produce one
    /// panel per group, then offset all panels to mid-surface.
    /// </summary>
    private static List<PanelGeometry>? BuildFromRisers(
        StairsRun run,
        StairNode node,
        XYZ travelDir,
        StairParameterContext context,
        List<string> log)
    {
        var risers = SketchExtractor.GetSortedRisers(run, travelDir, log);
        if (risers == null || risers.Count < 2)
        {
            log.Add($"[WARN] Run {run.Id}: <2 riser curves — cannot build panels.");
            return null;
        }

        // Median consecutive-riser spacing = expected tread depth.
        var spacings = Enumerable.Range(0, risers.Count - 1)
            .Select(i => Math.Abs(
                RiserMid(risers[i + 1]).DotProduct(travelDir) -
                RiserMid(risers[i]).DotProduct(travelDir)))
            .OrderBy(x => x)
            .ToList();

        double median           = spacings[spacings.Count / 2];
        double landingThreshold = median > 0
            ? median * LandingSpacingMultiplier
            : double.MaxValue;

        double baseZ      = node.BaseElevation;
        double topZ       = node.TopElevation;
        double totalRise  = topZ - baseZ;
        double currentZ   = baseZ;
        var    panels     = new List<PanelGeometry>();
        int    flightStart = 0;

        for (int i = 0; i < risers.Count; i++)
        {
            bool isLast        = i == risers.Count - 1;
            bool nextIsLanding = !isLast && (
                Math.Abs(RiserMid(risers[i + 1]).DotProduct(travelDir) -
                         RiserMid(risers[i]).DotProduct(travelDir))
                > landingThreshold);

            if (!isLast && !nextIsLanding) continue;

            // Build inclined panel for risers[flightStart..i].
            double flightProportion = (double)(i - flightStart + 1) / risers.Count;
            double flightRise       = totalRise * flightProportion;
            double flightBaseZ      = currentZ;
            double flightTopZ       = currentZ + flightRise;

            var fp = BuildFlightPanel(
                risers[flightStart], risers[i],
                travelDir, flightBaseZ, flightTopZ,
                run, context, $"flight-{flightStart}-{i}", log);
            if (fp != null) panels.Add(fp);
            currentZ = flightTopZ;

            // Build flat landing quad between this flight and the next.
            if (nextIsLanding && i + 1 < risers.Count)
            {
                var lp = BuildLandingQuad(
                    risers[i], risers[i + 1],
                    currentZ, run, context,
                    $"landing-after-{i}", log);
                if (lp != null) panels.Add(lp);
            }

            flightStart = i + 1;
        }

        if (panels.Count == 0) return null;
        log.Add($"[INFO] Run {run.Id}: riser panels — {panels.Count} panel(s).");
        return panels;
    }

    // ------------------------------------------------------------------
    // Flight panel (riser-based)
    // ------------------------------------------------------------------

    private static PanelGeometry? BuildFlightPanel(
        Curve firstRiser, Curve lastRiser,
        XYZ travelDir, double baseZ, double topZ,
        StairsRun run, StairParameterContext context,
        string label, List<string> log)
    {
        XYZ perpDir = PerpDir(travelDir);

        var (bl, br) = RiserEndpointsAtZ(firstRiser, perpDir, baseZ);
        var (tl, tr) = RiserEndpointsAtZ(lastRiser,  perpDir, topZ);

        if (!ValidQuad(bl, br, tl, tr, run.Id, log)) return null;

        var corners = MidSurfaceOffset.Apply(
            new List<XYZ> { bl, br, tr, tl }, context.ThicknessFt);

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
    // Landing quad (riser-based, between two flights)
    // ------------------------------------------------------------------

    private static PanelGeometry? BuildLandingQuad(
        Curve prevLastRiser, Curve nextFirstRiser,
        double z, StairsRun run, StairParameterContext context,
        string label, List<string> log)
    {
        var c4 = new List<XYZ>
        {
            new XYZ(prevLastRiser.GetEndPoint(0).X,  prevLastRiser.GetEndPoint(0).Y,  z),
            new XYZ(prevLastRiser.GetEndPoint(1).X,  prevLastRiser.GetEndPoint(1).Y,  z),
            new XYZ(nextFirstRiser.GetEndPoint(1).X, nextFirstRiser.GetEndPoint(1).Y, z),
            new XYZ(nextFirstRiser.GetEndPoint(0).X, nextFirstRiser.GetEndPoint(0).Y, z),
        };

        var ordered = OrderCCW(c4);
        if (ordered.Count < 3) return null;

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

    // ==================================================================
    // Geometry utilities
    // ==================================================================

    private static XYZ GetTravelDir(StairsRun run, StairNode node, List<string> log)
    {
        foreach (var seg in SafeGetPath(run))
        {
            var v = seg.GetEndPoint(1).Subtract(seg.GetEndPoint(0));
            if (v.GetLength() > 0.001)
                return new XYZ(v.X, v.Y, 0).Normalize();
        }

        log.Add($"[DEBUG] Run {run.Id}: travel direction from bounding box (path unavailable).");
        var bb = run.get_BoundingBox(null);
        if (bb != null)
        {
            var d = bb.Max.Subtract(bb.Min);
            if (d.GetLength() > 0.001) return new XYZ(d.X, d.Y, 0).Normalize();
        }
        return new XYZ(1, 0, 0);
    }

    private static List<Curve> SafeGetPath(StairsRun run)
    {
        try { return run.GetStairsPath()?.ToList() ?? new List<Curve>(); }
        catch { return new List<Curve>(); }
    }

    private static XYZ PerpDir(XYZ dir)
        => new XYZ(-dir.Y, dir.X, 0).Normalize();

    private static XYZ RiserMid(Curve r)
        => (r.GetEndPoint(0) + r.GetEndPoint(1)) / 2.0;

    private static (XYZ left, XYZ right) RiserEndpointsAtZ(
        Curve riser, XYZ perpDir, double z)
    {
        var a = riser.GetEndPoint(0);
        var b = riser.GetEndPoint(1);
        if (a.DotProduct(perpDir) > b.DotProduct(perpDir)) (a, b) = (b, a);
        return (new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z));
    }

    private static bool ValidQuad(
        XYZ bl, XYZ br, XYZ tl, XYZ tr, ElementId id, List<string> log)
    {
        double min = EngineConfig.MinEdgeFt;
        if (bl.DistanceTo(br) < min || tl.DistanceTo(tr) < min ||
            br.DistanceTo(tr) < min || bl.DistanceTo(tl) < min)
        {
            log.Add($"[WARN] Run {id}: degenerate quad skipped.");
            return false;
        }
        return true;
    }

    private static List<XYZ> OrderCCW(List<XYZ> pts)
    {
        double cx = pts.Average(p => p.X);
        double cy = pts.Average(p => p.Y);
        return pts.OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx)).ToList();
    }
}
