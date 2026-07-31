using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitOperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;
using RevitInvalidOperationException = Autodesk.Revit.Exceptions.InvalidOperationException;
using StructuralTools.Models;

namespace StructuralTools.Engine
{
    public class WallLoadEngine
    {
        // Configuration constants
        private const double DEFAULT_FUDGE_FACTOR_PCT = 10.0;
        private const bool DEFAULT_APPLY_FUDGE = true;
        private const double GRAVITY_M_S2 = 9.80665;
        
        // Tolerance constants (all in Revit internal units: feet)
        private const double FT_PER_M = 3.28084;
        private const double MIN_WALL_HEIGHT_FT = 0.001;           // ~0.3mm
        private const double MIN_CURVE_LENGTH_FT = 0.0328;         // ~10mm
        private const double MIN_SEGMENT_LENGTH_M = 0.010;         // 10mm
        private const double MIN_LOAD_SEGMENT_LENGTH_FT = 0.0025;  // ~0.76mm
        private const double MIN_NET_HEIGHT_M = 0.01;              // 10mm
        private const double MIN_LOAD_VALUE = 0.001;               // kN/m
        private const double MIN_OPENING_HEIGHT_FT = 0.001;        // ~0.3mm
        private const double MIN_PARAM_RANGE = 0.001;              // parameter tolerance
        private const double MIN_NORMAL_LENGTH = 0.001;            // vector length tolerance
        
        // Default concrete density (kN/m³)
        private const double DEFAULT_CONCRETE_DENSITY_KN_M3 = 24.0;

        private readonly UIApplication _uiApp;
        private readonly UIDocument _uiDoc;
        private readonly Document _doc;

        private List<WallEntry> _selectedWalls = new List<WallEntry>();
        private Element? _hostElement = null;
        private bool _applyFudge = DEFAULT_APPLY_FUDGE;
        private string _fudgePctText = DEFAULT_FUDGE_FACTOR_PCT.ToString("G4");
        private Dictionary<ElementId, double> _materialWeightCache;

        public WallLoadEngine(UIApplication uiApp)
        {
            _uiApp = uiApp;
            _uiDoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("No active document. Please open a Revit document first.");
            _doc = _uiDoc.Document;
            _materialWeightCache = new Dictionary<ElementId, double>();
        }

        public Result Run()
        {
            _materialWeightCache = new Dictionary<ElementId, double>();
            string? lastStatus = null;

            while (true)
            {
                var dialogData = ShowMainDialog(lastStatus);
                
                if (dialogData.Cancelled)
                    return Result.Cancelled;

                _applyFudge = dialogData.ApplyFudge;
                _fudgePctText = dialogData.FudgePctText;
                lastStatus = null;

                switch (dialogData.Action)
                {
                    case DialogAction.PickWalls:
                        lastStatus = PickWalls();
                        break;
                    case DialogAction.PickHost:
                        lastStatus = PickHost();
                        break;
                    case DialogAction.Generate:
                        lastStatus = Generate();
                        if (lastStatus == null)
                            return Result.Succeeded;
                        break;
                    case DialogAction.Settings:
                        ShowSettingsDialog();
                        lastStatus = "Settings updated.";
                        break;
                }
            }
        }

