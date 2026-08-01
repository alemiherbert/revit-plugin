using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using StructuralTools.StaircaseEngine;
using RevitOperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace StructuralTools.Engine;

/// <summary>
/// Thin orchestrator for the Staircase → Analytical Model converter.
/// Delegates all real work to the <see cref="StaircaseEngine"/> namespace's
/// classifier, engine strategies, connectivity resolver, and model builder.
///
/// Pipeline:
///   1. Pick stairs (native Revit selection — green Modify tab, Finish/Cancel)
///   2. For each stair: classify into a StairGraph
///   3. Route each node to the appropriate engine (Straight / Curved / Winder)
///   4. Resolve shared edges between adjacent panels (snap XYZ coordinates)
///   5. Create AnalyticalPanel elements with material + thickness
///   6. Commit transaction, show summary
///
/// If more than 50% of panels fail to create, the transaction is rolled back
/// so the model is not left in a half-baked state.
/// </summary>
public class StaircaseEngine
{
    /// <summary>If errors exceed this fraction of total panels, roll back.</summary>
    private const double ERROR_ROLLBACK_THRESHOLD = 0.5;

    private readonly UIDocument _uiDoc;
    private readonly Document _doc;

    public StaircaseEngine(UIApplication uiApp)
    {
        UIApplication app = uiApp ?? throw new ArgumentNullException(nameof(uiApp));
        UIDocument uiDoc = app.ActiveUIDocument ?? throw new InvalidOperationException(
            "No active Revit document. Open a document before constructing StaircaseEngine.");

        _uiDoc = uiDoc;
        _doc   = uiDoc.Document;
    }

    public Result Run()
    {
        // ---- STEP 1: Pick stairs ------------------------------------------------
        List<Stairs> stairs = PickStairs();
        if (stairs.Count == 0)
            return Result.Cancelled;

        // ---- Resolve material + thickness once for the whole batch --------------
        // (Avoids running a FilteredElementCollector per panel.)
        ElementId materialId = StairParameterExtractor.GetConcreteMaterial(_doc);
        double thicknessFt  = stairs.Count > 0
            ? StairParameterExtractor.GetWaistThickness(_doc, stairs[0])
            : EngineConfig.FallbackWaistDepth;

        // Reset the diagnostic file at the start of the batch.
        StraightEngine.ResetDiagFile();

        int totalPanels = 0;
        int totalErrors = 0;
        int totalConnections = 0;
        var summaryLines = new List<string>();
        var allLogs = new List<string>();

        using var tg = new TransactionGroup(_doc, "Generate Staircase Analytical Model");
        tg.Start();

        foreach (var stair in stairs)
        {
            try
            {
                var (panels, errors, connections, logs) = ProcessOneStair(
                    stair, materialId, thicknessFt);
                totalPanels += panels;
                totalErrors += errors;
                totalConnections += connections;
                allLogs.AddRange(logs);

                summaryLines.Add(
                    $"• '{stair.Name}' (ID {stair.Id}): " +
                    $"{panels} panels, {connections} edge snaps" +
                    (errors > 0 ? $", {errors} errors" : ""));
            }
            catch (Exception ex)
            {
                totalErrors++;
                summaryLines.Add($"• '{stair.Name}' (ID {stair.Id}): FAILED — {ex.Message}");
                allLogs.Add($"[ERROR] Stair {stair.Id}: {ex.Message}");
            }
        }

        tg.Assimilate();

        // ---- STEP 6: Show summary ----------------------------------------------
        ShowSummary(stairs, summaryLines, allLogs,
                    totalPanels, totalErrors, totalConnections,
                    materialId, thicknessFt);

        return totalErrors > 0 ? Result.Failed : Result.Succeeded;
    }

    // ---------------------------------------------------------------------
    // Per-stair processing
    // ---------------------------------------------------------------------

