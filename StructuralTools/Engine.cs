using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitOperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;
using RevitInvalidOperationException = Autodesk.Revit.Exceptions.InvalidOperationException;

namespace StructuralTools;

// =====================================================================
// MODELS
// =====================================================================

/// <summary>
/// Represents a wall element together with its placement transform (for linked models)
/// and the source document title. Implemented as a readonly struct because instances
/// are passed by value throughout the engine and never mutated.
/// </summary>
public readonly struct WallEntry
{
    public Wall Wall { get; }
    public Transform Transform { get; }
    public string? Source { get; }

    public WallEntry(Wall wall, Transform transform, string? source)
    {
        Wall      = wall      ?? throw new ArgumentNullException(nameof(wall));
        Transform = transform ?? Transform.Identity;
        Source    = source;
    }

    public bool IsLinked => Source != null;
}

/// <summary>
/// Result bag returned by load creation operations.
/// </summary>
public class LoadResult
{
    public List<LineLoad> Created { get; } = new();
    public List<string> Log { get; } = new();
    public int Errors { get; set; }
    public int LcFails { get; set; }

    public void LogInfo(string msg, string cat = "INFO")
        => Log.Add($"[{cat}] {msg}");
}

// =====================================================================
// SERVICES
// =====================================================================

/// <summary>
/// Pure unit-conversion helpers between Revit internal units and engineering units.
/// All methods fall back to a hard-coded constant if the Revit unit API throws,
/// so the engine keeps running even on unusual document unit configurations.
/// </summary>
public static class UnitConversionService
{
    // Hard-coded fallback constants. These are the conversions Revit itself
    // uses internally, so they only diverge from UnitUtils in pathological cases.

    /// <summary>1 ft = 0.3048 m</summary>
    private const double M_PER_FT = 0.3048;

    /// <summary>
    /// 1 kN/m³ = 0.101971621 kip/ft³ (Revit internal unit for unit weight is kip/ft³).
    /// </summary>
    private const double KIPFT3_PER_KNM3 = 0.101971621;

    /// <summary>
    /// 1 kg/m³ = 0.0624279606 lb/ft³ (Revit internal density unit is lb/ft³).
    /// </summary>
    private const double LBFT3_PER_KGM3 = 0.0624279606;

    /// <summary>
    /// 1 kN/m = 0.0685218 kip/ft (Revit internal force-per-length unit is kip/ft).
    /// </summary>
    private const double KIPFT_PER_KNM = 0.0685218;

    /// <summary>m/s² — used to convert density (kg/m³) to unit weight (kN/m³).</summary>
    private const double GRAVITY_M_S2 = 9.80665;

    /// <summary>Convert a length from Revit internal units (ft) to metres.</summary>
    public static double InternalLengthToM(double ft)
    {
        try { return UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters); }
        catch { return ft * M_PER_FT; }
    }

    /// <summary>Convert a unit weight from Revit internal units (kip/ft³) to kN/m³.</summary>
    public static double InternalUnitWeightToKnM3(double v)
    {
        try { return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.KilonewtonsPerCubicMeter); }
        catch { return v / KIPFT3_PER_KNM3; }
    }

    /// <summary>Convert a density from Revit internal units (lb/ft³) to kg/m³.</summary>
    public static double InternalDensityToKgM3(double v)
    {
        try { return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.KilogramsPerCubicMeter); }
        catch { return v / LBFT3_PER_KGM3; }
    }

    /// <summary>Convert a force-per-length from kN/m to Revit internal units (kip/ft).</summary>
    public static double KnPerMToInternal(double v)
    {
        try { return UnitUtils.ConvertToInternalUnits(v, UnitTypeId.KilonewtonsPerMeter); }
        catch { return v * KIPFT_PER_KNM; }
    }

    /// <summary>Convert a density in kg/m³ to a unit weight in kN/m³ (kg × g / 1000).</summary>
    public static double KgM3ToKnM3(double kgM3) => kgM3 * GRAVITY_M_S2 / 1000.0;

    /// <summary>Parse a culture-invariant numeric string (e.g. "10.5") into a double.</summary>
    public static bool TryParseInvariant(string? text, out double value)
        => double.TryParse((text ?? "").Trim(),
               NumberStyles.Any,
               CultureInfo.InvariantCulture,
               out value);
}