        private (DialogAction Action, bool Cancelled, bool ApplyFudge, string FudgePctText) ShowMainDialog(string? lastStatus)
        {
            int wallCount = _selectedWalls.Count;
            int linkedCount = _selectedWalls.Count(w => w.Source != null);
            int hostCount = wallCount - linkedCount;
            string wallsInfo = wallCount > 0
                ? $"{wallCount} wall(s) selected ({hostCount} host, {linkedCount} linked)"
                : "No walls selected";

            string hostInfo = _hostElement != null ? GetHostLabel(_hostElement) : "No host element selected";
            var (loadCase, lcMatched) = DetectDeadLoadCase();
            string loadCaseInfo = loadCase != null
                ? $"{loadCase.Name}{(lcMatched ? "" : " (⚠ auto-picked)")}"
                : "⚠ No load cases found";

            string message = $"Wall → Line Load Generator\n\n" +
                             $"STEP 1 — Walls:\n{wallsInfo}\n\n" +
                             $"STEP 2 — Host Element:\n{hostInfo}\n\n" +
                             $"Load Case:\n{loadCaseInfo}\n\n" +
                             $"Conservatism:\nFudge factor: {(_applyFudge ? $"+{_fudgePctText}%" : "Not applied")}\n\n" +
                             (string.IsNullOrEmpty(lastStatus) ? "" : $"Status: {lastStatus}\n\n");

            var dialog = new TaskDialog("Wall Load Generator");
            dialog.MainInstruction = "Wall → Line Load Generator";
            dialog.MainContent = message;
            
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, 
                "📐 Pick Walls (click or box-select, host or linked models)",
                wallCount > 0 ? $"Current: {wallsInfo}" : "");
            
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                "🏗 Pick Host Element (beam or floor, current model only)",
                _hostElement != null ? $"Current: {GetHostLabel(_hostElement)}" : "");
            
