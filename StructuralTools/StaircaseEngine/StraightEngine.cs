using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System.IO;

namespace StructuralTools.StaircaseEngine;

/// <summary>
/// Engine A — handles all straight <see cref="StairsRun"/> elements AND
/// all <see cref="StairsLanding"/> elements.
///
/// For a landing: builds a flat polygon panel from <c>GetFootprintBoundary()</c>.
/// For a straight run: builds a 4-corner inclined quad from the stairs path
/// and run width, with the leading edge at <c>BaseElevation</c> and the
/// trailing edge at <c>TopElevation</c>.
/// For a sketched run (multi-segment path): decomposes into flight groups
/// (slanted) and landing groups (flat) based on direction changes.
/// </summary>
public class StraightEngine : IEngineStrategy
{
    /// <summary>
    /// Temporary diagnostics collector. The orchestrator drains this after
    /// each <see cref="BuildPanels"/> call and appends to the summary log.
    /// Also written to a file on the user's Desktop for easy reading.
    /// </summary>
    public static List<string> Diagnostics { get; } = new();

    /// <summary>Path to the diagnostic log file (overwritten each run).</summary>
    private static readonly string DiagFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "StructuralTools_StaircaseDiag.txt");

    private static void Diag(string msg)
    {
        System.Diagnostics.Debug.WriteLine(msg);
        Diagnostics.Add(msg);
        try { File.AppendAllText(DiagFilePath, msg + Environment.NewLine); }
        catch { /* ignore file errors */ }
    }

    /// <summary>
    /// Clear the diagnostic file at the start of a run. Call once before any
    /// <see cref="BuildPanels"/> calls in the batch.
    /// </summary>
    public static void ResetDiagFile()
    {
        try { File.WriteAllText(DiagFilePath, ""); }
        catch { /* ignore */ }
    }

    /// <summary>Path to the diagnostic file — shown in the summary dialog.</summary>
    public static string DiagFilePathPublic => DiagFilePath;

    public List<PanelGeometry> BuildPanels(Document doc, StairNode node, StairParameterContext context)
    {
        Diagnostics.Clear();
        return node.SourceElement switch
        {
            StairsLanding landing => BuildLandingPanel(doc, landing, node, context),
            StairsRun run          => BuildRunPanels(doc, run, node, context),
            _ => new List<PanelGeometry>()
        };
    }

    // ---------------------------------------------------------------------
    // Run dispatch — straight vs sketched
    // ---------------------------------------------------------------------

    /// <summary>
    /// Build panels for a <see cref="StairsRun"/>. Dispatches to
    /// <see cref="BuildSketchedRunPanels"/> if the path has multiple segments
    /// (typical for sketched runs covering a dogleg / U-shape in one element),
    /// otherwise falls back to <see cref="BuildStraightRunPanel"/>.
    /// </summary>
    private static List<PanelGeometry> BuildRunPanels(
        Document doc, StairsRun run, StairNode node, StairParameterContext context)
    {
        var pathCurves = run.GetStairsPath();

        // Multi-segment path → sketched run with multiple flights baked in.
        if (pathCurves != null && pathCurves.Count() > 1)
        {
            return BuildSketchedRunPanels(doc, run, node, context);
        }

        return BuildStraightRunPanel(doc, run, node, context);
    }

    // ---------------------------------------------------------------------
    // Landing
    // ---------------------------------------------------------------------

    private static List<PanelGeometry> BuildLandingPanel(
        Document doc, StairsLanding landing, StairNode node, StairParameterContext context)
    {
        var panels = new List<PanelGeometry>();
        double z = node.BaseElevation;

        // Try GetFootprintBoundary().
        CurveLoop? outer = TryGetFootprintBoundary(landing);
        if (outer == null)
        {
            // Fallback: bounding-box rectangle.
            outer = TryGetBoundingBoxRectangle(landing, z);
        }

        if (outer == null) return panels;

        // Extract corners from the loop, projected to z.
        // CurveLoop is already closed (last curve's end == first curve's start),
        // so we only need each curve's start point.
        var corners = new List<XYZ>();
        foreach (var c in outer)
        {
            var p = c.GetEndPoint(0);
            corners.Add(new XYZ(p.X, p.Y, z));
        }

        if (corners.Count < 3) return panels;

        // If non-rectangular (>4 corners), tessellate into triangles via fan triangulation.
        if (corners.Count > 4)
        {
            for (int i = 1; i < corners.Count - 1; i++)
            {
                panels.Add(MakePanel(
                    new[] { corners[0], corners[i], corners[i + 1] },
                    landing, PanelRole.Landing, context, "tri"));
            }
            return panels;
        }

        panels.Add(MakePanel(corners.ToArray(), landing, PanelRole.Landing, context, "land"));
        return panels;
    }

    // ---------------------------------------------------------------------
    // Sketched run (multi-flight — the most common case in practice)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Build panels for a sketched run whose path has multiple segments.
    ///
    /// A sketched run can contain an entire dogleg / U-shape / L-shape as a
    /// single <see cref="StairsRun"/> element.
    ///
    /// Algorithm:
    ///   1. Get the run's footprint boundary via <c>GetFootprintBoundary()</c>.
    ///      This returns the ACTUAL outline of the run (not a centreline
    ///      approximation) — correct dimensions and orientation.
    ///   2. Extract path segments from <c>GetStairsPath()</c> to determine
    ///      flight directions and where landings occur (direction changes).
    ///   3. For each flight group, find the footprint edges parallel to the
    ///      flight direction — those form the flight panel's side edges.
    ///      The leading edge is the footprint edge at the flight's start;
    ///      the trailing edge is at the flight's end.
    ///   4. For each landing group, use the footprint edges in the landing's
    ///      direction.
    ///   5. Lift flight edges to their correct Z (base → top) and build
    ///      slanted panels; keep landing panels flat.
    ///   6. Snap shared edges between consecutive panels.
    ///
    /// This produces panels that match the actual run geometry because they're
    /// derived from the footprint, not from a centreline + assumed width.
    /// </summary>
    private static List<PanelGeometry> BuildSketchedRunPanels(
        Document doc, StairsRun run, StairNode node, StairParameterContext context)
    {
        var panels = new List<PanelGeometry>();

        var pathCurves = run.GetStairsPath()?.ToList();
        if (pathCurves == null || pathCurves.Count == 0) return panels;

        // ---- Extract path segments ------------------------------------------
        var segments = new List<(XYZ start, XYZ end, XYZ dir, double len)>();
        foreach (var c in pathCurves)
        {
            var s = c.GetEndPoint(0);
            var e = c.GetEndPoint(1);
            var d = e - s;
            double l = d.GetLength();
            if (l < 0.001) continue;
            segments.Add((s, e, d.Normalize(), l));
        }

        if (segments.Count == 0) return panels;
        if (segments.Count == 1)
            return BuildStraightRunPanel(doc, run, node, context);

        // Get footprint boundary — used as fallback if riser approach unavailable.
        var footprintLoops = TryGetFootprintBoundaries(run);
        Diag($"[DIAG] Run {run.Id}: {segments.Count} path segment(s), " +
             $"footprint={footprintLoops?.Count ?? 0} loop(s).");

        if (footprintLoops == null || footprintLoops.Count == 0)
        {
            // Fallback: use the old path+width approach if footprint unavailable.
            return BuildSketchedRunPanelsFromPath(run, node, context, segments);
        }

        var footprint = footprintLoops[0];  // outer loop
        var footprintPts = footprint.Select(c => c.GetEndPoint(0)).ToList();

        // ---- Group path segments by direction --------------------------------
        var groups = new List<List<int>>();
        var currentGroup = new List<int> { 0 };

        for (int i = 1; i < segments.Count; i++)
        {
            double dot = segments[i].dir.DotProduct(segments[i - 1].dir);
            if (dot > 0.5)
            {
                currentGroup.Add(i);
            }
            else
            {
                groups.Add(currentGroup);
                currentGroup = new List<int> { i };
            }
        }
        groups.Add(currentGroup);

        // ---- Classify groups as flight or landing ----------------------------
        // Use alternating index (even = flight, odd = landing) rather than
        // dot-product against the overall start→end direction. The dot-product
        // approach fails for dogleg / U-shaped runs where the two flights travel
        // in OPPOSITE directions, giving a near-zero dot product with the
        // overall direction and misclassifying both flights as landings.
        //
        // In Revit, a sketched StairsRun always starts with a flight — the
        // sequence is invariably flight → landing → flight (→ landing → flight…).
        // Tie-breaking by length is a secondary safeguard for ambiguous cases.
        var groupIsFlight = new bool[groups.Count];
        var groupLens = new double[groups.Count];
        for (int g = 0; g < groups.Count; g++)
            groupLens[g] = groups[g].Sum(i => segments[i].len);

        if (groups.Count == 1)
        {
            groupIsFlight[0] = true;  // single group — must be a flight
        }
        else
        {
            // Primary rule: even indices are flights.
            for (int g = 0; g < groups.Count; g++)
                groupIsFlight[g] = (g % 2 == 0);

            // Secondary safeguard: if the length pattern strongly disagrees
            // (a short group at an even index, shorter than 40% of the longest
            // group), swap its classification. Landing connectors in a sketched
            // run are typically much shorter than the flights they join.
            double maxLen = groupLens.Max();
            for (int g = 0; g < groups.Count; g++)
            {
                bool isShort = groupLens[g] < maxLen * 0.40;
                // Flip if parity says "flight" but length says "landing".
                if (groupIsFlight[g] && isShort && groups.Count >= 3)
                    groupIsFlight[g] = false;
            }

            // Guarantee at least one flight exists.
            if (!Array.Exists(groupIsFlight, f => f))
            {
                int longest = Array.IndexOf(groupLens, groupLens.Max());
                groupIsFlight[longest] = true;
            }
        }

        // ---- Compute total flight length (for proportional rise) -----------
        double baseZ = node.BaseElevation;
        double topZ  = node.TopElevation;
        double totalRise = topZ - baseZ;

        double totalFlightLength = 0;
        var groupLengths = new double[groups.Count];

        for (int g = 0; g < groups.Count; g++)
        {
            double groupLen = groups[g].Sum(i => segments[i].len);
            groupLengths[g] = groupLen;
            if (groupIsFlight[g]) totalFlightLength += groupLen;
        }

        if (totalFlightLength < 0.001)
            return BuildSketchedRunPanelsFromPath(run, node, context, segments);

        // ---- PRIMARY: riser-based panels (most accurate for sketch stairs) ----
        // GetRiserCurves() returns the black riser lines from the stair sketch.
        // Flight panels span from first riser to last riser per group; landing
        // panels are bounded by the last riser of the preceding flight and the
        // first riser of the following flight, forming a flat quad at the
        // landing elevation. Returns null if no riser curves are available.
        var riserPanels = TryBuildSketchedPanelsFromRisers(
            run, node, context, segments, groups, groupIsFlight, groupLengths, totalFlightLength);
        if (riserPanels != null) return riserPanels;

        // ---- FALLBACK: footprint-based panels ----------------------------------------
        double currentZ = baseZ;

        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            double groupLen = groupLengths[g];
            bool isFlight = groupIsFlight[g];

            if (groupLen < EngineConfig.MinEdgeFt) continue;

            var groupStart = segments[group[0]].start;
            var groupEnd   = segments[group[^1]].end;
            var groupDir   = (groupEnd - groupStart).Normalize();

            // Find footprint corners that belong to this group:
            // corners whose projection onto groupDir falls within the group's
            // extent along the path.
            double groupStartProj = groupStart.DotProduct(groupDir);
            double groupEndProj   = groupEnd.DotProduct(groupDir);
            double loProj = Math.Min(groupStartProj, groupEndProj);
            double hiProj = Math.Max(groupStartProj, groupEndProj);

            // Expand slightly to include corners right at the boundary.
            double tol = 0.05;
            var groupCorners = footprintPts
                .Where(p =>
                {
                    double proj = p.DotProduct(groupDir);
                    return proj >= loProj - tol && proj <= hiProj + tol;
                })
                .ToList();

            if (groupCorners.Count < 3) continue;

            if (isFlight)
            {
                // Slanted panel: distribute rise proportionally.
                double flightRise = totalRise * (groupLen / totalFlightLength);
                double flightBaseZ = currentZ;
                double flightTopZ  = currentZ + flightRise;

                // Assign Z based on each corner's projection along groupDir.
                // Corners near groupStart → flightBaseZ; corners near groupEnd → flightTopZ.
                var slantedCorners = groupCorners.Select(p =>
                {
                    double proj = p.DotProduct(groupDir);
                    double t = (proj - loProj) / Math.Max(hiProj - loProj, 0.001);
                    t = Math.Max(0, Math.Min(1, t));
                    double z = flightBaseZ + t * (flightTopZ - flightBaseZ);
                    return new XYZ(p.X, p.Y, z);
                }).ToList();

                panels.Add(new PanelGeometry
                {
                    Corners         = OrderCornersCCW(slantedCorners),
                    Thickness       = context.ThicknessFt,
                    MaterialId      = context.MaterialId,
                    Role            = PanelRole.Flight,
                    SourceElementId = run.Id,
                    Label           = $"Flight {run.Id} (sketched flight {g + 1})"
                });
                currentZ = flightTopZ;
            }
            else
            {
                // Flat landing panel at currentZ.
                var flatCorners = groupCorners
                    .Select(p => new XYZ(p.X, p.Y, currentZ))
                    .ToList();

                panels.Add(new PanelGeometry
                {
                    Corners         = OrderCornersCCW(flatCorners),
                    Thickness       = context.ThicknessFt,
                    MaterialId      = context.MaterialId,
                    Role            = PanelRole.Landing,
                    SourceElementId = run.Id,
                    Label           = $"Landing {run.Id} (sketched landing {g + 1})"
                });
            }
        }

        SnapConsecutivePanelEdges(panels);
        return panels;
    }

    /// <summary>
    /// Fallback: build sketched-run panels from path + width when
    /// <c>GetFootprintBoundary()</c> is unavailable.
    ///
    /// For sketched runs, <c>ActualRunWidth</c> is used for ALL panels
    /// (flights and landing) — the landing in a dogleg is the same width as
    /// the flights, just a flat panel connecting them. The bounding-box
    /// width is NOT used because the run's bbox includes both flights,
    /// which would produce a landing panel that's far too wide.
    /// </summary>
    private static List<PanelGeometry> BuildSketchedRunPanelsFromPath(
        StairsRun run, StairNode node, StairParameterContext context,
        List<(XYZ start, XYZ end, XYZ dir, double len)> segments)
    {
        var panels = new List<PanelGeometry>();

        // Group by direction.
        var groups = new List<List<int>>();
        var currentGroup = new List<int> { 0 };
        for (int i = 1; i < segments.Count; i++)
        {
            if (segments[i].dir.DotProduct(segments[i - 1].dir) > 0.5)
                currentGroup.Add(i);
            else
            {
                groups.Add(currentGroup);
                currentGroup = new List<int> { i };
            }
        }
        groups.Add(currentGroup);

        var overallDir = (segments[^1].end - segments[0].start).Normalize();
        var groupIsFlight = new bool[groups.Count];
        for (int g = 0; g < groups.Count; g++)
        {
            var groupDir = segments[groups[g][0]].dir;
            groupIsFlight[g] = Math.Abs(groupDir.DotProduct(overallDir)) > 0.5;
        }
        if (!Array.Exists(groupIsFlight, f => !f) && groups.Count > 1)
            for (int g = 0; g < groups.Count; g++)
                groupIsFlight[g] = (g % 2 == 0);

        double baseZ = node.BaseElevation;
        double topZ  = node.TopElevation;
        double totalRise = topZ - baseZ;
        double totalFlightLength = 0;
        var groupLengths = new double[groups.Count];
        for (int g = 0; g < groups.Count; g++)
        {
            groupLengths[g] = groups[g].Sum(i => segments[i].len);
            if (groupIsFlight[g]) totalFlightLength += groupLengths[g];
        }
        if (totalFlightLength < 0.001) return panels;

        // Use the run width from the instance property or type parameter.
        // For sketched runs, ActualRunWidth is often 0 — try the type parameter.
        double runWidth = StairParameterExtractor.GetRunWidth(run);

        if (runWidth < EngineConfig.MinEdgeFt)
        {
            // Width not found via property or type parameter.
            // Log ALL width-related parameters so we can find the right one.
            Diag($"[DIAG] Run width not found via ActualRunWidth or type parameters.");
            StairParameterExtractor.LogWidthParameters(run, Diagnostics);

            // Last-resort fallback: default 4 ft.
            runWidth = EngineConfig.FallbackRunWidth;
            Diag($"[DIAG] Using default fallback width {runWidth:F3} ft.");
        }
        else
        {
            Diag($"[DIAG] Using run width {runWidth:F3} ft (from "
                 + (run.ActualRunWidth > 0 ? "ActualRunWidth" : "type parameter")
                 + ") for all panels.");
        }

        double halfWidth = runWidth / 2.0;

        double currentZ = baseZ;

        for (int g = 0; g < groups.Count; g++)
        {
            double groupLen = groupLengths[g];
            if (groupLen < EngineConfig.MinEdgeFt) continue;

            var groupStart = segments[groups[g][0]].start;
            var groupEnd   = segments[groups[g][^1]].end;

            if (groupIsFlight[g])
            {
                double flightRise = totalRise * (groupLen / totalFlightLength);
                var panel = BuildSlantedPanel(
                    groupStart, groupEnd, currentZ, currentZ + flightRise,
                    halfWidth, run, context, $"sketched flight {g + 1} (path)");
                if (panel != null)
                {
                    panels.Add(panel);
                    Diag($"[DIAG]   Built flight panel {g + 1}: " +
                         $"{groupStart.X:F3},{groupStart.Y:F3} (z={currentZ:F3}) → " +
                         $"{groupEnd.X:F3},{groupEnd.Y:F3} (z={currentZ + flightRise:F3}), " +
                         $"width={runWidth:F3}, rise={flightRise:F3}");
                    currentZ += flightRise;
                }
                else
                {
                    Diag($"[DIAG]   Flight panel {g + 1}: BuildSlantedPanel returned null.");
                }
            }
            else
            {
                var panel = BuildFlatPanel(
                    groupStart, groupEnd, currentZ,
                    halfWidth, run, context, $"sketched landing {g + 1} (path)");
                if (panel != null)
                {
                    panels.Add(panel);
                    Diag($"[DIAG]   Built landing panel {g + 1}: " +
                         $"{groupStart.X:F3},{groupStart.Y:F3} → " +
                         $"{groupEnd.X:F3},{groupEnd.Y:F3} (z={currentZ:F3}), " +
                         $"width={runWidth:F3}");
                }
                else
                {
                    Diag($"[DIAG]   Landing panel {g + 1}: BuildFlatPanel returned null.");
                }
            }
        }

        SnapConsecutivePanelEdges(panels);
        return panels;
    }

    /// <summary>
    /// Order a list of XY points counter-clockwise (for valid CurveLoop winding).
    /// Uses the convex-hull-free centroid-angle method.
    /// </summary>
    private static List<XYZ> OrderCornersCCW(List<XYZ> pts)
    {
        if (pts.Count <= 3) return pts;

        // Compute centroid in XY.
        double cx = pts.Average(p => p.X);
        double cy = pts.Average(p => p.Y);

        // Sort by angle from centroid (in XY plane).
        return pts
            .OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx))
            .ToList();
    }

    // ---------------------------------------------------------------------
    // Panel construction helpers (shared by straight + sketched)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Build a slanted (inclined) quad panel from a path start/end and
    /// base/top elevations. Returns null if any edge is too short.
    /// </summary>
    private static PanelGeometry? BuildSlantedPanel(
        XYZ pathStart, XYZ pathEnd,
        double baseZ, double topZ,
        double halfWidth,
        StairsRun run, StairParameterContext context, string label)
    {
        XYZ dir = pathEnd - pathStart;
        if (dir.GetLength() < EngineConfig.MinEdgeFt) return null;
        dir = dir.Normalize();

        XYZ perp = new XYZ(-dir.Y, dir.X, 0) * halfWidth;

        var bl = new XYZ(pathStart.X - perp.X, pathStart.Y - perp.Y, baseZ);
        var br = new XYZ(pathStart.X + perp.X, pathStart.Y + perp.Y, baseZ);
        var tr = new XYZ(pathEnd.X   + perp.X, pathEnd.Y   + perp.Y, topZ);
        var tl = new XYZ(pathEnd.X   - perp.X, pathEnd.Y   - perp.Y, topZ);

        // Validate edges.
        if (bl.DistanceTo(br) < EngineConfig.MinEdgeFt) return null;
        if (tr.DistanceTo(tl) < EngineConfig.MinEdgeFt) return null;
        if (br.DistanceTo(tr) < EngineConfig.MinEdgeFt) return null;

        return new PanelGeometry
        {
            Corners         = new List<XYZ> { bl, br, tr, tl },
            Thickness       = context.ThicknessFt,
            MaterialId      = context.MaterialId,
            Role            = PanelRole.Flight,
            SourceElementId = run.Id,
            Label           = $"Flight {run.Id} ({label})"
        };
    }

    /// <summary>
    /// Build a flat (horizontal) quad panel from a path start/end at a given Z.
    /// Returns null if any edge is too short.
    /// </summary>
    private static PanelGeometry? BuildFlatPanel(
        XYZ pathStart, XYZ pathEnd, double z,
        double halfWidth,
        StairsRun run, StairParameterContext context, string label)
    {
        XYZ dir = pathEnd - pathStart;
        if (dir.GetLength() < EngineConfig.MinEdgeFt) return null;
        dir = dir.Normalize();

        XYZ perp = new XYZ(-dir.Y, dir.X, 0) * halfWidth;

        var c1 = new XYZ(pathStart.X - perp.X, pathStart.Y - perp.Y, z);
        var c2 = new XYZ(pathStart.X + perp.X, pathStart.Y + perp.Y, z);
        var c3 = new XYZ(pathEnd.X   + perp.X, pathEnd.Y   + perp.Y, z);
        var c4 = new XYZ(pathEnd.X   - perp.X, pathEnd.Y   - perp.Y, z);

        if (c1.DistanceTo(c2) < EngineConfig.MinEdgeFt) return null;
        if (c3.DistanceTo(c4) < EngineConfig.MinEdgeFt) return null;
        if (c2.DistanceTo(c3) < EngineConfig.MinEdgeFt) return null;

        return new PanelGeometry
        {
            Corners         = new List<XYZ> { c1, c2, c3, c4 },
            Thickness       = context.ThicknessFt,
            MaterialId      = context.MaterialId,
            Role            = PanelRole.Landing,
            SourceElementId = run.Id,
            Label           = $"Landing {run.Id} ({label})"
        };
    }

    // ---------------------------------------------------------------------
    // Internal sketched-run connectivity snapping
    // ---------------------------------------------------------------------

    /// <summary>
    /// Snap shared edges between consecutive panels produced by a sketched run.
    /// For each pair (panel[i], panel[i+1]), snaps the 2 corners of panel[i+1]
    /// that are closest to panel[i] to panel[i]'s 2 corners that are closest to
    /// panel[i+1], if within <see cref="EngineConfig.EdgeSnapTolerance"/>.
    ///
    /// Uses proximity rather than Z-sort so that flat landing panels (all corners
    /// at the same Z) are handled correctly — Z-sort picks arbitrary corners for
    /// flat panels and would miss the actual shared edge.
    /// </summary>
    private static void SnapConsecutivePanelEdges(List<PanelGeometry> panels)
    {
        for (int i = 0; i < panels.Count - 1; i++)
        {
            var lower = panels[i];
            var upper = panels[i + 1];

            // Lower panel's 2 trailing corners: those closest to the upper panel.
            var lowerTrailing = lower.Corners
                .OrderBy(lc => upper.Corners.Min(uc => lc.DistanceTo(uc)))
                .Take(2)
                .ToList();

            // Upper panel's 2 leading corner indices: those closest to the lower panel.
            var upperLeadingIndices = upper.Corners
                .Select((c, idx) => (c, idx, dist: lower.Corners.Min(lc => c.DistanceTo(lc))))
                .OrderBy(t => t.dist)
                .Take(2)
                .Select(t => t.idx)
                .ToList();

            // Snap each upper leading corner to the nearest lower trailing corner.
            foreach (int ui in upperLeadingIndices)
            {
                var uc = upper.Corners[ui];
                XYZ? nearest = null;
                double nearestDist = double.MaxValue;

                foreach (var lc in lowerTrailing)
                {
                    double d = uc.DistanceTo(lc);
                    if (d < nearestDist)
                    {
                        nearestDist = d;
                        nearest = lc;
                    }
                }

                if (nearest != null && nearestDist < EngineConfig.EdgeSnapTolerance)
                {
                    upper.Corners[ui] = nearest;
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // Straight run (single-segment path)
    // ---------------------------------------------------------------------

    private static List<PanelGeometry> BuildStraightRunPanel(
        Document doc, StairsRun run, StairNode node, StairParameterContext context)
    {
        var panels = new List<PanelGeometry>();

        // ---- Determine run direction from path --------------------------------
        // GetStairsPath() is used for orientation only.  The actual corner XY
        // positions come from GetFootprintBoundary() (see below), NOT from the
        // path endpoints.  GetStairsPath() returns the nosing centreline, which
        // starts/ends ~one tread-depth inside the structural run boundary;
        // using path endpoints directly causes three compounding errors:
        //   1. Width   — path centre ≠ run centre → asymmetric extrusion.
        //   2. Gradient — rise / nosing-span is steeper than rise / slab-span.
        //   3. Transition — trailing edge lands short of the landing boundary
        //      (gap ≈ tread depth >> 0.01 ft snap tolerance → never closes).
        var pathCurves = run.GetStairsPath()?.ToList();
        if (pathCurves == null || pathCurves.Count == 0)
        {
            Diag($"[StructuralTools] Run {run.Id}: GetStairsPath() returned empty.");
            return panels;
        }

        var pathP0 = pathCurves.First().GetEndPoint(0);
        var pathP1 = pathCurves.Last().GetEndPoint(1);
        XYZ rawDir  = pathP1 - pathP0;
        if (rawDir.GetLength() < EngineConfig.MinEdgeFt)
        {
            Diag($"[StructuralTools] Run {run.Id}: path direction degenerate.");
            return panels;
        }
        XYZ dir     = rawDir.Normalize();
        XYZ perpDir = new XYZ(-dir.Y, dir.X, 0);

        double baseZ = node.BaseElevation;
        double topZ  = node.TopElevation;

        // ---- PRIMARY: sketch riser lines ----------------------------------------
        // GetRiserCurves() returns the black riser lines drawn in the stair sketch.
        // Each riser spans the full run width boundary-to-boundary, giving exact
        // width, correct horizontal run length, and structural-boundary positions
        // (first riser = leading edge, last riser = trailing edge).
        var riserPanel = TryBuildPanelFromRisers(run, dir, perpDir, baseZ, topZ, context,
            $"risers-{run.Id}");
        if (riserPanel != null)
        {
            panels.Add(riserPanel);
            return panels;
        }

        // ---- SECONDARY: corners from footprint boundary -------------------------
        // GetFootprintBoundary() returns the true plan outline of the structural
        // slab: from the face of the first riser to the back of the last tread.
        // This is the same boundary that the adjacent landing uses, so run and
        // landing trailing/leading edges share the same XY coordinates and the
        // ConnectivityResolver snap works within floating-point tolerance.
        var footprintLoops = TryGetFootprintBoundaries(run);
        if (footprintLoops is { Count: > 0 })
        {
            var fpts = footprintLoops[0].Select(c => c.GetEndPoint(0)).ToList();
            if (fpts.Count >= 4)
            {
                // Sort all corners by projection along the run direction.
                var sorted = fpts.OrderBy(p => p.DotProduct(dir)).ToList();

                // 2 lowest-projection corners  → start (leading) edge at baseZ.
                // 2 highest-projection corners → end (trailing) edge at topZ.
                var startEdge = sorted.Take(2)
                    .OrderBy(p => p.DotProduct(perpDir)).ToList();
                var endEdge   = sorted.TakeLast(2)
                    .OrderBy(p => p.DotProduct(perpDir)).ToList();

                var bl = new XYZ(startEdge[0].X, startEdge[0].Y, baseZ);
                var br = new XYZ(startEdge[1].X, startEdge[1].Y, baseZ);
                var tr = new XYZ(endEdge[1].X,   endEdge[1].Y,   topZ);
                var tl = new XYZ(endEdge[0].X,   endEdge[0].Y,   topZ);

                double fpWidth = bl.DistanceTo(br);
                double fpRun   = new XYZ((bl.X + br.X) / 2, (bl.Y + br.Y) / 2, 0)
                                   .DistanceTo(new XYZ((tl.X + tr.X) / 2, (tl.Y + tr.Y) / 2, 0));
                Diag($"[StructuralTools] Run {run.Id}: footprint panel — " +
                     $"width={fpWidth * 304.8:F0} mm, horiz-run={fpRun * 304.8:F0} mm, " +
                     $"rise={(topZ - baseZ) * 304.8:F0} mm.");

                if (bl.DistanceTo(br) >= EngineConfig.MinEdgeFt &&
                    tl.DistanceTo(tr) >= EngineConfig.MinEdgeFt &&
                    br.DistanceTo(tr) >= EngineConfig.MinEdgeFt)
                {
                    panels.Add(MakePanel(
                        new[] { bl, br, tr, tl },
                        run, PanelRole.Flight, context, "run-fp"));
                    return panels;
                }

                Diag($"[StructuralTools] Run {run.Id}: footprint corners degenerate — falling back.");
            }
        }

        // ---- FALLBACK: path endpoints + StairParameterExtractor width --------
        // Only reached when GetFootprintBoundary() is unavailable.
        // Less accurate (gradient and transition-point errors remain), but
        // still correct width: uses the StairParameterExtractor fallback chain
        // (ActualRunWidth → type parameters) rather than the bounding-box
        // approach, which overestimates width for all rotated stairs.
        Diag($"[StructuralTools] Run {run.Id}: footprint unavailable — path+width fallback.");

        double pathLength = rawDir.GetLength();
        double runWidth   = StairParameterExtractor.GetRunWidth(run);
        if (runWidth < EngineConfig.MinEdgeFt)
        {
            Diag($"[StructuralTools] Run {run.Id}: width not found — using fallback {EngineConfig.FallbackRunWidth} ft.");
            runWidth = EngineConfig.FallbackRunWidth;
        }
        double halfWidth = runWidth / 2.0;
        XYZ    perp      = perpDir * halfWidth;

        var bl2 = new XYZ(pathP0.X - perp.X, pathP0.Y - perp.Y, baseZ);
        var br2 = new XYZ(pathP0.X + perp.X, pathP0.Y + perp.Y, baseZ);
        var tr2 = new XYZ(pathP1.X + perp.X, pathP1.Y + perp.Y, topZ);
        var tl2 = new XYZ(pathP1.X - perp.X, pathP1.Y - perp.Y, topZ);

        double leadEdge  = bl2.DistanceTo(br2);
        double trailEdge = tr2.DistanceTo(tl2);
        double sideEdge  = br2.DistanceTo(tr2);

        if (leadEdge < EngineConfig.MinEdgeFt ||
            trailEdge < EngineConfig.MinEdgeFt ||
            sideEdge  < EngineConfig.MinEdgeFt)
        {
            Diag($"[StructuralTools] Run {run.Id}: edge too short — " +
                 $"lead={leadEdge:F3} ft, trail={trailEdge:F3} ft, side={sideEdge:F3} ft.");
            return panels;
        }

        panels.Add(MakePanel(
            new[] { bl2, br2, tr2, tl2 },
            run, PanelRole.Flight, context, "run-path"));
        return panels;
    }

    // ---------------------------------------------------------------------
    // Riser-curve helpers (sketch-based geometry — primary source)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Get the riser curves from the stair sketch via reflection.
    /// These are the black transverse lines drawn with the riser tool;
    /// each curve spans the full structural width boundary-to-boundary.
    /// Returns null if <c>GetRiserCurves()</c> is unavailable or fails.
    /// </summary>
    private static IList<Curve>? TryGetRiserCurves(StairsRun run)
    {
        try
        {
            var method = typeof(StairsRun).GetMethod("GetRiserCurves");
            return method?.Invoke(run, null) as IList<Curve>;
        }
        catch { return null; }
    }

    /// <summary>
    /// Return the two endpoints of a riser curve ordered by their projection
    /// onto <paramref name="perpDir"/> (left = lower, right = higher),
    /// both lifted to <paramref name="z"/>.
    /// </summary>
    private static (XYZ left, XYZ right) OrderedRiserPtsAtZ(
        Curve riser, XYZ perpDir, double z)
    {
        var a = riser.GetEndPoint(0);
        var b = riser.GetEndPoint(1);
        if (a.DotProduct(perpDir) > b.DotProduct(perpDir)) (a, b) = (b, a);
        return (new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z));
    }

    /// <summary>
    /// Build a single inclined flight panel from the run's riser curves.
    ///
    /// All risers are sorted by midpoint projection along <paramref name="dir"/>.
    /// The first riser becomes the leading edge at <paramref name="baseZ"/>;
    /// the last riser becomes the trailing edge at <paramref name="topZ"/>.
    /// Because each riser spans boundary-to-boundary, width, gradient, and
    /// start/end positions are all structurally exact.
    ///
    /// Returns null if fewer than 2 risers are available.
    /// </summary>
    private static PanelGeometry? TryBuildPanelFromRisers(
        StairsRun run, XYZ dir, XYZ perpDir,
        double baseZ, double topZ,
        StairParameterContext context, string label)
    {
        var risers = TryGetRiserCurves(run);
        if (risers == null || risers.Count < 2) return null;

        var sorted = risers
            .OrderBy(r => (r.GetEndPoint(0).DotProduct(dir) +
                           r.GetEndPoint(1).DotProduct(dir)) / 2.0)
            .ToList();

        var (bl, br) = OrderedRiserPtsAtZ(sorted.First(), perpDir, baseZ);
        var (tl, tr) = OrderedRiserPtsAtZ(sorted.Last(),  perpDir, topZ);

        double width = bl.DistanceTo(br);
        Diag($"[StructuralTools] Run {run.Id}: riser panel — {risers.Count} riser(s), " +
             $"width={width * 304.8:F0} mm, rise={(topZ - baseZ) * 304.8:F0} mm.");

        if (width             < EngineConfig.MinEdgeFt ||
            tl.DistanceTo(tr) < EngineConfig.MinEdgeFt ||
            br.DistanceTo(tr) < EngineConfig.MinEdgeFt)
            return null;

        return new PanelGeometry
        {
            Corners         = new List<XYZ> { bl, br, tr, tl },
            Thickness       = context.ThicknessFt,
            MaterialId      = context.MaterialId,
            Role            = PanelRole.Flight,
            SourceElementId = run.Id,
            Label           = $"Flight {run.Id} ({label})"
        };
    }

    /// <summary>
    /// Build flight and landing panels for a sketched run using its riser
    /// curves as the sole geometric source.
    ///
    /// For each flight group: the group's first riser → leading edge at
    /// <c>flightBaseZ</c>; the group's last riser → trailing edge at
    /// <c>flightTopZ</c>. Rise is distributed proportionally by path length.
    ///
    /// For each landing group: the last riser of the preceding flight and the
    /// first riser of the following flight form the two opposite edges of a
    /// flat quad at the landing elevation. <see cref="OrderCornersCCW"/>
    /// handles all orientations correctly (straight, L-shape, U-shape).
    ///
    /// Returns null when no riser curves are available (not a sketch stair).
    /// </summary>
    private static List<PanelGeometry>? TryBuildSketchedPanelsFromRisers(
        StairsRun run, StairNode node, StairParameterContext context,
        List<(XYZ start, XYZ end, XYZ dir, double len)> segments,
        List<List<int>> groups, bool[] groupIsFlight,
        double[] groupLengths, double totalFlightLength)
    {
        var allRisers = TryGetRiserCurves(run);
        if (allRisers == null || allRisers.Count < 2) return null;

        // Overall sort direction — used when a group direction is degenerate.
        var overallVec = segments[^1].end - segments[0].start;
        XYZ sortDir = overallVec.GetLength() > 0.001
            ? overallVec.Normalize() : segments[0].dir;

        // Pre-sort all risers globally.
        var sortedRisers = allRisers
            .OrderBy(r => (r.GetEndPoint(0).DotProduct(sortDir) +
                           r.GetEndPoint(1).DotProduct(sortDir)) / 2.0)
            .ToList();

        // Assign risers to each flight group by projection range.
        var flightRisers = new Dictionary<int, List<Curve>>();
        for (int g = 0; g < groups.Count; g++)
        {
            if (!groupIsFlight[g]) continue;

            var gStart = segments[groups[g][0]].start;
            var gEnd   = segments[groups[g][^1]].end;
            XYZ gDir   = (gEnd - gStart).GetLength() > 0.001
                ? (gEnd - gStart).Normalize() : sortDir;

            double loProj = Math.Min(gStart.DotProduct(gDir), gEnd.DotProduct(gDir));
            double hiProj = Math.Max(gStart.DotProduct(gDir), gEnd.DotProduct(gDir));
            double tol    = 0.10;   // ~30 mm: absorbs floating-point edge variation

            flightRisers[g] = sortedRisers
                .Where(r =>
                {
                    double mid = (r.GetEndPoint(0).DotProduct(gDir) +
                                  r.GetEndPoint(1).DotProduct(gDir)) / 2.0;
                    return mid >= loProj - tol && mid <= hiProj + tol;
                })
                .OrderBy(r => (r.GetEndPoint(0).DotProduct(gDir) +
                               r.GetEndPoint(1).DotProduct(gDir)) / 2.0)
                .ToList();
        }

        // Bail if no flight group got any risers.
        if (!flightRisers.Any(kv => kv.Value.Count > 0)) return null;

        double baseZ      = node.BaseElevation;
        double topZ       = node.TopElevation;
        double totalRise  = topZ - baseZ;
        double currentZ   = baseZ;
        var    panels     = new List<PanelGeometry>();

        for (int g = 0; g < groups.Count; g++)
        {
            double groupLen = groupLengths[g];
            if (groupLen < EngineConfig.MinEdgeFt) continue;

            var gStart  = segments[groups[g][0]].start;
            var gEnd    = segments[groups[g][^1]].end;
            XYZ gDir    = (gEnd - gStart).GetLength() > 0.001
                ? (gEnd - gStart).Normalize() : sortDir;
            XYZ perpDir = new XYZ(-gDir.Y, gDir.X, 0);

            if (groupIsFlight[g])
            {
                var gr = flightRisers.TryGetValue(g, out var r) ? r : new List<Curve>();
                if (gr.Count < 1) continue;

                double flightRise  = totalRise * (groupLen / totalFlightLength);
                double flightBaseZ = currentZ;
                double flightTopZ  = currentZ + flightRise;

                var (bl, br) = OrderedRiserPtsAtZ(gr.First(), perpDir, flightBaseZ);
                var (tl, tr) = OrderedRiserPtsAtZ(gr.Last(),  perpDir, flightTopZ);

                if (bl.DistanceTo(br) >= EngineConfig.MinEdgeFt &&
                    tl.DistanceTo(tr) >= EngineConfig.MinEdgeFt &&
                    br.DistanceTo(tr) >= EngineConfig.MinEdgeFt)
                {
                    panels.Add(new PanelGeometry
                    {
                        Corners         = new List<XYZ> { bl, br, tr, tl },
                        Thickness       = context.ThicknessFt,
                        MaterialId      = context.MaterialId,
                        Role            = PanelRole.Flight,
                        SourceElementId = run.Id,
                        Label           = $"Flight {run.Id} (sketched flight {g + 1})"
                    });
                    Diag($"[DIAG] Sketched flight {g + 1}: {gr.Count} riser(s), " +
                         $"baseZ={flightBaseZ * 304.8:F0} mm → topZ={flightTopZ * 304.8:F0} mm.");
                }
                currentZ = flightTopZ;
            }
            else // landing
            {
                // Leading edge  = last riser of the previous flight.
                // Trailing edge = first riser of the next flight.
                Curve? prevLast = null;
                for (int pg = g - 1; pg >= 0; pg--)
                    if (groupIsFlight[pg] &&
                        flightRisers.TryGetValue(pg, out var pgr) && pgr.Count > 0)
                    { prevLast = pgr.Last(); break; }

                Curve? nextFirst = null;
                for (int ng = g + 1; ng < groups.Count; ng++)
                    if (groupIsFlight[ng] &&
                        flightRisers.TryGetValue(ng, out var ngr) && ngr.Count > 0)
                    { nextFirst = ngr.First(); break; }

                if (prevLast != null && nextFirst != null)
                {
                    // OrderCornersCCW produces a valid CCW quad for any landing
                    // orientation: straight, L-shaped, or U-shaped (180°).
                    var c4 = new List<XYZ>
                    {
                        new XYZ(prevLast.GetEndPoint(0).X,  prevLast.GetEndPoint(0).Y,  currentZ),
                        new XYZ(prevLast.GetEndPoint(1).X,  prevLast.GetEndPoint(1).Y,  currentZ),
                        new XYZ(nextFirst.GetEndPoint(0).X, nextFirst.GetEndPoint(0).Y, currentZ),
                        new XYZ(nextFirst.GetEndPoint(1).X, nextFirst.GetEndPoint(1).Y, currentZ),
                    };
                    var ordered = OrderCornersCCW(c4);
                    if (ordered.Count >= 3)
                    {
                        panels.Add(new PanelGeometry
                        {
                            Corners         = ordered,
                            Thickness       = context.ThicknessFt,
                            MaterialId      = context.MaterialId,
                            Role            = PanelRole.Landing,
                            SourceElementId = run.Id,
                            Label           = $"Landing {run.Id} (sketched landing {g + 1})"
                        });
                        Diag($"[DIAG] Sketched landing {g + 1}: riser-bounded flat, " +
                             $"z={currentZ * 304.8:F0} mm.");
                    }
                }
            }
        }

        if (panels.Count == 0) return null;
        SnapConsecutivePanelEdges(panels);
        return panels;
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static PanelGeometry MakePanel(
        IList<XYZ> corners, Element source,
        PanelRole role, StairParameterContext context, string suffix)
    {
        return new PanelGeometry
        {
            Corners         = corners.ToList(),
            Thickness       = context.ThicknessFt,
            MaterialId      = context.MaterialId,
            Role            = role,
            SourceElementId = source.Id,
            Label           = $"{role} {source.Id} ({suffix})"
        };
    }

    private static CurveLoop? TryGetFootprintBoundary(Element elem)
    {
        var loops = TryGetFootprintBoundaries(elem);
        return loops != null && loops.Count > 0 ? loops[0] : null;
    }

    /// <summary>
    /// Get ALL footprint boundary loops from <c>GetFootprintBoundary()</c>.
    /// Returns null if the method is unavailable or fails.
    /// </summary>
    private static IList<CurveLoop>? TryGetFootprintBoundaries(Element elem)
    {
        try
        {
            var method = elem.GetType().GetMethod("GetFootprintBoundary");
            if (method?.Invoke(elem, null) is not IList<CurveLoop> loops) return null;
            return loops;
        }
        catch { return null; }
    }

    private static CurveLoop? TryGetBoundingBoxRectangle(Element elem, double z)
    {
        var bb = elem.get_BoundingBox(null);
        if (bb == null) return null;

        var c1 = new XYZ(bb.Min.X, bb.Min.Y, z);
        var c2 = new XYZ(bb.Max.X, bb.Min.Y, z);
        var c3 = new XYZ(bb.Max.X, bb.Max.Y, z);
        var c4 = new XYZ(bb.Min.X, bb.Max.Y, z);

        return CurveLoop.Create(new[]
        {
            Line.CreateBound(c1, c2),
            Line.CreateBound(c2, c3),
            Line.CreateBound(c3, c4),
            Line.CreateBound(c4, c1)
        });
    }
}