/// <summary>
/// Resolves a wall's self-weight per unit area (kN/m²) from its compound structure layers,
/// with per-material caching to avoid recomputing densities for shared wall types.
/// </summary>
public sealed class MaterialService
{
    private readonly Dictionary<ElementId, double> _weightCache = new();

    /// <summary>
    /// Compute the wall's area-weight (kN/m²) — i.e. the line load per metre of wall
    /// per metre of height. Walks each compound-structure layer; falls back to a
    /// single-bulk density if the wall has no compound structure.
    /// </summary>
    public double CalcWallAreaWeight(Wall wall, double fallbackGammaKnM3, List<string>? log = null)
    {
        if (wall == null) return 0.0;

        var wt = wall.WallType;
        CompoundStructure? cs = null;
        try { cs = wt.GetCompoundStructure(); }
        catch (Exception ex) { log?.Add($"[DEBUG] WallType {wt.Name}: GetCompoundStructure threw {ex.GetType().Name}"); }

        if (cs != null)
        {
            double total = 0.0;
            foreach (var layer in cs.GetLayers())
            {
                double tM = UnitConversionService.InternalLengthToM(layer.Width);
                Material? mat = (layer.MaterialId != ElementId.InvalidElementId)
                    ? wall.Document.GetElement(layer.MaterialId) as Material
                    : null;
                total += tM * GetMaterialUnitWeightCached(mat, fallbackGammaKnM3);
            }
            if (total > 0) return total;
        }

        return UnitConversionService.InternalLengthToM(wt.Width) * fallbackGammaKnM3;
    }

    /// <summary>
    /// Look up a material's unit weight (kN/m³), preferring the structural asset's
    /// density, then the PHY_MATERIAL_PARAM_UNIT_WEIGHT parameter, then the fallback.
    /// Cached per <see cref="Material.Id"/>.
    /// </summary>
    public double GetMaterialUnitWeightCached(Material? mat, double fallbackGammaKnM3)
    {
        if (mat == null) return fallbackGammaKnM3;

        if (_weightCache.TryGetValue(mat.Id, out double cached))
            return cached;

        double weight = ComputeMaterialUnitWeight(mat, fallbackGammaKnM3);
        _weightCache[mat.Id] = weight;
        return weight;
    }

    private static double ComputeMaterialUnitWeight(Material mat, double fallbackGammaKnM3)
    {
        // 1. Structural asset density (preferred).
        if (mat.StructuralAssetId != ElementId.InvalidElementId)
        {
            var pse = mat.Document.GetElement(mat.StructuralAssetId) as PropertySetElement;
            if (pse != null)
            {
                var sa = pse.GetStructuralAsset();
                if (sa != null && sa.Density > 0)
                {
                    double kgM3 = UnitConversionService.InternalDensityToKgM3(sa.Density);
                    return UnitConversionService.KgM3ToKnM3(kgM3);
                }
            }
        }

        // 2. Legacy PHY_MATERIAL_PARAM_UNIT_WEIGHT parameter.
        var p = mat.get_Parameter(BuiltInParameter.PHY_MATERIAL_PARAM_UNIT_WEIGHT);
        if (p != null && p.HasValue)
        {
            double uw = p.AsDouble();
            if (uw > 0) return UnitConversionService.InternalUnitWeightToKnM3(uw);
        }

        // 3. Caller-supplied fallback.
        return fallbackGammaKnM3;
    }
}