            bool canGenerate = wallCount > 0 && _hostElement != null;
            if (canGenerate)
            {
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
                    "⚡ Generate Line Loads",
                    $"Will create loads on {_hostElement.Name}");
            }
            
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "⚙ Settings (fudge factor, defaults)");
            
            dialog.CommonButtons = TaskDialogCommonButtons.Close;

            var result = dialog.Show();
            
            switch (result)
            {
                case TaskDialogResult.CommandLink1:
                    return (DialogAction.PickWalls, false, _applyFudge, _fudgePctText);
                case TaskDialogResult.CommandLink2:
                    return (DialogAction.PickHost, false, _applyFudge, _fudgePctText);
                case TaskDialogResult.CommandLink3:
                    return (DialogAction.Generate, false, _applyFudge, _fudgePctText);
                case TaskDialogResult.CommandLink4:
                    return (DialogAction.Settings, false, _applyFudge, _fudgePctText);
                default:
                    return (DialogAction.None, true, _applyFudge, _fudgePctText);
            }
        }

        private void ShowSettingsDialog()
        {
            string message = $"Current Settings:\n\n" +
                             $"Fudge Factor: {(_applyFudge ? $"+{_fudgePctText}%" : "Not applied")}\n\n" +
                             "The fudge factor adds a conservatism allowance for incomplete modeling.\n\n" +
                             "Note: The Revit TaskDialog API does not support custom text input.\n" +
                             "To change the fudge factor, edit DEFAULT_FUDGE_FACTOR_PCT in the source code.";

            var dialog = new TaskDialog("Settings");
            dialog.MainInstruction = "Wall Load Generator Settings";
            dialog.MainContent = message;
            
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, 
                "Enable Fudge Factor (+10%)",
                _applyFudge ? "Currently enabled" : "");
            
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                "Disable Fudge Factor",
                !_applyFudge ? "Currently disabled" : "");
            
            dialog.CommonButtons = TaskDialogCommonButtons.Close;

            var result = dialog.Show();
            switch (result)
            {
                case TaskDialogResult.CommandLink1:
                    _applyFudge = true;
                    _fudgePctText = DEFAULT_FUDGE_FACTOR_PCT.ToString("G4");
                    break;
                case TaskDialogResult.CommandLink2:
                    _applyFudge = false;
                    break;
            }
        }

        private string? PickWalls()
        {
            SetStatusBar("Select walls — click or drag-box, host model or linked — then press Esc or green tick");
            
            try
            {
                IList<Reference> refs = _uiDoc.Selection.PickObjects(
                    ObjectType.LinkedElement,
                    new WallOrLinkFilter(),
                    "Select walls — click or drag-box, host model or linked — then press Finish (green tick)");

                var newWalls = new List<WallEntry>();
                var seenKeys = new HashSet<string>();
                int skipped = 0;
                int duplicates = 0;

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
                        var linkInst = _doc.GetElement(r.ElementId) as RevitLinkInstance;
                        if (linkInst == null) { skipped++; continue; }
                        var linkDoc = linkInst.GetLinkDocument();
                        if (linkDoc == null) { skipped++; continue; }
                        var elem = linkDoc.GetElement(r.LinkedElementId);
                        if (!(elem is Wall)) { skipped++; continue; }
                        entry = new WallEntry
                        {
                            Wall = (Wall)elem,
                            Transform = linkInst.GetTotalTransform(),
                            Source = linkDoc.Title
                        };
                    }
                    else
                    {
                        var elem = _doc.GetElement(r.ElementId);
                        if (!(elem is Wall)) { skipped++; continue; }
                        entry = new WallEntry
                        {
                            Wall = (Wall)elem,
                            Transform = Transform.Identity,
                            Source = null
                        };
                    }

                    seenKeys.Add(dedupeKey);
                    newWalls.Add(entry);
                }

                ClearStatusBar();

                if (newWalls.Count == 0)
                    return "⚠ No Wall elements in selection — try again.";

                _selectedWalls = newWalls;
                var notes = new List<string>();
                if (skipped > 0) notes.Add($"{skipped} non-wall pick(s) ignored");
                if (duplicates > 0) notes.Add($"{duplicates} duplicate reference(s) ignored");
                string suffix = notes.Count > 0 ? "  " + string.Join("; ", notes) + "." : "";
                return $"{newWalls.Count} wall(s) selected.{suffix}";
            }
            catch (RevitOperationCanceledException)
            {
                ClearStatusBar();
                return "Wall selection was cancelled.";
            }
        }

        private string? PickHost()
        {
            SetStatusBar("Select the host beam or floor (must be in the current model, not a link)");
            
            try
            {
                var r = _uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    "Select the host beam or floor (must be in the current model, not a link)");
                var elem = _doc.GetElement(r.ElementId);

                if (elem is RevitLinkInstance)
                {
                    ClearStatusBar();
                    return "⚠ Structural loads can only be hosted on elements in the current model — " +
                           "a linked beam/floor cannot be used as the host.";
                }

                if (ClassifyHost(elem) == null)
                {
                    ClearStatusBar();
                    return "⚠ That element is not a Floor or Structural Framing member. " +
                           "Pick a beam or a floor slab.";
                }

                _hostElement = elem;
                ClearStatusBar();
                return null;
            }
            catch (RevitOperationCanceledException)
            {
                ClearStatusBar();
                return "Host selection was cancelled.";
            }
        }

        private string? Generate()
        {
            var (loadCase, lcMatched) = DetectDeadLoadCase();
            var defaultLoadType = GetDefaultLoadType();

            if (defaultLoadType == null)
            {
                TaskDialog.Show("Error",
                    "No LineLoadType found in the model. Load a structural line-load family first.");
                return "⚠ No LineLoadType found.";
            }
            if (loadCase == null)
            {
                TaskDialog.Show("Error",
                    "No LoadCase found in the model. Create structural load cases first.");
                return "⚠ No LoadCase found.";
            }
            if (_selectedWalls.Count == 0 || _hostElement == null)
                return "⚠ Select at least one wall and a host element before generating.";

            double fudgeMultiplier = 1.0;
            if (_applyFudge)
            {
                if (!double.TryParse((_fudgePctText ?? "").Trim(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double fudgePct) || fudgePct <= 0)
                    return "⚠ Fudge factor must be a positive number (e.g. 10 for 10%). Fix the value and generate again.";
                fudgeMultiplier = 1.0 + fudgePct / 100.0;
            }

            SetStatusBar($"Generating loads on {_selectedWalls.Count} wall(s)...");
            
            var result = CreateLoads(
                _selectedWalls, _hostElement,
                loadCase, DEFAULT_CONCRETE_DENSITY_KN_M3, defaultLoadType,
                fudgeMultiplier);

            int linkedCount = _selectedWalls.Count(w => w.Source != null);
            ClearStatusBar();

            string summary =
                $"✅  {result.Created.Count} load segment(s) created\n" +
                $"⚠  {result.Errors} error(s)\n" +
                $"Load Case: {loadCase.Name}{(lcMatched ? "" : "  (⚠ no case named 'Dead'/'DL' found — used first case, please verify)")}\n" +
                $"Fudge factor: {(_applyFudge ? $"+{_fudgePctText}% applied  (×{fudgeMultiplier:F3})" : "Not applied")}\n" +
                (linkedCount > 0 ? $"Includes {linkedCount} wall(s) from linked model(s).\n" : "") +
                "\n--- Log ---\n" +
                string.Join("\n", result.Log.Take(20)) +
                (result.Log.Count > 20 ? $"\n… and {result.Log.Count - 20} more entries." : "");

            TaskDialog.Show("Wall Load Generation Complete", summary);
            return null;
        }

        private LoadResult CreateLoads(
            List<WallEntry> wallItems,
            Element physicalHost,
            LoadCase loadCase,
            double fallbackGamma,
            LineLoadType defaultLoadType,
            double fudgeMultiplier = 1.0)
        {
            var res = new LoadResult();

            void Log(string msg, string cat = "INFO") =>
                res.Log.Add($"[{cat}] {msg}");

            if (wallItems == null || wallItems.Count == 0)
            {
                Log("No walls provided.", "ERROR"); res.Errors++; return res;
            }
            if (physicalHost == null)
            {
                Log("No host element provided.", "ERROR"); res.Errors++; return res;
            }

            string? hostType = ClassifyHost(physicalHost);
            ElementId analId = GetAnalyticalId(physicalHost);

            if (analId == ElementId.InvalidElementId)
            {
                Log($"Analytical model not found for host ID {physicalHost.Id}. " +
                    "Enable Analytical Model for this element.", "ERROR");
                res.Errors++;
                return res;
            }

            using (var tx = new Transaction(_doc, "Generate Wall Line Loads"))
            {
                tx.Start();

                foreach (var entry in wallItems)
                {
                    Wall wall = entry.Wall;
                    Transform? transform = entry.Transform;
                    string? source = entry.Source;

                    if (wall == null) continue;

                    string wid = source != null
                        ? $"{wall.Id} (linked: {source})"
                        : wall.Id.ToString();

                    var lc = (wall.Location as LocationCurve)?.Curve;
                    if (lc == null)
                    {
                        Log($"Wall ID {wid}: No location curve.", "WARNING"); res.Errors++; continue;
                    }

                    double wallHeightFt = GetActualWallHeight(wall, wid, res.Log);
                    if (wallHeightFt < MIN_WALL_HEIGHT_FT)
                    {
                        Log($"Wall ID {wid}: Effective height is zero — skipping.", "WARNING");
                        continue;
                    }

                    var bb = wall.get_BoundingBox(null);
                    if (bb == null)
                    {
                        Log($"Wall ID {wid}: No bounding box — cannot clip opening heights.", "WARNING");
                        res.Errors++; continue;
                    }

                    double clLen = lc.Length;
                    if (clLen < MIN_CURVE_LENGTH_FT) continue;

                    double areaW = CalcWallAreaWeight(wall, fallbackGamma, res.Log, wid);

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

                            var info = GetOpeningInfo(ie, bb, lc, ps, pe, paramRange, wid, res.Log);
                            if (info.HasValue)
                                rawOpenings.Add(info.Value);
                        }
                    }

                    var openingData = MergeIntervals(rawOpenings);

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

                    int wallCount = 0;

                    for (int i = 0; i < tKnots.Count - 1; i++)
                    {
                        double t0 = tKnots[i], t1 = tKnots[i + 1];
                        if (InternalLenToM((t1 - t0) * clLen) < MIN_SEGMENT_LENGTH_M) continue;

                        double tMid = (t0 + t1) / 2.0;
                        double dh = openingData
                            .Where(op => op.tMin <= tMid && tMid <= op.tMax)
                            .Sum(op => op.h);

                        double netHm = InternalLenToM(Math.Max(0.0, wallHeightFt - dh));
                        if (netHm < MIN_NET_HEIGHT_M) continue;

                        double loadVal = areaW * netHm;
                        if (loadVal <= MIN_LOAD_VALUE) continue;
                        loadVal *= fudgeMultiplier;

                        foreach (var sc in GetSubCurve(lc, t0, t1))
                        {
                            if (sc.Length < MIN_LOAD_SEGMENT_LENGTH_FT) continue;

                            Curve raw = (transform.HasValue && !transform.Value.IsIdentity)
                                ? sc.CreateTransformed(transform.Value)
                                : sc;

                            Curve? lcCurve = BuildHostedCurve(raw, physicalHost, hostType, analId, res.Log, wid);
                            if (lcCurve == null) { res.Errors++; continue; }

                            var fv = new XYZ(0, 0, -KnPerMToInternal(loadVal));
                            try
                            {
                                var ll = LineLoad.Create(
                                    _doc, analId, lcCurve,
                                    fv, XYZ.Zero, defaultLoadType);

                                if (!AssignLoadCase(ll, loadCase))
                                {
                                    res.LcFails++;
                                    Log($"Wall ID {wid} [{t0:F3}–{t1:F3}]: load-case param not set.", "WARNING");
                                }
                                res.Created.Add(ll);
                                wallCount++;
                            }
                            catch (Exception ex)
                            {
                                Log($"Wall ID {wid} [{t0:F3}–{t1:F3}]: {ex.Message}", "ERROR");
                                res.Errors++;
                            }
                        }
                    }

                    string fudgeNote = fudgeMultiplier != 1.0
                        ? $"  x{fudgeMultiplier:F3} fudge (+{(fudgeMultiplier - 1.0) * 100:G4}%)"
                        : "";
                    Log($"Wall ID {wid}: {wallCount} segment(s) created  [{areaW:F2} kN/m²{fudgeNote}]",
                        "SUCCESS");
                }

                tx.Commit();
            }

            return res;
        }

        private class WallOrLinkFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) =>
                (elem is Wall) || (elem is RevitLinkInstance);

            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        private enum DialogAction { None, PickWalls, PickHost, Generate, Settings }

        #region Helper Methods

        private static double GetActualWallHeight(Wall wall, string wid, List<string> log)
        {
            var p = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (p != null && p.HasValue)
            {
                double h = p.AsDouble();
                if (h > MIN_OPENING_HEIGHT_FT)
                {
                    log?.Add($"[INFO] Wall {wid}: Used WALL_USER_HEIGHT_PARAM height ({InternalLenToM(h):F3} m).");
                    return h;
                }
            }

            var opts = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };

            GeometryElement geom = wall.get_Geometry(opts);
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
                            log?.Add($"[INFO] Wall {wid}: Used geometry solid bounding box height ({InternalLenToM(h):F3} m).");
                            return h;
                        }
                    }
                }
            }

            var bb = wall.get_BoundingBox(null);
            if (bb != null)
            {
                double h = bb.Max.Z - bb.Min.Z;
                log?.Add($"[INFO] Wall {wid}: Geometry extraction failed. Using element bounding box height ({InternalLenToM(h):F3} m).");
                return h;
            }

            log?.Add($"[WARNING] Wall {wid}: Could not determine wall height — returning 0.");
            return 0.0;
        }

        private static void ExtractLargestSolid(GeometryObject obj, ref Solid? largest, ref double maxVol)
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

        private static (double tMin, double tMax, double h)? GetOpeningInfo(
            Element insert,
            BoundingBoxXYZ wallBB,
            Curve lc,
            double ps,
            double pe,
            double paramRange,
            string wid,
            List<string> log)
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
            try { geom = insert.get_Geometry(opts); } catch (Exception ex) { Log($"Failed to get geometry for opening {insert.Id}: {ex.Message}", "DEBUG"); }

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

                        double tMin = double.MaxValue;
                        double tMax = double.MinValue;
                        bool any = false;

                        foreach (var pt in corners)
                        {
                            IntersectionResult? pr = null;
                            try { pr = lc.Project(pt); } catch (Exception ex) { Log($"Failed to project point for opening {insert.Id}: {ex.Message}", "DEBUG"); }
                            if (pr == null) continue;

                            double t = (pr.Parameter - ps) / paramRange;
                            t = Math.Max(0.0, Math.Min(1.0, t));
                            if (t < tMin) { tMin = t; any = true; }
                            if (t > tMax) { tMax = t; any = true; }
                        }

                        if (any && tMax - tMin > MIN_PARAM_RANGE && oh > 0.0)
                            return (tMin, tMax, oh);
                    }
                }
            }

            BoundingBoxXYZ? ib = null;
            try { ib = insert.get_BoundingBox(null); } catch (Exception ex) { Log($"Failed to get bounding box for opening {insert.Id}: {ex.Message}", "DEBUG"); }
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

            double tMin2 = double.MaxValue;
            double tMax2 = double.MinValue;
            bool any2 = false;

            foreach (var pt in pts2)
            {
                IntersectionResult? pr = null;
                try { pr = lc.Project(pt); } catch (Exception ex) { Log($"Failed to project point for opening bbox {insert.Id}: {ex.Message}", "DEBUG"); }
                if (pr == null) continue;

                double t = (pr.Parameter - ps) / paramRange;
                t = Math.Max(0.0, Math.Min(1.0, t));
                if (t < tMin2) { tMin2 = t; any2 = true; }
                if (t > tMax2) { tMax2 = t; any2 = true; }
            }

            if (any2 && tMax2 - tMin2 > MIN_PARAM_RANGE)
                return (tMin2, tMax2, oh2);

            return null;
        }

        private static List<(double tMin, double tMax, double h)> MergeIntervals(
            List<(double tMin, double tMax, double h)> intervals)
        {
            var result = new List<(double tMin, double tMax, double h)>();
            if (intervals == null || intervals.Count == 0) return result;

            var sorted = intervals.OrderBy(iv => iv.tMin).ToList();
            var current = sorted[0];

            for (int i = 1; i < sorted.Count; i++)
            {
                var next = sorted[i];
                if (next.tMin <= current.tMax + 1e-6)
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

        private static double InternalLenToM(double ft)
        {
            try { return UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters); }
            catch (Exception ex) 
            { 
                Log($"Unit conversion fallback (length): {ex.Message}", "DEBUG");
                return ft * 0.3048; 
            }
        }

        private static double InternalUnitWeightToKnM3(double v)
        {
            try { return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.KilonewtonsPerCubicMeter); }
            catch (Exception ex) 
            { 
                Log($"Unit conversion fallback (unit weight): {ex.Message}", "DEBUG");
                return v * 0.101971621; 
            }
        }

        private static double InternalDensityToKgM3(double v)
        {
            try { return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.KilogramsPerCubicMeter); }
            catch (Exception ex) 
            { 
                Log($"Unit conversion fallback (density): {ex.Message}", "DEBUG");
                return v * 16.0184634; 
            }
        }

        private static double KnPerMToInternal(double v)
        {
            try { return UnitUtils.ConvertToInternalUnits(v, UnitTypeId.KilonewtonsPerMeter); }
            catch (Exception ex) 
            { 
                Log($"Unit conversion fallback (force/length): {ex.Message}", "DEBUG");
                return v * 0.0685218; // 1 kN/m = 0.0685218 kip/ft
            }
        }

        private double GetMaterialUnitWeightCached(Material? mat, double defaultGamma)
        {
            if (mat == null) return defaultGamma;

            if (_materialWeightCache.TryGetValue(mat.Id, out double cached))
                return cached;

            double weight = ComputeMaterialUnitWeight(mat, defaultGamma);
            _materialWeightCache[mat.Id] = weight;
            return weight;
        }

        private static double ComputeMaterialUnitWeight(Material mat, double defaultGamma)
        {
            if (mat.StructuralAssetId != ElementId.InvalidElementId)
            {
                var pse = mat.Document.GetElement(mat.StructuralAssetId) as PropertySetElement;
                if (pse != null)
                {
                    var sa = pse.GetStructuralAsset();
                    if (sa != null && sa.Density > 0)
                    {
                        double kgM3 = InternalDensityToKgM3(sa.Density);
                        return kgM3 * GRAVITY_M_S2 / 1000.0;
                    }
                }
            }

            var p = mat.get_Parameter(BuiltInParameter.PHY_MATERIAL_PARAM_UNIT_WEIGHT);
            if (p != null && p.HasValue)
            {
                double uw = p.AsDouble();
                if (uw > 0) return InternalUnitWeightToKnM3(uw);
            }

            return defaultGamma;
        }

        private double CalcWallAreaWeight(Wall wall, double fallback, List<string> log, string wid)
        {
            var wt = wall.WallType;
            CompoundStructure? cs = null;
            try { cs = wt.GetCompoundStructure(); } catch (Exception ex) { Log($"Failed to get compound structure for wall {wall.Id}: {ex.Message}", "DEBUG"); }

            if (cs != null)
            {
                double total = 0.0;
                foreach (var layer in cs.GetLayers())
                {
                    double tM = InternalLenToM(layer.Width);
                    Material? mat = (layer.MaterialId != ElementId.InvalidElementId)
                        ? wall.Document.GetElement(layer.MaterialId) as Material
                        : null;
                    total += tM * GetMaterialUnitWeightCached(mat, fallback);
                }
                if (total > 0) return total;
            }

            return InternalLenToM(wt.Width) * fallback;
        }

        private static IEnumerable<Curve> GetSubCurve(Curve curve, double t0, double t1)
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
                    if (pts[i].DistanceTo(pts[i + 1]) > 0.005)
                        yield return Line.CreateBound(pts[i], pts[i + 1]);
                }
            }
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
                Log($"Failed to get analytical ID for {physElem.Id}: {ex.Message}", "DEBUG");
                return ElementId.InvalidElementId; 
            }
        }

        private static string? ClassifyHost(Element elem)
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

            var member = ae as Autodesk.Revit.DB.Structure.AnalyticalMember;
            if (member != null) return member.GetCurve();

            var analElem = ae as AnalyticalElement;
            if (analElem != null) return analElem.GetCurve();

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
            catch (Exception ex) { Log($"Failed to get host elevation for {floor.Id}: {ex.Message}", "DEBUG"); }

            var bb = floor.get_BoundingBox(null);
            return bb?.Max.Z ?? double.NaN;
        }

        private static XYZ ProjectOntoCurve(XYZ pt, Curve curve)
        {
            try
            {
                var r = curve.Project(pt);
                if (r != null) return r.XYZPoint;
            }
            catch (Exception ex) { Log($"Failed to project point onto curve: {ex.Message}", "DEBUG"); }

            if (curve is Line ln)
            {
                var o = ln.GetEndPoint(0);
                var d = (ln.GetEndPoint(1) - o).Normalize();
                double dist = (pt - o).DotProduct(d);
                return o + d * Math.Max(0.0, Math.Min(ln.Length, dist));
            }
            return pt;
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
                var np0 = ProjectOntoCurve(p0, bc);
                var np1 = ProjectOntoCurve(p1, bc);
                if (np0.DistanceTo(np1) < 0.005) return null;
                if (!np0.IsAlmostEqualTo(np1))
                    return Line.CreateBound(np0, np1);
                return null;
            }
            else if (hostType == "floor")
            {
                Curve? analResult = TryProjectOntoAnalyticalFloor(analId, p0, p1, log, wid);
                if (analResult != null) return analResult;

                log?.Add($"[INFO] Wall {wid}: Analytical floor projection unavailable — using level elevation.");
                double fz = GetFloorZ((Floor)physHost);
                if (double.IsNaN(fz)) return sc;
                var np0 = new XYZ(p0.X, p0.Y, fz);
                var np1 = new XYZ(p1.X, p1.Y, fz);
                if (np0.DistanceTo(np1) < 0.005) return null;
                return Line.CreateBound(np0, np1);
            }

            return sc;
        }

        private Curve? TryProjectOntoAnalyticalFloor(
            ElementId analId,
            XYZ p0,
            XYZ p1,
            List<string> log,
            string wid)
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
            catch (Exception ex) 
            { 
                Log($"Failed to get geometry for analytical element {analId}: {ex.Message}", "DEBUG"); 
            }

            if (geom != null)
            {
                Face? bestFace = null;
                double bestArea = 0.0;

                foreach (GeometryObject obj in geom)
                {
                    if (obj == null) continue;
                    Solid solid = obj as Solid;
                    if (solid == null || solid.Volume <= 0) continue;

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

                    if (normal != null && normal.GetLength() > MIN_NORMAL_LENGTH)
                    {
                        Plane plane = Plane.CreateByNormalAndOrigin(normal.Normalize(), origin);
                        XYZ np0 = ProjectPointOntoPlane(p0, plane);
                        XYZ np1 = ProjectPointOntoPlane(p1, plane);
                        if (np0 != null && np1 != null && np0.DistanceTo(np1) >= 0.005)
                        {
                            log?.Add($"[INFO] Wall {wid}: Floor load projected onto analytical surface plane.");
                            return Line.CreateBound(np0, np1);
                        }
                    }
                }
            }

            BoundingBoxXYZ? aebb = null;
            try { aebb = ae.get_BoundingBox(null); } 
            catch (Exception ex) 
            { 
                Log($"Failed to get bounding box for analytical element {analId}: {ex.Message}", "DEBUG"); 
            }

            if (aebb != null)
            {
                double midZ = (aebb.Min.Z + aebb.Max.Z) / 2.0;
                var np0 = new XYZ(p0.X, p0.Y, midZ);
                var np1 = new XYZ(p1.X, p1.Y, midZ);
                if (np0.DistanceTo(np1) >= 0.005)
                {
                    log?.Add($"[INFO] Wall {wid}: Floor load projected using analytical element bounding-box elevation.");
                    return Line.CreateBound(np0, np1);
                }
            }

            return null;
        }

        private static XYZ? ProjectPointOntoPlane(XYZ pt, Plane plane)
        {
            if (pt == null || plane == null) return null;
            double dist = plane.Normal.DotProduct(pt - plane.Origin);
            return pt - plane.Normal.Multiply(dist);
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
            catch (Exception)
            {
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
                string n = c.Name.ToLower();
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

        private void SetStatusBar(string message)
        {
            // Note: StatusBar access requires Revit 2027+ with MainWindow property
            // This is a no-op in earlier versions or if MainWindow is not available
            try
            {
                var mainWindowProp = _uiApp.GetType().GetProperty("MainWindow");
                if (mainWindowProp != null)
                {
                    var mainWindow = mainWindowProp.GetValue(_uiApp);
                    if (mainWindow != null)
                    {
                        var statusBarProp = mainWindow.GetType().GetProperty("StatusBar");
                        if (statusBarProp != null)
                        {
                            var statusBar = statusBarProp.GetValue(mainWindow);
                            if (statusBar != null)
                            {
                                var statusTextProp = statusBar.GetType().GetProperty("StatusText");
                                if (statusTextProp != null)
                                    statusTextProp.SetValue(statusBar, message);
                            }
                        }
                    }
                }
            }
            catch
            {
                // StatusBar not available - silently ignore
            }
        }

        private void ClearStatusBar()
        {
            SetStatusBar("Ready");
        }

        #endregion
    }
}
