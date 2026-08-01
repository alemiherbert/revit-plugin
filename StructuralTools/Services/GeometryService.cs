using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using StructuralTools.Services;

namespace StructuralTools.Services;

/// <summary>
/// Pure geometry helpers used by the wall-load engine: opening detection, interval
/// merging, sub-curve extraction, and host projection. Stateless — every method
/// takes its inputs explicitly and writes diagnostics into a caller-supplied log.
/// </summary>
public static class GeometryService
{
    // ---------------------------------------------------------------------
    // Tolerance constants — all in Revit internal units (ft) unless noted.
    // ---------------------------------------------------------------------
    public const double MIN_WALL_HEIGHT_FT       = 0.001;   // ~0.3 mm
    public const double MIN_CURVE_LENGTH_FT      = 0.0328;  // ~10 mm
    public const double MIN_LOAD_SEGMENT_LENGTH_FT = 0.0025; // ~0.76 mm
    public const double MIN_OPENING_HEIGHT_FT    = 0.001;   // ~0.3 mm
    public const double MIN_PARAM_RANGE          = 0.001;
    public const double MIN_NORMAL_LENGTH        = 0.001;

    /// <summary>10 mm — minimum sub-segment length, in metres.</summary>
    public const double MIN_SEGMENT_LENGTH_M = 0.010;

    /// <summary>10 mm — minimum net wall height after openings, in metres.</summary>
    public const double MIN_NET_HEIGHT_M = 0.01;

    /// <summary>kN/m — minimum non-trivial load magnitude.</summary>
    public const double MIN_LOAD_VALUE_KN_PER_M = 0.001;

    /// <summary>ft — minimum distance between two distinct XYZ points.</summary>
    public const double MIN_POINT_DIST_FT = 0.005;

    /// <summary>Maximum interval overlap before two openings are considered merged.</summary>
    public const double INTERVAL_MERGE_TOLERANCE = 1e-6;

    /// <summary>
    /// Find the wall's effective height (ft). Tries, in order:
    /// 1. WALL_USER_HEIGHT_PARAM
    /// 2. The largest solid's bounding-box height (geometry)
    /// 3. The element's bounding-box height
    /// 4. Returns 0 and logs a warning.
    /// </summary>
    public static double GetActualWallHeight(Wall wall, string wid, List<string>? log)
    {
        var p = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
        if (p != null && p.HasValue)
        {
            double h = p.AsDouble();
            if (h > MIN_OPENING_HEIGHT_FT)
            {
                log?.Add($"[INFO] Wall {wid}: Used WALL_USER_HEIGHT_PARAM height ({UnitConversionService.InternalLengthToM(h):F3} m).");
                return h;
            }
        }

        var opts = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine
        };

        GeometryElement? geom = null;
        try { geom = wall.get_Geometry(opts); }
        catch (Exception ex) { log?.Add($"[DEBUG] Wall {wid}: get_Geometry threw {ex.GetType().Name}: {ex.Message}"); }

        if (geom != null)
        {
            Solid? largest = null;
            double maxVol = 0.0;
            foreach (GeometryObject obj in geom)
            {
                if (obj == null) continue;
                ExtractLargestSolid(obj, ref largest, ref maxVol);
            }

            if (largest != null)
            {
                var sbb = largest.GetBoundingBox();
                if (sbb != null)
                {
                    double h = sbb.Max.Z - sbb.Min.Z;
                    if (h > MIN_OPENING_HEIGHT_FT)
                    {
                        log?.Add($"[INFO] Wall {wid}: Used geometry solid bounding box height ({UnitConversionService.InternalLengthToM(h):F3} m).");
                        return h;
                    }
                }
            }
        }

        var bb = wall.get_BoundingBox(null);
        if (bb != null)
        {
            double h = bb.Max.Z - bb.Min.Z;
            log?.Add($"[INFO] Wall {wid}: Geometry extraction failed. Using element bounding box height ({UnitConversionService.InternalLengthToM(h):F3} m).");
            return h;
        }

