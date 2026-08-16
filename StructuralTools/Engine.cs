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
/// Diagnostic command that identifies line loads with warnings (not fully hosted on analytical elements)
/// and highlights them in red in the current view. Warnings typically indicate loads that partially
/// overhang the analytical structure, which can cause structural analysis issues.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class HighlightProblematicLoadsCommand : IExternalCommand
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
            var doc = uiApp.ActiveUIDocument.Document;
            var activeView = uiApp.ActiveUIDocument.ActiveView;

            if (activeView == null)
            {
                TaskDialog.Show("Structural Tools",
                    "Open a view (plan or 3D) before running this command.");
                return Result.Cancelled;
            }

            // Collect all line loads
            var allLineLoads = new FilteredElementCollector(doc)
                .OfClass(typeof(LineLoad))
                .WhereElementIsNotElementType()
                .Cast<LineLoad>()
                .ToList();

            if (allLineLoads.Count == 0)
            {
                TaskDialog.Show("Structural Tools",
                    "No line loads found in the model.");
                return Result.Cancelled;
            }

            // Collect IDs of line loads with warnings and classify their repair case.
            var warnedLoadIds = new HashSet<ElementId>();
            var warningMessages = new HashSet<string>();

            // Store the diagnosis in the same transaction as the visual overrides so
            // the persisted schema survives and can be read back by the repair tool.
            var red = new Color(255, 0, 0);
            var ogs_red = new OverrideGraphicSettings().SetProjectionLineColor(red);
            var ogs_clear = new OverrideGraphicSettings();

            using (var tx = new Transaction(doc, "Highlight Problematic Loads"))
            {
                tx.Start();

                foreach (var warning in doc.GetWarnings())
                {
                    try
                    {
                        string warningText = warning.GetDescriptionText();
                        if (string.IsNullOrWhiteSpace(warningText))
                            continue;

                        RepairCase classification = ClassifyWarning(warningText);
                        var failingElementIds = warning.GetFailingElements();

                        foreach (var elementId in failingElementIds)
                        {
                            var load = allLineLoads.FirstOrDefault(ll => ll.Id == elementId);
                            if (load == null)
                                continue;

                            warnedLoadIds.Add(elementId);
                            warningMessages.Add(warningText);

                            RevitLoadUtils.StoreDiagnosis(doc, load, new LoadDiagnosis
                            {
                                Case = classification,
                                WarningText = warningText,
                                HostId = load.HostElementId != ElementId.InvalidElementId ? load.HostElementId : null,
                                Severity = 1
                            });
                        }
                    }
                    catch { /* Skip problematic warnings */ }
                }

                foreach (var lineLoad in allLineLoads)
                {
                    if (warnedLoadIds.Contains(lineLoad.Id))
                        activeView.SetElementOverrides(lineLoad.Id, ogs_red);
                    else
                        activeView.SetElementOverrides(lineLoad.Id, ogs_clear);
                }

                tx.Commit();
            }

            // Show summary
            string summary = 
                $"✅ Visualization updated in '{activeView.Name}'\n\n" +
                $"Problematic loads (with warnings): {warnedLoadIds.Count}\n" +
                $"Clean loads: {allLineLoads.Count - warnedLoadIds.Count}\n" +
                $"Total loads: {allLineLoads.Count}\n\n" +
                "Problematic loads are highlighted in RED.\n" +
                "These loads may not be fully hosted on analytical elements.\n\n";

            // Add warning classification helper within the command scope.
            static RepairCase ClassifyWarning(string warningText)
            {
                string text = warningText.ToLowerInvariant();

                if (text.Contains("line load exceeds the host boundaries") ||
                    text.Contains("exceeds the host boundaries") ||
                    text.Contains("host boundaries") ||
                    text.Contains("placed over an analytical opening") ||
                    text.Contains("analytical opening") ||
                    text.Contains("over an analytical opening") ||
                    text.Contains("overhang") ||
                    text.Contains("panel edge") ||
                    text.Contains("analytical panel") ||
                    text.Contains("outside") ||
                    text.Contains("edge") ||
                    text.Contains("trim") ||
                    text.Contains("boundary"))
                {
                    return RepairCase.OverhangPanelEdge;
                }

                if (text.Contains("node") || text.Contains("endpoint") || text.Contains("end point") || text.Contains("beyond") || text.Contains("overshoot") || text.Contains("extend"))
                    return RepairCase.ExtendsBeyondNode;

                if (text.Contains("offset") || text.Contains("parallel") || text.Contains("misalign") || text.Contains("alignment"))
                    return RepairCase.OffsetToEdge;

                if (text.Contains("host") || text.Contains("member") || text.Contains("floating") || text.Contains("not attached") || text.Contains("not hosted") || text.Contains("nearest member"))
                    return RepairCase.MoveToNearestMember;

                if (text.Contains("short") || text.Contains("degenerate") || text.Contains("negligible") || text.Contains("zero length") || text.Contains("too small"))
                    return RepairCase.DeleteMinimalLoad;

                if (text.Contains("snap") || text.Contains("near edge") || text.Contains("closest") || text.Contains("panel"))
                    return RepairCase.SnapToPanelEdge;

                return RepairCase.ManualReview;
            }

            if (warningMessages.Count > 0)
            {
                summary += "Sample warnings:\n" + string.Join("\n", warningMessages.Take(3));
            }

            TaskDialog.Show("Load Diagnostic", summary);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = $"Error highlighting loads: {ex.Message}\n\n{ex.StackTrace}";
            TaskDialog.Show("Structural Tools - Error", message);
            return Result.Failed;
        }
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