/// <summary>
/// Pure geometry helpers used by the wall-load engine: opening detection, interval
/// merging, sub-curve extraction, and host projection. Stateless — every method
/// takes its inputs explicitly and writes diagnostics into a caller-supplied log.
/// </summary>
public static class GeometryService
{
    // Tolerance constants — all in Revit internal units (ft) unless noted.
    public const double MIN_WALL_HEIGHT_FT       = 0.001;   // ~0.3 mm
    public const double MIN_CURVE_LENGTH_FT      = 0.0328;  // ~10 mm
    public const double MIN_LOAD_SEGMENT_LENGTH_FT = 0.0025; // ~0.76 mm
    public const double MIN_OPENING_HEIGHT_FT    = 0.001;   // ~0.3 mm
    public const double MIN_PARAM_RANGE          = 0.001;
    public const double MIN_NORMAL_LENGTH        = 0.001;
    public const double MIN_SEGMENT_LENGTH_M = 0.010;        // 10 mm
    public const double MIN_NET_HEIGHT_M = 0.01;             // 10 mm
    public const double MIN_LOAD_VALUE_KN_PER_M = 0.001;    // kN/m
    public const double MIN_POINT_DIST_FT = 0.005;          // ft
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

// =====================================================================
// COMMANDS
// =====================================================================

/// <summary>
/// Main command that generates wall loads. Bound to the "Generate Wall Loads" ribbon button.
/// Enters Revit's native wall-picking mode (green Modify contextual tab with Finish/Cancel),
/// then host-picking mode, then creates the line loads.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class GenerateWallLoadsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication uiApp = commandData.Application;

        if (uiApp.ActiveUIDocument == null)
        {
            TaskDialog.Show("Structural Tools",
                "Open a Revit document before running this command.");
            return Result.Cancelled;
        }

        try
        {
            var engine = new WallLoadEngine(uiApp);
            return engine.Run();
        }
        catch (RevitOperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = $"Error generating wall loads: {ex.Message}\n\n{ex.StackTrace}";
            TaskDialog.Show("Structural Tools - Error", message);
            return Result.Failed;
        }
    }
}

/// <summary>
/// Stub command for staircase to analytical conversion (placeholder).
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class StaircaseToAnalyticalCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        TaskDialog.Show("Structural Tools",
            "Staircase to Analytical conversion is not yet implemented.\n\n" +
            "This command will convert staircases (any type) to analytical panels:\n" +
            "- Each run becomes one slanted analytical panel\n" +
            "- Each landing becomes one flat analytical panel");
        return Result.Cancelled;
    }
}

// =====================================================================
// ENGINE
// =====================================================================

/// <summary>
/// Orchestrates the Wall → Line Load generator using native Revit selection.
/// When <see cref="Run"/> is called it:
///   1. Calls <see cref="UIDocument.Selection.PickObjects"/>, which makes Revit
///      enter its native selection mode — a green "Modify | Pick Walls" contextual
///      tab appears with Finish (✓) and Cancel (✗) buttons. Only Wall elements
///      (host or linked) are clickable.
///   2. Calls <see cref="UIDocument.Selection.PickObject"/> for the host beam/floor.
///   3. Creates the line loads in a single transaction.
///   4. Shows a TaskDialog summary.
/// No WPF dialogs are used.
/// </summary>
public class WallLoadEngine
{
    /// <summary>Default concrete density (kN/m³) used when no material density is found.</summary>
    private const double DEFAULT_CONCRETE_DENSITY_KN_M3 = 24.0;

    /// <summary>If errors exceed this fraction of total walls, the whole transaction rolls back.</summary>
    private const double ERROR_ROLLBACK_THRESHOLD = 0.5;

    private readonly UIApplication _uiApp;
    private readonly UIDocument _uiDoc;
    private readonly Document _doc;
    private readonly MaterialService _materialService;

    public WallLoadEngine(UIApplication uiApp)
    {
        UIApplication app = uiApp ?? throw new ArgumentNullException(nameof(uiApp));
        UIDocument uiDoc = app.ActiveUIDocument ?? throw new InvalidOperationException(
            "No active Revit document. Open a document before constructing WallLoadEngine.");

        _uiApp  = app;
        _uiDoc  = uiDoc;
        _doc    = uiDoc.Document;
        _materialService = new MaterialService();
    }