        log?.Add($"[WARNING] Wall {wid}: Could not determine wall height — returning 0.");
        return 0.0;
    }

    /// <summary>
    /// Walk a geometry element recursively and find the solid with the largest volume.
    /// </summary>
    public static void ExtractLargestSolid(GeometryObject obj, ref Solid? largest, ref double maxVol)
    {
        if (obj == null) return;

        if (obj is Solid solid)
        {
            if (solid.Volume > 0 && solid.Volume > maxVol)
            {
                maxVol = solid.Volume;
                largest = solid;
            }
            return;
        }

        if (obj is GeometryInstance gi)
        {
            GeometryElement instGeom = gi.GetInstanceGeometry();
            if (instGeom == null) return;
            foreach (GeometryObject child in instGeom)
                ExtractLargestSolid(child, ref largest, ref maxVol);
        }
    }

    /// <summary>
    /// Project an opening (door/window/family instance) onto a wall's location curve
    /// and return its normalised parameter range [0..1] and clipped height in ft.
    /// Returns null if the opening cannot be characterised.
    /// </summary>
    public static (double tMin, double tMax, double h)? GetOpeningInfo(
        Element insert,
        BoundingBoxXYZ wallBB,
        Curve lc,
        double ps,
        double pe,
        double paramRange,
        string wid,
        List<string>? log)
    {
        double wallZMin = wallBB.Min.Z;
        double wallZMax = wallBB.Max.Z;

        var opts = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine
        };

        GeometryElement? geom = null;
        try { geom = insert.get_Geometry(opts); }
        catch (Exception ex) { log?.Add($"[DEBUG] Wall {wid}: opening {insert.Id} get_Geometry threw {ex.GetType().Name}"); }

        if (geom != null)
        {
            Solid? largest = null;
            double maxVol = 0.0;
            foreach (GeometryObject obj in geom)
            {
                if (obj == null) continue;
                ExtractLargestSolid(obj, ref largest, ref maxVol);
            }

            if (largest != null)
            {
                var sbb = largest.GetBoundingBox();
                if (sbb != null)
                {
                    double oh = Math.Max(0.0,
                        Math.Min(sbb.Max.Z, wallZMax) - Math.Max(sbb.Min.Z, wallZMin));

                    var corners = new[]
                    {
                        sbb.Min,
                        sbb.Max,
                        new XYZ(sbb.Min.X, sbb.Max.Y, sbb.Min.Z),
                        new XYZ(sbb.Max.X, sbb.Min.Y, sbb.Min.Z),
                        new XYZ(sbb.Min.X, sbb.Min.Y, sbb.Max.Z),
                        new XYZ(sbb.Max.X, sbb.Max.Y, sbb.Max.Z),
                        new XYZ(sbb.Min.X, sbb.Max.Y, sbb.Max.Z),
                        new XYZ(sbb.Max.X, sbb.Min.Y, sbb.Max.Z),
                    };

                    var (tMin, tMax, any) = ProjectCorners(lc, corners, ps, pe, paramRange);
                    if (any && tMax - tMin > MIN_PARAM_RANGE && oh > 0.0)
                        return (tMin, tMax, oh);
                }
            }
        }

        BoundingBoxXYZ? ib = null;
        try { ib = insert.get_BoundingBox(null); }
        catch (Exception ex) { log?.Add($"[DEBUG] Wall {wid}: opening {insert.Id} get_BoundingBox threw {ex.GetType().Name}"); }

        if (ib == null)
        {
            log?.Add($"[INFO] Wall {wid}: Opening {insert.Id} has no geometry or bounding box — skipped.");
            return null;
        }

        double oh2 = Math.Max(0.0,
            Math.Min(ib.Max.Z, wallZMax) - Math.Max(ib.Min.Z, wallZMin));

        var pts2 = new[]
        {
            ib.Min,
            ib.Max,
            new XYZ(ib.Min.X, ib.Max.Y, ib.Min.Z),
            new XYZ(ib.Max.X, ib.Min.Y, ib.Min.Z)
        };

        var (tMin2, tMax2, any2) = ProjectCorners(lc, pts2, ps, pe, paramRange);
        if (any2 && tMax2 - tMin2 > MIN_PARAM_RANGE)
            return (tMin2, tMax2, oh2);

        return null;
    }

    /// <summary>
    /// Merge overlapping (tMin, tMax, h) intervals. Overlapping intervals take the
    /// maximum height of either interval (conservative).
    /// </summary>
    public static List<(double tMin, double tMax, double h)> MergeIntervals(
        List<(double tMin, double tMax, double h)> intervals)
    {
        var result = new List<(double tMin, double tMax, double h)>();
        if (intervals == null || intervals.Count == 0) return result;

        var sorted = intervals.OrderBy(iv => iv.tMin).ToList();
        var current = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];
            if (next.tMin <= current.tMax + INTERVAL_MERGE_TOLERANCE)
            {
                current = (
                    current.tMin,
                    Math.Max(current.tMax, next.tMax),
                    Math.Max(current.h, next.h));
            }
            else
            {
                result.Add(current);
                current = next;
            }
        }
        result.Add(current);
        return result;
    }

    /// <summary>
    /// Yield one or more sub-curves of <paramref name="curve"/> covering the
    /// normalised parameter range [t0, t1]. Lines and arcs yield a single sub-curve;
    /// other curve types are tessellated.
    /// </summary>
    public static IEnumerable<Curve> GetSubCurve(Curve curve, double t0, double t1)
    {
        double ps = curve.GetEndParameter(0);
        double pe = curve.GetEndParameter(1);
        double r0 = ps + t0 * (pe - ps);
        double r1 = ps + t1 * (pe - ps);

        if (curve is Line)
        {
            yield return Line.CreateBound(
                curve.Evaluate(r0, false),
                curve.Evaluate(r1, false));
        }
        else if (curve is Arc)
        {
            var p0 = curve.Evaluate(r0, false);
            var p1 = curve.Evaluate(r1, false);
            var pm = curve.Evaluate((r0 + r1) / 2.0, false);
            yield return Arc.Create(p0, p1, pm);
        }
        else
        {
            var c = curve.Clone();
            c.MakeBound(r0, r1);
            var pts = c.Tessellate();
            for (int i = 0; i < pts.Count - 1; i++)
            {
                if (pts[i].DistanceTo(pts[i + 1]) > MIN_POINT_DIST_FT)
                    yield return Line.CreateBound(pts[i], pts[i + 1]);
            }
        }
    }

    /// <summary>
    /// Project a 3D point onto a curve, returning the closest point on the curve.
    /// Falls back to manual line-projection if <see cref="Curve.Project"/> throws.
    /// </summary>
    public static XYZ ProjectOntoCurve(XYZ pt, Curve curve)
    {
        try
        {
            var r = curve.Project(pt);
            if (r != null) return r.XYZPoint;
        }
        catch { /* fall through to manual projection */ }

        if (curve is Line ln)
        {
            var o = ln.GetEndPoint(0);
            var d = (ln.GetEndPoint(1) - o).Normalize();
            double dist = (pt - o).DotProduct(d);
            return o + d * Math.Max(0.0, Math.Min(ln.Length, dist));
        }
        return pt;
    }

    /// <summary>
    /// Project a point onto a plane defined by normal + origin.
    /// </summary>
    public static XYZ? ProjectPointOntoPlane(XYZ pt, Plane plane)
    {
        if (pt == null || plane == null) return null;
        double dist = plane.Normal.DotProduct(pt - plane.Origin);
        return pt - plane.Normal.Multiply(dist);
    }

    // ---------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------

    private static (double tMin, double tMax, bool any) ProjectCorners(
        Curve lc, XYZ[] points, double ps, double pe, double paramRange)
    {
        double tMin = double.MaxValue;
        double tMax = double.MinValue;
        bool any = false;

        foreach (var pt in points)
        {
            IntersectionResult? pr = null;
            try { pr = lc.Project(pt); }
            catch { continue; }
            if (pr == null) continue;

            double t = (pr.Parameter - ps) / paramRange;
            t = Math.Max(0.0, Math.Min(1.0, t));
            if (t < tMin) { tMin = t; any = true; }
            if (t > tMax) { tMax = t; any = true; }
        }

        return (tMin, tMax, any);
    }
}
