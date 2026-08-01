using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using StructuralTools.Models;
using StructuralTools.Services;
using RevitOperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;
using RevitInvalidOperationException = Autodesk.Revit.Exceptions.InvalidOperationException;

namespace StructuralTools.Engine;

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
    // ---------------------------------------------------------------------
    // Configuration constants
    // ---------------------------------------------------------------------

    /// <summary>Default concrete density (kN/m³) used when no material density is found.</summary>
    private const double DEFAULT_CONCRETE_DENSITY_KN_M3 = 24.0;

    /// <summary>If errors exceed this fraction of total walls, the whole transaction rolls back.</summary>
    private const double ERROR_ROLLBACK_THRESHOLD = 0.5;

    // ---------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------
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

    // ---------------------------------------------------------------------
    // Public entry point
    // ---------------------------------------------------------------------

    /// <summary>
    /// Run the full pick-walls → pick-host → generate-loads flow.
    /// </summary>
    public Result Run()
    {
        // ---- STEP 1: Pick walls ------------------------------------------------
        // PickObjects enters Revit's native selection mode. Revit shows the green
        // "Modify" contextual tab with Finish (✓) and Cancel (✗) buttons.
        // The WallOrLinkFilter makes only Wall elements (and RevitLinkInstances
        // for picking through links) clickable — everything else is greyed out.
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

    // ---------------------------------------------------------------------
    // Selection
    // ---------------------------------------------------------------------

    /// <summary>
    /// Pick walls from the host model or linked models. Returns an empty list
    /// if the user cancels Pass 1 (the red X on the first prompt).
    ///
    /// Two sequential passes are used because <see cref="ObjectType.LinkedElement"/>
    /// — which is required to select elements inside linked Revit models — does NOT
    /// expose host-document elements as selectable targets in all Revit versions.
    /// Using <see cref="ObjectType.Element"/> for host walls is the reliable path.
    ///
    /// Pass 1 (host walls): <see cref="ObjectType.Element"/> + <see cref="HostWallFilter"/>.
    ///   Cancel (✗) aborts the entire command.
    /// Pass 2 (linked walls): <see cref="ObjectType.LinkedElement"/> + <see cref="WallOrLinkFilter"/>.
    ///   Cancel (✗) is treated as "no linked walls" — not an error.
    /// </summary>
    private List<WallEntry> PickWalls()
    {
        var allWalls   = new List<WallEntry>();
        var seenKeys   = new HashSet<string>();
        int skipped    = 0;
        int duplicates = 0;

        // ---- Pass 1: host model walls (ObjectType.Element) ------------------
        IList<Reference> hostRefs;
        try
        {
            hostRefs = _uiDoc.Selection.PickObjects(
                ObjectType.Element,
                new HostWallFilter(),
                "Pass 1 of 2 — Select walls in THIS model. Finish (✓) when done.");
        }
        catch (RevitOperationCanceledException)
        {
            return new List<WallEntry>(); // user cancelled — abort command
        }

        foreach (var r in hostRefs)
        {
            string key = $"host:{r.ElementId.Value}";
            if (seenKeys.Contains(key)) { duplicates++; continue; }
            if (_doc.GetElement(r.ElementId) is not Wall w) { skipped++; continue; }
            seenKeys.Add(key);
            allWalls.Add(new WallEntry(w, Transform.Identity, null));
        }

        // ---- Pass 2: linked model walls (ObjectType.LinkedElement) -----------
        // Optional — Cancel (✗) means "no linked walls"; it is not an error.
        IList<Reference> linkedRefs;
        try
        {
            linkedRefs = _uiDoc.Selection.PickObjects(
                ObjectType.LinkedElement,
                new WallOrLinkFilter(),
                "Pass 2 of 2 — Select walls in LINKED models, or Cancel (✗) to skip.");
        }
        catch (RevitOperationCanceledException)
        {
            linkedRefs = Array.Empty<Reference>();
        }

        foreach (var r in linkedRefs)
        {
            bool isLinked = r.LinkedElementId != ElementId.InvalidElementId;
            string key = isLinked
                ? $"link:{r.ElementId.Value}:{r.LinkedElementId.Value}"
                : $"host:{r.ElementId.Value}";

            if (seenKeys.Contains(key)) { duplicates++; continue; }

            if (isLinked)
            {
                if (_doc.GetElement(r.ElementId) is not RevitLinkInstance li) { skipped++; continue; }
                var ld = li.GetLinkDocument();
                if (ld == null) { skipped++; continue; }
                if (ld.GetElement(r.LinkedElementId) is not Wall w) { skipped++; continue; }
                seenKeys.Add(key);
                allWalls.Add(new WallEntry(w, li.GetTotalTransform(), ld.Title));
            }
            else
            {
                // Some Revit versions do return host-document elements via
                // ObjectType.LinkedElement (LinkedElementId == InvalidElementId).
                // Accept them here to avoid losing them; deduplication via
                // seenKeys prevents double-counting with Pass 1.
                if (_doc.GetElement(r.ElementId) is not Wall w) { skipped++; continue; }
                seenKeys.Add(key);
                allWalls.Add(new WallEntry(w, Transform.Identity, null));
            }
        }

        if (allWalls.Count == 0)
        {
            TaskDialog.Show("Wall Load Generator",
                "No Wall elements were picked.\n" +
                (skipped    > 0 ? $"({skipped} non-wall pick(s) ignored)\n"  : "") +
                (duplicates > 0 ? $"({duplicates} duplicate pick(s) ignored)" : ""));
        }

        return allWalls;
    }

    /// <summary>
    /// Pick the host beam or floor (current model only). Returns null on cancel.
    /// Uses an <see cref="ISelectionFilter"/> so only Floors and Structural Framing
    /// members are clickable — everything else is greyed out.
    /// </summary>
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

    // ---------------------------------------------------------------------
    // Summary
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // Load creation
    // ---------------------------------------------------------------------

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
        Transform transform = entry.Transform;  // WallEntry.Transform is never null (defaults to Identity)
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

    // ---------------------------------------------------------------------
    // Host projection
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // Selection filters
    // ---------------------------------------------------------------------

    /// <summary>
    /// Filter for Pass 1 (host model walls). Only Wall elements in the current
    /// document are selectable — everything else is greyed out.
    /// Used with <see cref="ObjectType.Element"/>.
    /// </summary>
    private class HostWallFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is Wall;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    /// <summary>
    /// Filter for Pass 2 (linked model walls). Allows selecting a
    /// <see cref="RevitLinkInstance"/> (so the user can "enter" the link)
    /// and Wall elements found inside linked models.
    /// Used with <see cref="ObjectType.LinkedElement"/>.
    /// </summary>
    private class WallOrLinkFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) =>
            (elem is Wall) || (elem is RevitLinkInstance);

        public bool AllowReference(Reference reference, XYZ position) => true;
    }

    /// <summary>
    /// Filter that allows selecting only Floors and Structural Framing members
    /// (beams) in the current document. Linked elements are not allowed because
    /// analytical loads must be hosted on elements in the current model.
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

    // ---------------------------------------------------------------------
    // Misc helpers
    // ---------------------------------------------------------------------

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