    /// <summary>
    /// Run the full pick-walls → pick-host → generate-loads flow.
    /// </summary>
    public Result Run()
    {
        // ---- STEP 1: Pick walls ------------------------------------------------
        List<WallEntry> walls = PickWalls();
        if (walls.Count == 0)
            return Result.Cancelled;

        // ---- STEP 2: Pick host -------------------------------------------------
        Element? host = PickHost();
        if (host == null)
            return Result.Cancelled;

        // ---- STEP 3: Resolve load case + load type -----------------------------
        var (loadCase, lcMatched) = DetectDeadLoadCase();
        LineLoadType? defaultLoadType = GetDefaultLoadType();

        if (defaultLoadType == null)
        {
            TaskDialog.Show("Structural Tools - Error",
                "No LineLoadType found in the model.\nLoad a structural line-load family first.");
            return Result.Failed;
        }
        if (loadCase == null)
        {
            TaskDialog.Show("Structural Tools - Error",
                "No LoadCase found in the model.\nCreate structural load cases first.");
            return Result.Failed;
        }

        // ---- STEP 4: Create loads ----------------------------------------------
        LoadResult result = CreateLoads(
            walls, host,
            loadCase, DEFAULT_CONCRETE_DENSITY_KN_M3, defaultLoadType);

        // ---- STEP 5: Show summary ----------------------------------------------
        int linkedCount = walls.Count(w => w.IsLinked);
        ShowSummary(result, walls, host, loadCase, lcMatched, linkedCount);

        return result.Errors > 0 ? Result.Failed : Result.Succeeded;
    }

    private List<WallEntry> PickWalls()
    {
        IList<Reference> refs;
        try
        {
            refs = _uiDoc.Selection.PickObjects(
                ObjectType.LinkedElement,
                new WallOrLinkFilter(),
                "Select walls — click or box-select (host or linked). Press Finish (✓) when done.");
        }
        catch (RevitOperationCanceledException)
        {
            return new List<WallEntry>();
        }

        var newWalls = new List<WallEntry>();
        var seenKeys = new HashSet<string>();
        int skipped = 0, duplicates = 0;

        foreach (var r in refs)
        {
            bool isLinked = r.LinkedElementId != ElementId.InvalidElementId;
            string dedupeKey = isLinked
                ? $"link:{r.ElementId.Value}:{r.LinkedElementId.Value}"
                : $"host:{r.ElementId.Value}";

            if (seenKeys.Contains(dedupeKey)) { duplicates++; continue; }

            WallEntry entry;
            if (isLinked)
            {
                if (_doc.GetElement(r.ElementId) is not RevitLinkInstance linkInst) { skipped++; continue; }
                var linkDoc = linkInst.GetLinkDocument();
                if (linkDoc == null) { skipped++; continue; }
                if (linkDoc.GetElement(r.LinkedElementId) is not Wall w) { skipped++; continue; }
                entry = new WallEntry(w, linkInst.GetTotalTransform(), linkDoc.Title);
            }
            else
            {
                if (_doc.GetElement(r.ElementId) is not Wall w) { skipped++; continue; }
                entry = new WallEntry(w, Transform.Identity, null);
            }

            seenKeys.Add(dedupeKey);
            newWalls.Add(entry);
        }

        if (newWalls.Count == 0)
        {
            TaskDialog.Show("Wall Load Generator",
                $"No Wall elements were picked.\n" +
                (skipped    > 0 ? $"({skipped} non-wall pick(s) ignored)\n" : "") +
                (duplicates > 0 ? $"({duplicates} duplicate pick(s) ignored)" : ""));
        }

        return newWalls;
    }

    private Element? PickHost()
    {
        Reference r;
        try
        {
            r = _uiDoc.Selection.PickObject(
                ObjectType.Element,
                new HostElementFilter(),
                "Select the host beam or floor (current model only).");
        }
        catch (RevitOperationCanceledException)
        {
            return null;
        }

        return _doc.GetElement(r.ElementId);
    }

