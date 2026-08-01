using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace StructuralTools.SketchEngine;

/// <summary>
/// Extracts boundary and riser curves from a <see cref="StairsRun"/> or
/// <see cref="StairsLanding"/>.
///
/// Boundary source priority for runs:
///   1. <c>run.SketchId</c> → <see cref="Sketch.Profile"/> (direct API, no reflection)
///   2. <c>GetFootprintBoundary()</c> via reflection
///
/// Riser source:
///   <c>GetRiserCurves()</c> via reflection (no public API equivalent exists)
///
/// All methods are null-safe and log failures rather than throwing.
/// </summary>
public static class SketchExtractor
{
    // ------------------------------------------------------------------
    // Riser curves
    // ------------------------------------------------------------------

    /// <summary>
    /// Return all riser curves for the run, sorted by their midpoint
    /// projection along <paramref name="travelDir"/>.
    /// Returns <c>null</c> when <c>GetRiserCurves()</c> is unavailable or
    /// yields no curves.
    /// </summary>
    public static IList<Curve>? GetSortedRisers(
        StairsRun run, XYZ travelDir, List<string> log)
    {
        IList<Curve>? risers = null;
        try
        {
            var m = typeof(StairsRun).GetMethod("GetRiserCurves");
            risers = m?.Invoke(run, null) as IList<Curve>;
        }
        catch (Exception ex)
        {
            log.Add($"[WARN] Run {run.Id}: GetRiserCurves() threw — {ex.Message}");
        }

        if (risers == null || risers.Count == 0)
        {
            log.Add($"[WARN] Run {run.Id}: no riser curves available.");
            return null;
        }

        // Sort by midpoint along travelDir so first = leading edge, last = trailing.
        var sorted = risers
            .OrderBy(r =>
            {
                var mid = (r.GetEndPoint(0) + r.GetEndPoint(1)) / 2.0;
                return mid.DotProduct(travelDir);
            })
            .ToList();

        log.Add($"[DEBUG] Run {run.Id}: {sorted.Count} riser curve(s) sorted along travel.");
        return sorted;
    }

    // ------------------------------------------------------------------
    // Run boundary
    // ------------------------------------------------------------------

    /// <summary>
    /// Return the outer boundary loop for a <see cref="StairsRun"/>.
    ///
    /// Tries (1) <c>run.SketchId → Sketch.Profile</c> (direct, fastest), then
    /// (2) <c>GetFootprintBoundary()</c> reflection.
    /// Returns <c>null</c> when neither source yields a usable loop.
    /// </summary>
    public static CurveLoop? GetRunBoundary(
        StairsRun run, Document doc, List<string> log)
    {
        // ---- Primary: SketchId → Sketch.Profile ----------------------------
        try
        {
            var sketchIdProp = typeof(StairsRun).GetProperty("SketchId");
            if (sketchIdProp?.GetValue(run) is ElementId sketchId &&
                sketchId != ElementId.InvalidElementId)
            {
                if (doc.GetElement(sketchId) is Sketch sketch &&
                    sketch.Profile is { Size: > 0 } profile)
                {
                    // Profile[0] = outer boundary loop.
                    var arr = profile.get_Item(0);
                    if (arr != null && arr.Size > 0)
                    {
                        var loop = new CurveLoop();
                        foreach (Curve c in arr) loop.Append(c);
                        log.Add($"[DEBUG] Run {run.Id}: boundary from Sketch.Profile " +
                                $"({arr.Size} curves).");
                        return loop;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Add($"[DEBUG] Run {run.Id}: SketchId→Profile failed — {ex.Message}");
        }

        // ---- Fallback: GetFootprintBoundary() reflection -------------------
        return TryFootprintBoundary(run, log);
    }

    // ------------------------------------------------------------------
    // Landing boundary
    // ------------------------------------------------------------------

    /// <summary>
    /// Return the outer boundary loop for a <see cref="StairsLanding"/>.
    /// Uses <c>GetFootprintBoundary()</c> reflection.
    /// </summary>
    public static CurveLoop? GetLandingBoundary(
        StairsLanding landing, List<string> log)
        => TryFootprintBoundary(landing, log);

    // ------------------------------------------------------------------
    // Shared: reflection-based GetFootprintBoundary
    // ------------------------------------------------------------------

    private static CurveLoop? TryFootprintBoundary(Element elem, List<string> log)
    {
        foreach (string name in new[] { "GetFootprintBoundary", "GetFootprintBoundaries" })
        {
            try
            {
                var m = elem.GetType().GetMethod(name);
                if (m == null) continue;

                var result = m.Invoke(elem, null);

                // Single CurveLoop return
                if (result is CurveLoop loop)
                {
                    log.Add($"[DEBUG] {elem.GetType().Name} {elem.Id}: " +
                            $"boundary from {name}() (single loop).");
                    return loop;
                }

                // IList<CurveLoop> return — take the outer (first) loop
                if (result is IList<CurveLoop> loops && loops.Count > 0)
                {
                    log.Add($"[DEBUG] {elem.GetType().Name} {elem.Id}: " +
                            $"boundary from {name}() (loop 0 of {loops.Count}).");
                    return loops[0];
                }
            }
            catch (Exception ex)
            {
                log.Add($"[DEBUG] {elem.GetType().Name} {elem.Id}: {name}() — {ex.Message}");
            }
        }

        log.Add($"[WARN] {elem.GetType().Name} {elem.Id}: no boundary source available.");
        return null;
    }
}
