using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace StructuralTools;

/// <summary>
/// Experimental: repairs line loads that HighlightProblematicLoadsCommand already
/// diagnosed, using the case-based rules in RepairEngine. Only touches loads with
/// an existing diagnosis — never guesses at a fix for anything else.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class RepairIdentifiedLoadsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDoc = commandData.Application.ActiveUIDocument;
        Document doc = uiDoc.Document;

        List<LineLoad> loadList = new FilteredElementCollector(doc)
            .OfClass(typeof(LineLoad))
            .Cast<LineLoad>()
            .ToList();

        IDictionary<ElementId, LoadDiagnosis> problemMap = RevitLoadUtils.GetPreviouslyIdentifiedProblemLoads(doc);

        if (problemMap.Count == 0)
        {
            TaskDialog.Show("Repair Identified Loads",
                "No previously diagnosed problem loads found. Run \"Highlight Problematic Loads\" first.");
            return Result.Succeeded;
        }

        List<RepairOutcome> repaired, failed, flagged;

        using (Transaction tx = new(doc, "Repair Identified Loads (experimental)"))
        {
            tx.Start();
            (repaired, failed, flagged) = RepairEngine.RepairIdentifiedLoads(doc, loadList, problemMap);
            tx.Commit();
        }

        string detail = failed.Count == 0 && flagged.Count == 0
            ? "No validation failures were reported."
            : string.Join("\n",
                failed.Select(f => $"FAILED: {f.OriginalLoadId} — {f.Reason}").Concat(
                flagged.Select(f => $"FLAGGED: {f.OriginalLoadId} — {f.Reason}"))
                .Take(10));

        string summary = $"Repaired: {repaired.Count}\n" +
            $"Flagged for manual review: {flagged.Count}\n" +
            $"Failed: {failed.Count}\n\n" +
            "Experimental — please spot-check the results before trusting them on a live model.";

        if (!string.IsNullOrWhiteSpace(detail))
            summary += $"\n\nDetails:\n{detail}";

        TaskDialog.Show("Repair Identified Loads", summary);

        return Result.Succeeded;
    }
}