    private static void ShowSummary(
        LoadResult result,
        List<WallEntry> walls,
        Element host,
        LoadCase loadCase,
        bool lcMatched,
        int linkedCount)
    {
        string summary =
            $"{(result.Errors == 0 ? "✅" : "⚠")}  {result.Created.Count} load segment(s) created\n" +
            $"⚠  {result.Errors} error(s)\n" +
            $"Walls processed: {walls.Count}" +
                (linkedCount > 0 ? $" ({linkedCount} linked)" : "") + "\n" +
            $"Host: {GetHostLabel(host)}\n" +
            $"Load Case: {loadCase.Name}" +
                (lcMatched ? "" : "  (⚠ no case named 'Dead'/'DL' — used first case, verify)") + "\n\n" +
            "--- Log ---\n" +
            string.Join("\n", result.Log.Take(20)) +
            (result.Log.Count > 20 ? $"\n… and {result.Log.Count - 20} more entries." : "");

        TaskDialog.Show("Wall Load Generation Complete", summary);
    }

    private LoadResult CreateLoads(
        List<WallEntry> wallItems,
        Element physicalHost,
        LoadCase loadCase,
        double fallbackGamma,
        LineLoadType defaultLoadType)
    {
        var res = new LoadResult();

        if (wallItems == null || wallItems.Count == 0)
        {
            res.LogInfo("No walls provided.", "ERROR"); res.Errors++; return res;
        }
        if (physicalHost == null)
        {
            res.LogInfo("No host element provided.", "ERROR"); res.Errors++; return res;
        }

        string? hostType = ClassifyHost(physicalHost);
        ElementId analId = GetAnalyticalId(physicalHost);

        if (analId == ElementId.InvalidElementId)
        {
            res.LogInfo($"Analytical model not found for host ID {physicalHost.Id}. " +
                        "Enable Analytical Model for this element.", "ERROR");
            res.Errors++;
            return res;
        }

        // TransactionGroup gives us atomic rollback if too many loads fail.
        using var tg = new TransactionGroup(_doc, "Generate Wall Line Loads");
        tg.Start();

        using (var tx = new Transaction(_doc, "Create Line Loads"))
        {
            tx.Start();

            foreach (var entry in wallItems)
            {
                ProcessWall(entry, physicalHost, hostType, analId,
                            loadCase, fallbackGamma, defaultLoadType, res);
            }

            // Roll back the entire group if too many walls errored.
            if (res.Errors > Math.Ceiling(wallItems.Count * ERROR_ROLLBACK_THRESHOLD))
            {
                tx.RollBack();
                tg.RollBack();
                res.LogInfo(
                    $"Aborted: {res.Errors} of {wallItems.Count} walls errored " +
                    $"(threshold {ERROR_ROLLBACK_THRESHOLD:P0}). No loads were committed.", "ERROR");
                return res;
            }

            tx.Commit();
        }

        tg.Assimilate();
        return res;
    }

