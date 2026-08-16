using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace StructuralTools;

/// <summary>Case classification produced by the diagnostic (Highlight) tool for a single problem load.</summary>
public enum RepairCase
{
    OverhangPanelEdge,
    ExtendsBeyondNode,
    MoveToNearestMember,
    OffsetToEdge,
    SnapToPanelEdge,
    DeleteMinimalLoad,
    ManualReview
}

/// <summary>One diagnosed problem, as produced (and persisted) by HighlightProblematicLoadsCommand.</summary>
public sealed class LoadDiagnosis
{
    public RepairCase Case { get; init; }
    public string WarningText { get; init; } = string.Empty;
    public ElementId? HostId { get; init; }
    public XYZ? SuggestedPoint { get; init; }
    public int Severity { get; init; }
}

public enum RepairStatus { Repaired, Flagged, Failed }

public sealed class RepairOutcome
{
    public ElementId OriginalLoadId { get; init; } = ElementId.InvalidElementId;
    public RepairStatus Status { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Applies previously-diagnosed repairs to line loads. Every case follows the same
/// shape: compute the fix -> Validate() -> commit, or flag/fail instead of writing
/// a geometry change that hasn't passed validation.
/// </summary>
public static class RepairEngine
{
    /// <summary>Minimum line-load length (Revit internal units, decimal feet) below which a segment is degenerate.
    /// Match this to whatever tolerance HighlightProblematicLoadsCommand already uses for consistency.</summary>
    public const double MinSegmentLength = 0.01;

    public static (List<RepairOutcome> Repaired, List<RepairOutcome> Failed, List<RepairOutcome> Flagged)
        RepairIdentifiedLoads(Document doc, IList<LineLoad> loadList, IDictionary<ElementId, LoadDiagnosis> problemMap)
    {
        var repaired = new List<RepairOutcome>();
        var failed = new List<RepairOutcome>();
        var flagged = new List<RepairOutcome>();

        foreach (var load in loadList)
        {
            if (!load.IsValidObject)
            {
                continue;
            }

            if (!problemMap.TryGetValue(load.Id, out var diagnosis))
                continue; // only touch loads the diagnostic tool already flagged

            try
            {
                if (!load.IsValidObject)
                    continue;

                var outcome = RepairOne(doc, load, diagnosis);
                (outcome.Status switch
                {
                    RepairStatus.Repaired => repaired,
                    RepairStatus.Flagged => flagged,
                    _ => failed
                }).Add(outcome);
            }
            catch (Exception ex)
            {
                failed.Add(new RepairOutcome
                {
                    OriginalLoadId = load.Id,
                    Status = RepairStatus.Failed,
                    Reason = $"{diagnosis.Case}: {ex.Message}"
                });
            }
        }

        return (repaired, failed, flagged);
    }

    private static RepairOutcome RepairOne(Document doc, LineLoad load, LoadDiagnosis diagnosis) => diagnosis.Case switch
    {
        RepairCase.OverhangPanelEdge => RepairOverhangPanelEdge(doc, load),
        RepairCase.ExtendsBeyondNode => RepairExtendsBeyondNode(doc, load),
        RepairCase.MoveToNearestMember => RepairMoveToNearestMember(doc, load),
        RepairCase.OffsetToEdge => RepairOffsetToEdge(doc, load),
        RepairCase.SnapToPanelEdge => RepairSnapToPanelEdge(doc, load),
        RepairCase.DeleteMinimalLoad => DeleteMinimal(doc, load),
        RepairCase.ManualReview => Flag(load.Id, diagnosis.WarningText),
        _ => Flag(load.Id, $"unhandled_case:{diagnosis.Case}")
    };

    // ---- Case handlers: compute -> Validate -> commit, every time -------

    private static RepairOutcome RepairOverhangPanelEdge(Document doc, LineLoad load)
    {
        var host = RevitLoadUtils.GetHostElement(doc, load);
        var boundary = RevitLoadUtils.GetAnalyticalPanelBoundary(host);

        var startClamped = RevitLoadUtils.ClampToNearestBoundary(RevitLoadUtils.GetLoadStartPoint(load), boundary);
        var endClamped = RevitLoadUtils.ClampToNearestBoundary(RevitLoadUtils.GetLoadEndPoint(load), boundary);

        if (startClamped.DistanceTo(endClamped) < MinSegmentLength)
        {
            doc.Delete(load.Id);
            return Ok(load.Id, "deleted_shortened_to_zero");
        }

        var candidate = Line.CreateBound(startClamped, endClamped);
        if (!RepairEngine.Validate(candidate, host))
            return Failed(load.Id, "overhang_panel_edge_failed_validation");

        RevitLoadUtils.ReplaceLoadGeometry(load, candidate);
        return Ok(load.Id, "trimmed_to_panel_edge");
    }

    private static RepairOutcome RepairExtendsBeyondNode(Document doc, LineLoad load)
    {
        var host = RevitLoadUtils.GetHostElement(doc, load);
        var node = RevitLoadUtils.FindNearestNode(doc, load, host);

        Curve candidate = node is not null
            ? RevitLoadUtils.ShortenToNearestNode(load, node)
            : RevitLoadUtils.ShortenToNearestMemberEndpoint(load, host);

        if (!Validate(candidate, host))
            return Failed(load.Id, "extends_beyond_node_failed_validation");

        RevitLoadUtils.ReplaceLoadGeometry(load, candidate);
        return Ok(load.Id, node is not null ? "shortened_to_nearest_node" : "shortened_to_member_endpoint");
    }

    private static RepairOutcome RepairMoveToNearestMember(Document doc, LineLoad load)
    {
        var host = RevitLoadUtils.GetHostElement(doc, load);
        var nearestMember = RevitLoadUtils.FindNearestStructuralMember(doc, load, host);

        if (nearestMember is null)
            return Flag(load.Id, "no_nearest_member_found");

        var candidate = RevitLoadUtils.ProjectLoadOntoMember(load, nearestMember);
        if (!Validate(candidate, nearestMember))
            return Failed(load.Id, "move_to_nearest_member_failed_validation");

        RevitLoadUtils.ReplaceLoadGeometry(load, candidate);
        RevitLoadUtils.ReassignHost(doc, load, nearestMember);
        return Ok(load.Id, "moved_to_nearest_member");
    }

    private static RepairOutcome RepairOffsetToEdge(Document doc, LineLoad load)
    {
        var host = RevitLoadUtils.GetHostElement(doc, load);
        var edge = RevitLoadUtils.FindNearestAnalyticalEdge(load, host);
        var offset = RevitLoadUtils.ComputeSignedOffset(load, edge);

        var candidate = RevitLoadUtils.OffsetCurveParallelToEdge(load, edge, offset);
        if (!Validate(candidate, host))
            return Failed(load.Id, "offset_to_edge_failed_validation");

        RevitLoadUtils.ReplaceLoadGeometry(load, candidate);
        return Ok(load.Id, "offset_to_edge");
    }

    private static RepairOutcome RepairSnapToPanelEdge(Document doc, LineLoad load)
    {
        var host = RevitLoadUtils.GetHostElement(doc, load);
        var edge = RevitLoadUtils.FindNearestAnalyticalEdge(load, host);

        var start = RevitLoadUtils.SnapPointToEdge(RevitLoadUtils.GetLoadStartPoint(load), edge);
        var end = RevitLoadUtils.SnapPointToEdge(RevitLoadUtils.GetLoadEndPoint(load), edge);

        if (start.DistanceTo(end) < MinSegmentLength)
        {
            doc.Delete(load.Id);
            return Ok(load.Id, "deleted_shortened_to_zero");
        }

        var candidate = Line.CreateBound(start, end);
        if (!Validate(candidate, host))
            return Failed(load.Id, "snap_to_panel_edge_failed_validation");

        RevitLoadUtils.ReplaceLoadGeometry(load, candidate);
        return Ok(load.Id, "snapped_to_panel_edge");
    }

    private static RepairOutcome DeleteMinimal(Document doc, LineLoad load)
    {
        doc.Delete(load.Id);
        return Ok(load.Id, "deleted_minimal_load");
    }

    // ---- Validation --------------------------------------------------
    // Every case above routes its candidate curve through here before it's
    // written back to the model. Nothing commits on a failed validation —
    // it becomes a Failed outcome instead, same as an exception would.

    public static bool Validate(Curve? curve, Element host)
    {
        if (curve is null) return false;
        if (curve.Length < MinSegmentLength) return false;
        if (RevitLoadUtils.IsOutsideAnalyticalBoundary(curve, host)) return false;
        if (RevitLoadUtils.IsSelfIntersecting(curve)) return false;
        return true;
    }

    private static RepairOutcome Ok(ElementId originalId, string reason) =>
        new() { OriginalLoadId = originalId, Status = RepairStatus.Repaired, Reason = reason };

    private static RepairOutcome Flag(ElementId originalId, string reason) =>
        new() { OriginalLoadId = originalId, Status = RepairStatus.Flagged, Reason = reason };

    private static RepairOutcome Failed(ElementId originalId, string reason) =>
        new() { OriginalLoadId = originalId, Status = RepairStatus.Failed, Reason = reason };
}
