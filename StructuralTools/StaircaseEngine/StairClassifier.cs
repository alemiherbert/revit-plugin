using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace StructuralTools.StaircaseEngine;

/// <summary>
/// Builds a <see cref="StairGraph"/> from a Revit <see cref="Stairs"/> element.
/// Classifies each run as Straight / Curved / Winder, builds adjacency from
/// shared boundary edges, sorts bottom-to-top, and detects branching.
/// </summary>
public static class StairClassifier
{
    /// <summary>
    /// Classify a stair into a <see cref="StairGraph"/>. Diagnostics are
    /// written to <paramref name="log"/> so they appear in the summary dialog.
    /// </summary>
    public static StairGraph Classify(Document doc, Stairs stair, List<string> log)
    {
        var graph = new StairGraph();
        double stairsBaseElev = stair.BaseElevation;

        // ---- Diagnostic: log what the stair contains ----------------------
        var runIds = stair.GetStairsRuns();
        var landingIds = stair.GetStairsLandings();
        log.Add($"[DEBUG] Stair {stair.Id} '{stair.Name}': " +
                $"{runIds.Count} run(s), {landingIds.Count} landing(s).");

        // ---- Create nodes for runs ----------------------------------------
        foreach (ElementId runId in runIds)
        {
            var runElem = doc.GetElement(runId);
            if (runElem is not StairsRun run)
            {
                log.Add($"[WARNING] Run ID {runId} is {runElem?.GetType().Name ?? "null"}, not StairsRun — skipped.");
                continue;
            }

            var node = new StairNode
            {
                ElementId     = run.Id,
                Type          = StairNodeType.Run,
                SourceElement = run,
                BaseElevation = stairsBaseElev + run.BaseElevation,
                TopElevation  = stairsBaseElev + run.TopElevation
            };

            node.Tag = ClassifyRun(run, log);
            graph.Nodes.Add(node);

            log.Add($"[DEBUG] Run {run.Id}: tag={node.Tag}, " +
                    $"base={run.BaseElevation:F3}, top={run.TopElevation:F3}, " +
                    $"width={run.ActualRunWidth:F3}, risers={run.ActualRisersNumber}.");
        }

        // ---- Create nodes for landings ------------------------------------
        foreach (ElementId landId in landingIds)
        {
            var landElem = doc.GetElement(landId);
            if (landElem is not StairsLanding landing)
            {
                log.Add($"[WARNING] Landing ID {landId} is {landElem?.GetType().Name ?? "null"}, not StairsLanding — skipped.");
                continue;
            }

            var bb = landing.get_BoundingBox(null);
            double topZ = bb != null ? bb.Max.Z : stairsBaseElev + landing.BaseElevation;

            var node = new StairNode
            {
                ElementId     = landing.Id,
                Type          = StairNodeType.Landing,
                SourceElement = landing,
                BaseElevation = stairsBaseElev + landing.BaseElevation,
                TopElevation  = topZ
            };
            graph.Nodes.Add(node);

            log.Add($"[DEBUG] Landing {landing.Id}: base={landing.BaseElevation:F3}.");
        }

        // ---- Build adjacency from shared boundary edges -------------------
        BuildAdjacency(graph);

        // ---- Sort and detect branching ------------------------------------
        graph.RootNode = graph.NodesBottomToTop().FirstOrDefault();
        graph.IsBranching = graph.Nodes.Any(n => n.IsBranching);

        return graph;
    }

    /// <summary>
    /// Classify a run. Only sketch-based straight runs are supported;
    /// curved and winder runs are not handled and a warning is logged.
    /// </summary>
    private static RunTag ClassifyRun(StairsRun run, List<string> log)
    {
        // Warn if the path contains arcs — this tool only supports sketch-based
        // straight stairs (straight riser / boundary lines).
        try
        {
            var path = run.GetStairsPath();
            if (path != null)
            {
                foreach (var c in path)
                {
                    if (c is Arc or Ellipse or NurbSpline or HermiteSpline)
                    {
                        log.Add($"[WARNING] Run {run.Id} has a curved path — " +
                                "curved and spiral stairs are not supported. " +
                                "This run will be skipped or produce no panels.");
                        break;
                    }
                }
            }
        }
        catch { /* ignore — treat as straight */ }

        return RunTag.StraightRun;
    }

    /// <summary>
    /// Build adjacency: two nodes are adjacent if their source elements share
    /// a boundary edge (compared within <see cref="EngineConfig.EdgeSnapTolerance"/>).
    /// </summary>
    private static void BuildAdjacency(StairGraph graph)
    {
        // Collect boundary edges for each node.
        var nodeEdges = new Dictionary<ElementId, List<XYZ[]>>();

        foreach (var node in graph.Nodes)
        {
            nodeEdges[node.ElementId] = GetBoundaryEndpoints(node.SourceElement!);
        }

        // Compare every pair of nodes.
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            for (int j = i + 1; j < graph.Nodes.Count; j++)
            {
                var a = graph.Nodes[i];
                var b = graph.Nodes[j];

                if (EdgesShareAnyPoint(nodeEdges[a.ElementId], nodeEdges[b.ElementId]))
                {
                    a.ConnectedTo.Add(b);
                    b.ConnectedTo.Add(a);
                }
            }
        }
    }

    /// <summary>
    /// Get boundary endpoints of a stair component (run or landing) via
    /// <c>GetFootprintBoundary()</c>. Returns a list of XYZ pairs (start, end)
    /// representing each boundary edge's endpoints.
    /// </summary>
    private static List<XYZ[]> GetBoundaryEndpoints(Element elem)
    {
        var edges = new List<XYZ[]>();

        try
        {
            var method = elem.GetType().GetMethod("GetFootprintBoundary");
            if (method?.Invoke(elem, null) is not IList<CurveLoop> loops) return edges;

            foreach (var loop in loops)
            {
                foreach (var c in loop)
                {
                    edges.Add(new[] { c.GetEndPoint(0), c.GetEndPoint(1) });
                }
            }
        }
        catch { }

        return edges;
    }

    /// <summary>
    /// Check if any endpoint from set A is within tolerance of any endpoint
    /// from set B. Used for adjacency detection.
    /// </summary>
    private static bool EdgesShareAnyPoint(List<XYZ[]> a, List<XYZ[]> b)
    {
        foreach (var ea in a)
        {
            foreach (var eb in b)
            {
                if (ea[0].DistanceTo(eb[0]) < EngineConfig.EdgeSnapTolerance) return true;
                if (ea[0].DistanceTo(eb[1]) < EngineConfig.EdgeSnapTolerance) return true;
                if (ea[1].DistanceTo(eb[0]) < EngineConfig.EdgeSnapTolerance) return true;
                if (ea[1].DistanceTo(eb[1]) < EngineConfig.EdgeSnapTolerance) return true;
            }
        }
        return false;
    }
}