    private void ProcessWall(
        WallEntry entry,
        Element physicalHost,
        string? hostType,
        ElementId analId,
        LoadCase loadCase,
        double fallbackGamma,
        LineLoadType defaultLoadType,
        LoadResult res)
    {
        Wall wall = entry.Wall;
        Transform transform = entry.Transform;
        string wid = entry.IsLinked
            ? $"{wall.Id} (linked: {entry.Source ?? "?"})"
            : wall.Id.ToString();

        var lc = (wall.Location as LocationCurve)?.Curve;
        if (lc == null)
        {
            res.LogInfo($"Wall ID {wid}: No location curve.", "WARNING"); res.Errors++; return;
        }

        double wallHeightFt = GeometryService.GetActualWallHeight(wall, wid, res.Log);
        if (wallHeightFt < GeometryService.MIN_WALL_HEIGHT_FT)
        {
            res.LogInfo($"Wall ID {wid}: Effective height is zero — skipping.", "WARNING");
            return;
        }

        var bb = wall.get_BoundingBox(null);
        if (bb == null)
        {
            res.LogInfo($"Wall ID {wid}: No bounding box — cannot clip opening heights.", "WARNING");
            res.Errors++; return;
        }

        double clLen = lc.Length;
        if (clLen < GeometryService.MIN_CURVE_LENGTH_FT) return;

        double areaW = _materialService.CalcWallAreaWeight(wall, fallbackGamma, res.Log);

        IList<ElementId> insertIds;
        if (!TryFindInserts(wall, out insertIds))
            insertIds = new List<ElementId>();

        double ps = lc.GetEndParameter(0);
        double pe = lc.GetEndParameter(1);
        double paramRange = pe - ps;

        var rawOpenings = new List<(double tMin, double tMax, double h)>();
        if (Math.Abs(paramRange) > 1e-9)
        {
            foreach (var insId in insertIds)
            {
                var ie = wall.Document.GetElement(insId);
                if (ie == null) continue;

                var info = GeometryService.GetOpeningInfo(ie, bb, lc, ps, pe, paramRange, wid, res.Log);
                if (info.HasValue)
                    rawOpenings.Add(info.Value);
            }
        }

        var openingData = GeometryService.MergeIntervals(rawOpenings);

        var tKnots = new List<double> { 0.0, 1.0 };
        foreach (var op in openingData)
        {
            tKnots.Add(op.tMin);
            tKnots.Add(op.tMax);
        }
        tKnots = tKnots
            .Select(t => Math.Round(t, 5))
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        int segmentsForThisWall = 0;

        for (int i = 0; i < tKnots.Count - 1; i++)
        {
            double t0 = tKnots[i], t1 = tKnots[i + 1];
            if (UnitConversionService.InternalLengthToM((t1 - t0) * clLen) < GeometryService.MIN_SEGMENT_LENGTH_M)
                continue;

            double tMid = (t0 + t1) / 2.0;
            double dh = openingData
                .Where(op => op.tMin <= tMid && tMid <= op.tMax)
                .Sum(op => op.h);

            double netHm = UnitConversionService.InternalLengthToM(Math.Max(0.0, wallHeightFt - dh));
            if (netHm < GeometryService.MIN_NET_HEIGHT_M) continue;

            double loadVal = areaW * netHm;
            if (loadVal <= GeometryService.MIN_LOAD_VALUE_KN_PER_M) continue;

            foreach (var sc in GeometryService.GetSubCurve(lc, t0, t1))
            {
                if (sc.Length < GeometryService.MIN_LOAD_SEGMENT_LENGTH_FT) continue;

                Curve raw = !transform.IsIdentity
                    ? sc.CreateTransformed(transform)
                    : sc;

                Curve? lcCurve = BuildHostedCurve(raw, physicalHost, hostType, analId, res.Log, wid);
                if (lcCurve == null) { res.Errors++; continue; }

                var fv = new XYZ(0, 0, -UnitConversionService.KnPerMToInternal(loadVal));
                try
                {
                    var ll = LineLoad.Create(
                        _doc, analId, lcCurve,
                        fv, XYZ.Zero, defaultLoadType);

                    if (!AssignLoadCase(ll, loadCase))
                    {
                        res.LcFails++;
                        res.LogInfo($"Wall ID {wid} [{t0:F3}–{t1:F3}]: load-case param not set.", "WARNING");
                    }
                    res.Created.Add(ll);
                    segmentsForThisWall++;
                }
                catch (Exception ex)
                {
                    res.LogInfo($"Wall ID {wid} [{t0:F3}–{t1:F3}]: {ex.Message}", "ERROR");
                    res.Errors++;
                }
            }
        }

        res.LogInfo($"Wall ID {wid}: {segmentsForThisWall} segment(s) created  [{areaW:F2} kN/m²]",
                    "SUCCESS");
    }

    private Curve? BuildHostedCurve(
        Curve sc,
        Element physHost,
        string? hostType,
        ElementId analId,
        List<string> log,
        string wid)
    {
        var p0 = sc.GetEndPoint(0);
        var p1 = sc.GetEndPoint(1);

        if (hostType == "beam")
        {
            var bc = GetBeamCurve(analId);
            if (bc == null) return sc;
            var np0 = GeometryService.ProjectOntoCurve(p0, bc);
            var np1 = GeometryService.ProjectOntoCurve(p1, bc);
            if (np0.DistanceTo(np1) < GeometryService.MIN_POINT_DIST_FT) return null;
            if (!np0.IsAlmostEqualTo(np1))
                return Line.CreateBound(np0, np1);
            return null;
        }

        if (hostType == "floor")
        {
            Curve? analResult = TryProjectOntoAnalyticalFloor(analId, p0, p1, log, wid);
            if (analResult != null) return analResult;

            log.Add($"[INFO] Wall {wid}: Analytical floor projection unavailable — using level elevation.");
            double fz = GetFloorZ((Floor)physHost);
            if (double.IsNaN(fz)) return sc;
            var np0 = new XYZ(p0.X, p0.Y, fz);
            var np1 = new XYZ(p1.X, p1.Y, fz);
            if (np0.DistanceTo(np1) < GeometryService.MIN_POINT_DIST_FT) return null;
            return Line.CreateBound(np0, np1);
        }

        return sc;
    }