    private (int panels, int errors, int connections, List<string> logs) ProcessOneStair(
        Stairs stair, ElementId materialId, double thicknessFt)
    {
        var logs = new List<string>();

        // STEP 2: Classify into a StairGraph.
        StairGraph graph = StairClassifier.Classify(_doc, stair, logs);

        int runCount = graph.Nodes.Count(n => n.Type == StairNodeType.Run);
        int landingCount = graph.Nodes.Count(n => n.Type == StairNodeType.Landing);
        logs.Add($"[INFO] Classified stair {stair.Id}: {graph.Nodes.Count} nodes " +
                 $"({runCount} runs, {landingCount} landings, branching={graph.IsBranching}).");

        // Warn if the stair looks unusual.
        if (runCount == 0)
        {
            logs.Add($"[WARNING] Stair {stair.Id} has no runs — is this a component-based stair?");
        }
        if (runCount == 1 && landingCount == 0)
        {
            logs.Add($"[WARNING] Stair {stair.Id} has only 1 run and 0 landings. " +
                     "If this is a multi-flight stair, it may be a sketched run or a legacy stair. " +
                     "The converter will treat it as a single straight flight.");
        }

        // Build a parameter context so engines don't re-query the document per panel.
        var context = new StairParameterContext
        {
            MaterialId = materialId,
            ThicknessFt = thicknessFt
        };

        // STEP 3: Generate panel geometry per node.
        var allPanels = new List<PanelGeometry>();
        foreach (var node in graph.NodesBottomToTop())
        {
            try
            {
                var engine = EngineRouter.GetEngine(node);
                var nodePanels = engine.BuildPanels(_doc, node, context);
                allPanels.AddRange(nodePanels);
                logs.Add($"[DEBUG] Node {node.ElementId} ({node.Type}/{node.Tag}): " +
                         $"{nodePanels.Count} panel(s) via {engine.GetType().Name}.");

                // Drain engine diagnostics into the summary log.
                // Drain diagnostics from whichever engine ran.
                if (engine is SketchEngine.SketchEngineStrategy)
                {
                    logs.AddRange(SketchEngine.SketchEngineStrategy.Diagnostics);
                    SketchEngine.SketchEngineStrategy.Diagnostics.Clear();
                }
                else if (engine is StraightEngine)
                {
                    logs.AddRange(StraightEngine.Diagnostics);
                    StraightEngine.Diagnostics.Clear();
                }
            }
            catch (Exception ex)
            {
                logs.Add($"[ERROR] Node {node.ElementId}: engine failed — {ex.Message}");
            }
        }

        // STEP 4: Resolve shared edges.
        int connections = ConnectivityResolver.ResolveSharedEdges(graph, allPanels);
        logs.Add($"[INFO] Resolved {connections} shared-edge snap(s).");

        // STEP 5: Create analytical panels in one transaction.
        using var tx = new Transaction(_doc, $"Convert stair {stair.Id}");
        tx.Start();

        var builder = new AnalyticalModelBuilder(_doc, logs);
        var createdPanels = builder.CreatePanels(allPanels);

        int errors = allPanels.Count - createdPanels.Count;

        // Roll back if too many panels failed.
        if (errors > Math.Ceiling(allPanels.Count * ERROR_ROLLBACK_THRESHOLD) && allPanels.Count > 0)
        {
            tx.RollBack();
            logs.Add($"[ERROR] Aborted: {errors} of {allPanels.Count} panels failed " +
                     $"(threshold {ERROR_ROLLBACK_THRESHOLD:P0}). No panels were committed.");
            return (0, errors, connections, logs);
        }

        tx.Commit();

        if (errors > 0)
            logs.Add($"[WARNING] {errors} panel(s) failed to create — see builder logs.");

        return (createdPanels.Count, errors, connections, logs);
    }

    // ---------------------------------------------------------------------
    // Selection
    // ---------------------------------------------------------------------

    private List<Stairs> PickStairs()
    {
        IList<Reference> refs;
        try
        {
            refs = _uiDoc.Selection.PickObjects(
                ObjectType.Element,
                new StairsFilter(),
                "Select stairs — click or box-select. Press Finish (✓) when done.");
        }
        catch (RevitOperationCanceledException)
        {
            return new List<Stairs>();
        }

        var stairs = new List<Stairs>();
        foreach (var r in refs)
        {
            if (_doc.GetElement(r.ElementId) is Stairs s)
                stairs.Add(s);
        }

        if (stairs.Count == 0)
            TaskDialog.Show("Staircase Converter", "No Stairs elements were picked.");

        return stairs;
    }

    // ---------------------------------------------------------------------
    // Summary
    // ---------------------------------------------------------------------

    private static void ShowSummary(
        List<Stairs> stairs,
        List<string> summaryLines,
        List<string> allLogs,
        int totalPanels,
        int totalErrors,
        int totalConnections,
        ElementId materialId,
        double thicknessFt)
    {
        double thicknessMm = thicknessFt * 304.8;

        var lines = new List<string>
        {
            $"{(totalErrors == 0 ? "✅" : "⚠")}  {totalPanels} analytical panel(s) created",
            $"🔗  {totalConnections} shared-edge snap(s) resolved",
            $"⚠  {totalErrors} error(s)",
            $"Stairs processed: {stairs.Count}",
            $"Material ID: {materialId}",
            $"Waist thickness: {thicknessFt:F3} ft ({thicknessMm:F0} mm)",
            $"📋  Diagnostics: {StraightEngine.DiagFilePathPublic}",
            "",
            "--- Per-stair breakdown ---"
        };
        lines.AddRange(summaryLines);

        var logsToShow = allLogs.Take(15).ToList();
        if (logsToShow.Count > 0)
        {
            lines.Add("");
            lines.Add($"--- Log (first 15 of {allLogs.Count} — full log in diagnostic file) ---");
            lines.AddRange(logsToShow);
        }

        TaskDialog.Show("Staircase Conversion Complete", string.Join("\n", lines));
    }

    // ---------------------------------------------------------------------
    // Selection filter
    // ---------------------------------------------------------------------

    private class StairsFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is Stairs;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}