    private Curve? TryProjectOntoAnalyticalFloor(
        ElementId analId, XYZ p0, XYZ p1, List<string> log, string wid)
    {
        var ae = _doc.GetElement(analId);
        if (ae == null) return null;

        var opts = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine
        };

        GeometryElement? geom = null;
        try { geom = ae.get_Geometry(opts); }
        catch (Exception ex) { log.Add($"[DEBUG] Wall {wid}: analytical floor get_Geometry threw {ex.GetType().Name}"); }

        if (geom != null)
        {
            Face? bestFace = null;
            double bestArea = 0.0;

            foreach (GeometryObject obj in geom)
            {
                if (obj is not Solid solid || solid.Volume <= 0) continue;
                foreach (Face face in solid.Faces)
                {
                    if (face != null && face.Area > bestArea)
                    {
                        bestArea = face.Area;
                        bestFace = face;
                    }
                }
            }

            if (bestFace != null)
            {
                var uvBB = bestFace.GetBoundingBox();
                var uvCtr = new UV(
                    (uvBB.Min.U + uvBB.Max.U) / 2.0,
                    (uvBB.Min.V + uvBB.Max.V) / 2.0);

                XYZ normal = bestFace.ComputeNormal(uvCtr);
                XYZ origin = bestFace.Evaluate(uvCtr);

                if (normal != null && normal.GetLength() > GeometryService.MIN_NORMAL_LENGTH)
                {
                    Plane plane = Plane.CreateByNormalAndOrigin(normal.Normalize(), origin);
                    XYZ? np0 = GeometryService.ProjectPointOntoPlane(p0, plane);
                    XYZ? np1 = GeometryService.ProjectPointOntoPlane(p1, plane);
                    if (np0 != null && np1 != null && np0.DistanceTo(np1) >= GeometryService.MIN_POINT_DIST_FT)
                    {
                        log.Add($"[INFO] Wall {wid}: Floor load projected onto analytical surface plane.");
                        return Line.CreateBound(np0, np1);
                    }
                }
            }
        }

        BoundingBoxXYZ? aebb = null;
        try { aebb = ae.get_BoundingBox(null); }
        catch (Exception ex) { log.Add($"[DEBUG] Wall {wid}: analytical element get_BoundingBox threw {ex.GetType().Name}"); }

        if (aebb != null)
        {
            double midZ = (aebb.Min.Z + aebb.Max.Z) / 2.0;
            var np0 = new XYZ(p0.X, p0.Y, midZ);
            var np1 = new XYZ(p1.X, p1.Y, midZ);
            if (np0.DistanceTo(np1) >= GeometryService.MIN_POINT_DIST_FT)
            {
                log.Add($"[INFO] Wall {wid}: Floor load projected using analytical element bounding-box elevation.");
                return Line.CreateBound(np0, np1);
            }
        }

        return null;
    }

    /// <summary>
    /// Filter that allows selecting only Wall elements (host model) or
    /// RevitLinkInstance elements (so walls inside links can also be picked).
    /// </summary>
    private class WallOrLinkFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) =>
            (elem is Wall) || (elem is RevitLinkInstance);

        public bool AllowReference(Reference reference, XYZ position) => true;
    }

    /// <summary>
    /// Filter that allows selecting only Floors and Structural Framing members
    /// (beams) in the current document.
    /// </summary>
    private class HostElementFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem is Floor) return true;
            if (elem.Category != null &&
                elem.Category.Id == new ElementId(BuiltInCategory.OST_StructuralFraming))
                return true;
            return false;
        }

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    private ElementId GetAnalyticalId(Element physElem)
    {
        try
        {
            var mgr = AnalyticalToPhysicalAssociationManager
                          .GetAnalyticalToPhysicalAssociationManager(_doc);
            return mgr.GetAssociatedElementId(physElem.Id);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[StructuralTools] GetAnalyticalId failed for {physElem.Id}: {ex.Message}");
            return ElementId.InvalidElementId;
        }
    }

    private static string? ClassifyHost(Element? elem)
    {
        if (elem == null) return null;
        if (elem is Floor) return "floor";
        if (elem.Category != null &&
            elem.Category.Id == new ElementId(BuiltInCategory.OST_StructuralFraming))
            return "beam";
        return null;
    }

    private Curve? GetBeamCurve(ElementId analyticalId)
    {
        var ae = _doc.GetElement(analyticalId);
        if (ae == null) return null;

        if (ae is Autodesk.Revit.DB.Structure.AnalyticalMember member)
            return member.GetCurve();

        if (ae is AnalyticalElement analElem)
            return analElem.GetCurve();

        return null;
    }

    private static double GetFloorZ(Floor floor)
    {
        try
        {
            var lv = floor.Document.GetElement(floor.LevelId) as Level;
            if (lv != null)
            {
                double offsetFt = 0.0;
                var p = floor.LookupParameter("Base Offset")
                     ?? floor.LookupParameter("Height Offset From Level");

                if (p != null && p.HasValue)
                    offsetFt = p.AsDouble();

                if (p == null)
                {
                    var levelParam = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                    if (levelParam != null && levelParam.HasValue)
                        offsetFt = levelParam.AsDouble();
                }

                return lv.Elevation + offsetFt;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[StructuralTools] GetFloorZ failed for floor {floor.Id}: {ex.Message}");
        }

        var bb = floor.get_BoundingBox(null);
        return bb?.Max.Z ?? double.NaN;
    }

    private static bool TryFindInserts(Wall wall, out IList<ElementId> insertIds)
    {
        insertIds = new List<ElementId>();
        if (wall == null) return false;
        try
        {
            insertIds = wall.FindInserts(true, false, true, true);
            return true;
        }
        catch (RevitInvalidOperationException)
        {
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[StructuralTools] FindInserts failed for wall {wall.Id}: {ex.Message}");
            return false;
        }
    }

    private static bool AssignLoadCase(LineLoad ll, LoadCase lc)
    {
        var p = ll.get_Parameter(BuiltInParameter.LOAD_CASE_ID);
        if (p != null && !p.IsReadOnly)
        {
            p.Set(lc.Id);
            return true;
        }

        foreach (string name in new[] { "Load Case", "Case" })
        {
            var np = ll.LookupParameter(name);
            if (np != null && !np.IsReadOnly)
            {
                np.Set(lc.Id);
                return true;
            }
        }
        return false;
    }

    private (LoadCase? lc, bool matched) DetectDeadLoadCase()
    {
        var cases = new FilteredElementCollector(_doc)
            .OfClass(typeof(LoadCase))
            .Cast<LoadCase>()
            .ToList();

        foreach (var c in cases)
        {
            string n = c.Name.ToLowerInvariant();
            if (n.Contains("dead") || n.Contains("dl"))
                return (c, true);
        }
        return cases.Count > 0 ? (cases[0], false) : ((LoadCase?)null, false);
    }

    private LineLoadType? GetDefaultLoadType() =>
        new FilteredElementCollector(_doc)
            .OfClass(typeof(LineLoadType))
            .Cast<LineLoadType>()
            .FirstOrDefault();

    private static string GetElemLabel(Element? elem)
    {
        if (elem == null) return "—";
        try { return $"{elem.Name} (ID {elem.Id})"; }
        catch { return $"{elem.GetType().Name} (ID {elem.Id})"; }
    }

    private static string GetHostLabel(Element? elem)
    {
        if (elem == null) return "—";
        string kind = ClassifyHost(elem) == "beam" ? "Beam" : "Floor/Panel";
        return $"{kind} · {GetElemLabel(elem)}";
    }
}
